using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed class FriendConfiguration
{
    public string Endpoint { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public Guid DeviceId { get; set; }
    public string Credential { get; set; } = "";
    public DateTimeOffset CredentialExpiresUtc { get; set; }
    public string ClientExecutablePath { get; set; } = "";
}

public sealed record PublicProfile(Guid Id, string Name, string State, string? JoinAddress);
public sealed record CompanionStatus(bool RemoteControlsEnabled, string? Notice, IReadOnlyList<PublicProfile> Profiles,
    bool? OwnerGameRunning, bool? YourGameRunning, bool CanStart, bool CanStop, DateTimeOffset ReceivedUtc);
public sealed record FriendView(string Mode, string State, string Detail, string Endpoint, DateTimeOffset? LastConnectedUtc,
    bool? LocalGameRunning, bool RemoteControlsEnabled, bool CanStart, bool CanStop, IReadOnlyList<PublicProfile> Profiles,
    string ClientExecutablePath = "");
public sealed record FriendActionResult(bool Ok, string Code, string Message, CompanionStatus? Status);

public sealed class FriendService
{
    private const string ConfigFile = "friend.protected";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LocalData data;
    private FriendConfiguration? config;
    private FriendView view;
    private Guid instanceId = Guid.NewGuid();
    private long sequence;

    public FriendService(LocalData data)
    {
        this.data = data;
        config = LoadConfig(data);
        view = config is null
            ? new("Friend", "Not paired", "Enter the Host IP and one-time pairing code.", "", null, null, false, false, false, [])
            : new("Friend", "Disconnected/Unknown", "Waiting for a verified Host response.", config.Endpoint,
                null, ClientMonitor.IsRunning(config.ClientExecutablePath), false, false, false, [], config.ClientExecutablePath);
    }

    public FriendView View() => view;

    public async Task<FriendActionResult> SetClientPathAsync(string path)
    {
        await gate.WaitAsync();
        try
        {
            if (config is null) return new(false, "NotPaired", "Pair with a Host first.", null);
            if (path is null || (path.Length > 0 && (!Path.IsPathFullyQualified(path) || !File.Exists(path))))
                return new(false, "InvalidClientPath", "Choose an installed game client executable by absolute path.", null);
            config.ClientExecutablePath = path.Length == 0 ? "" : Path.GetFullPath(path);
            data.SaveProtected(ConfigFile, JsonSerializer.SerializeToUtf8Bytes(config, Json));
            view = view with { ClientExecutablePath = config.ClientExecutablePath,
                LocalGameRunning = ClientMonitor.IsRunning(config.ClientExecutablePath) };
            return new(true, "ClientPathSaved", "Game client path saved in Windows protected storage.", null);
        }
        finally { gate.Release(); }
    }

    public async Task<FriendActionResult> PairAsync(string invitation, string clientExecutablePath, string? hostAddress = null)
    {
        await gate.WaitAsync();
        try
        {
            PairingInvite? invite;
            try { invite = JsonSerializer.Deserialize<PairingInvite>(invitation, Json); }
            catch (JsonException) { return new(false, "InvalidInvite", "Pairing code is invalid. Paste the whole code from the Host.", null); }
            if (invite is null || !HostIdentity.TryEndpoint(invite.Endpoint, out _) ||
                !ValidFingerprint(invite.Fingerprint) || string.IsNullOrWhiteSpace(invite.Code) ||
                invite.DeviceId == Guid.Empty || invite.ExpiresUtc <= DateTimeOffset.UtcNow ||
                clientExecutablePath is null ||
                (clientExecutablePath.Length > 0 && !Path.IsPathFullyQualified(clientExecutablePath)))
                return new(false, "InvalidInvite", "Invite or game client path is invalid or expired.", null);
            if (!string.IsNullOrWhiteSpace(hostAddress) &&
                (!HostIdentity.TryAddress(hostAddress, out var enteredEndpoint) ||
                 !string.Equals(enteredEndpoint, invite.Endpoint, StringComparison.OrdinalIgnoreCase)))
                return new(false, "HostAddressMismatch", "Host IP or port differs from the pairing code. Check the address with the Host.", null);
            try
            {
                using var client = MakeClient(invite.Endpoint, invite.Fingerprint);
                var response = await client.PostAsync("api/companion/pair", new StringContent(
                    JsonSerializer.Serialize(new PairingActivation(invite.DeviceId, invite.Code), Json), Encoding.UTF8, "application/json"));
                if (!response.IsSuccessStatusCode) return new(false, "PairingRejected", "Host did not accept this one-time invite.", null);
                var credential = await response.Content.ReadFromJsonAsync<PairingCredential>(Json);
                if (credential is null || credential.DeviceId != invite.DeviceId || credential.Credential.Length < 32)
                    return new(false, "PairingRejected", "Host returned an invalid credential.", null);
                config = new FriendConfiguration
                {
                    Endpoint = invite.Endpoint, Fingerprint = invite.Fingerprint, DeviceId = invite.DeviceId,
                    Credential = credential.Credential, CredentialExpiresUtc = credential.ExpiresUtc,
                    ClientExecutablePath = clientExecutablePath
                };
                data.SaveProtected(ConfigFile, JsonSerializer.SerializeToUtf8Bytes(config, Json));
                instanceId = Guid.NewGuid();
                sequence = 0;
                view = new FriendView("Friend", "Disconnected/Unknown", "Paired; waiting for an authenticated heartbeat.",
                    config.Endpoint, null, ClientMonitor.IsRunning(config.ClientExecutablePath), false, false, false, [],
                    config.ClientExecutablePath);
                return new(true, "Paired", "Device paired and credential saved in Windows protected storage.", null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return new(false, "Disconnected", "Could not pair over pinned TLS: " + ex.Message +
                    (ex.InnerException is null ? "" : " " + ex.InnerException.Message), null);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<FriendView> PollAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (config is null) return view;
            var localRunning = ClientMonitor.IsRunning(config.ClientExecutablePath);
            if (config.CredentialExpiresUtc <= DateTimeOffset.UtcNow)
            {
                view = view with { State = "Disconnected/Unknown", Detail = "Device credential expired; ask the Host to rotate it.",
                    LocalGameRunning = localRunning, RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                return view;
            }
            try
            {
                using var client = MakeClient(config.Endpoint, config.Fingerprint);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Credential);
                var heartbeat = new HeartbeatRequest(config.DeviceId, instanceId, ++sequence, "0.2", localRunning);
                var request = new HttpRequestMessage(HttpMethod.Post, "api/companion/heartbeat")
                {
                    Content = new StringContent(JsonSerializer.Serialize(heartbeat, Json), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Device-Id", config.DeviceId.ToString());
                using var response = await client.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    view = view with { State = "Revoked", Detail = "Host revoked this device.", LocalGameRunning = localRunning,
                        RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                    return view;
                }
                if (!response.IsSuccessStatusCode)
                {
                    view = view with { State = "Disconnected/Unknown", Detail = $"Host returned {(int)response.StatusCode}.",
                        LocalGameRunning = localRunning, RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                    return view;
                }
                var status = await response.Content.ReadFromJsonAsync<CompanionStatus>(Json);
                if (status is null) throw new IOException("Host status was empty.");
                view = new FriendView("Friend", status.RemoteControlsEnabled ? "Connected" : "Disabled",
                    status.RemoteControlsEnabled ? "Authenticated Host connection." : status.Notice ?? "Host remote controls are off.",
                    config.Endpoint, DateTimeOffset.UtcNow, localRunning, status.RemoteControlsEnabled,
                    status.CanStart, status.CanStop, status.Profiles, config.ClientExecutablePath);
                return view;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                view = view with { State = "Disconnected/Unknown", Detail = "Host could not be verified: " + ex.Message,
                    LocalGameRunning = localRunning, RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                return view;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<FriendActionResult> RequestAsync(Guid profileId, string action)
    {
        await gate.WaitAsync();
        try
        {
            if (config is null) return new(false, "NotPaired", "Pair with a Host first.", null);
            if (action is not ("start" or "stop")) return new(false, "InvalidAction", "Only Start and Stop are available.", null);
            try
            {
                using var client = MakeClient(config.Endpoint, config.Fingerprint);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Credential);
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/companion/" + action)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { deviceId = config.DeviceId, profileId }, Json), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Device-Id", config.DeviceId.ToString());
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                using var response = await client.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var denied = await response.Content.ReadFromJsonAsync<FriendActionResult>(Json);
                    if (denied?.Code == "Revoked") view = view with { State = "Revoked", Detail = "Host revoked this device.",
                        RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                    return denied ?? new(false, "Forbidden", "Host denied this action.", null);
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    view = view with { State = "Disconnected/Unknown", Detail = "Host rejected this device credential.",
                        RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                return await response.Content.ReadFromJsonAsync<FriendActionResult>(Json)
                    ?? new(false, "InvalidResponse", "Host returned an empty action result.", null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                view = view with { State = "Disconnected/Unknown", Detail = "Host could not be verified: " + ex.Message,
                    RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                return new(false, "Disconnected", view.Detail, null);
            }
        }
        finally { gate.Release(); }
    }

    private static FriendConfiguration? LoadConfig(LocalData data)
    {
        var bytes = data.LoadProtected(ConfigFile);
        return bytes is null ? null : JsonSerializer.Deserialize<FriendConfiguration>(bytes, Json);
    }

    private static bool ValidFingerprint(string? value)
    {
        if (value?.Length != 64) return false;
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static HttpClient MakeClient(string endpoint, string fingerprint)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                DateTimeOffset.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
                DateTimeOffset.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(fingerprint))
        };
        return new HttpClient(handler) { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(6) };
    }
}

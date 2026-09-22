using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Authentication;
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

public sealed record PublicProfile(Guid Id, string Name, string State, string? JoinAddress,
    bool CanStopNow = false, string? StopReason = null, string Kind = "",
    int? OnlinePlayers = null, int? MaxPlayers = null, DateTimeOffset? AutoShutdownAtUtc = null,
    string? AutoShutdownReason = null);
public sealed record CompanionStatus(bool RemoteControlsEnabled, string? Notice, IReadOnlyList<PublicProfile> Profiles,
    bool? OwnerGameRunning, bool? YourGameRunning, bool CanStart, bool CanStop, DateTimeOffset ReceivedUtc);
public sealed record FriendView(string Mode, string State, string Detail, string Endpoint, DateTimeOffset? LastConnectedUtc,
    bool? LocalGameRunning, bool RemoteControlsEnabled, bool CanStart, bool CanStop, IReadOnlyList<PublicProfile> Profiles,
    string ClientExecutablePath = "", Guid ConnectionId = default, IReadOnlyList<FriendView>? Connections = null,
    string? ConnectionCode = null);
public sealed record FriendActionResult(bool Ok, string Code, string Message, CompanionStatus? Status);

internal sealed class FriendLink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LocalData data;
    private readonly string configFile;
    private FriendConfiguration? config;
    private FriendView view;
    private Guid instanceId = Guid.NewGuid();
    private long sequence;

    public FriendLink(LocalData data, string configFile)
    {
        this.data = data;
        this.configFile = configFile;
        config = LoadConfig(data, configFile);
        view = config is null
            ? new("Friend", "Not paired", "Paste the server invite code from the Host PC.", "", null, null, false, false, false, [])
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
            data.SaveProtected(configFile, JsonSerializer.SerializeToUtf8Bytes(config, Json));
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
            if (invitation?.TrimStart().StartsWith('{') == true)
            {
                // Existing invitations remain usable until their normal expiry.
                try { invite = JsonSerializer.Deserialize<PairingInvite>(invitation, Json); }
                catch (JsonException) { return new(false, "InvalidInvite", "Pairing password is invalid.", null); }
            }
            else
            {
                if (!PairingPassword.TryDecode(invitation, hostAddress, out invite))
                    return new(false, "InvalidInvite", "Invite is invalid, expired, no longer current, or has a different Host address. Ask the Host for the current server code.", null);
            }
            if (invite is null || !HostIdentity.TryEndpoint(invite.Endpoint, out _) ||
                !ValidFingerprint(invite.Fingerprint) || string.IsNullOrWhiteSpace(invite.Code) ||
                invite.DeviceId == Guid.Empty || invite.ExpiresUtc <= DateTimeOffset.UtcNow ||
                clientExecutablePath is null ||
                (clientExecutablePath.Length > 0 && !Path.IsPathFullyQualified(clientExecutablePath)))
                return new(false, "InvalidInvite", "Invite or game client path is invalid or expired.", null);
            if (!string.IsNullOrWhiteSpace(hostAddress) &&
                (!HostIdentity.TryAddress(hostAddress, out var enteredEndpoint) ||
                 !string.Equals(enteredEndpoint, invite.Endpoint, StringComparison.OrdinalIgnoreCase)))
                return new(false, "HostAddressMismatch", "Host IP or port differs from this pairing invitation. Check the address with the Host.", null);
            try
            {
                using var client = MakeClient(invite.Endpoint, invite.Fingerprint);
                var response = await client.PostAsync("api/companion/pair", new StringContent(
                    JsonSerializer.Serialize(new PairingActivation(invite.DeviceId, invite.Code, invite.ServerScope), Json), Encoding.UTF8, "application/json"));
                if (!response.IsSuccessStatusCode)
                    return response.StatusCode == HttpStatusCode.TooManyRequests
                        ? new(false, "HostBusy", "The Host is limiting connection attempts. Wait, then try the current invite again.", null)
                        : response.StatusCode == HttpStatusCode.Unauthorized
                            ? new(false, "PairingRejected", "The Host rejected this code. It may have been refreshed or revoked; ask for the current server code.", null)
                            : new(false, "HostUnavailable", $"The Host app returned {(int)response.StatusCode} during pairing. Ask the Host to check its app.", null);
                var credential = await response.Content.ReadFromJsonAsync<PairingCredential>(Json);
                if (credential is null || credential.DeviceId == Guid.Empty ||
                    (!invite.ServerScope && credential.DeviceId != invite.DeviceId) || credential.Credential.Length < 32)
                    return new(false, "PairingRejected", "Host returned an invalid credential.", null);
                config = new FriendConfiguration
                {
                    Endpoint = invite.Endpoint, Fingerprint = invite.Fingerprint, DeviceId = credential.DeviceId,
                    Credential = credential.Credential, CredentialExpiresUtc = credential.ExpiresUtc,
                    ClientExecutablePath = clientExecutablePath
                };
                data.SaveProtected(configFile, JsonSerializer.SerializeToUtf8Bytes(config, Json));
                instanceId = Guid.NewGuid();
                sequence = 0;
                view = new FriendView("Friend", "Disconnected/Unknown", "Paired; waiting for an authenticated heartbeat.",
                    config.Endpoint, null, ClientMonitor.IsRunning(config.ClientExecutablePath), false, false, false, [],
                    config.ClientExecutablePath);
                return new(true, "Paired", "Device paired and credential saved in Windows protected storage.", null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                return PairConnectionFailure(ex);
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
                view = view with { State = "Disconnected/Unknown", Detail = "Device credential expired; ask the Host for the current server code.", ConnectionCode = "CredentialExpired",
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
                    PairingDecision? denial = null;
                    try { denial = JsonSerializer.Deserialize<PairingDecision>(await response.Content.ReadAsStringAsync(), Json); }
                    catch (JsonException) { /* A generic 403 is not evidence of revocation. */ }
                    var revoked = denial?.Code == "Revoked";
                    view = view with { State = revoked ? "Revoked" : "Disconnected/Unknown",
                        Detail = revoked ? "Host refreshed this server code or revoked this PC. Ask for the current code." : "Host access is unavailable or denied.",
                        ConnectionCode = revoked ? "Revoked" : "HostAccessDenied", LocalGameRunning = localRunning,
                        RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                    return view;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var issue = response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => new ConnectionIssue("CredentialRejected", "The Host rejected this PC's credential. Ask for the current server code."),
                        HttpStatusCode.TooManyRequests => new ConnectionIssue("HostBusy", "The Host is limiting requests. Wait a moment and check again."),
                        _ => new ConnectionIssue("HostUnavailable", $"The Host app returned {(int)response.StatusCode}. Ask the Host to check its app.")
                    };
                    view = view with { State = "Disconnected/Unknown", Detail = issue.Message, ConnectionCode = issue.Code,
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
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                var issue = ConnectionFailure(ex);
                view = view with { State = "Disconnected/Unknown", Detail = issue.Message, ConnectionCode = issue.Code,
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
                if (action == "stop") client.Timeout = TimeSpan.FromSeconds(105);
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
                    FriendActionResult? denied = null;
                    try { denied = JsonSerializer.Deserialize<FriendActionResult>(await response.Content.ReadAsStringAsync(), Json); }
                    catch (JsonException) { /* A generic 403 has no action result. */ }
                    if (denied?.Code == "Revoked") view = view with { State = "Revoked", Detail = "Host refreshed this server code or revoked this PC. Ask for the current code.", ConnectionCode = "Revoked",
                        RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                    else if (denied is null) view = view with { State = "Disconnected/Unknown", Detail = "Host access is unavailable or denied.", ConnectionCode = "HostAccessDenied",
                        RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                    return denied ?? new(false, "Disconnected", "Host access is unavailable or denied.", null);
                }
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    view = view with { State = "Disconnected/Unknown", Detail = "Host companion access is paused.", ConnectionCode = "HostUnavailable",
                        RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    view = view with { State = "Disconnected/Unknown", Detail = "Host rejected this device credential.", ConnectionCode = "CredentialRejected",
                        RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                return await response.Content.ReadFromJsonAsync<FriendActionResult>(Json)
                    ?? new(false, "InvalidResponse", "Host returned an empty action result.", null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                var issue = ConnectionFailure(ex);
                view = view with { State = "Disconnected/Unknown", Detail = issue.Message, ConnectionCode = issue.Code,
                    RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                return new(false, issue.Code, issue.Message + " The action result is unknown; check Host status before retrying.", null);
            }
        }
        finally { gate.Release(); }
    }

    private static FriendConfiguration? LoadConfig(LocalData data, string configFile)
    {
        var bytes = data.LoadProtected(configFile);
        return bytes is null ? null : JsonSerializer.Deserialize<FriendConfiguration>(bytes, Json);
    }

    private static bool ValidFingerprint(string? value)
    {
        if (value?.Length != 64) return false;
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }

    private sealed record ConnectionIssue(string Code, string Message);

    private static FriendActionResult PairConnectionFailure(Exception failure)
    {
        var issue = ConnectionFailure(failure);
        return new(false, issue.Code, issue.Message, null);
    }

    private static ConnectionIssue ConnectionFailure(Exception failure)
    {
        for (var error = failure; error is not null; error = error.InnerException)
        {
            if (error is AuthenticationException)
                return new("HostIdentityMismatch",
                    "The Host's HTTPS identity did not match this invite. Do not continue with this code; ask the Host for a new copy.");
            if (error is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused)
                return new("HostPortClosed", "The connection was refused at the invite address. The Host HTTPS listener may be off.");
            if (error is SocketException timedOut && timedOut.SocketErrorCode == SocketError.TimedOut)
                return new("HostPortTimedOut", "The invite address and Friend TCP port did not answer before the connection timed out.");
            if (error is SocketException network && network.SocketErrorCode == SocketError.NetworkUnreachable)
                return new("FriendNetworkUnavailable", "This PC could not route to the invite address. Check this PC's internet connection.");
            if (error is SocketException unreachable && unreachable.SocketErrorCode == SocketError.HostUnreachable)
                return new("HostUnreachable", "The invite address could not be reached from this network.");
            if (error is SocketException missingAddress && missingAddress.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
                return new("InviteAddressInvalid", "The invite address could not be resolved. Ask the Host for a fresh code.");
        }
        if (failure is TaskCanceledException)
            return new("HostTimedOut", "The Host did not respond before the request timed out. Its connection state is unknown.");
        if (failure is JsonException)
            return new("HostInvalidResponse", "The Host returned a response this app could not read. Ask the Host to check its app version and status.");
        return new("Disconnected", "Could not verify the Host over HTTPS. Check this PC's internet connection and the invite address.");
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

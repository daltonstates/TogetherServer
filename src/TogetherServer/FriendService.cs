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
    public List<string>? AcceptedFingerprints { get; set; }
    public Guid HostId { get; set; }
    public Guid DeviceId { get; set; }
    public string Credential { get; set; } = "";
    public DateTimeOffset CredentialExpiresUtc { get; set; }
    public DateTimeOffset? CertificateExpiresUtc { get; set; }
    public Guid? PendingRenewalRequestId { get; set; }
    public ConnectionRoute? Route { get; set; }
}

public sealed record PublicProfile(Guid Id, string Name, string State, string? JoinAddress,
    bool CanStopNow = false, string? StopReason = null, string Kind = "",
    int? OnlinePlayers = null, int? MaxPlayers = null, DateTimeOffset? AutoShutdownAtUtc = null,
    string? AutoShutdownReason = null, bool CanStart = false, bool CanStop = false,
    bool CanRestartNow = false, string? RestartReason = null,
    RemoteOperationView? Operation = null);
public sealed record CompanionStatus(bool RemoteControlsEnabled, string? Notice, IReadOnlyList<PublicProfile> Profiles,
    bool CanStart, bool CanStop, DateTimeOffset ReceivedUtc, CompanionProtocolInfo? Protocol = null,
    HostCertificateState? Certificates = null, ConnectionRoute? Route = null);
public sealed record FriendView(string Mode, string State, string Detail, string Endpoint, DateTimeOffset? LastConnectedUtc,
    bool RemoteControlsEnabled, bool CanStart, bool CanStop, IReadOnlyList<PublicProfile> Profiles,
    Guid ConnectionId = default, IReadOnlyList<FriendView>? Connections = null,
    string? ConnectionCode = null, string? HostVersion = null,
    string FriendVersion = "", int? HostProtocolVersion = null, bool ProtocolCompatible = true,
    DateTimeOffset? CredentialExpiresUtc = null, DateTimeOffset? CertificateExpiresUtc = null,
    string? ExpiryWarning = null, string RouteMode = ConnectionRouteModes.DirectInternet,
    string? RouteAddress = null, Guid HostId = default);
public sealed record FriendActionResult(bool Ok, string Code, string Message, CompanionStatus? Status,
    IReadOnlyList<PortConflictView>? PortConflicts = null, Guid? OperationId = null,
    string? OperationState = null);

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
    private HttpClient? client;

    public FriendLink(LocalData data, string configFile)
    {
        this.data = data;
        this.configFile = configFile;
        config = LoadConfig(data, configFile);
        if (config is not null)
        {
            config.AcceptedFingerprints = ValidFingerprints(config.AcceptedFingerprints)
                .Append(config.Fingerprint).Where(ValidFingerprint).Distinct(StringComparer.Ordinal).ToList();
            config.Route = ConnectionRoutes.Normalize(config.Route);
        }
        view = config is null
            ? new("Friend", "Not paired", "Paste the server invite code from the Host PC.", "", null, false, false, false, [])
            : new("Friend", "Disconnected/Unknown", "Waiting for a verified Host response.", config.Endpoint,
                null, false, false, false, []);
    }

    public FriendView View() => view;

    public GameEndpointProbeResult ProbeGameEndpoint(Guid profileId)
    {
        var profile = view.Profiles.SingleOrDefault(item => item.Id == profileId);
        return profile is null
            ? new(false, "UnknownProfile", "This server is not available from the selected Host connection.", DateTimeOffset.UtcNow)
            : GameEndpointProbe.Check(profile);
    }

    public async Task<FriendActionResult> PairAsync(string invitation, string? hostAddress = null)
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
                invite.DeviceId == Guid.Empty || invite.ExpiresUtc <= DateTimeOffset.UtcNow)
                return new(false, "InvalidInvite", "Invite is invalid or expired.", null);
            if (!string.IsNullOrWhiteSpace(hostAddress) &&
                (!HostIdentity.TryAddress(hostAddress, out var enteredEndpoint) ||
                 !string.Equals(enteredEndpoint, invite.Endpoint, StringComparison.OrdinalIgnoreCase)))
                return new(false, "HostAddressMismatch", "Host IP or port differs from this pairing invitation. Check the address with the Host.", null);
            try
            {
                using var pairingClient = MakeClient(invite.Endpoint, [invite.Fingerprint]);
                using var response = await pairingClient.PostAsync("api/companion/pair", new StringContent(
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
                    AcceptedFingerprints = [invite.Fingerprint], Credential = credential.Credential,
                    CredentialExpiresUtc = credential.ExpiresUtc,
                    Route = new ConnectionRoute()
                };
                data.SaveProtected(configFile, JsonSerializer.SerializeToUtf8Bytes(config, Json));
                client?.Dispose();
                client = null;
                instanceId = Guid.NewGuid();
                sequence = 0;
                view = new FriendView("Friend", "Disconnected/Unknown", "Paired; waiting for an authenticated heartbeat.",
                    config.Endpoint, null, false, false, false, []);
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
            if (config.CredentialExpiresUtc <= DateTimeOffset.UtcNow)
            {
                view = view with { State = "Disconnected/Unknown", Detail = "Device credential expired; ask the Host for the current server code.", ConnectionCode = "CredentialExpired",
                    RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                return view;
            }
            try
            {
                await RenewCredentialIfNeededAsync();
                var hostClient = HostClient();
                var heartbeat = new HeartbeatRequest(config.DeviceId, instanceId, ++sequence,
                    CompanionProtocol.AppVersion, CompanionProtocol.Current, CompanionProtocol.Capabilities);
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/companion/heartbeat")
                {
                    Content = new StringContent(JsonSerializer.Serialize(heartbeat, Json), Encoding.UTF8, "application/json")
                };
                using var response = await hostClient.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    PairingDecision? denial = null;
                    try { denial = JsonSerializer.Deserialize<PairingDecision>(await response.Content.ReadAsStringAsync(), Json); }
                    catch (JsonException) { /* A generic 403 is not evidence of revocation. */ }
                    var revoked = denial?.Code == "Revoked";
                    view = view with { State = revoked ? "Revoked" : "Disconnected/Unknown",
                        Detail = revoked ? "Host refreshed this server code or revoked this PC. Ask for the current code." : "Host access is unavailable or denied.",
                        ConnectionCode = revoked ? "Revoked" : "HostAccessDenied",
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
                        RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                    return view;
                }
                var status = await response.Content.ReadFromJsonAsync<CompanionStatus>(Json);
                if (status is null) throw new IOException("Host status was empty.");
                ApplyHostMetadata(status.Certificates, status.Route);
                var compatible = CompanionProtocol.Supports(status.Protocol);
                var warning = ExpiryWarning();
                view = new FriendView("Friend", !compatible ? "Update required" :
                        status.RemoteControlsEnabled ? "Connected" : "Disabled",
                    !compatible ? status.Protocol?.CompatibilityMessage ?? "Update required before remote controls can be used." :
                        status.RemoteControlsEnabled ? "Authenticated Host connection." : status.Notice ?? "Host remote controls are off.",
                    config.Endpoint, DateTimeOffset.UtcNow, compatible && status.RemoteControlsEnabled,
                    compatible && status.CanStart, compatible && status.CanStop, status.Profiles,
                    HostVersion: status.Protocol?.AppVersion, FriendVersion: CompanionProtocol.AppVersion,
                    HostProtocolVersion: status.Protocol?.ProtocolVersion, ProtocolCompatible: compatible,
                    CredentialExpiresUtc: config.CredentialExpiresUtc,
                    CertificateExpiresUtc: config.CertificateExpiresUtc, ExpiryWarning: warning,
                    RouteMode: config.Route?.Mode ?? ConnectionRouteModes.DirectInternet,
                    RouteAddress: config.Route?.Address, HostId: config.HostId);
                return view;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                var issue = ConnectionFailure(ex);
                view = view with { State = "Disconnected/Unknown", Detail = issue.Message, ConnectionCode = issue.Code,
                    RemoteControlsEnabled = false, CanStart = false, CanStop = false, Profiles = [] };
                return view;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<FriendActionResult> RecoverEndpointAsync(string endpoint)
    {
        await gate.WaitAsync();
        try
        {
            if (config is null) return new(false, "NotPaired", "Choose a saved Host connection first.", null);
            if (!HostIdentity.TryEndpoint(endpoint, out var parsed))
                return new(false, "InvalidEndpoint", "Enter an HTTPS IPv4 address and port supplied by the Host.", null);
            var normalized = parsed.GetLeftPart(UriPartial.Authority);
            try
            {
                using var recoveryClient = MakeClient(normalized, AcceptedPins());
                recoveryClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Credential);
                recoveryClient.DefaultRequestHeaders.Add("X-Device-Id", config.DeviceId.ToString());
                using var response = await recoveryClient.PostAsJsonAsync("api/companion/endpoint/recover",
                    new EndpointRecoveryProofRequest(config.DeviceId), Json);
                if (!response.IsSuccessStatusCode)
                    return new(false, response.StatusCode == HttpStatusCode.Unauthorized ? "CredentialRejected" : "RecoveryRejected",
                        response.StatusCode == HttpStatusCode.Unauthorized
                            ? "The Host rejected this saved credential. Pair again with a new code."
                            : $"The Host did not approve endpoint recovery ({(int)response.StatusCode}).", null);
                var proof = await response.Content.ReadFromJsonAsync<EndpointRecoveryProof>(Json);
                if (proof is null || !string.Equals(proof.Endpoint.TrimEnd('/'), normalized.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                    proof.HostId == Guid.Empty || config.HostId != Guid.Empty && proof.HostId != config.HostId ||
                    !AcceptedPins().Contains(proof.Certificates.ActiveFingerprint, StringComparer.Ordinal))
                    return new(false, "RecoveryProofInvalid", "The endpoint did not prove the saved Host identity and credential.", null);
                config.Endpoint = normalized;
                config.HostId = proof.HostId;
                ApplyHostMetadata(proof.Certificates, proof.Route);
                SaveConfig();
                client?.Dispose();
                client = null;
                view = view with { Endpoint = config.Endpoint, State = "Disconnected/Unknown",
                    Detail = "Host endpoint recovered; checking the authenticated connection.", ConnectionCode = null,
                    RouteMode = config.Route?.Mode ?? ConnectionRouteModes.DirectInternet,
                    RouteAddress = config.Route?.Address, HostId = config.HostId };
                return new(true, "EndpointRecovered", "The saved Host endpoint changed only after its existing TLS pin and device credential were verified.", null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            { return PairConnectionFailure(ex); }
        }
        finally { gate.Release(); }
    }

    public async Task<FriendActionResult> RequestAsync(Guid profileId, string action)
    {
        await gate.WaitAsync();
        try
        {
            if (config is null) return new(false, "NotPaired", "Pair with a Host first.", null);
            if (action is not ("start" or "stop" or "restart" or "replace"))
                return new(false, "InvalidAction", "Only Start, Stop, Restart, and empty-server conflict replacement are available.", null);
            try
            {
                var hostClient = HostClient();
                var requestId = Guid.NewGuid();
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/companion/" + action)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { deviceId = config.DeviceId, profileId }, Json), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Idempotency-Key", requestId.ToString());
                using var response = await hostClient.SendAsync(request);
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
                var submitted = await response.Content.ReadFromJsonAsync<FriendActionResult>(Json)
                    ?? new(false, "InvalidResponse", "Host returned an empty action result.", null);
                return submitted;
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

    private HttpClient HostClient()
    {
        if (config is null) throw new InvalidOperationException("Pair with a Host first.");
        if (client is not null) return client;
        client = MakeClient(config.Endpoint, AcceptedPins());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Credential);
        client.DefaultRequestHeaders.Add("X-Device-Id", config.DeviceId.ToString());
        return client;
    }

    private static FriendConfiguration? LoadConfig(LocalData data, string configFile)
    {
        var bytes = data.LoadProtected(configFile);
        return bytes is null ? null : JsonSerializer.Deserialize<FriendConfiguration>(bytes, Json);
    }

    private void SaveConfig()
    {
        if (config is null) return;
        data.SaveProtected(configFile, JsonSerializer.SerializeToUtf8Bytes(config, Json));
    }

    private IReadOnlyList<string> AcceptedPins()
    {
        if (config is null) return [];
        var pins = ValidFingerprints(config.AcceptedFingerprints).Append(config.Fingerprint)
            .Where(ValidFingerprint).Distinct(StringComparer.Ordinal).ToList();
        if (pins.Count == 0) throw new InvalidDataException("The saved Host fingerprint is invalid.");
        return pins;
    }

    private void ApplyHostMetadata(HostCertificateState? certificates, ConnectionRoute? route)
    {
        if (config is null) return;
        if (certificates is not null)
        {
            var accepted = AcceptedPins();
            if (config.HostId != Guid.Empty && certificates.HostId != config.HostId)
                throw new AuthenticationException("The authenticated endpoint returned a different Host identity.");
            if (!accepted.Contains(certificates.ActiveFingerprint, StringComparer.Ordinal))
                throw new AuthenticationException("The active Host certificate was not one of the saved pins.");
            var announced = new[] { certificates.ActiveFingerprint, certificates.NextFingerprint,
                    certificates.PreviousAcceptedUntilUtc > DateTimeOffset.UtcNow ? certificates.PreviousFingerprint : null }
                .Where(ValidFingerprint).Cast<string>().Distinct(StringComparer.Ordinal).ToList();
            var pinsChanged = !accepted.ToHashSet(StringComparer.Ordinal).SetEquals(announced);
            config.HostId = certificates.HostId;
            config.Fingerprint = certificates.ActiveFingerprint;
            config.AcceptedFingerprints = announced;
            config.CertificateExpiresUtc = certificates.ActiveExpiresUtc;
            if (pinsChanged)
            {
                client?.Dispose();
                client = null;
            }
        }
        config.Route = ConnectionRoutes.Normalize(route ?? config.Route);
        SaveConfig();
    }

    private async Task RenewCredentialIfNeededAsync()
    {
        if (config is null || config.CredentialExpiresUtc - DateTimeOffset.UtcNow > TimeSpan.FromDays(14)) return;
        config.PendingRenewalRequestId ??= Guid.NewGuid();
        SaveConfig();
        var hostClient = HostClient();
        using var response = await hostClient.PostAsJsonAsync("api/companion/credential/renew",
            new CredentialRenewalRequest(config.DeviceId, config.PendingRenewalRequestId.Value), Json);
        if (!response.IsSuccessStatusCode) return;
        var renewal = await response.Content.ReadFromJsonAsync<CredentialRenewal>(Json);
        if (renewal is null || renewal.DeviceId != config.DeviceId ||
            renewal.RequestId != config.PendingRenewalRequestId || renewal.Credential.Length < 32 ||
            renewal.ExpiresUtc <= DateTimeOffset.UtcNow)
            throw new IOException("Host returned an invalid credential renewal.");
        config.Credential = renewal.Credential;
        config.CredentialExpiresUtc = renewal.ExpiresUtc;
        config.PendingRenewalRequestId = null;
        SaveConfig();
        client?.Dispose();
        client = null;
    }

    private string? ExpiryWarning()
    {
        if (config is null) return null;
        var warnings = new List<string>();
        if (config.CredentialExpiresUtc - DateTimeOffset.UtcNow <= TimeSpan.FromDays(14))
            warnings.Add($"Device credential expires {config.CredentialExpiresUtc.LocalDateTime:g}");
        if (config.CertificateExpiresUtc is { } certificateExpiry && certificateExpiry - DateTimeOffset.UtcNow <= TimeSpan.FromDays(30))
            warnings.Add($"Host certificate expires {certificateExpiry.LocalDateTime:g}");
        return warnings.Count == 0 ? null : string.Join(". ", warnings) + ".";
    }

    private static IEnumerable<string> ValidFingerprints(IEnumerable<string>? values) =>
        values?.Where(ValidFingerprint) ?? [];

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

    private static HttpClient MakeClient(string endpoint, IEnumerable<string> fingerprints)
    {
        var pins = fingerprints.Where(ValidFingerprint).Select(Convert.FromHexString).ToList();
        if (pins.Count == 0) throw new AuthenticationException("No valid Host certificate pin is saved.");
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                DateTimeOffset.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
                DateTimeOffset.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
                pins.Any(pin => CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), pin))
        };
        return new HttpClient(handler) { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(6) };
    }
}

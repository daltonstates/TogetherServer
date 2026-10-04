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
    public string DisplayName { get; set; } = "";
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
    public List<PendingFriendOperation>? PendingOperations { get; set; }
    public List<PublicProfile>? CachedProfiles { get; set; }
    public List<Guid> ConsentedSharedWorldProfiles { get; set; } = [];
    public Dictionary<Guid, string> SharedWorldSigningKeys { get; set; } = [];
    public Dictionary<Guid, long> LastSharedHostVersions { get; set; } = [];
    public Dictionary<Guid, string> LastSharedHostHashes { get; set; } = [];
    public Dictionary<Guid, SharedWorldVersion> LastSharedHostManifests { get; set; } = [];
    public Dictionary<Guid, List<SharedWorldVersion>> CompetingSharedHostManifests { get; set; } = [];
    public HashSet<Guid> SharedWorldConflicts { get; set; } = [];
    public Dictionary<Guid, string> ChatOwnerKeys { get; set; } = [];
    public HashSet<Guid> ChatDeniedProfiles { get; set; } = [];
    public Dictionary<Guid, Guid> LastSharedHostGroups { get; set; } = [];
    public Dictionary<Guid, Guid> PendingSharedWorldGroups { get; set; } = [];
    public Dictionary<Guid, Guid> ApprovedSharedWorldGroups { get; set; } = [];
    public Dictionary<Guid, SharedRosterFloor> SharedRosterFloors { get; set; } = [];
}

public sealed record SharedRosterFloor(Guid GroupId, long Epoch, long Revision, string Signature);

public sealed class PendingFriendOperation
{
    public Guid RequestId { get; set; }
    public Guid ProfileId { get; set; }
    public string Action { get; set; } = "";
    public Guid? OperationId { get; set; }
    public DateTimeOffset RequestedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record PublicProfile(Guid Id, string Name, string State, string? JoinAddress,
    bool CanStopNow = false, string? StopReason = null, string Kind = "",
    int? OnlinePlayers = null, int? MaxPlayers = null, DateTimeOffset? AutoShutdownAtUtc = null,
    string? AutoShutdownReason = null, bool CanStart = false, bool CanStop = false,
    bool CanRestartNow = false, string? RestartReason = null,
    RemoteOperationView? Operation = null,
    bool MaintenanceEnabled = false, string? MaintenanceMessage = null,
    bool CanExtendTimer = false, int TimerExtensionMinutes = 0,
    int TimerExtensionRemainingMinutes = 0, bool CanViewLogs = false);
public sealed record CompanionStatus(bool RemoteControlsEnabled, string? Notice, IReadOnlyList<PublicProfile> Profiles,
    bool CanStart, bool CanStop, DateTimeOffset ReceivedUtc, CompanionProtocolInfo? Protocol = null,
    HostCertificateState? Certificates = null, ConnectionRoute? Route = null,
    IReadOnlyList<ActivityEvent>? Activity = null);
public sealed record ChatProfile(Guid Id, string Name, bool Supported);
public sealed record FriendView(string Mode, string State, string Detail, string Endpoint, DateTimeOffset? LastConnectedUtc,
    bool RemoteControlsEnabled, bool CanStart, bool CanStop, IReadOnlyList<PublicProfile> Profiles,
    IReadOnlyList<string> HostCapabilities,
    Guid ConnectionId = default, IReadOnlyList<FriendView>? Connections = null,
    string? ConnectionCode = null, string? HostVersion = null,
    string FriendVersion = "", int? HostProtocolVersion = null, bool ProtocolCompatible = true,
    DateTimeOffset? CredentialExpiresUtc = null, DateTimeOffset? CertificateExpiresUtc = null,
    string? ExpiryWarning = null, string RouteMode = ConnectionRouteModes.DirectInternet,
    string? RouteAddress = null, Guid HostId = default, string? ConnectionName = null,
    IReadOnlyList<ActivityEvent>? Activity = null,
    IReadOnlyList<ChatProfile>? ChatProfiles = null);
public sealed record FriendActionResult(bool Ok, string Code, string Message, CompanionStatus? Status,
    IReadOnlyList<PortConflictView>? PortConflicts = null, Guid? OperationId = null,
    string? OperationState = null);

internal sealed partial class FriendLink : IDisposable
{
    internal const int MaximumServerLogResponseBytes = 3 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifetimeSync = new();
    private readonly LocalData data;
    private readonly ServerChat chat;
    private readonly string configFile;
    private readonly Func<string, IEnumerable<string>, HttpClient>? historyReviewClientFactory;
    private FriendConfiguration? config;
    private FriendView view;
    private Guid instanceId = Guid.NewGuid();
    private long sequence;
    private DateTimeOffset lastChatSyncUtc;
    private int nextChatProfile;
    private HttpClient? client;
    private readonly Func<string, IEnumerable<string>, HttpClient> makeClient;
    private readonly SharedWorldHostLoss sharedHostLoss;
    internal bool CurrentRecoveryHostLoss(Guid profileId)
    {
        var connection = config;
        if (connection?.ApprovedSharedWorldGroups?.TryGetValue(profileId, out var groupId) != true ||
            !connection.ConsentedSharedWorldProfiles.Contains(profileId) || !sharedHostLoss.MayPropose)
            return false;
        try
        {
            var heads = WorldAuthorityTrust.EffectiveHeads(new WorldAuthorityStore(data).Read(profileId));
            if (heads.Length == 0) return true;
            // Enrollment keeps the old Host connection. Failure at its address
            // cannot establish loss of the successor named by signed authority.
            return heads.Length == 1 && heads[0].Proposal.GroupId == groupId &&
                HostIdentity.TryEndpoint(connection.Endpoint, out var observed) &&
                HostIdentity.TryEndpoint(heads[0].Proposal.CandidateAddress, out var current) &&
                observed.GetLeftPart(UriPartial.Authority).Equals(
                    current.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return false; }
    }

    internal async Task<bool> ProbeRecoveryHostLossAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!CurrentRecoveryHostLoss(profileId)) return false;
            try
            {
                using var response = await HostClient().GetAsync("api/companion/status", cancellationToken);
                ObserveSharedHost(HostReachabilityObservation.OtherResponse);
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                var issue = ConnectionFailure(ex);
                ObserveSharedHost(issue.Code is "HostPortClosed" or "HostPortTimedOut" or
                    "HostUnreachable" or "HostTimedOut" or "FriendNetworkUnavailable"
                    ? HostReachabilityObservation.TransportFailure
                    : HostReachabilityObservation.OtherResponse);
                return CurrentRecoveryHostLoss(profileId);
            }
        }
        finally { gate.Release(); }
    }

    private void ObserveSharedHost(HostReachabilityObservation observation)
    {
        sharedHostLoss.Observe(observation);
        if (observation != HostReachabilityObservation.TransportFailure && config is not null)
        {
            var inbox = new SharedWorldVoteInbox(data);
            var separate = new SharedWorldSeparateCopyStore(data);
            foreach (var profileId in config.ApprovedSharedWorldGroups?.Keys.AsEnumerable() ??
                     Enumerable.Empty<Guid>())
            {
                lock (SharedWorldMutationGate.For(data.RootPath))
                    separate.MarkHostReturned(profileId);
                // Recovery offers require Host loss. A resolution offer instead
                // names the exact competing signed heads and remains useful
                // when the old Host returns to review the split.
                var resolution = inbox.Armed(profileId);
                var floor = config.SharedRosterFloors?.GetValueOrDefault(profileId);
                if (resolution?.Proposal.Schema != 3 ||
                    config.ConsentedSharedWorldProfiles?.Contains(profileId) != true ||
                    config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) !=
                        resolution.Roster.GroupId ||
                    config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) !=
                        resolution.Roster.OwnerPublicKey ||
                    floor is null || floor.Epoch != resolution.Roster.Epoch ||
                    floor.Revision != resolution.Roster.Revision ||
                    floor.Signature != resolution.Roster.Signature)
                    inbox.Retire(profileId);
            }
        }
    }
    private int retainedOperations;
    private bool disposed;
    private bool resourcesDisposed;

    public FriendLink(LocalData data, string configFile,
        Func<string, IEnumerable<string>, HttpClient>? clientFactory = null,
        SharedWorldHostLoss? hostLoss = null)
    {
        sharedHostLoss = hostLoss ?? new SharedWorldHostLoss();
        makeClient = clientFactory ?? MakeClient;
        this.data = data;
        chat = new ServerChat(data);
        this.configFile = configFile;
        historyReviewClientFactory = clientFactory;
        config = LoadConfig(data, configFile);
        if (config is not null)
        {
            if (string.IsNullOrWhiteSpace(config.DisplayName))
                config.DisplayName = HostLabel(config.Endpoint);
            config.AcceptedFingerprints = ValidFingerprints(config.AcceptedFingerprints)
                .Append(config.Fingerprint).Where(ValidFingerprint).Distinct(StringComparer.Ordinal).ToList();
            config.Route = ConnectionRoutes.Normalize(config.Route);
            config.PendingOperations = (config.PendingOperations ?? [])
                .Where(item => item.RequestId != Guid.Empty && item.ProfileId != Guid.Empty &&
                    item.Action is "start" or "stop" or "restart" or "replace" or "extend" &&
                    item.RequestedUtc >= DateTimeOffset.UtcNow.AddDays(-30))
                .OrderBy(item => item.RequestedUtc).TakeLast(20).ToList();
            config.CachedProfiles ??= [];
            config.ChatOwnerKeys ??= [];
            config.ChatDeniedProfiles ??= [];
        }
        view = config is null
            ? new("Friend", "Not connected", "Paste the server code from the Host PC.", "", null, false, false, false, [], [])
            : new("Friend", "Disconnected/Unknown", "Waiting for a verified Host response.", config.Endpoint,
                null, false, false, false, [], [], ConnectionName: config.DisplayName,
                ChatProfiles: CachedChatProfiles());
    }

    public FriendView View() => view;
    internal Guid ChatHostId => config?.HostId ?? Guid.Empty;
    internal bool Configured => config is not null;

    public GameEndpointProbeResult ProbeGameEndpoint(Guid profileId)
    {
        var profile = view.Profiles.SingleOrDefault(item => item.Id == profileId);
        return profile is null
            ? new(false, "UnknownProfile", "This server is not available from the selected Host connection.", DateTimeOffset.UtcNow)
            : GameEndpointProbe.Check(profile);
    }

    public async Task<ServerLogResult> ReadLogsAsync(Guid profileId, ServerLogQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!TryRetain()) return LogFailure("ConnectionClosed", "This saved Host connection is closing.");
        var entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            entered = true;
            if (config is null) return LogFailure("NotPaired", "Connect to a Host first.");
            var profile = view.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null)
                return new(false, "UnknownProfile",
                    "This server is not available from the selected Host connection.",
                    ServerLogSourceStates.Missing, null, [], null, false);
            if (!profile.CanViewLogs)
                return LogFailure("PermissionDenied",
                    "The Host has not granted View logs permission for this server to this PC.");
            if (view.HostCapabilities?.Contains(CompanionProtocol.ServerLogsCapability,
                    StringComparer.Ordinal) != true)
                return new(false, "ServerLogsUpdateRequired",
                    "Update the Host app before viewing server logs from this PC.",
                    ServerLogSourceStates.Unsupported, null, [], null, false);
            if (config.CredentialExpiresUtc <= DateTimeOffset.UtcNow)
                return LogFailure("CredentialExpired",
                    "This PC's saved access expired. Connect again with the current server code.");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"api/companion/servers/{profileId}/logs?{ServerLogService.QueryString(query)}");
                using var response = await HostClient().SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var payload = await ReadBoundedLogPayloadAsync(response.Content, cancellationToken);
                if (payload is null)
                    return LogFailure("LogResponseTooLarge",
                        "The Host returned an oversized server-log response, so it was not read.");
                try
                {
                    var result = JsonSerializer.Deserialize<ServerLogResult>(payload, Json);
                    if (ValidLogResult(result)) return result!;
                }
                catch (JsonException) { /* Parse a bounded denial below. */ }

                try
                {
                    var denial = JsonSerializer.Deserialize<PairingDecision>(payload, Json);
                    if (denial is { Code.Length: > 0 and <= 80, Message.Length: > 0 and <= 600 } &&
                        !denial.Code.Any(char.IsControl) && !denial.Message.Any(char.IsControl))
                        return LogFailure(denial.Code, denial.Message);
                }
                catch (JsonException) { /* Converted to a typed invalid response below. */ }
                return LogFailure("InvalidResponse", "The Host returned an unreadable server-log response.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                var issue = ConnectionFailure(ex);
                return LogFailure(issue.Code, issue.Message);
            }
        }
        finally
        {
            if (entered) gate.Release();
            ReleaseRetained();
        }
    }

    public async Task<FriendActionResult> PairAsync(string invitation, string? hostAddress = null)
    {
        if (!TryRetain()) return ClosedAction();
        await gate.WaitAsync();
        try
        {
            PairingInvite? invite;
            if (invitation?.TrimStart().StartsWith('{') == true)
            {
                // Existing JSON invitations remain readable for migration.
                try { invite = JsonSerializer.Deserialize<PairingInvite>(invitation, Json); }
                catch (JsonException) { return new(false, "InvalidInvite", "The server code is not valid.", null); }
            }
            else
            {
                if (!PairingPassword.TryDecode(invitation, hostAddress, out invite))
                    return new(false, "InvalidInvite", "This server code is not valid or no longer current. Ask the Host to copy the current code.", null);
            }
            if (invite is null || !HostIdentity.TryEndpoint(invite.Endpoint, out _) ||
                !ValidFingerprint(invite.Fingerprint) || string.IsNullOrWhiteSpace(invite.Code) ||
                invite.DeviceId == Guid.Empty || !invite.ServerScope && invite.ExpiresUtc <= DateTimeOffset.UtcNow)
                return new(false, "InvalidInvite", "This invite has expired or is not valid.", null);
            if (!string.IsNullOrWhiteSpace(hostAddress) &&
                (!HostIdentity.TryAddress(hostAddress, out var enteredEndpoint) ||
                 !string.Equals(enteredEndpoint, invite.Endpoint, StringComparison.OrdinalIgnoreCase)))
                return new(false, "HostAddressMismatch", "The Host address does not match this server code. Check the address with the Host.", null);
            try
            {
                using var pairingClient = MakeClient(invite.Endpoint, [invite.Fingerprint]);
                using var response = await pairingClient.PostAsync("api/companion/pair", new StringContent(
                    JsonSerializer.Serialize(new PairingActivation(invite.DeviceId, invite.Code, invite.ServerScope), Json), Encoding.UTF8, "application/json"));
                if (!response.IsSuccessStatusCode)
                    return response.StatusCode == HttpStatusCode.TooManyRequests
                        ? new(false, "HostBusy", "There were too many connection attempts. Wait a moment, then try again.", null)
                        : response.StatusCode == HttpStatusCode.Unauthorized
                            ? new(false, "PairingRejected", "The Host did not accept this code. It may have been replaced; ask for the current server code.", null)
                            : new(false, "HostUnavailable", $"The Host app returned {(int)response.StatusCode} while connecting. Ask the Host to check its app.", null);
                var credential = await response.Content.ReadFromJsonAsync<PairingCredential>(Json);
                if (credential is null || credential.DeviceId == Guid.Empty ||
                    (!invite.ServerScope && credential.DeviceId != invite.DeviceId) || credential.Credential.Length < 32)
                    return new(false, "PairingRejected", "The Host returned invalid access information.", null);
                config = new FriendConfiguration
                {
                    DisplayName = HostLabel(invite.Endpoint),
                    Endpoint = invite.Endpoint,
                    Fingerprint = invite.Fingerprint,
                    DeviceId = credential.DeviceId,
                    AcceptedFingerprints = [invite.Fingerprint],
                    Credential = credential.Credential,
                    CredentialExpiresUtc = credential.ExpiresUtc,
                    Route = new ConnectionRoute(),
                    PendingOperations = [],
                    CachedProfiles = []
                };
                data.SaveProtected(configFile, JsonSerializer.SerializeToUtf8Bytes(config, Json));
                client?.Dispose();
                client = null;
                instanceId = Guid.NewGuid();
                sequence = 0;
                view = new FriendView("Friend", credential.ApprovalPending ? "Awaiting approval" : "Disconnected/Unknown",
                    credential.ApprovalPending ? "Connected securely. Waiting for the Host to approve this PC."
                        : "Connected. Waiting for the Host to respond.",
                    config.Endpoint, null, false, false, false, [], [], ConnectionName: config.DisplayName);
                return new(true, credential.ApprovalPending ? "ApprovalPending" : "Paired",
                    credential.ApprovalPending
                        ? "This PC was saved securely. The Host must approve it before it can connect."
                        : "This PC connected and its access was saved securely.", null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                return PairConnectionFailure(ex);
            }
        }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<FriendView> PollAsync()
    {
        if (!TryRetain()) return view;
        await gate.WaitAsync();
        try
        {
            if (config is null) return view;
            if (config.CredentialExpiresUtc <= DateTimeOffset.UtcNow)
            {
                view = view with
                {
                    State = "Disconnected/Unknown",
                    Detail = "This PC's saved access expired. Ask the Host for the current server code.",
                    ConnectionCode = "CredentialExpired",
                    RemoteControlsEnabled = false,
                    CanStart = false,
                    CanStop = false,
                    Profiles = []
                };
                return view;
            }
            try
            {
                await RenewCredentialIfNeededAsync();
                if (await PollPendingOperationsAsync())
                {
                    ObserveSharedHost(HostReachabilityObservation.Authenticated);
                    await ReturnAuthorityToOriginalHostAsync();
                    return view;
                }
                var hostClient = HostClient();
                var heartbeat = new HeartbeatRequest(config.DeviceId, instanceId, ++sequence,
                    CompanionProtocol.AppVersion, CompanionProtocol.Current, CompanionProtocol.Capabilities);
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/companion/heartbeat")
                {
                    Content = new StringContent(JsonSerializer.Serialize(heartbeat, Json), Encoding.UTF8, "application/json")
                };
                using var response = await hostClient.SendAsync(request);
                // A reachable endpoint, including an access denial, does not
                // establish that the Host disappeared. Only transport failure
                // may start the manual takeover wait.
                ObserveSharedHost(HostReachabilityObservation.OtherResponse);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    PairingDecision? denial = null;
                    try { denial = JsonSerializer.Deserialize<PairingDecision>(await response.Content.ReadAsStringAsync(), Json); }
                    catch (JsonException) { /* A generic 403 is not evidence of revocation. */ }
                    var revoked = denial?.Code == "Revoked";
                    var approvalPending = denial?.Code == "ApprovalPending";
                    var accessExpired = denial?.Code == "AccessExpired";
                    view = view with
                    {
                        State = revoked ? "Revoked" : approvalPending ? "Awaiting approval" :
                            accessExpired ? "Access expired" : "Disconnected/Unknown",
                        Detail = revoked ? "The Host replaced this server code or removed this PC's access. Ask for the current code."
                            : approvalPending ? "The Host must approve this PC before it can connect."
                            : accessExpired ? "The Host ended access for this PC at the saved time."
                            : "Host access is unavailable or denied.",
                        ConnectionCode = revoked ? "Revoked" : approvalPending ? "ApprovalPending" :
                            accessExpired ? "AccessExpired" : "HostAccessDenied",
                        RemoteControlsEnabled = false,
                        CanStart = false,
                        CanStop = false,
                        Profiles = []
                    };
                    return view;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var issue = response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => new ConnectionIssue("CredentialRejected", "The Host did not accept this PC's saved access. Ask for the current server code."),
                        HttpStatusCode.TooManyRequests => new ConnectionIssue("HostBusy", "The Host is limiting requests. Wait a moment and check again."),
                        _ => new ConnectionIssue("HostUnavailable", $"The Host app returned {(int)response.StatusCode}. Ask the Host to check its app.")
                    };
                    view = view with
                    {
                        State = "Disconnected/Unknown",
                        Detail = issue.Message,
                        ConnectionCode = issue.Code,
                        RemoteControlsEnabled = false,
                        CanStart = false,
                        CanStop = false,
                        Profiles = []
                    };
                    return view;
                }
                var status = await response.Content.ReadFromJsonAsync<CompanionStatus>(Json);
                if (status is null) throw new IOException("Host status was empty.");
                ObserveSharedHost(HostReachabilityObservation.Authenticated);
                ApplyStatus(status);
                if (DateTimeOffset.UtcNow - lastChatSyncUtc >= TimeSpan.FromSeconds(15))
                {
                    lastChatSyncUtc = DateTimeOffset.UtcNow;
                    var profiles = view.Profiles;
                    if (profiles.Count > 0 && view.HostCapabilities.Contains(CompanionProtocol.ServerChatCapability))
                    {
                        var profile = profiles[nextChatProfile++ % profiles.Count];
                        try { _ = await SyncChatCoreAsync(profile.Id); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                                   UnauthorizedAccessException or CryptographicException or
                                                   ArgumentException or InvalidOperationException)
                        { data.TryAudit($"chat-sync-unavailable {ex.GetType().Name} {DateTimeOffset.UtcNow:O}"); }
                    }
                }
                await ReturnAuthorityToOriginalHostAsync();
                return view;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                var issue = ConnectionFailure(ex);
                deliveredAuthority.Clear();
                ObserveSharedHost(issue.Code is "HostPortClosed" or "HostPortTimedOut" or
                    "HostUnreachable" or "HostTimedOut" or "FriendNetworkUnavailable"
                    ? HostReachabilityObservation.TransportFailure
                    : HostReachabilityObservation.OtherResponse);
                view = view with
                {
                    State = "Disconnected/Unknown",
                    Detail = issue.Message,
                    ConnectionCode = issue.Code,
                    RemoteControlsEnabled = false,
                    CanStart = false,
                    CanStop = false,
                    Profiles = ProfilesWithPendingOperations()
                };
                return view;
            }
        }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<FriendActionResult> RecoverEndpointAsync(string endpoint)
    {
        if (!TryRetain()) return ClosedAction();
        CancelSharedTransfers();
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
                recoveryClient.DefaultRequestHeaders.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
                using var response = await recoveryClient.PostAsJsonAsync("api/companion/endpoint/recover",
                    new EndpointRecoveryProofRequest(config.DeviceId), Json);
                if (!response.IsSuccessStatusCode)
                    return new(false, response.StatusCode == HttpStatusCode.Unauthorized ? "CredentialRejected" : "RecoveryRejected",
                        response.StatusCode == HttpStatusCode.Unauthorized
                            ? "The Host did not accept this PC's saved access. Connect again with a new code."
                            : $"The Host did not approve the new address ({(int)response.StatusCode}).", null);
                var proof = await response.Content.ReadFromJsonAsync<EndpointRecoveryProof>(Json);
                if (proof is null || !string.Equals(proof.Endpoint.TrimEnd('/'), normalized.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                    proof.HostId == Guid.Empty || config.HostId != Guid.Empty && proof.HostId != config.HostId ||
                    !AcceptedPins().Contains(proof.Certificates.ActiveFingerprint, StringComparer.Ordinal))
                    return new(false, "RecoveryProofInvalid", "The new address did not match the saved Host and this PC's access.", null);
                config.Endpoint = normalized;
                config.HostId = proof.HostId;
                ApplyHostMetadata(proof.Certificates, proof.Route);
                SaveConfig();
                client?.Dispose();
                client = null;
                view = view with
                {
                    Endpoint = config.Endpoint,
                    State = "Disconnected/Unknown",
                    Detail = "Host address updated. Checking the secure connection.",
                    ConnectionCode = null,
                    RouteMode = config.Route?.Mode ?? ConnectionRouteModes.DirectInternet,
                    RouteAddress = config.Route?.Address,
                    HostId = config.HostId
                };
                return new(true, "EndpointRecovered", "The saved Host address was updated after TogetherServer verified the Host and this PC's access.", null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            { return PairConnectionFailure(ex); }
        }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<FriendActionResult> RenameAsync(string? name)
    {
        if (!TryRetain()) return ClosedAction();
        await gate.WaitAsync();
        try
        {
            var value = name?.Trim() ?? "";
            if (value.Length is < 1 or > 48 || value.Any(char.IsControl))
                return new(false, "InvalidConnectionName", "Use a name between 1 and 48 characters without line breaks.", null);
            if (config is null) return new(false, "NotPaired", "Choose a saved Host connection first.", null);
            config.DisplayName = value;
            SaveConfig();
            view = view with { ConnectionName = value };
            return new(true, "ConnectionRenamed", "Saved Host connection renamed on this PC.", null);
        }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<FriendActionResult> ForgetAsync(bool preserveSharedRoomCopies = false)
    {
        if (!TryRetain()) return ClosedAction();
        CancelSharedTransfers();
        await gate.WaitAsync();
        try
        {
            if (config is null) return new(false, "NotPaired", "Choose a saved Host connection first.", null);
            var revoked = false;
            try
            {
                var hostClient = HostClient();
                using var response = await hostClient.PostAsJsonAsync("api/companion/credential/revoke",
                    new DeviceSelfRequest(config.DeviceId), Json);
                revoked = response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                // Forget remains available offline. The result explicitly tells
                // the user that the Host still needs to revoke the stale PC.
            }
            client?.Dispose();
            client = null;
            chat.Forget(config.HostId, config.DeviceId,
                config.CachedProfiles?.Select(profile => profile.Id) ?? [], preserveSharedRoomCopies);
            data.DeleteProtected(configFile);
            data.DeleteProtected($"shared-world-pc-signing-{config.DeviceId:N}.protected");
            config = null;
            view = new("Friend", "Not connected", "This saved Host connection was forgotten.", "",
                null, false, false, false, [], []);
            return new(true, revoked ? "ConnectionForgottenAndRevoked" : "ConnectionForgottenLocally",
                revoked
                    ? "The Host removed this PC's access, then TogetherServer removed the saved connection from this PC."
                    : "The saved connection was removed from this PC, but the Host could not be reached. Ask the Host to remove this PC from Friend access.",
                null);
        }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<FriendActionResult> RequestAsync(Guid profileId, string action)
    {
        if (!TryRetain()) return ClosedAction();
        await gate.WaitAsync();
        try
        {
            if (config is null) return new(false, "NotPaired", "Connect to a Host first.", null);
            if (action is not ("start" or "stop" or "restart" or "replace" or "extend" or "refresh"))
                return new(false, "InvalidAction", "Only Start, Stop, Restart, adding shutdown time, and player refresh are available.", null);
            if (action == "refresh") return await RefreshPlayerCountAsync(profileId);
            config.PendingOperations ??= [];
            var pending = config.PendingOperations.FirstOrDefault(item => item.ProfileId == profileId);
            if (pending is not null && !pending.Action.Equals(action, StringComparison.Ordinal))
                return new(false, "OperationInProgress",
                    "Wait for the current server action to finish before requesting another one.", null,
                    OperationId: pending.OperationId, OperationState: RemoteOperationStates.Pending);
            if (pending is null)
            {
                pending = new PendingFriendOperation
                {
                    RequestId = Guid.NewGuid(),
                    ProfileId = profileId,
                    Action = action,
                    RequestedUtc = DateTimeOffset.UtcNow
                };
                config.PendingOperations.Add(pending);
                SaveConfig();
            }
            try
            {
                var submitted = await SubmitPendingOperationAsync(pending);
                if (submitted.OperationId is { } operationId)
                {
                    pending.OperationId = operationId;
                    var operation = OperationFromSubmission(pending, submitted);
                    ApplyOperation(operation);
                    if (RemoteOperationStates.Terminal(operation.State))
                        config.PendingOperations.Remove(pending);
                    SaveConfig();
                }
                else if (submitted.Code != "InvalidResponse")
                {
                    config.PendingOperations.Remove(pending);
                    SaveConfig();
                }
                return submitted;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                var issue = ConnectionFailure(ex);
                view = view with
                {
                    State = "Disconnected/Unknown",
                    Detail = issue.Message,
                    ConnectionCode = issue.Code,
                    RemoteControlsEnabled = false,
                    CanStart = false,
                    CanStop = false,
                    Profiles = ProfilesWithPendingOperations()
                };
                return new(false, issue.Code, issue.Message + " The action result is unknown; check Host status before retrying.", null);
            }
        }
        finally { gate.Release(); ReleaseRetained(); }
    }

    private async Task<FriendActionResult> RefreshPlayerCountAsync(Guid profileId)
    {
        if (config is null) return new(false, "NotPaired", "Connect to a Host first.", null);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/companion/refresh")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                { deviceId = config.DeviceId, profileId }, Json), Encoding.UTF8, "application/json")
            };
            using var response = await HostClient().SendAsync(request);
            FriendActionResult? result = null;
            try { result = await response.Content.ReadFromJsonAsync<FriendActionResult>(Json); }
            catch (JsonException) { /* Converted into a bounded invalid-response result below. */ }
            result ??= new(false, "InvalidResponse", "Host returned an unreadable player-count refresh result.", null);
            ApplyActionConnectionState(response.StatusCode, result);
            if (result.Status is { } status) ApplyStatus(status);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            var issue = ConnectionFailure(ex);
            view = view with
            {
                State = "Disconnected/Unknown",
                Detail = issue.Message,
                ConnectionCode = issue.Code,
                RemoteControlsEnabled = false,
                CanStart = false,
                CanStop = false,
                Profiles = ProfilesWithPendingOperations()
            };
            return new(false, issue.Code, issue.Message + " The player-count refresh did not return a result.", null);
        }
    }

    private async Task<FriendActionResult> SubmitPendingOperationAsync(PendingFriendOperation pending)
    {
        if (config is null) return new(false, "NotPaired", "Connect to a Host first.", null);
        var hostClient = HostClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/companion/" + pending.Action)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            { deviceId = config.DeviceId, profileId = pending.ProfileId }, Json), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", pending.RequestId.ToString());
        using var response = await hostClient.SendAsync(request);
        FriendActionResult? result = null;
        try { result = await response.Content.ReadFromJsonAsync<FriendActionResult>(Json); }
        catch (JsonException) { /* Converted into a bounded invalid-response result below. */ }
        result ??= new(false, "InvalidResponse", "Host returned an unreadable action result.", null);
        ApplyActionConnectionState(response.StatusCode, result);
        return result;
    }

    private async Task<bool> PollPendingOperationsAsync()
    {
        if (config?.PendingOperations is not { Count: > 0 } pendingOperations) return false;
        var reachedHost = false;
        foreach (var pending in pendingOperations.ToList())
        {
            if (pending.OperationId is null)
            {
                var submitted = await SubmitPendingOperationAsync(pending);
                if (submitted.OperationId is not { } acceptedId)
                {
                    if (submitted.Code != "InvalidResponse") pendingOperations.Remove(pending);
                    continue;
                }
                reachedHost = true;
                pending.OperationId = acceptedId;
                var submittedOperation = OperationFromSubmission(pending, submitted);
                ApplyOperation(submittedOperation);
                if (RemoteOperationStates.Terminal(submittedOperation.State))
                {
                    pendingOperations.Remove(pending);
                    continue;
                }
            }

            using var response = await HostClient().GetAsync("api/companion/operations/" + pending.OperationId);
            reachedHost = true;
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                PairingDecision? denial = null;
                try { denial = await response.Content.ReadFromJsonAsync<PairingDecision>(Json); }
                catch (JsonException) { /* A generic denial is not an owner access-expiry signal. */ }
                if (denial?.Code == "AccessExpired")
                {
                    ApplyAccessExpired();
                    SaveConfig();
                    return true;
                }
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                ApplyOperation(new RemoteOperationView(pending.OperationId!.Value, pending.ProfileId, pending.Action,
                    RemoteOperationStates.Interrupted, false, "UnknownOperation",
                    "The Host no longer has this operation. It was not replayed; refresh server status before trying again.",
                    pending.RequestedUtc, null, DateTimeOffset.UtcNow));
                pendingOperations.Remove(pending);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Operation status returned {(int)response.StatusCode}.", null, response.StatusCode);
            var operation = await response.Content.ReadFromJsonAsync<RemoteOperationView>(Json)
                ?? throw new IOException("Host operation status was empty.");
            if (operation.Id != pending.OperationId || operation.ProfileId != pending.ProfileId ||
                !operation.Action.Equals(pending.Action, StringComparison.Ordinal))
                throw new IOException("Host operation status did not match the saved request.");
            ApplyOperation(operation);
            if (RemoteOperationStates.Terminal(operation.State)) pendingOperations.Remove(pending);
        }
        SaveConfig();
        if (!reachedHost) return false;
        var active = pendingOperations.Count > 0;
        view = view with
        {
            State = "Connected",
            Detail = active ? "Authenticated Host connection; a remote operation is still in progress."
                : "Authenticated Host connection; the latest remote operation finished.",
            ConnectionCode = null,
            LastConnectedUtc = DateTimeOffset.UtcNow,
            Profiles = ProfilesWithPendingOperations()
        };
        return true;
    }

    private RemoteOperationView OperationFromSubmission(PendingFriendOperation pending, FriendActionResult submitted)
    {
        var state = submitted.OperationState is RemoteOperationStates.Pending or RemoteOperationStates.Running or
            RemoteOperationStates.Succeeded or RemoteOperationStates.Failed or RemoteOperationStates.Interrupted
            ? submitted.OperationState : RemoteOperationStates.Pending;
        return new(submitted.OperationId ?? pending.OperationId ?? Guid.Empty, pending.ProfileId, pending.Action, state,
            RemoteOperationStates.Terminal(state) ? submitted.Ok : null, submitted.Code, submitted.Message,
            pending.RequestedUtc, state == RemoteOperationStates.Pending ? null : DateTimeOffset.UtcNow,
            RemoteOperationStates.Terminal(state) ? DateTimeOffset.UtcNow : null, submitted.PortConflicts);
    }

    private void ApplyOperation(RemoteOperationView operation)
    {
        if (config is null) return;
        var profiles = (view.Profiles.Count > 0 ? view.Profiles : config.CachedProfiles ?? []).ToList();
        var index = profiles.FindIndex(item => item.Id == operation.ProfileId);
        if (index >= 0) profiles[index] = profiles[index] with { Operation = operation };
        else profiles.Add(new PublicProfile(operation.ProfileId, "Server", "Unknown", null, Operation: operation));
        config.CachedProfiles = profiles;
        view = view with { Profiles = profiles };
    }

    private IReadOnlyList<PublicProfile> ProfilesWithPendingOperations()
    {
        if (config is null) return [];
        var profiles = (view.Profiles.Count > 0 ? view.Profiles : config.CachedProfiles ?? []).ToList();
        foreach (var pending in config.PendingOperations ?? [])
        {
            var index = profiles.FindIndex(item => item.Id == pending.ProfileId);
            var existing = index >= 0 ? profiles[index].Operation : null;
            if (existing is not null && existing.Id == pending.OperationId) continue;
            var operation = new RemoteOperationView(pending.OperationId ?? Guid.Empty, pending.ProfileId, pending.Action,
                RemoteOperationStates.Pending, null, "OperationPending",
                "Waiting for the Host to confirm this saved request.", pending.RequestedUtc, null, null);
            if (index >= 0) profiles[index] = profiles[index] with { Operation = operation };
            else profiles.Add(new PublicProfile(pending.ProfileId, "Server", "Unknown", null, Operation: operation));
        }
        return profiles;
    }

    private void ApplyActionConnectionState(HttpStatusCode statusCode, FriendActionResult result)
    {
        if (statusCode == HttpStatusCode.Forbidden && result.Code == "AccessExpired")
            ApplyAccessExpired();
        else if (statusCode == HttpStatusCode.Forbidden && result.Code == "Revoked")
            view = view with
            {
                State = "Revoked",
                Detail = "Host refreshed this server code or revoked this PC. Ask for the current code.",
                ConnectionCode = "Revoked",
                RemoteControlsEnabled = false,
                CanStart = false,
                CanStop = false,
                Profiles = []
            };
        else if (statusCode == HttpStatusCode.Forbidden && result.Code is "Unauthorized" or "Disconnected")
            view = view with
            {
                State = "Disconnected/Unknown",
                Detail = "Host access is unavailable or denied.",
                ConnectionCode = "HostAccessDenied",
                RemoteControlsEnabled = false,
                CanStart = false,
                CanStop = false,
                Profiles = ProfilesWithPendingOperations()
            };
        else if (statusCode == HttpStatusCode.ServiceUnavailable)
            view = view with
            {
                State = "Disconnected/Unknown",
                Detail = "Friend access is paused on the Host.",
                ConnectionCode = "HostUnavailable",
                RemoteControlsEnabled = false,
                CanStart = false,
                CanStop = false,
                Profiles = ProfilesWithPendingOperations()
            };
        else if (statusCode == HttpStatusCode.Unauthorized)
            view = view with
            {
                State = "Disconnected/Unknown",
                Detail = "The Host did not accept this PC's saved access.",
                ConnectionCode = "CredentialRejected",
                RemoteControlsEnabled = false,
                CanStart = false,
                CanStop = false,
                Profiles = ProfilesWithPendingOperations()
            };
    }

    private void ApplyAccessExpired()
    {
        view = view with
        {
            State = "Access expired",
            Detail = "The Host ended access for this PC at the saved time.",
            ConnectionCode = "AccessExpired",
            RemoteControlsEnabled = false,
            CanStart = false,
            CanStop = false,
            Profiles = []
        };
    }

    private void ApplyStatus(CompanionStatus status)
    {
        if (config is null) throw new InvalidOperationException("Connect to a Host first.");
        config.CachedProfiles = status.Profiles.ToList();
        ApplyHostMetadata(status.Certificates, status.Route);
        var compatible = CompanionProtocol.Supports(status.Protocol);
        var warning = ExpiryWarning();
        view = new FriendView("Friend", !compatible ? "Update required" :
                status.RemoteControlsEnabled ? "Connected" : "Disabled",
            !compatible ? status.Protocol?.CompatibilityMessage ?? "Update required before remote controls can be used." :
                status.RemoteControlsEnabled ? "Secure Host connection." : status.Notice ?? "Friend controls are off on the Host.",
            config.Endpoint, DateTimeOffset.UtcNow, compatible && status.RemoteControlsEnabled,
            compatible && status.CanStart, compatible && status.CanStop, status.Profiles,
            status.Protocol?.Capabilities ?? [],
            HostVersion: status.Protocol?.AppVersion, FriendVersion: CompanionProtocol.AppVersion,
            HostProtocolVersion: status.Protocol?.ProtocolVersion, ProtocolCompatible: compatible,
            CredentialExpiresUtc: config.CredentialExpiresUtc,
            CertificateExpiresUtc: config.CertificateExpiresUtc, ExpiryWarning: warning,
            RouteMode: config.Route?.Mode ?? ConnectionRouteModes.DirectInternet,
            RouteAddress: config.Route?.Address, HostId: config.HostId,
            ConnectionName: config.DisplayName, Activity: status.Activity);
        view = view with { ChatProfiles = CachedChatProfiles() };
    }

    private IReadOnlyList<ChatProfile> CachedChatProfiles() =>
        config?.CachedProfiles?.Select(profile => new ChatProfile(profile.Id, profile.Name,
            config.ChatOwnerKeys?.ContainsKey(profile.Id) == true)).ToList() ?? [];

    private static bool ValidLogResult(ServerLogResult? result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.Code) || result.Code.Length > 80 ||
            string.IsNullOrWhiteSpace(result.Message) || result.Message.Length > 600 ||
            result.SourceState is not (ServerLogSourceStates.Active or ServerLogSourceStates.Ended or
                ServerLogSourceStates.Missing or ServerLogSourceStates.Unsupported or ServerLogSourceStates.Unavailable) ||
            result.RunId is { Length: > 32 } || result.Cursor is { Length: > 160 } ||
            result.Records is null || result.Records.Count > ServerLogService.MaximumLimit)
            return false;
        return result.Records.All(record => record is not null && record.Message is not null &&
            record.Message.Length <= 2048 && record.Severity is not null && record.Severity.Length <= 16 &&
            record.Category is not null && record.Category.Length <= 32 &&
            record.Stream is not null && record.Stream.Length <= 16);
    }

    internal static async Task<byte[]?> ReadBoundedLogPayloadAsync(HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumServerLogResponseBytes) return null;
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var payload = new MemoryStream(content.Headers.ContentLength is > 0
            ? (int)content.Headers.ContentLength.Value : 0);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = MaximumServerLogResponseBytes - (int)payload.Length;
            var read = await source.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, remaining + 1)), cancellationToken);
            if (read == 0) return payload.ToArray();
            if (read > remaining) return null;
            payload.Write(buffer, 0, read);
        }
    }

    private static ServerLogResult LogFailure(string code, string message) =>
        new(false, code, message, ServerLogSourceStates.Unavailable, null, [], null, false);

    private HttpClient HostClient()
    {
        if (config is null) throw new InvalidOperationException("Connect to a Host first.");
        if (client is not null) return client;
        client = makeClient(config.Endpoint, AcceptedPins());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Credential);
        client.DefaultRequestHeaders.Add("X-Device-Id", config.DeviceId.ToString());
        client.DefaultRequestHeaders.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
        return client;
    }

    private static FriendConfiguration? LoadConfig(LocalData data, string configFile)
    {
        var config = data.LoadProtectedJson<FriendConfiguration>(configFile);
        if (config is null) return null;
        // HostId was added after the original protected Friend format and is
        // learned from authenticated Host metadata on the first successful
        // poll. Do not quarantine an otherwise valid legacy credential merely
        // because that metadata has not been observed yet.
        if (config.DeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(config.Credential) || config.Credential.Length is < 32 or > 4096 ||
            config.Credential.Any(char.IsControl) || config.CredentialExpiresUtc == default ||
            !HostIdentity.TryEndpoint(config.Endpoint, out _) || !ValidFingerprint(config.Fingerprint) ||
            config.AcceptedFingerprints is { Count: > 10 } ||
            config.PendingOperations is { Count: > 100 } || config.PendingOperations?.Any(item => item is null) == true ||
            config.CachedProfiles is { Count: > 100 } || config.CachedProfiles?.Any(item => item is null) == true)
        {
            data.QuarantineState(configFile,
                "TogetherServer disabled an invalid saved Friend connection for owner review.", false);
            data.TryAudit($"friend-connection-disabled invalid-config {DateTimeOffset.UtcNow:O}");
            return null;
        }
        return config;
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
                CancelSharedTransfers();
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
            warnings.Add($"Saved access expires {config.CredentialExpiresUtc.LocalDateTime:g}");
        if (config.CertificateExpiresUtc is { } certificateExpiry && certificateExpiry - DateTimeOffset.UtcNow <= TimeSpan.FromDays(30))
            warnings.Add($"Secure Host identity expires {certificateExpiry.LocalDateTime:g}");
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

    internal static HttpClient MakeClient(string endpoint, IEnumerable<string> fingerprints)
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

    private static string HostLabel(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? $"Host {uri.Host}" : "Saved Host";

    private bool TryRetain()
    {
        lock (lifetimeSync)
        {
            if (disposed) return false;
            retainedOperations++;
            return true;
        }
    }

    private void ReleaseRetained()
    {
        var cleanup = false;
        lock (lifetimeSync)
        {
            retainedOperations--;
            if (retainedOperations < 0) throw new InvalidOperationException("Friend connection lifetime underflow.");
            if (disposed && retainedOperations == 0 && !resourcesDisposed)
            {
                resourcesDisposed = true;
                cleanup = true;
            }
        }
        if (cleanup) CleanupResources();
    }

    private static FriendActionResult ClosedAction() =>
        new(false, "ConnectionClosed", "This saved Host connection is closing.", null);

    private void CleanupResources()
    {
        client?.Dispose();
        client = null;
        gate.Dispose();
    }

    public void Dispose()
    {
        CancelSharedTransfers();
        var cleanup = false;
        lock (lifetimeSync)
        {
            if (disposed) return;
            disposed = true;
            if (retainedOperations == 0 && !resourcesDisposed)
            {
                resourcesDisposed = true;
                cleanup = true;
            }
        }
        if (cleanup) CleanupResources();
    }
}

public sealed record DeviceSelfRequest(Guid DeviceId);

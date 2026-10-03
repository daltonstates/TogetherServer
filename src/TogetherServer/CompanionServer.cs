using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace TogetherServer;

public static class CompanionListenerStates
{
    public const string Off = "Off";
    public const string Idle = "Idle";
    public const string Listening = "Listening";
    public const string Error = "Error";
}

// The public HTTPS listener is a second listener in the same Windows process.
// It starts only after the owner enables Friend connections and pairing/TLS
// material is ready; the local GUI remains bound to loopback.
public sealed class CompanionServer(LocalData data, HostManager manager, PairingService pairing,
    GameServerRegistry games, ServerLogService serverLogs, SemaphoreSlim modeGate, int localPort, Func<bool>? isUpdating = null,
    Func<bool>? isShuttingDown = null,
    Func<Guid, CancellationToken, Task<bool>>? recoveryLossProbe = null,
    Func<Guid, bool>? recoveryLossCurrent = null)
{
    private readonly HostIdentity identity = new(data);
    private WebApplication? active;
    private X509Certificate2? certificate;
    private string? activeAddress;
    private readonly SemaphoreSlim listenerGate = new(1, 1);
    private readonly RemoteOperationCoordinator operations = new(data);
    private readonly AuthenticatedDeviceRateLimiter deviceRateLimiter = new(180, TimeSpan.FromMinutes(1));
    private readonly SharedWorldEnrollmentNonces sharedEnrollment = new();
    private readonly SharedWorldVoteInbox recoveryVotes = new(data);
    private const int MaximumAuthorityRecordBytes = 2 * 1024 * 1024;

    private static async Task<byte[]?> ReadBoundedAuthorityAsync(Stream body, long? declaredLength,
        CancellationToken cancellationToken)
    {
        if (declaredLength is > MaximumAuthorityRecordBytes) return null;
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await body.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaximumAuthorityRecordBytes) return null;
            output.Write(buffer, 0, read);
        }
        return output.Length == 0 ? null : output.ToArray();
    }

    public bool Active => active is not null && manager.CompanionListeningEnabled;
    public string ListenerState { get; private set; } = CompanionListenerStates.Off;
    public string? Warning { get; private set; }
    public IReadOnlyList<RemoteOperationView> RecentOperations() => operations.Recent();

    public async Task SyncAsync()
    {
        await listenerGate.WaitAsync();
        try { await SyncCoreAsync(); }
        finally { listenerGate.Release(); }
    }

    private async Task SyncCoreAsync()
    {
        var settings = (await manager.SnapshotAsync()).Settings;
        if (!settings.CompanionListeningEnabled)
        {
            await StopCoreAsync();
            Warning = null;
            ListenerState = CompanionListenerStates.Off;
            return;
        }
        var address = settings.CompanionBindAddress + ":" + settings.CompanionPort;
        X509Certificate2? nextCertificate = null;
        WebApplication? nextApp = null;
        try
        {
            if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out var endpoint) ||
                endpoint.Port != settings.CompanionPort || !IPAddress.TryParse(settings.CompanionBindAddress, out var bind) ||
                settings.CompanionPort < 1024 || settings.CompanionPort == localPort)
                throw new InvalidOperationException("The Friend app address, bind address, or TCP port is invalid.");
            if (!pairing.HasInviteOrCredential() && !recoveryVotes.HasArmedOffer(settings.CompanionEndpoint))
            {
                await StopCoreAsync();
                Warning = null;
                ListenerState = CompanionListenerStates.Idle;
                return;
            }
            _ = identity.StageNextIfExpiring(settings.CompanionEndpoint, TimeSpan.FromDays(30));
            nextCertificate = identity.Load();
            if (nextCertificate is null || !nextCertificate.HasPrivateKey ||
                DateTimeOffset.UtcNow < nextCertificate.NotBefore.ToUniversalTime() ||
                DateTimeOffset.UtcNow > nextCertificate.NotAfter.ToUniversalTime())
                throw new InvalidOperationException("The Host TLS identity is unavailable or expired.");
            var configurationKey = address + "|" + endpoint.GetLeftPart(UriPartial.Authority) + "|" +
                HostIdentity.Fingerprint(nextCertificate);
            if (active is not null && activeAddress == configurationKey)
            {
                nextCertificate.Dispose();
                Warning = null;
                ListenerState = CompanionListenerStates.Listening;
                return;
            }
            await StopCoreAsync();
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(bind, settings.CompanionPort, listener => listener.UseHttps(nextCertificate)));
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetFixedWindowLimiter("host", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
                options.AddPolicy("pairing", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            });
            nextApp = builder.Build();
            nextApp.Use(async (context, next) =>
            {
                var authorityIngest = context.Request.Path.Value?.EndsWith(
                    "/shared-world/authority", StringComparison.Ordinal) == true;
                var maximumBody = authorityIngest ? 2 * 1024 * 1024 : 4096;
                if (!manager.CompanionListeningEnabled || !context.Request.IsHttps ||
                    context.Connection.LocalPort != settings.CompanionPort ||
                    !context.Request.Path.StartsWithSegments("/api/companion") ||
                    !string.Equals(context.Request.Host.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) ||
                    context.Request.Host.Port != settings.CompanionPort ||
                    context.Request.ContentLength > maximumBody)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                    bodySize.MaxRequestBodySize = maximumBody;
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Cache-Control"] = "no-store";
                await next();
            });
            nextApp.UseRateLimiter();
            MapRoutes(nextApp);
            await nextApp.StartAsync();
            active = nextApp;
            certificate = nextCertificate;
            activeAddress = configurationKey;
            Warning = null;
            ListenerState = CompanionListenerStates.Listening;
            DiagnosticOutput.WriteLine($"Companion HTTPS listener: {address}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException or
            System.Security.SecurityException or System.Security.Cryptography.CryptographicException)
        {
            if (nextApp is not null) await nextApp.DisposeAsync();
            nextCertificate?.Dispose();
            await StopCoreAsync();
            Warning = "Friend connections could not start: " + ex.Message;
            ListenerState = CompanionListenerStates.Error;
            DiagnosticOutput.WriteError(Warning);
        }
    }

    public async Task StopAsync()
    {
        await listenerGate.WaitAsync();
        try
        {
            await StopCoreAsync();
            await operations.DisposeAsync();
            Warning = null;
            ListenerState = CompanionListenerStates.Off;
        }
        finally { listenerGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var old = active;
        active = null;
        activeAddress = null;
        if (old is not null)
        {
            await old.StopAsync();
            await old.DisposeAsync();
        }
        certificate?.Dispose();
        certificate = null;
    }

    public IReadOnlyDictionary<Guid, object> StopSafety(HostSnapshot snapshot) =>
        snapshot.Settings.Profiles.ToDictionary(profile => profile.Id, profile =>
        {
            using var permit = RemoteStopSafety.TryAcquire(snapshot, profile.Id, data, games);
            return (object)new { available = permit.Allowed, reason = permit.Reason };
        });

    private void MapRoutes(WebApplication app)
    {
        static int AuthenticationStatus(PairingDecision decision) =>
            decision.Code == "RateLimited" ? StatusCodes.Status429TooManyRequests :
            decision.Code is "Revoked" or "ApprovalPending" or "AccessExpired"
                ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;

        bool Authenticate(HttpContext context, out PairedDevice? device, out PairingDecision decision)
            => AuthenticateDetailed(context, out device, out decision, out _);

        bool AuthenticateDetailed(HttpContext context, out PairedDevice? device, out PairingDecision decision,
            out bool usedPreviousCredential)
        {
            usedPreviousCredential = false;
            if (!Guid.TryParse(context.Request.Headers["X-Device-Id"], out var id))
            {
                device = null;
                decision = new(false, "Unauthorized", "Device ID was not accepted.");
                return false;
            }
            var auth = context.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : null;
            decision = pairing.Authenticate(id, token, out device, out usedPreviousCredential);
            if (!decision.Ok) return false;
            if (deviceRateLimiter.TryAcquire(id)) return true;
            device = null;
            decision = new(false, "RateLimited", "This PC is sending too many requests. Wait a moment and try again.");
            return false;
        }

        bool Reauthorize(PairedDevice loadedDevice, out PairedDevice? currentDevice,
            out PairingDecision decision)
        {
            decision = pairing.AuthorizeActiveDevice(loadedDevice.Id, out currentDevice);
            return decision.Ok;
        }

        async Task<(PairingDecision Decision, PairedDevice? Current)> AuthorizeShared(
            PairedDevice loaded, Guid profileId)
        {
            if (pairing.SharedRosterDirty(profileId))
                return (new(false, "RosterUnavailable", "The signed roster needs owner repair."), null);
            var decision = pairing.AuthorizeReceiveSaves(loaded, profileId, out var current);
            if (!decision.Ok || current is null) return (decision, null);
            try
            {
                var roster = await manager.SharedWorldRosterAsync(profileId);
                decision = pairing.AuthorizeReceiveSaves(current, profileId, out var refreshed);
                if (decision.Ok && refreshed?.SharedWorldPublicKey is not null && roster is not null &&
                    roster.Members.SingleOrDefault(item => item.DeviceId == refreshed.Id) is
                    { Revoked: false, Grants: { Receive: true } } member &&
                    member.PublicKey == refreshed.SharedWorldPublicKey &&
                    (member.AccessExpiresUtc is null || member.AccessExpiresUtc > DateTimeOffset.UtcNow) &&
                    !pairing.SharedRosterDirty(profileId))
                    return (decision, refreshed);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException)
            { /* A damaged roster denies transfer. */ }
            return (new(false, "RosterDenied", "The current signed roster does not allow this transfer."), null);
        }

        async Task<CompanionStatus> PublicStatus(Guid deviceId)
        {
            var snapshot = await manager.CompanionSnapshotAsync();
            var address = ConnectionRoutes.GameAddress(snapshot.Settings);
            var own = pairing.Views().SingleOrDefault(view => view.Id == deviceId);
            var recentOperations = operations.RecentFor(deviceId);
            var recordedRuns = data.LoadRuns();
            var profiles = snapshot.Settings.Profiles.Where(profile =>
                own?.AssignedProfileIds.Contains(profile.Id) == true).Select(profile =>
            {
                var run = snapshot.Runs.Single(item => item.ProfileId == profile.Id);
                var permission = own?.ServerPermissions?.SingleOrDefault(item => item.ProfileId == profile.Id);
                var canStart = permission?.CanStart ?? own?.CanStart == true;
                var canStop = permission?.CanStop ?? own?.CanStop == true;
                var canExtendTimer = permission?.CanExtendTimer ?? own?.CanExtendTimer == true;
                var canViewLogs = permission?.CanViewLogs ?? own?.CanViewLogs == true;
                var canAddShutdownTime = canExtendTimer && snapshot.Settings.AutoShutdownEnabled &&
                    run.State == "Ready" && run.PlayerCountTrusted && run.OnlinePlayers is not null;
                using var permit = RemoteStopSafety.TryAcquire(snapshot, profile.Id, data, games, recordedRuns);
                return new PublicProfile(profile.Id, profile.Name,
                    run.State,
                    games.TryGet(profile.Kind, out var driver) ? driver.JoinAddress(profile, address) : null,
                    snapshot.Settings.RemoteControlsEnabled && canStop && permit.Allowed,
                    permit.Allowed ? null : permit.Reason,
                    profile.Kind == GameKinds.Custom ? profile.Custom?.GameName ?? "Custom game" : profile.Kind,
                    run.OnlinePlayers, run.MaxPlayers,
                    run.AutoShutdownAtUtc, run.AutoShutdownReason, canStart, canStop,
                    snapshot.Settings.RemoteControlsEnabled && canStart && canStop && permit.Allowed,
                    permit.Allowed ? null : permit.Reason,
                    recentOperations.FirstOrDefault(operation => operation.ProfileId == profile.Id),
                    profile.Maintenance?.Enabled == true,
                    string.IsNullOrWhiteSpace(profile.Maintenance?.Message) ? null : profile.Maintenance.Message,
                    canAddShutdownTime,
                    snapshot.Settings.FriendTimerExtensionMinutes,
                    Math.Max(0, snapshot.Settings.FriendTimerExtensionMaximumMinutes - run.FriendAddedMinutes),
                    canViewLogs);
            }).ToList();
            var protocol = CompanionProtocol.Describe(own?.ProtocolVersion);
            var assigned = own?.AssignedProfileIds.ToHashSet() ?? [];
            var activity = data.LoadActivity(100).Where(item =>
                    item.Visibility == ActivityVisibility.Device && item.DeviceId == deviceId ||
                    item.Visibility == ActivityVisibility.AssignedFriends && item.ProfileId is { } profileId && assigned.Contains(profileId))
                .Take(50).ToList();
            return new CompanionStatus(snapshot.Settings.RemoteControlsEnabled && protocol.Compatible,
                !protocol.Compatible ? protocol.CompatibilityMessage :
                snapshot.Settings.RemoteControlsEnabled ? null : "The Host has paused remote Start and Stop.",
                profiles, profiles.Any(profile => profile.CanStart), profiles.Any(profile => profile.CanStop),
                DateTimeOffset.UtcNow, protocol, identity.State(), ConnectionRoutes.Normalize(snapshot.Settings.ConnectionRoute),
                activity);
        }

        var companion = app.MapGroup("/api/companion");
        companion.MapPost("/servers/{profileId:guid}/shared-world/route-proof/{recordHash}",
            async (Guid profileId, string recordHash, SharedWorldRouteChallenge challenge) =>
        {
            if (certificate is null || recordHash.Length != 64 ||
                !recordHash.All(Uri.IsHexDigit) || challenge is null ||
                challenge.RecordHash != recordHash || challenge.ProfileId != profileId)
                return Results.NotFound();
            var proof = await manager.SignSuccessorRouteProofAsync(profileId, recordHash,
                challenge, HostIdentity.Fingerprint(certificate));
            return proof is null ? Results.NotFound() : Results.Json(proof);
        }).RequireRateLimiting("pairing");
        async Task<bool> CandidateOfferMatchesListener(Guid profileId, string proposalHash)
        {
            try
            {
                var offer = recoveryVotes.Armed(profileId);
                if (offer is null || WorldAuthorityTrust.ProposalHash(offer.Proposal) != proposalHash ||
                    certificate is null ||
                    HostIdentity.Fingerprint(certificate) != offer.CandidateTlsFingerprint)
                    return false;
                var current = (await manager.SnapshotAsync()).Settings;
                return current.CompanionListeningEnabled &&
                    current.CompanionEndpoint == offer.Proposal.CandidateAddress;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
            { return false; }
        }
        companion.MapGet("/servers/{profileId:guid}/shared-world/recovery/{proposalHash}/challenge/{deviceId:guid}",
            async (Guid profileId, string proposalHash, Guid deviceId) =>
        {
            if (proposalHash.Length != 64 || !proposalHash.All(Uri.IsHexDigit) ||
                !await CandidateOfferMatchesListener(profileId, proposalHash))
                return Results.NotFound();
            try
            {
                var challenge = recoveryVotes.Challenge(profileId, proposalHash, deviceId);
                return challenge is null ? Results.StatusCode(403) : Results.Json(challenge);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
            { return Results.Conflict(new { code = "RecoveryOfferUnavailable" }); }
        });
        companion.MapPost("/servers/{profileId:guid}/shared-world/recovery/{proposalHash}/offer",
            async (HttpContext context, Guid profileId, string proposalHash) =>
        {
            if (proposalHash.Length != 64 || !proposalHash.All(Uri.IsHexDigit) ||
                !await CandidateOfferMatchesListener(profileId, proposalHash))
                return Results.NotFound();
            var body = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, context.RequestAborted);
            if (body is null) return Results.BadRequest(new { code = "InvalidRecoveryRequest" });
            try
            {
                var request = JsonSerializer.Deserialize<WorldAuthorityOfferRequest>(body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (request is null || request.ProfileId != profileId || request.ProposalHash != proposalHash)
                    return Results.BadRequest(new { code = "InvalidRecoveryRequest" });
                var offer = recoveryVotes.ReadOffer(request);
                return offer is null ? Results.StatusCode(403) : Results.Json(offer);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException)
            { return Results.Conflict(new { code = "RecoveryOfferUnavailable" }); }
        });
        companion.MapPost("/servers/{profileId:guid}/shared-world/recovery/{proposalHash}/vote",
            async (HttpContext context, Guid profileId, string proposalHash) =>
        {
            if (proposalHash.Length != 64 || !proposalHash.All(Uri.IsHexDigit) ||
                !await CandidateOfferMatchesListener(profileId, proposalHash))
                return Results.NotFound();
            var body = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, context.RequestAborted);
            if (body is null) return Results.BadRequest(new { code = "InvalidRecoveryRequest" });
            try
            {
                var vote = JsonSerializer.Deserialize<WorldAuthorityVote>(body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (vote is null) return Results.BadRequest(new { code = "InvalidRecoveryRequest" });
                if (recoveryLossProbe is null || recoveryLossCurrent is null ||
                    !await recoveryLossProbe(profileId,
                        context.RequestAborted))
                    return Results.Json(new WorldAuthorityVoteResult(false, "HostLossNotConfirmed"),
                        statusCode: 403);
                var result = recoveryVotes.AcceptVote(profileId, proposalHash, vote,
                    () => recoveryLossCurrent(profileId));
                return Results.Json(result, statusCode: result.Ok ? 200 : 403);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException)
            { return Results.Conflict(new { code = "RecoveryVoteUnavailable" }); }
        });
        companion.MapPost("/servers/{profileId:guid}/shared-world/authority",
            async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!pairing.CanAccess(device!, profileId) ||
                device!.SharedWorldPublicKey is null)
                return Results.StatusCode(403);
            var body = await ReadBoundedAuthorityAsync(context.Request.Body,
                context.Request.ContentLength, context.RequestAborted);
            if (body is null) return Results.BadRequest(new { code = "InvalidAuthorityRecord" });
            try
            {
                var record = JsonSerializer.Deserialize<WorldAuthorityRecord>(body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (record is null || record.Proposal.ProfileId != profileId ||
                    record.Roster.Members.SingleOrDefault(member => member.DeviceId == device.Id) is
                        not { Revoked: false } member ||
                    member.PublicKey != device.SharedWorldPublicKey ||
                    !(member.Grants.Receive || member.Grants.RecoveryVoter || member.Grants.EligibleHost) ||
                    member.AccessExpiresUtc is { } expires && expires <= DateTimeOffset.UtcNow ||
                    !WorldAuthorityTrust.Verify(record))
                    return Results.StatusCode(403);
                if (!Reauthorize(device, out var currentDevice, out decision) ||
                    currentDevice is null || !pairing.CanAccess(currentDevice, profileId) ||
                    currentDevice.SharedWorldPublicKey != member.PublicKey)
                    return Results.StatusCode(403);
                await manager.ApplySharedWorldAuthorityAsync(record,
                    commit => pairing.CommitSharedWorldAuthority(device.Id, profileId,
                        member.PublicKey, commit));
                return Results.Json(new { code = "AuthorityRecorded", record.RecordHash });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                       CryptographicException or UnauthorizedAccessException)
            { return Results.Conflict(new { code = "AuthorityUnavailable" }); }
        });
        companion.MapPost("/pair", (PairingActivation request) =>
        {
            var credential = pairing.Activate(request);
            return credential is null ? Results.Json(new { code = "PairingRejected" }, statusCode: 401) : Results.Json(credential);
        }).RequireRateLimiting("pairing");
        companion.MapPost("/heartbeat", async (HttpContext context, HeartbeatRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (request.DeviceId != device!.Id) return Results.BadRequest(new { code = "DeviceMismatch" });
            decision = pairing.RecordHeartbeat(device, request);
            if (!decision.Ok)
                return Results.Json(decision, statusCode: decision.Code is "Revoked" or "ApprovalPending" or "AccessExpired"
                    ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict);
            var status = await PublicStatus(device.Id);
            if (!Reauthorize(device, out _, out decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            return Results.Json(status);
        });
        companion.MapGet("/status", async (HttpContext context) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var status = await PublicStatus(device!.Id);
            if (!Reauthorize(device, out _, out decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            return Results.Json(status);
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/enrollment", (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!pairing.CanAccess(device!, profileId)) return Results.StatusCode(403);
            return Results.Json(new SharedWorldEnrollmentChallenge(sharedEnrollment.Issue(device!.Id, profileId)));
        });
        companion.MapPost("/servers/{profileId:guid}/shared-world/enrollment",
            async (HttpContext context, Guid profileId, SharedWorldEnrollmentRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!pairing.CanAccess(device!, profileId) ||
                !sharedEnrollment.Consume(device!.Id, profileId, request.Nonce))
                return Results.StatusCode(403);
            decision = pairing.BindSharedWorldKey(device.Id, request);
            if (!decision.Ok) return Results.Json(decision, statusCode: 403);
            try
            {
                var roster = await manager.SharedWorldRosterAsync(profileId);
                if (decision.Code != "IdentityAlreadyEnrolled" || roster is null ||
                    roster.Members.All(item => item.DeviceId != device.Id || item.PublicKey != request.PublicKey))
                {
                    var published = await manager.PublishSharedWorldRosterAsync(profileId, pairing.SharedRosterMembers(profileId));
                    pairing.ConfirmSharedRosterPublished(profileId, published);
                }
                return Results.Json(decision);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
            { return Results.Conflict(new { code = "RosterUnavailable" }); }
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/roster", async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var auth = await AuthorizeShared(device!, profileId);
            if (!auth.Decision.Ok) return Results.Json(auth.Decision, statusCode: 403);
            var roster = await manager.SharedWorldRosterAsync(profileId);
            var recheck = await AuthorizeShared(auth.Current!, profileId);
            return recheck.Decision.Ok ? Results.Json(roster) : Results.Json(recheck.Decision, statusCode: 403);
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/authority", async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var auth = await AuthorizeShared(device!, profileId);
            if (!auth.Decision.Ok) return Results.Json(auth.Decision, statusCode: 403);
            var offset = int.TryParse(context.Request.Query["offset"], out var parsedOffset) &&
                parsedOffset >= 0 ? parsedOffset : 0;
            var records = await manager.SharedWorldAuthorityAsync(profileId);
            var recheck = await AuthorizeShared(auth.Current!, profileId);
            return !recheck.Decision.Ok ? Results.Json(recheck.Decision, statusCode: 403) :
                records is null ? Results.Conflict(new { code = "AuthorityUnavailable" }) :
                Results.Json(records.Skip(offset).Take(WorldAuthorityTrust.PageSize).ToArray());
        });
        companion.MapPost("/servers/{profileId:guid}/shared-world/receipts",
            async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName], out var protocol) ||
                !CompanionProtocol.IsCompatible(protocol))
                return Results.BadRequest(new { code = "InvalidSharedWorldRequest" });
            var body = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, context.RequestAborted);
            if (body is null) return Results.BadRequest(new { code = "InvalidSharedWorldRequest" });
            var receipt = SharedWorldReceiptTrust.Parse(body);
            if (receipt is null) return Results.BadRequest(new { code = "InvalidSharedWorldRequest" });
            var auth = await AuthorizeShared(device!, profileId);
            if (!auth.Decision.Ok || auth.Current is null)
                return Results.Json(auth.Decision, statusCode: 403);
            SharedWorldReceiptResult result;
            try { result = await manager.ConfirmSharedWorldReceiptAsync(profileId, device!.Id, receipt); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            { return Results.Conflict(new { code = "ReceiptUnavailable" }); }
            var recheck = await AuthorizeShared(auth.Current, profileId);
            if (!recheck.Decision.Ok) return Results.Json(recheck.Decision, statusCode: 403);
            return Results.Json(result, statusCode: result.Ok ? 200 : 409);
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/handoff/offer",
            async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName], out var protocol) ||
                !CompanionProtocol.IsCompatible(protocol))
                return Results.Conflict(new { code = "SharedWorldsUpdateRequired" });
            decision = pairing.AuthorizeReceiveSaves(device!, profileId, out var current);
            if (!decision.Ok || current?.SharedWorldPublicKey is null)
                return Results.Json(decision, statusCode: 403);
            WorldAuthorityRecord? offer;
            try { offer = await manager.ReadPlannedHandoffOfferAsync(profileId, current.Id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException)
            { return Results.Conflict(new { code = "HandoffUnavailable" }); }
            decision = pairing.AuthorizeReceiveSaves(current, profileId, out var refreshed);
            if (!decision.Ok || refreshed?.SharedWorldPublicKey is null)
                return Results.Json(decision, statusCode: 403);
            if (offer is null || offer.Proposal.CandidatePublicKey != refreshed.SharedWorldPublicKey ||
                offer.SuccessorReceipt?.DeviceId != refreshed.Id)
                return Results.NotFound(new { code = "HandoffUnavailable" });
            return Results.Json(offer);
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world", async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var auth = await AuthorizeShared(device!, profileId);
            decision = auth.Decision;
            var current = auth.Current;
            if (!decision.Ok || current is null)
                return Results.Json(decision, statusCode: StatusCodes.Status403Forbidden);
            if (!int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName], out var protocol) ||
                !CompanionProtocol.IsCompatible(protocol))
                return Results.Conflict(new { code = "SharedWorldsUpdateRequired" });
            var (status, _) = await manager.SharedWorldReadAsync(profileId);
            decision = (await AuthorizeShared(current, profileId)).Decision;
            if (!decision.Ok || !status.Enabled)
                return Results.Json(new { code = decision.Ok ? "SharingOff" : decision.Code },
                    statusCode: StatusCodes.Status403Forbidden);
            return status.Latest is null ? Results.NotFound(new { code = "NoPublishedSave" }) :
                Results.Json(status.Latest);
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/versions/{number:long}",
            async (HttpContext context, Guid profileId, long number) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var auth = await AuthorizeShared(device!, profileId);
            decision = auth.Decision;
            var current = auth.Current;
            if (!decision.Ok || current is null)
                return Results.Json(decision, statusCode: StatusCodes.Status403Forbidden);
            var (status, _) = await manager.SharedWorldReadAsync(profileId);
            if (!status.Enabled || status.Latest is null || number < 1 || number >= status.Latest.Number)
                return Results.NotFound(new { code = "SharedVersionUnavailable" });
            SharedWorldVersion prior;
            try
            {
                prior = await Task.Run(() => manager.ReadEarlierSharedVersion(status.Latest, number),
                context.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            { return Results.NotFound(new { code = "SharedVersionUnavailable" }); }
            var (latest, _) = await manager.SharedWorldReadAsync(profileId);
            decision = (await AuthorizeShared(current, profileId)).Decision;
            if (!decision.Ok || !latest.Enabled)
                return Results.Json(new { code = decision.Ok ? "SharingOff" : decision.Code },
                    statusCode: StatusCodes.Status403Forbidden);
            return latest.Latest?.VersionHash != status.Latest.VersionHash ?
                Results.NotFound(new { code = "SharedVersionUnavailable" }) : Results.Json(prior);
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/{versionHash}/files/{fileIndex:int}/chunks/{offset:long}",
            async (HttpContext context, Guid profileId, string versionHash, int fileIndex, long offset) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var auth = await AuthorizeShared(device!, profileId);
            decision = auth.Decision;
            var current = auth.Current;
            if (!decision.Ok || current is null)
                return Results.Json(decision, statusCode: StatusCodes.Status403Forbidden);
            if (versionHash.Length != 64 || !versionHash.All(Uri.IsHexDigit) ||
                !int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName], out var protocol) ||
                !CompanionProtocol.IsCompatible(protocol))
                return Results.BadRequest(new { code = "InvalidSharedWorldRequest" });
            var (status, _) = await manager.SharedWorldReadAsync(profileId);
            if (!status.Enabled || status.Latest?.VersionHash != versionHash)
                return Results.NotFound(new { code = "SharedVersionUnavailable" });
            byte[] chunk;
            try
            {
                chunk = await Task.Run(() => manager.ReadSharedChunk(status.Latest, fileIndex, offset),
                context.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { code = "SharedChunkUnavailable" }); }
            var (latest, _) = await manager.SharedWorldReadAsync(profileId);
            decision = (await AuthorizeShared(current, profileId)).Decision;
            if (!decision.Ok || !latest.Enabled)
                return Results.Json(new { code = decision.Ok ? "SharingOff" : decision.Code },
                    statusCode: StatusCodes.Status403Forbidden);
            if (latest.Latest?.VersionHash != versionHash)
                return Results.NotFound(new { code = "SharedVersionUnavailable" });
            return Results.Bytes(chunk, "application/octet-stream");
        });
        companion.MapGet("/servers/{profileId:guid}/logs", async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            decision = pairing.AuthorizeViewLogs(device!, profileId, out var currentDevice);
            if (!decision.Ok || currentDevice is null)
                return Results.Json(decision, statusCode: decision.Code == "PermissionDenied"
                    ? StatusCodes.Status403Forbidden : AuthenticationStatus(decision));
            if (!int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName].ToString(), out var clientProtocol) ||
                !CompanionProtocol.IsCompatible(clientProtocol))
                return Results.Json(new ServerLogResult(false, "ServerLogsUpdateRequired",
                    "Update the Friend app before viewing server logs from this Host.",
                    ServerLogSourceStates.Unsupported, null, [], null, false),
                    statusCode: StatusCodes.Status409Conflict);
            if (!ServerLogQueryParser.TryParse(context.Request.Query, out var query, out var error))
                return Results.BadRequest(error);
            var result = await serverLogs.ReadAsync(profileId, query, ServerLogAudience.Friend);
            decision = pairing.AuthorizeViewLogs(currentDevice, profileId, out _);
            if (!decision.Ok)
                return Results.Json(decision, statusCode: decision.Code == "PermissionDenied"
                    ? StatusCodes.Status403Forbidden : AuthenticationStatus(decision));
            return result.Code == "CustomRemoteLogsUnavailable"
                ? Results.Json(result, statusCode: StatusCodes.Status403Forbidden)
                : Results.Json(result);
        });
        companion.MapPost("/refresh", async (HttpContext context, RemoteActionRequest request) =>
        {
            if (isUpdating?.Invoke() == true)
                return Results.Json(new FriendActionResult(false, "UpdatePending",
                    "The Host is restarting for an update.", null), statusCode: 503);
            if (isShuttingDown?.Invoke() == true)
                return Results.Json(new FriendActionResult(false, "HostShuttingDown",
                    "The Host is closing and cannot refresh the player count.", null), statusCode: 503);
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                    statusCode: AuthenticationStatus(decision));
            if (request.DeviceId != device!.Id || request.ProfileId == Guid.Empty)
                return Results.BadRequest(new FriendActionResult(false, "InvalidRequest",
                    "Device or profile ID is invalid.", null));
            if (!int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName].ToString(), out var clientProtocol) ||
                !CompanionProtocol.IsCompatible(clientProtocol))
                return Results.Json(new FriendActionResult(false, "ProtocolIncompatible",
                    int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName].ToString(), out var reportedProtocol)
                        ? CompanionProtocol.CompatibilityMessage(reportedProtocol)
                        : "Update required: this Friend app did not identify a supported companion protocol.", null),
                    statusCode: StatusCodes.Status409Conflict);
            if (!Reauthorize(device, out var currentDevice, out decision) || currentDevice is null)
                return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                    statusCode: AuthenticationStatus(decision));
            if (!pairing.CanAccess(currentDevice, request.ProfileId))
            {
                if (!Reauthorize(currentDevice, out _, out decision))
                    return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                        statusCode: AuthenticationStatus(decision));
                return Results.Json(new FriendActionResult(false, "PermissionDenied",
                    "The Host has not assigned this server to this PC.", null), statusCode: 403);
            }

            var result = await manager.RefreshPlayerCountAsync(request.ProfileId);
            data.TryAudit($"remote-player-count-refresh {device.Id} {request.ProfileId} {result.Code} {DateTimeOffset.UtcNow:O}");
            var status = await PublicStatus(device.Id);
            if (!Reauthorize(currentDevice, out _, out decision))
                return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                    statusCode: AuthenticationStatus(decision));
            return Results.Json(new FriendActionResult(result.Ok, result.Code, result.Message, status));
        });
        companion.MapPost("/credential/renew", (HttpContext context, CredentialRenewalRequest request) =>
        {
            if (!AuthenticateDetailed(context, out var device, out var decision, out var usedPreviousCredential))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (request.DeviceId != device!.Id)
                return Results.BadRequest(new PairingDecision(false, "DeviceMismatch", "Device ID did not match the authenticated PC."));
            if (!Reauthorize(device, out var currentDevice, out decision) || currentDevice is null)
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var renewed = pairing.Renew(currentDevice, request, usedPreviousCredential);
            if (renewed is null && !Reauthorize(currentDevice, out _, out decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            return renewed is null
                ? Results.Json(new PairingDecision(false, "RenewalRejected", "The credential could not be renewed."), statusCode: 409)
                : Results.Json(renewed);
        });
        companion.MapPost("/credential/revoke", (HttpContext context, DeviceSelfRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (request.DeviceId != device!.Id)
                return Results.BadRequest(new PairingDecision(false, "DeviceMismatch", "Device ID did not match the authenticated PC."));
            if (!Reauthorize(device, out var currentDevice, out decision) || currentDevice is null)
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            return Results.Json(pairing.Revoke(currentDevice.Id));
        });
        companion.MapPost("/endpoint/recover", async (HttpContext context, EndpointRecoveryProofRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (request.DeviceId != device!.Id)
                return Results.BadRequest(new PairingDecision(false, "DeviceMismatch", "Device ID did not match the authenticated PC."));
            var snapshot = await manager.SnapshotAsync();
            var certificates = identity.State();
            if (certificates is null)
                return Results.Json(new PairingDecision(false, "IdentityUnavailable", "The Host identity is unavailable."), statusCode: 503);
            if (!Reauthorize(device, out _, out decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            return Results.Json(new EndpointRecoveryProof(certificates.HostId,
                snapshot.Settings.CompanionEndpoint, certificates,
                ConnectionRoutes.Normalize(snapshot.Settings.ConnectionRoute)));
        });

        async Task<RemoteOperationOutcome> ExecuteRemoteAction(Guid deviceId, Guid profileId, RemoteActionKind action,
            int clientProtocol)
        {
            if (!CompanionProtocol.IsCompatible(clientProtocol))
                return new(false, "ProtocolIncompatible", CompanionProtocol.CompatibilityMessage(clientProtocol));
            await modeGate.WaitAsync();
            try
            {
                if (isUpdating?.Invoke() == true)
                    return new(false, "UpdatePending", "The Host is restarting for an update.");
                if (isShuttingDown?.Invoke() == true)
                    return new(false, "HostShuttingDown", "The Host is closing and did not run this request.");
                var authorization = pairing.AuthorizeActiveDevice(deviceId, out var device);
                if (!authorization.Ok || device is null)
                    return new(false, authorization.Code, authorization.Message);
                var snapshot = await manager.SnapshotAsync();
                if (snapshot.Recovery?.LifecycleBlocked == true)
                    return new(false, "DataRecoveryRequired",
                        "Remote lifecycle actions are paused until the Host owner reviews recovered local data.");
                if (!snapshot.Settings.RemoteControlsEnabled)
                    return new(false, "RemoteControlsDisabled", "The Host has paused remote controls.");
                var profile = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == profileId);
                if (profile?.Maintenance?.Enabled == true)
                    return new(false, "MaintenanceMode", string.IsNullOrWhiteSpace(profile.Maintenance.Message)
                        ? "The Host has placed this server in maintenance mode. Remote actions are paused."
                        : "Maintenance: " + profile.Maintenance.Message);
                if (!RemoteActionPolicy.Allowed(pairing, device, profileId, action))
                {
                    authorization = pairing.AuthorizeActiveDevice(deviceId, out _);
                    if (!authorization.Ok)
                        return new(false, authorization.Code, authorization.Message);
                    return new(false, "PermissionDenied", "The Host has not granted this action for this server to this PC.");
                }

                ActionResult result;
                if (action is RemoteActionKind.Stop or RemoteActionKind.Restart)
                {
                    using var permit = RemoteStopSafety.TryAcquire(snapshot, profileId, data, games);
                    if (!permit.Allowed)
                        return new(false, permit.Code, permit.Reason);
                    result = action == RemoteActionKind.Stop
                        ? await manager.StopAsync(profileId, permit.StillSafe)
                        : await manager.RestartAsync(profileId, permit.StillSafe);
                }
                else
                    result = action switch
                    {
                        RemoteActionKind.Replace => await manager.ReplaceEmptyPortConflictsAndStartAsync(profileId),
                        RemoteActionKind.Extend => await manager.ExtendAutoShutdownForFriendAsync(profileId),
                        RemoteActionKind.Start => await manager.StartAsync(profileId),
                        _ => throw new ArgumentOutOfRangeException(nameof(action))
                    };
                data.TryAudit($"remote-{RemoteActionPolicy.Name(action)} {deviceId} {profileId} {result.Code} {DateTimeOffset.UtcNow:O}");
                return new(result.Ok, result.Code, result.Message, result.PortConflicts);
            }
            finally { modeGate.Release(); }
        }

        async Task<IResult> RemoteAction(HttpContext context, RemoteActionRequest request, RemoteActionKind action)
        {
            if (isUpdating?.Invoke() == true)
                return Results.Json(new FriendActionResult(false, "UpdatePending", "The Host is restarting for an update.", null),
                    statusCode: 503);
            if (isShuttingDown?.Invoke() == true)
                return Results.Json(new FriendActionResult(false, "HostShuttingDown", "The Host is closing and cannot accept another request.", null),
                    statusCode: 503);
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                    statusCode: AuthenticationStatus(decision));
            if (request.DeviceId != device!.Id || request.ProfileId == Guid.Empty)
                return Results.BadRequest(new FriendActionResult(false, "InvalidRequest", "Device or profile ID is invalid.", null));
            if (!int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName].ToString(), out var clientProtocol) ||
                !CompanionProtocol.IsCompatible(clientProtocol))
                return Results.Json(new FriendActionResult(false, "ProtocolIncompatible",
                    int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName].ToString(), out var reportedProtocol)
                        ? CompanionProtocol.CompatibilityMessage(reportedProtocol)
                        : "Update required: this Friend app did not identify a supported companion protocol.", null),
                    statusCode: StatusCodes.Status409Conflict);
            if (!Reauthorize(device, out var currentDevice, out decision) || currentDevice is null)
                return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                    statusCode: AuthenticationStatus(decision));
            device = currentDevice;
            if (!pairing.CanAccess(device, request.ProfileId))
            {
                if (!Reauthorize(device, out _, out decision))
                    return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                        statusCode: AuthenticationStatus(decision));
                return Results.Json(new FriendActionResult(false, "PermissionDenied", "The Host has not assigned this server to this PC.", null),
                    statusCode: 403);
            }
            if (!Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var key) || key == Guid.Empty)
                return Results.BadRequest(new FriendActionResult(false, "IdempotencyKeyRequired", "A request ID is required.", null));
            var actionName = RemoteActionPolicy.Name(action);
            var prior = operations.Lookup(device.Id, key, request.ProfileId, actionName);
            if (prior is not null)
            {
                if (!Reauthorize(device, out _, out decision))
                    return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                        statusCode: AuthenticationStatus(decision));
                if (!prior.Accepted)
                    return Results.Conflict(new FriendActionResult(false, prior.Code, prior.Message, null));
                var existing = prior.Operation!;
                return Results.Json(new FriendActionResult(existing.Ok ?? true,
                        RemoteOperationStates.Terminal(existing.State) ? existing.Code : "OperationAccepted",
                        existing.Message, null, existing.PortConflicts,
                        existing.Id, existing.State), statusCode: StatusCodes.Status202Accepted);
            }
            if (!manager.RemoteControlsEnabled)
                return Results.Json(new FriendActionResult(false, "RemoteControlsDisabled", "The Host has paused remote controls.",
                    null), statusCode: 403);
            if (manager.LifecycleBlocked)
                return Results.Json(new FriendActionResult(false, "DataRecoveryRequired",
                    "Remote lifecycle actions are paused until the Host owner reviews recovered local data.", null),
                    statusCode: StatusCodes.Status409Conflict);
            if (manager.RemoteMaintenanceBlocker(request.ProfileId) is { } maintenanceBlocker)
                return Results.Json(new FriendActionResult(false, "MaintenanceMode", maintenanceBlocker, null), statusCode: 403);
            if (!RemoteActionPolicy.Allowed(pairing, device, request.ProfileId, action))
            {
                if (!Reauthorize(device, out _, out decision))
                    return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                        statusCode: AuthenticationStatus(decision));
                return Results.Json(new FriendActionResult(false, "PermissionDenied",
                    "The Host has not granted this action for this server to this PC.", null), statusCode: 403);
            }
            var submission = operations.Submit(device.Id, key, request.ProfileId, actionName,
                () => ExecuteRemoteAction(device.Id, request.ProfileId, action, clientProtocol));
            if (!submission.Accepted)
                return Results.Conflict(new FriendActionResult(false, submission.Code, submission.Message, null));
            var accepted = submission.Operation!;
            return Results.Json(new FriendActionResult(true, "OperationAccepted",
                    submission.Existing ? "The Host already accepted this request." : "The Host accepted this request.",
                    null, accepted.PortConflicts, accepted.Id, accepted.State),
                statusCode: StatusCodes.Status202Accepted);
        }

        companion.MapGet("/operations/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!Reauthorize(device!, out var currentDevice, out decision) || currentDevice is null)
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var operation = operations.Find(currentDevice.Id, id);
            if (!Reauthorize(currentDevice, out _, out decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            return operation is null ? Results.NotFound(new { code = "UnknownOperation" }) : Results.Json(operation);
        });
        companion.MapPost("/start", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, RemoteActionKind.Start));
        companion.MapPost("/stop", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, RemoteActionKind.Stop));
        companion.MapPost("/restart", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, RemoteActionKind.Restart));
        companion.MapPost("/replace", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, RemoteActionKind.Replace));
        companion.MapPost("/extend", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, RemoteActionKind.Extend));
    }
}

internal sealed class AuthenticatedDeviceRateLimiter(int permitLimit, TimeSpan window, TimeProvider? clock = null)
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, Window> windows = [];
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private int calls;

    private sealed class Window(DateTimeOffset startedUtc)
    {
        public DateTimeOffset StartedUtc { get; set; } = startedUtc;
        public int Count { get; set; }
    }

    public bool TryAcquire(Guid deviceId)
    {
        var now = clock.GetUtcNow();
        lock (sync)
        {
            if (++calls % 256 == 0)
                foreach (var stale in windows.Where(item => now - item.Value.StartedUtc >= window + window)
                             .Select(item => item.Key).ToList())
                    windows.Remove(stale);
            if (!windows.TryGetValue(deviceId, out var current) || now - current.StartedUtc >= window)
            {
                current = new(now);
                windows[deviceId] = current;
            }
            if (current.Count >= permitLimit) return false;
            current.Count++;
            return true;
        }
    }
}

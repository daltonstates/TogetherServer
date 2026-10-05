using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Hosting;
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
    Func<Guid, bool>? recoveryLossCurrent = null,
    Action<IWebHostBuilder>? inMemoryTransport = null,
    ServerChat? serverChat = null)
{
    private readonly HostIdentity identity = new(data);
    private WebApplication? active;
    private X509Certificate2? certificate;
    private string? activeAddress;
    private readonly SemaphoreSlim listenerGate = new(1, 1);
    private readonly RemoteOperationCoordinator operations = new(data);
    private readonly AuthenticatedDeviceRateLimiter deviceRateLimiter = new(180, TimeSpan.FromMinutes(1));
    private readonly SharedWorldEnrollmentNonces sharedEnrollment = new();
    private readonly SharedWorldEnrollmentNonces successorEnrollment = new();
    private readonly SharedWorldVoteInbox recoveryVotes = new(data);
    private readonly ServerChat chat = serverChat ?? new ServerChat(data);
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
    internal WebApplication? InMemoryApp => inMemoryTransport is null ? null : active;
    public string ListenerState { get; private set; } = CompanionListenerStates.Off;
    public string? Warning { get; private set; }
    public IReadOnlyList<RemoteOperationView> RecentOperations() => operations.Recent();

    // Lets local takeover checks distinguish this app's HTTPS listener from an
    // unrelated process occupying the same control port.
    internal bool OwnsListener(string endpoint, int port, string fingerprint,
        string bindAddress)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        var key = bindAddress + ":" + port + "|" +
            uri.GetLeftPart(UriPartial.Authority) + "|" + fingerprint;
        return Active && ListenerState == CompanionListenerStates.Listening &&
            Volatile.Read(ref activeAddress) == key;
    }

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
            if (!pairing.HasInviteOrCredential() &&
                !recoveryVotes.HasArmedOffer(settings.CompanionEndpoint) &&
                !manager.HasSuccessorRouteCandidate(settings))
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
            if (inMemoryTransport is null)
                builder.WebHost.ConfigureKestrel(options =>
                    options.Listen(bind, settings.CompanionPort, listener => listener.UseHttps(nextCertificate)));
            else inMemoryTransport(builder.WebHost);
            nextApp = BuildApp(builder, settings, endpoint, inMemoryTransport is not null);
            await nextApp.StartAsync();
            active = nextApp;
            certificate = nextCertificate;
            activeAddress = configurationKey;
            Warning = null;
            ListenerState = CompanionListenerStates.Listening;
            DiagnosticOutput.WriteLine(inMemoryTransport is null
                ? $"Companion HTTPS listener: {address}"
                : "Companion routes started in memory for checks.");
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

    // The checks use the same companion routes and request guard with TestServer.
    // TestServer has no socket, so only its synthetic local-port feature is set here.
    internal WebApplication BuildInMemoryApp(WebApplicationBuilder builder, HostSettings settings,
        Uri endpoint) => BuildApp(builder, settings, endpoint, true);

    private WebApplication BuildApp(WebApplicationBuilder builder, HostSettings settings,
        Uri endpoint, bool inMemory)
    {
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
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (inMemory) context.Connection.LocalPort = settings.CompanionPort;
            var authorityIngest = context.Request.Path.Value?.EndsWith(
                "/shared-world/authority", StringComparison.Ordinal) == true ||
                context.Request.Path.Value?.EndsWith(
                    "/shared-world/resolution/owner-offer", StringComparison.Ordinal) == true;
            var chatSync = context.Request.Path.Value?.EndsWith(
                "/chat/sync", StringComparison.Ordinal) == true;
            var maximumBody = authorityIngest ? 2 * 1024 * 1024 :
                chatSync ? ServerChat.MaximumWireBytes : 4096;
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
        app.UseRateLimiter();
        MapRoutes(app);
        return app;
    }

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
            var decision = pairing.AuthorizeActiveDevice(loaded.Id, out var current);
            if (!decision.Ok || current is null || !pairing.CanAccess(current, profileId))
                return (new(false, "PermissionDenied", "This PC cannot access this shared world."), null);
            try
            {
                var roster = await manager.SharedWorldRosterAsync(profileId);
                decision = pairing.AuthorizeActiveDevice(current.Id, out var refreshed);
                if (decision.Ok && refreshed?.SharedWorldPublicKey is not null && roster is not null &&
                    pairing.CanAccess(refreshed, profileId) &&
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


        async Task<(PairingDecision Decision, PairedDevice? Current)> AuthorizeHistoryReview(
            PairedDevice loaded, Guid profileId)
        {
            var decision = pairing.AuthorizeSharedHistory(loaded, profileId, out var current);
            if (!decision.Ok || current is null) return (decision, null);
            try
            {
                var roster = await manager.SharedWorldReviewRosterAsync(profileId);
                decision = pairing.AuthorizeSharedHistory(current, profileId, out var refreshed);
                if (decision.Ok && refreshed?.SharedWorldPublicKey is not null && roster is not null &&
                    roster.Members.SingleOrDefault(item => item.DeviceId == refreshed.Id) is
                    { Revoked: false } member &&
                    member.PublicKey == refreshed.SharedWorldPublicKey &&
                    (member.Grants.Receive &&
                        refreshed.SharedWorldGrants?.GetValueOrDefault(profileId)?.Receive == true ||
                     member.Grants.RecoveryVoter &&
                        refreshed.SharedWorldGrants?.GetValueOrDefault(profileId)?.RecoveryVoter == true) &&
                    (member.AccessExpiresUtc is null || member.AccessExpiresUtc > DateTimeOffset.UtcNow))
                    return (decision, refreshed);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException)
            { /* Invalid review material denies access. */ }
            return (new(false, "RosterDenied", "The current signed roster does not allow history review."), null);
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
        async Task<(WorldAuthorityRecord? Head, SharedWorldRoster? Roster)> SuccessorEnrollmentState(
            Guid profileId, string recordHash)
        {
            if (certificate is null || recordHash.Length != 64 ||
                !recordHash.All(Uri.IsHexDigit) || pairing.SharedRosterDirty(profileId))
                return (null, null);
            var head = new WorldAuthorityStore(data).LocalAuthorizedHead(profileId);
            var roster = head?.RecordHash == recordHash &&
                head.Proposal.CandidateAddress == (await manager.SnapshotAsync()).Settings.CompanionEndpoint
                ? await manager.SharedWorldRosterAsync(profileId) : null;
            return roster is not null && roster.GroupId == head?.Proposal.GroupId &&
                roster.OwnerPublicKey == head.Roster.OwnerPublicKey &&
                SharedWorldRosterTrust.Verify(roster) ? (head, roster) : (null, null);
        }
        companion.MapGet("/servers/{profileId:guid}/shared-world/successor-enrollment/{recordHash}/{deviceId:guid}",
            async (Guid profileId, string recordHash, Guid deviceId) =>
        {
            try
            {
                var (head, roster) = await SuccessorEnrollmentState(profileId, recordHash);
                var member = roster?.Members.SingleOrDefault(item => item.DeviceId == deviceId);
                if (head is null || member is not { Revoked: false, Grants.Receive: true } ||
                    member.AccessExpiresUtc is { } end && end <= DateTimeOffset.UtcNow)
                    return Results.NotFound();
                return Results.Json(new SuccessorEnrollmentChallenge(
                    successorEnrollment.Issue(deviceId, profileId), head.RecordHash,
                    roster!.GroupId, roster.OwnerPublicKey, head.Proposal.CandidateAddress,
                    HostIdentity.Fingerprint(certificate!)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                       CryptographicException or UnauthorizedAccessException)
            { return Results.NotFound(); }
        }).RequireRateLimiting("pairing");
        companion.MapPost("/servers/{profileId:guid}/shared-world/successor-enrollment",
            async (Guid profileId, SuccessorEnrollmentRequest request) =>
        {
            try
            {
                if (request is null || !successorEnrollment.Consume(request.DeviceId, profileId,
                    request.Nonce)) return Results.StatusCode(403);
                var (head, roster) = await SuccessorEnrollmentState(profileId, request.RecordHash);
                if (head is null || roster is null || certificate is null) return Results.StatusCode(403);
                var credential = pairing.EnrollSuccessor(head, roster, request,
                    HostIdentity.Fingerprint(certificate));
                return credential is null ? Results.StatusCode(403) : Results.Json(credential);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                       CryptographicException or UnauthorizedAccessException)
            { return Results.StatusCode(403); }
        }).RequireRateLimiting("pairing");
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
        companion.MapPost("/servers/{profileId:guid}/shared-world/separate-route/{branchHash}/proof",
            async (HttpContext context, Guid profileId, string branchHash) =>
        {
            if (certificate is null || branchHash.Length != 64 ||
                !branchHash.All(Uri.IsHexDigit)) return Results.NotFound();
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, 2048, context.RequestAborted);
            if (bytes is null) return Results.NotFound();
            SeparateCopyRouteChallenge? challenge;
            try
            {
                challenge = JsonSerializer.Deserialize<SeparateCopyRouteChallenge>(bytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException) { return Results.NotFound(); }
            if (challenge is null) return Results.NotFound();
            var proof = await manager.SignSeparateRouteProofAsync(profileId, branchHash,
                challenge, HostIdentity.Fingerprint(certificate));
            return proof is null ? Results.NotFound() : Results.Json(proof);
        }).RequireRateLimiting("pairing");
        companion.MapPost("/servers/{profileId:guid}/shared-world/separate-route/{branchHash}/confirm",
            async (HttpContext context, Guid profileId, string branchHash) =>
        {
            if (certificate is null || branchHash.Length != 64 ||
                !branchHash.All(Uri.IsHexDigit)) return Results.NotFound();
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, 4096, context.RequestAborted);
            if (bytes is null) return Results.NotFound();
            SeparateCopyRouteConfirmation? confirmation;
            try
            {
                confirmation = JsonSerializer.Deserialize<SeparateCopyRouteConfirmation>(bytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException) { return Results.NotFound(); }
            return confirmation is not null && await manager.ConfirmSeparateRouteAsync(profileId,
                branchHash, confirmation, HostIdentity.Fingerprint(certificate)) ?
                Results.Ok(new { code = "SeparateRouteObserved" }) : Results.NotFound();
        }).RequireRateLimiting("pairing");
        companion.MapPost("/servers/{profileId:guid}/shared-world/route-confirm/{recordHash}",
            async (Guid profileId, string recordHash, SharedWorldRouteConfirmation confirmation) =>
        {
            if (certificate is null || recordHash.Length != 64 ||
                !recordHash.All(Uri.IsHexDigit) || confirmation is null)
                return Results.NotFound();
            return await manager.ConfirmSuccessorRouteAsync(profileId, recordHash,
                confirmation, HostIdentity.Fingerprint(certificate)) ?
                Results.Ok(new { code = "ControlRouteConfirmed" }) : Results.NotFound();
        }).RequireRateLimiting("pairing");
        async Task<bool> CandidateOfferMatchesListener(Guid profileId, string proposalHash)
        {
            try
            {
                var offer = recoveryVotes.OfferForTransport(profileId, proposalHash);
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
                if (recoveryVotes.Armed(profileId)?.Proposal.Schema != 3 &&
                    (recoveryLossProbe is null || recoveryLossCurrent is null ||
                     !await recoveryLossProbe!(profileId, context.RequestAborted)))
                    return Results.Json(new WorldAuthorityVoteResult(false, "HostLossNotConfirmed"),
                        statusCode: 403);
                var result = recoveryVotes.AcceptVote(profileId, proposalHash, vote,
                    () => recoveryLossCurrent!(profileId));
                return Results.Json(result, statusCode: result.Ok ? 200 : 403);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException)
            { return Results.Conflict(new { code = "RecoveryVoteUnavailable" }); }
        });
        companion.MapPost("/servers/{profileId:guid}/shared-world/resolution/{proposalHash}/owner-approval",
            async (HttpContext context, Guid profileId, string proposalHash) =>
        {
            if (proposalHash.Length != 64 || !proposalHash.All(Uri.IsHexDigit) ||
                !await CandidateOfferMatchesListener(profileId, proposalHash))
                return Results.NotFound();
            var body = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
                context.Request.ContentLength, context.RequestAborted);
            if (body is null) return Results.BadRequest(new { code = "InvalidOwnerApproval" });
            try
            {
                var approval = JsonSerializer.Deserialize<WorldAuthorityOwnerApproval>(body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (approval is null || approval.ProposalHash != proposalHash)
                    return Results.BadRequest(new { code = "InvalidOwnerApproval" });
                var result = recoveryVotes.AcceptOwnerApproval(profileId, approval);
                return Results.Json(result, statusCode: result.Ok ? 200 : 403);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                       CryptographicException)
            { return Results.Conflict(new { code = "OwnerApprovalUnavailable" }); }
        }).RequireRateLimiting("pairing");
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
                    !WorldAuthorityTrust.Verify(record, true))
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
        companion.MapPost("/servers/{profileId:guid}/shared-world/resolution/owner-offer",
            async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!pairing.CanAccess(device!, profileId) || device!.SharedWorldPublicKey is null)
                return Results.StatusCode(403);
            var body = await ReadBoundedAuthorityAsync(context.Request.Body,
                context.Request.ContentLength, context.RequestAborted);
            if (body is null) return Results.BadRequest(new { code = "InvalidResolutionOffer" });
            try
            {
                var offer = JsonSerializer.Deserialize<WorldAuthorityOffer>(body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (offer is null || offer.Proposal.ProfileId != profileId ||
                    offer.Proposal.ProposerDeviceId != device.Id ||
                    offer.Proposal.ProposerPublicKey != device.SharedWorldPublicKey ||
                    device.SharedWorldGrants?.GetValueOrDefault(profileId)?.EligibleHost != true ||
                    !SharedWorldElection.VerifyOffer(offer) ||
                    !Reauthorize(device, out var current, out decision) || current is null ||
                    !pairing.CanAccess(current, profileId) ||
                    current.SharedWorldGrants?.GetValueOrDefault(profileId)?.EligibleHost != true)
                    return Results.StatusCode(403);
                new WorldAuthorityStore(data).StageResolutionOffer(offer);
                return Results.Json(new
                {
                    code = "ResolutionOfferRecorded",
                    proposalHash = WorldAuthorityTrust.ProposalHash(offer.Proposal)
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                       CryptographicException)
            { return Results.Conflict(new { code = "ResolutionOfferUnavailable" }); }
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
        companion.MapPost("/servers/{profileId:guid}/rehearsal/exchange", async
            (HttpContext context, Guid profileId, RemoteRehearsalExchangeRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName], out var protocol) ||
                !CompanionProtocol.IsCompatible(protocol))
                return Results.Json(new { code = "RehearsalUpdateRequired" }, statusCode: 409);
            if (request.RequestId == Guid.Empty)
                return Results.BadRequest(new { code = "InvalidRehearsalRequest" });
            var auth = await AuthorizeShared(device!, profileId);
            if (!auth.Decision.Ok || auth.Current is null || !chat.IsMember(profileId, auth.Current.Id))
                return Results.Json(new { code = "RehearsalAccessDenied" }, statusCode: 403);
            try
            {
                var result = await manager.RehearsalExchangeAsync(profileId, auth.Current.Id,
                    request.RequestId, request.ChatMessageId, chat, identity.State()?.HostId ?? Guid.Empty,
                    Active && ListenerState == CompanionListenerStates.Listening);
                var recheck = await AuthorizeShared(auth.Current, profileId);
                if (!recheck.Decision.Ok || recheck.Current is null || !chat.IsMember(profileId, recheck.Current.Id))
                    return Results.Json(new { code = "RehearsalAccessDenied" }, statusCode: 403);
                return Results.Json(result);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                CryptographicException or JsonException or ArgumentException)
            { return Results.Json(new RemoteRehearsalExchange(false, "RehearsalUnavailable")); }
        });
        companion.MapPost("/servers/{profileId:guid}/chat/sync",
            (HttpContext context, Guid profileId, ChatSyncRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName], out var protocol) ||
                !CompanionProtocol.IsCompatible(protocol))
                return Results.Json(new { code = "ChatUpdateRequired" }, statusCode: 409);
            if (!pairing.CanAccess(device!, profileId) || !chat.IsMember(profileId, device!.Id))
                return Results.Json(new { code = "ChatAccessDenied" }, statusCode: 403);
            var hostId = identity.State()?.HostId ?? Guid.Empty;
            if (hostId == Guid.Empty) return Results.Json(new { code = "ChatUnavailable" }, statusCode: 503);
            if (request is null || request.Entries?.Count > ServerChat.MaximumEntries ||
                request.Drafts?.Count > ServerChat.MaximumDrafts ||
                request.Drafts?.Any(draft => draft is null || draft.Id == Guid.Empty ||
                    !ServerChat.ValidText(draft.Text)) == true ||
                request.Drafts?.Select(draft => draft.Id).Distinct().Count() != request.Drafts?.Count)
                return Results.BadRequest(new { code = "InvalidChatRequest" });
            try
            {
                var publicKey = chat.OwnerPublicKey();
                if (!chat.Merge(hostId, profileId, request.Entries, publicKey))
                    return Results.BadRequest(new { code = "InvalidChatCopy" });
                // Draft identity comes from the current authenticated device, not the body.
                if (!pairing.AuthorizeActiveDevice(device.Id, out var current).Ok ||
                    current is null || !pairing.CanAccess(current, profileId) ||
                    !chat.IsMember(profileId, current.Id))
                    return Results.Json(new { code = "ChatAccessDenied" }, statusCode: 403);
                foreach (var draft in request.Drafts ?? [])
                    chat.Post(hostId, profileId, current.Id, current.Name, draft.Text, draft.Id);
                if (!pairing.AuthorizeActiveDevice(device.Id, out current).Ok ||
                    current is null || !pairing.CanAccess(current, profileId) ||
                    !chat.IsMember(profileId, current.Id))
                    return Results.Json(new { code = "ChatAccessDenied" }, statusCode: 403);
                return Results.Json(new ChatSyncResponse(true, "ChatSynced", "Chat is up to date.",
                    hostId, profileId, publicKey, chat.Read(hostId, profileId)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                                       CryptographicException or UnauthorizedAccessException or ArgumentException)
            { return Results.Json(new { code = "ChatUnavailable" }, statusCode: 503); }
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/enrollment", (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!pairing.CanAccess(device!, profileId) || pairing.SharedRosterDirty(profileId))
                return Results.StatusCode(403);
            return Results.Json(new SharedWorldEnrollmentChallenge(sharedEnrollment.Issue(device!.Id, profileId)));
        });
        companion.MapPost("/servers/{profileId:guid}/shared-world/enrollment",
            async (HttpContext context, Guid profileId, SharedWorldEnrollmentRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!pairing.CanAccess(device!, profileId) || pairing.SharedRosterDirty(profileId) ||
                !sharedEnrollment.Consume(device!.Id, profileId, request.Nonce))
                return Results.StatusCode(403);
            if (!await manager.SharedRosterManagementAvailableAsync(profileId))
            {
                var inherited = await manager.SharedWorldRosterAsync(profileId);
                var current = pairing.AuthorizeReceiveSaves(device, profileId, out var enrolled);
                return current.Ok && !pairing.SharedRosterDirty(profileId) &&
                    SharedWorldRosterTrust.VerifyEnrollment(device.Id, request) &&
                    enrolled?.SharedWorldPublicKey == request.PublicKey &&
                    inherited?.Members.SingleOrDefault(item => item.DeviceId == device.Id) is
                    { Revoked: false, Grants.Receive: true } member &&
                    member.PublicKey == request.PublicKey &&
                    (member.AccessExpiresUtc is null || member.AccessExpiresUtc > DateTimeOffset.UtcNow)
                    ? Results.Json(new PairingDecision(true, "IdentityAlreadyEnrolled",
                        "This PC's inherited signing identity is active."))
                    : Results.StatusCode(403);
            }
            // Enrollment changes the signed membership. A successor can host
            // under the inherited roster but cannot re-sign it as the owner.
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
            var auth = await AuthorizeHistoryReview(device!, profileId);
            if (!auth.Decision.Ok) return Results.Json(auth.Decision, statusCode: 403);
            var roster = await manager.SharedWorldReviewRosterAsync(profileId);
            var recheck = await AuthorizeHistoryReview(auth.Current!, profileId);
            return recheck.Decision.Ok ? Results.Json(roster) : Results.Json(recheck.Decision, statusCode: 403);
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/roster/revisions", async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!pairing.CanAccess(device!, profileId) || device!.SharedWorldPublicKey is null)
                return Results.StatusCode(403);
            var rawOffset = context.Request.Query["offset"];
            long offset = 0;
            if (context.Request.Query.Count != (rawOffset.Count == 0 ? 0 : 1) ||
                rawOffset.Count > 1 || rawOffset.Count == 1 &&
                !long.TryParse(rawOffset[0], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out offset))
                return Results.BadRequest(new { code = "InvalidRosterOffset" });
            try
            {
                var revisions = await manager.SharedWorldRosterHistoryAsync(profileId, offset);
                if (!Reauthorize(device, out var current, out decision) ||
                    !pairing.CanAccess(current!, profileId))
                    return Results.StatusCode(403);
                return revisions is null ? Results.NotFound() : Results.Json(revisions);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
            { return Results.Conflict(new { code = "RosterUnavailable" }); }
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/roster/revisions/current", async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!pairing.CanAccess(device!, profileId) || device!.SharedWorldPublicKey is null)
                return Results.StatusCode(403);
            try
            {
                var roster = await manager.SharedWorldRosterAsync(profileId);
                if (!Reauthorize(device, out var current, out decision) ||
                    !pairing.CanAccess(current!, profileId)) return Results.StatusCode(403);
                return roster is null ? Results.NotFound() : Results.Json(roster);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
            { return Results.Conflict(new { code = "RosterUnavailable" }); }
        });
        companion.MapPost("/servers/{profileId:guid}/shared-world/roster/revisions", async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (!int.TryParse(context.Request.Headers[CompanionProtocol.HeaderName], out var protocol) ||
                !CompanionProtocol.IsCompatible(protocol) || !pairing.CanAccess(device!, profileId) ||
                device!.SharedWorldPublicKey is null || context.Request.ContentLength is > 262144)
                return Results.StatusCode(403);
            byte[] body;
            using (var buffer = new MemoryStream())
            {
                var part = new byte[8192];
                int read;
                while ((read = await context.Request.Body.ReadAsync(part, context.RequestAborted)) > 0)
                {
                    if (buffer.Length + read > 262144)
                        return Results.BadRequest(new { code = "InvalidRosterRevision" });
                    buffer.Write(part, 0, read);
                }
                if (buffer.Length == 0) return Results.BadRequest(new { code = "InvalidRosterRevision" });
                body = buffer.ToArray();
            }
            SharedWorldRoster? revision;
            try { revision = JsonSerializer.Deserialize<SharedWorldRoster>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { return Results.BadRequest(new { code = "InvalidRosterRevision" }); }
            if (revision is null || revision.ProfileId != profileId)
                return Results.BadRequest(new { code = "InvalidRosterRevision" });
            try
            {
                if (!Reauthorize(device, out var current, out decision) ||
                    !pairing.CanAccess(current!, profileId) || current!.SharedWorldPublicKey is null)
                    return Results.StatusCode(403);
                var published = await manager.PublishDelegatedRosterAsync(profileId,
                    current.Id, current.SharedWorldPublicKey, revision,
                    () => !pairing.SharedRosterDirty(profileId) &&
                        pairing.AuthorizeActiveDevice(current.Id, out var latest).Ok &&
                        latest?.SharedWorldPublicKey == current.SharedWorldPublicKey &&
                        pairing.CanAccess(latest, profileId));
                return Results.Json(published);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
            { return Results.Conflict(new { code = "RosterRevisionRejected" }); }
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/authority", async (HttpContext context, Guid profileId) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var auth = await AuthorizeHistoryReview(device!, profileId);
            if (!auth.Decision.Ok) return Results.Json(auth.Decision, statusCode: 403);
            var offset = int.TryParse(context.Request.Query["offset"], out var parsedOffset) &&
                parsedOffset >= 0 ? parsedOffset : 0;
            var records = await manager.SharedWorldReviewHistoryAsync(profileId, offset);
            var recheck = await AuthorizeHistoryReview(auth.Current!, profileId);
            return !recheck.Decision.Ok ? Results.Json(recheck.Decision, statusCode: 403) :
                records is null ? Results.Conflict(new { code = "AuthorityUnavailable" }) :
                Results.Json(records);
        });
        companion.MapGet("/servers/{profileId:guid}/shared-world/authority/{recordHash}/proof/{number:long}",
            async (HttpContext context, Guid profileId, string recordHash, long number) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var auth = await AuthorizeHistoryReview(device!, profileId);
            if (!auth.Decision.Ok) return Results.Json(auth.Decision, statusCode: 403);
            SharedWorldVersion? proof;
            try { proof = await manager.SharedWorldReviewProofAsync(profileId, recordHash, number); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException)
            { return Results.NotFound(new { code = "HistoryProofUnavailable" }); }
            var recheck = await AuthorizeHistoryReview(auth.Current!, profileId);
            if (!recheck.Decision.Ok) return Results.Json(recheck.Decision, statusCode: 403);
            if (proof is null) return Results.NotFound(new { code = "HistoryProofUnavailable" });
            var bytes = JsonSerializer.SerializeToUtf8Bytes(proof,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return bytes.Length > SharedWorldService.MaximumManifestBytes
                ? Results.NotFound(new { code = "HistoryProofUnavailable" }) :
                Results.Bytes(bytes, "application/json");
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
        companion.MapGet("/servers/{profileId:guid}/shared-world/versions/range/{start:long}/{count:int}",
            async (HttpContext context, Guid profileId, long start, int count) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            var auth = await AuthorizeShared(device!, profileId);
            decision = auth.Decision;
            var current = auth.Current;
            if (!decision.Ok || current is null)
                return Results.Json(decision, statusCode: StatusCodes.Status403Forbidden);
            var (status, _) = await manager.SharedWorldReadAsync(profileId);
            if (!status.Enabled || status.Latest is null ||
                count is < 1 or > WorldAuthorityTrust.ChainVersionsPerCheck ||
                start < 1 || start > status.Latest.Number - count)
                return Results.NotFound(new { code = "SharedVersionRangeUnavailable" });
            IReadOnlyList<SharedWorldVersion> versions;
            try
            {
                versions = await Task.Run(() => manager.ReadEarlierSharedVersionRange(
                    status.Latest, start, count), context.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            { return Results.NotFound(new { code = "SharedVersionRangeUnavailable" }); }
            var (latest, _) = await manager.SharedWorldReadAsync(profileId);
            decision = (await AuthorizeShared(current, profileId)).Decision;
            if (!decision.Ok || !latest.Enabled)
                return Results.Json(new { code = decision.Ok ? "SharingOff" : decision.Code },
                    statusCode: StatusCodes.Status403Forbidden);
            return latest.Latest?.VersionHash != status.Latest.VersionHash ?
                Results.NotFound(new { code = "SharedVersionRangeUnavailable" }) : Results.Json(versions);
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

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace TogetherServer;

// The public HTTPS listener is a second listener in the same Windows process.
// It starts only after the owner enables Friend connections and pairing/TLS
// material is ready; the local GUI remains bound to loopback.
public sealed class CompanionServer(LocalData data, HostManager manager, PairingService pairing,
    GameServerRegistry games, SemaphoreSlim modeGate, int localPort, Func<bool>? isUpdating = null)
{
    private readonly HostIdentity identity = new(data);
    private WebApplication? active;
    private X509Certificate2? certificate;
    private string? activeAddress;
    private readonly SemaphoreSlim listenerGate = new(1, 1);
    private readonly RemoteOperationCoordinator operations = new(data);

    public bool Active => active is not null && manager.CompanionListeningEnabled;
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
            return;
        }
        var address = settings.CompanionBindAddress + ":" + settings.CompanionPort;
        X509Certificate2? nextCertificate = null;
        WebApplication? nextApp = null;
        try
        {
            if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out var endpoint) ||
                endpoint.Port != settings.CompanionPort || !IPAddress.TryParse(settings.CompanionBindAddress, out var bind) ||
                !pairing.HasInviteOrCredential() || settings.CompanionPort < 1024 ||
                settings.CompanionPort == localPort)
                throw new InvalidOperationException("Pairing, Host address, port, or TLS identity is incomplete.");
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
                options.AddPolicy("device", context => RateLimitPartition.GetFixedWindowLimiter(
                    Guid.TryParse(context.Request.Headers["X-Device-Id"], out var deviceId)
                        ? deviceId.ToString("N") : "unauthenticated", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            });
            nextApp = builder.Build();
            nextApp.Use(async (context, next) =>
            {
                if (!manager.CompanionListeningEnabled || !context.Request.IsHttps ||
                    context.Connection.LocalPort != settings.CompanionPort ||
                    !context.Request.Path.StartsWithSegments("/api/companion") ||
                    !string.Equals(context.Request.Host.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) ||
                    context.Request.Host.Port != settings.CompanionPort || context.Request.ContentLength > 4096)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                    bodySize.MaxRequestBodySize = 4096;
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
            Console.WriteLine($"Companion HTTPS listener: {address}");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException or
            System.Security.Cryptography.CryptographicException)
        {
            if (nextApp is not null) await nextApp.DisposeAsync();
            nextCertificate?.Dispose();
            await StopCoreAsync();
            Warning = "Friend connections could not start: " + ex.Message;
            Console.Error.WriteLine(Warning);
        }
    }

    public async Task StopAsync()
    {
        await listenerGate.WaitAsync();
        try { await StopCoreAsync(); }
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
            decision.Code is "Revoked" or "ApprovalPending" ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;

        bool Authenticate(HttpContext context, out PairedDevice? device, out PairingDecision decision)
        {
            if (!Guid.TryParse(context.Request.Headers["X-Device-Id"], out var id))
            {
                device = null;
                decision = new(false, "Unauthorized", "Device ID was not accepted.");
                return false;
            }
            var auth = context.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : null;
            decision = pairing.Authenticate(id, token, out device);
            return decision.Ok;
        }

        async Task<CompanionStatus> PublicStatus(Guid deviceId)
        {
            var snapshot = await manager.SnapshotAsync();
            var address = ConnectionRoutes.GameAddress(snapshot.Settings);
            var own = pairing.Views().SingleOrDefault(view => view.Id == deviceId);
            var recentOperations = operations.RecentFor(deviceId);
            var profiles = snapshot.Settings.Profiles.Where(profile =>
                own?.AssignedProfileIds.Contains(profile.Id) == true).Select(profile =>
            {
                var run = snapshot.Runs.Single(item => item.ProfileId == profile.Id);
                var permission = own?.ServerPermissions?.SingleOrDefault(item => item.ProfileId == profile.Id);
                var canStart = permission?.CanStart ?? own?.CanStart == true;
                var canStop = permission?.CanStop ?? own?.CanStop == true;
                var canExtendTimer = permission?.CanExtendTimer ?? own?.CanExtendTimer == true;
                using var permit = RemoteStopSafety.TryAcquire(snapshot, profile.Id, data, games);
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
                    canExtendTimer,
                    snapshot.Settings.FriendTimerExtensionMinutes,
                    Math.Max(0, snapshot.Settings.FriendTimerExtensionMaximumMinutes - run.FriendAddedMinutes));
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
            return decision.Ok ? Results.Json(await PublicStatus(device.Id)) :
                Results.Json(decision, statusCode: decision.Code is "Revoked" or "ApprovalPending" ? 403 : 409);
        }).RequireRateLimiting("device");
        companion.MapGet("/status", async (HttpContext context) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            return Results.Json(await PublicStatus(device!.Id));
        }).RequireRateLimiting("device");
        companion.MapPost("/credential/renew", (HttpContext context, CredentialRenewalRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (request.DeviceId != device!.Id)
                return Results.BadRequest(new PairingDecision(false, "DeviceMismatch", "Device ID did not match the authenticated PC."));
            var renewed = pairing.Renew(device, request);
            return renewed is null
                ? Results.Json(new PairingDecision(false, "RenewalRejected", "The credential could not be renewed."), statusCode: 409)
                : Results.Json(renewed);
        }).RequireRateLimiting("device");
        companion.MapPost("/credential/revoke", (HttpContext context, DeviceSelfRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: AuthenticationStatus(decision));
            if (request.DeviceId != device!.Id)
                return Results.BadRequest(new PairingDecision(false, "DeviceMismatch", "Device ID did not match the authenticated PC."));
            return Results.Json(pairing.Revoke(device.Id));
        }).RequireRateLimiting("device");
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
            return Results.Json(new EndpointRecoveryProof(certificates.HostId,
                snapshot.Settings.CompanionEndpoint, certificates,
                ConnectionRoutes.Normalize(snapshot.Settings.ConnectionRoute)));
        }).RequireRateLimiting("device");

        async Task<RemoteOperationOutcome> ExecuteRemoteAction(Guid deviceId, Guid profileId, string action)
        {
            await modeGate.WaitAsync();
            try
            {
                if (isUpdating?.Invoke() == true)
                    return new(false, "UpdatePending", "The Host is restarting for an update.");
                if (!pairing.TryGetActiveDevice(deviceId, out var device) || device is null)
                    return new(false, "PermissionDenied", "This Friend PC is no longer authorized.");
                var snapshot = await manager.SnapshotAsync();
                if (!snapshot.Settings.RemoteControlsEnabled)
                    return new(false, "RemoteControlsDisabled", "The Host has paused remote controls.");
                var profile = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == profileId);
                if (profile?.Maintenance?.Enabled == true)
                    return new(false, "MaintenanceMode", string.IsNullOrWhiteSpace(profile.Maintenance.Message)
                        ? "The Host has placed this server in maintenance mode. Remote actions are paused."
                        : "Maintenance: " + profile.Maintenance.Message);
                if (!pairing.CanAccess(device, profileId) ||
                    action is "start" or "replace" && !pairing.CanStart(device, profileId) ||
                    action == "stop" && !pairing.CanStop(device, profileId) ||
                    action == "restart" && (!pairing.CanStart(device, profileId) || !pairing.CanStop(device, profileId)) ||
                    action == "extend" && !pairing.CanExtendTimer(device, profileId))
                    return new(false, "PermissionDenied", "The Host has not granted this action for this server to this PC.");

                ActionResult result;
                if (action is "stop" or "restart")
                {
                    using var permit = RemoteStopSafety.TryAcquire(snapshot, profileId, data, games);
                    if (!permit.Allowed)
                        return new(false, permit.Code, permit.Reason);
                    result = action == "stop"
                        ? await manager.StopAsync(profileId, permit.StillSafe)
                        : await manager.RestartAsync(profileId, permit.StillSafe);
                }
                else if (action == "replace")
                    result = await manager.ReplaceEmptyPortConflictsAndStartAsync(profileId);
                else if (action == "extend")
                    result = await manager.ExtendAutoShutdownForFriendAsync(profileId);
                else
                    result = await manager.StartAsync(profileId);
                data.Audit($"remote-{action} {deviceId} {profileId} {result.Code} {DateTimeOffset.UtcNow:O}");
                return new(result.Ok, result.Code, result.Message, result.PortConflicts);
            }
            finally { modeGate.Release(); }
        }

        async Task<IResult> RemoteAction(HttpContext context, RemoteActionRequest request, string action)
        {
            if (isUpdating?.Invoke() == true)
                return Results.Json(new FriendActionResult(false, "UpdatePending", "The Host is restarting for an update.", null),
                    statusCode: 503);
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                    statusCode: AuthenticationStatus(decision));
            if (request.DeviceId != device!.Id || request.ProfileId == Guid.Empty)
                return Results.BadRequest(new FriendActionResult(false, "InvalidRequest", "Device or profile ID is invalid.", null));
            if (!pairing.CanAccess(device, request.ProfileId))
                return Results.Json(new FriendActionResult(false, "PermissionDenied", "The Host has not assigned this server to this PC.", null),
                    statusCode: 403);
            if (!Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var key) || key == Guid.Empty)
                return Results.BadRequest(new FriendActionResult(false, "IdempotencyKeyRequired", "A request ID is required.", null));
            var prior = operations.Lookup(device.Id, key, request.ProfileId, action);
            if (prior is not null)
            {
                if (!prior.Accepted)
                    return Results.Conflict(new FriendActionResult(false, prior.Code, prior.Message, null));
                var existing = prior.Operation!;
                return Results.Json(new FriendActionResult(existing.Ok ?? true,
                        RemoteOperationStates.Terminal(existing.State) ? existing.Code : "OperationAccepted",
                        existing.Message, null, existing.PortConflicts,
                        existing.Id, existing.State), statusCode: StatusCodes.Status202Accepted);
            }
            var snapshot = await manager.SnapshotAsync();
            if (!snapshot.Settings.RemoteControlsEnabled)
                return Results.Json(new FriendActionResult(false, "RemoteControlsDisabled", "The Host has paused remote controls.",
                    await PublicStatus(device.Id)), statusCode: 403);
            var profile = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == request.ProfileId);
            if (profile?.Maintenance?.Enabled == true)
                return Results.Json(new FriendActionResult(false, "MaintenanceMode",
                    string.IsNullOrWhiteSpace(profile.Maintenance.Message)
                        ? "The Host has placed this server in maintenance mode. Remote actions are paused."
                        : "Maintenance: " + profile.Maintenance.Message,
                    await PublicStatus(device.Id)), statusCode: 403);
            if (action is "start" or "replace" && !pairing.CanStart(device, request.ProfileId) ||
                action == "stop" && !pairing.CanStop(device, request.ProfileId) ||
                action == "restart" && (!pairing.CanStart(device, request.ProfileId) || !pairing.CanStop(device, request.ProfileId)) ||
                action == "extend" && !pairing.CanExtendTimer(device, request.ProfileId))
                return Results.Json(new FriendActionResult(false, "PermissionDenied",
                    "The Host has not granted this action for this server to this PC.", await PublicStatus(device.Id)), statusCode: 403);
            var submission = operations.Submit(device.Id, key, request.ProfileId, action,
                () => ExecuteRemoteAction(device.Id, request.ProfileId, action));
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
            var operation = operations.Find(device!.Id, id);
            return operation is null ? Results.NotFound(new { code = "UnknownOperation" }) : Results.Json(operation);
        }).RequireRateLimiting("device");
        companion.MapPost("/start", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, "start"))
            .RequireRateLimiting("device");
        companion.MapPost("/stop", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, "stop"))
            .RequireRateLimiting("device");
        companion.MapPost("/restart", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, "restart"))
            .RequireRateLimiting("device");
        companion.MapPost("/replace", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, "replace"))
            .RequireRateLimiting("device");
        companion.MapPost("/extend", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, "extend"))
            .RequireRateLimiting("device");
    }
}

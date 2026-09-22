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
    private readonly Dictionary<(Guid DeviceId, Guid Key), (string Action, Guid ProfileId, FriendActionResult Result)> replay = new();

    public bool Active => active is not null && manager.CompanionListeningEnabled;
    public string? Warning { get; private set; }

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
        if (active is not null && activeAddress == address) return;
        await StopCoreAsync();
        X509Certificate2? nextCertificate = null;
        WebApplication? nextApp = null;
        try
        {
            if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out var endpoint) ||
                endpoint.Port != settings.CompanionPort || !IPAddress.TryParse(settings.CompanionBindAddress, out var bind) ||
                !pairing.HasInviteOrCredential() || settings.CompanionPort < 1024 ||
                settings.CompanionPort == localPort ||
                !string.Equals(data.LoadIdentityEndpoint(), settings.CompanionEndpoint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Pairing, Host address, port, or TLS identity is incomplete.");
            nextCertificate = identity.Load();
            if (nextCertificate is null || !nextCertificate.HasPrivateKey ||
                DateTimeOffset.UtcNow < nextCertificate.NotBefore.ToUniversalTime() ||
                DateTimeOffset.UtcNow > nextCertificate.NotAfter.ToUniversalTime())
                throw new InvalidOperationException("The Host TLS identity is unavailable or expired.");
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(bind, settings.CompanionPort, listener => listener.UseHttps(nextCertificate)));
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy("companion", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
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
            activeAddress = address;
            Warning = null;
            Console.WriteLine($"Companion HTTPS listener: {address}");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException or
            System.Security.Cryptography.CryptographicException)
        {
            if (nextApp is not null) await nextApp.DisposeAsync();
            nextCertificate?.Dispose();
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
            var address = snapshot.Settings.PublicGameIpCheckedUtc is { } checkedUtc &&
                DateTimeOffset.UtcNow - checkedUtc <= TimeSpan.FromHours(1) ? snapshot.Settings.PublicGameIp : null;
            var own = pairing.Views().SingleOrDefault(view => view.Id == deviceId);
            var profiles = snapshot.Settings.Profiles.Where(profile => own?.ProfileId == Guid.Empty ||
                profile.Id == own?.ProfileId).Select(profile =>
            {
                var run = snapshot.Runs.Single(item => item.ProfileId == profile.Id);
                using var permit = RemoteStopSafety.TryAcquire(snapshot, profile.Id, data, games);
                return new PublicProfile(profile.Id, profile.Name,
                    run.State,
                    games.TryGet(profile.Kind, out var driver) ? driver.JoinAddress(profile, address) : null,
                    snapshot.Settings.RemoteControlsEnabled && own?.CanStop == true && permit.Allowed,
                    permit.Allowed ? null : permit.Reason, profile.Kind, run.OnlinePlayers, run.MaxPlayers);
            }).ToList();
            return new CompanionStatus(snapshot.Settings.RemoteControlsEnabled,
                snapshot.Settings.RemoteControlsEnabled ? null : "The Host has paused remote Start and Stop.",
                profiles, snapshot.OwnerGameRunning, own?.GameRunning,
                own?.CanStart == true, own?.CanStop == true, DateTimeOffset.UtcNow);
        }

        var companion = app.MapGroup("/api/companion").RequireRateLimiting("companion");
        companion.MapPost("/pair", (PairingActivation request) =>
        {
            var credential = pairing.Activate(request);
            return credential is null ? Results.Json(new { code = "PairingRejected" }, statusCode: 401) : Results.Json(credential);
        });
        companion.MapPost("/heartbeat", async (HttpContext context, HeartbeatRequest request) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: decision.Code == "Revoked" ? 403 : 401);
            if (request.DeviceId != device!.Id) return Results.BadRequest(new { code = "DeviceMismatch" });
            decision = pairing.RecordHeartbeat(device, request);
            return decision.Ok ? Results.Json(await PublicStatus(device.Id)) :
                Results.Json(decision, statusCode: decision.Code == "Revoked" ? 403 : 409);
        });
        companion.MapGet("/status", async (HttpContext context) =>
        {
            if (!Authenticate(context, out var device, out var decision))
                return Results.Json(decision, statusCode: decision.Code == "Revoked" ? 403 : 401);
            return Results.Json(await PublicStatus(device!.Id));
        });

        async Task<IResult> RemoteAction(HttpContext context, RemoteActionRequest request, string action)
        {
            await modeGate.WaitAsync();
            try
            {
                if (isUpdating?.Invoke() == true)
                    return Results.Json(new FriendActionResult(false, "UpdatePending", "The Host is restarting for an update.", null),
                        statusCode: 503);
                if (!Authenticate(context, out var device, out var decision))
                    return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null),
                        statusCode: decision.Code == "Revoked" ? 403 : 401);
                if (request.DeviceId != device!.Id || request.ProfileId == Guid.Empty)
                    return Results.BadRequest(new FriendActionResult(false, "InvalidRequest", "Device or profile ID is invalid.", null));
                if (device.ProfileId != Guid.Empty && device.ProfileId != request.ProfileId)
                    return Results.Json(new FriendActionResult(false, "PermissionDenied", "This PC is paired with a different server.", null),
                        statusCode: 403);
                if (!Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var key) || key == Guid.Empty)
                    return Results.BadRequest(new FriendActionResult(false, "IdempotencyKeyRequired", "A request ID is required.", null));
                var snapshot = await manager.SnapshotAsync();
                if (!snapshot.Settings.RemoteControlsEnabled)
                    return Results.Json(new FriendActionResult(false, "RemoteControlsDisabled", "The Host has paused remote controls.",
                        await PublicStatus(device.Id)), statusCode: 403);
                if (replay.TryGetValue((device.Id, key), out var prior))
                    return prior.Action == action && prior.ProfileId == request.ProfileId
                        ? Results.Json(prior.Result) : Results.Conflict(new FriendActionResult(false, "IdempotencyConflict", "Request ID was reused for a different action.", null));
                FriendActionResult result;
                if (action == "start" && !device.CanStart || action == "stop" && !device.CanStop)
                    result = new(false, "PermissionDenied", "The Host has not granted this action to this PC.", await PublicStatus(device.Id));
                else if (action == "stop")
                {
                    using var permit = RemoteStopSafety.TryAcquire(snapshot, request.ProfileId, data, games);
                    if (!permit.Allowed)
                        result = new(false, permit.Code, permit.Reason, await PublicStatus(device.Id));
                    else
                    {
                        var operation = await manager.StopAsync(request.ProfileId, permit.StillSafe);
                        result = new(operation.Ok, operation.Code, operation.Message, await PublicStatus(device.Id));
                        data.Audit($"remote-stop {device.Id} {request.ProfileId} {operation.Code} {DateTimeOffset.UtcNow:O}");
                    }
                }
                else
                {
                    var operation = await manager.StartAsync(request.ProfileId);
                    result = new(operation.Ok, operation.Code, operation.Message, await PublicStatus(device.Id));
                    data.Audit($"remote-start {device.Id} {request.ProfileId} {operation.Code} {DateTimeOffset.UtcNow:O}");
                }
                if (replay.Count >= 2048) replay.Clear();
                replay[(device.Id, key)] = (action, request.ProfileId, result);
                return Results.Json(result, statusCode: result.Code is "RemoteControlsDisabled" or "PermissionDenied" ? 403 : 200);
            }
            finally { modeGate.Release(); }
        }
        companion.MapPost("/start", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, "start"));
        companion.MapPost("/stop", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, "stop"));
    }
}

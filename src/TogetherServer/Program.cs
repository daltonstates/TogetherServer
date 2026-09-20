using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using TogetherServer;

var friendMode = args.Contains("--friend", StringComparer.OrdinalIgnoreCase);
if (friendMode && args.Contains("--host", StringComparer.OrdinalIgnoreCase))
    throw new ArgumentException("Choose either --host or --friend.");
var portIndex = Array.IndexOf(args, "--port");
var port = portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var parsedPort)
    ? parsedPort : 5127;
if (port is < 1024 or > 65535) throw new ArgumentException("Local GUI port must be between 1024 and 65535.");

var root = Environment.GetEnvironmentVariable("TOGETHERSERVER_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TogetherServer");
using var data = new LocalData(root);
var manager = new HostManager(data);
var pairing = new PairingService(data);
var identity = new HostIdentity(data);
var friend = new FriendService(data);
var modeGate = new SemaphoreSlim(1, 1);
var startupSettings = data.LoadSettings();
var companionUri = HostIdentity.TryEndpoint(startupSettings.CompanionEndpoint, out var configuredUri) ? configuredUri : null;
X509Certificate2? companionCertificate = null;
string? companionWarning = null;
if (!friendMode && startupSettings.CompanionListeningEnabled)
{
    try
    {
        companionCertificate = identity.Load();
        if (companionUri is null || companionUri.Port != startupSettings.CompanionPort ||
            startupSettings.CompanionPort == port || !IPAddress.TryParse(startupSettings.CompanionBindAddress, out _) ||
            !pairing.HasInviteOrCredential() || companionCertificate is null || !companionCertificate.HasPrivateKey ||
            !string.Equals(data.LoadIdentityEndpoint(), startupSettings.CompanionEndpoint, StringComparison.OrdinalIgnoreCase) ||
            DateTimeOffset.UtcNow < companionCertificate.NotBefore.ToUniversalTime() ||
            DateTimeOffset.UtcNow > companionCertificate.NotAfter.ToUniversalTime())
        {
            companionWarning = "Companion listener was not started: pairing, endpoint, port, or TLS identity is invalid.";
            companionCertificate = null;
        }
    }
    catch (Exception ex)
    {
        companionWarning = "Companion listener was not started: " + ex.Message;
        companionCertificate = null;
    }
}
var companionActive = companionCertificate is not null;
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
    if (companionCertificate is not null)
        options.Listen(IPAddress.Parse(startupSettings.CompanionBindAddress), startupSettings.CompanionPort,
            listener => listener.UseHttps(companionCertificate));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("companion", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    var localGui = context.Connection.LocalPort == port;
    if (localGui && (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
        !string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Host.Port != port || context.Request.Path.StartsWithSegments("/api/companion")))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (!localGui && (!companionActive || !manager.CompanionListeningEnabled ||
        context.Connection.LocalPort != startupSettings.CompanionPort ||
        !context.Request.IsHttps || !context.Request.Path.StartsWithSegments("/api/companion") ||
        !string.Equals(context.Request.Host.Host, companionUri!.Host, StringComparison.OrdinalIgnoreCase) ||
        context.Request.Host.Port != startupSettings.CompanionPort || context.Request.ContentLength > 4096))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (!localGui && context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        bodySize.MaxRequestBodySize = 4096;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";
    context.Response.Headers["Cache-Control"] = "no-store";
    if (localGui && context.Request.Path.StartsWithSegments("/api/local") &&
        context.Request.Method != "GET" &&
        (!string.Equals(context.Request.Headers.Origin, $"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
         context.Request.Headers["X-TogetherServer-Local"] != "1"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.UseRateLimiter();

app.MapGet("/api/local/snapshot", async () => friendMode
    ? Results.Json(friend.View())
    : Results.Json(await manager.SnapshotAsync()));
async Task<IResult> HostOnly(Func<Task<ActionResult>> action)
{
    await modeGate.WaitAsync();
    try { return friendMode ? Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." }) : Results.Json(await action()); }
    finally { modeGate.Release(); }
}
app.MapPut("/api/local/settings", (HostSettings settings) => HostOnly(() => manager.UpdateSettingsAsync(settings)));
app.MapPost("/api/local/profiles/{id:guid}/start", (Guid id) => HostOnly(() => manager.StartAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/stop", (Guid id) => HostOnly(() => manager.StopAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/health", (Guid id) => HostOnly(() => manager.HealthAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/forget", (Guid id) => HostOnly(() => manager.ForgetAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/password", (Guid id, ValheimPasswordRequest request) =>
    HostOnly(() => manager.SetValheimPasswordAsync(id, request.Password)));
app.MapPost("/api/local/mode/{mode}", async (string mode) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (!mode.Equals("host", StringComparison.OrdinalIgnoreCase) && !mode.Equals("friend", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { ok = false, code = "InvalidMode", message = "Choose Host or Friend." });
        if (mode.Equals("friend", StringComparison.OrdinalIgnoreCase) &&
            (await manager.SnapshotAsync()).Runs.Any(run => run.State != "Offline"))
            return Results.Conflict(new { ok = false, code = "ManagedRunPresent", message = "Stop or resolve every managed run before switching to Friend mode." });
        if (mode.Equals("friend", StringComparison.OrdinalIgnoreCase) && companionActive)
            return Results.Conflict(new { ok = false, code = "CompanionListenerActive", message = "Disable the companion listener and restart before switching to Friend mode." });
        friendMode = mode.Equals("friend", StringComparison.OrdinalIgnoreCase);
        return Results.Json(new { ok = true, code = "ModeChanged", message = $"Switched to {(friendMode ? "Friend" : "Host")} mode." });
    }
    finally { modeGate.Release(); }
});

app.MapGet("/api/local/companion", async () =>
{
    string? fingerprint = null;
    try { using var certificate = identity.Load(); if (certificate is not null) fingerprint = HostIdentity.Fingerprint(certificate); }
    catch { /* A bad protected identity is reported by listenerWarning and never exposed publicly. */ }
    return Results.Json(new { listenerActive = companionActive && manager.CompanionListeningEnabled,
        listenerWarning = companionActive && !manager.CompanionListeningEnabled
            ? "Companion requests are disabled now. Restart to release the HTTPS port." : companionWarning,
        endpoint = (await manager.SnapshotAsync()).Settings.CompanionEndpoint, fingerprint, devices = pairing.Views() });
});
app.MapPost("/api/local/devices/invite", async (InviteRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
        var settings = (await manager.SnapshotAsync()).Settings;
        if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out var endpoint) || endpoint.Port != settings.CompanionPort)
            return Results.BadRequest(new { ok = false, code = "InvalidEndpoint", message = "Save an HTTPS IP endpoint and matching companion port first." });
        try
        {
            using var certificate = identity.Ensure(settings.CompanionEndpoint);
            var invite = pairing.Issue(request.Name, request.CanStart, request.CanStop,
                settings.CompanionEndpoint, HostIdentity.Fingerprint(certificate), request.RotateDeviceId);
            return Results.Json(new { ok = true, code = "InviteCreated", message = "Copy this one-time invite through a trusted channel. It expires in 30 minutes.",
                invitation = JsonSerializer.Serialize(invite, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return Results.BadRequest(new { ok = false, code = "InviteFailed", message = ex.Message });
        }
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/devices/{id:guid}/revoke", async (Guid id) =>
{
    await modeGate.WaitAsync();
    try { return friendMode ? Results.Conflict(new { ok = false, code = "FriendMode" }) : Results.Json(pairing.Revoke(id)); }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/friend/pair", async (FriendPairRequest request) =>
    friendMode ? Results.Json(await friend.PairAsync(request.Invitation, request.ClientExecutablePath))
        : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/client-path", async (ClientPathRequest request) =>
    friendMode ? Results.Json(await friend.SetClientPathAsync(request.Path))
        : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/poll", async () =>
    friendMode ? Results.Json(await friend.PollAsync()) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/{action}", async (Guid id, string action) =>
    friendMode ? Results.Json(await friend.RequestAsync(id, action)) : Results.Conflict(new { ok = false, code = "HostMode" }));

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
    var profiles = snapshot.Settings.Profiles.Select(profile => new PublicProfile(profile.Id, profile.Name,
        snapshot.Runs.Single(run => run.ProfileId == profile.Id).State)).ToList();
    var own = pairing.Views().SingleOrDefault(view => view.Id == deviceId);
    return new CompanionStatus(snapshot.Settings.RemoteControlsEnabled,
        snapshot.Settings.RemoteControlsEnabled ? null : "The Host has turned remote Start and Stop off.",
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
    return decision.Ok ? Results.Json(await PublicStatus(device.Id)) : Results.Json(decision, statusCode: decision.Code == "Revoked" ? 403 : 409);
});
companion.MapGet("/status", async (HttpContext context) =>
{
    if (!Authenticate(context, out var device, out var decision))
        return Results.Json(decision, statusCode: decision.Code == "Revoked" ? 403 : 401);
    return Results.Json(await PublicStatus(device!.Id));
});
var replay = new Dictionary<(Guid DeviceId, Guid Key), (string Action, Guid ProfileId, FriendActionResult Result)>();
async Task<IResult> RemoteAction(HttpContext context, RemoteActionRequest request, string action)
{
    await modeGate.WaitAsync();
    try
    {
        if (!Authenticate(context, out var device, out var decision))
            return Results.Json(new FriendActionResult(false, decision.Code, decision.Message, null), statusCode: decision.Code == "Revoked" ? 403 : 401);
        if (request.DeviceId != device!.Id || request.ProfileId == Guid.Empty)
            return Results.BadRequest(new FriendActionResult(false, "InvalidRequest", "Device or profile ID is invalid.", null));
        if (!Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var key) || key == Guid.Empty)
            return Results.BadRequest(new FriendActionResult(false, "IdempotencyKeyRequired", "A request ID is required.", null));
        var snapshot = await manager.SnapshotAsync();
        if (!snapshot.Settings.RemoteControlsEnabled)
            return Results.Json(new FriendActionResult(false, "RemoteControlsDisabled", "The Host has turned remote controls off.",
                await PublicStatus(device.Id)), statusCode: 403);
        if (replay.TryGetValue((device.Id, key), out var prior))
            return prior.Action == action && prior.ProfileId == request.ProfileId
                ? Results.Json(prior.Result) : Results.Conflict(new FriendActionResult(false, "IdempotencyConflict", "Request ID was reused for a different action.", null));
        FriendActionResult result;
        if (action == "start" && !device.CanStart || action == "stop" && !device.CanStop)
            result = new(false, "PermissionDenied", "The Host has not granted this action to this device.", await PublicStatus(device.Id));
        else if (action == "stop" && (!snapshot.Settings.PermittedPlayersVerified ||
            ClientMonitor.IsRunning(snapshot.Settings.OwnerClientExecutablePath) != false || !pairing.AllKnownNotPlaying()))
            result = new(false, "PlayerStateUnknown", "A player may be active or companion coverage is unverified. Ask the Host for a local override.", await PublicStatus(device.Id));
        else
        {
            var operation = action == "start" ? await manager.StartAsync(request.ProfileId) : await manager.StopAsync(request.ProfileId);
            result = new(operation.Ok, operation.Code, operation.Ok ? operation.Message : "The Host rejected the fixed action: " + operation.Code,
                await PublicStatus(device.Id));
            data.Audit($"remote-{action} {device.Id} {request.ProfileId} {operation.Code} {DateTimeOffset.UtcNow:O}");
        }
        if (replay.Count >= 2048) replay.Clear();
        replay[(device.Id, key)] = (action, request.ProfileId, result);
        return Results.Json(result, statusCode: result.Code is "RemoteControlsDisabled" or "PermissionDenied" ? 403 : 200);
    }
    finally { modeGate.Release(); }
}
companion.MapPost("/start", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, "start"));
companion.MapPost("/stop", (HttpContext context, RemoteActionRequest request) => RemoteAction(context, request, "stop"));

var assembly = Assembly.GetExecutingAssembly();
var assets = assembly.GetManifestResourceNames()
    .Where(name => name.StartsWith("Ui/", StringComparison.Ordinal))
    .ToDictionary(name => name[3..].Replace('\\', '/'), name => name, StringComparer.OrdinalIgnoreCase);
app.MapGet("/{**path}", async (HttpContext context, string? path) =>
{
    var requested = string.IsNullOrWhiteSpace(path) ? "index.html" : path.TrimStart('/');
    if (requested.Contains("..", StringComparison.Ordinal) || !assets.TryGetValue(requested, out var resource))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("UI asset not found. Build ui/ before publishing.");
        return;
    }
    context.Response.ContentType = Path.GetExtension(requested).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };
    await using var stream = assembly.GetManifestResourceStream(resource)!;
    await stream.CopyToAsync(context.Response.Body);
});

Console.WriteLine($"TogetherServer {(friendMode ? "Friend" : "Host")} local GUI: http://127.0.0.1:{port}/");
if (companionActive) Console.WriteLine($"Companion HTTPS listener: {startupSettings.CompanionBindAddress}:{startupSettings.CompanionPort}");
if (companionWarning is not null) Console.Error.WriteLine(companionWarning);
using var pollStop = new CancellationTokenSource();
var pollTask = Task.Run(async () =>
{
    while (!pollStop.IsCancellationRequested)
    {
        if (friendMode)
        {
            try { await friend.PollAsync(); }
            catch (Exception ex) { Console.Error.WriteLine("Friend poll failed: " + ex.GetType().Name); }
        }
        else
        {
            try { await manager.SnapshotAsync(); }
            catch (Exception ex) { Console.Error.WriteLine("Host client check failed: " + ex.GetType().Name); }
        }
        try { await Task.Delay(TimeSpan.FromSeconds(15), pollStop.Token); }
        catch (OperationCanceledException) { break; }
    }
});
try { await app.RunAsync(); }
finally { pollStop.Cancel(); await pollTask; }

using System.Net;
using System.Reflection;
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
var modeGate = new SemaphoreSlim(1, 1);
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
        !string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Host.Port != port)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";
    context.Response.Headers["Cache-Control"] = "no-store";
    if (context.Request.Path.StartsWithSegments("/api/local") &&
        context.Request.Method != "GET" &&
        (!string.Equals(context.Request.Headers.Origin, $"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
         context.Request.Headers["X-TogetherServer-Local"] != "1"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.MapGet("/api/local/snapshot", async () => friendMode
    ? Results.Json(new { mode = "Friend", state = "Not paired", detail = "Friend connection is not implemented in this slice." })
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
        friendMode = mode.Equals("friend", StringComparison.OrdinalIgnoreCase);
        return Results.Json(new { ok = true, code = "ModeChanged", message = $"Switched to {(friendMode ? "Friend" : "Host")} mode." });
    }
    finally { modeGate.Release(); }
});

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
await app.RunAsync();

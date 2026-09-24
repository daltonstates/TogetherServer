using System.Net;
using System.Reflection;
using System.Text.Json;
using TogetherServer;

if (args.Length > 0 && args[0] == "--apply-update")
{
    Environment.ExitCode = await UpdateInstaller.RunAsync(args);
    return;
}

var requestedFriend = args.Contains("--friend", StringComparer.OrdinalIgnoreCase);
var requestedHost = args.Contains("--host", StringComparer.OrdinalIgnoreCase);
var startupLaunch = args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
if (requestedFriend && requestedHost)
    throw new ArgumentException("Choose either --host or --friend.");
var openWindow = args.Length == 0 || args.Contains("--desktop", StringComparer.OrdinalIgnoreCase) || startupLaunch;
DesktopLaunch.EnsureConsoleForGameStop(openWindow);
var portIndex = Array.IndexOf(args, "--port");
var port = portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var parsedPort)
    ? parsedPort : 5127;
if (port is < 1024 or > 65535) throw new ArgumentException("Local GUI port must be between 1024 and 65535.");

var root = Environment.GetEnvironmentVariable("TOGETHERSERVER_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TogetherServer");
LocalData data;
try { data = new LocalData(root); }
catch (IOException ex) when (openWindow)
{
    if (await DesktopLaunch.TryShowExistingAsync(port, showWindow: !startupLaunch)) return;
    DesktopLaunch.ShowError("TogetherServer could not open its local data. Another instance may be starting.\n\n" + ex.Message);
    return;
}
using var ownedData = data;
var desktopPreferences = data.LoadDesktopPreferences();
var startupRegistration = new WindowsStartup(Environment.ProcessPath ?? "");
var friendMode = requestedFriend || (!requestedHost && data.LoadPreferredMode() == "Friend");
var games = new GameServerRegistry(data);
var pairing = new PairingService(data);
pairing.ReconcileProfiles(data.LoadSettings().Profiles.Select(profile => profile.Id));
var manager = new HostManager(data, games);
var identity = new HostIdentity(data);
var friend = new FriendService(data);
using var updateClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
var updater = new AppUpdater(updateClient, root, Environment.ProcessPath ?? "",
    Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0));
using var publicIpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
var publicIpLookup = new PublicIpLookup(publicIpClient);
using var externalProbeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var externalPortProbe = new ExternalPortProbe(externalProbeClient);
using var minecraftClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var minecraftInstaller = new MinecraftInstaller(minecraftClient, data);
var modeGate = new SemaphoreSlim(1, 1);
var updatePending = false;
var companionServer = new CompanionServer(data, manager, pairing, games, modeGate, port, () => updatePending);
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
var app = builder.Build();
var desktop = openWindow ? new DesktopWindow(new Uri($"http://127.0.0.1:{port}/"), root,
    app.Lifetime.StopApplication, desktopPreferences.CloseToTray, startupLaunch) : null;
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

app.MapGet("/api/local/snapshot", async () => friendMode
    ? Results.Json(friend.View())
    : Results.Json(await manager.SnapshotAsync()));
app.MapGet("/api/local/window", () => Results.Json(new
{
    available = desktop is not null,
    visible = desktop?.Visible ?? false,
    rendered = desktop?.Rendered ?? false,
    fileDialogOpen = desktop?.FileDialogOpen ?? false,
    customChrome = desktop?.CustomChrome ?? false
}));
object DesktopPreferenceView()
{
    bool startupAvailable;
    bool launchAtLogin;
    try
    {
        launchAtLogin = startupRegistration.IsEnabled();
        startupAvailable = true;
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
    {
        launchAtLogin = false;
        startupAvailable = false;
    }
    return new { available = desktop is not null, launchAtLogin,
        closeToTray = desktopPreferences.CloseToTray, startupAvailable };
}
app.MapGet("/api/local/desktop/preferences", () => Results.Json(DesktopPreferenceView()));
app.MapPut("/api/local/desktop/preferences", (DesktopPreferenceChange change) =>
{
    if (desktop is null) return Results.Json(new { ok = false, code = "WindowUnavailable",
        message = "Open the desktop app to change its startup and tray settings.", preferences = DesktopPreferenceView() });
    if (change.LaunchAtLogin.HasValue == change.CloseToTray.HasValue)
        return Results.Json(new { ok = false, code = "InvalidPreference",
            message = "Change one desktop preference at a time.", preferences = DesktopPreferenceView() });
    try
    {
        if (change.LaunchAtLogin is { } launchAtLogin) startupRegistration.SetEnabled(launchAtLogin);
        if (change.CloseToTray is { } closeToTray)
        {
            var next = new DesktopPreferences { CloseToTray = closeToTray };
            data.SaveDesktopPreferences(next);
            desktopPreferences = next;
            desktop.SetCloseToTray(closeToTray);
        }
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or
        IOException or InvalidOperationException)
    {
        return Results.Json(new { ok = false, code = "PreferenceFailed", message = ex.Message,
            preferences = DesktopPreferenceView() });
    }
    return Results.Json(new { ok = true, code = "PreferenceSaved", message = "App preference saved.",
        preferences = DesktopPreferenceView() });
});
app.MapGet("/api/local/update", async () => Results.Json(await updater.CheckAsync()));
app.MapPost("/api/local/update/check", async () => Results.Json(await updater.CheckAsync(true)));
app.MapPost("/api/local/update/install", async (HttpContext context) =>
{
    if (desktop is null) return Results.Json(new UpdateResult(false, "WindowUnavailable", "Open the published app window to update."));
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return Results.Json(new UpdateResult(false, "AlreadyUpdating", "The app is already restarting."));
        if ((await manager.SnapshotAsync()).Runs.Any(run => run.State != "Offline"))
            return Results.Json(new UpdateResult(false, "ManagedRunPresent",
                "Stop or resolve every hosted server before installing the update."));
    }
    finally { modeGate.Release(); }
    var prepared = await updater.PrepareAsync();
    if (!prepared.Ok) return Results.Json(prepared);
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return Results.Json(new UpdateResult(false, "AlreadyUpdating", "The app is already restarting."));
        if ((await manager.SnapshotAsync()).Runs.Any(run => run.State != "Offline"))
            return Results.Json(new UpdateResult(false, "ManagedRunPresent",
                "Stop or resolve every hosted server before installing the update."));
        var started = updater.StartReplacement();
        if (started.Ok)
        {
            updatePending = true;
            context.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
        }
        return Results.Json(started);
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/show", async () => desktop is not null && await desktop.ShowAsync()
    ? Results.Json(new { ok = true, code = "WindowShown" })
    : Results.Conflict(new { ok = false, code = "WindowUnavailable" }));
async Task<IResult> HostOnly(Func<Task<ActionResult>> action)
{
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return Results.Conflict(new { code = "UpdatePending", message = "TogetherServer is restarting for an update." });
        if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." });
        return Results.Json(await action());
    }
    finally { modeGate.Release(); }
}
async Task<IResult> HostOnlyCertification(Func<Task<CustomCertificationResult>> action)
{
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return Results.Conflict(new { code = "UpdatePending", message = "TogetherServer is restarting for an update." });
        if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." });
        return Results.Json(await action());
    }
    finally { modeGate.Release(); }
}
app.MapPut("/api/local/settings", async (HostSettings settings) =>
{
    await modeGate.WaitAsync();
    ActionResult result;
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to My server first." });
        result = await manager.UpdateSettingsAsync(settings);
    }
    finally { modeGate.Release(); }
    if (result.Ok)
    {
        pairing.ReconcileProfiles(result.Snapshot.Settings.Profiles.Select(profile => profile.Id));
        await companionServer.SyncAsync();
    }
    return Results.Json(result);
});
app.MapPost("/api/local/network/detect-public-ip", async () =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    var detection = await publicIpLookup.DetectAsync();
    if (!detection.Ok) return Results.Json(new { detection.Ok, code = "PublicIpUnavailable", detection.Address, detection.Message });
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
        var snapshot = await manager.RecordDetectedPublicIpAsync(detection.Address!);
        return Results.Json(new { detection.Ok, code = "PublicIpDetected", detection.Address, detection.Message, snapshot });
    }
    finally { modeGate.Release(); }
});
app.MapGet("/api/local/network/ports", async () => Results.Json(PortDiagnostics.Read(
    await manager.SnapshotAsync(), games, companionServer.Active, pairing.Views(), companionServer.Warning)));
app.MapGet("/api/local/network/routes", () => Results.Json(ConnectionRoutes.Detect()));
app.MapPost("/api/local/network/test-friend-route", async () =>
{
    if (friendMode) return Results.Conflict(new ExternalPortProbeResult("Unavailable",
        "Switch to My server before testing the Friend route.", 0, DateTimeOffset.UtcNow));
    var settings = (await manager.SnapshotAsync()).Settings;
    if (!companionServer.Active) return Results.Json(new ExternalPortProbeResult("Unavailable",
        "Start Friend app connections from Invite friends before testing the outside route.",
        settings.CompanionPort, DateTimeOffset.UtcNow));
    if (IPAddress.TryParse(settings.CompanionBindAddress, out var bindAddress) && IPAddress.IsLoopback(bindAddress))
        return Results.Json(new ExternalPortProbeResult("Unavailable",
            "Friend app connections are bound to this PC only. Use a LAN bind address or 0.0.0.0 for an outside route.",
            settings.CompanionPort, DateTimeOffset.UtcNow));
    return Results.Json(await externalPortProbe.CheckAsync(settings.CompanionEndpoint, settings.CompanionPort));
});
app.MapGet("/api/local/game-types", () => Results.Json(games.All.Select(game => new
{
    game.Kind,
    game.DisplayName
})));
app.MapPost("/api/local/profiles/{id:guid}/start", (Guid id) => HostOnly(() => manager.StartAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/stop", (Guid id) => HostOnly(() => manager.StopAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/restart", (Guid id) => HostOnly(() => manager.RestartAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/countdown/extend", (Guid id, CountdownExtensionRequest request) =>
    HostOnly(() => manager.ExtendAutoShutdownAsync(id, request.Minutes)));
app.MapPost("/api/local/profiles/{id:guid}/health", (Guid id) => HostOnly(() => manager.HealthAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/forget", (Guid id) => HostOnly(() => manager.ForgetAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/password", (Guid id, ValheimPasswordRequest request) =>
    HostOnly(() => manager.SetValheimPasswordAsync(id, request.Password)));
app.MapPut("/api/local/profiles/{id:guid}/custom-scripts", (Guid id, CustomScriptBundle scripts) =>
    HostOnly(() => manager.SetCustomScriptsAsync(id, scripts)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/begin", (Guid id) =>
    HostOnlyCertification(() => manager.BeginCustomCertificationAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/status", (Guid id) =>
    HostOnlyCertification(() => manager.CheckCustomCertificationAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/confirm", (Guid id) =>
    HostOnlyCertification(() => manager.ConfirmCustomCertificationAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/cancel", (Guid id) =>
    HostOnlyCertification(() => manager.CancelCustomCertificationAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/revoke", (Guid id) =>
    HostOnlyCertification(() => manager.RevokeCustomCertificationAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/custom-scripts/reveal", async (Guid id) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
        var snapshot = await manager.SnapshotAsync();
        if (!snapshot.Settings.Profiles.Any(profile => profile.Id == id && profile.Kind == GameKinds.Custom))
            return Results.NotFound(new { ok = false, code = "ProfileNotFound", message = "Saved custom game was not found." });
        var scripts = data.LoadCustomScripts(id);
        return Results.Json(new { ok = true, code = scripts is null ? "CustomScriptsEmpty" : "CustomScriptsLoaded",
            message = scripts is null ? "No custom scripts have been saved yet." : "Custom scripts loaded from Windows protected storage.",
            scripts = scripts ?? new CustomScriptBundle("", "", "") });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                               System.Security.Cryptography.CryptographicException or JsonException)
    {
        return Results.Json(new { ok = false, code = "CustomScriptsUnavailable", message = "Custom scripts could not be read: " + ex.Message });
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/profiles/{id:guid}/game-password/reveal", async (Guid id) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
        var snapshot = await manager.SnapshotAsync();
        if (!snapshot.Settings.Profiles.Any(profile => profile.Id == id && profile.Kind == "Valheim"))
            return Results.NotFound(new { ok = false, code = "ProfileNotFound", message = "Saved Valheim server was not found." });
        var password = data.LoadValheimPassword(id);
        return string.IsNullOrEmpty(password)
            ? Results.NotFound(new { ok = false, code = "PasswordRequired", message = "Save a game password first." })
            : Results.Json(new { ok = true, password });
    }
    finally { modeGate.Release(); }
});
app.MapGet("/api/local/valheim/discover", async () => Results.Json(friendMode
    ? ValheimSetup.Scan()
    : ValheimSetup.Scan((await manager.SnapshotAsync()).Settings.Profiles
        .Where(profile => profile.Kind == "Valheim").Select(profile => profile.WorldDirectory))));
app.MapGet("/api/local/minecraft/discover", async (string? folder) =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    var profiles = (await manager.SnapshotAsync()).Settings.Profiles;
    return Results.Json(MinecraftSetup.Scan(data, profiles, folder));
});
app.MapPost("/api/local/minecraft/install", async (MinecraftInstallRequest request, CancellationToken ct) =>
    friendMode
        ? Results.Conflict(new MinecraftInstallResult(false, "FriendMode", "Switch to Host mode first."))
        : Results.Json(await minecraftInstaller.InstallAsync(request, ct)));
app.MapPost("/api/local/valheim/browse-server", async () =>
{
    if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." });
    if (desktop is null) return Results.Conflict(new { code = "WindowUnavailable", message = "Open the TogetherServer window to browse files." });
    try
    {
        var path = await desktop.PickFileAsync("Choose Valheim Dedicated Server",
            "Valheim Dedicated Server (valheim_server.exe)|valheim_server.exe|Applications (*.exe)|*.exe");
        if (path is null) return Results.Json(new { ok = false, code = "Canceled", message = "No server executable selected." });
        if (!File.Exists(path) || !Path.GetFileName(path).Equals("valheim_server.exe", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { ok = false, code = "InvalidServerExecutable", message = "Choose valheim_server.exe from your installed Valheim Dedicated Server folder." });
        return Results.Json(new { ok = true, code = "ServerSelected", message = "Server executable selected. Save settings before starting.", executablePath = path });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, code = "BrowseFailed", message = "Could not open the Windows file picker: " + ex.Message }); }
});
app.MapPost("/api/local/minecraft/browse", async (MinecraftBrowseRequest request) =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    if (desktop is null) return Results.Conflict(new { ok = false, code = "WindowUnavailable", message = "Open the TogetherServer window to browse files." });
    if (request.Kind is not (GameKinds.MinecraftJava or GameKinds.MinecraftBedrock) ||
        request.Target is not ("folder" or "executable" or "jar") ||
        request.Target == "jar" && request.Kind != GameKinds.MinecraftJava)
        return Results.BadRequest(new { ok = false, code = "InvalidBrowseTarget", message = "Choose a supported Minecraft file or folder." });
    try
    {
        string? path;
        if (request.Target == "folder") path = await desktop.PickFolderAsync("Choose prepared Minecraft server folder");
        else
        {
            var expected = request.Target == "jar" ? ".jar" : request.Kind == GameKinds.MinecraftJava ? "java.exe" : "bedrock_server.exe";
            var filter = request.Target == "jar" ? "Minecraft server JAR (*.jar)|*.jar" :
                $"{expected}|{expected}|Applications (*.exe)|*.exe";
            path = await desktop.PickFileAsync("Choose " + expected, filter);
            if (path is not null && (!File.Exists(path) || (request.Target == "jar"
                    ? !Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase)
                    : !Path.GetFileName(path).Equals(expected, StringComparison.OrdinalIgnoreCase))))
                return Results.Json(new { ok = false, code = "InvalidFile", message = "Choose the requested server file." });
        }
        return Results.Json(path is null
            ? new { ok = false, code = "Canceled", message = "No path selected.", path = (string?)null }
            : new { ok = true, code = "PathSelected", message = "Path selected. Save setup before starting.", path = (string?)path });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, code = "BrowseFailed", message = "Could not open the Windows picker: " + ex.Message }); }
});
app.MapPost("/api/local/custom/browse-working-directory", async () =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    if (desktop is null) return Results.Conflict(new { ok = false, code = "WindowUnavailable", message = "Open the TogetherServer window to browse folders." });
    try
    {
        var path = await desktop.PickFolderAsync("Choose custom game working and save folder");
        return Results.Json(path is null
            ? new { ok = false, code = "Canceled", message = "No folder selected.", path = (string?)null }
            : new { ok = true, code = "PathSelected", message = "Working directory selected. Save setup before starting.", path = (string?)path });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, code = "BrowseFailed", message = "Could not open the Windows folder picker: " + ex.Message, path = (string?)null }); }
});
app.MapPost("/api/local/valheim/browse-world", async () =>
{
    if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." });
    if (desktop is null) return Results.Conflict(new { code = "WindowUnavailable", message = "Open the TogetherServer window to browse files." });
    try
    {
        var localSaveRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "IronGate", "Valheim", "worlds_local");
        var path = await desktop.PickFileAsync("Choose an existing Valheim world",
            "Valheim world files (*.db;*.fwl)|*.db;*.fwl|All files (*.*)|*.*", localSaveRoot);
        return Results.Json(path is null
            ? new WorldFileSelection(false, "Canceled", "No world file selected.", null, null)
            : ValheimSetup.SelectWorldFile(path));
    }
    catch (Exception ex) { return Results.Json(new WorldFileSelection(false, "BrowseFailed", "Could not open the Windows file picker: " + ex.Message, null, null)); }
});
app.MapPost("/api/local/valheim/browse-world-folder", async () =>
{
    if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." });
    if (desktop is null) return Results.Conflict(new { code = "WindowUnavailable", message = "Open the TogetherServer window to browse folders." });
    try
    {
        var localSaveRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "IronGate", "Valheim", "worlds_local");
        var path = await desktop.PickFolderAsync("Choose the Valheim world folder", localSaveRoot);
        return Results.Json(path is null
            ? new WorldFileSelection(false, "Canceled", "No world folder selected.", null, null)
            : ValheimSetup.SelectWorldFolder(path));
    }
    catch (Exception ex) { return Results.Json(new WorldFileSelection(false, "BrowseFailed", "Could not open the Windows folder picker: " + ex.Message, null, null)); }
});
app.MapPost("/api/local/valheim/import", async (ImportWorldRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." });
        return Results.Json(ValheimSetup.ImportCopy(data, request));
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/mode/{mode}", async (string mode) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (!mode.Equals("host", StringComparison.OrdinalIgnoreCase) && !mode.Equals("friend", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { ok = false, code = "InvalidMode", message = "Choose Host or Friend." });
        data.SavePreferredMode(mode.Equals("friend", StringComparison.OrdinalIgnoreCase) ? "Friend" : "Host");
        friendMode = mode.Equals("friend", StringComparison.OrdinalIgnoreCase);
        return Results.Json(new { ok = true, code = "ModeChanged", message = friendMode
            ? "Showing your connected Hosts. Your own server and Friend access keep running."
            : "Showing your server. Connections to other Hosts keep running." });
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/quit", async (HttpContext context) =>
{
    await modeGate.WaitAsync();
    try
    {
        if ((await manager.SnapshotAsync()).Runs.Any(run => run.State != "Offline"))
            return Results.Json(new { ok = false, code = "ManagedRunPresent",
                message = "Stop or resolve every managed server before quitting TogetherServer." });
        context.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
        return Results.Json(new { ok = true, code = "Closing", message = "TogetherServer is closing." });
    }
    finally { modeGate.Release(); }
});

app.MapGet("/api/local/companion", async () =>
{
    string? fingerprint = null;
    try { using var certificate = identity.Load(); if (certificate is not null) fingerprint = HostIdentity.Fingerprint(certificate); }
    catch { /* A bad protected identity is reported by the listener warning. */ }
    var snapshot = await manager.SnapshotAsync();
    return Results.Json(new { listenerActive = companionServer.Active,
        listenerWarning = companionServer.Warning,
        endpoint = snapshot.Settings.CompanionEndpoint, fingerprint, certificates = identity.State(),
        route = ConnectionRoutes.Normalize(snapshot.Settings.ConnectionRoute), devices = pairing.Views(),
        stopSafety = companionServer.StopSafety(snapshot) });
});
app.MapGet("/api/local/operations", () => Results.Json(companionServer.RecentOperations()));
app.MapPost("/api/local/companion/certificate/stage", async () =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
        var settings = data.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.CompanionEndpoint))
            return Results.BadRequest(new { ok = false, code = "EndpointRequired", message = "Save the Friend endpoint first." });
        var state = identity.StageNext(settings.CompanionEndpoint);
        data.Audit($"certificate-stage {state.NextFingerprint} {DateTimeOffset.UtcNow:O}");
        return Results.Json(new { ok = true, code = "CertificateStaged", message = "The next Host certificate is staged and will be announced over authenticated connections.", certificates = state });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
    { return Results.BadRequest(new { ok = false, code = "CertificateStageFailed", message = ex.Message }); }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/companion/certificate/activate", async () =>
{
    object result;
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
        var state = identity.ActivateNext();
        data.Audit($"certificate-activate {state.ActiveFingerprint} {DateTimeOffset.UtcNow:O}");
        result = new { ok = true, code = "CertificateActivated", message = "The staged Host certificate is now active. The prior pin remains in the recovery grace period.", certificates = state };
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
    { return Results.BadRequest(new { ok = false, code = "CertificateActivationFailed", message = ex.Message }); }
    finally { modeGate.Release(); }
    await companionServer.SyncAsync();
    return Results.Json(result);
});
app.MapPost("/api/local/companion/certificate/retire-previous", async () =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
        var state = identity.RetirePrevious();
        data.Audit($"certificate-retire-previous {DateTimeOffset.UtcNow:O}");
        return Results.Json(new { ok = true, code = "PreviousCertificateRetired", message = "The previous Host certificate pin was retired.", certificates = state });
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
    { return Results.BadRequest(new { ok = false, code = "CertificateRetireFailed", message = ex.Message }); }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/servers/{profileId:guid}/invite/current", async (Guid profileId) =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode" });
    var snapshot = await manager.SnapshotAsync();
    if (!snapshot.Settings.Profiles.Any(profile => profile.Id == profileId))
        return Results.NotFound(new { ok = false, code = "UnknownServer" });
    var current = pairing.CurrentServerInvite(profileId);
    if (current is not null && !string.IsNullOrWhiteSpace(snapshot.Settings.CompanionEndpoint))
    {
        using var currentCertificate = identity.Ensure(snapshot.Settings.CompanionEndpoint);
        var refreshed = pairing.IssueServer(profileId, current.CanStart, false,
            snapshot.Settings.CompanionEndpoint, HostIdentity.Fingerprint(currentCertificate), false);
        current = new ServerInviteView(refreshed, current.CanStart);
    }
    return Results.Json(new { ok = true, exists = current is not null,
        password = current is null ? null : PairingPassword.Encode(current.Invitation),
        canStart = current?.CanStart ?? true });
});
app.MapPost("/api/local/servers/{profileId:guid}/invite", async (Guid profileId, ServerInviteRequest request) =>
{
    var password = "";
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
        var settings = data.LoadSettings();
        if (!settings.Profiles.Any(profile => profile.Id == profileId))
            return Results.NotFound(new { ok = false, code = "UnknownServer", message = "Choose a saved server." });
        if (string.IsNullOrWhiteSpace(settings.CompanionEndpoint))
        {
            var route = ConnectionRoutes.Normalize(settings.ConnectionRoute);
            var address = route.Mode == ConnectionRouteModes.DirectInternet
                ? settings.PublicGameIpCheckedUtc is { } checkedUtc && DateTimeOffset.UtcNow - checkedUtc <= TimeSpan.FromHours(1) &&
                    GameConnection.IsPublicIpv4(settings.PublicGameIp) ? settings.PublicGameIp : null
                : ConnectionRoutes.ValidAddress(route.Address) ? route.Address : null;
            if (address is null)
                return Results.BadRequest(new { ok = false, code = "AddressUnavailable", message = route.Mode == ConnectionRouteModes.DirectInternet
                    ? "Check this PC's public IP address first." : "Choose an address for the selected route first." });
            settings.CompanionEndpoint = $"https://{address}:{settings.CompanionPort}";
            settings.CompanionBindAddress = route.Mode == ConnectionRouteModes.DirectInternet ? "0.0.0.0" : address;
            var saved = await manager.UpdateSettingsAsync(settings);
            if (!saved.Ok) return Results.BadRequest(new { ok = false, code = saved.Code, message = saved.Message });
        }
        if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out var endpoint) || endpoint.Port != settings.CompanionPort)
            return Results.BadRequest(new { ok = false, code = "InvalidEndpoint", message = "Save an HTTPS IP endpoint and matching companion port first." });
        try
        {
            using var certificate = identity.Ensure(settings.CompanionEndpoint);
            var firstHostInvite = !pairing.HasInviteOrCredential();
            var invite = pairing.IssueServer(profileId, request.CanStart, false,
                settings.CompanionEndpoint, HostIdentity.Fingerprint(certificate), request.Refresh);
            password = PairingPassword.Encode(invite);
            if (request.EnableConnections)
            {
                settings.CompanionListeningEnabled = true;
                if (firstHostInvite && request.CanStart)
                    settings.RemoteControlsEnabled = true;
                var saved = await manager.UpdateSettingsAsync(settings);
                if (!saved.Ok)
                    return Results.BadRequest(new { ok = false, code = saved.Code, message = saved.Message });
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return Results.BadRequest(new { ok = false, code = "InviteFailed", message = ex.Message });
        }
    }
    finally { modeGate.Release(); }
    if (request.EnableConnections) await companionServer.SyncAsync();
    return Results.Json(new { ok = true, code = request.Refresh ? "InviteRefreshed" : "InviteReady",
        message = request.Refresh ? "Server code refreshed. Previous code and paired access for this server were revoked." : "This server code can be shared with Friend PCs until you refresh it.",
        password,
        listenerActive = companionServer.Active, listenerWarning = companionServer.Warning });
});
app.MapPost("/api/local/devices/{id:guid}/revoke", async (Guid id) =>
{
    await modeGate.WaitAsync();
    try { return friendMode ? Results.Conflict(new { ok = false, code = "FriendMode" }) : Results.Json(pairing.Revoke(id)); }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/permissions", async (Guid id, DevicePermissionRequest request) =>
{
    await modeGate.WaitAsync();
    try { return friendMode ? Results.Conflict(new { ok = false, code = "FriendMode" }) :
        Results.Json(pairing.SetPermissions(id, request.CanStart, request.CanStop, request.Scope)); }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/servers", async (Guid id, DeviceServerAccessRequest request) =>
{
    await modeGate.WaitAsync();
    try { return friendMode ? Results.Conflict(new { ok = false, code = "FriendMode" }) :
        Results.Json(pairing.SetServerAccess(id, request.ProfileIds, request.Permissions,
            data.LoadSettings().Profiles.Select(profile => profile.Id))); }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/name", async (Guid id, DeviceNameRequest request) =>
{
    await modeGate.WaitAsync();
    try { return friendMode ? Results.Conflict(new { ok = false, code = "FriendMode" }) :
        Results.Json(pairing.SetName(id, request.Name)); }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/friend/pair", async (FriendPairRequest request) =>
    friendMode ? Results.Json(await friend.PairAsync(request.Invitation, request.HostAddress))
    : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/connections/{id:guid}/select", (Guid id) =>
    friendMode ? Results.Json(friend.Select(id)) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPut("/api/local/friend/connections/{id:guid}/endpoint", async (Guid id, EndpointRecoveryRequest request) =>
    friendMode ? Results.Json(await friend.RecoverEndpointAsync(id, request.Endpoint)) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/poll", async () =>
    friendMode ? Results.Json(await friend.PollAsync()) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/probe-game", async (Guid id) =>
    friendMode ? Results.Json(await friend.ProbeGameEndpointAsync(id)) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/{action}", async (Guid id, string action) =>
    friendMode ? Results.Json(await friend.RequestAsync(id, action)) : Results.Conflict(new { ok = false, code = "HostMode" }));

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
await companionServer.SyncAsync();
using var pollStop = new CancellationTokenSource();
var friendPollTask = Task.Run(async () =>
{
    while (!pollStop.IsCancellationRequested)
    {
        try { await friend.PollAsync(); }
        catch (Exception ex) { Console.Error.WriteLine("Friend poll failed: " + ex.GetType().Name); }
        try { await Task.Delay(TimeSpan.FromSeconds(5), pollStop.Token); }
        catch (OperationCanceledException) { break; }
    }
});
var idleShutdownTask = Task.Run(async () =>
{
    while (!pollStop.IsCancellationRequested)
    {
        try { await manager.RefreshObservationsAsync(); }
        catch (Exception ex) { Console.Error.WriteLine("Server observation failed: " + ex.GetType().Name); }
        try { await manager.MaintainIdleShutdownAsync(); }
        catch (Exception ex) { Console.Error.WriteLine("Empty-server timer failed: " + ex.GetType().Name); }
        try { await Task.Delay(TimeSpan.FromSeconds(3), pollStop.Token); }
        catch (OperationCanceledException) { break; }
    }
});
var updateCheckTask = Task.Run(async () =>
{
    while (!pollStop.IsCancellationRequested)
    {
        try { await updater.CheckAsync(); }
        catch (Exception ex) { Console.Error.WriteLine("Update check failed: " + ex.GetType().Name); }
        try { await Task.Delay(AppUpdater.AutomaticCheckInterval, pollStop.Token); }
        catch (OperationCanceledException) { break; }
    }
});
if (desktop is not null)
{
    app.Lifetime.ApplicationStarted.Register(desktop.Start);
    app.Lifetime.ApplicationStopping.Register(desktop.Exit);
}
try { await app.RunAsync(); }
catch (Exception ex) when (openWindow)
{
    DesktopLaunch.ShowError("TogetherServer could not start its local GUI.\n\n" + ex.Message);
    Environment.ExitCode = 1;
}
finally { pollStop.Cancel(); await Task.WhenAll(friendPollTask, idleShutdownTask, updateCheckTask); await companionServer.StopAsync(); }

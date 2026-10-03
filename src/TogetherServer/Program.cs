using System.Net;
using System.Reflection;
using System.Text.Json;
using TogetherServer;

if (MinecraftConsoleCapture.IsCommand(args))
{
    Environment.ExitCode = await MinecraftConsoleCapture.RunAsync(args);
    return;
}

if (InstallerIntegration.IsCommand(args))
{
    Environment.ExitCode = InstallerIntegration.Run(args, Environment.ProcessPath ?? "");
    return;
}

if (args.Length > 0 && args[0] == "--apply-update")
{
    Environment.ExitCode = await UpdateInstaller.RunAsync(args);
    return;
}

var requestedFriend = args.Contains("--friend", StringComparer.OrdinalIgnoreCase);
var requestedHost = args.Contains("--host", StringComparer.OrdinalIgnoreCase);
var startupLaunch = args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
var stagingRequested = AppInstance.RequestsStaging(args);
using var installerMutex = stagingRequested ? null : new Mutex(false, @"Local\TogetherServer.Application");
if (requestedFriend && requestedHost)
    throw new ArgumentException("Choose either --host or --friend.");
var openWindow = args.Length == 0 || args.Contains("--desktop", StringComparer.OrdinalIgnoreCase) || startupLaunch;
DesktopLaunch.EnsureConsoleForGameStop(openWindow);
var portIndex = Array.IndexOf(args, "--port");
var port = portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var parsedPort)
    ? parsedPort : stagingRequested ? 5128 : 5127;
if (port is < 1024 or > 65535) throw new ArgumentException("Local GUI port must be between 1024 and 65535.");

AppInstance instance;
LocalData data;
try
{
    instance = AppInstance.Resolve(args);
    instance.PrepareDataRoot();
    data = new LocalData(instance.DataRoot, instance.DefaultCompanionPort);
    var existingSettings = instance.ValidateSettings(data.LoadSettings());
    if (!existingSettings.Ok)
    {
        data.Dispose();
        throw new InvalidDataException(existingSettings.Message);
    }
}
catch (IOException ex)
{
    if (openWindow && await DesktopLaunch.TryShowExistingAsync(port, showWindow: !startupLaunch)) return;
    var message = "TogetherServer could not open its local data. Another instance may be starting.\n\n" + ex.Message;
    if (openWindow) DesktopLaunch.ShowError(message);
    else DiagnosticOutput.WriteError(message);
    Environment.ExitCode = 1;
    return;
}
catch (Exception ex) when (ex is ArgumentException or InvalidDataException or UnauthorizedAccessException or
                           System.Security.SecurityException)
{
    var message = "TogetherServer could not safely open its local data. No server action was started.\n\n" + ex.Message;
    if (openWindow) DesktopLaunch.ShowError(message);
    else DiagnosticOutput.WriteError(message);
    Environment.ExitCode = 1;
    return;
}
using var ownedData = data;
var root = instance.DataRoot;
var desktopPreferences = data.LoadDesktopPreferences();
var startupRegistration = new WindowsStartup(Environment.ProcessPath ?? "");
var friendMode = requestedFriend || (!requestedHost && data.LoadPreferredMode() == "Friend");
var games = new GameServerRegistry(data);
var pairing = new PairingService(data);
pairing.ReconcileProfiles(data.LoadSettings().Profiles.Select(profile => profile.Id));
using var hostingPower = new WindowsHostingPowerGuard();
var startupRecovery = new StartupRecoveryService(data, data.LoadRuns(), Environment.ProcessPath ?? "");
var manager = new HostManager(data, games, TimeProvider.System, hostingPower, startupRecovery, pairing);
async Task<SharedWorldRoster> PublishRosterAndConfirmAsync(Guid profileId,
    bool? ownerOverride = null, bool reviewSourceChange = false)
{
    var roster = await manager.PublishSharedWorldRosterAsync(profileId,
        pairing.SharedRosterMembers(profileId), ownerOverride, reviewSourceChange);
    pairing.ConfirmSharedRosterPublished(profileId, roster);
    return roster;
}
async Task RepairDirtyRostersAsync()
{
    foreach (var profile in data.LoadSettings().Profiles.Where(item =>
        item.SharedSavesEnabled && pairing.SharedRosterDirty(item.Id)))
    {
        if (!await manager.SharedRosterManagementAvailableAsync(profile.Id)) continue;
        await PublishRosterAndConfirmAsync(profile.Id);
    }
}
object SuccessorRosterReadOnly() => new { ok = false, code = "SuccessorRosterReadOnly",
    message = "Sharing permissions stay with the original owner. This successor PC can host and share verified saves, but cannot change the signed member list." };
try { if (!friendMode) await RepairDirtyRostersAsync(); }
catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException)
{ data.TryAudit($"shared-roster-repair-pending {ex.GetType().Name} {DateTimeOffset.UtcNow:O}"); }
var acceptanceRecorder = new AcceptanceRecorder(data, TimeProvider.System);
var updateCheckpoints = new StateCheckpointService(data, TimeProvider.System);
var serverLogs = new ServerLogService(data, manager);
var identity = new HostIdentity(data);
using var friend = new FriendService(data);
using var updateClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
var appVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
var updater = new AppUpdater(updateClient, root, Environment.ProcessPath ?? "", appVersion,
    enabled: instance.UpdatesAvailable,
    disabledMessage: "Automatic updates are disabled in staging. Rebuild or replace the staging package explicitly.");
using var publicIpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
var publicIpLookup = new PublicIpLookup(publicIpClient);
using var externalProbeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var externalPortProbe = new ExternalPortProbe(externalProbeClient);
ExternalPortProbeResult? latestRouteDiagnostic = null;
using var minecraftClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var minecraftInstaller = new MinecraftInstaller(minecraftClient, data, instance.IsStaging);
using var modeGate = new SemaphoreSlim(1, 1);
var updatePending = false;
var shutdownPending = false;
var companionServer = new CompanionServer(data, manager, pairing, games, serverLogs, modeGate, port,
    () => updatePending, () => shutdownPending, friend.ProbeRecoveryHostLossAsync,
    friend.CurrentRecoveryHostLoss);
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
var app = builder.Build();
var desktop = openWindow ? new DesktopWindow(new Uri($"http://127.0.0.1:{port}/"), root,
    app.Lifetime.StopApplication, desktopPreferences.CloseToTray, startupLaunch,
    instance.DisplayName, instance.IsStaging) : null;
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

static bool HasSensitiveLocalGetHeader(HttpContext context) =>
    context.Request.Headers["X-TogetherServer-Local"] == "1";

static IResult? FixedOwnerGetRejection(HttpContext context)
{
    if (!HasSensitiveLocalGetHeader(context))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (context.Request.QueryString.HasValue ||
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        return Results.BadRequest(new
        {
            code = "InvalidDiagnosticsRequest",
            message = "Diagnostics and support export accept no query, path, host, port, URL, or request body."
        });
    return null;
}

static IResult? RecentSessionsGetRejection(HttpContext context, out int limit)
{
    limit = 8;
    if (!HasSensitiveLocalGetHeader(context))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
        context.Request.Query.Keys.Any(key => !key.Equals("limit", StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new
        {
            code = "InvalidRecentSessionsRequest",
            message = $"Choose a saved server and use one limit from 1 to {HostManager.MaximumRecentSessionLimit}."
        });
    if (!context.Request.Query.TryGetValue("limit", out var values)) return null;
    if (values.Count != 1 || !int.TryParse(values[0], out limit) ||
        limit is < 1 or > HostManager.MaximumRecentSessionLimit)
        return Results.BadRequest(new
        {
            code = "InvalidRecentSessionsRequest",
            message = $"Recent session limit must be between 1 and {HostManager.MaximumRecentSessionLimit}."
        });
    return null;
}

app.MapGet("/api/local/snapshot", async () => friendMode
    ? Results.Json(friend.View())
    : Results.Json(await manager.SnapshotAsync()));
app.MapGet("/api/local/instance", () => Results.Json(instance.View(port)));
app.MapGet("/api/local/data-recovery", () => Results.Json(data.Recovery));
app.MapGet("/api/local/diagnostics", async (HttpContext context) =>
{
    if (FixedOwnerGetRejection(context) is { } rejection) return rejection;
    if (friendMode)
        return Results.Conflict(new
        {
            code = "FriendMode",
            message = "Switch to Host before opening owner diagnostics. Hosting and Friend connections keep running."
        });
    try
    {
        var snapshot = await manager.SnapshotAsync();
        var devices = pairing.Views();
        var ports = PortDiagnostics.Read(snapshot, games, companionServer.Active, devices,
            companionServer.Warning, companionServer.ListenerState);
        return Results.Json(OwnerDiagnostics.Build(snapshot, games, ports, devices,
            data.Recovery, updater.View, latestRouteDiagnostic, instance.IsStaging));
    }
    catch (Exception ex)
    {
        DiagnosticOutput.WriteError("Owner diagnostics failed: " + ex.GetType().Name);
        return Results.Json(new
        {
            code = "DiagnosticsUnavailable",
            message = "TogetherServer could not assemble owner diagnostics. No server action was started."
        }, statusCode: StatusCodes.Status500InternalServerError);
    }
});
app.MapGet("/api/local/support-report", async (HttpContext context) =>
{
    if (FixedOwnerGetRejection(context) is { } rejection) return rejection;
    if (friendMode)
        return Results.Conflict(new
        {
            code = "FriendMode",
            message = "Switch to Host before exporting an owner support report."
        });
    try
    {
        var snapshot = await manager.SnapshotAsync();
        var devices = pairing.Views();
        var ports = PortDiagnostics.Read(snapshot, games, companionServer.Active, devices,
            companionServer.Warning, companionServer.ListenerState);
        var diagnostics = OwnerDiagnostics.Build(snapshot, games, ports, devices,
            data.Recovery, updater.View, latestRouteDiagnostic, instance.IsStaging);
        return Results.Json(SupportReportExporter.Create(diagnostics, snapshot, instance,
            updater.View, data.LoadActivity(24), companionServer.RecentOperations(),
            data.ReadSupportLogMetadata(), data));
    }
    catch (Exception ex)
    {
        DiagnosticOutput.WriteError("Support report failed: " + ex.GetType().Name);
        return Results.Json(new
        {
            code = "SupportReportUnavailable",
            message = "TogetherServer could not create the support report. Private error details were left out."
        }, statusCode: StatusCodes.Status500InternalServerError);
    }
});
app.MapGet("/api/local/window", () => Results.Json(new
{
    available = desktop is not null,
    visible = desktop?.Visible ?? false,
    rendered = desktop?.Rendered ?? false,
    loadState = desktop?.LoadState ?? "Unavailable",
    loadErrorCode = desktop?.LoadErrorCode,
    loadFailureKind = desktop?.LoadFailureKind,
    loadFailureHResult = desktop?.LoadFailureHResult,
    fileDialogOpen = desktop?.FileDialogOpen ?? false,
    customChrome = desktop?.CustomChrome ?? false
}));
object DesktopPreferenceView()
{
    if (!instance.StartupAvailable)
        return new
        {
            available = desktop is not null,
            launchAtLogin = false,
            closeToTray = desktopPreferences.CloseToTray,
            startupAvailable = false
        };
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
    return new
    {
        available = desktop is not null,
        launchAtLogin,
        closeToTray = desktopPreferences.CloseToTray,
        startupAvailable
    };
}
app.MapGet("/api/local/desktop/preferences", () => Results.Json(DesktopPreferenceView()));
app.MapPut("/api/local/desktop/preferences", (DesktopPreferenceChange change) =>
{
    if (!instance.StartupAvailable && change.LaunchAtLogin.HasValue)
        return Results.Json(new
        {
            ok = false,
            code = "StagingStartupDisabled",
            message = "Staging never changes the production Windows sign-in registration. Open staging explicitly with its launcher.",
            preferences = DesktopPreferenceView()
        });
    if (desktop is null) return Results.Json(new
    {
        ok = false,
        code = "WindowUnavailable",
        message = "Open the desktop app to change its startup and tray settings.",
        preferences = DesktopPreferenceView()
    });
    if (change.LaunchAtLogin.HasValue == change.CloseToTray.HasValue)
        return Results.Json(new
        {
            ok = false,
            code = "InvalidPreference",
            message = "Change one desktop preference at a time.",
            preferences = DesktopPreferenceView()
        });
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
        return Results.Json(new
        {
            ok = false,
            code = "PreferenceFailed",
            message = ex.Message,
            preferences = DesktopPreferenceView()
        });
    }
    return Results.Json(new
    {
        ok = true,
        code = "PreferenceSaved",
        message = "App preference saved.",
        preferences = DesktopPreferenceView()
    });
});
app.MapGet("/api/local/update", async () => Results.Json(await updater.CheckAsync()));
app.MapPost("/api/local/update/check", async () => Results.Json(await updater.CheckAsync(true)));
app.MapPost("/api/local/update/install", async (HttpContext context) =>
{
    if (!instance.UpdatesAvailable)
        return Results.Json(new UpdateResult(false, "UpdatesDisabled",
            "Automatic updates are disabled in staging. Rebuild or replace the staging package explicitly."));
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
        var targetVersion = updater.PreparedVersion;
        if (targetVersion is null)
            return Results.Json(new UpdateResult(false, "NotReady", "No verified update is ready."));
        var checkpoint = updateCheckpoints.Create(appVersion.ToString(3), targetVersion,
            Environment.ProcessPath ?? "");
        if (!checkpoint.Ok || checkpoint.Checkpoint is null)
            return Results.Json(new UpdateResult(false, checkpoint.Code, checkpoint.Message));
        var started = updater.StartReplacement(checkpoint.Checkpoint);
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
async Task<IResult> HostOnly<T>(Func<Task<T>> action)
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
async Task<IResult> OwnerGet<T>(HttpContext context, Func<Task<T>> action, string area)
{
    if (FixedOwnerGetRejection(context) is { } rejection) return rejection;
    if (friendMode) return Results.Conflict(new { code = "FriendMode", message = $"{area} are Host-only." });
    return Results.Json(await action());
}
app.MapPut("/api/local/settings", async (HostSettings settings) =>
{
    await modeGate.WaitAsync();
    ActionResult result;
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to My server first." });
        var validation = instance.ValidateSettings(settings);
        result = validation.Ok
            ? await manager.UpdateSettingsAsync(settings)
            : new ActionResult(false, validation.Code, validation.Message, await manager.SnapshotAsync());
    }
    finally { modeGate.Release(); }
    if (result.Ok)
    {
        pairing.ReconcileProfiles(result.Snapshot.Settings.Profiles.Select(profile => profile.Id));
        await companionServer.SyncAsync();
    }
    return Results.Json(result);
});
app.MapPut("/api/local/settings/control-policy", async (HostControlPolicyChange change) =>
{
    await modeGate.WaitAsync();
    ActionResult result;
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to My server first." });
        result = await manager.UpdateControlPolicyAsync(change);
    }
    finally { modeGate.Release(); }
    if (result.Ok) await companionServer.SyncAsync();
    return Results.Json(result);
});
app.MapPost("/api/local/data-recovery/acknowledge", async (DataRecoveryAcknowledgement request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode)
            return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to My server first." });
        var recovery = data.Recovery;
        if (recovery.Notices.Count == 0)
            return Results.Json(new
            {
                ok = true,
                code = "NoRecoveryRequired",
                message = "There is no local data recovery notice to acknowledge.",
                snapshot = await manager.SnapshotAsync()
            });
        if (recovery.LifecycleBlocked && !request.ConfirmNoManagedServersRunning)
            return Results.Json(new
            {
                ok = false,
                code = "ConfirmationRequired",
                message = "Confirm that no game server managed by TogetherServer is still running."
            });
        var snapshot = await manager.SnapshotAsync();
        if (recovery.LifecycleBlocked && snapshot.Runs.Any(run => run.State != "Offline"))
            return Results.Json(new
            {
                ok = false,
                code = "ManagedRunPresent",
                message = "Stop or resolve every recorded managed server before acknowledging data recovery.",
                snapshot
            });
        data.AcknowledgeRecovery();
        data.TryAudit($"{DateTimeOffset.UtcNow:O} local data recovery acknowledged after explicit no-running-server confirmation");
        return Results.Json(new
        {
            ok = true,
            code = "RecoveryAcknowledged",
            message = "Server controls are available again. The saved warning files were kept for review.",
            snapshot = await manager.SnapshotAsync()
        });
    }
    finally { modeGate.Release(); }
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
    await manager.SnapshotAsync(), games, companionServer.Active, pairing.Views(), companionServer.Warning,
    companionServer.ListenerState)));
app.MapGet("/api/local/network/routes", () => Results.Json(ConnectionRoutes.Detect()));
app.MapPost("/api/local/network/test-friend-route", async () =>
{
    if (friendMode)
    {
        latestRouteDiagnostic = new ExternalPortProbeResult("Unavailable",
            "Switch to My server before testing the Friend route.", 0, DateTimeOffset.UtcNow);
        return Results.Conflict(latestRouteDiagnostic);
    }
    var settings = (await manager.SnapshotAsync()).Settings;
    if (!companionServer.Active)
    {
        latestRouteDiagnostic = new ExternalPortProbeResult("Unavailable",
            "Start Friend app connections from Invite friends before testing the outside route.",
            settings.CompanionPort, DateTimeOffset.UtcNow);
        return Results.Json(latestRouteDiagnostic);
    }
    if (IPAddress.TryParse(settings.CompanionBindAddress, out var bindAddress) && IPAddress.IsLoopback(bindAddress))
    {
        latestRouteDiagnostic = new ExternalPortProbeResult("Unavailable",
            "Friend app connections are bound to this PC only. Use a LAN bind address or 0.0.0.0 for an outside route.",
            settings.CompanionPort, DateTimeOffset.UtcNow);
        return Results.Json(latestRouteDiagnostic);
    }
    latestRouteDiagnostic = await externalPortProbe.CheckAsync(settings.CompanionEndpoint, settings.CompanionPort);
    return Results.Json(latestRouteDiagnostic);
});
app.MapGet("/api/local/game-types", () => Results.Json(games.All.Select(game => new
{
    game.Kind,
    game.DisplayName
})));
app.MapPost("/api/local/profiles/{id:guid}/start", (Guid id) => HostOnly(() => manager.StartAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/stop", (Guid id) => HostOnly(() => manager.StopAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/restart", (Guid id) => HostOnly(() => manager.RestartAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/safe-restart", (Guid id) =>
    HostOnly(() => manager.SafeRestartAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/prepare-change", (Guid id) =>
    HostOnly(() => manager.PrepareServerChangeAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/finish-change", (Guid id, FinishServerChangeRequest request) =>
    HostOnly(() => manager.FinishServerChangeAsync(id, request.ConfirmedGameJoin)));
app.MapPost("/api/local/profiles/{id:guid}/countdown/extend", (Guid id, CountdownExtensionRequest request) =>
    HostOnly(() => manager.ExtendAutoShutdownAsync(id, request.Minutes)));
app.MapPost("/api/local/profiles/{id:guid}/health", (Guid id) => HostOnly(() => manager.HealthAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/players/refresh", (Guid id) =>
    HostOnly(() => manager.RefreshPlayerCountAsync(id)));
app.MapGet("/api/local/profiles/{id:guid}/files", (HttpContext context, Guid id) =>
    OwnerGet(context, () => manager.ServerFilesAsync(id), "Server files"));
app.MapGet("/api/local/profiles/{id:guid}/addons", (HttpContext context, Guid id) =>
    OwnerGet(context, () => manager.ServerAddOnsAsync(id), "Server add-ons"));
app.MapPost("/api/local/profiles/{id:guid}/addons/import", async (Guid id, ServerAddOnImportRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return Results.Conflict(new { code = "UpdatePending", message = "TogetherServer is restarting for an update." });
        if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Server add-ons are Host-only." });
        if (desktop is null) return Results.Conflict(new { code = "WindowUnavailable", message = "Open the TogetherServer window first." });
        var addons = await manager.ServerAddOnsAsync(id);
        if (!addons.Ok || addons.ImportType is not ("FactorioMod" or "BedrockPack"))
            return Results.Conflict(new { code = "AddOnUnsupported", message = "This server has no reviewed add-on import." });
        var bedrock = addons.ImportType == "BedrockPack";
        var selected = await desktop.PickFileAsync(bedrock ? "Choose a Bedrock world pack" : "Choose a Factorio mod ZIP",
            bedrock ? "Bedrock packs (*.mcpack)|*.mcpack" : "Factorio mods (*.zip)|*.zip");
        if (selected is null) return Results.Json(new { ok = false, code = "Canceled", message = "No add-on package selected." });
        return Results.Json(await manager.ImportServerAddOnAsync(id, selected, request));
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/profiles/{id:guid}/addons/state", (Guid id, ServerAddOnChangeRequest request) =>
    HostOnly(() => manager.SetServerAddOnAsync(id, request)));
app.MapPost("/api/local/profiles/{id:guid}/addons/undo", (Guid id, ServerAddOnUndoRequest request) =>
    HostOnly(() => manager.UndoServerAddOnAsync(id, request)));
app.MapPost("/api/local/profiles/{id:guid}/addons/review-version", (Guid id, ServerAddOnImportRequest request) =>
    HostOnly(() => manager.ReviewServerAddOnVersionAsync(id, request)));
app.MapGet("/api/local/profiles/{id:guid}/files/{key}", (HttpContext context, Guid id, string key) =>
    OwnerGet(context, () => manager.ReadServerFileAsync(id, key), "Server files"));
app.MapPost("/api/local/profiles/{id:guid}/folders/{key}/open", async (Guid id, string key) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return Results.Conflict(new { ok = false, code = "UpdatePending", message = "TogetherServer is restarting for an update." });
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Server folders are Host-only." });
        if (desktop is null) return Results.Conflict(new { ok = false, code = "WindowUnavailable", message = "Open the TogetherServer window first." });
        var folder = await manager.ServerFolderAsync(id, key);
        return Results.Json(folder is not null && desktop.OpenFolder(folder)
            ? new { ok = true, code = "FolderOpened", message = "Folder opened in File Explorer." }
            : new { ok = false, code = "FolderUnavailable", message = "This server folder is not available on this PC." });
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/profiles/{id:guid}/files/{key}/open", async (Guid id, string key) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return Results.Conflict(new { ok = false, code = "UpdatePending", message = "TogetherServer is restarting for an update." });
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Server files are Host-only." });
        if (desktop is null) return Results.Conflict(new { ok = false, code = "WindowUnavailable", message = "Open the TogetherServer window first." });
        var file = await manager.ServerTextFileAsync(id, key);
        return Results.Json(file is not null && desktop.OpenTextFile(file)
            ? new { ok = true, code = "FileOpened", message = "File opened in Notepad. Reload here after saving outside TogetherServer." }
            : new { ok = false, code = "FileUnavailable", message = "This reviewed text file is not available on this PC." });
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/profiles/{id:guid}/files/{key}/create", (Guid id, string key) =>
    HostOnly(() => manager.CreateServerConfigurationAsync(id, key)));
app.MapPut("/api/local/profiles/{id:guid}/files/{key}", (Guid id, string key, ServerFileChangeRequest request) =>
    HostOnly(() => manager.SaveServerFileAsync(id, key, request)));
app.MapPost("/api/local/profiles/{id:guid}/files/{key}/undo", (Guid id, string key, ServerFileUndoRequest request) =>
    HostOnly(() => manager.UndoServerFileAsync(id, key, request)));
app.MapGet("/api/local/profiles/{id:guid}/logs", async (HttpContext context, Guid id) =>
{
    if (!HasSensitiveLocalGetHeader(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (friendMode)
        return Results.Conflict(new ServerLogResult(false, "FriendMode",
            "Switch to Host mode to read local server logs.", ServerLogSourceStates.Unavailable,
            null, [], null, false));
    if (!ServerLogQueryParser.TryParse(context.Request.Query, out var query, out var error))
        return Results.BadRequest(error);
    var result = await serverLogs.ReadAsync(id, query, ServerLogAudience.Host);
    return result.Code == "UnknownProfile" ? Results.NotFound(result) : Results.Json(result);
});
app.MapGet("/api/local/profiles/{id:guid}/sessions", async (HttpContext context, Guid id) =>
{
    if (RecentSessionsGetRejection(context, out var limit) is { } rejection) return rejection;
    if (friendMode)
        return Results.Conflict(new
        {
            code = "FriendMode",
            message = "Recent session summaries are available only to the local Host owner."
        });
    var result = await manager.RecentSessionsAsync(id, limit);
    return result.Ok ? Results.Json(result) : Results.NotFound(result);
});
app.MapGet("/api/local/profiles/{id:guid}/acceptance", async (HttpContext context, Guid id) =>
{
    if (!HasSensitiveLocalGetHeader(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (context.Request.QueryString.HasValue ||
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        return Results.BadRequest(new { code = "InvalidAcceptanceRequest", message = "Acceptance review accepts no query or request body." });
    if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." });
    var snapshot = await manager.SnapshotAsync();
    return Results.Json(acceptanceRecorder.View(snapshot.Settings, id));
});
app.MapPut("/api/local/profiles/{id:guid}/acceptance", async (Guid id, AcceptanceChange change) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return Results.Conflict(new { code = "UpdatePending", message = "TogetherServer is restarting for an update." });
        if (friendMode) return Results.Conflict(new { code = "FriendMode", message = "Switch to Host mode first." });
        var snapshot = await manager.SnapshotAsync();
        return Results.Json(acceptanceRecorder.Change(snapshot.Settings, id, change));
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/profiles/{id:guid}/forget", (Guid id) => HostOnly(() => manager.ForgetAsync(id)));
app.MapGet("/api/local/profiles/{id:guid}/backups", async (Guid id) => friendMode
    ? Results.Conflict(new { ok = false, code = "FriendMode", message = "Backups are local-owner-only." })
    : Results.Json(await manager.BackupsAsync(id)));
app.MapGet("/api/local/profiles/{id:guid}/shared-world", async (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Conflict(new { code = "FriendMode" }) :
    Results.Json(await manager.SharedWorldStatusAsync(id)));
app.MapPut("/api/local/profiles/{id:guid}/shared-world", async (Guid id, SharedWorldConsentRequest request) =>
{
    if (friendMode) return Results.Conflict(new SharedWorldResult(false, "FriendMode", "Switch to Host mode first."));
    if (request.Enabled && !await manager.SharedRosterManagementAvailableAsync(id))
        return Results.Conflict(SuccessorRosterReadOnly());
    if (request.Enabled) pairing.RequireSharedRosterPublication(id);
    var result = await manager.SetSharedSavesAsync(id, request.Enabled);
    if (result.Ok && request.Enabled)
        await PublishRosterAndConfirmAsync(id);
    return Results.Json(result);
});
app.MapGet("/api/local/profiles/{id:guid}/shared-world/handoff",
    (HttpContext context, Guid id) => OwnerGet(context,
        () => manager.PlannedHandoffStatusAsync(id), "Planned handoff details"));
app.MapPost("/api/local/profiles/{id:guid}/shared-world/handoff/prepare",
    (Guid id, PreparePlannedHandoffRequest request) => HostOnly(() =>
        manager.PreparePlannedHandoffAsync(id, request.SuccessorDeviceId, request.SuccessorAddress)));
app.MapPost("/api/local/profiles/{id:guid}/shared-world/handoff/complete",
    (Guid id) => HostOnly(() => manager.CompletePlannedHandoffAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/shared-world/handoff/cancel",
    (Guid id) => HostOnly(() => manager.CancelPlannedHandoffAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/backups/manual", (Guid id) =>
    HostOnly(() => manager.CreateManualBackupAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/backups/setup", (Guid id) =>
    HostOnly(() => manager.CreateCompleteSetupBackupAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/backups/{backupId:guid}/restore-setup", (Guid id, Guid backupId) =>
    HostOnly(() => manager.RestoreCompleteSetupAsync(id, backupId)));
app.MapPost("/api/local/profiles/{id:guid}/backups/{backupId:guid}/verify", async (Guid id, Guid backupId) =>
    friendMode
        ? Results.Conflict(new { ok = false, code = "FriendMode", message = "Backup verification is local-owner-only." })
        : Results.Json(await manager.VerifyBackupAsync(id, backupId)));
app.MapPost("/api/local/profiles/{id:guid}/backups/{backupId:guid}/vault", async (Guid id, Guid backupId) =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    if (desktop is null) return Results.Conflict(new { ok = false, code = "WindowUnavailable", message = "Open the TogetherServer window to choose a backup-vault folder." });
    try
    {
        var destination = await desktop.PickFolderAsync("Choose an external drive or network folder for this verified backup");
        if (destination is null)
            return Results.Json(new BackupSafetyResult(false, "Canceled", "No backup-vault folder was selected.", backupId, DateTimeOffset.UtcNow));
        return Results.Json(await manager.CopyBackupToVaultAsync(id, backupId, destination));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return Results.Json(new BackupSafetyResult(false, "VaultCopyFailed",
            "The backup-vault folder could not be used. The local backup was kept unchanged.", backupId, DateTimeOffset.UtcNow));
    }
});
app.MapPost("/api/local/profiles/{id:guid}/backups/{backupId:guid}/move-kit", async (Guid id, Guid backupId) =>
{
    if (friendMode) return Results.Conflict(new HostMoveKitResult(false, "FriendMode", "Switch to Host mode first."));
    if (desktop is null) return Results.Conflict(new HostMoveKitResult(false, "WindowUnavailable", "Open the TogetherServer window to choose a move-kit folder."));
    try
    {
        var destination = await desktop.PickFolderAsync("Choose an external drive or network folder for the Host move kit");
        return Results.Json(destination is null
            ? new HostMoveKitResult(false, "Canceled", "No move-kit folder was selected.")
            : await manager.PrepareMoveKitAsync(id, backupId, destination));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return Results.Json(new HostMoveKitResult(false, "MoveKitFailed", "The move-kit folder could not be used."));
    }
});
app.MapPost("/api/local/move-kit/inspect", async () =>
{
    if (friendMode) return Results.Conflict(new HostMoveKitResult(false, "FriendMode", "Switch to Host mode first."));
    if (desktop is null) return Results.Conflict(new HostMoveKitResult(false, "WindowUnavailable", "Open the TogetherServer window to choose a move kit."));
    try
    {
        var directory = await desktop.PickFolderAsync("Choose a TogetherServer .backup move-kit folder");
        return Results.Json(directory is null
            ? new HostMoveKitResult(false, "Canceled", "No move kit was selected.")
            : manager.InspectMoveKit(directory));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return Results.Json(new HostMoveKitResult(false, "MoveKitInvalid", "The selected move kit could not be inspected."));
    }
});
app.MapPost("/api/local/profiles/{id:guid}/backups/{backupId:guid}/rehearse", async (Guid id, Guid backupId) =>
    friendMode
        ? Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." })
        : Results.Json(await manager.RehearseRestoreAsync(id, backupId)));
app.MapPost("/api/local/profiles/{id:guid}/backups/{backupId:guid}/restore", (Guid id, Guid backupId) =>
    HostOnly(() => manager.RestoreBackupAsync(id, backupId)));
app.MapPost("/api/local/profiles/{id:guid}/resume-hosting", (Guid id) => HostOnly(async () =>
{
    var snapshot = await manager.SnapshotAsync();
    if (!startupRecovery.CanResume(id))
        return new ActionResult(false, "ResumeUnavailable",
            "This server was not recorded as active in the interrupted app session.", snapshot);
    var run = snapshot.Runs.SingleOrDefault(item => item.ProfileId == id);
    if (run?.State == "Failed")
    {
        var archived = await manager.ForgetAsync(id);
        if (!archived.Ok) return archived;
    }
    else if (run is not null && run.State != "Offline")
        return new ActionResult(false, "ResumeBlocked",
            run.State is "Ready" or "Starting" or "Process running"
                ? "TogetherServer already reattached to the exact managed process; another copy was not started."
                : "The prior process identity needs local review before hosting can resume.", snapshot);
    return await manager.StartAsync(id);
}));
app.MapPost("/api/local/profiles/{id:guid}/password", (Guid id, ValheimPasswordRequest request) =>
    HostOnly(() => manager.SetValheimPasswordAsync(id, request.Password)));
app.MapPut("/api/local/profiles/{id:guid}/custom-scripts", (Guid id, CustomScriptBundle scripts) =>
    HostOnly(() => manager.SetCustomScriptsAsync(id, scripts)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/begin", (Guid id) =>
    HostOnly(() => manager.BeginCustomCertificationAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/status", (Guid id) =>
    HostOnly(() => manager.CheckCustomCertificationAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/confirm", (Guid id) =>
    HostOnly(() => manager.ConfirmCustomCertificationAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/cancel", (Guid id) =>
    HostOnly(() => manager.CancelCustomCertificationAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/custom-certification/revoke", (Guid id) =>
    HostOnly(() => manager.RevokeCustomCertificationAsync(id)));
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
        return Results.Json(new
        {
            ok = true,
            code = scripts is null ? "CustomScriptsEmpty" : "CustomScriptsLoaded",
            message = scripts is null ? "No custom scripts have been saved yet." : "Custom scripts loaded from Windows protected storage.",
            scripts = scripts ?? new CustomScriptBundle("", "", "")
        });
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
app.MapGet("/api/local/valheim/discover", async () =>
{
    var discovery = instance.FreshWorldsOnly
        ? ValheimSetup.Scan(includeWorlds: false)
        : friendMode
        ? ValheimSetup.Scan()
        : ValheimSetup.Scan((await manager.SnapshotAsync()).Settings.Profiles
            .Where(profile => profile.Kind == "Valheim").Select(profile => profile.WorldDirectory));
    return Results.Json(instance.FreshWorldsOnly
        ? new ValheimDiscoveryResult(discovery.Installations, [])
        : discovery);
});
app.MapGet("/api/local/minecraft/discover", async (string? folder) =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    var profiles = (await manager.SnapshotAsync()).Settings.Profiles;
    if (instance.FreshWorldsOnly && !string.IsNullOrWhiteSpace(folder))
        return Results.Conflict(new { ok = false, code = "StagingFreshWorldRequired", message = "Development does not scan production Minecraft server folders." });
    return Results.Json(instance.FreshWorldsOnly
        ? MinecraftSetup.ScanManaged(data, profiles)
        : MinecraftSetup.Scan(data, profiles, folder));
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
app.MapPost("/api/local/factorio/browse-executable", async () =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    if (desktop is null) return Results.Conflict(new { ok = false, code = "WindowUnavailable", message = "Open the TogetherServer window to browse files." });
    try
    {
        var path = await desktop.PickFileAsync("Choose owner-installed Factorio server",
            "Factorio server (factorio.exe)|factorio.exe|Applications (*.exe)|*.exe");
        if (path is null) return Results.Json(new { ok = false, code = "Canceled", message = "No server executable selected.", path = (string?)null });
        if (!File.Exists(path) || !Path.GetFileName(path).Equals("factorio.exe", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { ok = false, code = "InvalidServerExecutable", message = "Choose factorio.exe from an owner-installed Factorio server.", path = (string?)null });
        return Results.Json(new { ok = true, code = "PathSelected", message = "Factorio executable selected. Save setup before starting.", path = (string?)path });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, code = "BrowseFailed", message = "Could not open the Windows file picker: " + ex.Message, path = (string?)null }); }
});
app.MapPost("/api/local/factorio/import-save", async (FactorioImportRequest request) =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    if (instance.FreshWorldsOnly) return Results.Conflict(new FactorioImportResult(false,
        "StagingFactorioDisabled", "Factorio preview is unavailable in fresh-world-only staging."));
    if (desktop is null) return Results.Conflict(new { ok = false, code = "WindowUnavailable", message = "Open the TogetherServer window to browse files." });
    try
    {
        var path = await desktop.PickFileAsync("Choose an existing Factorio save to copy",
            "Factorio saves (*.zip)|*.zip");
        return Results.Json(path is null
            ? new FactorioImportResult(false, "Canceled", "No Factorio save was selected.")
            : FactorioSetup.ImportCopy(data, request.ProfileId, path));
    }
    catch (Exception ex) { return Results.Json(new FactorioImportResult(false, "BrowseFailed", "Could not open the Windows file picker: " + ex.Message)); }
});
app.MapPost("/api/local/terraria/browse-executable", async () =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first.", path = (string?)null });
    if (desktop is null) return Results.Conflict(new { ok = false, code = "WindowUnavailable", message = "Open the TogetherServer window first.", path = (string?)null });
    try
    {
        var path = await desktop.PickFileAsync("Choose owner-installed Terraria server",
            "Terraria server (TerrariaServer.exe)|TerrariaServer.exe|Applications (*.exe)|*.exe");
        return Results.Json(path is null
            ? new { ok = false, code = "Canceled", message = "No server executable was selected.", path = (string?)null }
            : new { ok = true, code = "PathSelected", message = "Terraria server executable selected.", path = (string?)path });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    { return Results.Json(new { ok = false, code = "BrowseFailed", message = "Could not choose the Terraria executable.", path = (string?)null }); }
});
app.MapPost("/api/local/terraria/import-world", async (TerrariaImportRequest request) =>
{
    if (friendMode) return Results.Conflict(new TerrariaImportResult(false, "FriendMode", "Switch to Host mode first."));
    if (instance.FreshWorldsOnly) return Results.Conflict(new TerrariaImportResult(false,
        "StagingTerrariaDisabled", "Terraria preview is unavailable in fresh-world-only staging."));
    if (desktop is null) return Results.Conflict(new TerrariaImportResult(false, "WindowUnavailable", "Open the TogetherServer window first."));
    try
    {
        var path = await desktop.PickFileAsync("Choose an existing Terraria world to copy", "Terraria worlds (*.wld)|*.wld");
        return Results.Json(path is null
            ? new TerrariaImportResult(false, "Canceled", "No Terraria world was selected.")
            : TerrariaSetup.ImportCopy(data, request.ProfileId, path));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    { return Results.Json(new TerrariaImportResult(false, "BrowseFailed", "Could not choose the Terraria world.")); }
});
app.MapPost("/api/local/minecraft/browse", async (MinecraftBrowseRequest request) =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    if (instance.FreshWorldsOnly)
        return Results.Conflict(new { ok = false, code = "StagingFreshWorldRequired", message = "Development installs and keeps a separate Minecraft server instead of opening a production server folder." });
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
    if (instance.FreshWorldsOnly)
        return Results.Conflict(new { ok = false, code = "StagingCustomDisabled", message = "Custom scripts are disabled in staging to protect files outside its isolated data folder." });
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
    if (instance.FreshWorldsOnly)
        return Results.Conflict(new WorldFileSelection(false, "StagingFreshWorldRequired", "Development cannot open or copy an existing production Valheim world.", null, null));
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
    if (instance.FreshWorldsOnly)
        return Results.Conflict(new WorldFileSelection(false, "StagingFreshWorldRequired", "Development cannot open or copy an existing production Valheim world folder.", null, null));
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
        if (instance.FreshWorldsOnly)
            return Results.Json(new ImportWorldResult(false, "StagingFreshWorldRequired",
                "Development cannot copy an existing production Valheim world. Create a persistent world in separate development storage.", null));
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
        return Results.Json(new
        {
            ok = true,
            code = "ModeChanged",
            message = friendMode
            ? "Showing your connected Hosts. Your own server and Friend access keep running."
            : "Showing your server. Connections to other Hosts keep running."
        });
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/quit", async (HttpContext context) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (shutdownPending)
            return Results.Json(new { ok = true, code = "Closing", message = "TogetherServer is closing." });
        if ((await manager.SnapshotAsync()).Runs.Any(run => run.State != "Offline"))
            return Results.Json(new
            {
                ok = false,
                code = "ManagedRunPresent",
                message = "Stop or resolve every managed server before quitting TogetherServer."
            });
        shutdownPending = true;
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
    return Results.Json(new
    {
        listenerActive = companionServer.Active,
        listenerState = companionServer.ListenerState,
        listenerWarning = companionServer.Warning,
        endpoint = snapshot.Settings.CompanionEndpoint,
        fingerprint,
        certificates = identity.State(),
        route = ConnectionRoutes.Normalize(snapshot.Settings.ConnectionRoute),
        devices = pairing.Views(),
        stopSafety = companionServer.StopSafety(snapshot)
    });
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
        data.TryAudit($"certificate-stage {state.NextFingerprint} {DateTimeOffset.UtcNow:O}");
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
        data.TryAudit($"certificate-activate {state.ActiveFingerprint} {DateTimeOffset.UtcNow:O}");
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
        data.TryAudit($"certificate-retire-previous {DateTimeOffset.UtcNow:O}");
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
        current = pairing.CurrentServerInvite(profileId, snapshot.Settings.CompanionEndpoint,
            HostIdentity.Fingerprint(currentCertificate));
    }
    return Results.Json(new
    {
        ok = true,
        exists = current is not null,
        open = current?.Open ?? false,
        password = current?.Open == true ? PairingPassword.Encode(current.Invitation) : null,
        canStart = current?.CanStart ?? true,
        canViewLogs = current?.CanViewLogs ?? false,
        expiresUtc = (DateTimeOffset?)null,
        durationMinutes = 30,
        deviceLimit = 1,
        activatedDevices = current?.ActivatedDevices ?? 0,
        requireApproval = current?.RequireApproval ?? false
    });
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
                return Results.BadRequest(new
                {
                    ok = false,
                    code = "AddressUnavailable",
                    message = route.Mode == ConnectionRouteModes.DirectInternet
                    ? "Check this PC's public IP address first." : "Choose an address for the selected route first."
                });
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
                settings.CompanionEndpoint, HostIdentity.Fingerprint(certificate), request.Refresh,
                request.DurationMinutes, request.DeviceLimit, request.RequireApproval, request.CanViewLogs);
            password = PairingPassword.Encode(invite);
            if (request.Refresh) await RepairDirtyRostersAsync();
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
    return Results.Json(new
    {
        ok = true,
        code = request.Refresh ? "InviteRefreshed" : "InviteReady",
        message = request.Refresh ? "The old server code no longer works. PCs that used it must connect again with this code."
            : "Server code ready. It stays active until you replace it.",
        password,
        expiresUtc = (DateTimeOffset?)null,
        listenerActive = companionServer.Active,
        listenerWarning = companionServer.Warning
    });
});
app.MapPost("/api/local/servers/{profileId:guid}/pairing/close", async (Guid profileId) =>
{
    await modeGate.WaitAsync();
    PairingDecision result;
    try { result = friendMode ? new(false, "FriendMode", "Switch to Host mode first.") : pairing.ClosePairing(profileId); }
    finally { modeGate.Release(); }
    if (result.Ok) await companionServer.SyncAsync();
    return Results.Json(result);
});
app.MapPost("/api/local/servers/{profileId:guid}/pairing/emergency-revoke", async (Guid profileId) =>
{
    await modeGate.WaitAsync();
    PairingDecision result;
    try
    {
        result = friendMode ? new(false, "FriendMode", "Switch to Host mode first.") : pairing.EmergencyRevoke(profileId);
        if (result.Ok) await RepairDirtyRostersAsync();
    }
    finally { modeGate.Release(); }
    if (result.Ok) await companionServer.SyncAsync();
    return Results.Json(result);
});
app.MapPost("/api/local/devices/{id:guid}/revoke", async (Guid id) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode" });
        var result = pairing.Revoke(id);
        if (result.Ok) await RepairDirtyRostersAsync();
        return Results.Json(result);
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/devices/{id:guid}/approve", async (Guid id) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode" });
        var result = pairing.Approve(id);
        if (result.Ok) await RepairDirtyRostersAsync();
        return Results.Json(result);
    }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/shared-world/{profileId:guid}",
    async (Guid id, Guid profileId, SharedWorldGrantRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode" });
        if (!await manager.SharedRosterManagementAvailableAsync(profileId))
            return Results.Conflict(SuccessorRosterReadOnly());
        var status = await manager.SharedWorldStatusAsync(profileId);
        if (request.Enabled && !status.Enabled)
            return Results.Conflict(new { code = "SharingOff", message = "Enable sharing for this server first." });
        var result = pairing.SetReceiveSaves(id, profileId, request.Enabled);
        if (result.Ok && status.Enabled) await RepairDirtyRostersAsync();
        return Results.Json(result);
    }
    finally { modeGate.Release(); }
});
app.MapGet("/api/local/profiles/{profileId:guid}/shared-world/governance", async (HttpContext context, Guid profileId) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Conflict(new { code = "FriendMode" }) :
    pairing.SharedRosterDirty(profileId) ? Results.Conflict(new { code = "RosterUnavailable" }) :
        Results.Json(await manager.SharedWorldRosterAsync(profileId)));
app.MapPut("/api/local/profiles/{profileId:guid}/shared-world/governance",
    async (Guid profileId, SharedWorldGovernanceRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode" });
        if (!await manager.SharedRosterManagementAvailableAsync(profileId))
            return Results.Conflict(SuccessorRosterReadOnly());
        pairing.RequireSharedRosterPublication(profileId);
        return Results.Json(await PublishRosterAndConfirmAsync(profileId,
            request.OwnerOverride, request.ReviewSourceChange));
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/shared-world/repair-rosters", async () =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode" });
        await RepairDirtyRostersAsync();
        foreach (var shared in data.LoadSettings().Profiles.Where(profile =>
            profile.SharedSavesEnabled && pairing.SharedRosterDirty(profile.Id)))
            if (!await manager.SharedRosterManagementAvailableAsync(shared.Id))
                return Results.Conflict(new { ok = false, code = "SuccessorGovernanceUnresolved",
                    message = "Signed membership needs review before this PC can share or vote again." });
        return Results.Json(new { ok = true, code = "RostersRepaired" });
    }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/shared-world/{profileId:guid}/grants",
    async (Guid id, Guid profileId, SharedWorldDeviceGrantsRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode" });
        if (!await manager.SharedRosterManagementAvailableAsync(profileId))
            return Results.Conflict(SuccessorRosterReadOnly());
        if (!(await manager.SharedWorldStatusAsync(profileId)).Enabled)
            return Results.Conflict(new { code = "SharingOff" });
        var result = pairing.SetSharedWorldGrants(id, profileId, request.Grants);
        if (result.Ok) await RepairDirtyRostersAsync();
        return Results.Json(result);
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/devices/{id:guid}/shared-world/re-enroll", async (Guid id) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode" });
        foreach (var shared in data.LoadSettings().Profiles.Where(profile => profile.SharedSavesEnabled))
            if (!await manager.SharedRosterManagementAvailableAsync(shared.Id))
                return Results.Conflict(SuccessorRosterReadOnly());
        var result = pairing.ResetSharedWorldKey(id);
        if (result.Ok) await RepairDirtyRostersAsync();
        return Results.Json(result);
    }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/access-expiry", async (Guid id, DeviceAccessExpiryRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode)
            return Results.Conflict(new DeviceAccessExpiryResult(false, "FriendMode",
                "Switch to Host mode first."));
        var result = pairing.SetAccessExpiry(id, request);
        if (result.Ok) await RepairDirtyRostersAsync();
        return result.Ok ? Results.Json(result) : result.Code switch
        {
            "UnknownDevice" => Results.NotFound(result),
            "Revoked" => Results.Conflict(result),
            _ => Results.BadRequest(result)
        };
    }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/temporary-helper", async (Guid id, TemporaryHelperRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode)
            return Results.Conflict(new TemporaryHelperResult(false, "FriendMode", "Switch to Host mode first."));
        var result = pairing.SetTemporaryHelper(id, request);
        return result.Ok ? Results.Json(result) : result.Code switch
        {
            "UnknownDevice" => Results.NotFound(result),
            "Revoked" or "AccessUnavailable" => Results.Conflict(result),
            _ => Results.BadRequest(result)
        };
    }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/permissions", async (Guid id, DevicePermissionRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        return friendMode ? Results.Conflict(new { ok = false, code = "FriendMode" }) :
        Results.Json(pairing.SetPermissions(id, request.CanStart, request.CanStop, request.Scope,
            request.CanExtendTimer, request.CanViewLogs));
    }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/servers", async (Guid id, DeviceServerAccessRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode" });
        var result = pairing.SetServerAccess(id, request.ProfileIds, request.Permissions,
            data.LoadSettings().Profiles.Select(profile => profile.Id));
        if (result.Ok) await RepairDirtyRostersAsync();
        return Results.Json(result);
    }
    finally { modeGate.Release(); }
});
app.MapPut("/api/local/devices/{id:guid}/name", async (Guid id, DeviceNameRequest request) =>
{
    await modeGate.WaitAsync();
    try
    {
        return friendMode ? Results.Conflict(new { ok = false, code = "FriendMode" }) :
        Results.Json(pairing.SetName(id, request.Name));
    }
    finally { modeGate.Release(); }
});
app.MapPost("/api/local/friend/pair", async (FriendPairRequest request) =>
    friendMode ? Results.Json(await friend.PairAsync(request.Invitation, request.HostAddress))
    : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/connections/{id:guid}/select", (Guid id) =>
    friendMode ? Results.Json(friend.Select(id)) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPut("/api/local/friend/connections/{id:guid}/name", async (Guid id, DeviceNameRequest request) =>
    friendMode ? Results.Json(await friend.RenameAsync(id, request.Name)) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/connections/{id:guid}/forget", async (Guid id) =>
    friendMode ? Results.Json(await friend.ForgetAsync(id)) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPut("/api/local/friend/connections/{id:guid}/endpoint", async (Guid id, EndpointRecoveryRequest request) =>
    friendMode ? Results.Json(await friend.RecoverEndpointAsync(id, request.Endpoint)) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/poll", async () =>
    friendMode ? Results.Json(await friend.PollAsync()) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/probe-game", async (Guid id) =>
    friendMode ? Results.Json(await friend.ProbeGameEndpointAsync(id)) : Results.Conflict(new { ok = false, code = "HostMode" }));
app.MapGet("/api/local/friend/{id:guid}/shared-world", (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Json(friend.SharedWorldStatus(id)) : Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/readiness", (Guid id, TakeoverLocalSetup setup) =>
    friendMode ? Results.Json(friend.CheckTakeoverReadiness(id, setup, false)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/route-check",
    async (HttpContext context, Guid id, SharedWorldRouteRequest request) =>
    friendMode ? Results.Json(await friend.ProbeSuccessorRouteAsync(id,
        request.RecordHash, request.TlsFingerprint, context.RequestAborted)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/rehearse", (Guid id, TakeoverLocalSetup setup) =>
    friendMode ? Results.Json(friend.CheckTakeoverReadiness(id, setup, true)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPut("/api/local/friend/{id:guid}/shared-world/consent", async (Guid id, SharedWorldConsentRequest request) =>
    friendMode ? Results.Json(await friend.SetSharedWorldConsentAsync(id, request.Enabled)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/pull", async (HttpContext context, Guid id) =>
    friendMode ? Results.Json(await friend.PullSharedWorldAsync(id, context.RequestAborted)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/handoff/stage",
    async (HttpContext context, Guid id) =>
    friendMode ? Results.Json(await friend.StagePlannedHandoffAsync(id, context.RequestAborted)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapGet("/api/local/friend/{id:guid}/shared-world/handoff/restore", async (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Json(await manager.SuccessorRestoreStatusAsync(id)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/handoff/restore",
    async (HttpContext context, Guid id, SuccessorRestoreRequest request) =>
    friendMode ? Results.Json(await manager.RestoreSharedSuccessorAsync(id,
        request, context.RequestAborted)) : Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/check", async (HttpContext context, Guid id) =>
    friendMode ? Results.Json(await friend.CheckSharedWorldAsync(id, context.RequestAborted)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/recovery/offer", async (Guid id) =>
    friendMode ? Results.Json(await friend.PrepareRecoveryOfferAsync(id)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapGet("/api/local/friend/{id:guid}/shared-world/recovery", (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Json(new SharedWorldVoteInbox(data).Status(id, friend.RecoveryDeviceId(id))) :
    Results.Conflict(new { code = "HostMode" }));
app.MapGet("/api/local/friend/{id:guid}/shared-world/recovery/offer-code", (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    !friendMode ? Results.Conflict(new { code = "HostMode" }) :
    new SharedWorldVoteInbox(data).Armed(id) is { } offer ? Results.Json(new
    {
        proposalHash = WorldAuthorityTrust.ProposalHash(offer.Proposal), offer
    }) :
    Results.NotFound(new { code = "NoArmedOffer", message = "No signed offer is armed on this PC." }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/recovery/vote", async (HttpContext context, Guid id) =>
{
    if (!friendMode) return Results.Conflict(new { code = "HostMode" });
    var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
        context.Request.ContentLength, 512 * 1024, context.RequestAborted);
    if (bytes is null) return Results.BadRequest(new { code = "InvalidRecoveryOffer" });
    try
    {
        var offer = JsonSerializer.Deserialize<WorldAuthorityOffer>(bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return offer is null ? Results.BadRequest(new { code = "InvalidRecoveryOffer" }) :
            Results.Json(await friend.VoteOnRecoveryOfferAsync(id, offer, context.RequestAborted));
    }
    catch (JsonException) { return Results.BadRequest(new { code = "InvalidRecoveryOffer" }); }
});
app.MapPost("/api/local/friend/{id:guid}/shared-world/recovery/separate",
    async (Guid id, WorldSeparateCopyConfirmation confirmation) =>
    friendMode ? Results.Json(await friend.DeclareSeparateCopyAsync(id, confirmation.AcceptSplitWarning)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapGet("/api/local/friend/{id:guid}/logs", async (HttpContext context, Guid id) =>
{
    if (!HasSensitiveLocalGetHeader(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!friendMode)
        return Results.Conflict(new ServerLogResult(false, "HostMode",
            "Switch to Join to read logs shared by a Host.", ServerLogSourceStates.Unavailable,
            null, [], null, false));
    if (!ServerLogQueryParser.TryParse(context.Request.Query, out var query, out var error))
        return Results.BadRequest(error);
    return Results.Json(await friend.ReadLogsAsync(id, query, context.RequestAborted));
});
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

DiagnosticOutput.WriteLine($"{instance.DisplayName} {(friendMode ? "Friend" : "Host")} local GUI: http://127.0.0.1:{port}/");
await companionServer.SyncAsync();
var background = new AppBackgroundTasks(friend, manager, companionServer, updater,
    instance, data, desktop, () => friendMode);
background.Start();
if (desktop is not null)
{
    app.Lifetime.ApplicationStarted.Register(desktop.Start);
    app.Lifetime.ApplicationStopping.Register(desktop.Exit);
}
app.Lifetime.ApplicationStopping.Register(startupRecovery.MarkClean);
try { await app.RunAsync(); }
catch (Exception ex) when (openWindow)
{
    DesktopLaunch.ShowError("TogetherServer could not start its local GUI.\n\n" + ex.Message);
    Environment.ExitCode = 1;
}
finally { await background.DisposeAsync(); await companionServer.StopAsync(); }

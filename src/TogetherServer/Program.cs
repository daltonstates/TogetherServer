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
var games = new GameServerRegistry(data, includeFixture: instance.IsStaging);
var pairing = new PairingService(data);
pairing.ReconcileProfiles(data.LoadSettings().Profiles.Select(profile => profile.Id));
using var hostingPower = new WindowsHostingPowerGuard();
var startupRecovery = new StartupRecoveryService(data, data.LoadRuns(), Environment.ProcessPath ?? "");
var manager = new HostManager(data, games, TimeProvider.System, hostingPower, startupRecovery, pairing);
if (instance.IsStaging) manager.EnableStagingLiveFixture();
await manager.RecoverPendingBedrockResumeAsync();
async Task<SharedWorldRoster> PublishRosterAndConfirmAsync(Guid profileId,
    bool? ownerOverride = null, bool reviewSourceChange = false,
    SharedWorldOwnerEdit? ownerEdit = null)
{
    var roster = await manager.PublishSharedWorldRosterAsync(profileId,
        pairing.SharedRosterMembers(profileId), ownerOverride, reviewSourceChange, ownerEdit);
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
object SuccessorRosterReadOnly() => new
{
    ok = false,
    code = "SuccessorRosterReadOnly",
    message = "Sharing permissions stay with the original owner. This successor PC may host and share hash-verified post-Stop file copies after local setup and route checks pass. It cannot change the signed member list. Game load has not been checked."
};
try { if (!friendMode) await RepairDirtyRostersAsync(); }
catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException)
{ data.TryAudit($"shared-roster-repair-pending {ex.GetType().Name} {DateTimeOffset.UtcNow:O}"); }
var acceptanceRecorder = new AcceptanceRecorder(data, TimeProvider.System);
var updateCheckpoints = new StateCheckpointService(data, TimeProvider.System);
var serverLogs = new ServerLogService(data, manager);
var serverChat = new ServerChat(data);
var uiDrafts = new ProtectedUiDraftStore(data);
var notificationPreferences = new DesktopNotificationPreferences(data);
var qolDesktop = new QolLocalPreferences(data);
var updateUi = new UpdateUiPreferences(data);
var importSelections = new SetupImportSelections(data, instance.FreshWorldsOnly);
var identity = new HostIdentity(data);
using var friend = new FriendService(data);
using var updateClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
var appVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
var updater = new AppUpdater(updateClient, root, Environment.ProcessPath ?? "", appVersion,
    enabled: instance.UpdatesAvailable,
    disabledMessage: "Automatic updates are disabled in staging. Rebuild or replace the staging package explicitly.",
    uiPreferences: updateUi);
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
    friend.CurrentRecoveryHostLoss, serverChat: serverChat);
manager.CompanionListenerOwnershipProbe = companionServer.OwnsListener;
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
var app = builder.Build();
var desktop = openWindow ? new DesktopWindow(new Uri($"http://127.0.0.1:{port}/"), root,
    app.Lifetime.StopApplication, desktopPreferences.CloseToTray, startupLaunch,
    instance.DisplayName, instance.IsStaging, localPreferences: qolDesktop,
    notifications: notificationPreferences) : null;
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
app.MapGet("/api/local/diagnostics", (Func<HttpContext, Task<IResult>>)(async context =>
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
}));
app.MapGet("/api/local/support-report", (Func<HttpContext, Task<IResult>>)(async context =>
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
}));
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
IResult UpdatePreparationBlocked(string code, string message)
{
    updater.ReportPreparation(code == "AlreadyUpdating" ? "Restarting" : "Blocked", message,
        code == "AlreadyUpdating" ? null : message);
    return Results.Json(new UpdateResult(false, code, message));
}
app.MapPost("/api/local/update/install", (Func<HttpContext, Task<IResult>>)(async context =>
{
    if (!HasSensitiveLocalGetHeader(context)) return Results.StatusCode(403);
    if (context.Request.QueryString.HasValue)
        return Results.BadRequest(new UpdateResult(false, "InvalidUpdateRequest", "Update accepts no query or caller-selected fields."));
    var input = await FeatureBody(context, 1);
    if (input is null || input.Length != 0)
        return Results.BadRequest(new UpdateResult(false, "InvalidUpdateRequest", "Update accepts no caller-selected fields."));
    if (!instance.UpdatesAvailable)
        return UpdatePreparationBlocked("UpdatesDisabled",
            "Automatic updates are disabled in staging. Rebuild or replace the staging package explicitly.");
    if (desktop is null) return UpdatePreparationBlocked("WindowUnavailable", "Open the published app window to update.");
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return UpdatePreparationBlocked("AlreadyUpdating", "The app is already restarting.");
        if (shutdownPending) return UpdatePreparationBlocked("ShutdownPending", "TogetherServer is closing.");
        if ((await manager.SnapshotAsync()).Runs.Any(run => run.State != "Offline"))
            return UpdatePreparationBlocked("ManagedRunPresent", "Stop or resolve every hosted server before installing the update.");
    }
    finally { modeGate.Release(); }
    var prepared = await updater.PrepareAsync();
    if (!prepared.Ok) return Results.Json(prepared);
    await modeGate.WaitAsync();
    try
    {
        if (updatePending) return UpdatePreparationBlocked("AlreadyUpdating", "The app is already restarting.");
        if (shutdownPending) return UpdatePreparationBlocked("ShutdownPending", "TogetherServer is closing.");
        if ((await manager.SnapshotAsync()).Runs.Any(run => run.State != "Offline"))
            return UpdatePreparationBlocked("ManagedRunPresent", "Stop or resolve every hosted server before installing the update.");
        var targetVersion = updater.PreparedVersion;
        if (targetVersion is null)
            return UpdatePreparationBlocked("NotReady", "No verified update is ready.");
        updater.ReportPreparation("Checkpoint", "Protecting local settings before restart.");
        var checkpoint = updateCheckpoints.Create(appVersion.ToString(3), targetVersion,
            Environment.ProcessPath ?? "");
        if (!checkpoint.Ok || checkpoint.Checkpoint is null)
        {
            updater.ReportPreparation("Failed", checkpoint.Message, checkpoint.Message);
            return Results.Json(new UpdateResult(false, checkpoint.Code, checkpoint.Message));
        }
        var started = updater.StartReplacement(checkpoint.Checkpoint);
        if (started.Ok)
        {
            updater.ReportPreparation("Restarting", "TogetherServer is closing to install the verified update.");
            updatePending = true;
            context.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
        }
        return Results.Json(started);
    }
    finally { modeGate.Release(); }
}));
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
async Task<IResult> FeatureRole(HttpContext context, bool host, Func<Task<IResult>> action)
{
    if (!HasSensitiveLocalGetHeader(context)) return Results.StatusCode(403);
    if (context.Request.QueryString.HasValue || context.Request.Method == "GET" &&
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        return Results.BadRequest(new { code = "InvalidFeatureRequest", message = "Use only this feature's fixed reviewed fields." });
    if (!await modeGate.WaitAsync(TimeSpan.FromSeconds(4), context.RequestAborted))
        return Results.Conflict(new { code = "FeatureBusy", message = "Another local action is still running. Retry this feature when it finishes." });
    try
    {
        if (updatePending) return Results.Conflict(new { code = "UpdatePending", message = "TogetherServer is restarting for an update." });
        if (host == friendMode) return Results.Conflict(new { code = host ? "FriendMode" : "HostMode", message = host ? "Open Host to manage this feature." : "Open Join to use this feature." });
        return await action();
    }
    finally { modeGate.Release(); }
}
static async Task<byte[]?> FeatureBody(HttpContext context, int maximum)
{
    if (context.Request.ContentLength > maximum) return null;
    using var output = new MemoryStream(); var buffer = new byte[Math.Min(4096, maximum + 1)];
    while (true)
    {
        var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - (int)output.Length + 1)), context.RequestAborted);
        if (read == 0) return output.ToArray();
        if (output.Length + read > maximum) return null;
        output.Write(buffer, 0, read);
    }
}
async Task<IResult> ProtectedDraftAction(HttpContext context, string action)
{
    if (!HasSensitiveLocalGetHeader(context) || context.Request.QueryString.HasValue)
        return Results.StatusCode(403);
    var bytes = await FeatureBody(context, ProtectedUiDraftStore.MaximumFileDraftBytes * 6 + 2048);
    if (bytes is null) return Results.BadRequest(new { code = "InvalidDraft", message = "The local draft request is too large." });
    ProtectedUiDraftIdentity identity;
    ProtectedUiDraftSave? save = null;
    ProtectedUiDraftClear? clear = null;
    try
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        if (action == "save") { save = ProtectedUiDraftStore.ParseSave(document.RootElement); identity = save.Identity; }
        else if (action == "clear") { clear = ProtectedUiDraftStore.ParseClear(document.RootElement); identity = clear.Identity; }
        else identity = ProtectedUiDraftStore.ParseIdentity(document.RootElement);
    }
    catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
    { return Results.BadRequest(new { code = "InvalidDraft", message = "Use the fixed reviewed draft fields and limits." }); }
    return await FeatureRole(context, identity.ConnectionId is null, () =>
    {
        if (identity.ConnectionId is null)
        {
            var setup = identity.ProfileId == Guid.Empty && identity.Purpose == "settings" && identity.Key == "host-setup";
            if (!setup)
            {
                var profile = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == identity.ProfileId);
                if (profile is null)
                    return Task.FromResult<IResult>(Results.NotFound(new { code = "UnknownProfile", message = "This saved server is no longer available." }));
                if (!QolLocalEndpointInputs.DraftKeyMatches(identity, profile))
                    return Task.FromResult<IResult>(Results.BadRequest(new { code = "DraftScopeChanged", message = "Choose this server's reviewed file, settings, player list or chat draft." }));
            }
        }
        else
        {
            var current = friend.View();
            if (identity.Purpose != "chat" || identity.Key != "compose" || current.ConnectionId != identity.ConnectionId ||
                !current.Profiles.Any(profile => profile.Id == identity.ProfileId) &&
                current.ChatProfiles?.Any(profile => profile.Id == identity.ProfileId) != true)
                return Task.FromResult<IResult>(Results.Conflict(new { code = "DraftScopeChanged", message = "Open the saved Host and room before recovering its draft." }));
        }
        var result = save is not null ? uiDrafts.Save(identity, save.Text, save.ExpectedRevision) :
            clear is not null ? uiDrafts.Clear(identity, clear.ExpectedRevision) : uiDrafts.Read(identity);
        return Task.FromResult<IResult>(Results.Json(result));
    });
}
app.MapPost("/api/local/ui-drafts/read", (Func<HttpContext, Task<IResult>>)(context => ProtectedDraftAction(context, "read")));
app.MapPut("/api/local/ui-drafts", (Func<HttpContext, Task<IResult>>)(context => ProtectedDraftAction(context, "save")));
app.MapPost("/api/local/ui-drafts/clear", (Func<HttpContext, Task<IResult>>)(context => ProtectedDraftAction(context, "clear")));

bool OwnsNotificationScope(DesktopNotificationPreferenceChange change)
{
    if (change.ProfileId is not { } profileId) return true;
    if (change.ConnectionId is not { } connectionId)
        return !friendMode && data.LoadSettings().Profiles.Any(profile => profile.Id == profileId);
    var view = friend.View();
    return friendMode && view.Connections?.Any(connection => connection.ConnectionId == connectionId &&
        connection.Profiles.Any(profile => profile.Id == profileId)) == true;
}
app.MapGet("/api/local/desktop/notifications", (Func<HttpContext, Task<IResult>>)(context =>
    FeatureRole(context, !friendMode, () => Task.FromResult<IResult>(Results.Json(notificationPreferences.View())))));
app.MapPut("/api/local/desktop/notifications", (Func<HttpContext, Task<IResult>>)(context =>
    FeatureRole(context, !friendMode, async () =>
    {
        var bytes = await FeatureBody(context, 8192);
        if (bytes is null) return Results.BadRequest(new { code = "InvalidNotificationPreferences", message = "Use only the reviewed notification options." });
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var change = DesktopNotificationPreferences.ParseChange(document.RootElement);
            if (!OwnsNotificationScope(change)) return Results.Conflict(new { code = "NotificationScopeChanged", message = "Choose a currently owned or assigned server." });
            return Results.Json(notificationPreferences.Apply(change));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        { return Results.BadRequest(new { code = "InvalidNotificationPreferences", message = "Use only the reviewed notification options." }); }
    })));
app.MapGet("/api/local/update/preparation", (HttpContext context) =>
    FixedOwnerGetRejection(context) is { } rejection ? rejection : Results.Json(updater.Preparation));
app.MapPost("/api/local/update/snooze", (Func<HttpContext, Task<IResult>>)(context =>
    FeatureRole(context, !friendMode, async () =>
    {
        var bytes = await FeatureBody(context, 512);
        if (bytes is null) return Results.BadRequest(new { code = "InvalidUpdateReminder", message = "Choose a reviewed reminder for this version." });
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            return Results.Json(await updater.SnoozeAsync(UpdateUiPreferences.ParseSnooze(document.RootElement)));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        { return Results.BadRequest(new { code = "InvalidUpdateReminder", message = "Choose a reviewed reminder for this version." }); }
    })));
app.MapGet("/api/local/profiles/{id:guid}/backup-catalog", (HttpContext context, Guid id) =>
    FeatureRole(context, true, async () => Results.Json(await manager.BackupCatalogAsync(id))));

const string scopedChat = "/api/local/friend/connections/{connectionId:guid}/servers/{id:guid}/chat";
app.MapGet("/api/local/profiles/{id:guid}/chat/summary", (HttpContext context, Guid id) =>
    FeatureRole(context, true, () => Task.FromResult<IResult>(Results.Json(ServerChat.Summarize(HostChatRoom(id))))));
app.MapGet(scopedChat + "/summary", (HttpContext context, Guid connectionId, Guid id) =>
    FeatureRole(context, false, async () => Results.Json(await friend.ChatSummaryAsync(connectionId, id, context.RequestAborted))));
app.MapPost(scopedChat + "/sync", (HttpContext context, Guid connectionId, Guid id) =>
    FeatureRole(context, false, async () =>
    {
        var bytes = await FeatureBody(context, 1);
        if (bytes is null || bytes.Length != 0) return Results.BadRequest(new { code = "InvalidChatSync", message = "Sync accepts no request fields." });
        return Results.Json(await friend.SyncChatAsync(connectionId, id, context.RequestAborted));
    }));
app.MapPost(scopedChat + "/messages", (HttpContext context, Guid connectionId, Guid id) =>
    FeatureRole(context, false, async () =>
    {
        var bytes = await FeatureBody(context, ServerChat.MaximumPostBytes);
        if (bytes is null || !ServerChat.TryChatPost(bytes, out var request))
            return Results.BadRequest(new { code = "InvalidChatText", message = "Use only a message of at most 500 characters." });
        return Results.Json(await friend.PostChatAsync(connectionId, id, request!.Text, context.RequestAborted));
    }));
app.MapPut(scopedChat + "/queue/{draftId:guid}", (HttpContext context, Guid connectionId, Guid id, Guid draftId) =>
    FeatureRole(context, false, async () =>
    {
        var bytes = await FeatureBody(context, ServerChat.MaximumQueueMutationBytes);
        if (bytes is null || !ServerChat.TryQueueEdit(bytes, out var request))
            return Results.BadRequest(new { code = "InvalidChatQueueEdit", message = "Review only this queued message's fixed fields." });
        return Results.Json(await friend.EditQueuedChatAsync(connectionId, id, draftId, request!, context.RequestAborted));
    }));
app.MapPost(scopedChat + "/queue/{draftId:guid}/cancel", (HttpContext context, Guid connectionId, Guid id, Guid draftId) =>
    FeatureRole(context, false, async () =>
    {
        var bytes = await FeatureBody(context, ServerChat.MaximumQueueMutationBytes);
        if (bytes is null || !ServerChat.TryQueueCancel(bytes, out var request))
            return Results.BadRequest(new { code = "InvalidChatQueueCancel", message = "Review only this queued message's fixed fields." });
        return Results.Json(await friend.CancelQueuedChatAsync(connectionId, id, draftId, request!, context.RequestAborted));
    }));

app.MapGet("/api/local/profiles/{id:guid}/requirements", (HttpContext context, Guid id) =>
    FeatureRole(context, true, async () => Results.Json(await manager.GameRequirementsAsync(id, context.RequestAborted))));
app.MapPut("/api/local/profiles/{id:guid}/requirements", (HttpContext context, Guid id) =>
    FeatureRole(context, true, async () =>
    {
        var bytes = await FeatureBody(context, 256);
        if (bytes is null || !FeatureRequestParser.TryVersion(bytes, out var version))
            return Results.BadRequest(new { code = "InvalidVersion", message = "Use only one bounded game version or null to clear it." });
        return Results.Json(await manager.SetGameRequirementAsync(id, new(version), context.RequestAborted));
    }));
app.MapGet("/api/local/friend/{id:guid}/compatibility", (HttpContext context, Guid id) =>
    FeatureRole(context, false, async () => Results.Json(await friend.ReadGameCompatibilityAsync(id, context.RequestAborted))));
app.MapPut("/api/local/friend/{id:guid}/client-version", (HttpContext context, Guid id) =>
    FeatureRole(context, false, async () =>
    {
        var bytes = await FeatureBody(context, 256);
        if (bytes is null || !FeatureRequestParser.TryVersion(bytes, out var version))
            return Results.BadRequest(new { code = "InvalidVersion", message = "Use only one bounded manual client version or null." });
        return Results.Json(await friend.SetManualClientVersionAsync(id, new(version), context.RequestAborted));
    }));
app.MapPost("/api/local/friend/{id:guid}/open-game", (HttpContext context, Guid id) =>
    FeatureRole(context, false, async () =>
    {
        var bytes = await FeatureBody(context, 1);
        if (bytes is null || bytes.Length != 0)
            return Results.BadRequest(new { code = "InvalidGameLaunchRequest", message = "Open game accepts only a currently assigned saved server, without a body." });
        return Results.Json(await friend.OpenGameAsync(id, context.RequestAborted));
    }));
app.MapGet("/api/local/profiles/{id:guid}/backup-bookmarks", (HttpContext context, Guid id) =>
    FeatureRole(context, true, async () => Results.Json(await manager.BackupBookmarksAsync(id))));
app.MapPut("/api/local/profiles/{id:guid}/backups/{backupId:guid}/bookmark", (HttpContext context, Guid id, Guid backupId) =>
    FeatureRole(context, true, async () =>
    {
        var bytes = await FeatureBody(context, 512);
        if (bytes is null || !FeatureRequestParser.TryBookmark(bytes, out var request))
            return Results.BadRequest(new { code = "InvalidBackupBookmark", message = "Use only a backup name and pin flag." });
        var result = await manager.UpdateBackupBookmarkAsync(id, backupId, request!);
        return Results.Json(result, statusCode: result.Ok ? 200 : result.Code == "UnknownProfile" ? 404 : 400);
    }));
app.MapGet("/api/local/profiles/{id:guid}/weekly-summary", (HttpContext context, Guid id) =>
    FeatureRole(context, true, async () => Results.Json(await manager.WeeklySummaryAsync(id))));
app.MapGet("/api/local/profiles/{id:guid}/game-settings", (HttpContext context, Guid id) =>
    FeatureRole(context, true, async () => Results.Json(await manager.ReadGameSettingsAsync(id))));
async Task<IResult> GameSettingsMutation(HttpContext context, Guid id, string? key, bool preview = false, bool undo = false) =>
    await FeatureRole(context, true, async () =>
    {
        IResult invalid = Results.BadRequest(new { ok = false, code = "InvalidSettingsRequest", message = "Use only the reviewed game settings fields." });
        var maximum = undo ? GameSettingsRequestParser.MaximumUndoRequestBytes : key is null
            ? GameSettingsRequestParser.MaximumSettingsRequestBytes : GameSettingsRequestParser.MaximumListRequestBytes;
        var bytes = await FeatureBody(context, maximum);
        if (bytes is null) return invalid;
        if (undo)
        {
            if (!GameSettingsRequestParser.TryParseUndo(bytes, out var request)) return invalid;
            return key is null ? Results.Json(await manager.UndoGameSettingsAsync(id, request!)) :
                Results.Json(await manager.UndoGameAccessListAsync(id, key, request!));
        }
        if (key is null)
        {
            if (!GameSettingsRequestParser.TryParseSettings(bytes, out var request)) return invalid;
            return preview ? Results.Json(await manager.PreviewGameSettingsAsync(id, request!)) :
                Results.Json(await manager.SaveGameSettingsAsync(id, request!));
        }
        if (!GameSettingsRequestParser.TryParseList(bytes, out var listRequest)) return invalid;
        return preview ? Results.Json(await manager.PreviewGameAccessListAsync(id, key, listRequest!)) :
            Results.Json(await manager.SaveGameAccessListAsync(id, key, listRequest!));
    });
app.MapPost("/api/local/profiles/{id:guid}/game-settings/preview", (HttpContext context, Guid id) => GameSettingsMutation(context, id, null, preview: true));
app.MapPut("/api/local/profiles/{id:guid}/game-settings", (HttpContext context, Guid id) => GameSettingsMutation(context, id, null));
app.MapPost("/api/local/profiles/{id:guid}/game-settings/undo", (HttpContext context, Guid id) => GameSettingsMutation(context, id, null, undo: true));
app.MapGet("/api/local/profiles/{id:guid}/game-settings/lists/{key}", (HttpContext context, Guid id, string key) =>
    FeatureRole(context, true, async () => Results.Json(await manager.ReadGameAccessListAsync(id, key))));
app.MapPost("/api/local/profiles/{id:guid}/game-settings/lists/{key}/preview", (HttpContext context, Guid id, string key) => GameSettingsMutation(context, id, key, preview: true));
app.MapPut("/api/local/profiles/{id:guid}/game-settings/lists/{key}", (HttpContext context, Guid id, string key) => GameSettingsMutation(context, id, key));
app.MapPost("/api/local/profiles/{id:guid}/game-settings/lists/{key}/undo", (HttpContext context, Guid id, string key) => GameSettingsMutation(context, id, key, undo: true));
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
ChatRoomView HostChatRoom(Guid profileId)
{
    var saved = data.LoadSettings().Profiles.Any(profile => profile.Id == profileId);
    var hostId = identity.State()?.HostId ?? Guid.Empty;
    if (!saved || hostId == Guid.Empty)
        return new(false, saved ? "ChatUnavailable" : "UnknownProfile",
            saved ? "Invite friends to start this server's chat room." : "Choose a saved server.",
            hostId, profileId, [], []);
    var members = pairing.Views().Where(device => device.AssignedProfileIds.Contains(profileId) &&
        !device.Revoked).Select(device => new ChatMember(device.Id, device.Name,
            serverChat.IsMember(profileId, device.Id))).ToList();
    try
    {
        return new(true, "ChatReady", "Messages are copied to connected members.", hostId,
            profileId, serverChat.Read(hostId, profileId), [], members,
            Notice: serverChat.ReadOwnerNotice(hostId, profileId), NoticeSupported: true);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or
        System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or ArgumentException)
    { return new(false, "ChatReviewRequired", "This room's protected data needs owner review.", hostId, profileId, [], []); }
}
app.MapPost("/api/local/rehearsal/prepare", async () => friendMode
    ? Results.Conflict(new RemoteRehearsalSetup(false, "FriendMode", "Open the development Host first."))
    : Results.Json(await manager.PrepareRemoteRehearsalAsync(instance.IsStaging)));
app.MapPost("/api/local/friend/{id:guid}/rehearsal", async (HttpContext context, Guid id,
    RemoteRehearsalRequest request) => friendMode
    ? instance.IsStaging ? Results.Json(await friend.RunRemoteRehearsalAsync(id, request, context.RequestAborted))
        : Results.Conflict(new { code = "StagingRequired", message = "Run the development Friend app for test access." })
    : Results.Conflict(new { code = "HostMode" }));

app.MapGet("/api/local/profiles/{id:guid}/chat", (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Conflict(new { code = "FriendMode" }) : Results.Json(HostChatRoom(id)));
app.MapPut("/api/local/profiles/{id:guid}/chat/notice", (HttpContext context, Guid id) =>
    FeatureRole(context, true, async () =>
    {
        var bytes = await FeatureBody(context, 16 * 1024);
        if (bytes is null || !FeatureRequestParser.TryNotice(bytes, out var request) ||
            request!.Text is not null && !ServerChat.ValidNoticeText(request.Text))
            return Results.BadRequest(new { code = "InvalidNotice", message = "Use only a plain-text notice of at most 2000 characters and the current revision." });
        var room = HostChatRoom(id);
        if (!room.Ok) return Results.Json(room, statusCode: 409);
        try
        {
            serverChat.SetOwnerNotice(room.HostId, id, request.Text, request.ExpectedRevision);
            return Results.Json(HostChatRoom(id));
        }
        catch (PinnedNoticeConflictException)
        { return Results.Conflict(new { code = "NoticeChanged", message = "The notice changed. Reload before saving." }); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return Results.Json(new { code = "NoticeUnavailable", message = "The protected notice could not be saved. Review the room and try again." }, statusCode: 503); }
    }));
app.MapPost("/api/local/profiles/{id:guid}/chat/messages", (Guid id, ChatPostRequest request) =>
{
    if (friendMode) return Results.Conflict(new { code = "FriendMode" });
    var room = HostChatRoom(id);
    if (!room.Ok) return Results.Json(room, statusCode: 409);
    if (!ServerChat.ValidText(request.Text))
        return Results.BadRequest(new { code = "InvalidChatText", message = "Write a message of at most 500 characters." });
    try
    {
        serverChat.Post(room.HostId, id, Guid.Empty, "Host", request.Text!);
        return Results.Json(HostChatRoom(id));
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or
                               System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
    { return Results.Json(new { code = "ChatUnavailable", message = "Chat could not be saved." }, statusCode: 503); }
});
app.MapPut("/api/local/profiles/{id:guid}/chat/members/{deviceId:guid}",
    (Guid id, Guid deviceId, ChatMemberChange change) =>
{
    if (friendMode) return Results.Conflict(new { code = "FriendMode" });
    if (!data.LoadSettings().Profiles.Any(profile => profile.Id == id))
        return Results.NotFound(new { code = "UnknownProfile" });
    if (!pairing.Views().Any(device => device.Id == deviceId && !device.Revoked &&
        device.AssignedProfileIds.Contains(id)))
        return Results.NotFound(new { code = "UnknownChatMember" });
    try
    {
        serverChat.SetMember(id, deviceId, change.Allowed);
        return Results.Json(HostChatRoom(id));
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
    { return Results.Json(new { code = "ChatUnavailable", message = "Room access needs owner review." }, statusCode: 503); }
});
app.MapGet("/api/local/profiles/{id:guid}/backups", async (Guid id) => friendMode
    ? Results.Conflict(new { ok = false, code = "FriendMode", message = "Backups are local-owner-only." })
    : Results.Json(await manager.BackupsAsync(id)));
app.MapGet("/api/local/profiles/{id:guid}/shared-world", async (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Conflict(new { code = "FriendMode" }) :
    Results.Json(await manager.SharedWorldStatusAsync(id)));
app.MapGet("/api/local/profiles/{id:guid}/shared-world/live-orphan", async (HttpContext context, Guid id) =>
{
    if (LocalSharedLiveOrphanRequest.RejectGet(context, friendMode) is { } rejection)
        return rejection;
    return Results.Json(await manager.SharedLiveOrphanReviewAsync(id));
});
app.MapPost("/api/local/profiles/{id:guid}/shared-world/live-orphan/quarantine",
    async (HttpContext context, Guid id) =>
    {
        var (versionHash, rejection) = await LocalSharedLiveOrphanRequest.ReadPostAsync(context, friendMode);
        if (rejection is not null) return rejection;
        return await HostOnly(() => manager.QuarantineSharedLiveOrphanAsync(id, versionHash!));
    });
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
app.MapPost("/api/local/profiles/{id:guid}/shared-world/live/save", async (Guid id, LiveSaveRequest request, HttpContext context) =>
    friendMode ? Results.Conflict(new LiveSaveActionResult(false, "FriendMode", "Switch to Host mode first.")) :
        Results.Json(await manager.SaveAndShareAsync(id, request, context.RequestAborted)));
app.MapPost("/api/local/profiles/{id:guid}/shared-world/live/withdraw", async (Guid id, LiveSaveRequest request) =>
    friendMode ? Results.Conflict(new LiveSaveActionResult(false, "FriendMode", "Switch to Host mode first.")) :
        Results.Json(await manager.WithdrawLiveSaveAsync(id, request)));
app.MapPost("/api/local/profiles/{id:guid}/shared-world/live/resume", async (Guid id) =>
    friendMode ? Results.Conflict(new LiveSaveActionResult(false, "FriendMode", "Switch to Host mode first.")) :
        Results.Json(await manager.RecoverPendingBedrockResumeAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/backups/setup", (Guid id) =>
    HostOnly(() => manager.CreateCompleteSetupBackupAsync(id)));
app.MapPost("/api/local/profiles/{id:guid}/backups/{backupId:guid}/restore-setup", (Guid id, Guid backupId) =>
    HostOnly(() => manager.RestoreCompleteSetupAsync(id, backupId)));
app.MapPost("/api/local/profiles/{id:guid}/backups/{backupId:guid}/verify", async (Guid id, Guid backupId) =>
    friendMode
        ? Results.Conflict(new { ok = false, code = "FriendMode", message = "Backup verification is local-owner-only." })
        : Results.Json(await manager.VerifyBackupWithEvidenceAsync(id, backupId)));
app.MapPost("/api/local/profiles/{id:guid}/backups/{backupId:guid}/vault", async (Guid id, Guid backupId) =>
{
    if (friendMode) return Results.Conflict(new { ok = false, code = "FriendMode", message = "Switch to Host mode first." });
    if (desktop is null) return Results.Conflict(new { ok = false, code = "WindowUnavailable", message = "Open the TogetherServer window to choose a backup-vault folder." });
    try
    {
        var destination = await desktop.PickFolderAsync("Choose an external drive or network folder for this verified backup");
        if (destination is null)
            return Results.Json(new BackupSafetyResult(false, "Canceled", "No backup-vault folder was selected.", backupId, DateTimeOffset.UtcNow));
        return Results.Json(await manager.CopyBackupToVaultWithEvidenceAsync(id, backupId, destination));
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
        : Results.Json(await manager.RehearseRestoreWithEvidenceAsync(id, backupId)));
app.MapPost("/api/local/profiles/{id:guid}/world-load/prepare", async (Guid id, WorldLoadPreparationRequest request) =>
    friendMode ? Results.Conflict(new WorldLoadRehearsalResult(false, "FriendMode", "Switch to Host mode first.")) :
        Results.Json(await manager.PrepareWorldLoadRehearsalAsync(id, request.BackupId)));
app.MapPost("/api/local/friend/{id:guid}/world-load/prepare", async (Guid id, HttpContext context) =>
    !friendMode ? Results.Conflict(new WorldLoadRehearsalResult(false, "HostMode", "Switch to Friend mode first.")) :
        Results.Json(await friend.PrepareWorldLoadAsync(id, manager, context.RequestAborted)));
app.MapGet("/api/local/world-load/source/{id:guid}", async (Guid id, HttpContext context) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
        Results.Json(await manager.WorldLoadRehearsalsAsync(id)));
app.MapGet("/api/local/world-load/{id:guid}", async (Guid id, HttpContext context) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
        Results.Json(await manager.WorldLoadRehearsalAsync(id)));
app.MapPost("/api/local/world-load/{id:guid}/start", async (Guid id) =>
    Results.Json(await manager.WorldLoadRehearsalAsync(id, "start")));
app.MapPost("/api/local/world-load/{id:guid}/stop", async (Guid id) =>
    Results.Json(await manager.WorldLoadRehearsalAsync(id, "stop")));
app.MapPost("/api/local/world-load/{id:guid}/confirm", async (Guid id, WorldLoadConfirmationRequest request) =>
    Results.Json(await manager.ConfirmWorldLoadWithBackupEvidenceAsync(id, request)));
app.MapPost("/api/local/world-load/{id:guid}/cleanup", async (Guid id, WorldLoadCleanupRequest request) =>
    Results.Json(await manager.WorldLoadRehearsalAsync(id, "cleanup", confirmStopped: request.ConfirmStopped)));
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
bool SharedServerScopeMatches(SharedServerBrowseRequest request, bool hostScope, Guid? connectionId)
{
    if (hostScope)
        return !friendMode && data.LoadSettings().Profiles.Any(profile =>
            profile.Id == request.ProfileId && profile.Kind == request.Kind);
    var view = friend.View();
    return friendMode && connectionId is { } selected && selected != Guid.Empty &&
        view.ConnectionId == selected && view.Profiles.Any(profile =>
            QolLocalEndpointInputs.ProfileGameMatches(profile, request));
}
async Task<IResult> BrowseSharedServerFile(HttpContext context)
{
    var hostScope = !friendMode;
    return await FeatureRole(context, hostScope, async () =>
    {
        if (shutdownPending)
            return Results.Conflict(new SharedServerBrowseResult(false, "ShutdownPending", "TogetherServer is closing."));
        var bytes = await FeatureBody(context, QolLocalEndpointInputs.MaximumSharedBrowseBytes);
        if (bytes is null || !QolLocalEndpointInputs.TrySharedServerBrowse(bytes, out var request))
            return Results.BadRequest(new SharedServerBrowseResult(false, "InvalidServerSelection",
                "Choose a currently owned or assigned server and its supported game."));
        var connectionId = hostScope ? (Guid?)null : friend.View().ConnectionId;
        if (!SharedServerScopeMatches(request!, hostScope, connectionId))
            return Results.Conflict(new SharedServerBrowseResult(false, "ServerSelectionScopeChanged",
                "Open the current server and choose its game before browsing."));
        if (desktop is null)
            return Results.Conflict(new SharedServerBrowseResult(false, "WindowUnavailable",
                "Open the TogetherServer window to choose a server file."));
        var picker = QolLocalEndpointInputs.Picker(request!.Kind)!;
        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            var selected = await desktop.PickFileAsync(picker.Title, picker.Filter);
            context.RequestAborted.ThrowIfCancellationRequested();
            if (shutdownPending || !SharedServerScopeMatches(request, hostScope, connectionId))
                return Results.Conflict(new SharedServerBrowseResult(false, "ServerSelectionScopeChanged",
                    "The selected server changed. Open it and browse again."));
            if (selected is null)
                return Results.Json(new SharedServerBrowseResult(false, "Canceled", "No server file selected."));
            if (!Path.IsPathFullyQualified(selected) ||
                !Path.GetFileName(selected).Equals(picker.FileName, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new SharedServerBrowseResult(false, "InvalidServerFile",
                    "Choose " + picker.FileName + " from the installed server folder."));
            var path = Path.GetFullPath(selected);
            SetupImportSourceFacts.EnsurePlainFile(path);
            if (request.Kind == GameKinds.MinecraftJava)
            {
                var folder = Path.GetDirectoryName(path)!;
                SetupImportSourceFacts.EnsurePlainFile(Path.Combine(folder, ".togetherserver-java.json"));
                if (!MinecraftSetup.IsSupportedVanillaServerJar(path, folder))
                    return Results.Json(new SharedServerBrowseResult(false, "UnsupportedServerJar",
                        "Choose server.jar from a TogetherServer-installed official Minecraft Java server."));
            }
            if (!SharedServerScopeMatches(request, hostScope, connectionId))
                return Results.Conflict(new SharedServerBrowseResult(false, "ServerSelectionScopeChanged",
                    "The selected server changed. Open it and browse again."));
            // This path is returned only by the owner's loopback GUI. Existing
            // local hosting setup still validates it before any managed Start.
            return Results.Json(new SharedServerBrowseResult(true, "PathSelected",
                "Server file selected. Review and save local hosting setup before starting.", path));
        }
        catch (Exception error) when (SetupImportSourceFacts.IsSourceException(error) || error is InvalidOperationException)
        {
            return Results.Json(new SharedServerBrowseResult(false, "BrowseFailed",
                "Could not choose a plain installed server file. Browse again from its local folder."));
        }
    });
}
app.MapPost("/api/local/shared-worlds/browse-server-file", (Func<HttpContext, Task<IResult>>)BrowseSharedServerFile);
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
bool ImportProfileGameMatches(Guid profileId, string kind) =>
    !data.LoadSettings().Profiles.Any(profile => profile.Id == profileId && profile.Kind != kind);
IResult ImportConfirmationFailure(string code, string message) => Results.Json(new
{
    ok = false, code, message, worldId = (string?)null, worldDirectory = (string?)null
});
Task<IResult> PrepareOwnerImport(HttpContext context, string kind, string title, string filter) =>
    FeatureRole(context, true, async () =>
    {
        if (shutdownPending)
            return Results.Conflict(new SetupImportPreviewResult(false, "ShutdownPending", "TogetherServer is closing."));
        // Refuse before opening a picker or inspecting an original save.
        if (instance.FreshWorldsOnly)
            return Results.Conflict(new SetupImportPreviewResult(false, "StagingFreshWorldRequired",
                "Development cannot import an existing world. Use separate fresh development storage."));
        var bytes = await FeatureBody(context, SetupImportRequestParser.MaximumPrepareRequestBytes);
        if (bytes is null || !SetupImportRequestParser.TryParsePrepare(bytes, out var profileId))
            return Results.BadRequest(new SetupImportPreviewResult(false, "InvalidImportSelection", "Use only one server profile ID."));
        if (!ImportProfileGameMatches(profileId, kind))
            return Results.Conflict(new SetupImportPreviewResult(false, "ImportScopeChanged", "Choose the current server's game before browsing."));
        if (desktop is null)
            return Results.Conflict(new SetupImportPreviewResult(false, "WindowUnavailable", "Open the TogetherServer window to choose a source file."));
        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            var selected = await desktop.PickFileAsync(title, filter);
            context.RequestAborted.ThrowIfCancellationRequested();
            if (!ImportProfileGameMatches(profileId, kind))
                return Results.Conflict(new SetupImportPreviewResult(false, "ImportScopeChanged", "The server's game changed. Browse and review its source again."));
            return Results.Json(importSelections.Prepare(kind, profileId, selected));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Security.SecurityException)
        { return Results.Json(new SetupImportPreviewResult(false, "BrowseFailed", "Could not choose the source file. No copy was made.")); }
    });
Task<IResult> LegacyImportReviewRequired(HttpContext context) => FeatureRole(context, true, async () =>
{
    if (shutdownPending) return ImportConfirmationFailure("ShutdownPending", "TogetherServer is closing.");
    if (instance.FreshWorldsOnly)
        return ImportConfirmationFailure("StagingFreshWorldRequired", "Development cannot import an existing world. Use separate fresh development storage.");
    var bytes = await FeatureBody(context, SetupImportRequestParser.MaximumPrepareRequestBytes);
    if (bytes is null || !SetupImportRequestParser.TryParsePrepare(bytes, out _))
        return Results.BadRequest(new { ok = false, code = "InvalidImportSelection", message = "Use only one server profile ID.",
            worldId = (string?)null, worldDirectory = (string?)null });
    return ImportConfirmationFailure("ImportReviewRequired", "Browse and review the source preview, then choose Copy to create a managed copy.");
});
app.MapPost("/api/local/factorio/preview-save", (Func<HttpContext, Task<IResult>>)(context =>
    PrepareOwnerImport(context, GameKinds.Factorio, "Choose an existing Factorio save to preview", "Factorio saves (*.zip)|*.zip")));
app.MapPost("/api/local/terraria/preview-world", (Func<HttpContext, Task<IResult>>)(context =>
    PrepareOwnerImport(context, GameKinds.Terraria, "Choose an existing Terraria world to preview", "Terraria worlds (*.wld)|*.wld")));
app.MapPost("/api/local/factorio/import-save", (Func<HttpContext, Task<IResult>>)LegacyImportReviewRequired);
app.MapPost("/api/local/setup/import-confirm", (Func<HttpContext, Task<IResult>>)(context =>
    FeatureRole(context, true, async () =>
    {
        if (shutdownPending) return ImportConfirmationFailure("ShutdownPending", "TogetherServer is closing.");
        if (instance.FreshWorldsOnly)
            return ImportConfirmationFailure("StagingFreshWorldRequired", "Development cannot import an existing world. Use separate fresh development storage.");
        var bytes = await FeatureBody(context, SetupImportRequestParser.MaximumRequestBytes);
        if (bytes is null || !SetupImportRequestParser.TryParse(bytes, out var request))
            return Results.BadRequest(new { ok = false, code = "InvalidImportConfirmation", message = "Confirm only a selected source token, its server profile ID and supported game.",
                worldId = (string?)null, worldDirectory = (string?)null });
        if (!ImportProfileGameMatches(request!.ProfileId, request.Kind))
            return ImportConfirmationFailure("ImportScopeChanged", "The server's game changed. Browse and review its source again.");
        context.RequestAborted.ThrowIfCancellationRequested();
        var resolved = importSelections.Resolve(request.SelectionId, request.Kind, request.ProfileId);
        if (!resolved.Ok || resolved.SelectedPath is null) return ImportConfirmationFailure(resolved.Code, resolved.Message);
        // Only the native token store resolves the path; the original verified
        // importer retains source/hash, fresh destination and copy-once guards.
        return request.Kind == GameKinds.Factorio
            ? Results.Json(FactorioSetup.ImportCopy(data, request.ProfileId, resolved.SelectedPath))
            : Results.Json(TerrariaSetup.ImportCopy(data, request.ProfileId, resolved.SelectedPath));
    })));
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
app.MapPost("/api/local/terraria/import-world", (Func<HttpContext, Task<IResult>>)LegacyImportReviewRequired);
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
app.MapPost("/api/local/quit", (Func<HttpContext, Task<IResult>>)(async context =>
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
}));

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
        if (settings.Profiles.Any(profile => profile.Id == profileId && profile.WorldLoadRehearsalId is not null))
            return Results.Conflict(new { ok = false, code = "WorldLoadOwnerOnly", message = "Disposable load rehearsals have no Friend invitations." });
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
        if (result.Ok && status.Enabled) await PublishRosterAndConfirmAsync(profileId,
            ownerEdit: new(id, Receive: request.Enabled, Revoked: request.Enabled ? false : null));
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
app.MapGet("/api/local/profiles/{profileId:guid}/shared-world/resolution/offers",
    (HttpContext context, Guid profileId) =>
{
    if (!HasSensitiveLocalGetHeader(context)) return Results.StatusCode(403);
    if (friendMode) return Results.Conflict(new { code = "FriendMode" });
    try
    {
        var offers = new WorldAuthorityStore(data).PendingResolutionOffers(profileId);
        return Results.Json(offers.Select(offer => new
        {
            proposalHash = WorldAuthorityTrust.ProposalHash(offer.Proposal),
            selectedVersion = offer.Version.Number,
            competingBranches = offer.Proposal.CompetingHeadHashes?.Count ?? 0,
            candidateDeviceId = offer.Proposal.ProposerDeviceId,
            candidateAddress = offer.Proposal.CandidateAddress
        }).ToArray());
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
    { return Results.Conflict(new { code = "ResolutionOffersUnavailable" }); }
});
app.MapPost("/api/local/profiles/{profileId:guid}/shared-world/resolution/approve/{proposalHash}",
    async (HttpContext context, Guid profileId, string proposalHash) =>
{
    await modeGate.WaitAsync(context.RequestAborted);
    try
    {
        if (friendMode) return Results.Conflict(new { code = "FriendMode" });
        if (proposalHash.Length != 64 || !proposalHash.All(Uri.IsHexDigit))
            return Results.BadRequest(new { code = "InvalidResolutionId" });
        var offer = new WorldAuthorityStore(data).PendingResolutionOffers(profileId)
            .SingleOrDefault(item => WorldAuthorityTrust.ProposalHash(item.Proposal) == proposalHash);
        if (offer is null) return Results.NotFound();
        var approval = await manager.SignSharedWorldResolutionAsync(profileId, offer);
        using var client = FriendLink.MakeClient(offer.Proposal.CandidateAddress,
            [offer.CandidateTlsFingerprint]);
        using var response = await client.PostAsJsonAsync(
            $"api/companion/servers/{profileId}/shared-world/resolution/{proposalHash}/owner-approval",
            approval, context.RequestAborted);
        var bytes = await FriendLink.ReadBoundedSharedAsync(response.Content,
            2 * 1024 * 1024, context.RequestAborted);
        var result = response.IsSuccessStatusCode && bytes is not null ?
            JsonSerializer.Deserialize<WorldAuthorityVoteResult>(bytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;
        if (result is not { Ok: true, Decision: not null } ||
            WorldAuthorityTrust.ProposalHash(result.Decision.Proposal) != proposalHash)
            return Results.Conflict(new { code = "OwnerApprovalDeliveryPending" });
        await manager.ApplySharedWorldAuthorityAsync(result.Decision);
        return Results.Json(new { code = "OwnerOverrideRecorded", result.Decision.RecordHash });
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                               System.Security.Cryptography.CryptographicException or
                               HttpRequestException or TaskCanceledException)
    { return Results.Conflict(new { code = "OwnerApprovalDeliveryPending" }); }
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
                return Results.Conflict(new
                {
                    ok = false,
                    code = "SuccessorGovernanceUnresolved",
                    message = "Signed membership needs review before this PC can share or vote again."
                });
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
        if (result.Ok) await PublishRosterAndConfirmAsync(profileId,
            ownerEdit: new(id, Grants: request.Grants, Revoked: false));
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
app.MapGet("/api/local/friend/{id:guid}/chat", (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Json(friend.ChatRoom(id)) : Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/chat/sync", async (Guid id) =>
    friendMode ? Results.Json(await friend.SyncChatAsync(id)) : Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/chat/messages", async (Guid id, ChatPostRequest request) =>
    friendMode ? Results.Json(await friend.PostChatAsync(id, request.Text)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/probe-game", (HttpContext context, Guid id) =>
    FeatureRole(context, false, async () =>
    {
        if (shutdownPending)
            return Results.Conflict(new GameEndpointProbeResult(false, "ShutdownPending", "TogetherServer is closing.", DateTimeOffset.UtcNow));
        var bytes = await FeatureBody(context, QolLocalEndpointInputs.MaximumGameProbeBytes);
        if (bytes is null || !QolLocalEndpointInputs.TryGameProbe(bytes, out var request))
            return Results.BadRequest(new GameEndpointProbeResult(false, "InvalidGameProbe",
                "Use only the selected saved Host ID and current nullable server run ID.", DateTimeOffset.UtcNow));
        context.RequestAborted.ThrowIfCancellationRequested();
        return Results.Json(await friend.ProbeGameEndpointAsync(request!.ConnectionId, id, request.RunOperationId));
    }));
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
app.MapPost("/api/local/friend/{id:guid}/shared-world/successor-enrollment",
    async (HttpContext context, Guid id, SharedWorldRouteRequest request) =>
    friendMode ? Results.Json(await friend.EnrollWithSuccessorAsync(id,
        request.RecordHash, request.TlsFingerprint, context.RequestAborted)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/recovery/separate/route-check",
    async (HttpContext context, Guid id) =>
{
    if (!friendMode) return Results.Conflict(new { code = "HostMode" });
    var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
        context.Request.ContentLength, 512 * 1024, context.RequestAborted);
    if (bytes is null) return Results.BadRequest(new { code = "InvalidSeparateRouteProof" });
    try
    {
        var request = JsonSerializer.Deserialize<SeparateCopyRouteRequest>(bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return request?.Branch is null ? Results.BadRequest(new { code = "InvalidSeparateRouteProof" }) :
            Results.Json(await friend.ProbeSeparateCopyRouteAsync(id, request.Branch,
                context.RequestAborted));
    }
    catch (JsonException) { return Results.BadRequest(new { code = "InvalidSeparateRouteProof" }); }
});
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
    {
        if (!friendMode) return Results.Conflict(new { code = "HostMode" });
        var result = await manager.RestoreSharedSuccessorAsync(id,
            request, context.RequestAborted);
        if (result.Ok) await companionServer.SyncAsync();
        return Results.Json(result);
    });
app.MapPost("/api/local/friend/{id:guid}/shared-world/handoff/finish",
    async (Guid id, SuccessorFinishRequest request) =>
    friendMode ? Results.Json(await manager.FinishSharedSuccessorAsync(id, request)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/check", async (HttpContext context, Guid id) =>
    friendMode ? Results.Json(await friend.CheckSharedWorldAsync(id, context.RequestAborted)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/history/review", async (HttpContext context, Guid id,
    WorldHistoryReviewRequest request) =>
    friendMode ? Results.Json(await friend.ReviewSharedHistoryAsync(id, request, context.RequestAborted)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/recovery/offer", async (Guid id) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (!friendMode) return Results.Conflict(new { code = "HostMode" });
        var result = await friend.PrepareRecoveryOfferAsync(id);
        return Results.Json(await SharedWorldCandidateListener.ActivateAsync(result, manager,
            companionServer));
    }
    finally { modeGate.Release(); }
});
app.MapGet("/api/local/friend/{id:guid}/shared-world/recovery", (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Json(new SharedWorldVoteInbox(data).Status(id, friend.RecoveryDeviceId(id))) :
    Results.Conflict(new { code = "HostMode" }));
app.MapGet("/api/local/friend/{id:guid}/shared-world/recovery/offer-code", (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    !friendMode ? Results.Conflict(new { code = "HostMode" }) :
    new SharedWorldVoteInbox(data).Armed(id) is { } offer ? Results.Json(new
    {
        proposalHash = WorldAuthorityTrust.ProposalHash(offer.Proposal),
        offer
    }) :
    Results.NotFound(new { code = "NoArmedOffer", message = "No signed offer is armed on this PC." }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/resolution/offer/{selectedHeadHash}",
    async (Guid id, string selectedHeadHash) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (!friendMode) return Results.Conflict(new { code = "HostMode" });
        var result = await friend.PrepareResolutionOfferAsync(id, selectedHeadHash);
        return Results.Json(await SharedWorldCandidateListener.ActivateAsync(result, manager,
            companionServer));
    }
    finally { modeGate.Release(); }
});
app.MapGet("/api/local/friend/{id:guid}/shared-world/resolution/heads",
    (HttpContext context, Guid id) =>
{
    if (!HasSensitiveLocalGetHeader(context)) return Results.StatusCode(403);
    if (!friendMode) return Results.Conflict(new { code = "HostMode" });
    try { return Results.Json(friend.ResolutionChoices(id)); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
    { return Results.Conflict(new { code = "ResolutionUnavailable" }); }
});
app.MapPost("/api/local/friend/{id:guid}/shared-world/resolution/owner-offer/{selectedHeadHash}",
    async (Guid id, string selectedHeadHash) =>
{
    await modeGate.WaitAsync();
    try
    {
        if (!friendMode) return Results.Conflict(new { code = "HostMode" });
        var result = await friend.PrepareResolutionOfferAsync(id, selectedHeadHash, true);
        return Results.Json(await SharedWorldCandidateListener.ActivateAsync(result, manager,
            companionServer));
    }
    finally { modeGate.Release(); }
});
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
app.MapPost("/api/local/friend/{id:guid}/shared-world/resolution/invitations",
    async (HttpContext context, Guid id) =>
{
    if (!friendMode) return Results.Conflict(new { code = "HostMode" });
    var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(context.Request.Body,
        context.Request.ContentLength, 512 * 1024, context.RequestAborted);
    if (bytes is null) return Results.BadRequest(new { code = "InvalidResolutionInvitation" });
    try
    {
        var offer = JsonSerializer.Deserialize<WorldAuthorityOffer>(bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return offer is null ? Results.BadRequest(new { code = "InvalidResolutionInvitation" }) :
            Results.Json(await friend.ImportResolutionInvitationAsync(id, offer));
    }
    catch (JsonException) { return Results.BadRequest(new { code = "InvalidResolutionInvitation" }); }
});
app.MapPost("/api/local/friend/{id:guid}/shared-world/resolution/vote/{proposalHash}",
    async (HttpContext context, Guid id, string proposalHash) =>
    friendMode ? Results.Json(await friend.VoteOnResolutionIdAsync(id,
        proposalHash, context.RequestAborted)) : Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/recovery/separate",
    async (Guid id, WorldSeparateCopyConfirmation confirmation) =>
    friendMode ? Results.Json(await friend.DeclareSeparateCopyAsync(id, confirmation.AcceptSplitWarning)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapGet("/api/local/friend/{id:guid}/shared-world/recovery/separate",
    (HttpContext context, Guid id) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    friendMode ? Results.Json(friend.SeparateCopyBranches(id)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapGet("/api/local/friend/{id:guid}/shared-world/recovery/separate/hosting/{branchHash}",
    async (HttpContext context, Guid id, string branchHash) =>
    !HasSensitiveLocalGetHeader(context) ? Results.StatusCode(403) :
    !friendMode ? Results.Conflict(new { code = "HostMode" }) :
    !friend.SeparateCopyBranches(id).Any(item => item.BranchHash == branchHash) ?
        Results.NotFound() :
        Results.Json(await manager.SeparateCopyHostStatusAsync(id, branchHash,
            friend.CurrentRecoveryHostLoss(id))));
app.MapPost("/api/local/friend/{id:guid}/shared-world/recovery/separate/restore",
    async (HttpContext context, Guid id, SeparateCopyHostRestoreRequest request) =>
    !friendMode ? Results.Conflict(new { code = "HostMode" }) :
    !friend.SeparateCopyBranches(id).Any(item => item.BranchHash == request.BranchHash) ?
        Results.Json(new SuccessorRestoreResult(false, "SeparateProofRejected",
            "Choose this PC's recorded signed separate copy.")) :
    !await friend.ProbeRecoveryHostLossAsync(id, context.RequestAborted) ?
        Results.Json(new SuccessorRestoreResult(false, "HostLossNotConfirmed",
            "This PC must still confirm two minutes without reaching the old Host.")) :
        Results.Json(await manager.RestoreSeparateCopyAsync(id, request,
            context.RequestAborted)));
app.MapPost("/api/local/friend/{id:guid}/shared-world/recovery/separate/finish",
    async (HttpContext context, Guid id, SeparateCopyHostFinishRequest request) =>
    !friendMode ? Results.Conflict(new { code = "HostMode" }) :
    !friend.SeparateCopyBranches(id).Any(item => item.BranchHash == request.BranchHash) ?
        Results.Json(new SuccessorRestoreResult(false, "SeparateProofRejected",
            "Choose this PC's recorded signed separate copy.")) :
    !await friend.ProbeRecoveryHostLossAsync(id, context.RequestAborted) ?
        Results.Json(new SuccessorRestoreResult(false, "HostLossNotConfirmed",
            "This PC must still confirm two minutes without reaching the old Host.")) :
        Results.Json(await manager.FinishSeparateCopyAsync(id, request)));
app.MapPost("/api/local/friend/{id:guid}/shared-world/recovery/separate/start",
    async (HttpContext context, Guid id, SeparateCopyHostStartRequest request) =>
    !friendMode ? Results.Conflict(new { code = "HostMode" }) :
    !request.AcceptSplitWarning ? Results.Json(new
    {
        ok = false,
        code = "SplitWarningRequired",
        message = "Confirm that another game server may still be running and both histories need review."
    }) :
    !friend.SeparateCopyBranches(id).Any(item => item.BranchHash == request.BranchHash) ?
        Results.Json(new
        {
            ok = false,
            code = "SeparateProofRejected",
            message = "Choose this PC's recorded signed separate copy."
        }) :
    !await friend.ProbeRecoveryHostLossAsync(id, context.RequestAborted) ?
        Results.Json(new
        {
            ok = false,
            code = "HostLossNotConfirmed",
            message = "This PC must still confirm two minutes without reaching the old Host."
        }) :
        Results.Json(await manager.StartSeparateCopyAsync(request.LocalProfileId,
            request.BranchHash)));
app.MapPost("/api/local/friend/{id:guid}/shared-world/sharing", async (HttpContext context,
    Guid id, SharedWorldDelegateChangeRequest change) =>
    friendMode ? Results.Json(await friend.ChangeSharedWorldGrantsAsync(id, change, context.RequestAborted)) :
    Results.Conflict(new { code = "HostMode" }));
app.MapPost("/api/local/friend/{id:guid}/shared-world/sharing/check", async (HttpContext context,
    Guid id) => friendMode ? Results.Json(await friend.CheckSharedWorldSharingAsync(id,
        context.RequestAborted)) : Results.Conflict(new { code = "HostMode" }));
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

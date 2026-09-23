using System.Net;
using System.Net.Sockets;
using TogetherServer;

var root = Path.GetFullPath("local-data/custom-checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}

void Require(bool value, string message) { if (!value) throw new Exception(message); }
int FreePort()
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var port = Random.Shared.Next(35000, 59000);
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return port;
        }
        catch (SocketException) { }
    }
    throw new Exception("No free UDP port found.");
}

ServerProfile Profile(string name, int port)
{
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    return new ServerProfile
    {
        Kind = GameKinds.Custom,
        Name = name,
        ServerName = name,
        WorldId = name,
        WorldDirectory = directory,
        GamePort = port,
        ExecutablePath = "",
        Custom = new CustomGameOptions
        {
            GameName = "Synthetic custom game",
            PrimaryProtocol = "UDP",
            AdditionalPorts = [new("TCP", port + 1, "Query")]
        }
    };
}

CustomScriptBundle Scripts(string status = "") => new(
    """
    if ($env:TOGETHERSERVER_MANAGED_PID -ne [string]$PID) { exit 19 }
    $stop = Join-Path $env:TOGETHERSERVER_WORKING_DIRECTORY 'stop.signal'
    Remove-Item -LiteralPath $stop -Force -ErrorAction SilentlyContinue
    while (-not (Test-Path -LiteralPath $stop)) { Start-Sleep -Milliseconds 100 }
    exit 0
    """,
    string.IsNullOrWhiteSpace(status) ?
    """
    @{ state = 'Ready'; detail = 'Synthetic custom status'; onlinePlayers = 0; maxPlayers = 4; players = @('Alice', 'Bob') } | ConvertTo-Json -Compress
    """ : status,
    """
    $stop = Join-Path $env:TOGETHERSERVER_WORKING_DIRECTORY 'stop.signal'
    New-Item -ItemType File -Path $stop -Force | Out-Null
    """);

async Task<RunView> WaitForState(HostManager manager, Guid profileId, string state)
{
    var deadline = DateTime.UtcNow.AddSeconds(12);
    RunView? view = null;
    while (DateTime.UtcNow < deadline)
    {
        view = (await manager.SnapshotAsync()).Runs.Single(run => run.ProfileId == profileId);
        if (view.State == state) return view;
        await Task.Delay(150);
    }
    throw new Exception($"Expected {state}, last state was {view?.State}: {view?.Detail}");
}

await Check("protected scripts are required and never authorize remote stop", async () =>
{
    using var data = new LocalData(Path.Combine(root, "lifecycle-data"));
    var games = new GameServerRegistry(data);
    var manager = new HostManager(data, games);
    var activeManager = manager;
    var profile = Profile("custom-lifecycle", FreePort());
    var settings = new HostSettings
    {
        MaxConcurrentServers = 2,
        AutoShutdownEnabled = true,
        IdleMinutes = 1,
        Profiles = [profile]
    };
    Require((await manager.UpdateSettingsAsync(settings)).Ok, "custom settings failed");
    Require((await manager.StartAsync(profile.Id)).Code == "CustomScriptsRequired", "start did not require scripts");
    Require((await manager.SetCustomScriptsAsync(profile.Id, new("", "", ""))).Code == "CustomScriptsInvalid",
        "blank scripts were accepted");
    Require((await manager.SetCustomScriptsAsync(profile.Id,
        new(new string('x', 65 * 1024), "Write-Output '{}'", "exit 0"))).Code == "CustomScriptsInvalid",
        "oversized scripts were accepted");
    Require((await manager.SetCustomScriptsAsync(profile.Id, Scripts())).Ok, "scripts were not saved");
    Require(data.HasCustomScripts(profile.Id), "protected script record missing");
    var protectedFile = Directory.GetFiles(Path.Combine(root, "lifecycle-data"), "custom-scripts-*.protected").Single();
    Require(!File.ReadAllText(protectedFile).Contains("Synthetic custom status", StringComparison.Ordinal),
        "script was stored as plaintext");

    Require((await manager.StartAsync(profile.Id)).Ok, "custom start failed");
    try
    {
        var ready = await WaitForState(manager, profile.Id, "Ready");
        Require(ready.OnlinePlayers == 0 && ready.MaxPlayers == 4, "script count was not displayed");
        Require(ready.PlayerNames?.SequenceEqual(["Alice", "Bob"]) == true, "script player list was not displayed");
        Require(!ready.PlayerCountTrusted, "script count was marked authoritative");
        Require(ready.AutoShutdownAtUtc is null && ready.AutoShutdownReason?.Contains("display-only") == true,
            "custom zero count started automatic shutdown");
        using var permit = RemoteStopSafety.TryAcquire(await manager.SnapshotAsync(), profile.Id, data, games);
        Require(!permit.Allowed && permit.Code == "PlayerCountUntrusted", "custom zero count authorized Friend Stop");
        Require((await manager.SetCustomScriptsAsync(profile.Id, Scripts())).Code == "ProfileInUse",
            "running scripts were editable");
        activeManager = new HostManager(data, games);
        var reattached = (await activeManager.SnapshotAsync()).Runs.Single(run => run.ProfileId == profile.Id);
        Require(reattached.State == "Ready" && reattached.OnlinePlayers == 0 && !reattached.PlayerCountTrusted,
            "a restarted Host did not reattach the exact custom Start wrapper safely");
    }
    finally
    {
        var stopped = await activeManager.StopAsync(profile.Id);
        Require(stopped.Ok, $"custom cleanup failed: {stopped.Code} {stopped.Message}");
    }
    Require((await activeManager.UpdateSettingsAsync(new HostSettings { Profiles = [] })).Ok, "profile removal failed");
    Require(!data.HasCustomScripts(profile.Id), "removing a custom profile retained its protected scripts");
});

await Check("invalid status fails closed and local stop remains available", async () =>
{
    using var data = new LocalData(Path.Combine(root, "invalid-status-data"));
    var manager = new HostManager(data);
    var profile = Profile("invalid-status", FreePort());
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] })).Ok, "settings failed");
    Require((await manager.SetCustomScriptsAsync(profile.Id, Scripts("Write-Output 'not json'"))).Ok, "scripts failed");
    Require((await manager.StartAsync(profile.Id)).Ok, "start failed");
    try
    {
        var unknown = await WaitForState(manager, profile.Id, "Unknown");
        Require(unknown.OnlinePlayers is null && !unknown.PlayerCountTrusted, "invalid status did not fail closed");
    }
    finally
    {
        var stopped = await manager.StopAsync(profile.Id);
        Require(stopped.Ok, $"local custom Stop failed: {stopped.Code} {stopped.Message}");
    }

    Require((await manager.SetCustomScriptsAsync(profile.Id,
        Scripts("Write-Output ('x' * (64 * 1024 + 1))"))).Ok, "oversized-output scripts failed");
    Require((await manager.StartAsync(profile.Id)).Ok, "oversized-output start failed");
    try
    {
        var oversized = await WaitForState(manager, profile.Id, "Unknown");
        Require(oversized.Detail.Contains("too much output", StringComparison.OrdinalIgnoreCase),
            "oversized status output did not fail closed");
    }
    finally
    {
        var stopped = await manager.StopAsync(profile.Id);
        Require(stopped.Ok, $"oversized-output Stop failed: {stopped.Code} {stopped.Message}");
    }

    Require((await manager.SetCustomScriptsAsync(profile.Id,
        Scripts("Start-Sleep -Seconds 8"))).Ok, "timeout scripts failed");
    Require((await manager.StartAsync(profile.Id)).Ok, "timeout start failed");
    var timeoutWatch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var timedOut = await WaitForState(manager, profile.Id, "Unknown");
        Require(timedOut.Detail.Contains("4 seconds", StringComparison.OrdinalIgnoreCase) &&
            timeoutWatch.Elapsed < TimeSpan.FromSeconds(7), "status timeout was not bounded near four seconds");
    }
    finally
    {
        var stopped = await manager.StopAsync(profile.Id);
        Require(stopped.Ok, $"timeout cleanup failed: {stopped.Code} {stopped.Message}");
    }
});

await Check("script count cannot replace a conflicting server", async () =>
{
    using var data = new LocalData(Path.Combine(root, "conflict-data"));
    var manager = new HostManager(data);
    var port = FreePort();
    var running = Profile("custom-running", port);
    var requested = Profile("custom-requested", port);
    Require((await manager.UpdateSettingsAsync(new HostSettings { MaxConcurrentServers = 2, Profiles = [running, requested] })).Ok,
        "settings failed");
    Require((await manager.SetCustomScriptsAsync(running.Id, Scripts())).Ok, "running scripts failed");
    Require((await manager.SetCustomScriptsAsync(requested.Id, Scripts())).Ok, "requested scripts failed");
    Require((await manager.StartAsync(running.Id)).Ok, "first start failed");
    try
    {
        await WaitForState(manager, running.Id, "Ready");
        var conflict = await manager.StartAsync(requested.Id);
        Require(conflict.Code == "PortConflict" && conflict.PortConflicts is [{ CanReplace: false }],
            "custom conflict was incorrectly replaceable");
        var replace = await manager.ReplaceEmptyPortConflictsAndStartAsync(requested.Id);
        Require(replace.Code == "PortConflictProtected", "custom scripted count authorized replacement");
    }
    finally
    {
        var stopped = await manager.StopAsync(running.Id);
        Require(stopped.Ok, $"conflict cleanup failed: {stopped.Code} {stopped.Message}");
    }
});

Console.WriteLine($"Custom game checks: {passed} passed, {failed} failed. Data: {root}");
Environment.ExitCode = failed == 0 ? 0 : 1;

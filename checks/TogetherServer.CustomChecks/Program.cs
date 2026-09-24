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

CustomScriptBundle ContractV2Scripts(string status = "") => Scripts(string.IsNullOrWhiteSpace(status) ?
    """
    $countPath = Join-Path $env:TOGETHERSERVER_WORKING_DIRECTORY 'players.txt'
    $count = if (Test-Path -LiteralPath $countPath) { [int](Get-Content -LiteralPath $countPath -Raw) } else { 0 }
    @{
      contractVersion = [int]$env:TOGETHERSERVER_CONTRACT_VERSION
      probeId = $env:TOGETHERSERVER_PROBE_ID
      operationId = $env:TOGETHERSERVER_OPERATION_ID
      state = 'Ready'
      detail = 'Synthetic contract v2 status'
      onlinePlayers = $count
      maxPlayers = 4
    } | ConvertTo-Json -Compress
    """ : status);

async Task<RunView> WaitForState(HostManager manager, Guid profileId, string state)
{
    var deadline = DateTime.UtcNow.AddSeconds(12);
    RunView? view = null;
    while (DateTime.UtcNow < deadline)
    {
        await manager.RefreshObservationsAsync();
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
        Require(ready.AutoShutdownAtUtc is null && ready.AutoShutdownReason?.Contains("certification", StringComparison.OrdinalIgnoreCase) == true,
            "custom zero count started automatic shutdown");
        using var permit = RemoteStopSafety.TryAcquire(await manager.SnapshotAsync(), profile.Id, data, games);
        Require(!permit.Allowed && permit.Code == "PlayerCountUntrusted", "custom zero count authorized Friend Stop");
        Require((await manager.SetCustomScriptsAsync(profile.Id, Scripts())).Code == "ProfileInUse",
            "running scripts were editable");
        activeManager = new HostManager(data, games);
        await activeManager.RefreshObservationsAsync();
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

await Check("owner certification enables guarded Custom lifecycle and invalidates on change", async () =>
{
    using var data = new LocalData(Path.Combine(root, "certified-lifecycle-data"));
    var games = new GameServerRegistry(data);
    var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-23T12:00:00Z"));
    var manager = new HostManager(data, games, clock);
    var port = FreePort();
    var profile = Profile("custom-certified", port);
    var requested = Profile("custom-replacement", port);
    Require((await manager.UpdateSettingsAsync(new HostSettings
    {
        MaxConcurrentServers = 2,
        AutoShutdownEnabled = true,
        IdleMinutes = 1,
        Profiles = [profile, requested]
    })).Ok, "certification settings failed");
    Require((await manager.SetCustomScriptsAsync(profile.Id, ContractV2Scripts())).Ok, "certification scripts failed");
    Require((await manager.SetCustomScriptsAsync(requested.Id, ContractV2Scripts())).Ok, "replacement scripts failed");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "players.txt"), "0");
    File.WriteAllText(Path.Combine(requested.WorldDirectory, "players.txt"), "0");

    var begun = await manager.BeginCustomCertificationAsync(profile.Id);
    Require(begun.Ok && begun.Certification.Stage == "StartingFirstRun", "certification did not begin offline");
    var firstZero = await manager.CheckCustomCertificationAsync(profile.Id);
    Require(firstZero.Ok && firstZero.Certification.Stage == "AwaitingFirstJoin",
        $"first exact zero was not observed: {firstZero.Code} {firstZero.Message} stage={firstZero.Certification.Stage}");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "players.txt"), "1");
    var firstJoin = await manager.CheckCustomCertificationAsync(profile.Id);
    Require(firstJoin.Certification.Stage == "AwaitingFirstLeave", "positive player count was not observed");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "persistent-change.txt"), "recognizable-owner-change");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "players.txt"), "0");
    var firstLeave = await manager.CheckCustomCertificationAsync(profile.Id);
    Require(firstLeave.Certification.Stage == "ConfirmFirstChange", "first return to zero was not observed");
    var restarted = await manager.ConfirmCustomCertificationAsync(profile.Id);
    Require(restarted.Ok && restarted.Certification.Stage == "AwaitingRestartReady",
        $"certification restart failed: {restarted.Code} {restarted.Message}");
    Require(File.ReadAllText(Path.Combine(profile.WorldDirectory, "persistent-change.txt")) == "recognizable-owner-change",
        "recognizable test change did not survive restart");
    var secondZero = await manager.CheckCustomCertificationAsync(profile.Id);
    Require(secondZero.Certification.Stage == "AwaitingSecondJoin", "post-restart exact zero was not observed");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "players.txt"), "1");
    var secondJoin = await manager.CheckCustomCertificationAsync(profile.Id);
    Require(secondJoin.Certification.Stage == "ConfirmSecondChange", "post-restart join was not observed");
    var saved = await manager.ConfirmCustomCertificationAsync(profile.Id);
    Require(saved.Ok && saved.Certification.Stage == "AwaitingSecondLeave", "save survival confirmation failed");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "players.txt"), "0");
    var completed = await manager.CheckCustomCertificationAsync(profile.Id);
    Require(completed.Ok && completed.Code == "CustomCertificationCompleted" && completed.Certification.Certified,
        $"certification did not complete: {completed.Code} {completed.Message}");

    var protectedCertification = Directory.GetFiles(Path.Combine(root, "certified-lifecycle-data"),
        "custom-certification-*.protected").Single();
    Require(!File.ReadAllText(protectedCertification).Contains("recognizable-owner-change", StringComparison.Ordinal),
        "certification stored private test data");
    await manager.RefreshObservationsAsync();
    var certified = await manager.SnapshotAsync();
    var certifiedRun = certified.Runs.Single(run => run.ProfileId == profile.Id);
    Require(certifiedRun.PlayerCountTrusted && certifiedRun.OnlinePlayers == 0,
        "certified contract-v2 count was not authoritative");
    var reattachedManager = new HostManager(data, games, clock);
    await reattachedManager.RefreshObservationsAsync();
    var reattachedCertified = (await reattachedManager.SnapshotAsync()).Runs.Single(run => run.ProfileId == profile.Id);
    Require(reattachedCertified.PlayerCountTrusted &&
        (await reattachedManager.SnapshotAsync()).CustomCertifications![profile.Id].Certified,
        "protected certification did not survive a Host manager restart");
    using (var permit = RemoteStopSafety.TryAcquire(certified, profile.Id, data, games))
        Require(permit.Allowed, $"certified Custom Stop was denied: {permit.Code} {permit.Reason}");

    Require(certifiedRun.AutoShutdownAtUtc is not null, "certified Custom zero count did not start idle shutdown");
    clock.Advance(TimeSpan.FromMinutes(2));
    await manager.RefreshObservationsAsync();
    var automatic = await manager.MaintainIdleShutdownAsync();
    Require(automatic.Count == 1 && automatic[0].Ok,
        "certified Custom automatic shutdown failed: " + string.Join("; ", automatic.Select(item => item.Code + " " + item.Message)));

    Require((await manager.StartAsync(profile.Id)).Ok, "certified profile did not start again");
    await WaitForState(manager, profile.Id, "Ready");
    using (var staleZeroPermit = RemoteStopSafety.TryAcquire(await manager.SnapshotAsync(), profile.Id, data, games))
    {
        Require(staleZeroPermit.Allowed, "initial certified zero permit was denied");
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "players.txt"), "1");
        var protectedRestart = await manager.RestartAsync(profile.Id, staleZeroPermit.StillSafe);
        Require(!protectedRestart.Ok && protectedRestart.Code == "PlayersOnlineOrUnknown",
            "a player appearing before the in-gate recheck did not block Custom restart");
    }
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "players.txt"), "0");
    await manager.RefreshObservationsAsync();
    using (var permit = RemoteStopSafety.TryAcquire(await manager.SnapshotAsync(), profile.Id, data, games))
    {
        Require(permit.Allowed, "certified Custom restart permit was denied");
        var remoteRestart = await manager.RestartAsync(profile.Id, permit.StillSafe);
        Require(remoteRestart.Ok, $"certified Custom restart failed: {remoteRestart.Code} {remoteRestart.Message}");
    }
    await WaitForState(manager, profile.Id, "Ready");
    var conflict = await manager.StartAsync(requested.Id);
    Require(conflict.Code == "PortConflict" && conflict.PortConflicts is [{ CanReplace: true }],
        "certified empty Custom conflict was not replaceable");
    var replaced = await manager.ReplaceEmptyPortConflictsAndStartAsync(requested.Id);
    Require(replaced.Ok && replaced.Code == "PortConflictReplaced", "certified Custom replacement failed");
    Require((await manager.StopAsync(requested.Id)).Ok, "replacement cleanup failed");

    Require((await manager.SetCustomScriptsAsync(profile.Id, ContractV2Scripts())).Ok,
        "script resave failed");
    var invalidated = (await manager.SnapshotAsync()).CustomCertifications![profile.Id];
    Require(!invalidated.Certified && invalidated.Stage == "NotCertified",
        "lifecycle-affecting script change did not invalidate certification");
});

await Check("contract v2 rejects stale wrong and contradictory proofs", async () =>
{
    async Task Exercise(string name, string status, string expected)
    {
        using var data = new LocalData(Path.Combine(root, "contract-invalid-" + name));
        var manager = new HostManager(data);
        var profile = Profile("invalid-" + name, FreePort());
        Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] })).Ok, "settings failed");
        Require((await manager.SetCustomScriptsAsync(profile.Id, ContractV2Scripts(status))).Ok, "scripts failed");
        var began = await manager.BeginCustomCertificationAsync(profile.Id);
        Require(began.Ok, "certification did not begin");
        try
        {
            var checkedStep = await manager.CheckCustomCertificationAsync(profile.Id);
            Require(!checkedStep.Ok && checkedStep.Code == "CustomContractInvalid" &&
                checkedStep.Message.Contains(expected, StringComparison.OrdinalIgnoreCase),
                $"invalid {name} proof did not fail closed: {checkedStep.Code} {checkedStep.Message}");
        }
        finally
        {
            var stopped = await manager.StopAsync(profile.Id);
            Require(stopped.Ok, $"{name} cleanup failed: {stopped.Code} {stopped.Message}");
        }
    }

    await Exercise("probe", """
      @{ contractVersion = 2; probeId = 'stale'; operationId = $env:TOGETHERSERVER_OPERATION_ID; state = 'Ready'; onlinePlayers = 0 } | ConvertTo-Json -Compress
      """, "probeId");
    await Exercise("operation", """
      @{ contractVersion = 2; probeId = $env:TOGETHERSERVER_PROBE_ID; operationId = [guid]::Empty.ToString(); state = 'Ready'; onlinePlayers = 0 } | ConvertTo-Json -Compress
      """, "operationId");
    await Exercise("contradiction", """
      @{ contractVersion = 2; probeId = $env:TOGETHERSERVER_PROBE_ID; operationId = $env:TOGETHERSERVER_OPERATION_ID; state = 'Ready'; onlinePlayers = 0; players = @('Alice') } | ConvertTo-Json -Compress
      """, "contradict");
});

Console.WriteLine($"Custom game checks: {passed} passed, {failed} failed. Data: {root}");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan by) => current = current.Add(by);
}

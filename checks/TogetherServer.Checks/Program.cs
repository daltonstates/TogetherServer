using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;
using TogetherServer;

var fixture = Path.GetFullPath("src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe");
if (!File.Exists(fixture)) throw new FileNotFoundException("Build the fixture in Release first.", fixture);
var root = Path.GetFullPath("local-data/checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}

HostSettings Settings(params ServerProfile[] profiles) => new() { MaxConcurrentServers = 2, Profiles = [.. profiles] };
ServerProfile Profile(string name, string world, int port, string? directory = null)
{
    var path = directory ?? Path.Combine(root, name);
    Directory.CreateDirectory(path);
    return new ServerProfile { Name = name, WorldId = world, WorldDirectory = path, GamePort = port, ExecutablePath = fixture };
}
void Require(bool value, string message) { if (!value) throw new Exception(message); }
int FreePort()
{
    for (var i = 0; i < 100; i++)
    {
        var port = Random.Shared.Next(35000, 59000);
        try
        {
            using var one = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            using var two = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            one.Bind(new IPEndPoint(IPAddress.Any, port));
            two.Bind(new IPEndPoint(IPAddress.Any, port + 1));
            return port;
        }
        catch (SocketException) { }
    }
    throw new Exception("No free UDP pair found.");
}
LocalData Data(string name) => new(Path.Combine(root, name));

await Check("duplicate start is serialized", async () =>
{
    using var data = Data("duplicate");
    var manager = new HostManager(data);
    var profile = Profile("duplicate-world", "duplicate-world", FreePort());
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    try
    {
        var starts = await Task.WhenAll(manager.StartAsync(profile.Id), manager.StartAsync(profile.Id));
        Require(starts.Count(result => result.Ok) == 1, "expected one successful launch");
        Require(starts.Single(result => !result.Ok).Code == "AlreadyManaged", "duplicate was not rejected");
        Require((await manager.HealthAsync(profile.Id)).Code == "FixtureProcessRunning", "fixture identity missing");
    }
    finally
    {
        var cleanup = await manager.StopAsync(profile.Id);
        Require(cleanup.Ok, $"fixture cleanup failed: {cleanup.Code} {cleanup.Message}");
    }
});

await Check("one writer per world", async () =>
{
    using var data = Data("world");
    var manager = new HostManager(data);
    var port = FreePort();
    var a = Profile("world-a", "same-world", port);
    var b = Profile("world-b", "same-world", port + 10, a.WorldDirectory);
    Require((await manager.UpdateSettingsAsync(Settings(a, b))).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try { Require((await manager.StartAsync(b.Id)).Code == "WorldConflict", "second world writer was allowed"); }
    finally { Require((await manager.StopAsync(a.Id)).Ok, "fixture cleanup failed"); }
});

await Check("separate save folders can use the same world name", async () =>
{
    using var data = Data("same-name-separate-folders");
    var manager = new HostManager(data);
    var port = FreePort();
    var a = Profile("same-name-a", "same-name", port);
    var b = Profile("same-name-b", "same-name", port + 10);
    Require((await manager.UpdateSettingsAsync(Settings(a, b))).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try { Require((await manager.StartAsync(b.Id)).Ok, "independent save folder was blocked by its world name"); }
    finally
    {
        Require((await manager.StopAsync(a.Id)).Ok, "first fixture cleanup failed");
        if (data.LoadRuns().Any(run => run.ProfileId == b.Id))
            Require((await manager.StopAsync(b.Id)).Ok, "second fixture cleanup failed");
    }
});

await Check("managed maximum", async () =>
{
    using var data = Data("maximum");
    var manager = new HostManager(data);
    var port = FreePort();
    var a = Profile("max-a", "max-a", port);
    var b = Profile("max-b", "max-b", port + 10);
    var settings = Settings(a, b);
    settings.MaxConcurrentServers = 1;
    Require((await manager.UpdateSettingsAsync(settings)).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try { Require((await manager.StartAsync(b.Id)).Code == "MaxConcurrent", "maximum was bypassed"); }
    finally { Require((await manager.StopAsync(a.Id)).Ok, "fixture cleanup failed"); }
});

await Check("managed port overlap", async () =>
{
    using var data = Data("ports");
    var manager = new HostManager(data);
    var port = FreePort();
    var a = Profile("port-a", "port-a", port);
    var b = Profile("port-b", "port-b", port + 1);
    Require((await manager.UpdateSettingsAsync(Settings(a, b))).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try { Require((await manager.StartAsync(b.Id)).Code == "PortConflict", "overlap was allowed"); }
    finally { Require((await manager.StopAsync(a.Id)).Ok, "fixture cleanup failed"); }
});

await Check("occupied local UDP port", async () =>
{
    using var data = Data("bound-port");
    var manager = new HostManager(data);
    var port = FreePort();
    var profile = Profile("bound-port", "bound-port", port);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
    socket.Bind(new IPEndPoint(IPAddress.Any, port));
    Require((await manager.StartAsync(profile.Id)).Code == "PortInUse", "occupied port was allowed");
});

await Check("restart reattaches exact fixture identity", async () =>
{
    var profile = Profile("restart", "restart", FreePort());
    using (var data = Data("restart"))
    {
        var manager = new HostManager(data);
        Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
        Require((await manager.StartAsync(profile.Id)).Ok, "start failed");
    }
    using (var data = Data("restart"))
    {
        var manager = new HostManager(data);
        Require((await manager.HealthAsync(profile.Id)).Code == "FixtureProcessRunning", "reattach identity failed");
        Require((await manager.StopAsync(profile.Id)).Ok, "reattached fixture stop failed");
    }
});

await Check("identity mismatch blocks start and never stops an unrelated process", async () =>
{
    var a = Profile("identity-a", "identity-a", FreePort());
    var b = Profile("identity-b", "identity-b", FreePort());
    ManagedRun original;
    using (var data = Data("identity-a"))
    {
        var manager = new HostManager(data);
        Require((await manager.UpdateSettingsAsync(Settings(a))).Ok, "A settings failed");
        Require((await manager.StartAsync(a.Id)).Ok, "A start failed");
        original = data.LoadRuns().Single();
    }
    using var otherData = Data("identity-b");
    var other = new HostManager(otherData);
    Require((await other.UpdateSettingsAsync(Settings(b))).Ok, "B settings failed");
    Require((await other.StartAsync(b.Id)).Ok, "B start failed");
    var unrelated = otherData.LoadRuns().Single();
    try
    {
        using var data = Data("identity-a");
        var changed = data.LoadRuns().Single();
        changed.ProcessId = unrelated.ProcessId; // A stale or corrupt PID must never become authority over B.
        data.SaveRuns([changed]);
        var manager = new HostManager(data);
        Require((await manager.HealthAsync(a.Id)).Code == "IdentityUnknown", "identity mismatch not detected");
        Require((await manager.StopAsync(a.Id)).Code == "IdentityUnknown", "unrelated process received a stop");
        Require((await manager.StartAsync(a.Id)).Code == "AlreadyManaged", "unknown world accepted a second writer");
        Require((await other.HealthAsync(b.Id)).Code == "FixtureProcessRunning", "unrelated process was stopped");
    }
    finally
    {
        await DirectFixtureStop(original);
        Require((await other.StopAsync(b.Id)).Ok, "B cleanup failed");
    }
});

await Check("game drivers are explicit and unknown games fail closed", async () =>
{
    using var data = Data("drivers");
    var registry = new GameServerRegistry(data);
    Require(registry.All.Select(driver => driver.Kind).Order().SequenceEqual(new[]
        { GameKinds.Fixture, GameKinds.MinecraftBedrock, GameKinds.MinecraftJava, GameKinds.Valheim }),
        "The built-in games and fixture were not separately registered");
    var profile = Profile("unknown-game", "unknown-game", FreePort());
    profile.Kind = "UnregisteredGame";
    var manager = new HostManager(data, registry);
    var result = await manager.UpdateSettingsAsync(Settings(profile));
    Require(!result.Ok && result.Code == "InvalidSettings", "an unregistered game profile was accepted");
});

await Check("port diagnostics show local game and Friend listeners honestly", async () =>
{
    using var data = Data("port-diagnostics");
    var gamePort = FreePort();
    using var game = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
    using var query = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
    game.Bind(new IPEndPoint(IPAddress.Any, gamePort));
    query.Bind(new IPEndPoint(IPAddress.Any, gamePort + 1));
    using var control = new TcpListener(IPAddress.Loopback, 0);
    control.Start();
    var controlPort = ((IPEndPoint)control.LocalEndpoint).Port;
    var profile = new ServerProfile { Kind = GameKinds.Valheim, Name = "Port check", WorldId = "port-check",
        WorldDirectory = root, ExecutablePath = fixture, GamePort = gamePort };
    var settings = Settings(profile);
    settings.CompanionPort = controlPort;
    settings.CompanionBindAddress = "127.0.0.1";
    settings.CompanionListeningEnabled = true;
    settings.CompanionEndpoint = $"https://1.2.3.4:{controlPort}";
    settings.PublicGameIp = "1.2.3.4";
    settings.PublicGameIpCheckedUtc = DateTimeOffset.UtcNow;
    var snapshot = new HostSnapshot(settings, [new RunView(profile.Id, "Ready", "fixture", 1)],
        "fixture", "Host", new Dictionary<Guid, bool>(), root);
    var device = new DeviceView(Guid.NewGuid(), profile.Id, [profile.Id], "Friend PC", true, false, false, true,
        DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
    var diagnostics = PortDiagnostics.Read(snapshot, new GameServerRegistry(data), true, [device]);
    Require(diagnostics.Games.Single().State == "Open on PC" &&
        diagnostics.Games.Single().RouteKind == "Direct" && diagnostics.Games.Single().Kind == GameKinds.Valheim,
        "open Valheim Steam UDP ports or their direct route were not reported");
    Require(diagnostics.Control.State == "Open on PC" && diagnostics.Control.BindScope == "Loopback only" &&
        diagnostics.Control.EndpointState == "Address hint" && diagnostics.Control.RemoteState == "Friend connected" &&
        diagnostics.Control.RemoteDetail.Contains("network location is unknown", StringComparison.Ordinal),
        "local TCP listener or authenticated Friend evidence overstated the outside-network route");
    profile.Crossplay = true;
    diagnostics = PortDiagnostics.Read(snapshot, new GameServerRegistry(data), true, [device]);
    Require(diagnostics.Games.Single().RouteKind == "Relay" && diagnostics.Games.Single().State == "Relay ready",
        "Valheim Crossplay relay was presented as direct game-port forwarding");
    profile.Crossplay = false;
    settings.CompanionBindAddress = "0.0.0.0";
    diagnostics = PortDiagnostics.Read(snapshot, new GameServerRegistry(data), true, [device]);
    Require(diagnostics.Control.State == "Closed on PC" && diagnostics.Control.RemoteState == "Not verified",
        "a listener on the wrong bind address was reported open");
    settings.CompanionBindAddress = "127.0.0.1";
    var staleDevice = device with { LastHeartbeatUtc = DateTimeOffset.UtcNow.AddSeconds(-46) };
    diagnostics = PortDiagnostics.Read(snapshot, new GameServerRegistry(data), true, [staleDevice]);
    Require(diagnostics.Control.RemoteState == "Not verified", "a stale heartbeat was reported as connected");
    diagnostics = PortDiagnostics.Read(snapshot, new GameServerRegistry(data), false, [], "TLS listener failed");
    Require(diagnostics.Control.State == "Not listening" && diagnostics.Control.Detail == "TLS listener failed",
        "an enabled but failed HTTPS listener was reported off");
    query.Dispose();
    diagnostics = PortDiagnostics.Read(snapshot, new GameServerRegistry(data), true, []);
    Require(diagnostics.Games.Single().State == "Closed on PC" && diagnostics.Control.RemoteState == "Not verified",
        "a missing UDP port or absent Friend route was claimed as open");
    using var loopbackGame = new TcpListener(IPAddress.Loopback, 0);
    loopbackGame.Start();
    var javaProfile = new ServerProfile { Kind = GameKinds.MinecraftJava, Name = "Local game",
        GamePort = ((IPEndPoint)loopbackGame.LocalEndpoint).Port };
    var javaSnapshot = new HostSnapshot(Settings(javaProfile),
        [new RunView(javaProfile.Id, "Ready", "fixture", 1)],
        "fixture", "Host", new Dictionary<Guid, bool>(), root);
    var javaPorts = PortDiagnostics.Read(javaSnapshot, new GameServerRegistry(data), false, []);
    Require(javaPorts.Games.Single().State == "Loopback only" &&
        javaPorts.Games.Single().Kind == GameKinds.MinecraftJava,
        "a loopback-only game socket was reported as available to other PCs");
    await Task.CompletedTask;
});

await Check("Windows startup and tray preference stay scoped to this user", async () =>
{
    var keyPath = @"Software\TogetherServer\Checks\" + Guid.NewGuid().ToString("N");
    var appDirectory = Path.Combine(root, "desktop preferences with spaces");
    Directory.CreateDirectory(appDirectory);
    var appPath = Path.Combine(appDirectory, "TogetherServer.exe");
    File.WriteAllText(appPath, "disposable startup path");
    var startup = new WindowsStartup(appPath, keyPath);
    try
    {
        Require(!startup.IsEnabled(), "startup was enabled before the user opted in");
        startup.SetEnabled(true);
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            Require(key?.GetValue("TogetherServer") as string == $"\"{appPath}\" --startup",
                "Windows startup did not quote the exact EXE path and tray argument");
        Require(startup.IsEnabled(), "saved startup entry was not recognized");
        var missing = new WindowsStartup(Path.Combine(appDirectory, "missing", "TogetherServer.exe"), keyPath);
        try { missing.SetEnabled(true); throw new Exception("a missing EXE was registered at sign-in"); }
        catch (InvalidOperationException) { }
        Require(startup.IsEnabled(), "an invalid path replaced the existing startup entry");
        startup.SetEnabled(false);
        Require(!startup.IsEnabled(), "disabling startup left its entry behind");
        using var data = Data("desktop-preferences");
        Require(!data.LoadDesktopPreferences().CloseToTray, "close-to-tray was enabled by default");
        data.SaveDesktopPreferences(new DesktopPreferences { CloseToTray = true });
        Require(data.LoadDesktopPreferences().CloseToTray, "close-to-tray did not persist");
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
    await Task.CompletedTask;
});

Console.WriteLine($"Checks: {passed} passed, {failed} failed. Fixture data: {root}");
return failed == 0 ? 0 : 1;

static async Task DirectFixtureStop(ManagedRun run)
{
    using var pipe = new NamedPipeClientStream(".", run.StopPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await pipe.ConnectAsync(timeout.Token);
    using var writer = new StreamWriter(pipe) { AutoFlush = true };
    await writer.WriteLineAsync("stop");
    using var process = Process.GetProcessById(run.ProcessId!.Value);
    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    await process.WaitForExitAsync(exitTimeout.Token);
}

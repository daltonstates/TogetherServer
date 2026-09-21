using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TogetherServer;

var fixture = Path.GetFullPath("src/TogetherServer.MinecraftFixture/bin/Release/net10.0/TogetherServer.MinecraftFixture.exe");
if (!File.Exists(fixture)) throw new FileNotFoundException("Build the Minecraft console fixture first.", fixture);
var root = Path.GetFullPath("local-data/minecraft-checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}

int FreePort()
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var port = Random.Shared.Next(35000, 59000);
        try
        {
            using var tcp = new TcpListener(IPAddress.Loopback, port);
            tcp.Start();
            using var udp = new UdpClient(port);
            using var ipv6 = new UdpClient(AddressFamily.InterNetworkV6);
            ipv6.Client.DualMode = false;
            ipv6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port + 1));
            return port;
        }
        catch (SocketException) { }
    }
    throw new Exception("No free TCP and UDP port found.");
}

string CopyFixture(string destination, string name)
{
    Directory.CreateDirectory(destination);
    var source = Path.GetDirectoryName(fixture)!;
    foreach (var file in Directory.GetFiles(source, "TogetherServer.MinecraftFixture.*"))
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
    var target = Path.Combine(destination, name);
    File.Copy(fixture, target, true);
    File.Copy(Path.Combine(source, "TogetherServer.MinecraftFixture.runtimeconfig.json"),
        Path.Combine(destination, Path.GetFileNameWithoutExtension(name) + ".runtimeconfig.json"), true);
    File.Copy(Path.Combine(source, "TogetherServer.MinecraftFixture.deps.json"),
        Path.Combine(destination, Path.GetFileNameWithoutExtension(name) + ".deps.json"), true);
    return target;
}

ServerProfile Profile(string kind, string name, string world, int port, bool eula = true)
{
    var serverRoot = Path.Combine(root, name);
    Directory.CreateDirectory(serverRoot);
    File.WriteAllText(Path.Combine(serverRoot, "server.properties"),
        $"level-name={world}\nserver-port={port}\nserver-portv6={port + 1}\nenable-lan-visibility=false\n");
    if (kind == GameKinds.MinecraftJava)
    {
        // Synthetic text only: this fixture does not use or accept a real game EULA.
        File.WriteAllText(Path.Combine(serverRoot, "eula.txt"), "eula=" + eula.ToString().ToLowerInvariant());
        var jar = Path.Combine(serverRoot, "fixture-server.jar");
        File.WriteAllText(jar, "disposable fixture marker, not a game binary");
        var java = CopyFixture(Path.Combine(root, "java-bin"), "java.exe");
        return new ServerProfile { Kind = kind, Name = name, WorldId = world, WorldDirectory = serverRoot,
            GamePort = port, ExecutablePath = java, Minecraft = new MinecraftOptions { ServerJarPath = jar } };
    }
    var bedrock = CopyFixture(serverRoot, "bedrock_server.exe");
    return new ServerProfile { Kind = kind, Name = name, WorldId = world, WorldDirectory = serverRoot,
        GamePort = port, ExecutablePath = bedrock, Minecraft = new MinecraftOptions() };
}

async Task Ready(HostManager manager, Guid id)
{
    for (var attempt = 0; attempt < 15; attempt++)
    {
        if ((await manager.HealthAsync(id)).Code == "MinecraftLocalStatus") return;
        await Task.Delay(200);
    }
    throw new Exception("Synthetic Minecraft status did not become ready");
}

async Task Stop(HostManager manager, ServerProfile profile)
{
    var result = await manager.StopAsync(profile.Id);
    Require(result.Ok, $"{profile.Kind} fixture stop failed: {result.Code} {result.Message}");
    Require(File.Exists(Path.Combine(profile.WorldDirectory, "stop.marker")), "stop command did not reach the synthetic console");
}

await Check("Java and Bedrock settings fail closed without prepared files", async () =>
{
    var port = FreePort();
    var java = Profile(GameKinds.MinecraftJava, "java-validation", "world", port, eula: false);
    var bedrock = Profile(GameKinds.MinecraftBedrock, "bedrock-validation", "world", port);
    using var data = new LocalData(Path.Combine(root, "validation-data"));
    var manager = new HostManager(data);
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [java, bedrock] })).Ok, "profile settings rejected");
    Require((await manager.StartAsync(java.Id)).Code == "MinecraftEulaRequired", "unprepared Java EULA was accepted");
    File.WriteAllText(Path.Combine(java.WorldDirectory, "eula.txt"), "eula=true");
    File.WriteAllText(Path.Combine(java.WorldDirectory, "server.properties"), "level-name=other\nserver-port=" + port);
    Require((await manager.StartAsync(java.Id)).Code == "MinecraftWorldMismatch", "mismatched Java world was accepted");
    File.WriteAllText(Path.Combine(bedrock.WorldDirectory, "server.properties"), "level-name=world\nserver-port=1");
    Require((await manager.StartAsync(bedrock.Id)).Code == "MinecraftPortMismatch", "mismatched Bedrock port was accepted");
    File.WriteAllText(Path.Combine(bedrock.WorldDirectory, "server.properties"),
        $"level-name=world\nserver-port={port}\nserver-portv6=invalid\n");
    Require((await manager.StartAsync(bedrock.Id)).Code == "MinecraftIpv6PortInvalid", "invalid Bedrock IPv6 port was accepted");
    File.WriteAllText(Path.Combine(bedrock.WorldDirectory, "server.properties"),
        $"level-name=world\nserver-port={port}\nserver-portv6={port + 1}\nenable-lan-visibility=true\n");
    var bedrockDriver = new GameServerRegistry(data).All.Single(driver => driver.Kind == GameKinds.MinecraftBedrock);
    var visiblePorts = bedrockDriver.Ports(bedrock);
    Require(visiblePorts.Any(item => item.Family == "IPv4" && item.Port == 19132) &&
        visiblePorts.Any(item => item.Family == "IPv6" && item.Port == 19133),
        "Bedrock LAN visibility lost its default discovery ports");
});

await Check("two games can share a world name and numeric port on different protocols", async () =>
{
    var port = FreePort();
    var java = Profile(GameKinds.MinecraftJava, "java-concurrent", "shared", port);
    var bedrock = Profile(GameKinds.MinecraftBedrock, "bedrock-concurrent", "shared", port);
    using var data = new LocalData(Path.Combine(root, "concurrent-data"));
    var manager = new HostManager(data);
    Require((await manager.UpdateSettingsAsync(new HostSettings { MaxConcurrentServers = 2, Profiles = [java, bedrock] })).Ok,
        "two game profiles were rejected");
    try
    {
        var startedJava = await manager.StartAsync(java.Id);
        Require(startedJava.Ok, $"Java fixture start failed: {startedJava.Code} {startedJava.Message}");
        await Ready(manager, java.Id);
        var startedBedrock = await manager.StartAsync(bedrock.Id);
        Require(startedBedrock.Ok, $"Bedrock fixture start failed: {startedBedrock.Code} {startedBedrock.Message}");
        await Ready(manager, bedrock.Id);
        var snapshot = await manager.SnapshotAsync();
        var ports = PortDiagnostics.Read(snapshot, new GameServerRegistry(data), false, []);
        Require(ports.Games.Count == 2 && ports.Games.All(check => check.State == "Open on PC"),
            "local TCP and UDP listeners were not reported for both games");
        var extra = Profile(GameKinds.MinecraftBedrock, "bedrock-conflict", "other", FreePort());
        File.WriteAllText(Path.Combine(extra.WorldDirectory, "server.properties"),
            $"level-name=other\nserver-port={extra.GamePort}\nserver-portv6={port + 1}\nenable-lan-visibility=false\n");
        Require((await manager.UpdateSettingsAsync(new HostSettings { MaxConcurrentServers = 3,
            Profiles = [java, bedrock, extra] })).Ok, "third Bedrock profile rejected");
        Require((await manager.StartAsync(extra.Id)).Code == "PortConflict",
            "second Bedrock run could reuse the first run's IPv6 game port");
        var pairing = new PairingService(data);
        using var javaPermit = RemoteStopSafety.TryAcquire(snapshot, java.Id, data, pairing);
        using var bedrockPermit = RemoteStopSafety.TryAcquire(snapshot, bedrock.Id, data, pairing);
        Require(!javaPermit.Allowed && !bedrockPermit.Allowed,
            "Minecraft remote Stop was enabled without game-specific player coverage evidence");
        var runs = data.LoadRuns();
        Require(runs.Count == 2 && runs.Any(run => run.Kind == GameKinds.MinecraftJava &&
            run.DeclaredPorts.Single().Protocol == "TCP") && runs.Any(run => run.Kind == GameKinds.MinecraftBedrock &&
            run.DeclaredPorts.Count == 2 && run.DeclaredPorts.Any(declared => declared.Family == "IPv4" && declared.Port == port) &&
            run.DeclaredPorts.Any(declared => declared.Family == "IPv6" && declared.Port == port + 1)),
            "game ports and Bedrock address families were not recorded per run");
        await Stop(manager, bedrock);
        await Stop(manager, java);
    }
    finally
    {
        foreach (var run in data.LoadRuns())
        {
            try
            {
                using var process = Process.GetProcessById(run.ProcessId!.Value);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                    Path.GetFullPath(process.MainModule!.FileName).Equals(run.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    process.Kill(); // Exact disposable fixture only, after a failed check.
            }
            catch (Exception) { }
        }
    }
});

await Check("older run records recover port ownership from their saved profile", async () =>
{
    var port = FreePort();
    var first = Profile(GameKinds.MinecraftJava, "java-old-a", "alpha", port);
    var second = Profile(GameKinds.MinecraftJava, "java-old-b", "beta", port);
    using var data = new LocalData(Path.Combine(root, "legacy-data"));
    var manager = new HostManager(data);
    Require((await manager.UpdateSettingsAsync(new HostSettings { MaxConcurrentServers = 2, Profiles = [first, second] })).Ok,
        "legacy profiles rejected");
    var started = await manager.StartAsync(first.Id);
    Require(started.Ok, $"legacy fixture start failed: {started.Code} {started.Message}");
    try
    {
        var runs = data.LoadRuns();
        runs.Single().DeclaredPorts.Clear();
        data.SaveRuns(runs);
        var restarted = new HostManager(data);
        Require((await restarted.StartAsync(second.Id)).Code == "PortConflict", "legacy run lost its declared TCP port");
        await Stop(restarted, first);
    }
    finally
    {
        foreach (var run in data.LoadRuns())
        {
            try
            {
                using var process = Process.GetProcessById(run.ProcessId!.Value);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                    Path.GetFullPath(process.MainModule!.FileName).Equals(run.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    process.Kill();
            }
            catch (Exception) { }
        }
    }
});

Console.WriteLine($"Minecraft checks: {passed} passed, {failed} failed. Disposable data: {root}");
return failed == 0 ? 0 : 1;

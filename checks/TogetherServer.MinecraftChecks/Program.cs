using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

if (MinecraftConsoleCapture.IsCommand(args))
{
    Environment.ExitCode = await MinecraftConsoleCapture.RunAsync(args);
    return Environment.ExitCode;
}

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
        if (GameServerRegistry.PortsAvailable([
            new("TCP", port, "Java game"),
            new("UDP", port, "Bedrock IPv4 game", "IPv4"),
            new("UDP", port + 1, "Bedrock IPv6 game", "IPv6")], PortProbeMode.LoopbackOnly))
            return port;
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

void WriteVanillaJar(string path)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using (var manifest = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open(), Encoding.ASCII))
        manifest.Write("Manifest-Version: 1.0\nMain-Class: net.minecraft.server.Main\n\n");
    using var payload = zip.CreateEntry("net/minecraft/server/Main.class").Open();
    payload.Write(new byte[256]);
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
        var jar = Path.Combine(serverRoot, "server.jar");
        WriteVanillaJar(jar);
        File.WriteAllText(Path.Combine(serverRoot, ".togetherserver-java.json"), JsonSerializer.Serialize(new
        {
            version = "fixture",
            sha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(jar)))
        }));
        var java = CopyFixture(Path.Combine(root, "java-bin"), "java.exe");
        return new ServerProfile
        {
            Kind = kind,
            Name = name,
            WorldId = world,
            WorldDirectory = serverRoot,
            GamePort = port,
            ExecutablePath = java,
            Minecraft = new MinecraftOptions { ServerJarPath = jar }
        };
    }
    var bedrock = CopyFixture(serverRoot, "bedrock_server.exe");
    return new ServerProfile
    {
        Kind = kind,
        Name = name,
        WorldId = world,
        WorldDirectory = serverRoot,
        GamePort = port,
        ExecutablePath = bedrock,
        Minecraft = new MinecraftOptions()
    };
}

async Task Ready(HostManager manager, Guid id)
{
    for (var attempt = 0; attempt < 15; attempt++)
    {
        await manager.RefreshObservationsAsync();
        if ((await manager.SnapshotAsync()).Runs.Single(run => run.ProfileId == id).State == "Ready") return;
        await Task.Delay(200);
    }
    throw new Exception("Synthetic Minecraft status did not become ready");
}

async Task Stop(HostManager manager, ServerProfile profile)
{
    var marker = Path.Combine(profile.WorldDirectory, "stop.marker");
    if (File.Exists(marker)) File.Delete(marker);
    var result = await manager.StopAsync(profile.Id);
    Require(result.Ok, $"{profile.Kind} fixture stop failed: {result.Code} {result.Message}");
    Require(File.Exists(marker), "stop command did not reach the synthetic console");
}

async Task<ServerLogResult> WaitForLogs(ServerLogService logs, Guid profileId,
    ServerLogAudience audience, Func<ServerLogResult, bool> ready)
{
    ServerLogResult? last = null;
    for (var attempt = 0; attempt < 50; attempt++)
    {
        last = await logs.ReadAsync(profileId, new(Limit: 200), audience);
        if (ready(last)) return last;
        await Task.Delay(100);
    }
    throw new Exception("Managed Minecraft log did not become available: " + last?.Code + " " + last?.Message);
}

async Task WaitForExit(int? processId, long? startedUtcTicks)
{
    if (processId is null || startedUtcTicks is null) return;
    for (var attempt = 0; attempt < 50; attempt++)
    {
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startedUtcTicks) return;
        }
        catch (ArgumentException) { return; }
        await Task.Delay(100);
    }
    throw new Exception("Minecraft console capture host did not exit after its exact game process.");
}

await Check("Java and Bedrock settings fail closed without prepared files", async () =>
{
    var port = FreePort();
    var java = Profile(GameKinds.MinecraftJava, "java-validation", "world", port, eula: false);
    var bedrock = Profile(GameKinds.MinecraftBedrock, "bedrock-validation", "world", port);
    using var data = new LocalData(Path.Combine(root, "validation-data"));
    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [java, bedrock] })).Ok, "profile settings rejected");
    Require((await manager.StartAsync(java.Id)).Code == "MinecraftEulaRequired", "unprepared Java EULA was accepted");
    File.WriteAllText(Path.Combine(java.WorldDirectory, "eula.txt"), "eula=true");
    var paper = Path.Combine(java.WorldDirectory, "paper-1.21.jar");
    File.Copy(java.Minecraft!.ServerJarPath, paper);
    java.Minecraft.ServerJarPath = paper;
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [java, bedrock] })).Ok,
        "modded JAR validation settings failed");
    Require((await manager.StartAsync(java.Id)).Code == "MinecraftVanillaJarRequired", "a modded Java JAR was accepted");
    java.Minecraft.ServerJarPath = Path.Combine(java.WorldDirectory, "server.jar");
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [java, bedrock] })).Ok,
        "vanilla JAR validation settings failed");
    File.WriteAllText(Path.Combine(java.WorldDirectory, "server.properties"), "level-name=other\nserver-port=" + port);
    Require((await manager.StartAsync(java.Id)).Code == "MinecraftWorldMismatch", "mismatched Java world was accepted");
    File.WriteAllText(Path.Combine(bedrock.WorldDirectory, "server.properties"), "level-name=world\nserver-port=1");
    Require((await manager.StartAsync(bedrock.Id)).Code == "MinecraftPortMismatch", "mismatched Bedrock port was accepted");
    File.WriteAllText(Path.Combine(bedrock.WorldDirectory, "server.properties"),
        $"level-name=world\nserver-port={port}\nserver-portv6=invalid\n");
    Require((await manager.StartAsync(bedrock.Id)).Code == "MinecraftIpv6PortInvalid", "invalid Bedrock IPv6 port was accepted");
    File.WriteAllText(Path.Combine(bedrock.WorldDirectory, "server.properties"),
        $"level-name=world\nserver-port={port}\nserver-portv6={port + 1}\nenable-lan-visibility=true\n");
    var bedrockDriver = new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly).All.Single(driver => driver.Kind == GameKinds.MinecraftBedrock);
    var visiblePorts = bedrockDriver.Ports(bedrock);
    Require(visiblePorts.Any(item => item.Family == "IPv4" && item.Port == 19132) &&
        visiblePorts.Any(item => item.Family == "IPv6" && item.Port == 19133),
        "Bedrock LAN visibility lost its default discovery ports");
});

await Check("known pre-game capture failure clears only its exact run and permits retry", async () =>
{
    var profile = Profile(GameKinds.MinecraftJava, "java-pre-game-failure", "world", FreePort());
    File.WriteAllText(profile.ExecutablePath, "not a Windows executable");
    using var data = new LocalData(Path.Combine(root, "pre-game-failure-data"));
    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] })).Ok,
        "pre-game failure profile was rejected");
    var failedLaunch = await manager.StartAsync(profile.Id);
    Require(!failedLaunch.Ok && failedLaunch.Code == "LaunchFailed" && data.LoadRuns().Count == 0,
        "a capture host that proved no game started left an unresolved managed run");

    File.Copy(fixture, profile.ExecutablePath, true);
    var retry = await manager.StartAsync(profile.Id);
    Require(retry.Ok, $"known-safe launch cleanup did not permit retry: {retry.Code} {retry.Message}");
    try
    {
        await Ready(manager, profile.Id);
        var exactRun = data.LoadRuns().Single();
        await Stop(manager, profile);
        await WaitForExit(exactRun.ConsoleCaptureProcessId, exactRun.ConsoleCaptureStartTimeUtcTicks);
    }
    finally
    {
        foreach (var run in data.LoadRuns())
        {
            try
            {
                using var process = Process.GetProcessById(run.ProcessId!.Value);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                    Path.GetFullPath(process.MainModule!.FileName).Equals(run.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase))
                    process.Kill();
            }
            catch (Exception) { }
        }
    }
});

await Check("ambiguous or live capture identity keeps the unresolved run", () =>
{
    using var liveCapture = Process.GetCurrentProcess();
    var captureStarted = liveCapture.StartTime.ToUniversalTime().Ticks;
    var capturePath = Path.GetFullPath(liveCapture.MainModule!.FileName);
    var unresolved = new ManagedRun
    {
        OperationId = Guid.NewGuid(),
        ConsoleCaptureProcessId = liveCapture.Id,
        ConsoleCaptureStartTimeUtcTicks = captureStarted,
        ConsoleCaptureExecutablePath = capturePath
    };
    var liveCleared = WindowsConsoleProcess.TryClearExitedMinecraftCapture(unresolved,
        liveCapture, captureStarted, capturePath, waitMilliseconds: 0);
    Require(!liveCleared && unresolved.ConsoleCaptureProcessId == liveCapture.Id,
        "a still-running capture identity was cleared as a known-safe launch failure");

    unresolved.ProcessId = Environment.ProcessId;
    var gameIdentityCleared = WindowsConsoleProcess.TryClearExitedMinecraftCapture(unresolved,
        liveCapture, captureStarted, capturePath, waitMilliseconds: 0);
    Require(!gameIdentityCleared && unresolved.ProcessId == Environment.ProcessId &&
        unresolved.ConsoleCaptureProcessId == liveCapture.Id,
        "a run with a possible game identity was cleared after launch failure");
    return Task.CompletedTask;
});

await Check("Java and Bedrock logs are exact-run bounded, typed, and sanitized", async () =>
{
    foreach (var kind in new[] { GameKinds.MinecraftJava, GameKinds.MinecraftBedrock })
    {
        var name = kind == GameKinds.MinecraftJava ? "java-logs" : "bedrock-logs";
        var profile = Profile(kind, name, "world", FreePort());
        using var data = new LocalData(Path.Combine(root, name + "-data"));
        var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
        Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] })).Ok,
            kind + " log profile was rejected");
        var started = await manager.StartAsync(profile.Id);
        Require(started.Ok, $"{kind} log fixture start failed: {started.Code} {started.Message}");
        try
        {
            await Ready(manager, profile.Id);
            var exactRun = data.LoadRuns().Single();
            Require(exactRun.LogPath == data.NewRunLogPath(exactRun.OperationId) &&
                exactRun.ConsoleCaptureProcessId is not null &&
                exactRun.ConsoleCaptureStartTimeUtcTicks is not null &&
                !string.IsNullOrWhiteSpace(exactRun.ConsoleCaptureExecutablePath),
                kind + " did not record its exact owned log and capture-host identity");
            var logs = new ServerLogService(data, manager);
            var host = await WaitForLogs(logs, profile.Id, ServerLogAudience.Host, result =>
                result.Ok && result.Records.Any(record => record.Message.Contains("capture complete", StringComparison.Ordinal)));
            Require(host.SourceState == ServerLogSourceStates.Active &&
                host.RunId == exactRun.OperationId.ToString("N") && host.Records.Count <= 200 &&
                host.Records.All(record => record.Message.Length <= 2048 &&
                    record.Message.All(character => !char.IsControl(character) &&
                        char.GetUnicodeCategory(character) is not (UnicodeCategory.Format or
                            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))) &&
                host.Records.Any(record => record.Stream == "Stdout") &&
                host.Records.Any(record => record.Stream == "Stderr" && record.Severity == "Warning") &&
                host.Records.Any(record => record.Stream == "Stdout" &&
                    record.Message.Contains("@TS-MINECRAFT-1", StringComparison.Ordinal)) &&
                host.Records.Any(record => record.Message.Contains("snowman=☃", StringComparison.Ordinal)) &&
                host.Records.Any(record => record.Message.EndsWith("[truncated]", StringComparison.Ordinal)),
                kind + " console framing lost UTF-8, streams, severity, bounds, or exact-run identity");
            var hostText = string.Join(' ', host.Records.Select(record => record.Message));
            Require(!hostText.Contains("host-secret", StringComparison.Ordinal) &&
                !hostText.Contains("token-secret", StringComparison.Ordinal) &&
                !hostText.Contains("last-secret", StringComparison.Ordinal),
                kind + " Host log DTO exposed a protected secret");

            var friend = await logs.ReadAsync(profile.Id, new(Limit: 200), ServerLogAudience.Friend);
            var friendText = string.Join(' ', friend.Records.Select(record => record.Message));
            Require(friend.Ok && friend.RunId == exactRun.OperationId.ToString("N") &&
                friend.Records.Any(record => (record.Category is "Player" or "Chat") &&
                    record.Message.StartsWith("Player activity redacted.", StringComparison.Ordinal)) &&
                !friendText.Contains("Alice", StringComparison.Ordinal) &&
                !friendText.Contains("203.0.113.7", StringComparison.Ordinal) &&
                !friendText.Contains("198.51.100.24", StringComparison.Ordinal) &&
                !friendText.Contains("2533274790395900", StringComparison.Ordinal) &&
                !friendText.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase) &&
                !friendText.Contains("abcdefghijklmnopqrstuvwxyzABCDEF0123456789", StringComparison.Ordinal) &&
                friend.Records.All(record => record.Message.Length <= 2048 &&
                    record.Message.All(character => !char.IsControl(character))),
                kind + " Friend log DTO leaked an identity, address, path, token, or injected control");

            var first = await logs.ReadAsync(profile.Id, new(Limit: 1), ServerLogAudience.Host);
            Require(first.Ok && first.Records.Count == 1 && !string.IsNullOrWhiteSpace(first.Cursor),
                kind + " did not return a bounded cursor page");
            var oldCursor = first.Cursor;
            await Stop(manager, profile);
            await WaitForExit(exactRun.ConsoleCaptureProcessId, exactRun.ConsoleCaptureStartTimeUtcTicks);
            var ended = await WaitForLogs(logs, profile.Id, ServerLogAudience.Host,
                result => result.Ok && result.SourceState == ServerLogSourceStates.Ended);
            Require(ended.RunId == exactRun.OperationId.ToString("N"),
                kind + " did not retain the exact ended-run log");

            var restarted = await manager.StartAsync(profile.Id);
            Require(restarted.Ok, $"{kind} fixture restart failed: {restarted.Code} {restarted.Message}");
            await Ready(manager, profile.Id);
            var nextRun = data.LoadRuns().Single();
            Require(nextRun.OperationId != exactRun.OperationId,
                kind + " restart reused its managed-run operation ID");
            var stale = await logs.ReadAsync(profile.Id, new(oldCursor, 10), ServerLogAudience.Host);
            Require(!stale.Ok && stale.Code == "InvalidLogCursor" && stale.Records.Count == 0,
                kind + " accepted a cursor from another managed run");
            await Stop(manager, profile);
            await WaitForExit(nextRun.ConsoleCaptureProcessId, nextRun.ConsoleCaptureStartTimeUtcTicks);
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
    }
});

await Check("chatty capture cannot block graceful Stop or kill an unrelated process", async () =>
{
    var profile = Profile(GameKinds.MinecraftJava, "java-chatty", "world", FreePort());
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "synthetic-chatty-lines.txt"), "100000");
    var unrelatedRoot = Path.Combine(root, "unrelated-fixture");
    Directory.CreateDirectory(unrelatedRoot);
    var unrelatedPort = FreePort();
    File.WriteAllText(Path.Combine(unrelatedRoot, "server.properties"),
        $"level-name=unrelated\nserver-port={unrelatedPort}\nserver-portv6={unrelatedPort + 1}\nenable-lan-visibility=false\n");
    var unrelatedStart = new ProcessStartInfo(fixture)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = unrelatedRoot,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    using var unrelated = Process.Start(unrelatedStart) ?? throw new Exception("unrelated fixture did not start");
    var unrelatedOutput = unrelated.StandardOutput.ReadToEndAsync();
    var unrelatedError = unrelated.StandardError.ReadToEndAsync();
    using var data = new LocalData(Path.Combine(root, "chatty-data"));
    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] })).Ok,
        "chatty profile was rejected");
    try
    {
        var started = await manager.StartAsync(profile.Id);
        Require(started.Ok, $"chatty fixture start failed: {started.Code} {started.Message}");
        await Ready(manager, profile.Id);
        var exactRun = data.LoadRuns().Single();
        var timer = Stopwatch.StartNew();
        await Stop(manager, profile);
        timer.Stop();
        await WaitForExit(exactRun.ConsoleCaptureProcessId, exactRun.ConsoleCaptureStartTimeUtcTicks);
        unrelated.Refresh();
        Require(!unrelated.HasExited, "graceful Stop terminated an unrelated fixture process");
        Require(timer.Elapsed < TimeSpan.FromSeconds(30),
            "chatty stdout/stderr prevented the fixed graceful stop from completing promptly");
        var log = new FileInfo(exactRun.LogPath);
        Require(log.Exists && log.Length <= 8L * 1024 * 1024,
            "rapid console capture exceeded its per-run storage bound");
        var response = await new ServerLogService(data, manager)
            .ReadAsync(profile.Id, new(Limit: 25), ServerLogAudience.Host);
        Require(response.Ok && response.SourceState == ServerLogSourceStates.Ended &&
            response.Records.Count <= 25 &&
            response.Records.All(record => record.Message.Length <= 2048),
            "rapid log response exceeded its record or message bounds");
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
        unrelated.Refresh();
        if (!unrelated.HasExited)
        {
            await unrelated.StandardInput.WriteLineAsync("stop");
            await unrelated.StandardInput.FlushAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await unrelated.WaitForExitAsync(cleanup.Token); }
            catch (OperationCanceledException) { unrelated.Kill(); }
        }
        await Task.WhenAll(unrelatedOutput, unrelatedError);
    }
});

await Check("missing display log cannot change Minecraft lifecycle authority", async () =>
{
    var profile = Profile(GameKinds.MinecraftBedrock, "bedrock-log-failure", "world", FreePort());
    using var data = new LocalData(Path.Combine(root, "log-failure-data"));
    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] })).Ok,
        "log-failure profile was rejected");
    var started = await manager.StartAsync(profile.Id);
    Require(started.Ok, $"log-failure fixture start failed: {started.Code} {started.Message}");
    try
    {
        await Ready(manager, profile.Id);
        var exactRun = data.LoadRuns().Single();
        File.Delete(exactRun.LogPath);
        var unavailable = await new ServerLogService(data, manager)
            .ReadAsync(profile.Id, new(), ServerLogAudience.Friend);
        Require(!unavailable.Ok && unavailable.Code == "LogNotCreated" &&
            unavailable.SourceState == ServerLogSourceStates.Missing,
            "a missing active capture did not return an honest typed state");
        var snapshot = await manager.SnapshotAsync();
        Require(snapshot.Runs.Single(run => run.ProfileId == profile.Id) is
        { State: "Ready", OnlinePlayers: 0, PlayerCountTrusted: true },
            "display-log failure changed Minecraft readiness or player-count authority");
        using var permit = RemoteStopSafety.TryAcquire(snapshot, profile.Id, data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
        Require(permit.Allowed, "display-log failure changed the zero-player Stop permit");
        var marker = Path.Combine(profile.WorldDirectory, "stop.marker");
        if (File.Exists(marker)) File.Delete(marker);
        var stopped = await manager.StopAsync(profile.Id, permit.StillSafe);
        Require(stopped.Ok && File.Exists(marker),
            "display-log failure prevented the existing exact graceful Stop path");
        await WaitForExit(exactRun.ConsoleCaptureProcessId, exactRun.ConsoleCaptureStartTimeUtcTicks);
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

await Check("two games can share a world name and numeric port on different protocols", async () =>
{
    var port = FreePort();
    var java = Profile(GameKinds.MinecraftJava, "java-concurrent", "shared", port);
    var bedrock = Profile(GameKinds.MinecraftBedrock, "bedrock-concurrent", "shared", port);
    using var data = new LocalData(Path.Combine(root, "concurrent-data"));
    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(new HostSettings
    {
        MaxConcurrentServers = 2,
        AutoShutdownEnabled = true,
        IdleMinutes = 15,
        Profiles = [java, bedrock]
    })).Ok,
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
        Require(snapshot.Runs.Where(run => run.ProfileId == java.Id || run.ProfileId == bedrock.Id)
            .All(run => run.OnlinePlayers == 0 && run.MaxPlayers == 10 && run.AutoShutdownAtUtc is not null),
            "Minecraft player counts and empty-server deadlines were not exposed in the Host snapshot");
        var games = new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly);
        var ports = PortDiagnostics.Read(snapshot, games, false, []);
        Require(ports.Games.Count == 2 && ports.Games.All(check => check.State is "Open on PC" or "Loopback only"),
            "local TCP and UDP listeners were not reported for both games: " +
            string.Join(" | ", ports.Games.Select(check => $"{check.Kind}={check.State}: {check.Detail}")));
        var extra = Profile(GameKinds.MinecraftBedrock, "bedrock-conflict", "other", FreePort());
        File.WriteAllText(Path.Combine(extra.WorldDirectory, "server.properties"),
            $"level-name=other\nserver-port={extra.GamePort}\nserver-portv6={port + 1}\nenable-lan-visibility=false\n");
        Require((await manager.UpdateSettingsAsync(new HostSettings
        {
            MaxConcurrentServers = 3,
            AutoShutdownEnabled = true,
            IdleMinutes = 15,
            Profiles = [java, bedrock, extra]
        })).Ok,
            "third Bedrock profile rejected");
        Require((await manager.StartAsync(extra.Id)).Code == "PortConflict",
            "second Bedrock run could reuse the first run's IPv6 game port");
        using var javaPermit = RemoteStopSafety.TryAcquire(snapshot, java.Id, data, games);
        using var bedrockPermit = RemoteStopSafety.TryAcquire(snapshot, bedrock.Id, data, games);
        Require(javaPermit.Allowed && bedrockPermit.Allowed,
            "Minecraft remote Stop was not enabled for verified zero-player status replies");
        File.WriteAllText(Path.Combine(java.WorldDirectory, "synthetic-online-players.txt"), "unknown");
        File.WriteAllText(Path.Combine(bedrock.WorldDirectory, "synthetic-online-players.txt"), "unknown");
        await manager.RefreshObservationsAsync();
        var unknownSnapshot = await manager.SnapshotAsync();
        Require(unknownSnapshot.Runs.Where(run => run.ProfileId == java.Id || run.ProfileId == bedrock.Id)
            .All(run => run.State == "Ready" && run.OnlinePlayers is null && run.AutoShutdownAtUtc is null),
            "valid Minecraft status replies with invalid counts did not stay Ready or cancel their countdowns");
        using var javaUnknown = RemoteStopSafety.TryAcquire(unknownSnapshot, java.Id, data, games);
        using var bedrockUnknown = RemoteStopSafety.TryAcquire(unknownSnapshot, bedrock.Id, data, games);
        Require(!javaUnknown.Allowed && javaUnknown.Code == "PlayerCountUnknown" &&
            !bedrockUnknown.Allowed && bedrockUnknown.Code == "PlayerCountUnknown",
            "Minecraft remote Stop did not fail closed for invalid player counts");
        File.WriteAllText(Path.Combine(java.WorldDirectory, "synthetic-online-players.txt"), "2");
        File.WriteAllText(Path.Combine(bedrock.WorldDirectory, "synthetic-online-players.txt"), "0");
        await manager.RefreshObservationsAsync();
        var mixedSnapshot = await manager.SnapshotAsync();
        Require(mixedSnapshot.Runs.Single(run => run.ProfileId == java.Id).AutoShutdownAtUtc is null &&
            mixedSnapshot.Runs.Single(run => run.ProfileId == bedrock.Id).AutoShutdownAtUtc is not null,
            "positive and zero Minecraft counts did not produce separate per-server countdown states");
        using var occupied = RemoteStopSafety.TryAcquire(mixedSnapshot, java.Id, data, games);
        Require(!occupied.Allowed && occupied.Code == "PlayersOnline",
            "Minecraft remote Stop accepted a server reporting online players");
        File.WriteAllText(Path.Combine(java.WorldDirectory, "synthetic-online-players.txt"), "0");
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
    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(new HostSettings { MaxConcurrentServers = 2, Profiles = [first, second] })).Ok,
        "legacy profiles rejected");
    var started = await manager.StartAsync(first.Id);
    Require(started.Ok, $"legacy fixture start failed: {started.Code} {started.Message}");
    try
    {
        var runs = data.LoadRuns();
        runs.Single().DeclaredPorts.Clear();
        data.SaveRuns(runs);
        var restarted = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
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

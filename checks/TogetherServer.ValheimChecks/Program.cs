using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TogetherServer;

var fixture = Path.GetFullPath("src/TogetherServer.ValheimFixture/bin/Release/net10.0/valheim_server.exe");
var unrelatedFixture = Path.GetFullPath("src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe");
if (!File.Exists(fixture)) throw new FileNotFoundException("Build the synthetic Valheim console fixture first.", fixture);
if (!File.Exists(unrelatedFixture)) throw new FileNotFoundException("Build the ordinary fixture first.", unrelatedFixture);
var root = Path.GetFullPath("local-data/valheim-checks/" + Guid.NewGuid().ToString("N"));
var sourceWorld = Path.Combine(root, "source-world");
Directory.CreateDirectory(Path.Combine(sourceWorld, "worlds_local"));
File.WriteAllText(Path.Combine(sourceWorld, "worlds_local", "fixture-world.db"), "synthetic database");
File.WriteAllText(Path.Combine(sourceWorld, "worlds_local", "fixture-world.fwl"), "synthetic metadata");
var world = sourceWorld;
Environment.SetEnvironmentVariable("TOGETHERSERVER_FIXTURE_ROOT", root);
var port = FreePort();
var profile = new ServerProfile { Kind = "Valheim", Name = "Synthetic Valheim", ServerName = "Fixture \"Valheim\"",
    WorldId = "fixture-world", WorldDirectory = world, GamePort = port, ExecutablePath = fixture };
var second = new ServerProfile { Kind = "Valheim", Name = "Duplicate world", ServerName = "Duplicate",
    WorldId = profile.WorldId, WorldDirectory = world, GamePort = port + 10, ExecutablePath = fixture };
var passes = 0;
int? fixturePid = null;
long? fixtureStart = null;
try
{
    var steam = Path.Combine(root, "Steam");
    var secondLibrary = Path.Combine(root, "OtherDriveLibrary");
    Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
    var installed = Path.Combine(secondLibrary, "steamapps", "common", "Valheim dedicated server");
    Directory.CreateDirectory(installed);
    File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
        "\"libraryfolders\" { \"1\" { \"path\" \"" + secondLibrary.Replace("\\", "\\\\") + "\" } }");
    File.WriteAllText(Path.Combine(secondLibrary, "steamapps", "appmanifest_896660.acf"),
        "\"AppState\" { \"installdir\" \"Valheim dedicated server\" }");
    File.WriteAllText(Path.Combine(installed, "valheim_server.exe"), "synthetic discovery marker; never executed");
    var found = ValheimSetup.ScanRoots([steam], [sourceWorld]);
    Require(found.Installations.Single().ExecutablePath == Path.Combine(installed, "valheim_server.exe"),
        "Steam library path on another root was not found");
    Require(found.Worlds.Single().Name == "fixture-world", "local world pair was not discovered");
    using (var importData = new LocalData(Path.Combine(root, "host")))
    {
        var copied = ValheimSetup.ImportCopy(importData, new ImportWorldRequest(profile.Id, sourceWorld, profile.WorldId));
        Require(copied.Ok && copied.WorldDirectory is not null, "local world import failed");
        world = copied.WorldDirectory!;
        profile.WorldDirectory = world;
        second.WorldDirectory = world;
        Require(ValheimSetup.HasWorldPair(world, profile.WorldId), "imported world pair was missing");
        Require(File.ReadAllText(Path.Combine(sourceWorld, "worlds_local", "fixture-world.db")) == "synthetic database",
            "original source world changed");
        Require(ValheimSetup.ImportCopy(importData, new ImportWorldRequest(profile.Id, sourceWorld, profile.WorldId)).Code == "AlreadyImported",
            "second import overwrote an existing world copy");
        Require(ValheimSetup.ImportCopy(importData, new ImportWorldRequest(Guid.NewGuid(), sourceWorld, "missing")).Code == "MissingWorldPair",
            "incomplete world was imported");
    }
    Console.WriteLine("PASS Steam library discovery and read-only source save import (synthetic)"); passes++;

    using (var data = new LocalData(Path.Combine(root, "host")))
    {
        var host = new HostManager(data);
        var newSeed = new ServerProfile { Kind = "Valheim", Name = "Unsafe new seed", ServerName = "Unsafe new seed",
            WorldSource = "New", WorldId = profile.WorldId, WorldDirectory = world,
            GamePort = port + 30, ExecutablePath = fixture };
        var unimported = new ServerProfile { Kind = "Valheim", Name = "Unimported source", ServerName = "Unimported source",
            WorldId = profile.WorldId, WorldDirectory = sourceWorld, GamePort = port + 40, ExecutablePath = fixture };
        var settings = new HostSettings { MaxConcurrentServers = 2, Profiles = [profile, second, newSeed, unimported] };
        Require((await host.UpdateSettingsAsync(settings)).Ok, "Valheim settings rejected");
        Require((await host.StartAsync(newSeed.Id)).Code == "WorldAlreadyExists", "new seed reused existing world files");
        Require((await host.StartAsync(unimported.Id)).Code == "WorldImportRequired", "source save was allowed to be started directly");
        Require((await host.StartAsync(profile.Id)).Code == "PasswordRequired", "passwordless start was allowed");
        Require((await host.SetValheimPasswordAsync(profile.Id, "fixture-pass-123")).Ok, "protected password failed");
        Require(data.HasValheimPassword(profile.Id), "protected password was not stored");
        var metadata = Path.Combine(world, "worlds_local", "fixture-world.fwl");
        var withheld = metadata + ".withheld";
        File.Move(metadata, withheld);
        try { Require((await host.StartAsync(profile.Id)).Code == "MissingWorldPair", "missing world pair created a new seed"); }
        finally { File.Move(withheld, metadata); }
        Console.WriteLine("PASS Valheim profile and protected password gate (synthetic)"); passes++;

        var started = await host.StartAsync(profile.Id);
        Require(started.Ok && started.Code == "ValheimStarting", $"synthetic launch failed: {started.Code} {started.Message}");
        var recorded = data.LoadRuns().Single();
        fixturePid = recorded.ProcessId;
        fixtureStart = recorded.StartTimeUtcTicks;
        Require(recorded.Kind == "Valheim" && recorded.ExecutablePath == fixture && recorded.WorldDirectory == world,
            "managed process/world identity was not recorded");
        await WaitForReady(host, profile.Id);
        Require((await host.HealthAsync(profile.Id)).Code == "ValheimLogReady", "server-connected log was not recognized");
        Require((await host.StartAsync(profile.Id)).Code == "AlreadyManaged", "duplicate Valheim start was accepted");
        Require((await host.StartAsync(second.Id)).Code == "WorldConflict", "second writer to same world was accepted");
        Require(!File.Exists(Path.Combine(world, "synthetic-stop.marker")), "fixture wrote a stop marker before Ctrl+C");
        Console.WriteLine("PASS typed launch, log readiness, duplicate and world guards (synthetic)"); passes++;
    }

    using (var data = new LocalData(Path.Combine(root, "host")))
    {
        var host = new HostManager(data);
        Require((await host.HealthAsync(profile.Id)).Code == "ValheimLogReady", "Host restart lost exact process/readiness identity");
        var unrelatedWorld = Path.Combine(root, "unrelated-world");
        Directory.CreateDirectory(unrelatedWorld);
        using var unrelatedData = new LocalData(Path.Combine(root, "unrelated-host"));
        var unrelatedHost = new HostManager(unrelatedData);
        var unrelatedProfile = new ServerProfile { Name = "Unrelated fixture", WorldId = "unrelated",
            WorldDirectory = unrelatedWorld, GamePort = port + 20, ExecutablePath = unrelatedFixture };
        Require((await unrelatedHost.UpdateSettingsAsync(new HostSettings { Profiles = [unrelatedProfile] })).Ok,
            "unrelated fixture settings failed");
        Require((await unrelatedHost.StartAsync(unrelatedProfile.Id)).Ok, "unrelated fixture start failed");
        try
        {
            var stopped = await host.StopAsync(profile.Id);
            Require(stopped.Ok, $"synthetic Ctrl+C stop failed: {stopped.Code} {stopped.Message}");
            Require((await unrelatedHost.HealthAsync(unrelatedProfile.Id)).Code == "FixtureProcessRunning",
                "Ctrl+C stopped an unrelated fixture process");
        }
        finally { Require((await unrelatedHost.StopAsync(unrelatedProfile.Id)).Ok, "unrelated fixture cleanup failed"); }
        Require(File.ReadAllText(Path.Combine(world, "synthetic-stop.marker")) == "Ctrl+C received",
            "synthetic console fixture did not receive Ctrl+C");
        Require((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == profile.Id).State == "Offline",
            "stopped Valheim record was not cleared");
        Console.WriteLine("PASS Host restart, isolated Ctrl+C exit, unrelated process survives (synthetic)"); passes++;

        var keep = Path.Combine(world, "existing-world-data.txt");
        File.WriteAllText(keep, "preserve this test file");
        var restarted = await host.StartAsync(profile.Id);
        Require(restarted.Ok, "Valheim fixture did not restart");
        var secondRun = data.LoadRuns().Single();
        fixturePid = secondRun.ProcessId;
        fixtureStart = secondRun.StartTimeUtcTicks;
        await WaitForReady(host, profile.Id);
        Require((await host.StopAsync(profile.Id)).Ok, "second synthetic Ctrl+C stop failed");
        Require(File.ReadAllText(keep) == "preserve this test file", "existing test world file changed");
        Require(File.ReadAllText(Path.Combine(sourceWorld, "worlds_local", "fixture-world.db")) == "synthetic database",
            "starting the imported copy changed the source world");
        Console.WriteLine("PASS synthetic restart preserves an unrelated world file"); passes++;
    }
    Console.WriteLine($"Synthetic Valheim checks: {passes} passed, 0 failed. Data: {root}");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL synthetic Valheim check after {passes} passes: {ex}");
    Console.WriteLine("Data: " + root);
    return 1;
}
finally
{
    if (fixturePid is { } pid && fixtureStart is { } ticks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks &&
                Path.GetFullPath(process.MainModule!.FileName).Equals(fixture, StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(); // Only the exact disposable synthetic fixture may be cleaned up after a failed check.
                process.WaitForExit(5000);
                Console.WriteLine("Cleaned up the exact synthetic fixture process after check failure.");
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}

static async Task WaitForReady(HostManager host, Guid id)
{
    for (var i = 0; i < 60; i++)
    {
        var state = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == id).State;
        if (state == "Ready") return;
        if (state is "Failed" or "Unknown") throw new Exception("Synthetic process failed before readiness: " + state);
        await Task.Delay(100);
    }
    throw new Exception("Synthetic server-connected log did not arrive.");
}

static int FreePort()
{
    for (var i = 0; i < 100; i++)
    {
        var port = Random.Shared.Next(36000, 55000);
        try
        {
            using var first = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            using var second = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            first.Bind(new IPEndPoint(IPAddress.Any, port));
            second.Bind(new IPEndPoint(IPAddress.Any, port + 1));
            return port;
        }
        catch (SocketException) { }
    }
    throw new Exception("No free synthetic UDP pair.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

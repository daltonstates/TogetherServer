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
var chunkedImportDirectory = "";
var chunkedProfileId = Guid.NewGuid();
try
{
    Require(GameConnection.JoinAddress(profile, "1.2.3.4") == $"1.2.3.4:{port}" &&
        GameConnection.JoinAddress(profile, "127.0.0.1") is null &&
        GameConnection.JoinAddress(profile, "192.168.1.2") is null &&
        GameConnection.JoinAddress(profile, "100.64.1.2") is null &&
        GameConnection.JoinAddress(profile, "203.0.113.2") is null &&
        GameConnection.JoinAddress(new ServerProfile { Kind = "Fixture" }, "1.2.3.4") is null,
        "Game join address used an unshareable address or a synthetic profile");
    Console.WriteLine("PASS public Valheim join address omits local, shared, test, and fixture addresses"); passes++;

    var steam = Path.Combine(root, "Steam");
    var secondLibrary = Path.Combine(root, "OtherDriveLibrary");
    Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
    var installed = Path.Combine(secondLibrary, "steamapps", "common", "Valheim dedicated server");
    Directory.CreateDirectory(installed);
    File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
        "\"libraryfolders\" { \"1\" { \"path\" \"" + secondLibrary.Replace("\\", "\\\\") + "\" } }");
    File.WriteAllText(Path.Combine(secondLibrary, "steamapps", "appmanifest_896660.acf"),
        "\"AppState\" { \"installdir\" \"Valheim dedicated server\" }");
    File.WriteAllText(Path.Combine(secondLibrary, "steamapps", "appmanifest_892970.acf"),
        "\"AppState\" { \"installdir\" \"Valheim\" }");
    File.WriteAllText(Path.Combine(installed, "valheim_server.exe"), "synthetic discovery marker; never executed");
    var gameClient = Path.Combine(secondLibrary, "steamapps", "common", "Valheim", "valheim.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(gameClient)!);
    File.WriteAllText(gameClient, "synthetic client discovery marker; never executed");
    var chunkedSource = Path.Combine(sourceWorld, "worlds_local", "chunked-world");
    CreateChunkedWorld(chunkedSource, 7);
    var cloudRoot = Path.Combine(steam, "userdata", "synthetic-account", "892970", "remote");
    var cloudSource = Path.Combine(cloudRoot, "worlds", "V1release");
    CreateChunkedWorld(cloudSource, 107);
    var found = ValheimSetup.ScanRoots([steam], [sourceWorld]);
    Require(found.Installations.Single().ExecutablePath == Path.Combine(installed, "valheim_server.exe"),
        "Steam library path on another root was not found");
    Require(found.Clients.Single().ExecutablePath == gameClient,
        "Valheim game client in the Steam library was not found");
    Require(found.Worlds.Count == 3 &&
        found.Worlds.Any(item => item.Name == "fixture-world" && item.Format == "Pair") &&
        found.Worlds.Any(item => item.Name == "chunked-world" && item.Format == "Folder") &&
        found.Worlds.Any(item => item.Name == "V1release" && item.SourceFolder == "worlds" && item.SaveRoot == cloudRoot),
        "local pair, local folder, or Steam cloud folder was not discovered");
    var customDrive = Path.Combine(root, "synthetic-drive");
    var standardSteam = Path.Combine(customDrive, "Steam", "steamapps", "common", "Valheim dedicated server");
    var customSteam = Path.Combine(customDrive, "My Custom Steam Folder");
    var customSave = Path.Combine(customDrive, "My Valheim Saves");
    var customInstall = Path.Combine(customSteam, "steamapps", "common", "Valheim dedicated server");
    Directory.CreateDirectory(standardSteam);
    Directory.CreateDirectory(customInstall);
    Directory.CreateDirectory(Path.Combine(customSave, "worlds_local"));
    File.WriteAllText(Path.Combine(standardSteam, "valheim_server.exe"), "synthetic G-drive Steam marker; never executed");
    File.WriteAllText(Path.Combine(customInstall, "valheim_server.exe"), "synthetic discovery marker; never executed");
    File.WriteAllText(Path.Combine(customSave, "worlds_local", "custom-world.db"), "synthetic database");
    File.WriteAllText(Path.Combine(customSave, "worlds_local", "custom-world.fwl"), "synthetic metadata");
    var customFound = ValheimSetup.ScanDriveRoots([customDrive], [], []);
    Require(customFound.Installations.Count == 2 &&
        customFound.Installations.Any(item => item.ExecutablePath == Path.Combine(standardSteam, "valheim_server.exe")) &&
        customFound.Installations.Any(item => item.ExecutablePath == Path.Combine(customInstall, "valheim_server.exe")),
        "G-drive Steam and custom Steam folders under another drive root were not both found");
    Require(customFound.Worlds.Single().SaveRoot == customSave,
        "custom save folder directly under another drive root was not found");
    var selected = ValheimSetup.SelectWorldFile(Path.Combine(customSave, "worlds_local", "custom-world.db"));
    Require(selected.Ok && selected.WorldId == "custom-world" && selected.SourceSaveRoot == customSave,
        "selected world file did not resolve its save root and matching pair");
    Require(ValheimSetup.SelectWorldFile(Path.Combine(customSave, "worlds_local", "custom-world.fwl")).Ok,
        "selecting the matching .fwl file was refused");
    var deepSave = Path.Combine(customDrive, "Games", "Backups", "Valheim Saves");
    Directory.CreateDirectory(Path.Combine(deepSave, "worlds_local"));
    File.WriteAllText(Path.Combine(deepSave, "worlds_local", "deep-world.db"), "synthetic database");
    File.WriteAllText(Path.Combine(deepSave, "worlds_local", "deep-world.fwl"), "synthetic metadata");
    Require(ValheimSetup.SelectWorldFile(Path.Combine(deepSave, "worlds_local", "deep-world.db")).SourceSaveRoot == deepSave,
        "selected world in a deeply nested custom folder was not resolved");
    File.WriteAllText(Path.Combine(customSave, "worlds_local", "missing-world.db"), "synthetic incomplete save");
    Require(ValheimSetup.SelectWorldFile(Path.Combine(customSave, "worlds_local", "missing-world.db")).Code == "MissingWorldPair",
        "incomplete selected world pair was accepted");
    Directory.CreateDirectory(Path.Combine(customSave, "worlds"));
    File.WriteAllText(Path.Combine(customSave, "worlds", "legacy.db"), "synthetic legacy save");
    Require(ValheimSetup.SelectWorldFile(Path.Combine(customSave, "worlds", "legacy.db")).Code == "UnsupportedWorldFolder",
        "legacy save folder was accepted without conversion");
    var selectedFolder = ValheimSetup.SelectWorldFolder(cloudSource);
    Require(selectedFolder.Ok && selectedFolder.WorldId == "V1release" &&
        selectedFolder.SourceSaveRoot == cloudRoot && selectedFolder.SourceFolder == "worlds",
        "selected Steam cloud world folder did not resolve its source");
    Require(ValheimSetup.SelectWorldFolder(Path.Combine(cloudRoot, "WORLDS", "V1release")).SourceFolder == "worlds",
        "world folder selection did not normalize Windows path casing");
    Require(ValheimSetup.SelectWorldFolder(chunkedSource).Ok, "local chunked world folder was refused");
    var incompleteFolder = Path.Combine(sourceWorld, "worlds_local", "incomplete-world");
    CreateChunkedWorld(incompleteFolder, 1);
    File.WriteAllText(Path.Combine(incompleteFolder, "_main.2.db2"), "incomplete newer revision");
    Require(ValheimSetup.SelectWorldFolder(incompleteFolder).Code == "IncompleteWorldFolder",
        "an incomplete latest chunked revision was accepted");
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
        var folderCopy = ValheimSetup.ImportCopy(importData, new ImportWorldRequest(chunkedProfileId, sourceWorld, "chunked-world"));
        Require(folderCopy.Ok && folderCopy.WorldDirectory is not null &&
            ValheimSetup.HasWorldData(folderCopy.WorldDirectory, "chunked-world"), "local chunked world was not imported");
        chunkedImportDirectory = folderCopy.WorldDirectory!;
        var copiedFolder = Path.Combine(folderCopy.WorldDirectory!, "worlds_local", "chunked-world");
        Require(Directory.GetFiles(chunkedSource).Select(Path.GetFileName).Order().SequenceEqual(
            Directory.GetFiles(copiedFolder).Select(Path.GetFileName).Order()), "chunked copy lost a file");
        foreach (var file in Directory.GetFiles(chunkedSource))
            Require(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(copiedFolder, Path.GetFileName(file)))),
                "chunked copy changed file contents");
        Require(ValheimSetup.ImportCopy(importData, new ImportWorldRequest(chunkedProfileId, sourceWorld, "chunked-world")).Code == "AlreadyImported",
            "second chunked import overwrote the first copy");
        Require(ValheimSetup.ImportCopy(importData, new ImportWorldRequest(Guid.NewGuid(), sourceWorld, "incomplete-world")).Code == "IncompleteWorldFolder",
            "incomplete chunked world was imported");
        var cloudCopy = ValheimSetup.ImportCopy(importData, new ImportWorldRequest(Guid.NewGuid(), cloudRoot, "V1release", "WORLDS"));
        Require(cloudCopy.Ok && cloudCopy.WorldDirectory is not null &&
            File.Exists(Path.Combine(cloudCopy.WorldDirectory, "worlds_local", "V1release", "_main.107.db2")),
            "synthetic Steam cloud folder did not copy into worlds_local");
        Require(File.ReadAllText(Path.Combine(cloudSource, "_main.107.db2")) == "synthetic world database 107",
            "synthetic Steam cloud source changed");
    }
    Console.WriteLine("PASS custom-drive discovery, local pair, chunked folder and Steam cloud cache copy (synthetic)"); passes++;

    using (var data = new LocalData(Path.Combine(root, "host")))
    {
        var host = new HostManager(data);
        var newSeed = new ServerProfile { Kind = "Valheim", Name = "Unsafe new seed", ServerName = "Unsafe new seed",
            WorldSource = "New", WorldId = profile.WorldId, WorldDirectory = world,
            GamePort = port + 30, ExecutablePath = fixture };
        var unimported = new ServerProfile { Kind = "Valheim", Name = "Unimported source", ServerName = "Unimported source",
            WorldId = profile.WorldId, WorldDirectory = sourceWorld, GamePort = port + 40, ExecutablePath = fixture };
        var chunked = new ServerProfile { Id = chunkedProfileId, Kind = "Valheim", Name = "Chunked copy", ServerName = "Chunked copy",
            WorldId = "chunked-world", WorldDirectory = chunkedImportDirectory, GamePort = port + 50, ExecutablePath = fixture };
        var chunkedNewSeed = new ServerProfile { Kind = "Valheim", Name = "Unsafe chunked seed", ServerName = "Unsafe chunked seed",
            WorldSource = "New", WorldId = "chunked-world", WorldDirectory = chunkedImportDirectory,
            GamePort = port + 60, ExecutablePath = fixture };
        var chunkedUnimported = new ServerProfile { Kind = "Valheim", Name = "Unimported chunked source", ServerName = "Unimported chunked source",
            WorldId = "chunked-world", WorldDirectory = sourceWorld, GamePort = port + 70, ExecutablePath = fixture };
        var settings = new HostSettings { MaxConcurrentServers = 2,
            Profiles = [profile, second, newSeed, unimported, chunked, chunkedNewSeed, chunkedUnimported] };
        Require((await host.UpdateSettingsAsync(settings)).Ok, "Valheim settings rejected");
        Require((await host.StartAsync(newSeed.Id)).Code == "WorldAlreadyExists", "new seed reused existing world files");
        Require((await host.StartAsync(chunkedNewSeed.Id)).Code == "WorldAlreadyExists", "new seed reused an existing chunked world folder");
        Require((await host.StartAsync(unimported.Id)).Code == "WorldImportRequired", "source save was allowed to be started directly");
        Require((await host.StartAsync(chunkedUnimported.Id)).Code == "WorldImportRequired", "chunked source was allowed to be started directly");
        Require((await host.StartAsync(chunked.Id)).Code == "PasswordRequired", "complete imported chunked world did not pass the Start world guard");
        var chunkIndex = Path.Combine(chunkedImportDirectory, "worlds_local", "chunked-world", "_main.7.chunks");
        var withheldIndex = chunkIndex + ".withheld";
        File.Move(chunkIndex, withheldIndex);
        try { Require((await host.StartAsync(chunked.Id)).Code == "MissingWorldData", "incomplete imported chunked world passed Start"); }
        finally { File.Move(withheldIndex, chunkIndex); }
        Require((await host.StartAsync(profile.Id)).Code == "PasswordRequired", "passwordless start was allowed");
        Require((await host.SetValheimPasswordAsync(profile.Id, "fixture-pass-123")).Ok, "protected password failed");
        Require(data.HasValheimPassword(profile.Id), "protected password was not stored");
        var metadata = Path.Combine(world, "worlds_local", "fixture-world.fwl");
        var withheld = metadata + ".withheld";
        File.Move(metadata, withheld);
        try { Require((await host.StartAsync(profile.Id)).Code == "MissingWorldData", "missing world pair created a new seed"); }
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

static void CreateChunkedWorld(string folder, int revision)
{
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, $"_main.{revision}.db2"), $"synthetic world database {revision}");
    File.WriteAllText(Path.Combine(folder, $"_main.{revision}.fwl2"), $"synthetic world metadata {revision}");
    File.WriteAllText(Path.Combine(folder, $"_main.{revision}.chunks"), $"synthetic chunk index {revision}");
    File.WriteAllText(Path.Combine(folder, $"_main.{revision}.ok"), "ok");
    File.WriteAllText(Path.Combine(folder, $"terrain.{revision}.chunk"), $"synthetic terrain chunk {revision}");
    File.WriteAllText(Path.Combine(folder, $"players.{revision}.chunk"), $"synthetic player chunk {revision}");
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

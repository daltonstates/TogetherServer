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
    using (var lookupClient = new HttpClient(new SyntheticIpHandler("1.2.3.4\n")))
    {
        var detection = await new PublicIpLookup(lookupClient, new Uri("https://lookup.invalid/")).DetectAsync();
        Require(detection.Ok && detection.Address == "1.2.3.4", "a valid external IPv4 lookup was not accepted");
    }
    using (var lookupClient = new HttpClient(new SyntheticIpHandler("127.0.0.1")))
    {
        var detection = await new PublicIpLookup(lookupClient, new Uri("https://lookup.invalid/")).DetectAsync();
        Require(!detection.Ok && detection.Address is null, "a loopback lookup was accepted as public");
    }
    using (var lookupClient = new HttpClient(new SyntheticIpHandler(new string('1', 128))))
    {
        var detection = await new PublicIpLookup(lookupClient, new Uri("https://lookup.invalid/")).DetectAsync();
        Require(!detection.Ok && detection.Address is null, "an oversized lookup response was accepted");
    }
    Console.WriteLine("PASS bounded outbound public-IP lookup accepts only a usable IPv4 response"); passes++;

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
    File.WriteAllText(Path.Combine(installed, "valheim_server.exe"), "synthetic discovery marker; never executed");
    var chunkedSource = Path.Combine(sourceWorld, "worlds_local", "chunked-world");
    CreateChunkedWorld(chunkedSource, 7);
    var cloudRoot = Path.Combine(steam, "userdata", "synthetic-account", "892970", "remote");
    var cloudSource = Path.Combine(cloudRoot, "worlds", "V1release");
    CreateChunkedWorld(cloudSource, 107);
    var found = ValheimSetup.ScanRoots([steam], [sourceWorld]);
    Require(found.Installations.Single().ExecutablePath == Path.Combine(installed, "valheim_server.exe"),
        "Steam library path on another root was not found");
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
        await host.RecordDetectedPublicIpAsync("1.2.3.4");
        Require((await host.UpdateSettingsAsync(new HostSettings { MaxConcurrentServers = 2,
            Profiles = settings.Profiles })).Ok, "an older settings form could not be saved");
        var addressSnapshot = await host.SnapshotAsync();
        Require(addressSnapshot.Settings.PublicGameIp == "1.2.3.4" &&
            addressSnapshot.Settings.PublicGameIpCheckedUtc is not null,
            "an older settings form replaced the detected address");
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

    using (var stopData = new LocalData(Path.Combine(root, "remote-stop-host")))
    {
        var games = new GameServerRegistry(stopData);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var host = new HostManager(stopData, games, clock);
        var stopProfile = new ServerProfile { Kind = "Valheim", Name = "Restricted synthetic world",
            ServerName = "Fixture \"Valheim\"", WorldSource = "New", WorldId = "fixture-world",
            GamePort = FreePort(), ExecutablePath = fixture };
        stopProfile.WorldDirectory = stopData.NewWorldDirectory(stopProfile.Id);
        Require((await host.UpdateSettingsAsync(new HostSettings { Profiles = [stopProfile] })).Ok,
            "restricted synthetic profile was rejected");
        Require((await host.SetValheimPasswordAsync(stopProfile.Id, "fixture-pass-123")).Ok,
            "restricted synthetic password was rejected");
        using (var offline = RemoteStopSafety.TryAcquire(await host.SnapshotAsync(), stopProfile.Id, stopData, games))
            Require(!offline.Allowed && offline.Code == "ServerNotReady", "remote Stop was offered while the server was offline");
        var started = await host.StartAsync(stopProfile.Id);
        Require(started.Ok, "restricted synthetic server did not start");
        await WaitForReady(host, stopProfile.Id);
        try
        {
            var snapshot = await host.SnapshotAsync();
            var view = snapshot.Runs.Single(run => run.ProfileId == stopProfile.Id);
            Require(view.OnlinePlayers == 0 && view.MaxPlayers == 10,
                "Valheim's synthetic server count was not exposed in the Host snapshot");
            var playerCountPath = Path.Combine(stopProfile.WorldDirectory, "synthetic-online-players.txt");
            using (var ready = RemoteStopSafety.TryAcquire(snapshot, stopProfile.Id, stopData, games))
            {
                Require(ready.Allowed, "remote Stop was not offered when the server reported zero players");
                File.WriteAllText(playerCountPath, "unknown");
                var canceled = await host.StopAsync(stopProfile.Id, ready.StillSafe);
                Require(!canceled.Ok && canceled.Code == "PlayersOnlineOrUnknown" &&
                    !File.Exists(Path.Combine(stopProfile.WorldDirectory, "synthetic-stop.marker")),
                    "an invalid player count after approval did not cancel remote Stop before signaling");
            }
            using (var unknown = RemoteStopSafety.TryAcquire(await host.SnapshotAsync(), stopProfile.Id, stopData, games))
                Require(!unknown.Allowed && unknown.Code == "PlayerCountUnknown",
                    "remote Stop accepted an invalid Valheim player count");
            using (var ready = RemoteStopSafety.TryAcquire(await ZeroCountSnapshot(host, stopProfile.Id, playerCountPath),
                stopProfile.Id, stopData, games))
            {
                Require(ready.Allowed, "remote Stop did not recover after the server reported zero players");
                File.WriteAllText(playerCountPath, "1");
                var canceled = await host.StopAsync(stopProfile.Id, ready.StillSafe);
                Require(!canceled.Ok && canceled.Code == "PlayersOnlineOrUnknown" &&
                    !File.Exists(Path.Combine(stopProfile.WorldDirectory, "synthetic-stop.marker")),
                    "a positive player-count change after approval did not cancel remote Stop before signaling");
            }
            using (var playing = RemoteStopSafety.TryAcquire(await host.SnapshotAsync(), stopProfile.Id, stopData, games))
                Require(!playing.Allowed && playing.Code == "PlayersOnline",
                    "remote Stop accepted a server reporting an online player");
            File.WriteAllText(playerCountPath, "0");
            using var permit = RemoteStopSafety.TryAcquire(await host.SnapshotAsync(), stopProfile.Id, stopData, games);
            Require(permit.Allowed, "zero online players did not allow remote Stop");
            var stopped = await host.StopAsync(stopProfile.Id, permit.StillSafe);
            Require(stopped.Ok && stopped.Code == "ValheimStopped", "safe synthetic remote Stop did not exit through Ctrl+C");
        }
        finally
        {
            if ((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id).State != "Offline")
                await host.StopAsync(stopProfile.Id);
        }
        var worldFolder = Path.Combine(stopProfile.WorldDirectory, "worlds_local");
        Directory.CreateDirectory(worldFolder);
        File.WriteAllText(Path.Combine(worldFolder, stopProfile.WorldId + ".db"), "synthetic saved world");
        File.WriteAllText(Path.Combine(worldFolder, stopProfile.WorldId + ".fwl"), "synthetic saved metadata");
        Require((await host.StartAsync(stopProfile.Id)).Ok,
            "a TogetherServer-created world could not start again after it gained save files");
        await WaitForReady(host, stopProfile.Id);
        File.WriteAllText(Path.Combine(stopProfile.WorldDirectory, "synthetic-online-players.txt"), "1");
        var occupied = await host.SnapshotAsync();
        Require(occupied.Runs.Single(run => run.ProfileId == stopProfile.Id).OnlinePlayers == 1,
            "restarted server did not report the synthetic online player");
        Require((await host.StopAsync(stopProfile.Id)).Ok,
            "the local Host could not override the online-player remote Stop guard");
        Require(File.ReadAllText(Path.Combine(worldFolder, stopProfile.WorldId + ".db")) == "synthetic saved world",
            "restarting an app-owned world changed its disposable saved data");
        Console.WriteLine("PASS remote Stop needs two zero-player reports; local Host override and app-owned restart work"); passes++;

        var timerSettings = stopData.LoadSettings();
        timerSettings.AutoShutdownEnabled = true;
        timerSettings.IdleMinutes = 1;
        Require((await host.UpdateSettingsAsync(timerSettings)).Ok, "empty-server timer settings were rejected");
        var timerCountPath = Path.Combine(stopProfile.WorldDirectory, "synthetic-online-players.txt");
        var stopMarker = Path.Combine(stopProfile.WorldDirectory, "synthetic-stop.marker");
        File.WriteAllText(timerCountPath, "0");
        if (File.Exists(stopMarker)) File.Delete(stopMarker);
        Require((await host.StartAsync(stopProfile.Id)).Ok, "empty-server timer fixture did not start");
        await WaitForReady(host, stopProfile.Id);
        try
        {
            var first = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id);
            Require(first.OnlinePlayers == 0 && first.AutoShutdownAtUtc == clock.GetUtcNow().AddMinutes(1),
                "a zero-player Ready server did not publish its shutdown deadline");

            var invalidExtension = await host.ExtendAutoShutdownAsync(stopProfile.Id, 0);
            Require(!invalidExtension.Ok && invalidExtension.Code == "InvalidExtension",
                "a zero-minute countdown extension was accepted");
            var extended = await host.ExtendAutoShutdownAsync(stopProfile.Id, 30);
            Require(extended.Ok && extended.Snapshot.Runs.Single(run => run.ProfileId == stopProfile.Id)
                    .AutoShutdownAtUtc == clock.GetUtcNow().AddMinutes(31),
                "the Host could not extend the active countdown by an exact number of minutes");

            File.WriteAllText(timerCountPath, "1");
            var canceledExtension = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id);
            Require(canceledExtension.OnlinePlayers == 1 && canceledExtension.AutoShutdownAtUtc is null,
                "an online player did not cancel the extended countdown");
            File.WriteAllText(timerCountPath, "0");
            Require((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id)
                    .AutoShutdownAtUtc == clock.GetUtcNow().AddMinutes(1),
                "an extension leaked into the next empty-server countdown");

            var longerTimerSettings = stopData.LoadSettings();
            longerTimerSettings.IdleMinutes = 2;
            Require((await host.UpdateSettingsAsync(longerTimerSettings)).Ok &&
                (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id).AutoShutdownAtUtc == clock.GetUtcNow().AddMinutes(2),
                "changing the wait did not start a full new idle window");
            var restoredTimerSettings = stopData.LoadSettings();
            restoredTimerSettings.IdleMinutes = 1;
            Require((await host.UpdateSettingsAsync(restoredTimerSettings)).Ok &&
                (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id).AutoShutdownAtUtc == clock.GetUtcNow().AddMinutes(1),
                "restoring the wait did not start a full new idle window");

            clock.Advance(TimeSpan.FromSeconds(30));
            File.WriteAllText(timerCountPath, "1");
            var occupiedTimer = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id);
            Require(occupiedTimer.OnlinePlayers == 1 && occupiedTimer.AutoShutdownAtUtc is null,
                "an online player did not cancel the empty-server countdown");

            File.WriteAllText(timerCountPath, "0");
            var secondTimer = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id);
            Require(secondTimer.AutoShutdownAtUtc == clock.GetUtcNow().AddMinutes(1),
                "the countdown did not restart from a new zero-player observation");
            clock.Advance(TimeSpan.FromSeconds(30));
            File.WriteAllText(timerCountPath, "unknown");
            var unknownTimer = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id);
            Require(unknownTimer.OnlinePlayers is null && unknownTimer.AutoShutdownAtUtc is null,
                "an unavailable count was treated as zero or left the countdown running");

            File.WriteAllText(timerCountPath, "0");
            var third = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id);
            Require(third.AutoShutdownAtUtc == clock.GetUtcNow().AddMinutes(1),
                "the countdown did not restart after an unavailable count recovered");
            clock.Advance(TimeSpan.FromSeconds(59));
            Require((await host.MaintainIdleShutdownAsync()).Count == 0 &&
                (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id).State == "Ready",
                "automatic shutdown ran before the full idle window");
            clock.Advance(TimeSpan.FromSeconds(1));
            var automatic = await host.MaintainIdleShutdownAsync();
            Require(automatic.Count == 1 && automatic[0].Ok && automatic[0].Code == "ValheimStopped" &&
                (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id).State == "Offline" &&
                File.ReadAllText(stopMarker) == "Ctrl+C received",
                "countdown expiry did not use the final zero check and graceful Stop path");
        }
        finally
        {
            if ((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == stopProfile.Id).State != "Offline")
                await host.StopAsync(stopProfile.Id);
        }
        Console.WriteLine("PASS server-count-only countdown extends, resets for players/Unknown, and stops gracefully after a final zero check"); passes++;
    }

    using (var logData = new LocalData(Path.Combine(root, "private-log-count-host")))
    {
        var games = new GameServerRegistry(logData);
        var host = new HostManager(logData, games);
        var logProfile = new ServerProfile { Kind = "Valheim", Name = "Private synthetic world",
            ServerName = "Fixture \"Valheim\"", WorldSource = "New", WorldId = "fixture-world",
            GamePort = FreePort(), ExecutablePath = fixture, PublicListing = false, Crossplay = false };
        logProfile.WorldDirectory = logData.NewWorldDirectory(logProfile.Id);
        Directory.CreateDirectory(logProfile.WorldDirectory);
        File.WriteAllText(Path.Combine(logProfile.WorldDirectory, "synthetic-query-silent"),
            "bind the private query port without replying");
        Require((await host.UpdateSettingsAsync(new HostSettings { Profiles = [logProfile] })).Ok,
            "private synthetic profile was rejected");
        Require((await host.SetValheimPasswordAsync(logProfile.Id, "fixture-pass-123")).Ok,
            "private synthetic password was rejected");
        Require((await host.StartAsync(logProfile.Id)).Ok, "private synthetic server did not start");
        var recorded = logData.LoadRuns().Single();
        fixturePid = recorded.ProcessId;
        fixtureStart = recorded.StartTimeUtcTicks;
        await WaitForReady(host, logProfile.Id);
        try
        {
            var initial = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id);
            Require(initial.OnlinePlayers == 0 && initial.MaxPlayers == 10 &&
                initial.Detail.Contains("server log reports", StringComparison.Ordinal) &&
                initial.AutoShutdownReason == "Automatic shutdown is off.",
                "private query silence did not fall back to the owned log or explain the disabled timer");

            var timerSettings = logData.LoadSettings();
            timerSettings.AutoShutdownEnabled = true;
            timerSettings.IdleMinutes = 1;
            Require((await host.UpdateSettingsAsync(timerSettings)).Ok,
                "private log-count timer settings were rejected");
            Require((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id).AutoShutdownAtUtc is not null,
                "private zero-player log count did not start the timer");

            File.AppendAllText(recorded.LogPath, "09/22/2026 12:01:00: New connection\n");
            var partialJoin = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id);
            Require(partialJoin.OnlinePlayers is null && partialJoin.AutoShutdownAtUtc is null &&
                partialJoin.AutoShutdownReason == "Waiting for a reliable player count.",
                "an incomplete log connection was treated as zero or left the timer unexplained");

            File.AppendAllText(recorded.LogPath, "09/22/2026 12:01:01: Got connection SteamID 111111\n");
            var joined = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id);
            Require(joined.OnlinePlayers == 1 && joined.AutoShutdownAtUtc is null &&
                joined.AutoShutdownReason == "Waiting for the server to be empty.",
                "a completed log connection did not report one player and explain the stopped timer");
            File.AppendAllText(recorded.LogPath, "09/22/2026 12:01:30: Game server connected\n");
            Require((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id).OnlinePlayers == 1,
                "a repeated readiness marker reset an existing player count to zero");

            File.AppendAllText(recorded.LogPath, "09/22/2026 12:02:00: RPC_Disconnect\n");
            var partialLeave = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id);
            Require(partialLeave.OnlinePlayers is null && partialLeave.AutoShutdownAtUtc is null,
                "an incomplete log disconnection was treated as a reliable count");
            File.AppendAllText(recorded.LogPath, "09/22/2026 12:02:01: Closing socket 111111\n");
            var left = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id);
            Require(left.OnlinePlayers == 0 && left.AutoShutdownAtUtc is not null,
                "the completed log disconnection did not restore zero and a fresh timer");

            File.AppendAllText(recorded.LogPath, "09/22/2026 12:10:00: Connections 2 ZDOS:123 sent:0 recv:0\n");
            Require((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id).OnlinePlayers == 2,
                "the periodic Valheim connection checkpoint did not replace the derived count");
            File.AppendAllText(recorded.LogPath, "09/22/2026 12:15:00: Connections invalid ZDOS:123 sent:0 recv:0\n");
            Require((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id).OnlinePlayers is null,
                "a malformed Valheim connection checkpoint left a trusted count");
            File.AppendAllText(recorded.LogPath, "09/22/2026 12:20:00: Connections 0 ZDOS:123 sent:0 recv:0\n");
            var checkpointZero = await host.SnapshotAsync();
            Require(checkpointZero.Runs.Single(run => run.ProfileId == logProfile.Id).OnlinePlayers == 0,
                "the periodic Valheim zero checkpoint did not recover the count");

            using (var permit = RemoteStopSafety.TryAcquire(checkpointZero, logProfile.Id, logData, games))
            {
                Require(permit.Allowed, "the private log-backed zero did not allow a guarded remote Stop");
                File.AppendAllText(recorded.LogPath, "09/22/2026 12:21:00: New connection\n");
                var canceled = await host.StopAsync(logProfile.Id, permit.StillSafe);
                Require(!canceled.Ok && canceled.Code == "PlayersOnlineOrUnknown",
                    "an incomplete private log event did not cancel Stop during the final recheck");
            }
            File.AppendAllText(recorded.LogPath,
                "09/22/2026 12:21:01: Got connection SteamID 222222\n" +
                "09/22/2026 12:22:00: RPC_Disconnect\n" +
                "09/22/2026 12:22:01: Closing socket 222222\n");
            using var finalPermit = RemoteStopSafety.TryAcquire(await host.SnapshotAsync(), logProfile.Id, logData, games);
            Require(finalPermit.Allowed, "private log count did not recover after a complete join and leave");
            var stopped = await host.StopAsync(logProfile.Id, finalPermit.StillSafe);
            Require(stopped.Ok && stopped.Code == "ValheimStopped",
                "private log-backed guarded Stop did not use Valheim Ctrl+C");
            fixturePid = null;
            fixtureStart = null;
        }
        finally
        {
            if ((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id).State != "Offline")
                await host.StopAsync(logProfile.Id);
        }

        var crossplaySettings = logData.LoadSettings();
        crossplaySettings.Profiles.Single().Crossplay = true;
        Require((await host.UpdateSettingsAsync(crossplaySettings)).Ok,
            "stopped private profile could not be switched to the Crossplay safety case");
        Require((await host.StartAsync(logProfile.Id)).Ok, "silent-query Crossplay fixture did not start");
        recorded = logData.LoadRuns().Single();
        fixturePid = recorded.ProcessId;
        fixtureStart = recorded.StartTimeUtcTicks;
        await WaitForReady(host, logProfile.Id);
        try
        {
            var crossplay = (await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id);
            Require(crossplay.OnlinePlayers is null && crossplay.AutoShutdownAtUtc is null &&
                crossplay.Detail.Contains("not enabled for Crossplay", StringComparison.Ordinal),
                "silent Crossplay query used the unvalidated server-log fallback");
        }
        finally
        {
            if ((await host.SnapshotAsync()).Runs.Single(run => run.ProfileId == logProfile.Id).State != "Offline")
                await host.StopAsync(logProfile.Id);
            fixturePid = null;
            fixtureStart = null;
        }
        Console.WriteLine("PASS private non-Crossplay log fallback reports zero/join/leave, explains blockers, fails closed, and stays off for Crossplay"); passes++;
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

static async Task<HostSnapshot> ZeroCountSnapshot(HostManager host, Guid profileId, string countPath)
{
    File.WriteAllText(countPath, "0");
    var snapshot = await host.SnapshotAsync();
    if (snapshot.Runs.Single(run => run.ProfileId == profileId).OnlinePlayers != 0)
        throw new Exception("synthetic server did not restore a zero-player count");
    return snapshot;
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

sealed class SyntheticIpHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
}

sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
{
    private DateTimeOffset utcNow = initialUtc;
    public override DateTimeOffset GetUtcNow() => utcNow;
    public void Advance(TimeSpan value) => utcNow = utcNow.Add(value);
}

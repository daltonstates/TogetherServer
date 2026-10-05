using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using TogetherServer;

var fixture = Path.GetFullPath("src/TogetherServer.FactorioFixture/bin/Release/net10.0/factorio.exe");
if (!File.Exists(fixture)) throw new FileNotFoundException("Build the synthetic Factorio fixture first.", fixture);
var root = Path.GetFullPath("local-data/factorio-checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("TOGETHERSERVER_FACTORIO_FIXTURE_ROOT", root);
var passed = 0;
var failed = 0;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}

await Check("Factorio exact completed archive and sealed snapshot",
    () => BedrockFactorioSnapshotChecks.RunFactorioProcessAsync(root, fixture));

(int Game, int Rcon) FreePorts()
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var game = Random.Shared.Next(35000, 50000);
        var rcon = Random.Shared.Next(50001, 59000);
        if (GameServerRegistry.PortsAvailable([
            new("UDP", game, "Factorio game"), new("TCP", rcon, "Factorio local RCON")],
            PortProbeMode.ObserveOnly))
            return (game, rcon);
    }
    throw new Exception("No free synthetic Factorio ports were found.");
}

ServerProfile Profile(LocalData data, string name)
{
    var ports = FreePorts();
    var sourceRoot = Path.Combine(root, name, "original-saves");
    Directory.CreateDirectory(sourceRoot);
    var source = Path.Combine(sourceRoot, "fixture-save.zip");
    WriteSyntheticSave(source, "original synthetic save");
    var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
    var profile = new ServerProfile
    {
        Kind = GameKinds.Factorio,
        Name = name,
        ServerName = name,
        WorldId = "",
        WorldDirectory = "",
        ExecutablePath = fixture,
        GamePort = ports.Game,
        Factorio = new() { RconPort = ports.Rcon },
        Backups = new() { Enabled = true, RetentionCount = 3, MinimumFreeSpaceMb = 0 }
    };
    var imported = FactorioSetup.ImportCopy(data, profile.Id, source);
    Require(imported.Ok && imported.WorldId == "fixture-save" && imported.WorldDirectory is not null &&
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))) == originalHash,
        "Factorio save was not copied into isolated managed storage");
    profile.WorldId = imported.WorldId!;
    profile.WorldDirectory = imported.WorldDirectory!;
    return profile;
}

void WriteSyntheticSave(string path, string content)
{
    if (File.Exists(path)) File.Delete(path);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    using var writer = new StreamWriter(archive.CreateEntry("fixture-save/level.dat").Open());
    writer.Write(content);
}

async Task<RunView> WaitForReady(HostManager manager, Guid profileId, int? expectedPlayers = null)
{
    RunView? last = null;
    for (var attempt = 0; attempt < 40; attempt++)
    {
        await manager.RefreshObservationsAsync();
        last = (await manager.SnapshotAsync()).Runs.SingleOrDefault(run => run.ProfileId == profileId);
        if (last?.State == "Ready" && last.PlayerCountTrusted &&
            (expectedPlayers is null || last.OnlinePlayers == expectedPlayers)) return last;
        await Task.Delay(100);
    }
    throw new Exception("Synthetic Factorio RCON did not become ready: " + last?.Detail);
}

await Check("Factorio preview validates only an owner-installed executable, existing save, and separate RCON port", () =>
{
    using var data = new LocalData(Path.Combine(root, "validation-data"));
    var driver = new GameServerRegistry(data, false, PortProbeMode.ObserveOnly).All.Single(item => item.Kind == GameKinds.Factorio);
    var profile = Profile(data, "validation");
    var managedSave = Path.Combine(profile.WorldDirectory, profile.WorldId + ".zip");
    var managedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(managedSave)));
    var duplicateSource = Path.Combine(root, "duplicate-factorio", profile.WorldId + ".zip");
    Directory.CreateDirectory(Path.GetDirectoryName(duplicateSource)!);
    WriteSyntheticSave(duplicateSource, "different save with the same name");
    var duplicate = FactorioSetup.ImportCopy(data, profile.Id, duplicateSource);
    Require(!duplicate.Ok && duplicate.Code == "AlreadyImported" &&
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(managedSave))) == managedHash,
        "a second import overwrote the managed Factorio save copy");
    profile.ExecutablePath = Path.Combine(root, "not-factorio.exe");
    File.WriteAllText(profile.ExecutablePath, "not executable");
    Require(driver.ValidateForStart(profile)?.Code == "FactorioExecutableRequired", "wrong executable name was accepted");
    profile.ExecutablePath = fixture;
    File.Delete(Path.Combine(profile.WorldDirectory, profile.WorldId + ".zip"));
    Require(driver.ValidateForStart(profile)?.Code == "FactorioSaveRequired", "missing save ZIP was accepted");
    WriteSyntheticSave(Path.Combine(profile.WorldDirectory, profile.WorldId + ".zip"), "recreated synthetic save");
    var unmanaged = Path.Combine(root, "unmanaged-factorio-save");
    Directory.CreateDirectory(unmanaged);
    WriteSyntheticSave(Path.Combine(unmanaged, profile.WorldId + ".zip"), "unmanaged synthetic save");
    profile.WorldDirectory = unmanaged;
    Require(driver.ValidateForStart(profile)?.Code == "FactorioImportRequired", "unmanaged source save was started directly");
    profile.WorldDirectory = Path.Combine(data.FactorioServersRoot, profile.Id.ToString("N"), profile.WorldId);
    profile.Factorio!.RconPort = profile.GamePort;
    Require(driver.ValidateForStart(profile)?.Code == "FactorioRconPortInvalid", "shared game/RCON port was accepted");
    Require(!driver.SupportsCrashRecovery && driver.SupportsBackups,
        "preview capability boundary changed without acceptance");
    return Task.CompletedTask;
});

await Check("Factorio closed-archive inspector bounds paths and requires a complete readable ZIP", () =>
{
    var directory = Path.Combine(root, "archive-inspector");
    Directory.CreateDirectory(directory);
    var valid = Path.Combine(directory, "valid.zip");
    WriteSyntheticSave(valid, "bounded synthetic contents");
    using (var stream = new FileStream(valid, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var inspected = FactorioClosedArchiveCandidate.InspectArchiveContents(stream);
        Require(inspected.EntryCount == 1 && inspected.ArchiveLength == stream.Length,
            "a closed synthetic ZIP was not inspected");
    }

    void MustReject(string path, string reason)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            _ = FactorioClosedArchiveCandidate.InspectArchiveContents(stream);
            throw new Exception(reason + " was accepted");
        }
        catch (InvalidDataException) { }
    }

    var unfinished = Path.Combine(directory, "unfinished.zip");
    File.Copy(valid, unfinished);
    using (var append = new FileStream(unfinished, FileMode.Append, FileAccess.Write, FileShare.None))
        append.WriteByte(0x5A);
    MustReject(unfinished, "ZIP with bytes after its central-directory end");

    var unsafePath = Path.Combine(directory, "unsafe-path.zip");
    using (var archive = ZipFile.Open(unsafePath, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("../outside/level.dat").Open()))
        writer.Write("synthetic");
    MustReject(unsafePath, "traversal entry");

    var duplicate = Path.Combine(directory, "duplicate.zip");
    using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("world/level.dat").Open())) writer.Write("first");
        using (var writer = new StreamWriter(archive.CreateEntry("WORLD/LEVEL.DAT").Open())) writer.Write("second");
    }
    MustReject(duplicate, "case-colliding entries");
    return Task.CompletedTask;
});

await Check("Factorio live-save candidate rejects stale runs and dispatches only the fixed RCON command", async () =>
{
    using var data = new LocalData(Path.Combine(root, "live-save-candidate-data"));
    var profile = Profile(data, "live-save-candidate");
    profile.Backups.Enabled = false;
    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.ObserveOnly));
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] })).Ok,
        "Factorio candidate profile was rejected");
    var started = await manager.StartAsync(profile.Id);
    Require(started.Ok, $"Factorio candidate fixture did not start: {started.Code} {started.Message}");
    var run = data.LoadRuns().Single();
    var receivedMarker = Path.Combine(profile.WorldDirectory, "synthetic-server-save-received.marker");
    var port = new ExactManagedFactorioLiveSaveCommandPort(data);
    try
    {
        await WaitForReady(manager, profile.Id, 0);
        void RequireRejected(ManagedRun candidate, string reason)
        {
            try
            {
                port.RequestFactorioSave(candidate);
                throw new Exception(reason + " reached RCON");
            }
            catch (InvalidOperationException) { }
        }

        var wrongStart = data.LoadRuns().Single();
        wrongStart.StartTimeUtcTicks++;
        RequireRejected(wrongStart, "mismatched process start time");
        var recordedRuns = data.LoadRuns();
        var forgedRuns = data.LoadRuns();
        forgedRuns.Single().StartTimeUtcTicks++;
        data.SaveRuns(forgedRuns);
        try { RequireRejected(forgedRuns.Single(), "recorded process start time mismatch"); }
        finally { data.SaveRuns(recordedRuns); }
        var staleOperation = data.LoadRuns().Single();
        staleOperation.OperationId = Guid.NewGuid();
        RequireRejected(staleOperation, "stale operation");
        var wrongDeclaredPort = data.LoadRuns().Single();
        wrongDeclaredPort.DeclaredPorts = [new("UDP", profile.GamePort, "Game"),
            new("TCP", profile.Factorio!.RconPort + 1, "Local RCON")];
        RequireRejected(wrongDeclaredPort, "changed run RCON port");

        var settings = data.LoadSettings();
        var savedProfile = settings.Profiles.Single();
        var originalRconPort = savedProfile.Factorio!.RconPort;
        savedProfile.Factorio.RconPort = originalRconPort + 1;
        data.SaveSettings(settings);
        try { RequireRejected(run, "changed saved profile RCON port"); }
        finally
        {
            savedProfile.Factorio.RconPort = originalRconPort;
            data.SaveSettings(settings);
        }
        Require(!File.Exists(receivedMarker), "a rejected candidate reached the fixture RCON listener");

        var dispatched = port.RequestFactorioSave(run);
        Require(dispatched.OperationId == run.OperationId && dispatched.FixedCommand == "/server-save" &&
            !dispatched.CompletionConfirmed, "the candidate claimed a completed save");
        Require(File.Exists(receivedMarker) && File.ReadAllText(receivedMarker) == "authenticated /server-save",
            "the exact fixed save command did not reach authenticated fixture RCON");
        using (var process = Process.GetProcessById(run.ProcessId!.Value))
            Require(!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks,
                "the live-save command stopped or replaced the exact fixture process");

        // Structural fixture evidence only: the producer closed this disposable ZIP.
        var closedZip = Path.Combine(profile.WorldDirectory, "synthetic-live-save.zip");
        using (var stream = new FileStream(closedZip, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var entry = archive.Entries.Single();
            Require(entry.FullName == "fixture-save/level.dat" && entry.Length is > 0 and < 1024,
                "the closed synthetic ZIP did not have its expected bounded entry");
            using var reader = new StreamReader(entry.Open());
            Require(reader.ReadToEnd() == "synthetic live save", "the synthetic ZIP entry was unreadable");
        }
        var snapshot = new FactorioClosedArchiveCandidate(data).Stage(run);
        var reviewedSave = Path.Combine(profile.WorldDirectory, profile.WorldId + ".zip");
        var reviewedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(reviewedSave)));
        Require(snapshot.ProfileId == profile.Id && snapshot.OperationId == run.OperationId &&
            snapshot.ArchivePath == Path.Combine(data.RootPath, "factorio-archive-candidates",
                profile.Id.ToString("N"), run.OperationId.ToString("N"), "archive.zip") &&
            snapshot.SourceBeforeSha256 == reviewedHash && snapshot.SourceAfterSha256 == reviewedHash &&
            snapshot.StagedSha256 == reviewedHash && snapshot.EntryCount == 1 &&
            (File.GetAttributes(snapshot.ArchivePath) & FileAttributes.ReadOnly) != 0 &&
            !snapshot.CompletionConfirmed && !snapshot.LiveCaptureAccepted,
            "the closed-archive candidate was not immutable and tied to the reviewed exact-run ZIP");
        try
        {
            _ = new FactorioClosedArchiveCandidate(data).Stage(run);
            throw new Exception("a second archive replaced the first candidate");
        }
        catch (InvalidOperationException) { }
        Require(!SharedWorldLiveSaveAdapters.ForGame(GameKinds.Factorio)!.LiveCaptureAccepted &&
            !SharedWorldLiveSaveAdapters.Status(GameKinds.Factorio).Available,
            "fixture dispatch enabled live capture or sharing");
        var stopped = await manager.StopAsync(profile.Id);
        Require(stopped.Ok, $"Factorio candidate fixture did not stop: {stopped.Code} {stopped.Message}");
    }
    finally
    {
        foreach (var active in data.LoadRuns())
        {
            try
            {
                using var process = Process.GetProcessById(active.ProcessId!.Value);
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == active.StartTimeUtcTicks &&
                    Path.GetFullPath(process.MainModule!.FileName).Equals(active.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase)) process.Kill();
            }
            catch (Exception) { }
        }
    }
});

await Check("Factorio fixed launch, authenticated player count, graceful quit, backup vault, and rehearsal work", async () =>
{
    using var data = new LocalData(Path.Combine(root, "lifecycle-data"));
    var profile = Profile(data, "lifecycle");
    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.ObserveOnly));
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] })).Ok,
        "Factorio settings were rejected");
    var started = await manager.StartAsync(profile.Id);
    Require(started.Ok, $"Factorio fixture did not start: {started.Code} {started.Message}");
    var exactRun = data.LoadRuns().Single();
    try
    {
        var ready = await WaitForReady(manager, profile.Id, 0);
        Require(ready.DeclaredPorts?.Any(port => port.Protocol == "UDP" && port.Port == profile.GamePort) == true &&
            ready.DeclaredPorts.Any(port => port.Protocol == "TCP" && port.Port == profile.Factorio!.RconPort) == true,
            "Factorio game and local RCON ports were not declared");
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "synthetic-online-players.txt"), "2");
        ready = await WaitForReady(manager, profile.Id, 2);
        Require(ready.OnlinePlayers == 2 && ready.PlayerCountTrusted, "authenticated player count was not trusted");
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "synthetic-online-players.txt"), "0");
        await WaitForReady(manager, profile.Id, 0);
        var stopped = await manager.StopAsync(profile.Id);
        Require(stopped.Ok, $"Factorio graceful Stop failed: {stopped.Code} {stopped.Message}");
        Require(File.Exists(Path.Combine(profile.WorldDirectory, "synthetic-save-confirmed.marker")),
            "authenticated /quit did not reach the synthetic fixture");
        var backups = await manager.BackupsAsync(profile.Id);
        var backup = backups.Backups.Single();
        Require((await manager.VerifyBackupAsync(profile.Id, backup.Id)).Ok, "completed backup did not verify");
        var liveBytes = File.ReadAllBytes(Path.Combine(profile.WorldDirectory, profile.WorldId + ".zip"));
        var rehearsed = await manager.RehearseRestoreAsync(profile.Id, backup.Id);
        Require(rehearsed.Ok && File.ReadAllBytes(Path.Combine(profile.WorldDirectory,
            profile.WorldId + ".zip")).SequenceEqual(liveBytes), "restore rehearsal changed the live save");
        var vaultRoot = Path.Combine(root, "removable-drive-fixture");
        Directory.CreateDirectory(vaultRoot);
        var vaulted = await manager.CopyBackupToVaultAsync(profile.Id, backup.Id, vaultRoot);
        var vaultedDirectory = Path.Combine(vaultRoot, "TogetherServer Backups", profile.Id.ToString("N"),
            backup.Id.ToString("N") + ".backup");
        Require(vaulted.Ok && Directory.Exists(vaultedDirectory),
            "backup-vault copy did not verify");
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
                        StringComparison.OrdinalIgnoreCase)) process.Kill();
            }
            catch (Exception) { }
        }
    }
});

Console.WriteLine($"{passed} Factorio preview check groups passed; {failed} failed. Fixture evidence only: real game join/save acceptance remains open.");
return failed == 0 ? 0 : 1;

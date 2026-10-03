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

(int Game, int Rcon) FreePorts()
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var game = Random.Shared.Next(35000, 50000);
        var rcon = Random.Shared.Next(50001, 59000);
        if (GameServerRegistry.PortsAvailable([
            new("UDP", game, "Factorio game"), new("TCP", rcon, "Factorio local RCON")],
            PortProbeMode.LoopbackOnly))
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
    var driver = new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly).All.Single(item => item.Kind == GameKinds.Factorio);
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

await Check("Factorio fixed launch, authenticated player count, graceful quit, backup vault, and rehearsal work", async () =>
{
    using var data = new LocalData(Path.Combine(root, "lifecycle-data"));
    var profile = Profile(data, "lifecycle");
    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly));
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

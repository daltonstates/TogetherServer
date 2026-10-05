using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

internal static class WorldLoadRehearsalChecks
{
    internal static async Task RunAsync(string root, string fixture, Action<string, string> createJunction)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        using var data = new LocalData(Path.Combine(root, "world-load"));
        var games = new GameServerRegistry(data, true, PortProbeMode.ObserveOnly);
        var sourceRoot = Path.Combine(root, "world-load-source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "copy.bin"), "synthetic baseline");
        var profile = new ServerProfile { Name = "Disposable source", WorldId = "synthetic", WorldDirectory = sourceRoot, ExecutablePath = fixture };
        data.SaveSettings(new() { Profiles = [profile] });
        var backups = new WorldBackupService(data, TimeProvider.System, _ => long.MaxValue, games);
        var checkpoint = backups.Create(profile, BackupKinds.Manual, sourceRoot);
        Require(checkpoint.Ok && checkpoint.Backup is not null, "completed fixture backup missing");
        var backup = checkpoint.Backup!;
        var manager = new HostManager(data, games);
        manager.WorldLoadAvailableBytesForChecks = () => 0;
        Require((await manager.PrepareWorldLoadRehearsalAsync(profile.Id, backup.Id)).Code == "WorldLoadLowSpace", "low-space preparation accepted");
        manager.WorldLoadAvailableBytesForChecks = () => long.MaxValue;
        Require(!(await manager.PrepareWorldLoadRehearsalAsync(Guid.NewGuid(), backup.Id)).Ok, "wrong source accepted");
        var prepared = await manager.PrepareWorldLoadRehearsalAsync(profile.Id, backup.Id);
        Require(prepared.Ok && prepared.Rehearsal?.CanLaunch == true, "reviewed isolated fixture was not prepared: " + prepared.Code);
        var trial = prepared.Rehearsal!;
        Require(trial.WorldDirectory != sourceRoot && File.ReadAllText(Path.Combine(trial.WorldDirectory, "copy.bin")) == "synthetic baseline",
            "source was swapped or working copy not exact");
        Require(trial.LoadOutcome == "Unobserved" && trial.RestartOutcome == "Unobserved", "hash copy promoted to load proof");
        Require(!(await manager.StartAsync(trial.RehearsalProfileId!.Value)).Ok, "ordinary Start bypassed fixed guide");
        Require(!(await manager.SetSharedSavesAsync(trial.RehearsalProfileId.Value, true)).Ok, "rehearsal can share authority");
        static HostSettings Copy(HostSettings settings) => JsonSerializer.Deserialize<HostSettings>(JsonSerializer.Serialize(settings))!;
        var changed = Copy((await manager.SnapshotAsync()).Settings);
        changed.Profiles.Single(item => item.Id == trial.RehearsalProfileId).Maintenance.Enabled = false;
        Require(!(await manager.UpdateSettingsAsync(changed)).Ok, "rehearsal fence was editable");
        Require(!(await manager.WorldLoadRehearsalAsync(trial.Id, "confirm", new("Restart", true))).Ok, "restart confirmation skipped load");
        Require((await manager.WorldLoadRehearsalAsync(trial.Id, "confirm", new("Load", false))).Rehearsal?.LoadOutcome == "OwnerFailed",
            "failed load not retained");
        Require((await manager.WorldLoadRehearsalAsync(trial.Id, "confirm", new("Load", true, "fixture 1"))).Ok, "owner load confirmation rejected");
        Require((await manager.WorldLoadRehearsalAsync(trial.Id, "confirm", new("Change", true))).Ok, "owner change confirmation rejected");
        Require(!(await manager.WorldLoadRehearsalAsync(trial.Id, "confirm", new("Restart", true))).Ok, "unobserved managed restart accepted");
        Require(!(await manager.WorldLoadRehearsalAsync(trial.Id, "cleanup")).Ok, "cleanup skipped owner stopped confirmation");
        var linked = Path.Combine(trial.WorldDirectory, "linked");
        createJunction(linked, sourceRoot);
        Require(!(await manager.WorldLoadRehearsalAsync(trial.Id, "cleanup", confirmStopped: true)).Ok && File.Exists(Path.Combine(sourceRoot, "copy.bin")),
            "linked cleanup reached the source directory");
        Directory.Delete(linked); // Delete this exact disposable junction only, without recursion.
        var freshManager = new HostManager(data, games);
        Require((await freshManager.WorldLoadRehearsalAsync(trial.Id)).Rehearsal?.LoadOutcome == "OwnerConfirmed", "rehearsal result was not durable");
        var referenced = Copy((await manager.SnapshotAsync()).Settings);
        var other = new ServerProfile { Name = "Other disposable reference", WorldId = "other", WorldDirectory = trial.WorldDirectory, ExecutablePath = fixture };
        referenced.Profiles.Add(other);
        var referencedResult = await manager.UpdateSettingsAsync(referenced);
        Require(referencedResult.Ok, "reference fixture setup failed: " + referencedResult.Code + " " + referencedResult.Message);
        Require((await manager.WorldLoadRehearsalAsync(trial.Id, "cleanup", confirmStopped: true)).Code == "WorldLoadDirectoryInUse",
            "cleanup ignored another saved server's reference");
        referenced = Copy((await manager.SnapshotAsync()).Settings); referenced.Profiles.RemoveAll(item => item.Id == other.Id);
        Require((await manager.UpdateSettingsAsync(referenced)).Ok, "disposable reference removal failed");
        Require((await manager.WorldLoadRehearsalAsync(trial.Id, "cleanup", confirmStopped: true)).Ok && !Directory.Exists(trial.WorldDirectory), "exact cleanup failed");
        Require(File.ReadAllText(Path.Combine(sourceRoot, "copy.bin")) == "synthetic baseline" && backups.Verify(profile, backup.Id).Ok,
            "cleanup damaged original or completed backup");
        var payload = Path.Combine(data.BackupsRoot, profile.Id.ToString("N"), backup.Id.ToString("N") + ".backup", "payload", "copy.bin");
        File.WriteAllText(payload, "tampered");
        Require(!(await manager.PrepareWorldLoadRehearsalAsync(profile.Id, backup.Id)).Ok, "damaged manifest payload accepted");

        var binaryRoot = Path.Combine(root, "world-load-binary"); Directory.CreateDirectory(binaryRoot);
        var binary = Path.Combine(binaryRoot, "TogetherServer.Fixture.exe");
        File.WriteAllText(binary, "non-executable fixture hash only");
        var files = new[] { new SharedWorldFile("copy.bin", new FileInfo(Path.Combine(sourceRoot, "copy.bin")).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(sourceRoot, "copy.bin"))))) };
        var source = new VerifiedWorldLoadSource(profile.Id, GameKinds.Fixture, "synthetic", "Received", new string('A', 64),
            files, sourceRoot, new ServerProfile { ExecutablePath = binary });
        var beforeBinaryChange = await manager.PrepareReceivedWorldLoadRehearsalAsync(source);
        Require(beforeBinaryChange.Ok, "binary-bound rehearsal preparation failed");
        File.AppendAllText(binary, "changed");
        Require(!(await manager.WorldLoadRehearsalAsync(beforeBinaryChange.Rehearsal!.Id, "start")).Ok &&
            !(await manager.WorldLoadRehearsalAsync(beforeBinaryChange.Rehearsal.Id, "confirm", new("Load", true))).Ok, "changed binary was trusted");
        Require((await manager.WorldLoadRehearsalAsync(beforeBinaryChange.Rehearsal.Id, "cleanup", confirmStopped: true)).Ok, "changed binary prevented safe cleanup");
        var manual = await manager.PrepareReceivedWorldLoadRehearsalAsync(source with { InstalledProfile = null, Game = GameKinds.Terraria });
        Require(manual.Ok && !manual.Rehearsal!.CanLaunch && manual.Rehearsal.RehearsalProfileId is null, "unsafe automated game path exposed");
        Require(!(await manager.WorldLoadRehearsalAsync(manual.Rehearsal!.Id, "start")).Ok, "manual copy launched a process");
        Require((await manager.WorldLoadRehearsalAsync(manual.Rehearsal.Id, "cleanup", confirmStopped: true)).Ok, "manual copy cleanup failed");
        manager.WorldLoadAfterCopyForChecks = () => throw new IOException("simulated interruption after copy");
        Require(!(await manager.PrepareReceivedWorldLoadRehearsalAsync(source with { InstalledProfile = null })).Ok, "interrupted preparation passed");
        var interrupted = (await manager.WorldLoadRehearsalsAsync(profile.Id)).Rehearsals.Single(item => item.State == "Preparation interrupted");
        Require(Directory.Exists(interrupted.WorldDirectory) && !interrupted.CanLaunch, "interrupted copy was lost or became launchable");
        Require((await manager.WorldLoadRehearsalAsync(interrupted.Id, "cleanup", confirmStopped: true)).Ok, "interrupted preparation cannot be cleaned safely");
    }
}

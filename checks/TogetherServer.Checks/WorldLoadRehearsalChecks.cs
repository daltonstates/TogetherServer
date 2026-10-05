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
        async Task RequireRejectedLaunchAsync(HostManager current, string phase)
        {
            var launches = 0;
            current.BeforeManagedLaunchForChecks = () =>
            {
                launches++;
                throw new InvalidOperationException("A substituted rehearsal path reached the launch boundary.");
            };
            try
            {
                var rejected = await current.WorldLoadRehearsalAsync(trial.Id, "start");
                Require(!rejected.Ok && rejected.Code == "WorldLoadReviewRequired" && launches == 0,
                    phase + " did not reject the substituted path before launch: " + rejected.Code);
                var run = (await current.SnapshotAsync()).Runs.Single(item => item.ProfileId == trial.RehearsalProfileId);
                Require(run.State == "Offline" && run.ProcessId is null, phase + " created a managed run");
                Require(rejected.Rehearsal?.CopyIdentity == trial.CopyIdentity &&
                    File.ReadAllText(Path.Combine(sourceRoot, "copy.bin")) == "synthetic baseline" && backups.Verify(profile, backup.Id).Ok,
                    phase + " changed the source copy or its identity");
            }
            finally { current.BeforeManagedLaunchForChecks = null; }
        }
        async Task RequireRootSwapRejectedAsync(HostManager current, string phase)
        {
            var preserved = trial.WorldDirectory + ".plain";
            Require(trial.WorldDirectory == data.NewWorldDirectory(trial.RehearsalProfileId!.Value) &&
                Path.GetDirectoryName(preserved) == data.ManagedWorldsRoot && !Directory.Exists(preserved),
                "root substitution must stay inside the exact disposable world directory");
            Directory.Move(trial.WorldDirectory, preserved);
            try
            {
                await RequireRejectedLaunchAsync(current, phase + " with a missing working copy");
                createJunction(trial.WorldDirectory, sourceRoot);
                await RequireRejectedLaunchAsync(current, phase + " with a substituted root");
                Require(!(await current.WorldLoadRehearsalAsync(trial.Id, "cleanup", confirmStopped: true)).Ok &&
                    Directory.Exists(preserved) && File.ReadAllText(Path.Combine(sourceRoot, "copy.bin")) == "synthetic baseline",
                    phase + " cleanup followed the substituted root");
            }
            finally
            {
                if (Directory.Exists(trial.WorldDirectory))
                {
                    Require((File.GetAttributes(trial.WorldDirectory) & FileAttributes.ReparsePoint) != 0,
                        "refuse to remove a replacement plain directory during junction cleanup");
                    Directory.Delete(trial.WorldDirectory); // Remove only this exact disposable junction.
                }
                Directory.Move(preserved, trial.WorldDirectory);
            }
        }
        async Task RequireSubtreeSwapRejectedAsync(HostManager current, string phase)
        {
            var subtree = Path.Combine(trial.WorldDirectory, "worlds_local");
            createJunction(subtree, sourceRoot);
            try
            {
                await RequireRejectedLaunchAsync(current, phase + " with a substituted save subtree");
                Require(!(await current.WorldLoadRehearsalAsync(trial.Id, "cleanup", confirmStopped: true)).Ok &&
                    File.ReadAllText(Path.Combine(sourceRoot, "copy.bin")) == "synthetic baseline",
                    phase + " cleanup followed a linked save subtree");
            }
            finally { Directory.Delete(subtree); } // Remove only the disposable junction, without recursion.
        }
        await RequireRootSwapRejectedAsync(manager, "First Start");
        await RequireSubtreeSwapRejectedAsync(manager, "First Start");
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
        manager = freshManager;
        Require((await manager.WorldLoadRehearsalAsync(trial.Id, "start")).Ok, "restored plain copy cannot start");
        try
        {
            File.WriteAllText(Path.Combine(trial.WorldDirectory, "copy.bin"), "recognizable synthetic change");
            Require((await manager.WorldLoadRehearsalAsync(trial.Id, "confirm", new("Change", true))).Ok,
                "synthetic saved-change confirmation failed");
        }
        finally { Require((await manager.WorldLoadRehearsalAsync(trial.Id, "stop")).Ok, "first exact trial Stop failed"); }
        manager = new HostManager(data, games);
        var stoppedTrial = (await manager.WorldLoadRehearsalAsync(trial.Id)).Rehearsal;
        Require(stoppedTrial is { ManagedStarts: 1, GracefulStops: 1 },
            "the first exact Start/Stop was not retained across Host reconstruction");
        await RequireRootSwapRejectedAsync(manager, "Restart after exact Stop and Host reconstruction");
        await RequireSubtreeSwapRejectedAsync(manager, "Restart after exact Stop and Host reconstruction");
        Require((await manager.WorldLoadRehearsalAsync(trial.Id, "start")).Ok, "restored plain copy cannot restart");
        try
        {
            Require(File.ReadAllText(Path.Combine(trial.WorldDirectory, "copy.bin")) == "recognizable synthetic change" &&
                (await manager.WorldLoadRehearsalAsync(trial.Id, "confirm", new("Restart", true))).Ok,
                "a normal restart did not preserve the changed disposable copy and owner-reported result");
        }
        finally { Require((await manager.WorldLoadRehearsalAsync(trial.Id, "stop")).Ok, "second exact trial Stop failed"); }
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

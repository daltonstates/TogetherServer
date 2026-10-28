using System.Security.Cryptography;
using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Disposable files and held leases only. No updater helper, app, process,
// console attachment, listener, browser or real AppData is opened.
internal static class UpdateArtifactCleanupChecks
{
    internal static void Run(string root)
    {
        Downloads(Path.Combine(root, "update-artifact-downloads"));
        Checkpoints(Path.Combine(root, "update-artifact-checkpoints"));
        UnknownPending(Path.Combine(root, "update-artifact-unknown"));
    }

    private static void Downloads(string root)
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "TogetherServer.exe");
        File.WriteAllText(executable, "installed app bytes, never executed");
        var now = DateTimeOffset.UtcNow;
        var old = Artifact(root); var prepared = Artifact(root); var busy = Artifact(root);
        var recent = Artifact(root); var unknown = Artifact(root); var nested = Artifact(root);
        var completed = Artifact(root); var pending = Artifact(root); var failed = Artifact(root);
        File.WriteAllText(Path.Combine(unknown, "owner-note.txt"), "keep unknown data");
        Directory.CreateDirectory(Path.Combine(nested, "unexpected-child"));
        File.WriteAllText(Path.Combine(nested, "unexpected-child", "keep.txt"), "keep nested data");
        UpdateArtifactCleanup.MarkPending(completed, Digest(File.ReadAllText(executable)), null);
        UpdateArtifactCleanup.MarkPending(pending, new string('A', 64), null);
        UpdateArtifactCleanup.MarkPending(failed, new string('A', 64), null);
        UpdateArtifactCleanup.MarkFailed(failed);
        var liveWorld = Path.Combine(root, "backups", "world.db");
        Directory.CreateDirectory(Path.GetDirectoryName(liveWorld)!); File.WriteAllText(liveWorld, "world untouched");
        foreach (var directory in new[] { old, prepared, busy, unknown, nested, pending, failed }) Age(directory, now);
        using (UpdateArtifactCleanup.Lease(busy))
        {
            // Lease creation changes directory time; preserve the old-age test.
            Age(busy, now);
            var result = UpdateArtifactCleanup.Run(root, executable, Path.Combine(prepared, AppUpdater.AssetName), nowUtc: now);
            Require(result.RemovedDownloads == 3 && !Directory.Exists(old) && !Directory.Exists(completed) && !Directory.Exists(failed),
                "obsolete or exactly completed update downloads were not removed");
            Require(Directory.Exists(prepared) && Directory.Exists(busy) && Directory.Exists(recent) && Directory.Exists(pending) &&
                File.ReadAllText(Path.Combine(unknown, "owner-note.txt")) == "keep unknown data" &&
                File.ReadAllText(Path.Combine(nested, "unexpected-child", "keep.txt")) == "keep nested data",
                "cleanup removed prepared, leased, recent, pending or unknown update artifacts");
        }
        Require(File.ReadAllText(executable) == "installed app bytes, never executed" && File.ReadAllText(liveWorld) == "world untouched",
            "update cleanup changed the installed app or a world backup");
        var running = Artifact(root); Age(running, now);
        UpdateArtifactCleanup.Run(root, Path.Combine(running, AppUpdater.AssetName), nowUtc: now);
        Require(File.Exists(Path.Combine(running, AppUpdater.AssetName)), "cleanup deleted the current executable's update folder");
        var abandoned = Artifact(root);
        Require(UpdateArtifactCleanup.DiscardIncomplete(root, abandoned) && !Directory.Exists(abandoned) &&
            !UpdateArtifactCleanup.DiscardIncomplete(root, Path.GetDirectoryName(liveWorld)!) && File.Exists(liveWorld),
            "failed-download cleanup escaped the exact app-owned update folder or needed recursive deletion");
    }

    private static void Checkpoints(string root)
    {
        using var data = new LocalData(root);
        var executable = Path.Combine(root, "TogetherServer.exe");
        var firstBytes = "previous version zero";
        var clock = new Clock(DateTimeOffset.UtcNow.AddDays(-10));
        var checkpoints = new List<StateCheckpointReference>();
        // Creating each checkpoint applies retention. Pin the earlier ones as
        // unresolved handoffs until the complete test inventory exists.
        for (var index = 0; index < 6; index++)
        {
            var bytes = index == 0 ? firstBytes : $"previous version {index}";
            File.WriteAllText(executable, bytes);
            clock.Now = clock.Now.AddMinutes(1);
            var result = new StateCheckpointService(data, clock).Create("0.3.2", "0.3.3", executable);
            Require(result.Checkpoint is not null, "a disposable recovery checkpoint could not be created");
            checkpoints.Add(result.Checkpoint!);
            UpdateArtifactCleanup.MarkPending(Artifact(root), new string('F', 64), result.Checkpoint);
        }
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "updates")))
        {
            Require(AppInstance.ContainsPath(data.RootPath, Path.GetFullPath(directory)), "the disposable update folder escaped its test root");
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory, false);
        }
        File.WriteAllText(executable, "current installed version");
        File.WriteAllText(executable + ".previous", firstBytes);
        var pending = Artifact(root);
        UpdateArtifactCleanup.MarkPending(pending, new string('E', 64), checkpoints[1]);
        var stage = Path.Combine(data.UpdateCheckpointsRoot, Guid.NewGuid().ToString("N") + ".staging");
        Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(stage, "settings.json"), "incomplete copied state");
        Age(stage, DateTimeOffset.UtcNow);
        var resultCleanup = UpdateArtifactCleanup.Run(root, executable, protectedCheckpoint: checkpoints[2].Directory);
        Require(resultCleanup.RemovedCheckpoints == 2 && !Directory.Exists(stage) && !Directory.Exists(checkpoints[3].Directory),
            "cleanup did not remove an aged partial checkpoint and obsolete completed checkpoint");
        Require(new[] { 0, 1, 2, 4, 5 }.All(index => Directory.Exists(checkpoints[index].Directory)) &&
            File.ReadAllText(executable + ".previous") == firstBytes,
            "cleanup lost pending/current checkpoints, the newest two, or the previous-EXE rollback checkpoint");
    }

    private static void UnknownPending(string root)
    {
        using var data = new LocalData(root);
        var executable = Path.Combine(root, "TogetherServer.exe"); File.WriteAllText(executable, "synthetic app");
        var unknown = Artifact(root); File.WriteAllText(Path.Combine(unknown, "handoff.json"), "malformed");
        Age(unknown, DateTimeOffset.UtcNow);
        var checkpoint = Path.Combine(data.UpdateCheckpointsRoot, Guid.NewGuid().ToString("N") + ".staging");
        Directory.CreateDirectory(checkpoint); File.WriteAllText(Path.Combine(checkpoint, "settings.json"), "kept while handoff is unknown");
        Age(checkpoint, DateTimeOffset.UtcNow);
        var result = UpdateArtifactCleanup.Run(root, executable);
        Require(result.PreservedUnknown > 0 && Directory.Exists(unknown) && Directory.Exists(checkpoint),
            "malformed pending handoff metadata silently permitted recovery checkpoint cleanup");
    }

    private static string Artifact(string root)
    {
        var directory = Path.Combine(root, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, AppUpdater.AssetName), "synthetic payload, never executed");
        return directory;
    }
    private static void Age(string directory, DateTimeOffset now) => Directory.SetLastWriteTimeUtc(directory, now.AddDays(-2).UtcDateTime);
    private static string Digest(string bytes) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bytes)));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

using System.Collections.Concurrent;

namespace TogetherServer;

// Original-Host downloads and peer forwarding have different transports, but
// the local hosting action must observe both for the exact pinned world.
internal static class SharedSaveTransferProgress
{
    private sealed class Copy
    {
        internal ReceivedSharedWorldStatus? Status;
        internal DateTimeOffset UpdatedUtc = DateTimeOffset.UtcNow;
        internal int Active;
        internal bool ClearRequested;
        internal Lazy<BackgroundJobs.Attempt>? Job;
    }
    private sealed class Activity((string, string, string) key, Copy copy) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Lazy<BackgroundJobs.Attempt>? job = null;
            var complete = false;
            lock (progressSync)
            {
                copy.Active--; copy.UpdatedUtc = DateTimeOffset.UtcNow;
                if (copy.Active == 0)
                {
                    if (copy.ClearRequested)
                    {
                        complete = true;
                        copies.TryRemove(new(key, copy));
                    }
                    job = copy.Job;
                    copy.Job = null;
                }
            }
            if (complete) job?.Value.Complete("SaveReceived", "The save passed verification and receipt processing finished. Game loading has not been checked.");
            else job?.Value.Waiting("CatchUpPending", "The bounded transfer pass ended. Remaining bytes or receipt work will continue in the background.");
        }
    }
    private static readonly ConcurrentDictionary<(string DataRoot, string GroupKey, string VersionHash), Copy> copies = new();
    // Progress reads must never queue behind protected metadata, network,
    // discovery or verification work holding the world mutation gate.
    private static readonly object progressSync = new();
    private static (string, string, string) Key(LocalData data, SharedWorldPeerRoot root, string hash) =>
        (Path.GetFullPath(data.RootPath).ToUpperInvariant(), root.GroupKey, hash);

    internal static IDisposable Begin(LocalData data, SharedWorldPeerRoot root, string versionHash,
        string serverName = "Shared server", string worldName = "Shared world")
    {
        Copy copy;
        (string, string, string) key;
        Lazy<BackgroundJobs.Attempt>? job;
        lock (progressSync)
        {
            key = Key(data, root, versionHash);
            copy = copies.GetOrAdd(key, _ => new());
            if (copy.Active == 0) copy.Job = new(() => BackgroundJobs.For(data).Begin("download:" + root.GroupKey + ":" + versionHash,
                root.ProfileId, serverName, worldName, "Save download"), LazyThreadSafetyMode.ExecutionAndPublication);
            copy.Active++; copy.UpdatedUtc = DateTimeOffset.UtcNow;
            job = copy.Job;
        }
        // Lazy creation coalesces overlapping owners without doing persistence
        // while the global in-memory progress lock is held.
        try { _ = job?.Value; }
        catch
        {
            // No activity lease has reached the caller yet. An unforeseen
            // optional-recording error cannot leave a permanent hosting guard.
            lock (progressSync)
            {
                copy.Active--;
                if (ReferenceEquals(copy.Job, job)) copy.Job = null;
                if (copy.Active == 0) copies.TryRemove(new(key, copy));
            }
            throw;
        }
        return new Activity(key, copy);
    }

    internal static void Report(LocalData data, SharedWorldPeerRoot root, SharedWorldVersion version,
        string phase, long received, long total)
    {
        Lazy<BackgroundJobs.Attempt>? job;
        lock (progressSync)
        {
            var copy = copies.GetOrAdd(Key(data, root, version.VersionHash), _ => new());
            copy.Status = new(true, version.Number, null, phase, ReceivedBytes: received, TotalBytes: total,
                TransferPhase: phase, ReceiptConfirmed: false);
            copy.UpdatedUtc = DateTimeOffset.UtcNow;
            copy.ClearRequested = false;
            job = copy.Job;
        }
        job?.Value.Report(phase, received, total);
    }

    // Start can avoid resolving protected sharing identity when no transfer
    // could affect it. A held flight still counts before its first report or
    // after Clear, until the final activity lease has been disposed.
    internal static bool HasPending(LocalData data)
    {
        lock (progressSync)
        {
            var dataRoot = Path.GetFullPath(data.RootPath).ToUpperInvariant();
            var now = DateTimeOffset.UtcNow;
            var pending = false;
            foreach (var item in copies.Where(item => item.Key.DataRoot == dataRoot))
            {
                if (Expired(item.Value, now)) copies.TryRemove(item);
                else pending |= item.Value.Active > 0 || item.Value.Status is { } status && BlocksHosting(status);
            }
            return pending;
        }
    }

    private static bool Expired(Copy copy, DateTimeOffset now) => copy.Active == 0 &&
        now - copy.UpdatedUtc > TimeSpan.FromSeconds(30);

    internal static ReceivedSharedWorldStatus? Read(LocalData data, SharedWorldPeerRoot root)
    {
        lock (progressSync)
        {
            var dataRoot = Path.GetFullPath(data.RootPath).ToUpperInvariant();
            var now = DateTimeOffset.UtcNow;
            foreach (var item in copies.Where(item => item.Key.DataRoot == dataRoot && item.Key.GroupKey == root.GroupKey))
            {
                // Seed requests and bounded pull passes resume in separate
                // flights. An abandoned flight ceases being an active transfer;
                // latest-save selection still rejects incomplete payloads.
                if (Expired(item.Value, now))
                    copies.TryRemove(item);
                else if (item.Value.Status is { } status) return status;
                else if (item.Value.Active > 0) return new(true, null, null, "Receiving", TransferPhase: "Receiving");
            }
            return null;
        }
    }

    internal static void Clear(LocalData data, SharedWorldPeerRoot root, string versionHash)
    {
        Lazy<BackgroundJobs.Attempt>? job = null;
        lock (progressSync)
        {
            var key = Key(data, root, versionHash);
            if (!copies.TryGetValue(key, out var copy)) return;
            if (copy.Active == 0) { job = copy.Job; copies.TryRemove(new(key, copy)); }
            else { copy.ClearRequested = true; copy.Status = null; }
        }
        job?.Value.Complete("SaveReceived");
    }

    internal static void Fail(LocalData data, SharedWorldPeerRoot root, string versionHash, string code, string message)
    {
        Lazy<BackgroundJobs.Attempt>? job;
        lock (progressSync)
        {
            if (!copies.TryGetValue(Key(data, root, versionHash), out var copy)) return;
            job = copy.Job;
            copy.Job = null;
        }
        job?.Value.Fail(code, message);
    }

    internal static void Pending(LocalData data, SharedWorldPeerRoot root, string versionHash, string code, string message)
    {
        Lazy<BackgroundJobs.Attempt>? job;
        lock (progressSync)
        {
            if (!copies.TryGetValue(Key(data, root, versionHash), out var copy)) return;
            job = copy.Job;
            copy.Job = null;
        }
        job?.Value.Waiting(code, message);
    }

    internal static bool BlocksHosting(ReceivedSharedWorldStatus status) =>
        status.TransferPhase is "Receiving" or "Verifying" or "Receipt" ||
        status.State is "Receiving" or "Verifying" or "Receipt";
}

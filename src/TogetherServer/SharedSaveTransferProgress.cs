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
    }
    private sealed class Activity(LocalData data, (string, string, string) key, Copy copy) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                copy.Active--; copy.UpdatedUtc = DateTimeOffset.UtcNow;
                if (copy.Active == 0 && copy.ClearRequested) copies.TryRemove(new(key, copy));
            }
        }
    }
    private static readonly ConcurrentDictionary<(string DataRoot, string GroupKey, string VersionHash), Copy> copies = new();
    private static (string, string, string) Key(LocalData data, SharedWorldPeerRoot root, string hash) =>
        (Path.GetFullPath(data.RootPath).ToUpperInvariant(), root.GroupKey, hash);

    internal static IDisposable Begin(LocalData data, SharedWorldPeerRoot root, string versionHash)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            var key = Key(data, root, versionHash);
            var copy = copies.GetOrAdd(key, _ => new());
            copy.Active++; copy.UpdatedUtc = DateTimeOffset.UtcNow;
            return new Activity(data, key, copy);
        }
    }

    internal static void Report(LocalData data, SharedWorldPeerRoot root, SharedWorldVersion version,
        string phase, long received, long total)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            var copy = copies.GetOrAdd(Key(data, root, version.VersionHash), _ => new());
            copy.Status = new(true, version.Number, null, phase, ReceivedBytes: received, TotalBytes: total,
                TransferPhase: phase, ReceiptConfirmed: false);
            copy.UpdatedUtc = DateTimeOffset.UtcNow;
            copy.ClearRequested = false;
        }
    }

    internal static ReceivedSharedWorldStatus? Read(LocalData data, SharedWorldPeerRoot root)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            var dataRoot = Path.GetFullPath(data.RootPath).ToUpperInvariant();
            foreach (var item in copies.Where(item => item.Key.DataRoot == dataRoot && item.Key.GroupKey == root.GroupKey))
            {
                // Seed requests and bounded pull passes resume in separate
                // flights. An abandoned flight ceases being an active transfer;
                // latest-save selection still rejects incomplete payloads.
                if (item.Value.Active == 0 && DateTimeOffset.UtcNow - item.Value.UpdatedUtc > TimeSpan.FromSeconds(30))
                    copies.TryRemove(item);
                else if (item.Value.Status is { } status) return status;
                else if (item.Value.Active > 0) return new(true, null, null, "Receiving", TransferPhase: "Receiving");
            }
            return null;
        }
    }

    internal static void Clear(LocalData data, SharedWorldPeerRoot root, string versionHash)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            var key = Key(data, root, versionHash);
            if (!copies.TryGetValue(key, out var copy)) return;
            if (copy.Active == 0) copies.TryRemove(new(key, copy));
            else { copy.ClearRequested = true; copy.Status = null; }
        }
    }

    internal static bool BlocksHosting(ReceivedSharedWorldStatus status) =>
        status.TransferPhase is "Receiving" or "Verifying" or "Receipt" ||
        status.State is "Receiving" or "Verifying" or "Receipt";
}

namespace TogetherServer;

// A successfully served authenticated Chunk is evidence of an existing pull
// conversation, not receipt or authority. Only the competing offer is deferred.
internal sealed class PeerPullOwnership(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<(string World, string Version, string Recipient), long> pulls = [];
    private readonly object sync = new();
    internal void Served(string world, string version, string recipient)
    {
        lock (sync)
        {
            if (pulls.Count >= 256) pulls.Remove(pulls.MinBy(item => item.Value).Key);
            pulls[(world, version, recipient)] = time.GetTimestamp();
        }
    }
    internal bool Active(string world, string version, string recipient)
    {
        lock (sync)
        {
            var key = (world, version, recipient);
            if (!pulls.TryGetValue(key, out var timestamp)) return false;
            if (time.GetElapsedTime(timestamp) < TimeSpan.FromSeconds(60)) return true;
            pulls.Remove(key);
            return false;
        }
    }
}

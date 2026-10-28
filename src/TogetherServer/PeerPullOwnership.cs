namespace TogetherServer;

// A prepared authenticated Chunk is a pull scheduling hint, not evidence the
// receiver retained bytes or a receipt. Only an advancing requested position
// renews the hint; replayed positions cannot defer competing offers forever.
internal sealed class PeerPullOwnership(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private sealed record Progress(long Timestamp, int File, long EndOffset);
    private readonly Dictionary<(string World, string Version, string Recipient), Progress> pulls = [];
    private readonly object sync = new();
    internal void Served(string world, string version, string recipient, int file, long endOffset)
    {
        if (file < 0 || endOffset <= 0) return;
        lock (sync)
        {
            var key = (world, version, recipient);
            if (pulls.TryGetValue(key, out var previous))
            {
                if (file > previous.File || file == previous.File && endOffset > previous.EndOffset)
                    pulls[key] = new(time.GetTimestamp(), file, endOffset);
                return;
            }
            if (pulls.Count >= 256) pulls.Remove(pulls.MinBy(item => item.Value.Timestamp).Key);
            pulls[key] = new(time.GetTimestamp(), file, endOffset);
        }
    }
    internal bool Active(string world, string version, string recipient)
    {
        lock (sync)
        {
            var key = (world, version, recipient);
            // Retain the bounded high-water mark after expiry. Removing it
            // would let the next identical request create a fresh lease.
            return pulls.TryGetValue(key, out var progress) &&
                time.GetElapsedTime(progress.Timestamp) < TimeSpan.FromSeconds(60);
        }
    }
}

namespace TogetherServer;

// Scheduling hints only. Current permission, consent, signatures and exact-copy
// receipts remain the responsibility of each operation, never of a deadline.
internal sealed class PeerReplicationSchedule
{
    // Match the registry's bounded 128 worlds and each world's peer cap, so
    // rotating a large valid registry cannot evict deadlines into a busy loop.
    internal const int MaximumRoutes = 128 * SharedWorldPeerStore.MaximumPeers;
    private sealed record Entry(long Generation, int Failures, DateTimeOffset Due);
    private readonly Dictionary<string, Entry> routes = new(StringComparer.Ordinal);

    internal void RetainWorlds(IReadOnlySet<string> worlds)
    {
        foreach (var key in routes.Keys.Where(key => !worlds.Contains(key.Split(':')[0])).ToArray()) routes.Remove(key);
    }

    internal void RetainRoutes(string world, IReadOnlySet<string> keys)
    {
        foreach (var key in routes.Keys.Where(key => key.StartsWith(world + ":", StringComparison.Ordinal) && !keys.Contains(key)).ToArray())
            routes.Remove(key);
    }

    internal void Track(string key, long generation, DateTimeOffset now)
    {
        if (routes.ContainsKey(key)) return;
        if (routes.Count >= MaximumRoutes) routes.Remove(routes.MinBy(item => item.Value.Due).Key);
        routes[key] = new(generation, 0, now);
    }

    internal bool Due(string key, long generation, DateTimeOffset now) =>
        !routes.TryGetValue(key, out var entry) || entry.Generation != generation || now >= entry.Due;

    internal void Completed(string key, long generation, DateTimeOffset now, bool failed, bool pending,
        double jitter)
    {
        var failures = failed ? Math.Min(6, routes.GetValueOrDefault(key)?.Failures + 1 ?? 1) : 0;
        double seconds = failed ? Math.Min(300, 10 * (1 << failures)) : pending ? 2 : 60;
        // Bound jitter so offline retry cannot exceed five minutes. Active
        // resumable work remains prompt; idle discovery is about once a minute.
        if (!pending) seconds = Math.Clamp(seconds * (0.9 + Math.Clamp(jitter, 0, 1) * 0.1), 1, 300);
        if (!routes.ContainsKey(key) && routes.Count >= MaximumRoutes)
            routes.Remove(routes.MinBy(item => item.Value.Due).Key);
        routes[key] = new(generation, failures, now.AddSeconds(seconds));
    }

    internal TimeSpan NextDelay(DateTimeOffset now, long? generation = null) => routes.Count == 0 ? TimeSpan.FromSeconds(60) :
        generation is { } current && routes.Values.Any(item => item.Generation != current) ? TimeSpan.FromSeconds(1) :
            TimeSpan.FromSeconds(Math.Clamp((routes.Values.Min(item => item.Due) - now).TotalSeconds, 1, 60));
}

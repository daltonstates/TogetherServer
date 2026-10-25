namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    // Membership registration only appends worlds and is capped at 128. Do not
    // allocate gates for requester-supplied or unregistered world identities.
    private sealed class WorldExchangeGates
    {
        internal SemaphoreSlim Metadata { get; } = new(1, 1);
        internal SemaphoreSlim Wire { get; } = new(1, 1);
    }
    private readonly Dictionary<string, WorldExchangeGates> worldExchangeGates = new(StringComparer.Ordinal);

    private WorldExchangeGates ExchangeGatesFor(SharedWorldPeerRoot root)
    {
        lock (stateSync)
        {
            ObjectDisposedException.ThrowIf(peerDisposed, this);
            var group = Find(root.GroupKey);
            if (group is null || group.Root != root)
                throw new InvalidDataException("Register this shared world before exchanging metadata.");
            if (worldExchangeGates.TryGetValue(root.GroupKey, out var existing)) return existing;
            if (worldExchangeGates.Count >= 128)
                throw new InvalidDataException("World metadata capacity needs review.");
            var created = new WorldExchangeGates();
            worldExchangeGates.Add(root.GroupKey, created);
            return created;
        }
    }

    // A prepared Start needs current signed heads, not another download/seed
    // pass or a refresh of every saved Host. Selection, payload verification and
    // the final observation fence remain the same strict local resolver.
    internal async Task<WorldPeerLatestSourceResult> ResolveLatestPreparedSourceAsync(
        SharedWorldPeerRoot root, CancellationToken cancellationToken,
        Action<WorldPeerSourceProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            WorldPeerSourceProgress.Report(progress,
                new("PeerSync", "Checking this server's signed save information before using its prepared copy."));
            await DiscoverHostingMetadataAsync(root, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await ResolveLatestRecoverySourceOnceAsync(root, cancellationToken, progress);
        }
        catch (Exception error) when (PeerFailure(error))
        {
            ReportLatestReview(new("PreparedDiscovery", "ReviewRequired", error.GetType().Name));
            return new(false, "SharedSaveHistoryNeedsReview",
                "The prepared save or current signed membership needs review. Existing saves were kept.");
        }
    }

    private async Task DiscoverHostingMetadataAsync(SharedWorldPeerRoot root, CancellationToken cancellationToken)
    {
        // This budget includes semaphore waits and all targeted network work.
        // An unavailable peer leaves cached signed claims subject to the same
        // newest-head, fork, current-permission and complete-copy checks.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var group = Find(root.GroupKey);
            if (group is null || group.Root != root) return;
            using var key = RetainedLocalKey(group);
            if (key is null) return;
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var attempt = 0; attempt < SharedWorldPeerStore.MaximumPeers; attempt++)
            {
                bounded.Token.ThrowIfCancellationRequested();
                group = Find(root.GroupKey);
                if (group is null || group.Root != root) return;
                var peers = Store(group).Project().Peers
                    .Where(peer => peer.Authorized && !peer.HasConflict && peer.PublicKey != publicKey)
                    .SelectMany(peer => peer.Routes);
                var route = (group.Bootstrap is null ? peers : peers.Prepend(group.Bootstrap))
                    .Concat(AdditionalRoutes(group)).GroupBy(RouteKey).Select(routes => routes.First())
                    .FirstOrDefault(route => visited.Add(RouteKey(route)));
                if (route is null) return;
                try { await SyncPeerAsync(group, route, bounded.Token, includePayloads: false); }
                catch (Exception error) when (PeerFailure(error))
                { SetPeerWarning(root.GroupKey, "A known peer could not refresh its signed information. Existing signed saves were kept."); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { SetPeerWarning(root.GroupKey, "Signed discovery reached its time limit. Existing signed saves were kept."); }
    }
}

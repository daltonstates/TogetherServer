namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    // Start verifies an already prepared local source without retaining another
    // payload. A different or missing newest save requires explicit preparation.
    internal async Task<WorldPeerPreparedStartResult> ResolveLatestPreparedStartSourceAsync(
        SharedWorldPeerRoot root, Guid localProfileId, string branchHash, CancellationToken cancellationToken,
        Action<WorldPeerSourceProgress>? progress = null)
    {
        WorldPeerPreparedStartSource? source = null;
        var code = "LocalHostingPreparationRequired";
        try
        {
            await DiscoverHostingMetadataAsync(root, cancellationToken);
            var group = Find(root.GroupKey);
            if (group is null || group.Root != root || !await RecoveryAllowedAsync(group, cancellationToken))
                return new(false, "HostingPermissionRequired", "Current receiving and hosting permission needs review before Start.");
            var observation = LatestObservationHash(root) ?? throw new InvalidDataException("The current save observation is unavailable.");
            var prepared = WorldPeerPreparedStartSource.Open(data, root, localProfileId, branchHash, cancellationToken, progress);
            source = prepared;
            if (!AuthorizedDescriptor(group, prepared.Descriptor))
                throw new InvalidDataException("The exact prepared descriptor is no longer authorized.");
            code = "LatestSaveChanged";
            var state = Store(group).Project();
            if (state.Peers.Any(peer => peer.HasConflict)) throw new InvalidDataException("Conflicting signed metadata needs review.");
            var local = (await LocalSavesAsync(group, cancellationToken)).Where(item => !item.Descriptor.TestOnly &&
                item.Descriptor.TestArtifact is null && AuthorizedDescriptor(group, item.Descriptor)).ToArray();
            if (!local.Any(item => item.Descriptor.Version.VersionHash == prepared.Descriptor.Version.VersionHash &&
                item.Descriptor.Branch?.BranchHash == branchHash))
                throw new InvalidDataException("The prepared source is not in this world's current signed inventory.");
            var required = state.Branches.Select(item => item.Branch.HeadVersionHash)
                .Concat(state.Sessions.Select(item => item.Session.HeadVersionHash).OfType<string>())
                .Concat(local.Select(item => item.Descriptor.Version.VersionHash)).Distinct(StringComparer.Ordinal).ToArray();
            _ = await WorldPeerLatestSelection.SelectAsync(root, [prepared.Descriptor], required,
                (_, token) => { prepared.RequireCurrent(data, root, localProfileId, branchHash, token); return Task.CompletedTask; },
                (hash, number, token) => FindManifestAsync(group, hash, number, token), cancellationToken, ReportLatestReview);
            var current = Find(root.GroupKey);
            if (current is null || current.Root != root || !await RecoveryAllowedAsync(current, cancellationToken) ||
                LatestObservationHash(root) != observation)
                throw new InvalidDataException("The prepared source's permission or signed save information changed.");
            prepared.RequireCurrent(data, root, localProfileId, branchHash, cancellationToken);
            var result = new WorldPeerPreparedStartResult(true, "LatestSharedSaveReady",
                "This PC's complete prepared continuation matches the latest signed history.", prepared, observation);
            source = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (PeerFailure(error) || SharedWorldBranchLiveSource.ExpectedFailure(error))
        {
            return new(false, code, code == "LatestSaveChanged"
                ? "New or competing signed progress needs preparation before Start. Existing saves were kept."
                : "The complete prepared source could not be verified. Repeat preparation; existing saves were kept.");
        }
        finally { source?.Dispose(); }
    }

    // Membership registration only appends worlds and is capped at 128. Do not
    // allocate gates for requester-supplied or unregistered world identities.
    private sealed class WorldExchangeGates
    {
        internal SemaphoreSlim Metadata { get; } = new(1, 1);
        internal SemaphoreSlim LocalAdvertisement { get; } = new(1, 1);
        internal SemaphoreSlim Wire { get; } = new(1, 1);
        internal SemaphoreSlim Transfer { get; } = new(1, 1);
        internal Dictionary<string, PeerTransferFlight> Transfers { get; } = new(StringComparer.Ordinal);
        internal int Preparations { get; set; }
    }
    private sealed class PeerTransferFlight
    {
        internal Task Completion { get; set; } = Task.CompletedTask;
    }
    private sealed class HostingPreparationScope(WorldPeerService owner, WorldExchangeGates? gates) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0 || gates is null) return;
            lock (owner.stateSync) gates.Preparations--;
        }
    }
    private readonly Dictionary<string, WorldExchangeGates> worldExchangeGates = new(StringComparer.Ordinal);

    // A counter lease gives explicit local work priority without holding a
    // semaphore through file verification, restoration, or setup. It grants no
    // membership/hosting authority, and nested operation/source scopes are safe.
    internal IDisposable BeginHostingPreparation(SharedWorldPeerRoot root)
    {
        lock (stateSync)
        {
            var group = Find(root.GroupKey);
            if (group is null || group.Root != root) return new HostingPreparationScope(this, null);
            var gates = ExchangeGatesFor(root);
            gates.Preparations++;
            return new HostingPreparationScope(this, gates);
        }
    }

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

    private async Task<bool> DiscoverHostingMetadataAsync(SharedWorldPeerRoot root, CancellationToken cancellationToken)
    {
        // This budget includes semaphore waits and all targeted network work.
        // An unavailable peer leaves cached signed claims subject to the same
        // newest-head, fork, current-permission and complete-copy checks.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        var verifiedResponse = false;
        var localAdvertisementSettled = false;
        using var preparation = BeginHostingPreparation(root);
        try
        {
            var group = Find(root.GroupKey);
            if (group is null || group.Root != root) return false;
            await PublishLocalAdvertisementAsync(group, bounded.Token, retainedRoutesOnly: true);
            localAdvertisementSettled = true;
            using var key = RetainedLocalKey(group);
            if (key is null) return false;
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var attempt = 0; attempt < SharedWorldPeerStore.MaximumPeers; attempt++)
            {
                bounded.Token.ThrowIfCancellationRequested();
                group = Find(root.GroupKey);
                if (group is null || group.Root != root) return false;
                var peers = Store(group).Project().Peers
                    .Where(peer => peer.Authorized && !peer.HasConflict && peer.PublicKey != publicKey)
                    .SelectMany(peer => peer.Routes);
                var route = (group.Bootstrap is null ? peers : peers.Prepend(group.Bootstrap))
                    .Concat(AdditionalRoutes(group)).GroupBy(RouteKey).Select(routes => routes.First())
                    .FirstOrDefault(route => visited.Add(RouteKey(route)));
                if (route is null) return verifiedResponse;
                // A stale original-Host route must leave time for another
                // approved peer inside the same overall discovery budget.
                using var routeBudget = CancellationTokenSource.CreateLinkedTokenSource(bounded.Token);
                routeBudget.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await SyncPeerAsync(group, route, routeBudget.Token, includePayloads: false);
                    verifiedResponse = true;
                }
                catch (OperationCanceledException) when (routeBudget.IsCancellationRequested && !bounded.IsCancellationRequested)
                { SetPeerWarning(root.GroupKey, "A known peer could not refresh its signed information. Existing signed saves were kept."); }
                catch (Exception error) when (PeerFailure(error))
                { SetPeerWarning(root.GroupKey, "A known peer could not refresh its signed information. Existing signed saves were kept."); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!localAdvertisementSettled)
                throw new InvalidDataException("Current local hosting metadata could not be checked within the discovery budget.");
            SetPeerWarning(root.GroupKey, "Signed discovery reached its time limit. Existing signed saves were kept.");
            // This reports at least one fresh authenticated response. Other
            // unavailable routes remain Unknown and never grant Start authority.
            return verifiedResponse;
        }
        return verifiedResponse;
    }
}

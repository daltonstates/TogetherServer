namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    // A successful Start already recorded its exact managed operation. Publish
    // that Preparing session immediately, without waiting behind save bytes or
    // the round-robin background pass. This never starts or reconfigures a game.
    internal async Task AnnounceLocalHostingStateAsync(SharedWorldPeerRoot root,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var group = Find(root.GroupKey);
            if (group is null || group.Root != root || !group.SharingEnabled) return;
            using var key = LocalKey(group);
            if (key is null) return;
            if (!await PublishLocalAdvertisementAsync(group, bounded.Token)) return;
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var store = Store(group);
            var targetOffset = store.ReadPage().Total;
            var peers = store.Project().Peers.Where(peer => peer.Authorized && !peer.HasConflict && peer.PublicKey != publicKey)
                .SelectMany(peer => peer.Routes);
            // The original Host bootstrap goes first, including when this PC
            // has never received a current peer directory from another member.
            var routes = (group.Bootstrap is null ? peers : peers.Prepend(group.Bootstrap)).Concat(AdditionalRoutes(group))
                .GroupBy(RouteKey).Select(routes => routes.First()).Take(4).ToArray();
            foreach (var route in routes)
            {
                using var routeBudget = CancellationTokenSource.CreateLinkedTokenSource(bounded.Token);
                routeBudget.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    for (var page = 0; page < SharedWorldPeerStore.MaximumAnnouncements; page++)
                    {
                        var before = ReadCursor(group, route).SentOffset;
                        await SyncPeerMetadataAsync(group, route, routeBudget.Token);
                        var after = ReadCursor(group, route);
                        if (after.SentOffset >= targetOffset || after.SentOffset <= before) break;
                    }
                }
                catch (OperationCanceledException) when (routeBudget.IsCancellationRequested && !bounded.IsCancellationRequested)
                { SetPeerWarning(root.GroupKey, "A known peer could not receive the latest hosting report. Background discovery will retry."); }
                catch (Exception error) when (PeerFailure(error))
                { SetPeerWarning(root.GroupKey, "A known peer could not receive the latest hosting report. Background discovery will retry."); }
            }
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested) { }
        catch (Exception error) when (PeerFailure(error))
        { SetPeerWarning(root.GroupKey, "The latest hosting report could not be shared yet. Background discovery will retry."); }
    }

    // Called only by an explicit local Host instruction. Reuse the signed
    // registry and existing discovery transport without enabling any listener,
    // changing network policy or choosing the group's default on its behalf.
    internal async Task<WorldPeerActionResult> AnnounceLocalHostingAsync(SharedWorldPeerRoot root,
        CancellationToken cancellationToken)
    {
        try
        {
            Group group;
            string publicKey;
            lock (stateSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var registered = Find(root.GroupKey);
                if (registered is null || registered.Root != root)
                    return new(false, "PeerMembershipRequired", "Verify this world's sharing permission and allow its save on this PC, then try hosting again.");
                group = RefreshLocalHostingGroup(registered);
                if (!group.ReceiveConsented)
                    return new(false, "PeerMembershipRequired", "Verify this world's sharing permission and allow its save on this PC, then try hosting again.");
                using var key = LocalKey(group);
                publicKey = key is null ? "" : Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
                if (Access(group, group.DeviceId, publicKey) is not { Receive: true, EligibleHost: true })
                    return new(false, "HostingPermissionChanged", "The owner must allow this PC to receive and host this save.");
            }
            var snapshot = await manager.CompanionSnapshotAsync();
            using var certificate = new HostIdentity(data).Load();
            var incomingReady = snapshot.Settings.CompanionListeningEnabled && certificate is { HasPrivateKey: true } &&
                SharedWorldPeerTrust.ValidRoute(new(snapshot.Settings.CompanionEndpoint.TrimEnd('/'), HostIdentity.Fingerprint(certificate)));
            cancellationToken.ThrowIfCancellationRequested();
            lock (stateSync)
            {
                var current = Find(root.GroupKey);
                if (current is null || current.Root != root)
                    return new(false, "HostingPermissionChanged", "Sharing permission changed. Recheck this server before starting.");
                current = RefreshLocalHostingGroup(current);
                using var key = LocalKey(current);
                if (current.DeviceId != group.DeviceId || current.Roster.Signature != group.Roster.Signature ||
                    !current.ReceiveConsented || key is null ||
                    Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != publicKey ||
                    Access(current, current.DeviceId, publicKey) is not { Receive: true, EligibleHost: true })
                    return new(false, "HostingPermissionChanged", "Sharing permission changed. Recheck this server before starting.");
                cancellationToken.ThrowIfCancellationRequested();
                SaveGroup(current with { SharingEnabled = true });
            }
            // The explicit Host instruction arms outbound completed-save
            // sharing even when this PC cannot accept an incoming connection.
            // Planned moves still require callers to resolve this route result.
            if (!incomingReady)
                return new(false, "RecipientConnectionRequired", "Incoming connections need setup before another PC can connect here. Open Connection help and deliberately enable incoming connections. No connection setting was changed.");
        }
        catch (Exception error) when (PeerFailure(error))
        {
            return new(false, "HostingPermissionChanged", "Current sharing permission or local save consent could not be verified. Recheck this server before starting.");
        }
        // Coalesced background work advertises this registration and wakes for
        // material managed-session changes. Delivery needs reachable peers;
        // this result claims no network round trip, game join or save test.
        return new(true, "HostingAddressPrepared", "Approved PCs can discover this PC through signed peer exchange when they can reach it. Game connections still need testing.");
    }

    // Call under stateSync. Only an already registered exact world may advance
    // to its verified local journal head; no network or Friend-link gate runs.
    private Group RefreshLocalHostingGroup(Group group)
    {
        var current = group with
        {
            Roster = SharedWorldSeparateCopyStore.CurrentRoster(data, group.Roster),
            ReceiveConsented = new WorldPeerReceiveConsentStore(data).Effective(group.Root,
                group.DeviceId, group.ReceiveConsented)
        };
        if (current.Roster.Signature != group.Roster.Signature || current.ReceiveConsented != group.ReceiveConsented)
            SaveGroup(current);
        return current;
    }
}

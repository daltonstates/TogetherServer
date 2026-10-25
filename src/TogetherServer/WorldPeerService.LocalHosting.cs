namespace TogetherServer;

internal sealed partial class WorldPeerService
{
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
            if (!snapshot.Settings.CompanionListeningEnabled || certificate is not { HasPrivateKey: true } ||
                !SharedWorldPeerTrust.ValidRoute(new(snapshot.Settings.CompanionEndpoint.TrimEnd('/'), HostIdentity.Fingerprint(certificate))))
                return new(false, "RecipientConnectionRequired", "Open Connection help and deliberately enable incoming connections on this PC, then continue. No connection setting was changed.");
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
        }
        catch (Exception error) when (PeerFailure(error))
        {
            return new(false, "HostingPermissionChanged", "Current sharing permission or local save consent could not be verified. Recheck this server before starting.");
        }
        // The normal five-second background sync advertises this registration
        // and, after Start, the exact managed session. Delivery needs reachable peers;
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

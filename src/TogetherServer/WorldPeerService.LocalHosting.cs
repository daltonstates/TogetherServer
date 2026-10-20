namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    // Called only by an explicit local Host instruction. Reuse the signed
    // registry and existing discovery transport without enabling any listener,
    // changing network policy or choosing the group's default on its behalf.
    internal async Task<WorldPeerActionResult> AnnounceLocalHostingAsync(SharedWorldPeerRoot root,
        CancellationToken cancellationToken)
    {
        await RefreshMembershipsAsync(false, cancellationToken);
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var group = Find(root.GroupKey);
            if (group is null || group.Root != root || !group.ReceiveConsented)
                return new(false, "PeerMembershipRequired", "Verify this world's sharing permission and allow its save on this PC, then try hosting again.");
            using var key = LocalKey(group);
            var access = key is null ? null : Access(group, group.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            if (access is not { Receive: true, EligibleHost: true })
                return new(false, "HostingPermissionChanged", "The owner must allow this PC to receive and host this save.");
            var snapshot = await manager.CompanionSnapshotAsync();
            using var certificate = new HostIdentity(data).Load();
            if (!snapshot.Settings.CompanionListeningEnabled || certificate is not { HasPrivateKey: true } ||
                !SharedWorldPeerTrust.ValidRoute(new(snapshot.Settings.CompanionEndpoint.TrimEnd('/'), HostIdentity.Fingerprint(certificate))))
                return new(false, "RecipientConnectionRequired", "Open Connection help and deliberately enable incoming connections on this PC, then continue. No connection setting was changed.");
            cancellationToken.ThrowIfCancellationRequested();
            lock (stateSync)
            {
                var current = Find(root.GroupKey);
                if (current is null || current.Root != root || current.Roster.Signature != group.Roster.Signature ||
                    !current.ReceiveConsented || Access(current, current.DeviceId,
                        Convert.ToBase64String(key!.ExportSubjectPublicKeyInfo())) is not { Receive: true, EligibleHost: true })
                    return new(false, "HostingPermissionChanged", "Sharing permission changed. Recheck this server before starting.");
                SaveGroup(current with { SharingEnabled = true });
            }
        }
        finally { refreshGate.Release(); }
        // The normal five-second background sync repeats this after Start and
        // advertises the exact managed session. Delivery needs reachable peers;
        // this result claims no network round trip, game join or save test.
        ScheduleSync(cancellationToken);
        return new(true, "HostingAddressPrepared", "Approved PCs can discover this PC through signed peer exchange when they can reach it. Game connections still need testing.");
    }
}

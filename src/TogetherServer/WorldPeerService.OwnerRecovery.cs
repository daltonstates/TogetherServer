namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    internal async Task<WorldSeparateCopyResult> PrepareLatestOwnerRecoveryAsync(SharedWorldPeerRoot root,
        bool acceptSplitWarning, CancellationToken cancellationToken)
    {
        if (!acceptSplitWarning)
            return new(false, "SplitWarningRequired", "Confirm hosting this world's latest save on this PC.");
        var latest = await ResolveLatestRecoverySourceAsync(root, cancellationToken);
        if (!latest.Ok || latest.Source is null) return new(false, latest.Code, latest.Message);
        return await PrepareOwnerRecoveryAsync(root.GroupKey, latest.Source.BranchHash, true,
            cancellationToken, latest.Source);
    }

    // Local fixed-world action only. It creates a warned branch and retained
    // base, then reuses HostManager's fresh restore/finish/manual-Start path.
    // The original owner never becomes a synthetic voter or roster member.
    internal async Task<WorldSeparateCopyResult> PrepareOwnerRecoveryAsync(string groupKey,
        string? sourceBranchHash, bool acceptSplitWarning, CancellationToken cancellationToken,
        WorldPeerRecoverySource? selectedSource = null)
    {
        if (!acceptSplitWarning)
            return new(false, "SplitWarningRequired",
                "Confirm that another server may still be running and both histories will be preserved.");
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var source = selectedSource ?? await ResolveRecoverySourceAsync(groupKey, sourceBranchHash, cancellationToken);
            source.Verify(data, source.Root);
            var settings = (await manager.SnapshotAsync()).Settings;
            cancellationToken.ThrowIfCancellationRequested();
            lock (stateSync)
            {
                var group = Find(groupKey);
                if (group is null || group.DeviceId != Guid.Empty || !group.ReceiveConsented ||
                    LocalOwnerKey() != group.Root.OwnerPublicKey)
                    return new(false, "OwnerRecoveryUnavailable",
                        "Choose a world originally owned by this PC with a complete received save.");
                using var key = LocalKey(group);
                if (key is null || Access(group, Guid.Empty, group.Root.OwnerPublicKey) is not
                    { Receive: true, EligibleHost: true })
                    return new(false, "OwnerRecoveryUnavailable", "The current signed world membership needs review.");
                var roster = SharedWorldSeparateCopyStore.CurrentRoster(data, group.Roster);
                if (roster.Revision != group.Roster.Revision || roster.Signature != group.Roster.Signature)
                    return new(false, "WorldRosterCatchUpRequired", "Refresh this world's signed membership before recovery.");
                var sourceBranch = source.Descriptor.Branch;
                var vault = source.Vault;
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
                if (source.Root != group.Root || source.Descriptor.TestOnly ||
                    source.Descriptor.TestArtifact is not null ||
                    FriendLink.ReadReceivedLatest(vault)?.VersionHash != source.Descriptor.Version.VersionHash)
                    return new(false, "RecoverySourceChanged", "The selected complete save changed after review.");
                if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out var endpoint) ||
                    endpoint.GetLeftPart(UriPartial.Authority) != settings.CompanionEndpoint.TrimEnd('/'))
                    return new(false, "CandidateRouteUnavailable",
                        "Configure this PC's literal HTTPS address before preparing recovery. A local loopback address can be used for solo play.");
                using var certificate = new HostIdentity(data).Ensure(settings.CompanionEndpoint);
                var offer = SharedWorldSeparateCopyStore.PrepareOwnerOffer(vault, roster, key,
                    settings.CompanionEndpoint, HostIdentity.Fingerprint(certificate),
                    new WorldAuthorityStore(data), sourceBranch);
                cancellationToken.ThrowIfCancellationRequested();
                var branch = new SharedWorldSeparateCopyStore(data).Declare(offer, vault, key,
                    acceptSplitWarning, sourceBranch);
                return new(true, "SeparateCopyRecorded",
                    "The owner's recovery branch is recorded and its verified source is retained. Restore it into a fresh local world, finish local checks, then Start manually. Every existing history remains available.", branch);
            }
        }
        catch (Exception ex) when (PeerFailure(ex))
        {
            return new(false, "OwnerRecoveryUnavailable",
                "The selected complete save, signed ancestry, current membership, or local hosting identity needs review.");
        }
        finally { refreshGate.Release(); }
    }
}

namespace TogetherServer;

internal sealed partial class FriendLink
{
    internal async Task<WorldLoadRehearsalResult> PrepareWorldLoadAsync(Guid profileId, HostManager manager,
        CancellationToken cancellationToken)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This connection is closing.");
        SemaphoreSlim? copyGate = null;
        var entered = false;
        try
        {
            var root = await ConfiguredReceiveRootAsync(profileId, cancellationToken);
            if (root is null) return new(false, "NotPaired", "Choose a saved Host connection.");
            copyGate = SharedReceiveGates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
            await copyGate.WaitAsync(cancellationToken);
            entered = true;
            VerifiedWorldLoadSource source;
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (config is null || config.ConsentedSharedWorldProfiles?.Contains(profileId) != true ||
                    withdrawnSharedConsent.ContainsKey(profileId))
                    return new(false, "ConsentRequired", "Allow saves on this PC before rehearsing a received copy.");
                if (config.SharedWorldConflicts?.Contains(profileId) == true ||
                    config.PendingSharedWorldGroups?.ContainsKey(profileId) == true)
                    return new(false, "SharedWorldReviewRequired", "Review the signed save history before preparing a load rehearsal.");
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
                var version = ReadReceivedLatest(root);
                if (version is null || version.ProfileId != profileId || !AuthorizedVersionSigner(profileId, version) ||
                    config.SharedRosterFloors?.GetValueOrDefault(profileId)?.GroupId != version.GroupId ||
                    config.LastSharedHostHashes?.GetValueOrDefault(profileId) != version.VersionHash)
                    return new(false, "ReceivedCopyUnverified", "Check and receive the current signed copy before preparing a load rehearsal.");
                source = new(profileId, version.Game, version.WorldId, "Received", version.VersionHash,
                    version.Files, Path.Combine(root, version.VersionHash, SharedWorldService.PayloadDirectory));
            }
            finally { gate.Release(); }
            // Keep the normal per-vault receive gate throughout copying. Never hold the
            // Friend configuration gate while entering the Host lifecycle gate.
            return await manager.PrepareReceivedWorldLoadRehearsalAsync(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
        { return new(false, "ReceivedCopyUnverified", "The signed copy could not be revalidated. Its original files and receipt were kept."); }
        finally { if (entered) copyGate!.Release(); ReleaseRetained(); }
    }
}

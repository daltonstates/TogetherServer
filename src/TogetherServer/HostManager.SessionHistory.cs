namespace TogetherServer;

public sealed partial class HostManager
{
    // Local-owner history follows signed world identity across fresh local
    // continuation profiles. Names, routes and an announcement cannot join two
    // histories. Conflicting pinned roots sharing a source ID stay separate.
    private HashSet<Guid> SessionHistoryProfiles(Guid profileId)
    {
        var result = new HashSet<Guid> { profileId };
        var requested = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
        if (requested is null) return result;
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var sourceId = requested.SeparateCopySourceProfileId ?? requested.Id;
                var branches = new SharedWorldSeparateCopyStore(data).Read(sourceId)
                    .Where(SharedWorldSeparateCopyStore.Verify).ToArray();
                SharedWorldPeerRoot? root;
                if (requested.SeparateCopySourceProfileId is not null)
                {
                    var selected = branches.SingleOrDefault(item => item.BranchHash == requested.SeparateCopyBranchHash &&
                        SharedWorldBranchLiveSource.LocalProfileId(item.BranchHash) == requested.Id);
                    root = selected is null ? null : Root(selected.Offer.Roster);
                }
                else
                {
                    var roster = requested.SharedSavesEnabled ? sharedWorlds.ReadRoster(requested, requireRetainedSigningKey: true) : null;
                    root = roster is not null && SharedWorldRosterTrust.Verify(roster) ? Root(roster) : null;
                }
                if (root is null || root.ProfileId != sourceId || branches.Any(item => Root(item.Offer.Roster) != root)) return result;
                var original = settings.Profiles.SingleOrDefault(item => item.Id == sourceId && item.SeparateCopySourceProfileId is null);
                if (original is not null)
                {
                    var roster = sharedWorlds.ReadRoster(original, requireRetainedSigningKey: true);
                    if (roster is null || !SharedWorldRosterTrust.Verify(roster) || Root(roster) != root) return result;
                }
                result.Add(sourceId);
                foreach (var branch in branches)
                    result.Add(SharedWorldBranchLiveSource.LocalProfileId(branch.BranchHash));
                return result;
            }
        }
        catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error)) { return result; }

        static SharedWorldPeerRoot Root(SharedWorldRoster roster) => new(roster.OwnerPublicKey, roster.ProfileId, roster.GroupId);
    }
}

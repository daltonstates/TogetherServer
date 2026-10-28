namespace TogetherServer;

public sealed partial class HostManager
{
    internal Guid CompanionProfileId(Guid assignedProfileId) =>
        CompanionProfileId(Volatile.Read(ref lastCompanionSnapshot)!, assignedProfileId);
    // An owner can continue a received save in fresh storage on the same PC.
    // Existing paired PCs still address the original assigned server. Resolve
    // that alias only to one locally managed continuation of its signed world;
    // a network announcement, name or endpoint can never redirect controls.
    internal Guid CompanionProfileId(HostSnapshot snapshot, Guid assignedProfileId)
    {
        var original = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == assignedProfileId);
        if (original is null || original.SeparateCopySourceProfileId is not null || !original.SharedSavesEnabled ||
            snapshot.Runs.Any(item => item.ProfileId == assignedProfileId && item.RunOperationId is not null))
            return assignedProfileId;
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var roster = sharedWorlds.ReadRoster(original, requireRetainedSigningKey: true);
                if (roster is null || !SharedWorldRosterTrust.Verify(roster)) return assignedProfileId;
                var branches = new SharedWorldSeparateCopyStore(data).Read(assignedProfileId);
                return CompanionContinuation.Resolve(snapshot, original, roster, branches);
            }
        }
        catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
        { return assignedProfileId; }
    }
}

internal static class CompanionContinuation
{
    internal static Guid Resolve(HostSnapshot snapshot, ServerProfile original, SharedWorldRoster roster,
        IReadOnlyList<WorldSeparateCopyBranch> branches)
    {
        if (!SharedWorldRosterTrust.Verify(roster) || roster.ProfileId != original.Id || !original.SharedSavesEnabled ||
            original.SeparateCopySourceProfileId is not null || snapshot.Runs.Any(run =>
                run.ProfileId == original.Id && run.RunOperationId is not null)) return original.Id;
        var candidates = snapshot.Settings.Profiles.Where(profile =>
            profile.SeparateCopySourceProfileId == original.Id && profile.Kind == original.Kind &&
            profile.SeparateCopyBranchHash is { } hash &&
            snapshot.Runs.Any(run => run.ProfileId == profile.Id && run.RunOperationId is not null) &&
            branches.Count(branch => branch.BranchHash == hash && SharedWorldSeparateCopyStore.Verify(branch) &&
                branch.Offer.Roster.OwnerPublicKey == roster.OwnerPublicKey &&
                branch.Offer.Roster.GroupId == roster.GroupId && branch.Offer.Roster.ProfileId == roster.ProfileId &&
                SharedWorldBranchLiveSource.LocalProfileId(hash) == profile.Id) == 1).ToArray();
        return candidates.Length == 1 ? candidates[0].Id : original.Id;
    }
}

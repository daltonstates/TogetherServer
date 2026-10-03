namespace TogetherServer;

internal sealed partial class FriendLink
{
    // The selected, protected Friend connection identifies this installation;
    // a roster ID supplied by a pasted offer cannot claim local candidacy.
    internal Guid? RecoveryDeviceId(Guid profileId) =>
        config?.ApprovedSharedWorldGroups?.ContainsKey(profileId) == true ? config.DeviceId : null;

    internal IReadOnlyList<WorldSeparateCopyBranch> SeparateCopyBranches(Guid profileId)
    {
        gate.Wait();
        try
        {
            if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) is not { } group)
                return [];
            return new SharedWorldSeparateCopyStore(data).Read(profileId)
                .Where(branch => branch.Offer.Proposal.GroupId == group &&
                    branch.Offer.Proposal.ProposerDeviceId == config.DeviceId)
                .ToArray();
        }
        finally { gate.Release(); }
    }
}

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
            var sourceId = SharedWorldSourceProfileId(profileId);
            if (config is null || sourceId is null || !HasSharedWorldReceiveConsent(profileId) ||
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(sourceId.Value) is not { } group ||
                config.SharedWorldSigningKeys?.GetValueOrDefault(sourceId.Value) is not { } owner)
                return [];
            return new SharedWorldSeparateCopyStore(data).Read(sourceId.Value)
                .Where(branch => branch.Offer.Proposal.GroupId == group &&
                    branch.Offer.Roster.OwnerPublicKey == owner &&
                    branch.Offer.Proposal.ProposerDeviceId == SharedWorldDeviceId)
                .ToArray();
        }
        finally { gate.Release(); }
    }
}

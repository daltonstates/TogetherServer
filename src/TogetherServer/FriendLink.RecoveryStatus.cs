namespace TogetherServer;

internal sealed partial class FriendLink
{
    // The selected, protected Friend connection identifies this installation;
    // a roster ID supplied by a pasted offer cannot claim local candidacy.
    internal Guid? RecoveryDeviceId(Guid profileId) =>
        config?.ApprovedSharedWorldGroups?.ContainsKey(profileId) == true ? config.DeviceId : null;
}

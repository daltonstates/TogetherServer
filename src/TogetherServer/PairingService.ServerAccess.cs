namespace TogetherServer;

public sealed partial class PairingService
{
    // Session transports are internal world-sharing identities. Full Views()
    // remains available to companion authorization and status projection.
    public IReadOnlyList<DeviceView> OwnerPcViews()
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var visible = devices.Where(device => device.PeerWorldGroupKey is null)
                .Select(device => device.Id).ToHashSet();
            return Views().Where(view => visible.Contains(view.Id)).ToArray();
        }
    }

    // This is a local denial of one server, not a credential replacement or a
    // rewrite of another owner's signed world membership.
    public PairingDecision RemoveServerAccess(Guid deviceId, Guid profileId)
    {
        if (deviceId == Guid.Empty || profileId == Guid.Empty)
            return new(false, "InvalidServerAccess", "Choose a saved PC and server.");
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(item => item.Id == deviceId && item.PeerWorldGroupKey is null);
            if (device is null) return new(false, "UnknownDevice", "This saved PC was not found.");
            if (!data.LoadSettings().Profiles.Any(profile => profile.Id == profileId))
                return new(false, "UnknownServer", "This server is no longer saved on this Host.");
            var assigned = device.AssignedProfileIds?.Contains(profileId) == true;
            var hasSavedScope = assigned || device.SharedWorldGrants?.ContainsKey(profileId) == true ||
                device.SaveReceiveProfileIds?.Contains(profileId) == true ||
                device.ServerPermissionOverrides?.Any(permission => permission.ProfileId == profileId) == true;
            if (!hasSavedScope)
                return new(true, "ServerAccessAlreadyRemoved", "This PC already has no access to this server.");

            // Fence only locally owned signed membership before persisting the
            // removal. Inherited groups retain their original signed proofs;
            // current local assignment still denies this transport immediately.
            if (CanChangeSharedRoster([profileId])) MarkSharedRostersDirty([profileId]);
            device.AssignedProfileIds?.RemoveAll(id => id == profileId);
            device.ServerPermissionOverrides?.RemoveAll(permission => permission.ProfileId == profileId);
            device.SharedWorldGrants?.Remove(profileId);
            device.SaveReceiveProfileIds?.RemoveAll(id => id == profileId);
            SaveState();
            data.TryAudit($"server-access-remove {device.Id} {profileId} {UtcNow:O}");
            Activity("Access", "ServerAccessRemoved", "The Host removed a PC's access to this server.",
                ActivitySeverity.Warning, profileId, device.Id, ActivityVisibility.Device);
            return new(true, "ServerAccessRemoved", "Access to this server was removed. Access to other servers was kept.");
        }
    }
}

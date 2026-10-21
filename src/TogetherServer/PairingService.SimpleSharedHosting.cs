namespace TogetherServer;

public sealed partial class PairingService
{
    // The server's one owner choice covers every approved assigned PC, including
    // a PC that has not enrolled its signing key yet. Transport/session credentials
    // never create a second signed member or broaden inherited owner authority.
    private bool ApplySimpleSharedHostingPolicyLocked(PairedDevice device,
        IEnumerable<ServerProfile>? selectedProfiles = null)
    {
        if (device.PeerWorldGroupKey is not null) return false;
        var changed = false;
        foreach (var profile in selectedProfiles ?? data.LoadSettings().Profiles)
        {
            if (profile.WorldLoadRehearsalId is not null ||
                profile.SeparateCopySourceProfileId is not null || profile.SeparateCopyBranchHash is not null ||
                AuthorityStateOrDamaged(profile.Id)) continue;
            var enabled = profile.SharedSavesEnabled && !IsRevoked(device) && !device.ApprovalPending &&
                device.CredentialHash is not null && device.CredentialExpiresUtc > UtcNow &&
                !IsAccessExpired(device, UtcNow) && device.AssignedProfileIds?.Contains(profile.Id) == true;
            device.SharedWorldGrants ??= [];
            var before = device.SharedWorldGrants.GetValueOrDefault(profile.Id) ?? new();
            var next = before with { Receive = enabled, EligibleHost = enabled, Reshare = enabled };
            if (before != next)
            {
                device.SharedWorldGrants[profile.Id] = next;
                changed = true;
            }
            device.SaveReceiveProfileIds ??= [];
            if (enabled && !device.SaveReceiveProfileIds.Contains(profile.Id))
            {
                device.SaveReceiveProfileIds.Add(profile.Id);
                changed = true;
            }
            else if (!enabled && device.SaveReceiveProfileIds.Remove(profile.Id)) changed = true;
        }
        return changed;
    }

    internal void ReconcileSimpleSharedHosting(Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var profile = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == profileId) ??
                throw new InvalidDataException("The saved server no longer exists.");
            if (!CanChangeSharedRoster([profileId]))
                throw new InvalidDataException("Only the original owner can change shared hosting.");
            var changed = false;
            foreach (var device in devices)
                changed |= ApplySimpleSharedHostingPolicyLocked(device, [profile]);
            if (!changed) return;
            // Fence before persisting. A failed durable pairing write already
            // invalidates its in-memory access through SaveState's safety rule.
            RequireSharedRosterPublication(profileId);
            SaveState();
        }
    }

    internal IReadOnlyList<Guid> SharedEnrollmentProfileIds(Guid deviceId, Guid requestedProfileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(item => item.Id == deviceId);
            if (device is null || device.PeerWorldGroupKey is not null) return [];
            var assigned = device.AssignedProfileIds ?? [];
            return data.LoadSettings().Profiles.Where(profile => assigned.Contains(profile.Id) &&
                (profile.Id == requestedProfileId || profile.SharedSavesEnabled || SharedRosterDirty(profile.Id)))
                .Select(profile => profile.Id).ToArray();
        }
    }
}

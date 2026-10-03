namespace TogetherServer;

public sealed partial class HostManager
{
    private bool SharedAuthorityBlocked(Guid profileId, out string reason)
    {
        reason = "";
        try
        {
            return authority.HasState(profileId) && authority.Fenced(profileId,
                sharedWorlds.LocalAuthorityPublicKey(), out reason);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                   UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            reason = "Shared world authority could not be verified. Keep this world offline until its history is reviewed.";
            return true;
        }
    }

    public async Task<SharedWorldResult> SetSharedSavesAsync(Guid profileId, bool enabled)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return new(false, "UnknownProfile", "Choose a saved server.");
            if (enabled && SharedAuthorityBlocked(profileId, out var reason))
                return new(false, "SharedWorldAuthorityBlocked", reason);
            if (profile.Kind == GameKinds.Custom || !games.TryGet(profile.Kind, out var driver) ||
                !driver.SupportsBackups)
                return new(false, "SharingUnsupported", "Only reviewed built-in game worlds can be shared.");
            if (enabled && profile.Backups?.Enabled != true)
                return new(false, "RollingBackupRequired", "Enable rolling backup after graceful Stop first.");
            var previous = profile.SharedSavesEnabled;
            profile.SharedSavesEnabled = enabled;
            try { data.SaveSettings(settings); }
            catch { profile.SharedSavesEnabled = previous; throw; }
            AdvanceReadModel();
            Volatile.Write(ref lastOwnerSnapshot, Snapshot());
            Activity("Backup", enabled ? "SharedSavesEnabled" : "SharedSavesDisabled",
                enabled ? "The owner enabled post-Stop save sharing." :
                    "The owner disabled post-Stop save sharing.", ActivitySeverity.Important, profileId);
            return new(true, "SharedSavesSaved", enabled ?
                "Completed post-Stop backups can be shared with approved PCs." :
                "New shared save reads are disabled.");
        }
        finally { gate.Release(); }
    }

    public async Task<SharedWorldStatus> SharedWorldStatusAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            return profile is null ? new(false, null, "Server not found.") : sharedWorlds.Status(profile);
        }
        finally { gate.Release(); }
    }

    internal async Task<(SharedWorldStatus Status, ServerProfile? Profile)> SharedWorldReadAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return (new(false, null), null);
            if (SharedAuthorityBlocked(profileId, out var reason))
                return (new(false, null, reason), null);
            return (sharedWorlds.Status(profile), profile);
        }
        finally { gate.Release(); }
    }

    internal byte[] ReadSharedChunk(SharedWorldVersion version, int fileIndex, long offset)
    {
        if (SharedAuthorityBlocked(version.ProfileId, out _))
            throw new InvalidDataException("Shared world authority blocks transfer from this PC.");
        return sharedWorlds.ReadChunk(version, fileIndex, offset);
    }

    internal SharedWorldVersion ReadEarlierSharedVersion(SharedWorldVersion latest, long number)
    {
        if (SharedAuthorityBlocked(latest.ProfileId, out _))
            throw new InvalidDataException("Shared world authority blocks transfer from this PC.");
        return sharedWorlds.ReadEarlierVersion(latest, number);
    }

    internal async Task ApplySharedWorldAuthorityAsync(WorldAuthorityRecord record)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == record.Proposal.ProfileId);
            if (profile is null)
                throw new InvalidDataException("This PC does not have that world.");
            var roster = sharedWorlds.ReadRoster(profile);
            if (roster is null || !SharedWorldRosterTrust.Verify(record.Roster) ||
                record.Roster.GroupId != roster.GroupId ||
                record.Roster.OwnerPublicKey != roster.OwnerPublicKey ||
                record.Roster.Epoch < roster.Epoch || record.Roster.Revision < roster.Revision)
                throw new InvalidDataException("Authority roster is older or belongs to another group.");
            authority.Append(record);
            var active = runs.SingleOrDefault(run => run.ProfileId == profile.Id);
            Activity("Backup", "SharedWorldAuthorityApplied",
                active is not null && Identity(active) == "Matched"
                    ? "A verified authority change was recorded while the exact managed game process was running. Gracefully stop it and review both histories."
                    : "A verified authority change was recorded. This PC is fenced from starting or sharing this world until its history is reviewed.",
                ActivitySeverity.Important, profile.Id);
            AdvanceReadModel();
            Volatile.Write(ref lastOwnerSnapshot, Snapshot());
        }
        finally { gate.Release(); }
    }

    internal async Task BindSharedWorldSuccessorIdentityAsync(Guid profileId,
        string recordHash, Guid deviceId)
    {
        await gate.WaitAsync();
        try { authority.BindLocalSuccessor(profileId, recordHash, deviceId); }
        finally { gate.Release(); }
    }

    internal async Task<SharedWorldReceiptResult> ConfirmSharedWorldReceiptAsync(Guid profileId,
        Guid deviceId, SharedWorldReceipt receipt)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (SharedAuthorityBlocked(profileId, out _))
                return new(false, "SharedWorldAuthorityBlocked");
            return profile is null ? new(false, "UnknownProfile") :
                sharedWorlds.ConfirmReceipt(profile, deviceId, receipt);
        }
        finally { gate.Release(); }
    }

    public async Task<SharedWorldRoster?> SharedWorldRosterAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (SharedAuthorityBlocked(profileId, out _)) return null;
            return profile is null ? null : sharedWorlds.ReadRoster(profile);
        }
        finally { gate.Release(); }
    }

    public async Task<SharedWorldRoster> PublishSharedWorldRosterAsync(Guid profileId,
        IReadOnlyList<SharedWorldRosterMember> members, bool? ownerOverride = null,
        bool reviewSourceChange = false)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId) ??
                throw new InvalidDataException("Server not found.");
            if (!profile.SharedSavesEnabled || profile.Kind == GameKinds.Custom)
                throw new InvalidDataException("Shared saves are not enabled for this server.");
            if (SharedAuthorityBlocked(profileId, out _))
                throw new InvalidDataException("Shared world authority blocks roster publication from this PC.");
            return sharedWorlds.PublishRoster(profile, members, ownerOverride, reviewSourceChange);
        }
        finally { gate.Release(); }
    }

}

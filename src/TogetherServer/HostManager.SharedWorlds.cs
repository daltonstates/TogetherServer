namespace TogetherServer;

public sealed partial class HostManager
{
    private string LocalAuthorityComparisonKey(Guid profileId) =>
        authority.LocalAuthorizedHead(profileId) is null
            ? sharedWorlds.LocalAuthorityPublicKey()
            : string.Empty; // Schema-2 successor proof uses its enrolled hosting key.

    private bool SharedAuthorityBlocked(Guid profileId, out string reason)
    {
        reason = "";
        try
        {
            if (!authority.HasState(profileId)) return false;
            if (authority.GovernanceUnresolved(profileId))
            {
                reason = "Signed sharing membership is unresolved after a local access change. Keep this world offline and review group authority before sharing or voting.";
                return true;
            }
            if (authority.Fenced(profileId, LocalAuthorityComparisonKey(profileId), out reason))
                return true;
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null || !sharedWorlds.AuthorizedPublishedLineage(profile))
            {
                reason = "The published save does not continue the signed authority head. Keep this world offline for review.";
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                   UnauthorizedAccessException or System.Text.Json.JsonException or
                                   System.Security.Cryptography.CryptographicException)
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
            if (enabled && authority.HasState(profileId) && !profile.SharedSavesEnabled)
                return new(false, "SuccessorRosterReadOnly",
                    "This successor PC cannot re-enable sharing without a signed membership update from the original owner.");
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
            if (profile is null) return new(false, null, "Server not found.");
            var status = sharedWorlds.Status(profile) with
            { CanManageSharing = !authority.HasState(profileId) };
            return authority.HasState(profileId) && authority.GovernanceUnresolved(profileId)
                ? status with { Error = "Signed membership needs review. Sharing and recovery are paused on this PC." }
                : status;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> SharedRosterManagementAvailableAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            return settings.Profiles.Any(item => item.Id == profileId) &&
                !authority.HasState(profileId) && !SharedAuthorityBlocked(profileId, out _);
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
            authority.Append(record, enforceCurrentGrants: false);
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
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId) ??
                throw new InvalidDataException("This PC does not have that world.");
            authority.BindLocalSuccessor(profileId, recordHash, deviceId);
            var record = authority.LocalAuthorizedHead(profileId) ??
                throw new InvalidDataException("The bound successor is not the current authority head.");
            sharedWorlds.AdoptSuccessor(profile, record);
        }
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

    internal async Task<IReadOnlyList<SharedWorldRoster>?> SharedWorldRosterHistoryAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null || SharedAuthorityBlocked(profileId, out _)) return null;
            var current = sharedWorlds.ReadRoster(profile);
            if (current is null) return null;
            var chain = new SharedWorldRosterChainStore(data);
            return chain.HasState(profileId) ? chain.Read(profileId) : [current];
        }
        finally { gate.Release(); }
    }

    internal async Task<SharedWorldRoster> PublishDelegatedRosterAsync(Guid profileId,
        Guid deviceId, string enrolledPublicKey, SharedWorldRoster revision,
        Func<bool>? transportStillAuthorized = null)
    {
        await gate.WaitAsync();
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId) ??
                    throw new InvalidDataException("Server not found.");
                if (!profile.SharedSavesEnabled || profile.Kind == GameKinds.Custom ||
                    SharedAuthorityBlocked(profileId, out _) ||
                    authority.HasState(profileId) || transportStillAuthorized?.Invoke() == false)
                    throw new InvalidDataException("Sharing management is unavailable on this Host.");
                var parent = sharedWorlds.ReadRoster(profile) ??
                    throw new InvalidDataException("The current signed roster is unavailable.");
                var signer = parent.Members.SingleOrDefault(item => item.DeviceId == deviceId);
                if (signer is null || signer.PublicKey != enrolledPublicKey || signer.Revoked ||
                    !signer.Grants.ManageSharing ||
                    signer.AccessExpiresUtc is { } expiry && expiry <= DateTimeOffset.UtcNow ||
                    revision.HostAcceptedUtc is not null || revision.HostAcceptanceSignature is not null ||
                    revision.SignerDeviceId != deviceId || revision.SignerPublicKey != enrolledPublicKey ||
                    revision.PreviousRosterHash != SharedWorldRosterTrust.Hash(parent) ||
                    !SharedWorldRosterTrust.VerifyRevision(revision, parent,
                        parent.OwnerPublicKey, DateTimeOffset.UtcNow, true))
                    throw new InvalidDataException("This PC cannot publish that roster change.");
                var chain = new SharedWorldRosterChainStore(data);
                if (!chain.HasState(profileId)) chain.Append(parent, parent.OwnerPublicKey);
                var accepted = sharedWorlds.CountersignDelegatedRoster(revision, DateTimeOffset.UtcNow);
                chain.Append(accepted, parent.OwnerPublicKey);
                return sharedWorlds.ReadRoster(profile)!;
            }
        }
        finally { gate.Release(); }
    }

    internal async Task<IReadOnlyList<WorldAuthorityRecord>?> SharedWorldAuthorityAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            if (settings.Profiles.All(item => item.Id != profileId) ||
                SharedAuthorityBlocked(profileId, out _)) return null;
            return authority.Read(profileId);
        }
        finally { gate.Release(); }
    }

    public async Task<SharedWorldRoster> PublishSharedWorldRosterAsync(Guid profileId,
        IReadOnlyList<SharedWorldRosterMember> members, bool? ownerOverride = null,
        bool reviewSourceChange = false, SharedWorldOwnerEdit? ownerEdit = null)
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
            return sharedWorlds.PublishRoster(profile, members, ownerOverride, reviewSourceChange, ownerEdit);
        }
        finally { gate.Release(); }
    }

}

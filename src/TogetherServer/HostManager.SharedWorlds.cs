using System.Text.Json;

namespace TogetherServer;

public sealed record PlannedHandoffResult(bool Ok, string Code, string Message,
    SharedWorldVersion? Version = null, WorldAuthorityRecord? Authority = null);
public sealed record PlannedHandoffStatus(bool Pending, string Code, string Message,
    Guid? SuccessorDeviceId = null, long? FinalVersion = null,
    string? FinalVersionHash = null, bool ReceiptConfirmed = false,
    bool CanComplete = false, bool CanCancel = false);
public sealed record SharedWorldAuthorityHead(Guid GroupId, long Epoch, string RecordHash,
    string VersionHash, Guid HostDeviceId, string HostPublicKey, string HostAddress);
public sealed record SharedWorldAuthorityStatus(string State, string Message,
    SharedWorldAuthorityHead? Head = null,
    IReadOnlyList<SharedWorldAuthorityHead>? CompetingHeads = null,
    bool ExactManagedProcessRunning = false);
public sealed record PreparePlannedHandoffRequest(Guid SuccessorDeviceId, string SuccessorAddress);

internal sealed record PendingPlannedHandoff(int Schema, Guid ProfileId, Guid GroupId,
    string VersionHash, string RosterHash, Guid SuccessorDeviceId, string SuccessorAddress);

public sealed partial class HostManager
{
    private static string PlannedHandoffName(Guid profileId) =>
        $"planned-handoff-{profileId:N}.protected";

    public async Task<PlannedHandoffStatus> PlannedHandoffStatusAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var name = PlannedHandoffName(profileId);
            if (!data.HasProtected(name))
                return new(false, "NoPendingHandoff", "There is no planned handoff waiting for this world.");
            try
            {
                var bytes = data.LoadProtected(name);
                var pending = bytes is null ? null : JsonSerializer.Deserialize<PendingPlannedHandoff>(bytes);
                if (pending is not { Schema: 1 } || pending.ProfileId != profileId ||
                    pending.GroupId == Guid.Empty || pending.SuccessorDeviceId == Guid.Empty ||
                    pending.VersionHash is not { Length: 64 } ||
                    !pending.VersionHash.All(Uri.IsHexDigit) ||
                    pending.RosterHash is not { Length: 64 })
                    return new(true, "HandoffReviewRequired",
                        "The pending handoff could not be verified. Keep this world offline.");
                var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
                var roster = profile is null ? null : sharedWorlds.ReadRoster(profile);
                var version = profile is null ? null : sharedWorlds.Status(profile).Latest;
                var exact = profile?.SharedSavesEnabled == true && roster is not null &&
                    version is not null && roster.GroupId == pending.GroupId &&
                    version.GroupId == pending.GroupId && version.VersionHash == pending.VersionHash &&
                    WorldAuthorityTrust.RosterHash(roster) == pending.RosterHash &&
                    !authority.HasState(profileId) && !runs.Any(run => run.ProfileId == profileId);
                if (!exact)
                    return new(true, "HandoffReviewRequired",
                        "The post-Stop file copy, signed membership, or managed process needs review. Keep this world offline.",
                        pending.SuccessorDeviceId);
                var receipt = sharedWorlds.VerifiedReceipt(profile!, version!,
                    pending.SuccessorDeviceId, roster!);
                var currentAccess = pairing.TryCommitPlannedHandoff(profileId,
                    pending.SuccessorDeviceId, roster!, () => { });
                return new(true, receipt is null ? "WaitingForSuccessorCopy" :
                    currentAccess ? "ReadyToComplete" : "SuccessorAccessChanged",
                    receipt is null ? "The successor has not confirmed the exact post-Stop file copy yet." :
                    currentAccess ? "The exact copy is confirmed. The owner can complete the signed handoff." :
                    "The successor's current access changed. Cancel and review permissions.",
                    pending.SuccessorDeviceId, version!.Number, version.VersionHash,
                    receipt is not null, receipt is not null && currentAccess, true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException)
            {
                return new(true, "HandoffReviewRequired",
                    "The pending handoff could not be verified. Keep this world offline.");
            }
        }
        finally { gate.Release(); }
    }

    public async Task<PlannedHandoffResult> PreparePlannedHandoffAsync(Guid profileId,
        Guid successorDeviceId, string successorAddress)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null || !profile.SharedSavesEnabled)
                return new(false, "SharingOff", "Enable shared saves for this world first.");
            if (data.HasProtected(PlannedHandoffName(profileId)) || authority.HasState(profileId) ||
                SharedAuthorityBlocked(profileId, out _))
                return new(false, "HandoffAlreadyPending", "Review the current handoff or authority history first.");
            if (!Uri.TryCreate(successorAddress, UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0 ||
                successorAddress.Length is < 3 or > 255 ||
                !System.Net.IPAddress.TryParse(address.Host, out _))
                return new(false, "InvalidSuccessorAddress", "Enter the successor's direct HTTPS IP address and port.");
            var roster = sharedWorlds.ReadRoster(profile);
            var member = roster?.Members.SingleOrDefault(item => item.DeviceId == successorDeviceId);
            if (roster is null || member is not { Revoked: false, Grants: { Receive: true, EligibleHost: true } } ||
                member.AccessExpiresUtc is { } expiry && expiry <= clock.GetUtcNow() ||
                !pairing.TryCommitPlannedHandoff(profileId, successorDeviceId, roster, () => { }))
                return new(false, "SuccessorNotEligible", "Choose an approved PC allowed to receive and host this world.");
            var before = sharedWorlds.Status(profile).Latest?.VersionHash;
            var stopped = await StopUnderGateAsync(profileId, null);
            if (!stopped.Ok || stopped.Code == "StoppedBackupFailed")
                return new(false, "FinalSaveUnconfirmed", "The exact game process or its final backup was not confirmed. " + stopped.Message);
            var after = sharedWorlds.Status(profile).Latest;
            if (after is null || after.VersionHash == before || after.GroupId != roster.GroupId ||
                after.SigningPublicKey != roster.OwnerPublicKey)
                return new(false, "FinalSaveUnconfirmed", "The game stopped, but its hash-verified post-Stop file copy was not published. Keep this PC offline and review the backup.");
            var pending = new PendingPlannedHandoff(1, profileId, roster.GroupId,
                after.VersionHash, WorldAuthorityTrust.RosterHash(roster), successorDeviceId,
                successorAddress);
            data.SaveProtected(PlannedHandoffName(profileId), JsonSerializer.SerializeToUtf8Bytes(pending));
            return new(true, "WaitingForSuccessorCopy",
                "The hash-verified post-Stop file copy is available. Game load has not been checked. Wait for the successor's exact-copy confirmation before completing the handoff.", after);
        }
        finally { gate.Release(); }
    }

    public async Task<PlannedHandoffResult> CompletePlannedHandoffAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var name = PlannedHandoffName(profileId);
            if (!data.HasProtected(name))
                return new(false, "NoPendingHandoff", "Prepare a planned handoff first.");
            var bytes = data.LoadProtected(name);
            var pending = bytes is null ? null : JsonSerializer.Deserialize<PendingPlannedHandoff>(bytes);
            if (pending is not { Schema: 1 } || pending.ProfileId != profileId)
                return new(false, "HandoffStateInvalid", "The pending handoff could not be verified. Keep this world offline.");
            if (runs.Any(run => run.ProfileId == profileId))
                return new(false, "GameStillManaged", "The managed game process must be fully stopped before handoff.");
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null || !profile.SharedSavesEnabled || authority.HasState(profileId))
                return new(false, "HandoffChanged", "The shared world or authority changed. Review its history.");
            var roster = sharedWorlds.ReadRoster(profile);
            var version = sharedWorlds.Status(profile).Latest;
            if (roster is null || version is null || roster.GroupId != pending.GroupId ||
                version.GroupId != pending.GroupId || version.VersionHash != pending.VersionHash ||
                WorldAuthorityTrust.RosterHash(roster) != pending.RosterHash)
                return new(false, "HandoffChanged", "The post-Stop file copy or signed permissions changed. Review before continuing.");
            var receipt = sharedWorlds.VerifiedReceipt(profile, version, pending.SuccessorDeviceId, roster);
            if (receipt is null)
                return new(false, "WaitingForSuccessorCopy", "The successor has not confirmed this exact hash-verified file copy yet.", version);
            WorldAuthorityRecord? record = null;
            if (!pairing.TryCommitPlannedHandoff(profileId, pending.SuccessorDeviceId,
                roster, () =>
                {
                    record = sharedWorlds.SignPlannedHandoff(roster, version, receipt,
                        pending.SuccessorDeviceId, pending.SuccessorAddress, 1, null);
                    // The durable authority floor is the fence. Never report completion first.
                    authority.Append(record);
                }))
                return new(false, "SuccessorAccessChanged",
                    "The successor's access or signed membership changed. Cancel this unsigned handoff and review permissions.");
            data.DeleteProtected(name);
            Activity("Backup", "PlannedHandoffFenced",
                "The post-Stop file copy and successor receipt were verified. Game load has not been checked. This PC is fenced from starting or sharing this world.",
                ActivitySeverity.Important, profileId);
            return new(true, "OldHostFenced",
                "This PC is fenced. The successor must review local setup and verify its direct routes before starting.",
                version, record);
        }
        finally { gate.Release(); }
    }

    public async Task<PlannedHandoffResult> CancelPlannedHandoffAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var name = PlannedHandoffName(profileId);
            if (!data.HasProtected(name))
                return new(false, "NoPendingHandoff", "There is no pending planned handoff to cancel.");
            var bytes = data.LoadProtected(name);
            var pending = bytes is null ? null : JsonSerializer.Deserialize<PendingPlannedHandoff>(bytes);
            if (pending is not { Schema: 1 } || pending.ProfileId != profileId ||
                authority.HasState(profileId) || runs.Any(run => run.ProfileId == profileId))
                return new(false, "HandoffReviewRequired", "The pending handoff or managed process needs review. Keep this world offline.");
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            var roster = profile is null ? null : sharedWorlds.ReadRoster(profile);
            var version = profile is null ? null : sharedWorlds.Status(profile).Latest;
            if (roster is null || version is null || roster.GroupId != pending.GroupId ||
                version.GroupId != pending.GroupId || version.VersionHash != pending.VersionHash ||
                WorldAuthorityTrust.RosterHash(roster) != pending.RosterHash)
                return new(false, "HandoffReviewRequired", "The post-Stop file copy or signed permissions changed. Keep this world offline until reviewed.");
            data.DeleteProtected(name);
            Activity("Backup", "PlannedHandoffCanceled",
                "The owner canceled the unsigned handoff. Copies already received remain with their PCs.",
                ActivitySeverity.Important, profileId);
            return new(true, "HandoffCanceled",
                "The handoff was canceled before any signed authority change. This PC may host again; copies already received remain with their PCs.", version);
        }
        finally { gate.Release(); }
    }

    internal async Task<WorldAuthorityRecord?> ReadPlannedHandoffOfferAsync(Guid profileId,
        Guid successorDeviceId)
    {
        await gate.WaitAsync();
        try
        {
            var records = authority.Read(profileId);
            var heads = WorldAuthorityTrust.EffectiveHeads(records);
            return heads.Length == 1 && heads[0].Proposal.Kind == "Planned" &&
                heads[0].SuccessorReceipt?.DeviceId == successorDeviceId &&
                WorldAuthorityTrust.Verify(heads[0]) ? heads[0] : null;
        }
        finally { gate.Release(); }
    }
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
            if (data.HasProtected(PlannedHandoffName(profileId)))
                return new(false, "PlannedHandoffPending", "Complete or review the pending handoff first.");
            if (enabled && SharedAuthorityBlocked(profileId, out var reason))
                return new(false, "SharedWorldAuthorityBlocked", reason);
            if (enabled && authority.HasState(profileId) && !profile.SharedSavesEnabled)
                return new(false, "SuccessorRosterReadOnly",
                    "This successor PC cannot re-enable sharing without a signed membership update from the original owner.");
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
            var authorityStatus = SharedAuthorityStatusUnderGate(profile);
            var status = sharedWorlds.Status(profile);
            return status with
            {
                CanManageSharing = authorityStatus.State == "NoTakeover",
                Authority = authorityStatus
            };
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

    private SharedWorldAuthorityStatus SharedAuthorityStatusUnderGate(ServerProfile profile)
    {
        var exactRun = false;
        try
        {
            exactRun = runs.SingleOrDefault(run => run.ProfileId == profile.Id) is { } active &&
                Identity(active) == "Matched";
            if (!authority.HasState(profile.Id))
                return new("NoTakeover", "No takeover is recorded for this world.");
            var records = authority.Read(profile.Id);
            var heads = WorldAuthorityTrust.EffectiveHeads(records);
            if (heads.Length == 0)
                return new("ReviewRequired", "The hosting decision could not be verified. Keep this world offline.");
            SharedWorldAuthorityHead View(WorldAuthorityRecord record) => new(
                record.Proposal.GroupId, record.Proposal.Epoch, record.RecordHash,
                record.Version.VersionHash,
                record.Proposal.SuccessorBinding?.DeviceId ??
                    record.SuccessorReceipt?.DeviceId ??
                    record.Roster.Members.Single(member =>
                        member.PublicKey == record.Proposal.CandidatePublicKey).DeviceId,
                record.Proposal.CandidatePublicKey, record.Proposal.CandidateAddress);
            if (heads.Length > 1)
                return new("CompetingHistories",
                    "Competing hosting histories need review. Keep this world offline.",
                    CompetingHeads: heads.Select(View).ToArray(),
                    ExactManagedProcessRunning: exactRun);
            var head = View(heads[0]);
            if (authority.GovernanceUnresolved(profile.Id))
                return new("ReviewRequired", "Signed sharing membership needs review. Keep this world offline.",
                    ExactManagedProcessRunning: exactRun);
            if (authority.LocalAuthorizedHead(profile.Id) is null)
                return new("OldHostFenced", exactRun
                    ? "Another PC now hosts this world. The game is still running here; stop it gracefully and review the world history."
                    : "Another PC now hosts this world. This PC cannot start or share this world.",
                    head, ExactManagedProcessRunning: exactRun);
            if (SharedAuthorityBlocked(profile.Id, out _))
                return new("ReviewRequired", "The hosting decision or saved history needs review. Keep this world offline.",
                    ExactManagedProcessRunning: exactRun);
            return new("ThisPcHost", "This PC holds hosting authority for this world.", head,
                ExactManagedProcessRunning: exactRun);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or
                                   InvalidOperationException or ArgumentException)
        {
            return new("ReviewRequired",
                "The hosting decision could not be verified. Keep this world offline.",
                ExactManagedProcessRunning: exactRun);
        }
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

    internal async Task ApplySharedWorldAuthorityAsync(WorldAuthorityRecord record,
        Func<Action, bool>? authorizeCommit = null,
        IEnumerable<SharedWorldVersion>? lineageProof = null)
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
                record.Roster.Epoch < roster.Epoch || record.Roster.Revision < roster.Revision ||
                record.Schema == 2 && record.Roster.Signature != roster.Signature)
                throw new InvalidDataException("Authority roster is older or belongs to another group.");
            void Commit() => authority.Append(record, externalLineage: lineageProof);
            if (authorizeCommit is null) Commit();
            else if (!authorizeCommit(Commit))
                throw new UnauthorizedAccessException("The Friend PC's current sharing access changed.");
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

    internal async Task<IReadOnlyList<WorldAuthorityRecord>?> SharedWorldAuthorityAsync(
        Guid profileId, int offset = 0)
    {
        await gate.WaitAsync();
        try
        {
            if (settings.Profiles.All(item => item.Id != profileId) ||
                SharedAuthorityBlocked(profileId, out _)) return null;
            return authority.ReadPage(profileId, offset);
        }
        finally { gate.Release(); }
    }

    internal async Task<WorldAuthorityOwnerApproval> SignSharedWorldResolutionAsync(
        Guid profileId, WorldAuthorityOffer offer)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId) ??
                throw new InvalidDataException("The shared world is unavailable.");
            var roster = sharedWorlds.ReadRoster(profile) ??
                throw new InvalidDataException("The signed roster is unavailable.");
            if (!roster.OwnerOverride || offer.Proposal.Kind != "ResolutionOwnerOverride" ||
                offer.Proposal.ProfileId != profileId ||
                offer.Roster.Signature != roster.Signature ||
                !SharedWorldElection.VerifyOffer(offer))
                throw new InvalidDataException("The current roster does not allow this owner approval.");
            var records = authority.Read(profileId);
            var heads = WorldAuthorityTrust.EffectiveHeads(records);
            var draft = new WorldAuthorityRecord(2, offer.Proposal, offer.Roster,
                offer.Version, [], null, "");
            if (!WorldAuthorityTrust.ExactResolutionHeads(draft, heads) ||
                !WorldAuthorityTrust.VerifyLineage(draft, heads.Single(head =>
                    head.RecordHash == offer.Proposal.ParentAuthorityHash)))
                throw new InvalidDataException("The selected save or complete head set changed.");
            return authority.OwnerApproval(profileId, offer.Proposal,
                () => sharedWorlds.SignResolutionOwnerApproval(offer.Proposal));
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
            if ((!profile.SharedSavesEnabled && !reviewSourceChange) || profile.Kind == GameKinds.Custom)
                throw new InvalidDataException("Shared saves are not enabled for this server.");
            if (data.HasProtected(PlannedHandoffName(profileId)))
                throw new InvalidDataException("A planned handoff is pending; keep this signed roster unchanged.");
            if (SharedAuthorityBlocked(profileId, out _))
                throw new InvalidDataException("Shared world authority blocks roster publication from this PC.");
            return sharedWorlds.PublishRoster(profile, members, ownerOverride, reviewSourceChange);
        }
        finally { gate.Release(); }
    }

}

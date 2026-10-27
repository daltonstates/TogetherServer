using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed partial class HostManager
{
    // Retain the old local migration path for a server whose sharing is off.
    // The enabled server policy owns its receiving/forwarding/hosting grants;
    // per-PC controls cannot contradict that one choice.
    public async Task<PairingDecision> SetSharedWorldGrantsAsync(Guid deviceId,
        Guid profileId, SharedWorldGrants grants)
    {
        await gate.WaitAsync();
        try
        {
            if (deviceId == Guid.Empty || profileId == Guid.Empty || grants is null)
                return new(false, "InvalidGrants", "Choose an approved PC and the reviewed save permissions.");
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
                if (profile is null) return new(false, "UnknownProfile", "Choose a saved server.");
                if (profile.SharedSavesEnabled)
                    return new(false, "ServerHostingSettingRequired",
                        "Use Allow friends to host this server. Remove a PC's server assignment or access to exclude it.");
                if (profile.WorldLoadRehearsalId is not null)
                    return new(false, "WorldLoadCannotShare", "A disposable load rehearsal cannot publish or change shared-world authority.");
                if (profile.SeparateCopySourceProfileId is not null || profile.SeparateCopyBranchHash is not null)
                    return new(false, "SeparateCopyCannotShare", "Keep this separate history's original signed sharing permissions.");
                if (authority.HasState(profileId) || SharedAuthorityBlocked(profileId, out _))
                    return new(false, "SuccessorRosterReadOnly", "Change these save permissions on the original owner's PC.");
                if (data.HasProtected(PlannedHandoffName(profileId)))
                    return new(false, "PlannedHandoffPending", "Complete or review the pending move before changing save permissions.");

                // Validate canonical access before saving any prerequisite. The
                // shared mutation gate prevents revocation, assignment and
                // membership changes between validation and the pairing commit.
                var access = pairing.AuthorizeActiveDevice(deviceId, out var device);
                if (!access.Ok) return access;
                if (device!.PeerWorldGroupKey is not null)
                    return new(false, "PeerMembershipReadOnly", "Change permissions on the original signed member, not a session credential.");
                if (!pairing.CanAccess(device, profileId))
                    return new(false, "PermissionDenied", "Approve and assign this PC to the server first.");

                // A paused grant edit may advance this same signed roster. It
                // cannot review a changed source or replace a missing clock.
                // Inspect the binding before saving settings or pairing state.
                var previousRoster = sharedWorlds.ReadRoster(profile);
                var sharedRoot = Path.Combine(data.RootPath, "shared-worlds", profileId.ToString("N"));
                if (previousRoster is null && Directory.Exists(sharedRoot) &&
                    Directory.EnumerateFileSystemEntries(sharedRoot).Any())
                    return new(false, "SharedRosterReviewRequired",
                        "The existing sharing identity needs review. Keep its source and signed history before changing permissions.");

                var preparedSharing = false;
                if (grants is { Receive: true, EligibleHost: true } &&
                    (!profile.SharedSavesEnabled || profile.Backups?.Enabled != true))
                {
                    if (data.Recovery.LifecycleBlocked)
                        return new(false, "DataRecoveryRequired", "Review local data recovery before enabling save hosting.");
                    if (data.HasProtected(SetupRestorePendingName(profileId)))
                        return new(false, "SetupRestoreRecoveryRequired", "Finish the interrupted setup restore before enabling save hosting.");
                    if (profile.Kind == GameKinds.Custom || !games.TryGet(profile.Kind, out var driver) ||
                        !driver.SupportsBackups)
                        return new(false, "SharingUnsupported", "Only reviewed built-in game worlds can be shared.");

                    var next = JsonSerializer.Deserialize<HostSettings>(JsonSerializer.SerializeToUtf8Bytes(settings)) ??
                        throw new InvalidDataException("The current Host settings could not be copied.");
                    var changed = next.Profiles.Single(item => item.Id == profileId);
                    changed.Backups ??= new();
                    changed.Backups.Enabled = true;
                    changed.SharedSavesEnabled = true;
                    var saved = UpdateSettingsLocked(next);
                    if (!saved.Ok) return new(false, saved.Code, saved.Message);
                    preparedSharing = true;
                }

                var result = pairing.SetSharedWorldGrants(deviceId, profileId, grants);
                if (!result.Ok) return result;
                var currentProfile = settings.Profiles.Single(item => item.Id == profileId);
                var members = pairing.SharedRosterMembers(profileId);
                // Signing enrollment can still be pending. Its grant choices
                // remain in canonical pairing state until that key is enrolled.
                var edit = members.Any(member => member.DeviceId == deviceId)
                    ? new SharedWorldOwnerEdit(deviceId, Grants: grants, Revoked: false) : null;
                var roster = sharedWorlds.PublishRoster(currentProfile, members,
                    reviewSourceChange: false, ownerEdit: edit);
                pairing.ConfirmSharedRosterPublished(profileId, roster);
                if (preparedSharing) AnnounceSharingPolicy(profileId, true);
                return result.Ok && preparedSharing
                    ? result with { Message = "This PC can host this save. Backups after Stop and save sharing are enabled. Your friend still approves storage and prepares their server." }
                    : result;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or CryptographicException or ArgumentException)
        {
            // Settings and protected pairing snapshots are separate durable
            // files. Incomplete signed publication stays fenced by dirty roster
            // state; PairingService invalidates access on pairing save failure.
            return new(false, "SharedHostingPermissionUnconfirmed",
                "The save permission could not be confirmed. Review permissions before retrying; backup and sharing settings may already be enabled.");
        }
        finally { gate.Release(); }
    }
}

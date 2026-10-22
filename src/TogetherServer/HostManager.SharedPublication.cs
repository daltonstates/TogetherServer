namespace TogetherServer;

public sealed partial class HostManager
{
    // Reuse a sealed post-Stop backup. This never reads the live world, creates
    // a new backup, changes sharing grants, or starts/stops a game process.
    internal async Task<SharedWorldResult> RetrySharedSavePublicationAsync(Guid profileId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return new(false, "UnknownProfile", "Choose a saved server.");
            if (!profile.SharedSavesEnabled)
                return new(false, "SharingOff", "Turn on Allow friends to host this server first.");
            if (profile.WorldLoadRehearsalId is not null || profile.SeparateCopySourceProfileId is not null ||
                profile.SeparateCopyBranchHash is not null || authority.HasState(profileId))
                return new(false, "OwnerSharingRequired", "Share the backup from the original owner's PC.");
            if (data.Recovery.LifecycleBlocked || data.HasProtected(SetupRestorePendingName(profileId)))
                return new(false, "DataRecoveryRequired", "Finish this PC's recovery review before sharing a backup.");
            if (data.HasProtected(PlannedHandoffName(profileId)))
                return new(false, "PlannedHandoffPending", "Finish or cancel the hosting move before sharing a backup.");
            if (SharedAuthorityBlocked(profileId, out var reason))
                return new(false, "SharedWorldAuthorityBlocked", reason);
            if (pairing.SharedRosterDirty(profileId) || sharedWorlds.ReadRoster(profile) is null)
                return new(false, "SharingAccessNeedsReview", "Confirm this server's sharing permissions before sharing its backup.");
            if (!games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups || profile.Kind == GameKinds.Custom)
                return new(false, "SharingUnsupported", "This game does not support shared hosting.");

            var ordered = backups.CompletedRollingForSharing(profile);
            var completed = ordered.LastOrDefault();
            if (completed is null)
                return new(false, "NoCompletedBackup", "No completed backup after Stop is available. Stop the server normally once to create the first shared save.");
            var latest = sharedWorlds.Status(profile).Latest;
            if (latest is not null && latest.BackupId != completed.Id &&
                (latest.CaptureKind != SharedWorldCaptureKinds.PostStopBackup ||
                    !ordered.Any(item => item.Id == latest.BackupId)))
                return new(false, "SharedSaveOrderNeedsReview", "The order of this backup and the existing shared save could not be confirmed. Keep both copies. A normal server Stop can publish a fresh completed save.");

            cancellationToken.ThrowIfCancellationRequested();
            var result = sharedWorlds.RetryAfterStop(profile, completed.Id, latest?.VersionHash);
            Activity("Backup", result.Ok ? "SharedSavePublished" : "SharedSavePublishFailed",
                result.Ok ? "The completed backup is available to approved PCs. Receipt is confirmed separately." :
                    "The completed backup remains local; shared save publication needs review.",
                result.Ok ? ActivitySeverity.Important : ActivitySeverity.Warning, profileId);
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {
            return new(false, "SharedPublicationNeedsReview", "The completed backup or its signed sharing history could not be checked. Keep the saved copies and review sharing before retrying.");
        }
        finally { gate.Release(); }
    }
}

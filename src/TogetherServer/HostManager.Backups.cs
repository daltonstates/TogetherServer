using System.Text.Json;

namespace TogetherServer;

public sealed partial class HostManager
{
    public async Task<ActionResult> CreateManualBackupAsync(Guid profileId)
    {
        return await CreateOfflineBackupAsync(profileId, requireCompleteSetup: false);
    }

    public async Task<ActionResult> CreateCompleteSetupBackupAsync(Guid profileId)
    {
        return await CreateOfflineBackupAsync(profileId, requireCompleteSetup: true);
    }

    private async Task<ActionResult> CreateOfflineBackupAsync(Guid profileId, bool requireCompleteSetup)
    {
        ServerProfile profile;
        string saveDirectory;
        await gate.WaitAsync();
        try
        {
            var selected = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (selected is null) return Result(false, "UnknownProfile", "Choose a saved server.");
            if (requireCompleteSetup && ServerFileEditBlock(selected, "setup") is { } blocked)
                return Result(false, blocked.Code, blocked.Message);
            if (PrepareManualBackupUnderGate(profileId, out var preparedProfile, out var preparedDirectory) is { } rejected)
                return rejected;
            profile = preparedProfile!;
            saveDirectory = preparedDirectory!;
            worldCopyReservations.Add(profileId);
            AdvanceReadModel();
            Volatile.Write(ref lastOwnerSnapshot, Snapshot());
        }
        finally { gate.Release(); }

        WorldBackupResult created;
        try { created = await Task.Run(() => backups.Create(profile, BackupKinds.Manual, saveDirectory)); }
        finally
        {
            await gate.WaitAsync();
            try { worldCopyReservations.Remove(profileId); }
            finally { gate.Release(); }
        }
        await gate.WaitAsync();
        try { return ManualBackupResult(profileId, created, requireCompleteSetup); }
        finally { gate.Release(); }
    }

    public async Task<WorldBackupVerificationResult> VerifyBackupAsync(Guid profileId, Guid backupId)
    {
        ServerProfile? profile;
        await gate.WaitAsync();
        try { profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId); }
        finally { gate.Release(); }
        if (profile is null)
            return new(false, "UnknownProfile", "Choose a saved profile.", backupId, clock.GetUtcNow());
        if (!games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups)
            return new(false, "BackupsUnsupported",
                "Backup verification is available only for reviewed built-in game drivers.", backupId, clock.GetUtcNow());
        var verified = await Task.Run(() => backups.Verify(profile, backupId));
        Activity("Backup", verified.Ok ? "IntegrityVerified" : "IntegrityFailed",
            verified.Ok ? "A local-owner backup integrity check completed." :
                "A local-owner backup integrity check failed. The live world was not changed.",
            verified.Ok ? ActivitySeverity.Important : ActivitySeverity.Warning, profileId);
        return verified;
    }

    public async Task<BackupSafetyResult> CopyBackupToVaultAsync(Guid profileId, Guid backupId,
        string destinationRoot)
    {
        ServerProfile? profile;
        await gate.WaitAsync();
        try { profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId); }
        finally { gate.Release(); }
        if (profile is null)
            return new(false, "UnknownProfile", "Choose a saved profile.", backupId, clock.GetUtcNow());
        if (!games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups)
            return new(false, "BackupsUnsupported",
                "Backup vault copies are available only for reviewed built-in game drivers.", backupId, clock.GetUtcNow());
        var result = backups.CopyToVault(profile, backupId, destinationRoot);
        Activity("Backup", result.Ok ? "VaultCopyVerified" : "VaultCopyFailed",
            result.Ok ? "A completed backup was copied to the selected vault and hash-verified." :
                "A backup-vault copy was not confirmed; the local backup was kept.",
            result.Ok ? ActivitySeverity.Important : ActivitySeverity.Warning, profileId);
        return result;
    }

    public async Task<HostMoveKitResult> PrepareMoveKitAsync(Guid profileId, Guid backupId,
        string destinationRoot)
    {
        ServerProfile? profile;
        await gate.WaitAsync();
        try
        {
            profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return new(false, "UnknownProfile", "Choose a saved server.");
            if (runs.Any(run => run.ProfileId == profileId))
                return new(false, "ServerRunning", "Stop this server before preparing a move kit.");
            if (!games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups)
                return new(false, "BackupsUnsupported", "This game does not have a reviewed backup path.");
        }
        finally { gate.Release(); }
        return await Task.Run(() => backups.PrepareMoveKit(profile, backupId, destinationRoot));
    }

    public HostMoveKitResult InspectMoveKit(string directory) => backups.InspectMoveKit(directory);

    public async Task<BackupSafetyResult> RehearseRestoreAsync(Guid profileId, Guid backupId)
    {
        ServerProfile? profile;
        await gate.WaitAsync();
        try { profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId); }
        finally { gate.Release(); }
        if (profile is null)
            return new(false, "UnknownProfile", "Choose a saved profile.", backupId, clock.GetUtcNow());
        if (!games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups)
            return new(false, "BackupsUnsupported",
                "Restore rehearsal is available only for reviewed built-in game drivers.", backupId, clock.GetUtcNow());
        var result = backups.RehearseRestore(profile, backupId);
        Activity("Backup", result.Ok ? "RestoreRehearsalCompleted" : "RestoreRehearsalFailed",
            result.Ok ? "A completed backup restored into scratch storage and passed hash verification." :
                "A disposable restore rehearsal failed; the live world was not changed.",
            result.Ok ? ActivitySeverity.Important : ActivitySeverity.Warning, profileId);
        return result;
    }

    private ActionResult CreateManualBackupUnderGate(Guid profileId, bool requireCompleteSetup = false)
    {
        if (PrepareManualBackupUnderGate(profileId, out var profile, out var saveDirectory) is { } rejected)
            return rejected;
        return ManualBackupResult(profileId, backups.Create(profile!, BackupKinds.Manual, saveDirectory!), requireCompleteSetup);
    }

    private ActionResult? PrepareManualBackupUnderGate(Guid profileId, out ServerProfile? profile,
        out string? saveDirectory)
    {
        profile = null;
        saveDirectory = null;
        if (data.Recovery.LifecycleBlocked)
            return Result(false, "DataRecoveryRequired",
                "Review and acknowledge the recovered local data before creating a backup.");
        profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
        if (profile is null) return Result(false, "UnknownProfile", "Choose a saved profile.");
        if (!games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups)
            return Result(false, "BackupsUnsupported",
                "Manual backups are available only for reviewed built-in game drivers.");
        var activeSave = BlockIfSaveDirectoryActive(profile, "creating a manual backup");
        if (activeSave is not null) return activeSave;
        saveDirectory = driver.ManagedSaveDirectory(profile);
        if (saveDirectory is null)
            return Result(false, "BackupsUnsupported",
                "The selected driver does not expose a reviewed save-only directory.");
        return null;
    }

    private ActionResult ManualBackupResult(Guid profileId, WorldBackupResult created, bool requireCompleteSetup)
    {
        if (created.Ok && requireCompleteSetup && created.Backup?.SetupIncluded != true)
            return Result(false, "SetupCheckpointFailed",
                "The world backup completed, but a complete setup checkpoint did not. No server files were changed.");
        Activity("Backup", created.Ok ? "ManualBackupCompleted" : "ManualBackupFailed",
            created.Ok ? "A local-owner offline backup completed." :
                "A local-owner offline backup failed. Review world protection.",
            created.Ok ? ActivitySeverity.Important : ActivitySeverity.Warning, profileId);
        return Result(created.Ok, created.Ok ? "ManualBackupCompleted" : created.Code, created.Message);
    }

    public async Task<ActionResult> RestoreBackupAsync(Guid profileId, Guid backupId)
    {
        ServerProfile profile;
        string saveDirectory;
        await gate.WaitAsync();
        try
        {
            if (data.Recovery.LifecycleBlocked)
                return Result(false, "DataRecoveryRequired",
                    "Review and acknowledge the recovered local data before restoring a backup.");
            var selected = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (selected is null) return Result(false, "UnknownProfile", "Choose a saved profile.");
            if (!games.TryGet(selected.Kind, out var driver) || !driver.SupportsBackups)
                return Result(false, "BackupsUnsupported", "Backups and restore are available only for reviewed built-in game drivers.");
            var activeSave = BlockIfSaveDirectoryActive(selected, "restoring a backup");
            if (activeSave is not null) return activeSave;
            if (crashRecovery.RemoveAll(item => item.ProfileId == profileId) > 0)
                data.SaveCrashRecoveryStates(crashRecovery);
            var preparedDirectory = driver.ManagedSaveDirectory(selected);
            if (preparedDirectory is null)
                return Result(false, "BackupsUnsupported", "The selected driver does not expose a reviewed save-only directory.");
            profile = selected;
            saveDirectory = preparedDirectory;
            worldCopyReservations.Add(profileId);
            AdvanceReadModel();
            Volatile.Write(ref lastOwnerSnapshot, Snapshot());
        }
        finally { gate.Release(); }

        WorldBackupResult restored;
        try { restored = await Task.Run(() => backups.Restore(profile, backupId, saveDirectory)); }
        finally
        {
            await gate.WaitAsync();
            try { worldCopyReservations.Remove(profileId); }
            finally { gate.Release(); }
        }
        await gate.WaitAsync();
        try
        {
            Activity("Backup", restored.Ok ? "RestoreCompleted" : "RestoreFailed",
                restored.Ok ? "A local-owner backup restore completed." : "A local-owner backup restore failed. Review details locally.",
                restored.Ok ? ActivitySeverity.Important : ActivitySeverity.Warning, profileId);
            return Result(restored.Ok, restored.Code, restored.Message);
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> RestoreCompleteSetupAsync(Guid profileId, Guid backupId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return Result(false, "UnknownProfile", "Choose a saved profile.");
            if (ServerFileEditBlock(profile, "setup", allowPendingRestore: true) is { } blocked)
                return Result(false, blocked.Code, blocked.Message);
            if (!games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups ||
                driver.ManagedSaveDirectory(profile) is not { } saveDirectory)
                return Result(false, "BackupsUnsupported", "This server does not have a reviewed save directory.");
            ServerSetupSnapshot? setup;
            try { setup = backups.ReadSetup(profile, backupId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       System.Security.Cryptography.CryptographicException or JsonException)
            { return Result(false, "SetupCheckpointInvalid", "The protected setup checkpoint could not be verified."); }
            if (setup is null)
                return Result(false, "SetupCheckpointMissing", "This backup contains only the world. Choose a newer complete setup checkpoint.");
            try
            {
                // The pre-restore checkpoint must be able to capture the current configuration too.
                ServerSetupSnapshots.Capture(profile, data);
                data.SaveProtected(SetupRestorePendingName(profileId),
                    System.Text.Encoding.UTF8.GetBytes(backupId.ToString("N")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       System.Security.Cryptography.CryptographicException or JsonException or
                                       ArgumentException or NotSupportedException)
            { return Result(false, "SetupCheckpointFailed", "The current setup could not be protected before restore."); }
            var restoredWorld = backups.Restore(profile, backupId, saveDirectory);
            if (!restoredWorld.Ok || restoredWorld.PreRestoreBackupId is not { } beforeId)
                return Result(false, "SetupRestoreRecoveryRequired",
                    restoredWorld.Message + " Keep this server offline and retry a complete setup restore.");
            try
            {
                ServerSetupSnapshots.Restore(profile, setup);
                ServerAddOns.RestoreRecordedVersion(data, profile, setup.GameVersion);
                var restoredAddOns = ServerAddOns.List(data, profile);
                if (!restoredAddOns.Ok || restoredAddOns.StateToken != setup.AddOnStateToken)
                    throw new InvalidDataException("The restored add-on inventory differs from the checkpoint.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       System.Security.Cryptography.CryptographicException or JsonException or
                                       ArgumentException or NotSupportedException)
            {
                try
                {
                    var earlier = backups.ReadSetup(profile, beforeId);
                    var rollbackWorld = backups.Restore(profile, beforeId, saveDirectory);
                    if (!rollbackWorld.Ok || earlier is null)
                        return Result(false, "SetupRestoreRecoveryRequired",
                            "Setup restore failed after the world changed. The prior checkpoint is retained for recovery.");
                    ServerSetupSnapshots.Restore(profile, earlier);
                    ServerAddOns.RestoreRecordedVersion(data, profile, earlier.GameVersion);
                    data.DeleteProtected(SetupRestorePendingName(profileId));
                    return Result(false, "SetupRestoreRolledBack",
                        "Setup restore failed; the prior world and reviewed configuration were restored. The server remains offline.");
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or
                                                  InvalidDataException or System.Security.Cryptography.CryptographicException or
                                                  JsonException or ArgumentException or NotSupportedException)
                {
                    return Result(false, "SetupRestoreRecoveryRequired",
                        "Setup restore failed and automatic rollback was not confirmed. Keep this server offline and inspect the retained pre-restore checkpoint.");
                }
            }
            try { data.DeleteProtected(SetupRestorePendingName(profileId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result(false, "SetupRestoreRecoveryRequired",
                "World and reviewed setup were restored, but the recovery marker could not be cleared. Retry the complete setup restore before Start.");
            }
            Activity("Backup", "CompleteSetupRestored",
                "The Host restored the world, reviewed configuration, and managed add-on state from one checkpoint.",
                ActivitySeverity.Important, profileId);
            return Result(true, "CompleteSetupRestored",
                "World and reviewed setup restored while offline. Start the server and check a real game join. " +
                restoredWorld.Message);
        }
        finally { gate.Release(); }
    }

    private ActionResult? BlockIfSaveDirectoryActive(ServerProfile profile, string action)
    {
        if (WorldCopyBlock(profile, action) is { } copyBlock) return copyBlock;
        foreach (var run in runs.Where(item => WorldConflict(item, profile)).ToList())
        {
            var identity = Identity(run);
            if (identity == "Missing")
            {
                ArchiveDefinitivelyExitedRun(run, ServerSessionEndReason.ProcessExitedBeforeRestore);
                continue;
            }
            var sameProfile = run.ProfileId == profile.Id;
            var runningName = settings.Profiles.SingleOrDefault(item => item.Id == run.ProfileId)?.Name ?? "another server";
            if (identity == "Matched")
                return Result(false, sameProfile ? "ServerRunning" : "WorldRunning",
                    sameProfile
                        ? $"Stop the server gracefully before {action}."
                        : $"{runningName} is using the same save directory. Stop it gracefully before {action}.");
            return Result(false, "IdentityUnknown",
                sameProfile
                    ? $"Process identity is uncertain, so {action} remains blocked."
                    : $"Process identity for {runningName} is uncertain. Resolve that run before {action}.");
        }
        return null;
    }

    private ActionResult? WorldCopyBlock(ServerProfile profile, string action)
    {
        var target = CanonicalWorldPath(profile.WorldDirectory);
        return worldCopyReservations.Any(id => settings.Profiles.Any(candidate => candidate.Id == id &&
            CanonicalWorldPath(candidate.WorldDirectory).Equals(target, StringComparison.OrdinalIgnoreCase)))
            ? Result(false, "WorldCopyInProgress", $"Wait for the world copy to finish before {action}.")
            : null;
    }

}

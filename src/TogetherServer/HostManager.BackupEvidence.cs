namespace TogetherServer;

public sealed partial class HostManager
{
    public async Task<BackupCatalogView> BackupCatalogAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            return profile is null
                ? WorldBackupService.EmptyCatalog(profileId, "UnknownProfile", "Choose a saved Host server.")
                : backups.Catalog(profile);
        }
        catch (Exception ex) when (WorldBackupService.BackupEvidenceFailure(ex))
        { return WorldBackupService.EmptyCatalog(profileId, "BackupCatalogUnavailable", "The completed catalog needs local review."); }
        finally { gate.Release(); }
    }

    private async Task<BackupEvidenceLease?> CaptureBackupEvidenceLeaseAsync(Guid profileId, Guid backupId)
    {
        await gate.WaitAsync();
        try
        {
            return settings.Profiles.Any(item => item.Id == profileId)
                ? backups.LeaseBackupEvidence(profileId, backupId) : null;
        }
        catch (Exception ex) when (WorldBackupService.BackupEvidenceFailure(ex)) { return null; }
        finally { gate.Release(); }
    }

    // These wrappers call existing measured operations unchanged. Route wiring
    // belongs to the coordinator. There is no client-submitted evidence setter.
    public async Task<WorldBackupVerificationResult> VerifyBackupWithEvidenceAsync(Guid profileId, Guid backupId)
    {
        using var evidence = await CaptureBackupEvidenceLeaseAsync(profileId, backupId);
        var result = await VerifyBackupAsync(profileId, backupId);
        if (result.BackupId != backupId || result.Code is not ("BackupVerified" or "BackupIntegrityFailed")) return result;
        var saved = evidence is not null && backups.RecordBackupEvidence(profileId, backupId, evidence.CompletionIdentity, BackupEvidenceKinds.Integrity,
            result.Ok ? "Passed" : "Failed", result.CheckedUtc, result.Code);
        return saved ? result : result with { Message = result.Message + " The dated result could not be saved; review the catalog." };
    }

    public async Task<BackupSafetyResult> CopyBackupToVaultWithEvidenceAsync(Guid profileId, Guid backupId, string destinationRoot)
    {
        using var evidence = await CaptureBackupEvidenceLeaseAsync(profileId, backupId);
        var result = await CopyBackupToVaultAsync(profileId, backupId, destinationRoot);
        if (result.BackupId != backupId || result.Code is not ("VaultCopyVerified" or "VaultCopyFailed")) return result;
        var saved = evidence is not null && backups.RecordBackupEvidence(profileId, backupId, evidence.CompletionIdentity, BackupEvidenceKinds.Vault,
            result.Ok ? "Passed" : "Failed", result.CompletedUtc, result.Code);
        return saved ? result : result with { Message = result.Message + " The dated result could not be saved; review the catalog." };
    }

    public async Task<BackupSafetyResult> RehearseRestoreWithEvidenceAsync(Guid profileId, Guid backupId)
    {
        using var evidence = await CaptureBackupEvidenceLeaseAsync(profileId, backupId);
        var result = await RehearseRestoreAsync(profileId, backupId);
        if (result.BackupId != backupId || result.Code is not ("RestoreRehearsalCompleted" or "RestoreRehearsalFailed")) return result;
        var saved = evidence is not null && backups.RecordBackupEvidence(profileId, backupId, evidence.CompletionIdentity, BackupEvidenceKinds.HashRehearsal,
            result.Ok ? "Passed" : "Failed", result.CompletedUtc, result.Code);
        return saved ? result : result with { Message = result.Message + " The dated result could not be saved; review the catalog." };
    }

    public async Task<WorldLoadRehearsalResult> ConfirmWorldLoadWithBackupEvidenceAsync(Guid id, WorldLoadConfirmationRequest confirmation)
    {
        // The canonical rehearsal method alone decides order, unchanged binary,
        // exact run and owner confirmation. This records its accepted outcome.
        var result = await WorldLoadRehearsalAsync(id, "confirm", confirmation);
        if (!result.Ok || result.Rehearsal?.Id != id) return result;
        await gate.WaitAsync();
        try
        {
            var rehearsal = WorldLoadRecords().SingleOrDefault(item => item.Id == id);
            if (rehearsal is null || rehearsal.SourceKind != "Backup") return result;
            if (rehearsal.LoadOutcome != result.Rehearsal.LoadOutcome || rehearsal.ChangeOutcome != result.Rehearsal.ChangeOutcome ||
                rehearsal.RestartOutcome != result.Rehearsal.RestartOutcome)
                return result with { Message = result.Message + " A newer owner confirmation needs its own dated record." };
            var backupId = backups.BackupIdForEvidenceIdentity(rehearsal.SourceProfileId, rehearsal.CopyIdentity);
            if (backupId is null)
                return result with { Message = result.Message + " This result could not be matched to a retained backup." };
            var outcome = OwnerGameBackupOutcome(rehearsal.LoadOutcome, rehearsal.ChangeOutcome, rehearsal.RestartOutcome);
            var saved = backups.RecordBackupEvidence(rehearsal.SourceProfileId, backupId.Value, rehearsal.CopyIdentity,
                BackupEvidenceKinds.OwnerGameRehearsal, outcome, clock.GetUtcNow(),
                outcome == "Passed" ? "OwnerGameRehearsalConfirmed" : outcome == "Failed" ? "OwnerGameRehearsalFailed" : "OwnerGameRehearsalIncomplete");
            return saved ? result : result with { Message = result.Message + " The dated result could not be saved; review the catalog." };
        }
        catch (Exception ex) when (WorldBackupService.BackupEvidenceFailure(ex))
        { return result with { Message = result.Message + " The dated result could not be saved; review the catalog." }; }
        finally { gate.Release(); }
    }

    internal static string OwnerGameBackupOutcome(string load, string change, string restart)
    {
        var outcomes = new[] { load, change, restart };
        return outcomes.Any(item => item == "OwnerFailed") ? "Failed" :
            outcomes.All(item => item == "OwnerConfirmed") ? "Passed" : "Incomplete";
    }
}

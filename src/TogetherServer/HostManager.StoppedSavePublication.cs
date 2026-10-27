using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed partial class HostManager
{
    private const string StoppedProgressProofMessage =
        "The stopped save could not be matched to its latest completed backup. Existing worlds and backups were kept.";
    private const string StoppedProgressSharedMessage =
        "This stopped save is now shared within its signed history. Recheck hosting, then choose Start hosting when this PC is ready.";
    internal Func<ManagedRunArchive, bool>? StoppedSaveProcessAbsentForChecks { get; set; }

    // Passive metadata only. This is an attention projection, never evidence
    // authorizing publication, recapture, or Start.
    internal async Task<bool> StoppedSeparateSavePublicationPendingAsync(Guid sourceId, Guid localId,
        string branchHash, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == localId);
            var state = ReadSeparateHost(localId);
            if (profile?.SeparateCopySourceProfileId != sourceId || profile.SeparateCopyBranchHash != branchHash ||
                state is not { Ready: true, Started: true } || state.SourceProfileId != sourceId || state.BranchHash != branchHash)
                return false;
            var completed = backups.CompletedRollingForSharing(profile).LastOrDefault();
            if (completed is null) return false;
            var branch = CurrentSeparateBranch(sourceId, branchHash);
            if (branch is null) return false;
            var published = new SharedWorldSeparateCopyStore(data).ReadPublishedManifestHead(branch);
            return published is null || published.CaptureKind != SharedWorldCaptureKinds.PostStopBackup ||
                published.BackupId != completed.Id;
        }
        finally { gate.Release(); }
    }

    // Called only by the fixed, explicit local-owner repair action after the
    // Friend service checks its exact connection and local receive consent.
    // No remote peer request, poll, Prepare or Start calls this operation.
    internal async Task<ActionResult> RetryStoppedSeparateSavePublicationAsync(Guid sourceId, Guid localId,
        string branchHash, CancellationToken cancellationToken, Action? requireCurrentSelection = null)
    {
        try { requireCurrentSelection?.Invoke(); }
        catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
        {
            return Result(false, "StoppedProgressSharingChanged",
                "The selected sharing connection changed. Recheck it before sharing stopped progress.");
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!SharedWorldPeerTrust.ValidHash(branchHash) || sourceId == Guid.Empty || localId == Guid.Empty)
                return Result(false, "StoppedProgressProofRequired", StoppedProgressProofMessage);
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == localId);
            if (profile is null || profile.SeparateCopySourceProfileId != sourceId || profile.SeparateCopyBranchHash != branchHash)
                return Result(false, "StoppedProgressSharingChanged", "Current sharing permission or the exact signed history needs review. Existing saves were kept.");
            if (StoppedPublicationBusy(profile))
                return Result(false, "StoppedProgressBusy", "Keep this server stopped and finish other world operations before sharing its stopped save.");
            var context = SharedWorldBranchLiveSource.Resolve(data, profile, clock.GetUtcNow(), requireStarted: true);
            if (context.Branch is null || context.Branch.BranchHash != branchHash || context.Root.ProfileId != sourceId)
                return Result(false, "StoppedProgressSharingChanged", "Current sharing permission or the exact signed history needs review. Existing saves were kept.");
            var branch = context.Branch;
            var publishing = false;
            var authorityIdentity = StoppedAuthorityIdentity(sourceId);
            var store = new SharedWorldSeparateCopyStore(data);
            var published = store.ReadPublishedManifestHead(branch);
            var publishedHash = published?.VersionHash;
            var proof = backups.ReadStoppedSaveProof(profile);
            var stopped = RequireStoppedArchive(profile, proof, branch);
            if (!games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups || profile.Kind == GameKinds.Custom ||
                driver.ManagedSaveDirectory(profile) is not { } saveDirectory ||
                !SharedWorldBranchLiveSource.IsOwnedSaveSource(data, profile, saveDirectory))
                return Result(false, "StoppedProgressProofRequired", StoppedProgressProofMessage);

            void RequireCurrentSource()
            {
                cancellationToken.ThrowIfCancellationRequested();
                requireCurrentSelection?.Invoke();
                if (StoppedPublicationBusy(profile, reviewPublication: !publishing)) throw new InvalidDataException("The stopped world is busy.");
                var current = SharedWorldBranchLiveSource.Resolve(data, profile, clock.GetUtcNow(), requireStarted: true);
                if (!SharedWorldBranchLiveSource.SameSource(context, current) ||
                    !new WorldPeerReceiveConsentStore(data).Effective(context.Root, context.SourceDeviceId, legacyConsent: true) ||
                    StoppedAuthorityIdentity(sourceId) != authorityIdentity ||
                    store.ReadPublishedManifestHead(branch)?.VersionHash != publishedHash ||
                    RequireStoppedArchive(profile, proof, branch).OperationId != stopped.OperationId)
                    throw new InvalidDataException("The stopped source's current authorization changed.");
                backups.VerifyStoppedSaveLayoutAndSetup(profile, saveDirectory, proof, cancellationToken);
            }

            RequireCurrentSource();
            using var sourceLease = backups.PinStoppedSaveSource(profile, saveDirectory, proof, cancellationToken);
            var backup = proof.Backup;
            if (!backups.StoppedSavePayloadVerified(profile, proof, cancellationToken))
            {
                var recaptured = backups.RecaptureStoppedSave(profile, saveDirectory, proof, stopped.OperationId,
                    RequireCurrentSource, cancellationToken);
                if (!recaptured.Ok || recaptured.Backup is null)
                    return Result(false, recaptured.Code, recaptured.Message);
                backup = recaptured.Backup;
                // The new completion is fresh. Its direct original-stop anchor
                // allows safe retry if publication is interrupted afterward.
                proof = backups.ReadStoppedSaveProof(profile);
                if (proof.Backup.Id != backup.Id || proof.OriginalBackup.Id != backup.RecapturedFromBackupId ||
                    proof.Backup.StoppedOperationId != stopped.OperationId)
                    return Result(false, "StoppedProgressProofRequired", StoppedProgressProofMessage);
            }
            backups.VerifyStoppedSaveSource(profile, saveDirectory, proof, cancellationToken);
            RequireCurrentSource();
            cancellationToken.ThrowIfCancellationRequested();
            SharedWorldResult result;
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                RequireCurrentSource();
                // The normal publisher checks existing orphan records before
                // creating its own stage. Its callbacks must permit that one
                // in-flight stage while this root fence excludes other writers.
                publishing = true;
                result = PublishSeparateCopyAfterStopUnderGate(profile, backup.Id, cancellationToken, RequireCurrentSource);
            }
            if (!result.Ok || result.Version is null || result.Version.BackupId != backup.Id ||
                result.Version.CaptureKind != SharedWorldCaptureKinds.PostStopBackup)
                return Result(false, "StoppedProgressPublicationFailed", "The verified stopped backup remains local. Sharing was not confirmed; review and retry this explicit action.");
            NotifySharedSavePublished();
            return Result(true, published?.BackupId == backup.Id ? "StoppedProgressUnchanged" : "StoppedProgressShared",
                StoppedProgressSharedMessage);
        }
        catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
        {
            return Result(false, "StoppedProgressProofRequired", StoppedProgressProofMessage);
        }
        finally { gate.Release(); }
    }

    private bool StoppedPublicationBusy(ServerProfile profile, bool reviewPublication = true)
    {
        if (data.Recovery.LifecycleBlocked || worldCopyReservations.Count != 0 || backups.HasPendingRestore ||
            crashRecovery.Any(item => item.ProfileId == profile.Id) ||
            data.HasProtected(SetupRestorePendingName(profile.Id)) || data.HasProtected(PlannedHandoffName(profile.Id)) ||
            profile.SeparateCopySourceProfileId is { } source &&
                (data.HasProtected(SetupRestorePendingName(source)) || data.HasProtected(PlannedHandoffName(source))) ||
            data.HasProtected(SuccessorRestoreName(profile.Id)) ||
            profile.Kind == GameKinds.MinecraftBedrock && new BedrockLiveSaveCandidate(data).HasPendingResume ||
            ReadLiveSaveAttempts().Any(item => item.ProfileId == profile.Id && LiveAttemptPending(item)) ||
            AcceptanceStore.Read().Any(item => item.ProfileId == profile.Id && item.Pending && !item.Reviewed) ||
            reviewPublication && LivePublicationNeedsReviewUnderGate(profile)) return true;
        if (settings.Profiles.Any(other => other.Id != profile.Id && StoppedPathsOverlap(other.WorldDirectory, profile.WorldDirectory))) return true;
        // Even an exited/unknown record is unresolved here. Do not inspect,
        // archive, signal, or silently discard it during this repair action.
        return runs.Any(run => run.ProfileId == profile.Id || StoppedPathsOverlap(run.WorldDirectory, profile.WorldDirectory) ||
            settings.Profiles.Any(other => other.Id == run.ProfileId && other.SeparateCopySourceProfileId == profile.SeparateCopySourceProfileId));
    }

    private string StoppedAuthorityIdentity(Guid sourceId)
    {
        var records = authority.Read(sourceId);
        if (authority.GovernanceUnresolved(sourceId) || WorldAuthorityTrust.EffectiveHeads(records).Length > 1)
            throw new InvalidDataException("The stopped world's authority needs review.");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(records.Select(item => item.RecordHash).ToArray())));
    }

    private ManagedRunArchive RequireStoppedArchive(ServerProfile profile, StoppedSaveBackupProof proof,
        WorldSeparateCopyBranch branch)
    {
        var archive = data.LoadRunArchive();
        if (archive.Any(item => item is null)) throw new InvalidDataException("The stopped archive is invalid.");
        var records = archive.Where(item => item.ProfileId == profile.Id).ToArray();
        if (records.Length == 0 || records.Select(item => item.OperationId).Distinct().Count() != records.Length)
            throw new InvalidDataException("The stopped session is unavailable or ambiguous.");
        var latest = records.OrderBy(item => item.ArchivedUtc).Last();
        var summary = ToRecentServerSession(latest);
        if (records.Count(item => item.ArchivedUtc == latest.ArchivedUtc) != 1 || latest.OperationId == Guid.Empty ||
            latest.Kind != profile.Kind || latest.WorldId != profile.WorldId || latest.ProcessId is not > 0 ||
            latest.StartTimeUtcTicks is not > 0 || RunStartUtc(latest.StartTimeUtcTicks) != latest.StartedUtc ||
            summary.EndReason != ServerSessionEndReason.GracefulStop || summary.Outcome != ServerSessionOutcome.GracefulStop ||
            summary.BackupResult != ServerSessionBackupResult.Completed || summary.ReadyEverObserved != true ||
            summary.CrashRecoveryScheduled != false || latest.EndedUtc is not { } ended ||
            proof.OriginalBackup.CreatedUtc < ended || proof.OriginalBackup.CreatedUtc > latest.ArchivedUtc ||
            latest.ArchivedUtc > clock.GetUtcNow() || proof.OriginalBackup.CreatedUtc < branch.Offer.Version.CreatedUtc ||
            latest.CompletedBackupId is { } exactBackup && exactBackup != proof.OriginalBackup.Id ||
            proof.Backup.StoppedOperationId is { } operation && operation != latest.OperationId ||
            !StoppedProcessDefinitelyAbsent(latest) ||
            records.Any(item => item.OperationId != latest.OperationId &&
                (item.StartedUtc is null || item.EndedUtc is null ||
                 item.StartedUtc >= latest.EndedUtc || item.EndedUtc > latest.EndedUtc)))
            throw new InvalidDataException("The latest rolling backup is not corroborated by the latest completed graceful Stop.");
        // Legacy intervals corroborate local lifecycle history only. They are
        // never described as signed exit proof; whole-tree/setup equality and
        // the explicit owner's instruction remain mandatory.
        return latest;
    }

    private bool StoppedProcessDefinitelyAbsent(ManagedRunArchive archive)
    {
        if (StoppedSaveProcessAbsentForChecks is { } probe) return probe(archive);
        try
        {
            using var process = Process.GetProcessById(archive.ProcessId!.Value);
            // PID reuse proves this exact prior identity is gone. Never signal
            // the unrelated replacement; unavailable identity still refuses.
            return process.HasExited || process.StartTime.ToUniversalTime().Ticks != archive.StartTimeUtcTicks;
        }
        catch (ArgumentException) { return true; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or NotSupportedException)
        { return false; }
    }

    private static bool StoppedPathsOverlap(string left, string right)
    {
        var first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

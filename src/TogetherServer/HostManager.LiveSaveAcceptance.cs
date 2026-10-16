namespace TogetherServer;

public sealed partial class HostManager
{
    private LiveSaveAcceptanceStore? liveSaveAcceptanceStore;
    private LiveSaveAcceptanceStore AcceptanceStore => LazyInitializer.EnsureInitialized(
        ref liveSaveAcceptanceStore, () => new LiveSaveAcceptanceStore(data))!;

    private void RequireLiveAcceptanceRunProfile(ServerProfile profile, ManagedRun run)
    {
        if (!games.TryGet(profile.Kind, out var driver) ||
            !LiveSaveAcceptanceTrust.RunMatchesProfile(profile, run, driver.ManagedExecutablePath(profile), driver.Ports(profile)))
            throw new InvalidDataException("The reviewed executable, Java artifact, ports or world no longer match this exact run.");
    }

    private bool DistinctLiveReceiptDevice(SharedWorldReceipt? receipt) => receipt is not null &&
        // The owner root uses no device ID. A second local Join identity is
        // still this installation, not a separate-device test receipt.
        !data.HasProtected($"shared-world-pc-signing-{receipt.DeviceId:N}.protected");

    private string? LiveAcceptanceSourceBlock(ServerProfile profile)
    {
        if (data.Recovery.LifecycleBlocked || profile.WorldLoadRehearsalId is not null)
            return "LiveSaveTestAuthorityBlocked";
        if (LiveSaveAcceptanceTrust.IsRecoveryProfile(profile))
        {
            try { _ = ResolveBranchLiveSourceUnderGate(profile); return null; }
            catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { return "LiveSaveTestAuthorityBlocked"; }
        }
        if (!profile.SharedSavesEnabled ||
            data.HasProtected(PlannedHandoffName(profile.Id)) || pairing.SharedRosterDirty(profile.Id) ||
            SharedAuthorityBlocked(profile.Id, out _) || !sharedWorlds.LiveAcceptanceSourceMatches(profile))
            return "LiveSaveTestAuthorityBlocked";
        return null;
    }

    private LiveSaveSourceContext LiveAcceptanceSourceUnderGate(ServerProfile profile, bool requireStarted = false) =>
        LiveSaveAcceptanceTrust.IsRecoveryProfile(profile)
            ? ResolveBranchLiveSourceUnderGate(profile, requireStarted)
            : sharedWorlds.ResolveLiveSaveSource(profile, clock.GetUtcNow(), requireStarted);

    // Nomination only: no binary or mutable-world IO on each supervisor tick.
    // Every actual capture/status still performs full exact installed validation.
    internal static bool HasProtectedLiveAcceptanceCandidate(ServerProfile profile,
        IEnumerable<LiveSaveAcceptanceRecord> records) => records.Any(record =>
            record.ProfileId == profile.Id && record.Scope.ProfileId == profile.Id &&
            record.State == "Accepted" && record.AllStages && !record.Scope.Synthetic &&
            record.Artifact is { } artifact &&
            (LiveSaveAcceptanceTrust.IsRecoveryProfile(profile)
                ? artifact.Schema == 2 && artifact.Branch?.BranchHash == profile.SeparateCopyBranchHash &&
                    artifact.Version.ProfileId == profile.SeparateCopySourceProfileId
                : artifact.Schema == 1 && artifact.Branch is null));

    private bool InstalledLiveSaveAccepted(ServerProfile profile, CancellationToken token = default) =>
        InstalledLiveSaveAcceptanceRecord(profile, token) is not null;

    private LiveSaveAcceptanceRecord? InstalledLiveSaveAcceptanceRecord(ServerProfile profile, CancellationToken token = default)
    {
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                // Do not hash installed game binaries every supervisor tick
                // before an actual completed installation record exists.
                if (!HasProtectedLiveAcceptanceCandidate(profile, AcceptanceStore.Read()))
                    return null;
                if (LiveAcceptanceSourceBlock(profile) is not null) return null;
                var scope = LiveSaveInstallationScope(profile, token);
                return ProtectedLiveSaveAcceptanceRecord(profile, scope);
            }
        }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { return null; }
    }

    private LiveSaveAcceptanceRecord? ProtectedLiveSaveAcceptanceRecord(ServerProfile profile, LiveSaveAcceptanceScope scope)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            if (LiveAcceptanceSourceBlock(profile) is not null) return null;
            var source = LiveAcceptanceSourceUnderGate(profile);
            var roster = source.CurrentRoster;
            return AcceptanceStore.Read().LastOrDefault(record => record.ProfileId == profile.Id &&
                LiveSaveAcceptanceTrust.AcceptedForScope(record, scope, roster, clock.GetUtcNow()) &&
                DistinctLiveReceiptDevice(record.Receipt) &&
                record.Artifact is { } artifact && LiveSaveAcceptanceTrust.MatchesSource(artifact, source, profile, clock.GetUtcNow()) &&
                LiveSaveAcceptanceTrust.ReceiptMatches(artifact, record.Receipt, roster, clock.GetUtcNow()));
        }
    }

    // This wrapper is minted only after protected, exact installed acceptance.
    // The fixed adapter never interprets a caller-supplied command or path.
    private sealed class InstalledAcceptedAdapter(IManagedLiveSnapshotAdapter candidate,
        Func<ServerProfile, CancellationToken, bool> accepted, Action<ServerProfile, ManagedRun> exactProfile,
        LiveSaveAcceptanceRecord evidence) : IManagedLiveSnapshotAdapter
    {
        public string Game => candidate.Game;
        public bool LiveCaptureAccepted => true;
        internal LiveSaveAcceptanceRecord Evidence => evidence;
        internal IReadOnlyList<LiveSaveAcceptanceStage> Stages => LiveSaveAcceptanceTrust.Stages(evidence);
        public async Task<ImmutableLiveSaveSnapshot> CaptureAsync(ServerProfile profile, ManagedRun run,
            CancellationToken token, Guid? snapshotId = null)
        {
            exactProfile(profile, run);
            if (profile.Kind != Game || !accepted(profile, token)) throw new InvalidDataException();
            var snapshot = await candidate.CaptureAsync(profile, run, token, snapshotId);
            exactProfile(profile, run);
            if (!accepted(profile, token)) throw new InvalidDataException("The installed acceptance scope changed during capture.");
            return snapshot;
        }
    }

    private IManagedLiveSnapshotAdapter? AcceptedSnapshotAdapter(ServerProfile profile)
    {
        var evidence = InstalledLiveSaveAcceptanceRecord(profile);
        if (evidence is null) return null;
        var candidate = SharedWorldLiveSaveAdapters.CreateSnapshotAdapter(data, games, profile.Kind);
        if (candidate is null) return null;
        sharedWorlds.ConfigureManagedLiveSnapshots(games, item => InstalledLiveSaveAccepted(item));
        bool SameAcceptedInstallation(ServerProfile item, CancellationToken token)
        {
            var current = InstalledLiveSaveAcceptanceRecord(item, token);
            return current is not null && current.TestId == evidence.TestId &&
                SharedWorldLiveSaveAdapters.SameRealInstallation(evidence.Scope, current.Scope);
        }
        return new InstalledAcceptedAdapter(candidate, SameAcceptedInstallation, RequireLiveAcceptanceRunProfile, evidence);
    }

    public async Task<LiveSaveAcceptanceView> LiveSaveAcceptanceAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return AcceptanceUnavailable("UnknownProfile", "Choose a saved server.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            LiveSaveAcceptanceScope? scope = null;
            try { scope = LiveSaveInstallationScope(profile, deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { }
            var records = AcceptanceStore.Read().Where(record => record.ProfileId == profileId).ToArray();
            var acceptedRecord = scope is not null && !scope.Synthetic ? ProtectedLiveSaveAcceptanceRecord(profile, scope) : null;
            var matching = acceptedRecord ?? records.LastOrDefault(record => scope is not null &&
                SharedWorldLiveSaveAdapters.SameRealInstallation(record.Scope, scope));
            var accepted = acceptedRecord is not null;
            var code = scope is null ? "LiveSaveScopeUnavailable" : scope.Synthetic ? "SyntheticEvidenceOnly" :
                accepted ? "LiveSaveInstallationAccepted" : LiveAcceptanceSourceBlock(profile) ?? "GameAcceptanceRequired";
            return new(accepted, code, accepted ?
                "This exact installed binary and reviewed setup passed the private test. Game load, change and restart are owner-reported; all other installations remain unverified." :
                "Explicitly test a private sealed save, verify its copy on another authorized PC, then report actual load, change and restart for that exact test. Ordinary live capture stays disabled until all six stages pass.",
                scope, LiveSaveAcceptanceTrust.Stages(matching), records.Select(record =>
                    scope is null || !SharedWorldLiveSaveAdapters.SameRealInstallation(record.Scope, scope)
                        ? record.View() with { State = "Stale", Code = "LiveSaveTestScopeChanged" } :
                    record.Pending && !activeLiveSaves.ContainsKey(profileId)
                        ? (record with { State = "Interrupted", Code = "LiveSaveTestReviewRequired" }).View() :
                    record.State == "Accepted" && acceptedRecord is null
                        ? record.View() with { State = "Stale", Code = "LiveSaveTestAccessChanged" } : record.View()).ToArray());
        }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex))
        { return AcceptanceUnavailable("LiveSaveAcceptanceReviewRequired", "Private acceptance records need review. Keep all saved and partial copies."); }
        finally { gate.Release(); }
    }

    private static LiveSaveAcceptanceView AcceptanceUnavailable(string code, string message) =>
        new(false, code, message, null, LiveSaveAcceptanceTrust.Stages(null), []);

    public async Task<LiveSaveAcceptanceResult> RequestLiveSaveAcceptanceTestAsync(Guid profileId,
        LiveSaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request.RequestId == Guid.Empty) return new(false, "LiveSaveTestRequestInvalid", "A fresh test identity is required.");
        var stopGeneration = CaptureStopGeneration(profileId);
        await gate.WaitAsync(cancellationToken);
        CancellationTokenSource? deadline = null;
        List<LiveSaveAcceptanceRecord>? records = null;
        LiveSaveAcceptanceRecord? record = null;
        var snapshots = new ManagedLiveSnapshotStore(data, games);
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return new(false, "UnknownProfile", "Choose a saved server.");
            records = AcceptanceStore.Read();
            if (records.SingleOrDefault(item => item.TestId == request.RequestId) is { } existing)
            {
                if (existing.ProfileId != profileId) return new(false, "LiveSaveTestRequestMismatch", "This test belongs to another saved server.");
                if (existing.State is "ArtifactReady" or "Accepted")
                {
                    var currentScope = LiveSaveInstallationScope(profile, cancellationToken);
                    if (!SharedWorldLiveSaveAdapters.SameRealInstallation(existing.Scope, currentScope) || LiveAcceptanceSourceBlock(profile) is not null ||
                        existing.Artifact is not { } retainedArtifact ||
                        !sharedWorlds.AcceptanceTestSourceMatches(profile, retainedArtifact, clock.GetUtcNow()))
                        return new(false, "LiveSaveTestScopeChanged", "This retained test no longer matches the current installation or signed source. A retry never repeats its save.",
                            existing.View() with { State = "Stale", Code = "LiveSaveTestScopeChanged" });
                    if (existing.State == "Accepted" && (
                        !DistinctLiveReceiptDevice(existing.Receipt) ||
                        !LiveSaveAcceptanceTrust.AcceptedForScope(existing, currentScope,
                            LiveAcceptanceSourceUnderGate(profile).CurrentRoster, clock.GetUtcNow())))
                        return new(false, "LiveSaveTestAccessChanged", "This historical test needs a current authorized receipt and new owner confirmations. Its bytes were retained.",
                            existing.View() with { State = "Stale", Code = "LiveSaveTestAccessChanged" });
                }
                return new(existing.State is "ArtifactReady" or "Accepted", existing.Pending ? "LiveSaveTestReviewRequired" : existing.Code,
                    existing.Pending ? "An interrupted request needs explicit review; retry never sends another save." : "This exact test request already exists; no save was repeated.", existing.View());
            }
            if (SharedWorldLiveSaveAdapters.ForGame(profile.Kind) is null)
                return new(false, "LiveSaveCandidateUnavailable", "Only a reviewed real-game fixed candidate can enter this workflow. Fixtures never accept a real game.");
            var candidate = SharedWorldLiveSaveAdapters.CreateSnapshotAdapter(data, games, profile.Kind);
            if (candidate is null) return new(false, "LiveSaveCandidateUnavailable", "This game has no reviewed immutable snapshot candidate.");
            if (LiveAcceptanceSourceBlock(profile) is { } sourceBlock) return new(false, sourceBlock, "Resolve current signed source authority and sharing access before a private test.");
            if (profile.Maintenance.Enabled || WorldCopyBlock(profile, "a private live-save test") is not null ||
                LivePublicationNeedsReviewUnderGate(profile) ||
                ReadLiveSaveAttempts().Any(item => item.ProfileId == profileId && LiveAttemptPending(item)) ||
                new BedrockLiveSaveCandidate(data).HasPendingResume && profile.Kind == GameKinds.MinecraftBedrock)
                return new(false, "LiveSaveTestBlocked", "Resolve maintenance, copy work, interrupted publication or held-write resume first.");
            if (records.Any(item => item.ProfileId == profileId && !item.Reviewed && item.State is "Capturing" or "Interrupted" or "Failed"))
                return new(false, "LiveSaveTestReviewRequired", "Review the retained interrupted test before requesting another one.");
            if (records.Count >= LiveSaveAcceptanceStore.MaximumTests)
                return new(false, "LiveSaveTestRetentionLimit", "The bounded private test journal is full. Keep retained copies for owner review.");
            var run = runs.SingleOrDefault(item => item.ProfileId == profileId);
            if (run is null || run.StopRequestedUtc is not null || Identity(run) != "Matched")
                return new(false, "LiveSaveTestExactRunRequired", "The owner must deliberately run this exact managed installed game before the test.");
            RequireLiveAcceptanceRunProfile(profile, run);
            snapshots.RequireExactRun(profile, run);
            deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(LiveCaptureTimeout(profile.Kind));
            var captureToken = deadline.Token;
            var scope = LiveSaveInstallationScope(profile, captureToken);
            if (scope.Synthetic) return new(false, "SyntheticEvidenceOnly", "A fixture cannot accept any real installation.");
            var source = LiveAcceptanceSourceUnderGate(profile, requireStarted: true);
            record = new(request.RequestId, profileId, run.OperationId, scope, clock.GetUtcNow());
            records.Add(record); AcceptanceStore.Save(records);
            RegisterLiveCaptureCancellation(profileId, request.RequestId, deadline, stopGeneration);
            captureToken.ThrowIfCancellationRequested();
            sharedWorlds.ConfigureManagedLiveSnapshots(games, item => InstalledLiveSaveAccepted(item));
            void VerifyExactScope()
            {
                captureToken.ThrowIfCancellationRequested();
                RequireLiveAcceptanceRunProfile(profile, run);
                if (LiveAcceptanceSourceBlock(profile) is not null ||
                    !SharedWorldBranchLiveSource.SameSource(source, LiveAcceptanceSourceUnderGate(profile, requireStarted: true)) ||
                    !SharedWorldLiveSaveAdapters.SameRealInstallation(scope, LiveSaveInstallationScope(profile, captureToken)))
                    throw new InvalidDataException("The exact installed scope or current signed source changed.");
            }
            VerifyExactScope();
            // This is the only pre-acceptance path into a fixed save candidate.
            // It is reached solely by an explicit local owner mutation request.
            var snapshot = await candidate.CaptureAsync(profile, run, captureToken, request.RequestId);
            void VerifyExactScopeAndSnapshot()
            {
                captureToken.ThrowIfCancellationRequested();
                VerifyExactScope();
                if (!snapshots.Verify(profile, run, snapshot, captureToken))
                    throw new InvalidDataException("The exact run, installed scope or protected snapshot changed.");
            }
            VerifyExactScopeAndSnapshot();
            if (source.Branch is null)
                sharedWorlds.StageLiveCapture(profile, run, snapshot, captureToken, request.RequestId);
            var prepared = sharedWorlds.PrepareAcceptanceTestArtifact(profile, run, snapshot, scope, captureToken);
            AcceptanceStore.Stage(prepared.Artifact, prepared.SealedPayload, VerifyExactScopeAndSnapshot, captureToken);
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                VerifyExactScopeAndSnapshot();
                var complete = record with { Artifact = prepared.Artifact, State = "ArtifactReady", Code = "LiveSaveTestArtifactReady" };
                ReplaceAcceptance(records, complete);
                record = complete;
            }
            // The test owns a separately verified payload and protected journal.
            // Redundant private stages may be discarded only after that commit.
            try
            {
                if (source.Branch is null) sharedWorlds.DiscardLiveCapture(profileId, request.RequestId);
                snapshots.Discard(snapshot);
            }
            catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { }
            return new(true, record.Code, "A private test copy is ready. Transfer this exact TestOnly artifact to another authorized PC; it is never the group's current save or a recovery candidate.", record.View());
        }
        catch (OperationCanceledException)
        {
            record = KeepFailedTest(records, record, "LiveSaveTestInterrupted");
            return new(false, "LiveSaveTestInterrupted", "The private test was interrupted. Partial and sealed copies were retained for explicit review; this test granted no acceptance.", record?.View());
        }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex))
        {
            record = KeepFailedTest(records, record, "LiveSaveTestFailed");
            return new(false, "LiveSaveTestFailed", "Completion, sealed snapshot or exact-copy staging could not be verified. Retained private bytes need review; no production pointer changed.", record?.View());
        }
        finally
        {
            if (record is not null) activeLiveSaves.TryRemove(profileId, out _);
            deadline?.Dispose(); gate.Release();
        }
    }

    private void ReplaceAcceptance(List<LiveSaveAcceptanceRecord> records, LiveSaveAcceptanceRecord record)
    {
        var index = records.FindIndex(item => item.TestId == record.TestId);
        if (index < 0) throw new InvalidDataException();
        records[index] = record; AcceptanceStore.Save(records);
    }

    private LiveSaveAcceptanceRecord? KeepFailedTest(List<LiveSaveAcceptanceRecord>? records, LiveSaveAcceptanceRecord? record, string code)
    {
        if (records is null || record is null || record.Artifact is not null) return record;
        record = record with { State = code == "LiveSaveTestInterrupted" ? "Interrupted" : "Failed", Code = code };
        try { ReplaceAcceptance(records, record); }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { }
        return record;
    }

    private (ServerProfile Profile, LiveSaveAcceptanceRecord Record) RequireAcceptanceTest(Guid profileId, Guid testId,
        List<LiveSaveAcceptanceRecord> records, CancellationToken token, bool currentScope = true)
    {
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
        var record = records.SingleOrDefault(item => item.ProfileId == profileId && item.TestId == testId);
        if (profile is null || record is null || record.Artifact is not { } artifact || record.State is not ("ArtifactReady" or "Accepted") ||
            LiveAcceptanceSourceBlock(profile) is not null || !sharedWorlds.AcceptanceTestSourceMatches(profile, artifact, clock.GetUtcNow()) ||
            currentScope && !SharedWorldLiveSaveAdapters.SameRealInstallation(record.Scope, LiveSaveInstallationScope(profile, token)))
            throw new InvalidDataException("This exact private test no longer matches current source authority or installed setup.");
        return (profile, record);
    }

    public async Task<LiveSaveTestArtifact?> LiveSaveTestArtifactAsync(Guid profileId, Guid testId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var (_, record) = RequireAcceptanceTest(profileId, testId, AcceptanceStore.Read(), cancellationToken, currentScope: false);
                var artifact = AcceptanceStore.ReadArtifact(record.Artifact!, cancellationToken);
                _ = RequireAcceptanceTest(profileId, testId, AcceptanceStore.Read(), cancellationToken, currentScope: false);
                return artifact;
            }
        }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { return null; }
        finally { gate.Release(); }
    }

    public async Task<SharedWorldVersion?> LiveSaveTestManifestAsync(Guid profileId, Guid testId, CancellationToken cancellationToken = default) =>
        (await LiveSaveTestArtifactAsync(profileId, testId, cancellationToken))?.Version;

    public async Task<byte[]?> LiveSaveTestChunkAsync(Guid profileId, Guid testId, int index, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var (_, record) = RequireAcceptanceTest(profileId, testId, AcceptanceStore.Read(), cancellationToken, currentScope: false);
                var chunk = AcceptanceStore.ReadChunk(record.Artifact!, index, cancellationToken);
                _ = RequireAcceptanceTest(profileId, testId, AcceptanceStore.Read(), cancellationToken, currentScope: false);
                return chunk;
            }
        }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { return null; }
        finally { gate.Release(); }
    }

    public async Task<LiveSaveAcceptanceResult> RegisterLiveSaveTestReceiptAsync(Guid profileId, Guid testId,
        SharedWorldReceipt receipt, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var records = AcceptanceStore.Read();
                var (profile, record) = RequireAcceptanceTest(profileId, testId, records, cancellationToken);
                var roster = LiveAcceptanceSourceUnderGate(profile).CurrentRoster;
                if (!DistinctLiveReceiptDevice(receipt) ||
                    !LiveSaveAcceptanceTrust.ReceiptMatches(record.Artifact!, receipt, roster, clock.GetUtcNow()))
                    return new(false, "LiveSaveTestReceiptDenied", "A valid normal exact-copy receipt from a distinct currently authorized receiving device is required.");
                _ = AcceptanceStore.ReadArtifact(record.Artifact!, cancellationToken);
                _ = RequireAcceptanceTest(profileId, testId, records, cancellationToken);
                if (!LiveSaveAcceptanceTrust.ReceiptMatches(record.Artifact!, receipt, roster, clock.GetUtcNow()))
                    return new(false, "LiveSaveTestReceiptDenied", "The receiving device's access ended during exact-copy verification.");
                if (record.Receipt is { } prior)
                {
                    if (prior == receipt) return new(true, "LiveSaveTestAlreadyConfirmed", "This exact signed test receipt is already recorded.", record.View());
                    if (receipt.RosterEpoch == prior.RosterEpoch && receipt.RosterRevision == prior.RosterRevision &&
                        receipt.DeviceId != prior.DeviceId)
                        return new(true, "LiveSaveTestAdditionalCopyVerified", "Another distinct authorized receiver verified this exact test. Existing transfer evidence and owner reports were kept.", record.View());
                    // A new roster revision requires new human confirmations.
                    // A different receipt at the same revision is not new evidence.
                    if (receipt.RosterEpoch < prior.RosterEpoch || receipt.RosterEpoch == prior.RosterEpoch && receipt.RosterRevision <= prior.RosterRevision)
                        return new(false, "LiveSaveTestReceiptReplay", "This test already has a receipt at that roster revision.");
                }
                record = record with { Receipt = receipt, TransferUtc = clock.GetUtcNow(), LoadUtc = null, ChangeUtc = null,
                    RestartUtc = null, State = "ArtifactReady", Code = "LiveSaveTestTransferVerified" };
                ReplaceAcceptance(records, record);
                return new(true, record.Code, "The exact private copy has a separate-device signed receipt. Actual game load, recognizable change and restart still need explicit owner reports.", record.View());
            }
        }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex))
        { return new(false, "LiveSaveTestReceiptDenied", "The receipt, private bytes, current installation or sharing authority could not be verified."); }
        finally { gate.Release(); }
    }

    public async Task<LiveSaveAcceptanceResult> ConfirmLiveSaveAcceptanceAsync(Guid profileId, Guid testId,
        LiveSaveTestConfirmation confirmation, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var records = AcceptanceStore.Read();
                var (profile, record) = RequireAcceptanceTest(profileId, testId, records, cancellationToken);
                var roster = LiveAcceptanceSourceUnderGate(profile).CurrentRoster;
                if (!DistinctLiveReceiptDevice(record.Receipt) ||
                    !LiveSaveAcceptanceTrust.ReceiptMatches(record.Artifact!, record.Receipt, roster, clock.GetUtcNow()))
                    return new(false, "LiveSaveTestTransferRequired", "First obtain this exact test's signed receipt from another currently authorized device.");
                _ = AcceptanceStore.ReadArtifact(record.Artifact!, cancellationToken);
                _ = RequireAcceptanceTest(profileId, testId, records, cancellationToken);
                if (!LiveSaveAcceptanceTrust.ReceiptMatches(record.Artifact!, record.Receipt, roster, clock.GetUtcNow()))
                    return new(false, "LiveSaveTestTransferRequired", "The receiving device's access ended during private-copy verification.");
                record = LiveSaveAcceptanceTrust.Confirm(record, confirmation, clock.GetUtcNow());
                ReplaceAcceptance(records, record);
                return new(true, record.Code, record.AllStages ?
                    "All six stages are recorded for this exact installation. Load, change and restart remain explicitly OwnerReported. Binary, profile, world or setup changes invalidate this acceptance." :
                    "The explicit owner report was recorded for this exact private payload. Remaining stages stay unverified.", record.View());
            }
        }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex))
        { return new(false, "LiveSaveTestConfirmationDenied", "Confirm load, change and restart in order for the exact test and payload after its verified separate-device receipt."); }
        finally { gate.Release(); }
    }

    public async Task<LiveSaveAcceptanceResult> ReviewLiveSaveAcceptanceTestAsync(Guid profileId, Guid testId,
        CancellationToken cancellationToken = default)
    {
        CancelLiveSave(profileId, testId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var records = AcceptanceStore.Read();
            var record = records.SingleOrDefault(item => item.ProfileId == profileId && item.TestId == testId);
            if (record is null || record.Artifact is not null || record.State is not ("Capturing" or "Interrupted" or "Failed"))
                return new(false, "LiveSaveTestReviewDenied", "Choose an interrupted or failed private test. Completed evidence stays in history.");
            record = record with { Reviewed = true, State = "Interrupted", Code = "LiveSaveTestReviewed" };
            ReplaceAcceptance(records, record);
            return new(true, record.Code, "The interrupted test was acknowledged. All retained partial and sealed bytes remain private for review; no completion or acceptance was granted.", record.View());
        }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex))
        { return new(false, "LiveSaveAcceptanceReviewRequired", "Private test records could not be reviewed. Keep every copy."); }
        finally { gate.Release(); }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LiveSaveRequest(Guid RequestId);
public sealed record LiveSaveAttemptView(Guid RequestId, string State, string Code, string Message,
    string? VersionHash, long? VersionNumber);
public sealed record LiveSaveActionResult(bool Ok, string Code, string Message, LiveSaveAttemptView? Attempt = null);
internal sealed class LiveSaveAttempt
{
    public Guid RequestId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid OperationId { get; set; }
    public Guid? CaptureId { get; set; }
    public string State { get; set; } = "Requested";
    public string Code { get; set; } = "LiveSavePending";
    public string Message { get; set; } = "A fixed save request is pending. Game load is unverified.";
    public string? VersionHash { get; set; }
    public long? VersionNumber { get; set; }
    public LiveSaveAttemptView View() => new(RequestId, State, Code, Message, VersionHash, VersionNumber);
}

public sealed partial class HostManager
{
    private const string LiveSaveAttemptsName = "live-save-attempts.protected";
    private bool stagingLiveFixtureEnabled;
    private sealed record ActiveLiveSave(Guid RequestId, CancellationTokenSource Cancellation);
    private readonly ConcurrentDictionary<Guid, ActiveLiveSave> activeLiveSaves = new();
    internal void EnableStagingLiveFixture()
    {
        stagingLiveFixtureEnabled = true;
        sharedWorlds.EnableStagingLiveFixture(games);
    }
    internal Action? LiveAfterSourceScanForChecks { set => sharedWorlds.AfterLiveSourceScanForChecks = value; }
    internal Func<long>? LiveAvailableBytesForChecks { set => sharedWorlds.FixtureLiveAvailableBytesForChecks = value; }
    internal Action? LiveAfterSignedDirectoryForChecks { set => sharedWorlds.AfterLiveSignedDirectoryForChecks = value; }
    private List<LiveSaveAttempt> ReadLiveSaveAttempts()
    {
        var existed = data.HasProtected(LiveSaveAttemptsName);
        var bytes = data.LoadProtected(LiveSaveAttemptsName);
        if (bytes is null)
        {
            if (existed || data.Recovery.Notices.Any(item => item.StateFile == LiveSaveAttemptsName)) throw new InvalidDataException();
            return [];
        }
        if (bytes.Length > 64 * 1024) throw new InvalidDataException();
        var list = JsonSerializer.Deserialize<List<LiveSaveAttempt>>(bytes);
        if (list is null || list.Count > 64 || list.Any(item => item is null || item.RequestId == Guid.Empty ||
            item.ProfileId == Guid.Empty || item.OperationId == Guid.Empty || item.Message is not { Length: <= 500 } || item.Code is not { Length: <= 100 } ||
            item.State is not ("Requested" or "Capturing" or "Publishing" or "Published" or "Failed" or "Canceled" or "Withdrawn")) ||
            list.Select(item => item.RequestId).Distinct().Count() != list.Count) throw new InvalidDataException();
        return list;
    }
    private void SaveLiveSaveAttempts(List<LiveSaveAttempt> attempts) => data.SaveProtected(LiveSaveAttemptsName, JsonSerializer.SerializeToUtf8Bytes(attempts));
    private static bool LiveAttemptPending(LiveSaveAttempt attempt) => attempt.State is "Requested" or "Capturing" or "Publishing";
    private void CancelLiveSave(Guid profileId, Guid? requestId = null)
    {
        if (activeLiveSaves.TryGetValue(profileId, out var active) &&
            (requestId is null || active.RequestId == requestId))
            try { active.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }
    private IManagedLiveSnapshotAdapter? AcceptedSnapshotAdapter(string game)
    {
        if (!SharedWorldLiveSaveAdapters.IsGameAccepted(game)) return null;
        var adapter = SharedWorldLiveSaveAdapters.CreateSnapshotAdapter(data, games, game);
        return adapter?.LiveCaptureAccepted == true ? adapter : null;
    }
    private SharedWorldLiveSaveStatus LiveSaveStatusUnderGate(ServerProfile profile)
    {
        var status = SharedWorldLiveSaveAdapters.Status(profile.Kind);
        var pendingResume = new BedrockLiveSaveCandidate(data).HasPendingResume;
        if (profile.Kind == GameKinds.MinecraftBedrock && pendingResume)
            return status with
            {
                Code = "BedrockResumePending",
                ResumePending = true,
                Message = "A previous save hold has no verified resume acknowledgement. Retry exact-run resume or gracefully Stop and review; another capture is blocked."
            };
        var fixture = profile.Kind == GameKinds.Fixture && stagingLiveFixtureEnabled;
        if (!fixture && AcceptedSnapshotAdapter(profile.Kind) is null) return status;
        try
        {
            var attempts = ReadLiveSaveAttempts();
            if (ReconcilePublishedAttempts(profile, attempts)) SaveLiveSaveAttempts(attempts);
            if (!DiscardPublishedPrivateCopies(profile, attempts))
                return status with { Code = "LiveSaveReviewRequired", Message = "A published save's redundant private copy needs cleanup review. Its signed version and source were kept." };
            var attempt = attempts.LastOrDefault(item => item.ProfileId == profile.Id);
            var run = runs.SingleOrDefault(item => item.ProfileId == profile.Id);
            // Availability is derived again under the lifecycle gate on submission.
            var available = !data.Recovery.LifecycleBlocked && !data.HasProtected(PlannedHandoffName(profile.Id)) &&
                profile.SharedSavesEnabled && profile.WorldLoadRehearsalId is null && run is not null &&
                run.StopRequestedUtc is null && Identity(run) == "Matched" && (attempt is null || !LiveAttemptPending(attempt)) &&
                !SharedAuthorityBlocked(profile.Id, out _) && WorldCopyBlock(profile, "live capture") is null &&
                sharedWorlds.ReviewLiveOrphan(profile).Code == "None";
            return new(available, fixture ? "Staging fixture only. Synthetic completion, sealed immutable copy and signed publication do not prove a real game save or load." :
                "Save through the accepted game adapter and share its sealed copy with approved PCs.",
                profile.Kind, fixture ? available ? "StagingFixtureReady" : "StagingFixtureBlocked" :
                    available ? "LiveSaveReady" : "LiveSaveBlocked", status.Stages, LastAttempt: attempt?.View());
        }
        catch (Exception ex) when (LiveOrphanReviewFailure(ex))
        { return status with { Code = "LiveSaveReviewRequired", Message = "Live-save attempt records need owner review. Keep the copies." }; }
    }

    public async Task<LiveSaveActionResult> SaveAndShareAsync(Guid profileId, LiveSaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request.RequestId == Guid.Empty) return new(false, "LiveSaveRequestInvalid", "A fixed request identity is required.");
        await gate.WaitAsync(cancellationToken);
        CancellationTokenSource? deadline = null;
        List<LiveSaveAttempt>? attempts = null;
        LiveSaveAttempt? attempt = null;
        ManagedLiveSnapshotStore? snapshotStore = null;
        ImmutableLiveSaveSnapshot? snapshot = null;
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return new(false, "UnknownProfile", "Choose a saved server.");
            var fixture = profile.Kind == GameKinds.Fixture && stagingLiveFixtureEnabled;
            var snapshotAdapter = fixture ? null : AcceptedSnapshotAdapter(profile.Kind);
            if (!fixture && snapshotAdapter is null)
                return new(false, "GameAcceptanceRequired", SharedWorldLiveSaveAdapters.Status(profile.Kind).Message);
            if (data.Recovery.LifecycleBlocked || profile.SeparateCopySourceProfileId is not null ||
                SharedAuthorityBlocked(profileId, out _) || data.HasProtected(PlannedHandoffName(profileId)))
                return new(false, "LiveSaveAuthorityBlocked", "Resolve data recovery, handoff and signed hosting authority before sharing.");
            attempts = ReadLiveSaveAttempts();
            if (ReconcilePublishedAttempts(profile, attempts)) SaveLiveSaveAttempts(attempts);
            if (!DiscardPublishedPrivateCopies(profile, attempts))
                return new(false, "LiveSaveReviewRequired", "A published save's redundant private copy needs cleanup review. Keep the signed version and source.");
            var duplicate = attempts.SingleOrDefault(item => item.RequestId == request.RequestId);
            if (duplicate is not null)
            {
                if (duplicate.ProfileId == profileId && RecoverPublishedAttempt(profile, duplicate)) SaveLiveSaveAttempts(attempts);
                return duplicate.ProfileId != profileId ? new(false, "LiveSaveRequestMismatch", "This request belongs to another server.") :
                    new(duplicate.State == "Published", LiveAttemptPending(duplicate) ? "LiveSaveReviewRequired" : duplicate.Code,
                        LiveAttemptPending(duplicate) ? "An interrupted attempt needs explicit withdrawal and signed-copy review before retry." : duplicate.Message, duplicate.View());
            }
            if (attempts.Any(item => item.ProfileId == profileId && LiveAttemptPending(item)))
                return new(false, "LiveSaveReviewRequired", "Withdraw and review the interrupted attempt before another capture.");
            if (!LiveSaveStatusUnderGate(profile).Available) return new(false, "LiveSaveBlocked", "Enable shared saves and resolve the exact running server and any copy work first.");
            var run = runs.Single(item => item.ProfileId == profileId);
            if (attempts.Count == 64)
            {
                var removable = attempts.FirstOrDefault(item => !LiveAttemptPending(item));
                if (removable is null) return new(false, "LiveSaveRetentionLimit", "Review pending attempts before another capture.");
                attempts.Remove(removable);
            }
            attempt = new() { RequestId = request.RequestId, ProfileId = profileId, OperationId = run.OperationId };
            attempts.Add(attempt); SaveLiveSaveAttempts(attempts);
            deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            activeLiveSaves[profileId] = new(request.RequestId, deadline);
            LiveSaveCompletionEvidence completion;
            if (fixture)
            {
                IManagedLiveSaveAdapter adapter = new FixtureLiveSaveAdapter();
                completion = await adapter.CompleteAsync(run, deadline.Token);
            }
            else
            {
                snapshotStore = new ManagedLiveSnapshotStore(data, games);
                snapshot = await snapshotAdapter!.CaptureAsync(profile, run, deadline.Token, request.RequestId);
                completion = snapshot.Completion;
                sharedWorlds.ConfigureManagedLiveSnapshots(games);
            }
            if (Identity(run) != "Matched") throw new InvalidDataException();
            attempt.State = "Capturing"; attempt.CaptureId = Guid.NewGuid(); SaveLiveSaveAttempts(attempts);
            if (snapshot is null)
                sharedWorlds.StageLiveCapture(profile, run, completion, deadline.Token, attempt.CaptureId.Value);
            else
                sharedWorlds.StageLiveCapture(profile, run, snapshot, deadline.Token, attempt.CaptureId.Value);
            attempt.State = "Publishing"; SaveLiveSaveAttempts(attempts);
            var published = sharedWorlds.PublishLiveCapture(profile, attempt.CaptureId.Value,
                new(attempt.CaptureId.Value, run.OperationId, true), deadline.Token);
            if (!published.Ok || published.Version is null)
            {
                if (!RecoverPublishedAttempt(profile, attempt)) throw new InvalidDataException();
                SaveLiveSaveAttempts(attempts);
                return new(true, attempt.Code, attempt.Message, attempt.View());
            }
            attempt.State = "Published"; attempt.Code = "LiveSavePublished";
            attempt.Message = fixture ? "Synthetic running save published through signed lineage. Receive and exact-copy receipt use the normal grants. Real game load remains unverified." :
                "The accepted adapter's sealed running save is current. Receive and exact-copy receipt use the normal grants.";
            attempt.VersionHash = published.Version.VersionHash; attempt.VersionNumber = published.Version.Number;
            SaveLiveSaveAttempts(attempts);
            return new(true, attempt.Code, attempt.Message, attempt.View());
        }
        catch (OperationCanceledException)
        {
            if (attempt is not null && attempts is not null) TryRecordLiveFailure(attempts, attempt, "Canceled", "LiveSaveCanceled");
            if (attempt?.State == "Published") return new(true, attempt.Code, attempt.Message, attempt.View());
            return new(false, "LiveSaveCanceled", "Capture canceled. No partial copy was made current; keep any interrupted signed copy for review.", attempt?.View());
        }
        catch (Exception ex) when (LiveOrphanReviewFailure(ex))
        {
            if (attempt is not null && attempts is not null) TryRecordLiveFailure(attempts, attempt, "Failed", "LiveSaveFailed");
            if (attempt?.State == "Published") return new(true, attempt.Code, attempt.Message, attempt.View());
            return new(false, "LiveSaveFailed", "Save completion or immutable publication could not be certified. Review the current and interrupted signed copies before retry.", attempt?.View());
        }
        finally
        {
            activeLiveSaves.TryRemove(profileId, out _);
            deadline?.Dispose();
            try
            {
                if (snapshotStore is not null && snapshot is not null) snapshotStore.Discard(snapshot);
            }
            catch (Exception ex) when (LiveOrphanReviewFailure(ex))
            {
                Activity("Backup", "LiveSnapshotCleanupPending", "An adapter's sealed private copy needs cleanup review. Its source and signed versions were kept.",
                    ActivitySeverity.Warning, profileId);
            }
            finally { gate.Release(); }
        }
    }
    private void RecordLiveFailure(List<LiveSaveAttempt> attempts, LiveSaveAttempt attempt, string state, string code)
    {
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == attempt.ProfileId);
        if (profile is not null && RecoverPublishedAttempt(profile, attempt)) { SaveLiveSaveAttempts(attempts); return; }
        attempt.State = state; attempt.Code = code; attempt.Message = "Attempt did not complete. Game load is unverified; keep interrupted signed copies for review.";
        if (attempt.CaptureId is { } capture) sharedWorlds.DiscardLiveCapture(attempt.ProfileId, capture);
        SaveLiveSaveAttempts(attempts);
    }
    private void TryRecordLiveFailure(List<LiveSaveAttempt> attempts, LiveSaveAttempt attempt, string state, string code)
    {
        try { RecordLiveFailure(attempts, attempt, state, code); }
        catch (Exception ex) when (LiveOrphanReviewFailure(ex))
        { attempt.Code = "LiveSaveJournalReviewRequired"; attempt.Message = "The current copy or attempt journal needs review. Keep source and signed copies; retry this same request before another capture."; }
    }
    private bool RecoverPublishedAttempt(ServerProfile profile, LiveSaveAttempt attempt)
    {
        if (attempt.State is not ("Publishing" or "Published") || attempt.CaptureId is null) return false;
        var current = sharedWorlds.Status(profile).Latest;
        if (current?.CaptureKind != SharedWorldCaptureKinds.LiveSave || current.BackupId != attempt.CaptureId) return false;
        attempt.State = "Published"; attempt.Code = "LiveSavePublished";
        attempt.Message = "The exact signed live copy is current. This retry did not publish another version; real game load remains unverified.";
        attempt.VersionHash = current.VersionHash; attempt.VersionNumber = current.Number;
        return true;
    }
    private bool ReconcilePublishedAttempts(ServerProfile profile, List<LiveSaveAttempt> attempts)
    {
        var changed = false;
        foreach (var attempt in attempts.Where(item => item.ProfileId == profile.Id && item.State == "Publishing"))
            changed |= RecoverPublishedAttempt(profile, attempt);
        return changed;
    }
    private bool DiscardPublishedPrivateCopies(ServerProfile profile, IEnumerable<LiveSaveAttempt> attempts)
    {
        try
        {
            var snapshots = new ManagedLiveSnapshotStore(data, games);
            foreach (var attempt in attempts.Where(item => item.ProfileId == profile.Id && item.State == "Published"))
                snapshots.DiscardForAttempt(profile.Id, attempt.OperationId, attempt.RequestId);
            return true;
        }
        catch (Exception ex) when (LiveOrphanReviewFailure(ex)) { return false; }
    }
    public async Task<LiveSaveActionResult> WithdrawLiveSaveAsync(Guid profileId, LiveSaveRequest request)
    {
        if (request.RequestId == Guid.Empty)
            return new(false, "LiveSaveRequestInvalid", "A fixed request identity is required.");
        CancelLiveSave(profileId, request.RequestId);
        await gate.WaitAsync();
        try
        {
            var list = ReadLiveSaveAttempts(); var attempt = list.SingleOrDefault(item => item.RequestId == request.RequestId && item.ProfileId == profileId);
            if (attempt is null || list.LastOrDefault(item => item.ProfileId == profileId)?.RequestId != request.RequestId)
                return new(false, "LiveSaveWithdrawalDenied", "Choose this server's current pending or failed save request.");
            if (settings.Profiles.SingleOrDefault(item => item.Id == profileId) is { } profile &&
                RecoverPublishedAttempt(profile, attempt))
            {
                SaveLiveSaveAttempts(list);
                if (!DiscardPublishedPrivateCopies(profile, list))
                    return new(false, "LiveSaveReviewRequired", "The current signed save was recovered, but its redundant private copy needs cleanup review.", attempt.View());
                return new(false, "LiveSaveWithdrawalDenied", "The exact signed copy is already current. Its completed attempt was recovered without publishing another version.", attempt.View());
            }
            if (attempt.State == "Published")
                return new(false, "LiveSaveWithdrawalDenied", "A published copy stays in signed history. Choose a pending or failed attempt.", attempt.View());
            RecordLiveFailure(list, attempt, "Withdrawn", "LiveSaveWithdrawn");
            var snapshots = new ManagedLiveSnapshotStore(data, games);
            snapshots.DiscardForAttempt(profileId, attempt.OperationId, attempt.RequestId);
            snapshots.DiscardInterrupted(profileId);
            return new(true, attempt.Code, "Private capture withdrawn. Signed versions and source files were kept; use interrupted-copy review if needed.", attempt.View());
        }
        catch (Exception ex) when (LiveOrphanReviewFailure(ex))
        { return new(false, "LiveSaveReviewRequired", "The exact attempt could not be verified. Keep its files for review."); }
        finally { gate.Release(); }
    }

    public async Task<LiveSaveActionResult> RecoverPendingBedrockResumeAsync(Guid? profileId = null)
    {
        await gate.WaitAsync();
        try
        {
            var candidate = new BedrockLiveSaveCandidate(data);
            var pending = candidate.ReadPending();
            if (pending is null) return new(true, "BedrockResumeNotNeeded", "No pending save resume exists.");
            if (profileId is not null && profileId != pending.ProfileId)
                return new(false, "BedrockResumeProfileMismatch", "This pending resume belongs to another server.");
            var run = runs.SingleOrDefault(item => item.ProfileId == pending.ProfileId && item.OperationId == pending.OperationId);
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == pending.ProfileId);
            if (run is null || profile?.Kind != GameKinds.MinecraftBedrock || Identity(run) != "Matched" || run.StopRequestedUtc is not null)
                return new(false, "BedrockResumeReviewRequired", "The exact held process is unresolved or stopped. Keep its resume marker and review the save state.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var requestedUtc = DateTimeOffset.UtcNow;
            var acknowledged = await new BedrockManagedSnapshotAdapter(data, games).ResumeAsync(profile, run, timeout.Token);
            if (acknowledged)
            {
                Activity("Backup", "BedrockResumeConfirmed", "The exact held run acknowledged resumed writes; its pending marker was cleared.",
                    ActivitySeverity.Important, run.ProfileId);
                return new(true, "BedrockResumeConfirmed", "The exact held run acknowledged resumed writes. Game load and live-save acceptance remain separate checks.");
            }
            var dispatched = candidate.ReadPending();
            if (dispatched is not { ResumeDispatched: true, ResumeRequestedUtc: { } sentUtc } ||
                sentUtc < requestedUtc || dispatched.AttemptNonce != pending.AttemptNonce ||
                dispatched.ProfileId != pending.ProfileId || dispatched.OperationId != pending.OperationId ||
                dispatched.ProcessId != pending.ProcessId || dispatched.StartTimeUtcTicks != pending.StartTimeUtcTicks)
                return new(false, "BedrockResumeReviewRequired", "Exact-run save resume could not be confirmed as sent. Keep the pending marker and review the held process; another capture remains blocked.");
            Activity("Backup", "BedrockResumePending", "Exact-run save resume was sent after an interrupted hold. Completion is unverified; another live capture stays blocked.",
                ActivitySeverity.Important, run.ProfileId);
            return new(true, "BedrockResumeDispatched", "Exact-run save resume was sent. Its durable marker stays until a verified acknowledgement; another capture remains blocked.");
        }
        catch (Exception ex) when (LiveOrphanReviewFailure(ex))
        { return new(false, "BedrockResumeReviewRequired", "The pending hold or exact process could not be verified. Keep the marker and review the save state."); }
        finally { gate.Release(); }
    }
}

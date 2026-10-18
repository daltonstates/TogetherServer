using System.Text.Json;

namespace TogetherServer;

// These are projections of completed verification, never caller-supplied restore authority.
public sealed record SharedWorldFreshnessCopy(SharedWorldVersion Version, bool Complete,
    bool Verified, DateTimeOffset? VerifiedUtc = null);

public sealed record SharedWorldFreshnessStatus(Guid ProfileId, bool Enabled, string Code,
    string Message, SharedWorldFreshnessCopy? NewestCompleteVerified,
    double? CopyAgeSeconds, DateTimeOffset? LastAttemptUtc, DateTimeOffset? LastSafeCaptureUtc,
    DateTimeOffset? LastFailureUtc, string? LastFailureCode, DateTimeOffset? NextAttemptUtc,
    bool CaptureInProgress, int ConsecutiveFailures, SharedWorldLiveSaveStatus LiveSave);
public sealed record SharedWorldFreshnessSelection(SharedWorldFreshnessCopy? Selected,
    IReadOnlyList<SharedWorldFreshnessCopy> CompetingHeads);

public static class SharedWorldFreshness
{
    // Age is deliberately absent from eligibility. All supplied copies are kept;
    // selecting a copy neither discards another branch nor grants hosting authority.
    public static SharedWorldFreshnessCopy? SelectNewestCompleteVerified(
        IEnumerable<SharedWorldFreshnessCopy> copies, Guid profileId, Guid groupId,
        string? selectedHeadHash = null) => Select(copies, profileId, groupId, selectedHeadHash).Selected;

    public static SharedWorldFreshnessSelection Select(IEnumerable<SharedWorldFreshnessCopy> copies,
        Guid profileId, Guid groupId, string? selectedHeadHash = null)
    {
        // Test-only manifests fail the production signature verifier. Neither
        // a clock nor a larger branch-local number establishes ancestry.
        var signed = copies.Where(copy => copy.Version.ProfileId == profileId &&
                copy.Version.GroupId == groupId && SharedWorldService.VerifySignature(copy.Version))
            .GroupBy(copy => copy.Version.VersionHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.FirstOrDefault(copy => copy.Complete && copy.Verified) ?? group.First(), StringComparer.Ordinal);
        var eligible = signed.Values.Where(copy => copy.Complete && copy.Verified).ToArray();
        SharedWorldFreshnessCopy? Parent(SharedWorldFreshnessCopy child) =>
            child.Version.ParentHash is { } parentHash && signed.TryGetValue(parentHash, out var parent) &&
            parent.Version.Number < long.MaxValue && child.Version.Number == parent.Version.Number + 1 &&
            child.Version.Game == parent.Version.Game && child.Version.WorldId == parent.Version.WorldId ? parent : null;
        bool Descends(SharedWorldFreshnessCopy child, SharedWorldFreshnessCopy ancestor)
        {
            for (SharedWorldFreshnessCopy? cursor = child; cursor is not null; cursor = Parent(cursor))
                if (cursor.Version.VersionHash == ancestor.Version.VersionHash) return true;
            return false;
        }
        var heads = eligible.Where(candidate => !eligible.Any(other => other != candidate && Descends(other, candidate)))
            .OrderBy(copy => copy.Version.VersionHash, StringComparer.Ordinal).ToArray();
        if (selectedHeadHash is not null)
        {
            if (!signed.TryGetValue(selectedHeadHash, out var chosen)) return new(null, heads);
            for (SharedWorldFreshnessCopy? cursor = chosen; cursor is not null; cursor = Parent(cursor))
                if (cursor.Complete && cursor.Verified) return new(cursor, heads.Length > 1 ? heads : []);
            return new(null, heads.Length > 1 ? heads : []);
        }
        return heads.Length == 1 ? new(heads[0], []) : new(null, heads);
    }

    internal static bool SameCapture(IReadOnlyList<SharedWorldFile> left,
        SharedWorldPortableSetup leftSetup, IReadOnlyList<SharedWorldFile> right,
        SharedWorldPortableSetup rightSetup)
    {
        if (left.Count == 0 || left.Count != right.Count ||
            left.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != left.Count ||
            right.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != right.Count)
            return false;
        // Both inputs come from verified captures. Include every reviewed setup
        // field, so unchanged world bytes cannot suppress a changed setup version.
        return left.OrderBy(file => file.Path, StringComparer.Ordinal)
                   .SequenceEqual(right.OrderBy(file => file.Path, StringComparer.Ordinal)) &&
               JsonSerializer.Serialize(leftSetup) == JsonSerializer.Serialize(rightSetup);
    }

    internal static DateTimeOffset? CapturedUtc(SharedWorldFreshnessCopy? copy) =>
        copy?.Version.CreatedUtc is { } saved && saved != default ? saved : null;

    internal static double? CopyAgeSeconds(SharedWorldFreshnessCopy? copy, DateTimeOffset now) =>
        CapturedUtc(copy) is { } saved && saved <= now
            ? (now - saved).TotalSeconds : null;
}

internal sealed record SharedFreshnessTarget(Guid ProfileId, bool Eligible, string Code,
    DateTimeOffset? PreviousAttemptUtc = null, bool PreviousAttemptFailed = false);

internal sealed record SharedFreshnessScheduleState(string Code = "SharedFreshnessPending",
    DateTimeOffset? LastAttemptUtc = null, DateTimeOffset? LastSafeCaptureUtc = null,
    DateTimeOffset? LastFailureUtc = null, string? LastFailureCode = null,
    DateTimeOffset? NextAttemptUtc = null, bool InProgress = false, int ConsecutiveFailures = 0);

// One bounded pass is driven by the existing supervisor. There is no timer loop,
// game lifecycle operation, or task per profile. The real capture uses HostManager's gate.
internal sealed class SharedWorldFreshnessScheduler(TimeProvider clock,
    Func<CancellationToken, Task<IReadOnlyList<SharedFreshnessTarget>>> targets,
    Func<Guid, CancellationToken, Task<LiveSaveActionResult>> capture)
{
    internal static readonly TimeSpan CaptureInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);
    private readonly object sync = new();
    private readonly Dictionary<Guid, SharedFreshnessScheduleState> states = [];
    private Task? flight;
    private SharedFreshnessScheduleState? supervisorFailure;

    internal Task Schedule(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (flight is { IsCompleted: false }) return flight;
            if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
            if (supervisorFailure?.NextAttemptUtc is { } retry && retry > clock.GetUtcNow())
                return Task.CompletedTask;
            // Even a synchronously completing target reader cannot execute IO on
            // the observation supervisor's thread before this method returns.
            return flight = Task.Run(() => RunPassAsync(cancellationToken), CancellationToken.None);
        }
    }

    internal SharedFreshnessScheduleState State(Guid profileId)
    {
        lock (sync) return supervisorFailure ?? states.GetValueOrDefault(profileId) ?? new();
    }

    internal static TimeSpan FailureDelay(int failures) => TimeSpan.FromSeconds(
        Math.Min(MaximumBackoff.TotalSeconds, CaptureInterval.TotalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 10))));

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        Guid? activeProfile = null;
        try
        {
            var candidates = await targets(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SharedFreshnessTarget? selected;
            lock (sync)
            {
                supervisorFailure = null;
                var existing = candidates.Select(item => item.ProfileId).ToHashSet();
                foreach (var removed in states.Keys.Where(id => !existing.Contains(id)).ToArray()) states.Remove(removed);
                foreach (var candidate in candidates)
                {
                    if (!states.TryGetValue(candidate.ProfileId, out var state))
                    {
                        // The durable canonical journal seeds a minimum interval
                        // after app restart; legacy unknown dates stay unknown.
                        state = new(LastAttemptUtc: candidate.PreviousAttemptUtc,
                            NextAttemptUtc: candidate.PreviousAttemptUtc +
                                (candidate.PreviousAttemptFailed ? MaximumBackoff : CaptureInterval),
                            ConsecutiveFailures: candidate.PreviousAttemptFailed ? 1 : 0);
                    }
                    else if (candidate.PreviousAttemptUtc is { } recent &&
                        (state.LastAttemptUtc is null || recent > state.LastAttemptUtc))
                    {
                        // An explicit owner capture also buys a full interval.
                        // Do not force another save just after a manual success.
                        var next = recent + (candidate.PreviousAttemptFailed
                            ? FailureDelay(Math.Max(1, state.ConsecutiveFailures)) : CaptureInterval);
                        state = state with
                        {
                            NextAttemptUtc = candidate.PreviousAttemptFailed && state.NextAttemptUtc > next ? state.NextAttemptUtc : next,
                            ConsecutiveFailures = candidate.PreviousAttemptFailed ? Math.Max(1, state.ConsecutiveFailures) : 0
                        };
                    }
                    states[candidate.ProfileId] = state with
                    { Code = candidate.Eligible ? state.Code : candidate.Code };
                }
                var now = clock.GetUtcNow();
                selected = candidates.Where(item => item.Eligible &&
                        (states[item.ProfileId].NextAttemptUtc is null || states[item.ProfileId].NextAttemptUtc <= now))
                    .OrderBy(item => states[item.ProfileId].NextAttemptUtc)
                    .ThenBy(item => item.ProfileId).FirstOrDefault();
                if (selected is null) return;
                activeProfile = selected.ProfileId;
                states[selected.ProfileId] = states[selected.ProfileId] with
                { Code = "SharedFreshnessCapturing", LastAttemptUtc = now, InProgress = true };
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await capture(selected.ProfileId, cancellationToken);
            lock (sync)
            {
                var state = states[selected.ProfileId];
                var now = clock.GetUtcNow();
                var failures = result.Ok ? 0 : Math.Min(30, state.ConsecutiveFailures + 1);
                states[selected.ProfileId] = state with
                {
                    Code = result.Code,
                    InProgress = false,
                    ConsecutiveFailures = failures,
                    LastSafeCaptureUtc = result.Ok ? now : state.LastSafeCaptureUtc,
                    LastFailureUtc = result.Ok ? state.LastFailureUtc : now,
                    LastFailureCode = result.Ok ? state.LastFailureCode : result.Code,
                    NextAttemptUtc = now + (result.Ok ? CaptureInterval : FailureDelay(failures))
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (activeProfile is { } id)
                lock (sync)
                    states[id] = states[id] with
                    { Code = "SharedFreshnessCanceled", InProgress = false, NextAttemptUtc = clock.GetUtcNow() + CaptureInterval };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fire-and-forget supervisor integration must observe failures without
            // leaking paths or faulting the existing lifecycle observation loop.
            lock (sync)
            {
                var now = clock.GetUtcNow();
                if (activeProfile is { } id)
                {
                    var state = states[id];
                    var failures = Math.Min(30, state.ConsecutiveFailures + 1);
                    states[id] = state with
                    {
                        Code = "SharedFreshnessFailed",
                        InProgress = false,
                        LastFailureUtc = now,
                        LastFailureCode = "SharedFreshnessFailed",
                        ConsecutiveFailures = failures,
                        NextAttemptUtc = now + FailureDelay(failures)
                    };
                }
                else
                {
                    var failures = Math.Min(30, (supervisorFailure?.ConsecutiveFailures ?? 0) + 1);
                    supervisorFailure = new("SharedFreshnessReviewRequired", LastFailureUtc: now,
                        LastFailureCode: "SharedFreshnessReviewRequired", NextAttemptUtc: now + FailureDelay(failures),
                        ConsecutiveFailures: failures);
                }
            }
        }
    }
}

internal sealed partial class SharedWorldService
{
    // Read only the canonical signed lineage. Incomplete current bytes can fall
    // back to an older complete copy; an unpublished directory is never promoted.
    internal SharedWorldFreshnessCopy? NewestCompleteVerified(ServerProfile profile,
        DateTimeOffset verifiedUtc, CancellationToken cancellationToken)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                if (LiveSaveAcceptanceTrust.IsRecoveryProfile(profile))
                    return NewestCompleteVerifiedBranch(profile, verifiedUtc, cancellationToken);
                var latest = Status(profile).Latest;
                if (latest is null) return null;
                var candidate = latest;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var payload = Path.Combine(VersionRoot(candidate), PayloadDirectory);
                        EnsureUnlinkedRoot(data.RootPath, payload);
                        VerifyLiveTree(payload, candidate.Files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
                        return new(candidate, true, true, verifiedUtc);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
                    if (candidate.Number <= 1) return null;
                    var prior = ReadPublishedAncestor(latest, candidate.Number - 1);
                    if (prior is null || prior.VersionHash != candidate.ParentHash) return null;
                    candidate = prior;
                }
            }
    }

    // Follow only the chosen branch's exact parent hashes, including its signed
    // source branches. Neither another branch's current pointer nor its number
    // or date can displace this continuation's newest verified complete copy.
    private SharedWorldFreshnessCopy? NewestCompleteVerifiedBranch(ServerProfile profile,
        DateTimeOffset verifiedUtc, CancellationToken token)
    {
        var source = SharedWorldBranchLiveSource.Resolve(data, profile, verifiedUtc);
        var branch = source.Branch!;
        var store = new SharedWorldSeparateCopyStore(data);
        var candidate = store.ReadPublishedManifestHead(branch) ?? branch.Offer.Version;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var roots = new[] { store.PublishedRoot(branch), store.RetainedRoot(branch) };
            foreach (var root in roots)
            {
                try
                {
                    var payload = Path.Combine(root, candidate.VersionHash, PayloadDirectory);
                    EnsureUnlinkedRoot(data.RootPath, payload);
                    VerifyLiveTree(payload, candidate.Files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(), token);
                    return new(candidate, true, true, verifiedUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            if (candidate.VersionHash == branch.Offer.Version.VersionHash)
            {
                if (branch.SourceBranch is not { } parentBranch) return null;
                branch = parentBranch;
                try { _ = store.ReadPublishedManifest(branch, candidate.Number, candidate.VersionHash, verifyPayload: false); }
                catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { return null; }
                // The same signed source may still have its complete payload in
                // its own lane even when this recovery's retained copy is bad.
                continue;
            }
            if (candidate.ParentHash is not { } parent || candidate.Number <= branch.Offer.Version.Number) return null;
            try { candidate = store.ReadPublishedManifest(branch, candidate.Number - 1, parent, verifyPayload: false); }
            catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { return null; }
        }
    }

    internal SharedWorldVersion? ReuseUnchangedLiveCapture(ServerProfile profile, Guid captureId,
        CancellationToken cancellationToken)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                if (!LiveCaptureAccepted(profile))
                    throw new InvalidDataException("This game's live capture is not accepted.");
                var (captured, setup, _) = ReadVerifiedLiveCapture(profile, captureId, cancellationToken);
                var current = Status(profile).Latest;
                if (current is null || !SharedWorldFreshness.SameCapture(captured.Files,
                        SharedWorldPortableSetupReader.Capture(setup), current.Files, current.PortableSetup)) return null;
                if (Authority.GovernanceUnresolved(profile.Id) ||
                    Authority.HasState(profile.Id) && Authority.LocalAuthorizedHead(profile.Id) is null ||
                    !AuthorizedPublishedLineage(profile) || ReviewLiveOrphan(profile).Code != "None")
                    throw new InvalidDataException("The signed live-save lineage needs review.");
                var payload = Path.Combine(VersionRoot(current), PayloadDirectory);
                EnsureUnlinkedRoot(data.RootPath, payload);
                VerifyLiveTree(payload, current.Files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!RecordedExactLiveRun(profile, captured.OperationId, captured.ProcessId,
                        captured.StartTimeUtcTicks, requireReady: profile.Kind == GameKinds.Fixture))
                    throw new InvalidDataException("The exact live-save process changed.");
                if (!LiveCaptureAccepted(profile))
                    throw new InvalidDataException("The protected installed acceptance scope changed during verified-copy comparison.");
                cancellationToken.ThrowIfCancellationRequested();
                return current;
            }
    }
}

using System.Collections.Concurrent;

namespace TogetherServer;

// Original-Host downloads and peer forwarding have different transports, but
// the local hosting action must observe both for the exact pinned world.
internal static class SharedSaveTransferProgress
{
    private sealed record Outcome(bool Failed, string Code, string Message, SharedSaveTransferFailure? Failure = null);
    private sealed class Copy
    {
        internal ReceivedSharedWorldStatus? Status;
        internal DateTimeOffset UpdatedUtc = DateTimeOffset.UtcNow;
        internal int Active;
        internal bool ClearRequested;
        internal bool CompletionConfirmed;
        internal Outcome? Outcome;
        internal Lazy<BackgroundJobs.Attempt>? Job;
    }
    private sealed class Activity((string, string, string) key, Copy copy) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Lazy<BackgroundJobs.Attempt>? job = null;
            var complete = false;
            Outcome? outcome = null;
            lock (progressSync)
            {
                copy.Active--; copy.UpdatedUtc = DateTimeOffset.UtcNow;
                if (copy.Active == 0)
                {
                    complete = copy.CompletionConfirmed;
                    outcome = copy.Outcome;
                    if (copy.ClearRequested)
                    {
                        copies.TryRemove(new(key, copy));
                    }
                    job = copy.Job;
                    copy.Job = null;
                }
            }
            Finish(job, complete, outcome);
        }
    }
    private static readonly ConcurrentDictionary<(string DataRoot, string GroupKey, string VersionHash), Copy> copies = new();
    // Progress reads must never queue behind protected metadata, network,
    // discovery or verification work holding the world mutation gate.
    private static readonly object progressSync = new();
    private static (string, string, string) Key(LocalData data, SharedWorldPeerRoot root, string hash) =>
        (Path.GetFullPath(data.RootPath).ToUpperInvariant(), root.GroupKey, hash);

    internal static IDisposable Begin(LocalData data, SharedWorldPeerRoot root, string versionHash,
        string serverName = "Shared server", string worldName = "Shared world")
    {
        Copy copy;
        (string, string, string) key;
        Lazy<BackgroundJobs.Attempt>? job;
        lock (progressSync)
        {
            key = Key(data, root, versionHash);
            copy = copies.GetOrAdd(key, _ => new());
            if (copy.Active == 0)
            {
                copy.CompletionConfirmed = false;
                copy.ClearRequested = false;
                copy.Outcome = null;
                copy.Job = new(() => BackgroundJobs.For(data).Begin("download:" + root.GroupKey + ":" + versionHash,
                    root.ProfileId, serverName, worldName, "Save download"), LazyThreadSafetyMode.ExecutionAndPublication);
            }
            copy.Active++; copy.UpdatedUtc = DateTimeOffset.UtcNow;
            job = copy.Job;
        }
        // Lazy creation coalesces overlapping owners without doing persistence
        // while the global in-memory progress lock is held.
        try { _ = job?.Value; }
        catch
        {
            // No activity lease has reached the caller yet. An unforeseen
            // optional-recording error cannot leave a permanent hosting guard.
            lock (progressSync)
            {
                copy.Active--;
                if (ReferenceEquals(copy.Job, job)) copy.Job = null;
                if (copy.Active == 0) copies.TryRemove(new(key, copy));
            }
            throw;
        }
        return new Activity(key, copy);
    }

    internal static void Report(LocalData data, SharedWorldPeerRoot root, SharedWorldVersion version,
        string phase, long received, long total)
    {
        Lazy<BackgroundJobs.Attempt>? job;
        lock (progressSync)
        {
            var copy = copies.GetOrAdd(Key(data, root, version.VersionHash), _ => new());
            copy.Status = new(true, version.Number, null, phase, ReceivedBytes: received, TotalBytes: total,
                TransferPhase: phase, ReceiptConfirmed: false);
            copy.UpdatedUtc = DateTimeOffset.UtcNow;
            copy.ClearRequested = false;
            copy.CompletionConfirmed = false;
            job = copy.Job;
        }
        job?.Value.Report(phase, received, total);
    }

    // Start can avoid resolving protected sharing identity when no transfer
    // could affect it. A held flight still counts before its first report or
    // after Clear, until the final activity lease has been disposed.
    internal static bool HasPending(LocalData data)
    {
        lock (progressSync)
        {
            var dataRoot = Path.GetFullPath(data.RootPath).ToUpperInvariant();
            var now = DateTimeOffset.UtcNow;
            var pending = false;
            foreach (var item in copies.Where(item => item.Key.DataRoot == dataRoot))
            {
                if (Expired(item.Value, now)) copies.TryRemove(item);
                else pending |= item.Value.Active > 0 || item.Value.Status is { } status && BlocksHosting(status);
            }
            return pending;
        }
    }

    private static bool Expired(Copy copy, DateTimeOffset now) => copy.Active == 0 &&
        now - copy.UpdatedUtc > TimeSpan.FromSeconds(30);

    internal static ReceivedSharedWorldStatus? Read(LocalData data, SharedWorldPeerRoot root)
    {
        lock (progressSync)
        {
            var dataRoot = Path.GetFullPath(data.RootPath).ToUpperInvariant();
            var now = DateTimeOffset.UtcNow;
            foreach (var item in copies.Where(item => item.Key.DataRoot == dataRoot && item.Key.GroupKey == root.GroupKey))
            {
                // Seed requests and bounded pull passes resume in separate
                // flights. An abandoned flight ceases being an active transfer;
                // latest-save selection still rejects incomplete payloads.
                if (Expired(item.Value, now))
                    copies.TryRemove(item);
                else if (item.Value.Status is { } status) return status;
                else if (item.Value.Active > 0) return new(true, null, null, "Receiving", TransferPhase: "Receiving");
            }
            return null;
        }
    }

    internal static void Clear(LocalData data, SharedWorldPeerRoot root, string versionHash)
    {
        Lazy<BackgroundJobs.Attempt>? job = null;
        Outcome? outcome = null;
        var complete = false;
        lock (progressSync)
        {
            var key = Key(data, root, versionHash);
            if (!copies.TryGetValue(key, out var copy)) return;
            if (copy.Active == 0)
            {
                job = copy.Job; outcome = copy.Outcome; complete = copy.CompletionConfirmed;
                copies.TryRemove(new(key, copy));
            }
            else copy.ClearRequested = true;
        }
        Finish(job, complete, outcome);
    }

    // Clearing an observation also occurs for bounded history work and pauses.
    // Only a caller that completed verification and receipt processing may
    // request success; the held activity still blocks hosting until disposed.
    internal static void Complete(LocalData data, SharedWorldPeerRoot root, string versionHash)
    {
        Lazy<BackgroundJobs.Attempt>? job = null;
        lock (progressSync)
        {
            var key = Key(data, root, versionHash);
            if (!copies.TryGetValue(key, out var copy)) return;
            copy.CompletionConfirmed = true;
            copy.Outcome = null;
            copy.ClearRequested = true;
            if (copy.Active == 0) { job = copy.Job; copies.TryRemove(new(key, copy)); }
        }
        job?.Value.Complete("SaveReceived", "The save passed verification and receipt processing finished. Game loading has not been checked.");
    }

    internal static void Fail(LocalData data, SharedWorldPeerRoot root, string versionHash, string code, string message)
        => EndAttempt(data, root, versionHash, new(true, code, message));

    internal static void Pending(LocalData data, SharedWorldPeerRoot root, string versionHash, string code, string message)
        => EndAttempt(data, root, versionHash, new(false, code, message));

    private static void EndAttempt(LocalData data, SharedWorldPeerRoot root, string versionHash, Outcome outcome)
    {
        Lazy<BackgroundJobs.Attempt>? job = null;
        lock (progressSync)
        {
            if (!copies.TryGetValue(Key(data, root, versionHash), out var copy)) return;
            copy.CompletionConfirmed = false;
            if (outcome.Failed || copy.Outcome?.Failed != true) copy.Outcome = outcome;
            if (copy.Active == 0) { job = copy.Job; copy.Job = null; outcome = copy.Outcome ?? outcome; }
        }
        Finish(job, false, outcome);
    }

    internal static void ReceiveFailure(LocalData data, SharedWorldPeerRoot root, string versionHash,
        Exception error, CancellationToken cancellationToken, WorldPeerTransferOperation operation = WorldPeerTransferOperation.None,
        WorldPeerTransferPhase phase = WorldPeerTransferPhase.Pulling, bool workBudget = false)
    {
        Lazy<BackgroundJobs.Attempt>? job;
        lock (progressSync)
        {
            if (!copies.TryGetValue(Key(data, root, versionHash), out var copy)) return;
            job = copy.Job;
            phase = copy.Status?.TransferPhase switch
            {
                "Preparing" => WorldPeerTransferPhase.Preparing,
                "Receiving" => WorldPeerTransferPhase.Pulling,
                "Verifying" => WorldPeerTransferPhase.VerifyingLocal,
                "Receipt" => WorldPeerTransferPhase.Receipt,
                _ => phase
            };
        }
        var failure = SharedSaveTransferFailure.Classify(error, cancellationToken, operation, phase, workBudget);
        if (!failure.Pending && job is not null)
            StartAttemptDiagnostics.RecordFailure(data, root.ProfileId, "ReceiveSave_" + failure.Operation,
                failure.Phase, failure.Code, failure.Message, "TransferFailed", job.Value.Id, job.Value.Id);
        EndAttempt(data, root, versionHash, new(!failure.Pending, failure.Code, failure.Message, failure));
    }

    private static void Finish(Lazy<BackgroundJobs.Attempt>? job, bool complete, Outcome? outcome)
    {
        if (job is null) return;
        if (complete) job.Value.Complete("SaveReceived", "The save passed verification and receipt processing finished. Game loading has not been checked.");
        else if (outcome?.Failed == true)
        {
            if (outcome.Failure is { } failure) job.Value.Fail(failure);
            else job.Value.Fail(outcome.Code, outcome.Message);
        }
        else job.Value.Waiting(outcome?.Code ?? "CatchUpPending", outcome?.Message ??
            "This bounded pass ended before verification and receipt completion. Further history or payload checks may continue in the background.");
    }

    internal static bool BlocksHosting(ReceivedSharedWorldStatus status) =>
        status.TransferPhase is "Preparing" or "Receiving" or "Verifying" or "Receipt" ||
        status.State is "Preparing" or "Receiving" or "Verifying" or "Receipt";
}

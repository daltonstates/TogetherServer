using System.Collections.Concurrent;

namespace TogetherServer;

public sealed record BackgroundJob(Guid Id, Guid ProfileId, string ServerName, string WorldName,
    string Operation, string Phase, string State, long ReceivedBytes, long? TotalBytes,
    DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, DateTimeOffset? CompletedUtc,
    string? ResultCode, string? Message, int Attempts);
public sealed record BackgroundJobList(IReadOnlyList<BackgroundJob> Jobs, int ActiveCount);

// Display-only application jobs. Scope is internal canonical identity, never a
// route/credential. A job cannot grant permission, prove a receipt or start a run.
internal sealed class BackgroundJobs
{
    internal const int MaximumJobs = 100;
    internal const int MaximumVisibleJobs = 512;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    internal static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(30);
    internal sealed record Saved(string Scope, BackgroundJob Job, bool Periodic);
    internal sealed class Entry(Saved saved)
    {
        internal Saved Saved = saved;
        internal int Users;
        internal Completion? PendingCompletion;
    }
    internal sealed record Completion(string State, string Code, string? Message,
        string? DiagnosticOperation = null, string? DiagnosticPhase = null);
    private static readonly ConcurrentDictionary<string, Lazy<BackgroundJobs>> instances = new(StringComparer.OrdinalIgnoreCase);
    internal static BackgroundJobs For(LocalData data) => instances.GetOrAdd(Path.GetFullPath(data.RootPath),
        _ => new(() => new(data), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    private readonly LocalData data;
    private readonly TimeProvider clock;
    private readonly object sync = new();
    private readonly object saveSync = new();
    private readonly bool persistenceEnabled = true;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private DateTimeOffset lastSaved;
    internal Action? BeforePersistForChecks { get; set; }

    internal BackgroundJobs(LocalData data, TimeProvider? clock = null)
    {
        this.data = data; this.clock = clock ?? TimeProvider.System;
        var now = this.clock.GetUtcNow();
        Saved[] saved;
        try { saved = data.LoadState<Saved[]>("background-jobs.json", []); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Job history is optional display evidence. Keep useful current
            // work in memory, and preserve a history file we could not read.
            // A later app startup may retry; this session must not replace it.
            persistenceEnabled = false;
            saved = [];
            DiagnosticOutput.WriteError("Background job history could not be read: " + error.GetType().Name);
        }
        foreach (var item in saved.TakeLast(MaximumVisibleJobs))
        {
            if (item is null || item.Scope is null || item.Scope.Length > 256 || item.Job is not { } retained ||
                retained.ServerName is null || retained.WorldName is null || retained.Operation is null || retained.Phase is null ||
                retained.Id == Guid.Empty || retained.ProfileId == Guid.Empty || retained.Attempts is < 1 or > 1000000 ||
                retained.ReceivedBytes < 0 || retained.TotalBytes is < 0 || retained.TotalBytes < retained.ReceivedBytes ||
                retained.UpdatedUtc < now - Retention || retained.State is not
                    ("Running" or "Stalled" or "Waiting" or "Succeeded" or "Failed" or "Interrupted")) continue;
            var job = item.Job;
            if (job.State is "Running" or "Stalled") job = job with
            { State = "Interrupted", CompletedUtc = now, ResultCode = "AppInterrupted", Message = "The app stopped before this work finished. A later background check may retry it." };
            entries[item.Scope] = new(item with { Job = job });
        }
        Save(force: true);
    }

    internal sealed class Attempt(BackgroundJobs owner, string scope, Entry entry) : IDisposable
    {
        private int disposed;
        internal Guid Id => entry.Saved.Job.Id;
        internal void Report(string phase, long received = 0, long? total = null, string? message = null)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            owner.Change(scope, entry, job => job with
            {
                Phase = Bounded(phase, 80),
                State = "Running",
                ReceivedBytes = Math.Max(0, received),
                TotalBytes = total is >= 0 && total >= received ? total : null,
                Message = message is null ? null : Bounded(message, 500),
                CompletedUtc = null,
                ResultCode = null
            });
        }
        internal void Complete(string code, string? message = null) => Finish("Succeeded", code, message);
        internal void Fail(string code, string? message = null) => Finish("Failed", code, message);
        internal void Fail(SharedSaveTransferFailure failure) => Finish("Failed", failure.Code, failure.Message,
            "ReceiveSave_" + failure.Operation, failure.Phase);
        internal void Waiting(string code, string? message = null) => Finish("Waiting", code, message);
        private void Finish(string state, string code, string? message, string? diagnosticOperation = null, string? diagnosticPhase = null)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            owner.Finish(scope, entry, state, code, message, diagnosticOperation, diagnosticPhase);
        }
        public void Dispose() => Finish("Interrupted", "WorkInterrupted", "This attempt ended before completion. Progress may be resumed by a later check.");
    }

    internal Attempt Begin(string scope, Guid profileId, string serverName, string worldName,
        string operation, bool periodic = false)
    {
        Attempt attempt;
        var added = false;
        lock (sync)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            if (!entries.TryGetValue(scope, out var entry))
            {
                entry = new(new(scope, new(Guid.NewGuid(), profileId, Bounded(serverName, 100), Bounded(worldName, 100),
                    Bounded(operation, 80), "Queued", "Running", 0, null, now, now, null, null, null, 0), periodic));
                entries[scope] = entry;
                added = true;
            }
            if (entry.Users++ == 0)
            {
                entry.PendingCompletion = null;
                var previous = entry.Saved.Job;
                var resume = operation == "Save download";
                entry.Saved = entry.Saved with
                {
                    Job = previous with
                    {
                        State = "Running",
                        Phase = "Checking",
                        StartedUtc = resume ? previous.StartedUtc : now,
                        UpdatedUtc = now,
                        CompletedUtc = null,
                        ResultCode = null,
                        Message = null,
                        ReceivedBytes = resume ? previous.ReceivedBytes : 0,
                        TotalBytes = resume ? previous.TotalBytes : null,
                        Attempts = Math.Min(1000000, previous.Attempts + 1)
                    }
                };
            }
            attempt = new(this, scope, entry);
        }
        Save(force: added);
        return attempt;
    }

    private void Change(string scope, Entry expected, Func<BackgroundJob, BackgroundJob> update)
    {
        lock (sync)
        {
            if (entries.GetValueOrDefault(scope) != expected || expected.Users == 0) return;
            var next = update(expected.Saved.Job);
            // Another owner reporting work must not inherit an earlier
            // completion, even when its measured counters are unchanged.
            // An unchanged report still does not renew visible UpdatedUtc.
            if (expected.PendingCompletion?.State == "Succeeded") expected.PendingCompletion = null;
            if (next == expected.Saved.Job) return;
            expected.Saved = expected.Saved with { Job = next with { UpdatedUtc = clock.GetUtcNow() } };
        }
        Save(force: false);
    }
    private void Finish(string scope, Entry expected, string state, string code, string? message,
        string? diagnosticOperation = null, string? diagnosticPhase = null)
    {
        BackgroundJob? failure = null;
        string? failureOperation = null, failurePhase = null;
        var finished = false;
        lock (sync)
        {
            if (entries.GetValueOrDefault(scope) != expected || expected.Users == 0) return;
            var completion = new Completion(state, Bounded(code, 100), message is null ? null : Bounded(message, 500),
                diagnosticOperation, diagnosticPhase);
            // A genuine failure or verified completion supersedes an older
            // outcome. Passive disposal/waiting cannot erase either one.
            if (state is "Failed" or "Succeeded" || expected.PendingCompletion?.State is not ("Failed" or "Succeeded"))
                expected.PendingCompletion = completion;
            if (state == "Failed" && expected.Saved.Job.Operation == "Save download")
            {
                failure = expected.Saved.Job with { ResultCode = completion.Code, Message = completion.Message };
                failureOperation = completion.DiagnosticOperation;
                failurePhase = completion.DiagnosticPhase;
            }
            if (--expected.Users == 0)
            {
                completion = expected.PendingCompletion ?? completion;
                state = completion.State;
                finished = true;
                var now = clock.GetUtcNow();
                expected.Saved = expected.Saved with
                {
                    Job = expected.Saved.Job with
                    {
                        State = state,
                        ResultCode = completion.Code,
                        Message = completion.Message,
                        UpdatedUtc = now,
                        CompletedUtc = state == "Waiting" ? null : now
                    }
                };
                Prune(now);
            }
        }
        if (failure is not null)
            StartAttemptDiagnostics.RecordFailure(data, failure.ProfileId, failureOperation ?? "ReceiveSave",
                failurePhase ?? failure.Phase, failure.ResultCode ?? code, failure.Message ?? "The save transfer did not finish.",
                "TransferFailed", failure.Id, failure.Id);
        Save(force: failure is not null || finished && state != "Waiting");
    }
    internal BackgroundJobList Read()
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            var retained = entries.Values.Select(entry => entry.Saved.Job).Where(job => job.UpdatedUtc >= now - Retention).ToArray();
            var jobs = retained.Where(job => job.State is "Running" or "Stalled" or "Waiting")
                .Concat(retained.Where(job => job.State is not ("Running" or "Stalled" or "Waiting"))
                    .OrderByDescending(job => job.UpdatedUtc).Take(MaximumJobs))
                .Select(job => job.State == "Running" && now - job.UpdatedUtc > StalledAfter
                    ? job with { State = "Stalled", Message = "No progress has been reported recently. This work has not been confirmed complete." } : job)
                .OrderByDescending(job => job.State is "Running" or "Stalled").ThenByDescending(job => job.UpdatedUtc).Take(MaximumVisibleJobs).ToArray();
            return new(jobs, retained.Count(job => job.State is "Running" or "Stalled"));
        }
    }
    private static string Bounded(string value, int maximum) => new(SupportReportRedactor.Redact(value, maximum)
        .Where(character => !char.IsControl(character)).ToArray());
    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in entries.Where(pair => pair.Value.Users == 0 && pair.Value.Saved.Job.UpdatedUtc < now - Retention).ToArray()) entries.Remove(pair.Key);
        var history = entries.Where(pair => pair.Value.Users == 0 && pair.Value.Saved.Job.State != "Waiting")
            .OrderByDescending(pair => pair.Value.Saved.Job.UpdatedUtc).Skip(MaximumJobs).ToArray();
        foreach (var pair in history) entries.Remove(pair.Key);
        while (entries.Count >= MaximumVisibleJobs)
        {
            var oldest = entries.Where(pair => pair.Value.Users == 0).MinBy(pair => pair.Value.Saved.Job.UpdatedUtc);
            if (oldest.Value is null) break;
            entries.Remove(oldest.Key);
        }
    }
    private void Save(bool force)
    {
        if (!persistenceEnabled) return;
        // Only this persistence lock may surround I/O. A reader never waits
        // for disk flush, diagnostics or another world's progress callbacks.
        // Capture after acquiring saveSync so an older writer cannot overwrite
        // a newer snapshot when concurrent scopes finish out of order.
        lock (saveSync)
        {
            Saved[] snapshot;
            DateTimeOffset now;
            lock (sync)
            {
                now = clock.GetUtcNow();
                if (!force && now - lastSaved < TimeSpan.FromSeconds(1)) return;
                snapshot = entries.Values.Select(entry => entry.Saved).OrderBy(item => item.Job.UpdatedUtc)
                    .TakeLast(MaximumVisibleJobs).ToArray();
            }
            try
            {
                BeforePersistForChecks?.Invoke();
                data.SaveState("background-jobs.json", snapshot);
                lock (sync) lastSaved = now;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { DiagnosticOutput.WriteError("Background job history could not be saved: " + error.GetType().Name); }
        }
    }
}

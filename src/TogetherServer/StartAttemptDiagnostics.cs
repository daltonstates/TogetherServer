using System.Runtime.CompilerServices;

namespace TogetherServer;

// These are attempts, not game sessions. No duration, readiness, occupancy,
// credential, executable, world path or world content belongs in this journal.
public sealed record StartAttemptDiagnostic(Guid Id, Guid ProfileId, Guid OperationId,
    Guid? TargetDeviceId, string Operation, string Phase, string Code, string Message,
    DateTimeOffset RequestedUtc, DateTimeOffset? CompletedUtc, Guid? RelatedJobId, string Kind);

internal static class StartAttemptDiagnostics
{
    internal const int MaximumEntries = 500;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const string FileName = "start-attempts.json";
    private static readonly ConditionalWeakTable<LocalData, Journal> Journals = new();
    private static readonly AsyncLocal<Context?> Ambient = new();

    // GetValue may invoke competing factories. Construct exactly one journal:
    // only a genuine initial load may classify durable Pending as interrupted.
    private static Journal For(LocalData data)
    {
        lock (Journals) return Journals.GetValue(data, value => new(value));
    }

    private sealed class Journal
    {
        internal readonly object Sync = new();
        internal List<StartAttemptDiagnostic> Entries;
        internal Journal(LocalData data)
        {
            Entries = data.LoadState(FileName, new List<StartAttemptDiagnostic>());
            // A persisted intent is diagnostic evidence only. Restart never
            // resumes an explicit Start or turns an unfinished attempt into a run.
            var interrupted = false;
            Entries = Entries.Select(item =>
            {
                if (item.Kind != "Pending") return item;
                interrupted = true;
                return item with
                {
                    Kind = "Interrupted",
                    Code = "AppInterrupted",
                    Message = "The app closed before this operation completed. Review current status before another Start.",
                    CompletedUtc = DateTimeOffset.UtcNow
                };
            }).ToList();
            if (interrupted) Save(data, this);
        }
    }

    internal sealed class Context(LocalData data, Guid profileId, string operation,
        Guid id, Guid? relatedJobId)
    {
        internal LocalData Data { get; } = data;
        internal Guid ProfileId { get; } = profileId;
        internal Guid Id { get; } = id;
        internal Guid? RelatedJobId { get; set; } = relatedJobId;
        internal string Operation { get; } = operation;
        internal string Phase { get; set; } = "Requested";
        internal DateTimeOffset RequestedUtc { get; } = DateTimeOffset.UtcNow;
        internal bool Terminal { get; set; }
        internal string? FailureKind { get; set; }
        internal string? FailureCode { get; set; }
    }

    internal static Scope Begin(LocalData data, Guid profileId, string operation = "Start",
        Guid? operationId = null, Guid? relatedJobId = null)
    {
        if (Ambient.Value is { } existing && ReferenceEquals(existing.Data, data))
        {
            if (existing.Terminal)
            {
                existing.Terminal = false;
                existing.FailureKind = null;
                existing.FailureCode = null;
                Write(existing, "Pending", "OperationRunning", "The explicit operation is repeating its launch checks.", null);
            }
            return new(existing, false, null);
        }
        var previous = Ambient.Value;
        var context = new Context(data, profileId, Token(operation), operationId ?? Guid.NewGuid(), relatedJobId);
        Ambient.Value = context;
        Write(context, "Pending", "OperationRequested", "The explicit operation is being checked.", null);
        return new(context, true, previous);
    }

    internal sealed class Scope : IDisposable
    {
        private readonly Context context;
        private readonly bool owns;
        private readonly Context? previous;
        private bool disposed;
        internal Scope(Context context, bool owns, Context? previous)
        { this.context = context; this.owns = owns; this.previous = previous; }
        internal Guid OperationId => context.Id;
        internal void LinkJob(Guid jobId)
        {
            if (jobId == Guid.Empty || context.RelatedJobId == jobId) return;
            context.RelatedJobId = jobId;
            if (!context.Terminal)
                Write(context, "Pending", "OperationRunning", "The explicit operation is being checked.", null);
        }
        internal void ReportPhase(string phase)
        {
            if (context.Terminal || context.Phase == phase) return;
            context.Phase = Token(phase);
            Write(context, "Pending", "OperationRunning", "The explicit operation is being checked.", null);
        }
        internal void Complete(bool ok, string code, string message, string kind = "BlockedStart")
        {
            if (ok)
            {
                try
                {
                    var journal = For(context.Data);
                    lock (journal.Sync)
                    {
                        journal.Entries.RemoveAll(item => item.OperationId == context.Id);
                        Save(context.Data, journal);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                    System.Security.SecurityException or System.Text.Json.JsonException or ArgumentException)
                { context.Data.TryAudit($"start-diagnostics-unavailable {context.Id} {error.GetType().Name}"); }
            }
            else
            {
                // Outer request/service wrappers preserve the manager's more
                // precise failure classification for the same typed result.
                if (context.Terminal && context.FailureCode == code && context.FailureKind is { } previousKind)
                    kind = previousKind;
                if (code == "LaunchFailed") kind = "LaunchFailed";
                context.FailureKind = kind;
                context.FailureCode = code;
                Write(context, kind, code, message, DateTimeOffset.UtcNow);
            }
            context.Terminal = true;
        }
        internal void Fail(string code, string message, string kind = "PreparationFailed") => Complete(false, code, message, kind);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!owns) return;
            if (!context.Terminal) Fail("OperationInterrupted",
                "The operation was canceled or interrupted before completion. Review current status before another Start.", "Interrupted");
            Ambient.Value = previous;
        }
    }

    internal static void RecordFailure(LocalData data, Guid profileId, string operation,
        string phase, string code, string message, string kind = "PreparationFailed",
        Guid? operationId = null, Guid? relatedJobId = null)
    {
        var context = Ambient.Value is { } current && ReferenceEquals(current.Data, data) && operationId is null
            ? current : new Context(data, profileId, Token(operation), operationId ?? Guid.NewGuid(), relatedJobId);
        context.Phase = Token(phase);
        Write(context, kind, code, message, DateTimeOffset.UtcNow);
    }

    internal static IReadOnlyList<StartAttemptDiagnostic> Recent(LocalData data, Guid profileId, int limit)
        => Recent(data, new HashSet<Guid> { profileId }, limit);

    internal static IReadOnlyList<StartAttemptDiagnostic> Recent(LocalData data, IReadOnlySet<Guid> profileIds, int limit)
    {
        var journal = For(data);
        lock (journal.Sync)
            return journal.Entries.Where(item => profileIds.Contains(item.ProfileId) && item.Kind != "Pending" &&
                    item.RequestedUtc >= DateTimeOffset.UtcNow - Retention)
                .OrderByDescending(item => item.CompletedUtc ?? item.RequestedUtc)
                .Take(Math.Clamp(limit, 1, HostManager.MaximumRecentSessionLimit)).ToArray();
    }

    private static void Write(Context context, string kind, string code, string message, DateTimeOffset? completed)
    {
        try
        {
            var journal = For(context.Data);
            lock (journal.Sync)
            {
                var target = context.Data.LoadState<HostIdentityMetadata?>("host-identity.json", null)?.HostId;
                var entry = new StartAttemptDiagnostic(context.Id, context.ProfileId, context.Id,
                    target == Guid.Empty ? null : target, context.Operation, context.Phase, Token(code),
                    SupportReportRedactor.Redact(message), context.RequestedUtc, completed,
                    context.RelatedJobId, Token(kind));
                var existing = journal.Entries.FindIndex(item => item.OperationId == context.Id);
                if (existing < 0) journal.Entries.Add(entry);
                else journal.Entries[existing] = entry with { RequestedUtc = journal.Entries[existing].RequestedUtc };
                Save(context.Data, journal);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or System.Text.Json.JsonException or ArgumentException)
        { context.Data.TryAudit($"start-diagnostics-unavailable {context.Id} {error.GetType().Name} {DateTimeOffset.UtcNow:O}"); }
    }

    private static void Save(LocalData data, Journal journal)
    {
        journal.Entries = journal.Entries.Where(item => item.RequestedUtc >= DateTimeOffset.UtcNow - Retention)
            .OrderByDescending(item => item.RequestedUtc).Take(MaximumEntries).ToList();
        data.SaveState(FileName, journal.Entries);
    }

    private static string Token(string value) => SupportReportRedactor.Token(value, 64);
}

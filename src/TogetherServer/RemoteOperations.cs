using System.Collections.Concurrent;
using System.Text.Json;

namespace TogetherServer;

public static class RemoteOperationStates
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Interrupted = "Interrupted";

    public static bool Terminal(string state) => state is Succeeded or Failed or Interrupted;
}

public sealed class RemoteOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ProfileId { get; set; }
    public string Action { get; set; } = "";
    public string State { get; set; } = RemoteOperationStates.Pending;
    public bool? Ok { get; set; }
    public string Code { get; set; } = "OperationAccepted";
    public string Message { get; set; } = "Remote operation accepted by the Host.";
    public DateTimeOffset RequestedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public List<PortConflictView>? PortConflicts { get; set; }
}

public sealed record RemoteOperationView(Guid Id, Guid ProfileId, string Action, string State,
    bool? Ok, string Code, string Message, DateTimeOffset RequestedUtc,
    DateTimeOffset? StartedUtc, DateTimeOffset? CompletedUtc,
    IReadOnlyList<PortConflictView>? PortConflicts = null);

public sealed record RemoteOperationOutcome(bool Ok, string Code, string Message,
    IReadOnlyList<PortConflictView>? PortConflicts = null);

public sealed record RemoteOperationSubmission(bool Accepted, bool Existing, string Code, string Message,
    RemoteOperationView? Operation);

// Remote requests are journaled before execution. A Host restart marks unfinished
// work Interrupted and never replays a destructive operation automatically.
public sealed class RemoteOperationCoordinator : IAsyncDisposable
{
    private const int MaximumEntries = 500;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly object sync = new();
    private readonly LocalData data;
    private readonly List<RemoteOperation> operations;
    private readonly ConcurrentQueue<QueuedOperation> queue = new();
    private readonly SemaphoreSlim queueSignal = new(0);
    private readonly CancellationTokenSource workerStop = new();
    private readonly Task worker;
    private int disposed;

    private sealed record QueuedOperation(Guid OperationId, Func<Task<RemoteOperationOutcome>> Execute);

    public RemoteOperationCoordinator(LocalData data)
    {
        this.data = data;
        operations = data.LoadRemoteOperations();
        var interrupted = false;
        foreach (var operation in operations.Where(operation =>
                     operation.State is RemoteOperationStates.Pending or RemoteOperationStates.Running))
        {
            operation.State = RemoteOperationStates.Interrupted;
            operation.Ok = false;
            operation.Code = "HostRestarted";
            operation.Message = "The Host restarted before this operation reported a final result. It was not replayed; refresh server status before trying again.";
            operation.CompletedUtc = DateTimeOffset.UtcNow;
            interrupted = true;
        }
        var pruned = PruneLocked();
        if (interrupted || pruned) data.SaveRemoteOperations(operations);
        worker = Task.Run(() => ProcessQueueAsync(workerStop.Token));
    }

    public RemoteOperationSubmission Submit(Guid deviceId, Guid requestId, Guid profileId, string action,
        Func<Task<RemoteOperationOutcome>> execute)
    {
        RemoteOperation operation;
        lock (sync)
        {
            if (disposed != 0)
                return new(false, false, "OperationCoordinatorStopped",
                    "The Host is closing and cannot accept another remote operation.", null);
            var existing = operations.SingleOrDefault(item => item.DeviceId == deviceId && item.RequestId == requestId);
            if (existing is not null)
            {
                if (existing.ProfileId != profileId || !existing.Action.Equals(action, StringComparison.Ordinal))
                    return new(false, true, "IdempotencyConflict",
                        "Request ID was already used for a different action.", null);
                return new(true, true, existing.Code, existing.Message, View(existing));
            }

            operation = new RemoteOperation
            {
                DeviceId = deviceId,
                RequestId = requestId,
                ProfileId = profileId,
                Action = action
            };
            operations.Add(operation);
            PruneLocked();
            if (operations.Count > MaximumEntries)
            {
                // Never discard an unfinished operation merely to make room.
                // If all 500 retained entries are still active, reject new work
                // before it is journaled or scheduled.
                operations.Remove(operation);
                return new(false, false, "OperationQueueFull",
                    "The Host already has 500 unfinished remote operations. Wait for an operation to finish before trying again.", null);
            }
            try { data.SaveRemoteOperations(operations); }
            catch (Exception ex) when (JournalFailure(ex))
            {
                operations.Remove(operation);
                return new(false, false, "OperationJournalUnavailable",
                    "The Host could not safely record this request, so it was not run.", null);
            }
            queue.Enqueue(new(operation.Id, execute));
            queueSignal.Release();
        }

        data.TryAudit($"remote-operation-accepted {operation.Id} {deviceId} {profileId} {action} {operation.RequestedUtc:O}");
        RecordActivity("RemoteActionAccepted", $"A Friend requested {ActionLabel(action)}.",
            ActivitySeverity.Important, profileId);
        var acceptedView = View(operation);
        return new(true, false, acceptedView.Code, acceptedView.Message, acceptedView);
    }

    public RemoteOperationSubmission? Lookup(Guid deviceId, Guid requestId, Guid profileId, string action)
    {
        lock (sync)
        {
            var existing = operations.SingleOrDefault(item => item.DeviceId == deviceId && item.RequestId == requestId);
            if (existing is null) return null;
            return existing.ProfileId != profileId || !existing.Action.Equals(action, StringComparison.Ordinal)
                ? new(false, true, "IdempotencyConflict", "Request ID was already used for a different action.", null)
                : new(true, true, existing.Code, existing.Message, View(existing));
        }
    }

    public RemoteOperationView? Find(Guid deviceId, Guid operationId)
    {
        lock (sync)
        {
            var operation = operations.SingleOrDefault(item => item.Id == operationId && item.DeviceId == deviceId);
            return operation is null ? null : View(operation);
        }
    }

    public IReadOnlyList<RemoteOperationView> Recent(int maximum = 100)
    {
        lock (sync) return operations.OrderByDescending(item => item.RequestedUtc)
            .Take(Math.Clamp(maximum, 1, MaximumEntries)).Select(View).ToList();
    }

    public IReadOnlyList<RemoteOperationView> RecentFor(Guid deviceId, int maximum = 100)
    {
        lock (sync) return operations.Where(item => item.DeviceId == deviceId)
            .OrderByDescending(item => item.RequestedUtc)
            .Take(Math.Clamp(maximum, 1, MaximumEntries)).Select(View).ToList();
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await queueSignal.WaitAsync(cancellationToken);
                while (!cancellationToken.IsCancellationRequested && queue.TryDequeue(out var queued))
                {
                    try { await ExecuteAsync(queued.OperationId, queued.Execute); }
                    catch (Exception ex)
                    {
                        data.TryAudit($"remote-operation-worker-failed {queued.OperationId} {ex.GetType().Name} {DateTimeOffset.UtcNow:O}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ExecuteAsync(Guid operationId, Func<Task<RemoteOperationOutcome>> execute)
    {
        RemoteOperation operation;
        lock (sync)
        {
            operation = operations.Single(item => item.Id == operationId);
            operation.State = RemoteOperationStates.Running;
            operation.Code = "OperationRunning";
            operation.Message = "The Host is carrying out the requested action.";
            operation.StartedUtc = DateTimeOffset.UtcNow;
            try { data.SaveRemoteOperations(operations); }
            catch (Exception ex) when (JournalFailure(ex))
            {
                operation.State = RemoteOperationStates.Failed;
                operation.Ok = false;
                operation.Code = "OperationJournalUnavailable";
                operation.Message = "The Host could not safely update the operation journal, so the action was not run.";
                operation.CompletedUtc = DateTimeOffset.UtcNow;
                try { data.SaveRemoteOperations(operations); }
                catch (Exception retry) when (JournalFailure(retry)) { /* The action remains unexecuted and fails closed. */ }
                data.TryAudit($"remote-operation-journal-failed {operation.Id} before-execution {DateTimeOffset.UtcNow:O}");
                return;
            }
        }

        RemoteOperationOutcome outcome;
        try { outcome = await execute(); }
        catch (Exception ex)
        {
            outcome = new(false, "OperationFailed",
                "The Host could not complete the remote operation: " + ex.GetType().Name + ".");
        }

        var publicOutcome = PublicOutcome(operation.Action, outcome);
        lock (sync)
        {
            operation = operations.Single(item => item.Id == operationId);
            operation.State = publicOutcome.Ok ? RemoteOperationStates.Succeeded : RemoteOperationStates.Failed;
            operation.Ok = publicOutcome.Ok;
            operation.Code = publicOutcome.Code;
            operation.Message = publicOutcome.Message;
            operation.PortConflicts = publicOutcome.PortConflicts?.ToList();
            operation.CompletedUtc = DateTimeOffset.UtcNow;
            PruneLocked();
            try { data.SaveRemoteOperations(operations); }
            catch (Exception ex) when (JournalFailure(ex))
            {
                data.TryAudit($"remote-operation-journal-failed {operation.Id} after-execution {DateTimeOffset.UtcNow:O}");
            }
        }
        data.TryAudit($"remote-operation-complete {operation.Id} {operation.DeviceId} {operation.ProfileId} {operation.Action} {operation.Code} {operation.CompletedUtc:O}");
        RecordActivity(publicOutcome.Ok ? "RemoteActionSucceeded" : "RemoteActionFailed",
            $"Friend {ActionLabel(operation.Action)} {(publicOutcome.Ok ? "completed" : "failed")}.",
            publicOutcome.Ok ? ActivitySeverity.Important : ActivitySeverity.Warning, operation.ProfileId);
    }

    private bool PruneLocked()
    {
        var before = operations.Count;
        var cutoff = DateTimeOffset.UtcNow - Retention;
        operations.RemoveAll(item => RemoteOperationStates.Terminal(item.State) && item.RequestedUtc < cutoff);
        if (operations.Count > MaximumEntries)
        {
            var remove = operations.Where(item => RemoteOperationStates.Terminal(item.State))
                .OrderBy(item => item.RequestedUtc).Take(operations.Count - MaximumEntries).Select(item => item.Id).ToHashSet();
            operations.RemoveAll(item => remove.Contains(item.Id));
        }
        return operations.Count != before;
    }

    private static RemoteOperationView View(RemoteOperation operation) =>
        new(operation.Id, operation.ProfileId, operation.Action, operation.State, operation.Ok,
            operation.Code, operation.Message, operation.RequestedUtc, operation.StartedUtc,
            operation.CompletedUtc, operation.PortConflicts);

    private static string ActionLabel(string action) => action switch
    {
        "start" => "Start",
        "stop" => "Stop",
        "restart" => "Restart",
        "replace" => "empty-server replacement",
        "extend" => "countdown extension",
        _ => "server action"
    };

    private static RemoteOperationOutcome PublicOutcome(string action, RemoteOperationOutcome outcome)
    {
        var code = outcome.Code is { Length: > 0 and <= 64 } &&
                   outcome.Code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? outcome.Code : "OperationFailed";
        var label = ActionLabel(action);
        var message = outcome.Ok
            ? $"The Host completed the requested {label}."
            : code == "PortConflict"
                ? "Another managed server is using a required game port. Review the safe replacement option."
                : $"The Host could not complete the requested {label} ({code}). Review the current server status before trying again.";
        return new(outcome.Ok, code, message, outcome.PortConflicts);
    }

    private static bool JournalFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or JsonException;

    private void RecordActivity(string action, string message, string severity, Guid profileId)
    {
        try
        {
            data.RecordActivity("Remote", action, message, severity, profileId,
            visibility: ActivityVisibility.AssignedFriends);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or System.Text.Json.JsonException or NotSupportedException)
        { data.TryAudit($"activity-write-failed Remote {action} {ex.GetType().Name} {DateTimeOffset.UtcNow:O}"); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed != 0) return;
            disposed = 1;
            workerStop.Cancel();
        }
        try { await worker; }
        catch (OperationCanceledException) { }
        lock (sync)
        {
            var interrupted = false;
            foreach (var operation in operations.Where(item => item.State == RemoteOperationStates.Pending))
            {
                operation.State = RemoteOperationStates.Interrupted;
                operation.Ok = false;
                operation.Code = "HostShuttingDown";
                operation.Message = "The Host closed before this queued operation started. It was not run.";
                operation.CompletedUtc = DateTimeOffset.UtcNow;
                interrupted = true;
            }
            if (interrupted)
            {
                try { data.SaveRemoteOperations(operations); }
                catch (Exception ex) when (JournalFailure(ex))
                { data.TryAudit($"remote-operation-journal-failed during-shutdown {DateTimeOffset.UtcNow:O}"); }
            }
        }
        workerStop.Dispose();
        queueSignal.Dispose();
    }
}

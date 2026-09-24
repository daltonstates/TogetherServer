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
public sealed class RemoteOperationCoordinator
{
    private const int MaximumEntries = 500;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly object sync = new();
    private readonly LocalData data;
    private readonly List<RemoteOperation> operations;

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
        if (interrupted || PruneLocked()) data.SaveRemoteOperations(operations);
    }

    public RemoteOperationSubmission Submit(Guid deviceId, Guid requestId, Guid profileId, string action,
        Func<Task<RemoteOperationOutcome>> execute)
    {
        RemoteOperation operation;
        lock (sync)
        {
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
            data.SaveRemoteOperations(operations);
        }

        data.Audit($"remote-operation-accepted {operation.Id} {deviceId} {profileId} {action} {operation.RequestedUtc:O}");
        var acceptedView = View(operation);
        _ = Task.Run(() => ExecuteAsync(operation.Id, execute));
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
            data.SaveRemoteOperations(operations);
        }

        RemoteOperationOutcome outcome;
        try { outcome = await execute(); }
        catch (Exception ex)
        {
            outcome = new(false, "OperationFailed",
                "The Host could not complete the remote operation: " + ex.GetType().Name + ".");
        }

        lock (sync)
        {
            operation = operations.Single(item => item.Id == operationId);
            operation.State = outcome.Ok ? RemoteOperationStates.Succeeded : RemoteOperationStates.Failed;
            operation.Ok = outcome.Ok;
            operation.Code = outcome.Code;
            operation.Message = outcome.Message;
            operation.PortConflicts = outcome.PortConflicts?.ToList();
            operation.CompletedUtc = DateTimeOffset.UtcNow;
            PruneLocked();
            data.SaveRemoteOperations(operations);
        }
        data.Audit($"remote-operation-complete {operation.Id} {operation.DeviceId} {operation.ProfileId} {operation.Action} {operation.Code} {operation.CompletedUtc:O}");
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
}

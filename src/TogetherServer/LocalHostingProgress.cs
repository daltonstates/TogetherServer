namespace TogetherServer;

public sealed record LocalHostingProgress(string Phase, string State, string Message,
    DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, long ReceivedBytes,
    long TotalBytes, int? Percent, int CompletedFiles = 0, int TotalFiles = 0);

// A display-only observation of the one local mutation. It never drives a
// lifecycle decision or persists an instruction to resume work after restart.
internal sealed class LocalHostingProgressStore(TimeProvider? clock = null)
{
    private readonly object sync = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private object? owner;
    private Guid profile;
    private object? operation;
    private LocalHostingProgress? current;

    internal Operation Begin(object connection, Guid profileId)
    {
        lock (sync)
        {
            var token = new object();
            owner = connection;
            profile = profileId;
            operation = token;
            var now = time.GetUtcNow();
            current = new("Permission", "Working", "Checking the owner's current hosting permission.", now, now, 0, 0, null);
            return new(this, token);
        }
    }

    internal LocalHostingProgress? Read(object connection, Guid profileId)
    {
        lock (sync) return ReferenceEquals(owner, connection) && profile == profileId ? current : null;
    }

    internal sealed class Operation(LocalHostingProgressStore store, object token) : IDisposable
    {
        internal void Report(string phase, string message)
        {
            lock (store.sync)
            {
                if (!ReferenceEquals(store.operation, token) || store.current?.State != "Working") return;
                store.current = store.current with
                {
                    Phase = phase,
                    Message = message,
                    UpdatedUtc = store.time.GetUtcNow(),
                    ReceivedBytes = 0,
                    TotalBytes = 0,
                    Percent = null,
                    CompletedFiles = 0,
                    TotalFiles = 0
                };
            }
        }

        internal void Source(WorldPeerSourceProgress value)
        {
            lock (store.sync)
            {
                if (!ReferenceEquals(store.operation, token) || store.current?.State != "Working" ||
                    value.CompletedBytes < 0 || value.TotalBytes < value.CompletedBytes ||
                    value.CompletedFiles < 0 || value.TotalFiles < value.CompletedFiles) return;
                var previous = store.current;
                if (previous.Phase == value.Phase && previous.Message == value.Message &&
                    previous.TotalBytes == value.TotalBytes && previous.TotalFiles == value.TotalFiles &&
                    (value.CompletedBytes < previous.ReceivedBytes || value.CompletedFiles < previous.CompletedFiles)) return;
                if (previous.Phase == value.Phase && previous.Message == value.Message &&
                    previous.ReceivedBytes == value.CompletedBytes && previous.TotalBytes == value.TotalBytes &&
                    previous.CompletedFiles == value.CompletedFiles && previous.TotalFiles == value.TotalFiles) return;
                store.current = previous with
                {
                    Phase = value.Phase,
                    Message = value.Message,
                    UpdatedUtc = store.time.GetUtcNow(),
                    ReceivedBytes = value.CompletedBytes,
                    TotalBytes = value.TotalBytes,
                    Percent = value.TotalBytes > 0 ? (int)(100m * value.CompletedBytes / value.TotalBytes) : null,
                    CompletedFiles = value.CompletedFiles,
                    TotalFiles = value.TotalFiles
                };
            }
        }

        internal void Transfer(long received, long total)
        {
            lock (store.sync)
            {
                if (!ReferenceEquals(store.operation, token) || store.current?.State != "Working" ||
                    received < 0 || total <= 0 || received > total) return;
                var previous = store.current;
                if (previous.Phase == "Receiving" && previous.TotalBytes == total)
                    received = Math.Max(received, previous.ReceivedBytes);
                store.current = previous with
                {
                    Phase = "Receiving",
                    Message = "Receiving the completed shared save.",
                    UpdatedUtc = store.time.GetUtcNow(),
                    ReceivedBytes = received,
                    TotalBytes = total,
                    Percent = (int)(100m * received / total),
                    CompletedFiles = 0,
                    TotalFiles = 0
                };
            }
        }

        internal void Complete(bool ok, string message)
        {
            lock (store.sync)
            {
                if (!ReferenceEquals(store.operation, token) || store.current?.State != "Working") return;
                store.current = store.current with
                {
                    State = ok ? "Completed" : "Failed",
                    Message = message,
                    UpdatedUtc = store.time.GetUtcNow(),
                    Percent = null
                };
            }
        }

        public void Dispose() => Complete(false, "Hosting was canceled or interrupted. Existing saves were kept.");
    }
}

internal sealed partial class FriendLocalHostingService
{
    private readonly LocalHostingProgressStore hostingProgress = new();

    internal LocalHostingProgress? Progress(Guid profileId)
    {
        using var connection = friend.RetainLocalHostingConnection();
        return connection is not null && friend.CurrentLocalHostingConnection(connection)
            ? hostingProgress.Read(connection.Link, profileId) : null;
    }
}

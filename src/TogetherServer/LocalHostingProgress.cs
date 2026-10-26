namespace TogetherServer;

public sealed record LocalHostingPhaseTiming(string Phase, long ElapsedMilliseconds);

public sealed record LocalHostingProgress(string Phase, string State, string Message,
    DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, long ReceivedBytes,
    long TotalBytes, int? Percent, int CompletedFiles = 0, int TotalFiles = 0,
    IReadOnlyList<LocalHostingPhaseTiming>? PhaseTimings = null);

// A display-only observation of the one local mutation. It never drives a
// lifecycle decision or persists an instruction to resume work after restart.
internal sealed class LocalHostingProgressStore(TimeProvider? clock = null)
{
    internal const long MaximumPhaseMilliseconds = 24 * 60 * 60 * 1000;
    private static readonly string[] TimedPhases =
        ["Permission", "PeerSync", "LatestSave", "Receiving", "Verifying", "Receipt", "PreparingCopy",
            "ServerSetup", "Ports", "Connection", "Starting", "Other"];
    private readonly object sync = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, long> phaseTicks = new(StringComparer.Ordinal);
    private string timedPhase = "Permission";
    private long timingTimestamp;
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
            phaseTicks.Clear();
            timedPhase = "Permission";
            phaseTicks.Add(timedPhase, 0);
            timingTimestamp = time.GetTimestamp();
            current = new("Permission", "Working", "Checking the owner's current hosting permission.", now, now, 0, 0, null,
                PhaseTimings: [new(timedPhase, 0)]);
            return new(this, token);
        }
    }

    internal LocalHostingProgress? Read(object connection, Guid profileId)
    {
        lock (sync) return ReferenceEquals(owner, connection) && profile == profileId ? current : null;
    }

    // Checkpoint elapsed phase time only with accepted work reports or the
    // terminal result. Passive reads cannot add time or renew UpdatedUtc.
    // These timings describe the operation's waits/work, never readiness or
    // completion evidence. In particular Starting ends at the launch result.
    private IReadOnlyList<LocalHostingPhaseTiming> TimingCheckpoint(string nextPhase)
    {
        var timestamp = time.GetTimestamp();
        var elapsedTicks = Math.Clamp(time.GetElapsedTime(timingTimestamp, timestamp).Ticks,
            0, MaximumPhaseMilliseconds * TimeSpan.TicksPerMillisecond);
        phaseTicks[timedPhase] = Math.Min(MaximumPhaseMilliseconds * TimeSpan.TicksPerMillisecond,
            phaseTicks[timedPhase] + elapsedTicks);
        timingTimestamp = timestamp;
        timedPhase = TimedPhases.Contains(nextPhase, StringComparer.Ordinal) ? nextPhase : "Other";
        phaseTicks.TryAdd(timedPhase, 0);
        return TimedPhases.Where(phaseTicks.ContainsKey).Select(phase =>
            new LocalHostingPhaseTiming(phase, phaseTicks[phase] / TimeSpan.TicksPerMillisecond)).ToArray();
    }

    internal sealed class Operation(LocalHostingProgressStore store, object token) : IDisposable
    {
        internal void Report(string phase, string message)
        {
            lock (store.sync)
            {
                if (!ReferenceEquals(store.operation, token) || store.current?.State != "Working") return;
                if (store.current.Phase == phase && store.current.Message == message &&
                    store.current.ReceivedBytes == 0 && store.current.TotalBytes == 0 &&
                    store.current.CompletedFiles == 0 && store.current.TotalFiles == 0) return;
                store.current = store.current with
                {
                    Phase = phase,
                    Message = message,
                    UpdatedUtc = store.time.GetUtcNow(),
                    ReceivedBytes = 0,
                    TotalBytes = 0,
                    Percent = null,
                    CompletedFiles = 0,
                    TotalFiles = 0,
                    PhaseTimings = store.TimingCheckpoint(phase)
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
                    TotalFiles = value.TotalFiles,
                    PhaseTimings = store.TimingCheckpoint(value.Phase)
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
                if (previous.Phase == "Receiving" && previous.ReceivedBytes == received && previous.TotalBytes == total &&
                    previous.Message == "Receiving the completed shared save." &&
                    previous.CompletedFiles == 0 && previous.TotalFiles == 0) return;
                store.current = previous with
                {
                    Phase = "Receiving",
                    Message = "Receiving the completed shared save.",
                    UpdatedUtc = store.time.GetUtcNow(),
                    ReceivedBytes = received,
                    TotalBytes = total,
                    Percent = (int)(100m * received / total),
                    CompletedFiles = 0,
                    TotalFiles = 0,
                    PhaseTimings = store.TimingCheckpoint("Receiving")
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
                    Percent = null,
                    PhaseTimings = store.TimingCheckpoint(store.current.Phase)
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

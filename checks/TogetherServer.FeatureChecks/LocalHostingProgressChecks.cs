using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class LocalHostingProgressChecks
{
    internal static Task RunAsync()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var store = new LocalHostingProgressStore();
        var connection = new object();
        var otherConnection = new object();
        var profile = Guid.NewGuid();
        Require(store.Read(connection, profile) is null, "Reading idle progress created an operation.");
        using var first = store.Begin(connection, profile);
        Require(store.Read(otherConnection, profile) is null && store.Read(connection, Guid.NewGuid()) is null,
            "A different connection or world could read another operation's progress.");
        first.Transfer(250, 1000);
        Require(store.Read(connection, profile) is { State: "Working", Phase: "Receiving", Percent: 25 },
            "Real received bytes did not produce transfer progress.");
        first.Transfer(100, 1000);
        first.Transfer(2000, 1000);
        first.Transfer(-1, 1000);
        Require(store.Read(connection, profile) is { ReceivedBytes: 250, Percent: 25 },
            "Regressing or impossible byte updates fabricated progress.");
        first.Transfer(1000, 1000);
        Require(store.Read(connection, profile) is { State: "Working", Percent: 100 },
            "Finishing transfer incorrectly completed hosting preparation.");
        first.Report("Verifying", "Checking file hashes.");
        Require(store.Read(connection, profile) is { State: "Working", Percent: null, TotalBytes: 0 },
            "An unmeasured verification phase inherited a transfer percentage.");
        using var second = store.Begin(otherConnection, profile);
        first.Transfer(1000, 1000);
        first.Complete(true, "Old result");
        Require(store.Read(connection, profile) is null &&
            store.Read(otherConnection, profile) is { State: "Working", Phase: "Permission", Percent: null },
            "A late old-connection result replaced a new operation's progress.");
        second.Transfer(long.MaxValue / 2, long.MaxValue);
        Require(store.Read(otherConnection, profile)?.Percent == 49,
            "Large valid byte totals overflowed transfer progress.");
        second.Dispose();
        second.Transfer(1, 1);
        second.Complete(true, "Late result");
        Require(store.Read(otherConnection, profile) is { State: "Failed", Percent: null },
            "Cancellation or a late callback claimed hosting completion.");
        using var completed = store.Begin(connection, profile);
        completed.Complete(true, "Prepared");
        completed.Dispose();
        Require(store.Read(connection, profile) is { State: "Completed", Percent: null },
            "Disposal rewrote a completed result or fabricated an overall percentage.");
        Timings();
        return Task.CompletedTask;
    }

    private static void Timings()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static long Milliseconds(LocalHostingProgress? value, string phase) =>
            value?.PhaseTimings?.SingleOrDefault(item => item.Phase == phase)?.ElapsedMilliseconds ?? -1;
        var clock = new ProgressClock();
        var store = new LocalHostingProgressStore(clock);
        var connection = new object();
        var profile = Guid.NewGuid();
        using var operation = store.Begin(connection, profile);
        var began = store.Read(connection, profile);
        Require(began?.PhaseTimings is { Count: 1 } && Milliseconds(began, "Permission") == 0,
            "An explicit operation did not start with its own zero timing.");
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(2));
        operation.Report("PreparingCopy", "Preparing files.");
        var enteredCopy = store.Read(connection, profile);
        Require(Milliseconds(enteredCopy, "Permission") == 2000 && Milliseconds(enteredCopy, "PreparingCopy") == 0,
            "Phase transition timing used wall-clock subtraction or the wrong phase.");
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(3));
        var copying = new WorldPeerSourceProgress("PreparingCopy", "Preparing files.", 100, 1000, 1, 10);
        operation.Source(copying);
        var observedCopy = store.Read(connection, profile);
        Require(Milliseconds(observedCopy, "PreparingCopy") == 3000 &&
            Milliseconds(enteredCopy, "PreparingCopy") == 0,
            "An accepted copy observation did not checkpoint time or mutated its earlier snapshot.");
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(4));
        var timestampReads = clock.TimestampReads;
        Require(ReferenceEquals(store.Read(connection, profile), observedCopy) && clock.TimestampReads == timestampReads,
            "Passive polling read the timing clock or changed its stored observation.");
        operation.Source(copying);
        operation.Source(copying with { CompletedBytes = 99 });
        Require(ReferenceEquals(store.Read(connection, profile), observedCopy) && clock.TimestampReads == timestampReads,
            "Repeated or regressing copy callbacks added phase time or renewed progress.");
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        operation.Report("Verifying", "Checking file hashes.");
        Require(Milliseconds(store.Read(connection, profile), "PreparingCopy") == 8000,
            "The next actual observation lost elapsed time after passive polls.");
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        operation.Report("PreparingCopy", "Preparing more files.");
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(6));
        operation.Report("Starting", "Waiting for the launch request result.");
        var starting = store.Read(connection, profile);
        Require(Milliseconds(starting, "PreparingCopy") == 14000 && Milliseconds(starting, "Verifying") == 5000 &&
            Milliseconds(starting, "Starting") == 0,
            "Returning to a phase replaced its accumulated duration or mixed verification and launch time.");
        clock.MoveWallClock(TimeSpan.FromDays(3));
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(7));
        operation.Dispose();
        var canceled = store.Read(connection, profile);
        Require(canceled is { State: "Failed" } && Milliseconds(canceled, "Starting") == 7000 &&
            Milliseconds(canceled, "PreparingCopy") == 14000,
            "Cancellation failed to freeze the final monotonic interval independently of UTC.");
        clock.AdvanceMonotonic(TimeSpan.FromHours(1));
        operation.Report("Receiving", "Late callback.");
        operation.Transfer(100, 100);
        operation.Source(copying);
        operation.Complete(true, "Late result.");
        Require(ReferenceEquals(store.Read(connection, profile), canceled),
            "Polling or a late callback changed terminal phase timings.");

        using var reset = store.Begin(connection, profile);
        var resetProgress = store.Read(connection, profile);
        Require(resetProgress?.PhaseTimings is { Count: 1 } && Milliseconds(resetProgress, "Permission") == 0,
            "A separate explicit operation retained previous phase time.");
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        reset.Report("Permission", resetProgress!.Message);
        Require(ReferenceEquals(store.Read(connection, profile), resetProgress),
            "A duplicate zero-work phase report renewed its observation.");
        reset.Transfer(250, 1000);
        var transfer = store.Read(connection, profile);
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(10));
        reset.Transfer(250, 1000);
        reset.Transfer(100, 1000);
        Require(ReferenceEquals(store.Read(connection, profile), transfer),
            "Duplicate or regressing transfer bytes added time or renewed progress.");
        reset.Source(new("Verifying", "Checking empty files.", 0, 0, 0, 2));
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(2));
        reset.Source(new("Verifying", "Checking empty files.", 0, 0, 1, 2));
        Require(Milliseconds(store.Read(connection, profile), "Receiving") == 10000 &&
            Milliseconds(store.Read(connection, profile), "Verifying") == 2000,
            "A real zero-byte file advancement failed to checkpoint its separate verification time.");
        var anotherConnection = new object();
        var anotherProfile = Guid.NewGuid();
        using var scoped = store.Begin(anotherConnection, anotherProfile);
        reset.Complete(true, "Stale operation.");
        Require(store.Read(connection, profile) is null && store.Read(anotherConnection, profile) is null &&
            store.Read(anotherConnection, anotherProfile)?.PhaseTimings is { Count: 1 } &&
            Milliseconds(store.Read(anotherConnection, anotherProfile), "Permission") == 0,
            "Phase timings escaped their connection/profile/operation scope.");
        for (var index = 0; index < 30; index++)
            scoped.Report("Unrecognized-" + index, "Unrecognized phase.");
        clock.AdvanceMonotonic(TimeSpan.FromDays(50));
        scoped.Complete(true, "Prepared; readiness remains separate.");
        var bounded = store.Read(anotherConnection, anotherProfile);
        Require(bounded is { State: "Completed", PhaseTimings.Count: 2 } &&
            Milliseconds(bounded, "Other") == LocalHostingProgressStore.MaximumPhaseMilliseconds,
            "Unrecognized phases or a long monotonic gap produced unbounded timing metadata.");
        scoped.Dispose();
        Require(ReferenceEquals(store.Read(anotherConnection, anotherProfile), bounded),
            "Disposal rewrote completed phase timing results.");
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new
        {
            phase = "Permission",
            state = "Working",
            message = "Legacy",
            startedUtc = clock.GetUtcNow(),
            updatedUtc = clock.GetUtcNow(),
            receivedBytes = 0,
            totalBytes = 0,
            percent = (int?)null
        });
        Require(JsonSerializer.Deserialize<LocalHostingProgress>(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = true }) is { PhaseTimings: null, CompletedFiles: 0, TotalFiles: 0 },
            "Legacy progress JSON requires optional timing metadata.");
    }

    private sealed class ProgressClock : TimeProvider
    {
        private DateTimeOffset utc = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
        private long timestamp;
        internal int TimestampReads { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long GetTimestamp() { TimestampReads++; return timestamp; }
        internal void AdvanceMonotonic(TimeSpan elapsed) => timestamp += elapsed.Ticks;
        internal void MoveWallClock(TimeSpan elapsed) => utc += elapsed;
    }
}

using System.Diagnostics;

namespace TogetherServer;

internal enum HostingWorkStage { Inventory, SignedRegistry, PeerRequest, PayloadVerification, WorkingVerification, PeerWait }
internal enum PeerMembershipPassOutcome { None, WaitingForGate, Refreshing, Completed, TimedOut, Canceled, Failed }
internal enum PeerMembershipLookupOutcome
{
    None, Started, NotPaired, UnknownProfile, UpdateRequired, RosterUnavailable,
    EnrollmentDenied, InvalidChallenge, KeyReviewRequired, SigningIdentityChanged,
    RosterCatchUpPending, RosterRollback, RosterRejected, CanManage, PermissionDenied,
    TransportFailure, InvalidData, Canceled, Other
}
public sealed record HostingWorkMeasurement(string Stage, long Count, long ElapsedMilliseconds);
public sealed record HostingMembershipDiagnostic(long PassesStarted, long PassesCompleted,
    string PassOutcome, long PassElapsedMilliseconds, long LookupsStarted, long LookupsCompleted,
    string LookupOutcome, bool LookupReturnedRoster);
public sealed record HostingWorkSnapshot(long UptimeMilliseconds, long ProcessCpuMilliseconds,
    IReadOnlyList<HostingWorkMeasurement> Work, HostingMembershipDiagnostic? Membership = null);

// Small process-lifetime counters. Wall durations can overlap and must not be
// added into CPU time. CPU utilization requires differences between two reads.
internal sealed class HostingWorkCounters
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly long[] counts = new long[Enum.GetValues<HostingWorkStage>().Length];
    private readonly long[] ticks = new long[Enum.GetValues<HostingWorkStage>().Length];
    internal IDisposable Measure(HostingWorkStage stage) => new Measurement(this, (int)stage);
    private sealed class Measurement(HostingWorkCounters owner, int stage) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private int finished;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            Interlocked.Increment(ref owner.counts[stage]);
            Interlocked.Add(ref owner.ticks[stage], Stopwatch.GetTimestamp() - started);
        }
    }
    internal HostingWorkMeasurement Read(HostingWorkStage stage) => new(stage.ToString(),
        Interlocked.Read(ref counts[(int)stage]),
        (long)(Interlocked.Read(ref ticks[(int)stage]) * 1000d / Stopwatch.Frequency));

    internal HostingWorkSnapshot Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        return new((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            (long)process.TotalProcessorTime.TotalMilliseconds,
            Enum.GetValues<HostingWorkStage>().Select(Read).ToArray());
    }
}

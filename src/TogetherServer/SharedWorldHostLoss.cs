using System.Diagnostics;

namespace TogetherServer;

// Only failed attempts to reach the pinned, authenticated Host start the
// timeout. A response of any kind proves that the address is reachable and
// conservatively resets it. The timer is local and monotonic; wall clocks
// never select a save head or establish a quorum.
internal enum HostReachabilityObservation { Authenticated, OtherResponse, TransportFailure }

internal sealed class SharedWorldHostLoss(
    Func<long>? timestamp = null, long? timestampFrequency = null)
{
    internal static readonly TimeSpan RequiredDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan FreshFailureWindow = TimeSpan.FromSeconds(30);
    private readonly Func<long> now = timestamp ?? Stopwatch.GetTimestamp;
    private readonly long frequency = timestampFrequency ?? Stopwatch.Frequency;
    private readonly object sync = new();
    private long? firstFailure;
    private long? lastFailure;

    internal void Observe(HostReachabilityObservation observation)
    {
        lock (sync)
        {
            if (observation == HostReachabilityObservation.TransportFailure)
            {
                var stamp = now();
                firstFailure ??= stamp;
                lastFailure = stamp;
            }
            else
            {
                firstFailure = null;
                lastFailure = null;
            }
        }
    }

    internal bool MayPropose
    {
        get
        {
            lock (sync)
            {
                if (firstFailure is not { } start || lastFailure is not { } last ||
                    frequency <= 0) return false;
                var stamp = now();
                var elapsed = stamp - start;
                var sinceLast = stamp - last;
                return elapsed >= 0 && sinceLast >= 0 &&
                    (double)elapsed / frequency >= RequiredDelay.TotalSeconds &&
                    (double)sinceLast / frequency <= FreshFailureWindow.TotalSeconds;
            }
        }
    }

    internal TimeSpan? UnreachableFor
    {
        get
        {
            lock (sync)
            {
                if (firstFailure is not { } start || frequency <= 0) return null;
                var elapsed = now() - start;
                return elapsed < 0 ? null : TimeSpan.FromSeconds((double)elapsed / frequency);
            }
        }
    }
}

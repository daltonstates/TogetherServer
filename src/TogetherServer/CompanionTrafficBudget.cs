using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace TogetherServer;

// Transfer requests share the existing aggregate ceilings with controls, but
// cannot consume their reserved remainder. Route metadata is supplied by the
// application router; request paths, payload operations and display names are
// never trusted as traffic classifications.
internal static class CompanionTrafficBudget
{
    internal const int DeviceTotal = 180;
    internal const int DeviceBulk = 140;
    internal const int HostTotal = 600;
    internal const int HostBulk = 480;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const string SharedWorld = "/api/companion/servers/{profileId:guid}/shared-world";
    private const string Peers = "/api/companion/world-peers/";

    internal static bool IsBulkTransfer(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint { RoutePattern.RawText: { } route }) return false;
        return route == SharedWorld || route.StartsWith(SharedWorld + "/", StringComparison.Ordinal) ||
            route.StartsWith(Peers, StringComparison.Ordinal);
    }

    internal static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(TimeProvider? clock = null)
    {
        var state = new CompanionTrafficWindow(HostTotal, HostBulk, clock ?? TimeProvider.System);
        // Both partitions use this one aligned window and debit both counters
        // atomically. Separate replenishment timers or chained acquisition
        // stages could allow bulk to steal the reserve at a window boundary.
        return PartitionedRateLimiter.Create<HttpContext, bool>(context =>
            RateLimitPartition.Get(IsBulkTransfer(context), bulk => new TrafficLimiter(state, bulk)));
    }

    private sealed class TrafficLimiter(CompanionTrafficWindow state, bool bulk) : RateLimiter
    {
        private bool disposed;
        public override TimeSpan? IdleDuration => null; // Only two fixed application partitions exist.
        public override RateLimiterStatistics? GetStatistics() => state.Statistics(bulk);
        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return state.TryAcquire(bulk, permitCount, out var retry) ? TrafficLease.Accepted : new TrafficLease(retry);
        }
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested ? ValueTask.FromCanceled<RateLimitLease>(cancellationToken) :
            ValueTask.FromResult(AttemptAcquireCore(permitCount));
        protected override void Dispose(bool disposing) => disposed = true;
    }

    private sealed class TrafficLease : RateLimitLease
    {
        internal static readonly TrafficLease Accepted = new();
        private readonly TimeSpan? retry;
        private TrafficLease() { }
        internal TrafficLease(TimeSpan retry) => this.retry = retry;
        public override bool IsAcquired => retry is null;
        public override IEnumerable<string> MetadataNames => retry is null ? [] : [MetadataName.RetryAfter.Name];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == MetadataName.RetryAfter.Name ? retry : null;
            return metadata is not null;
        }
    }
}

// Called only after current device authentication. A rejected bulk request
// never increments the aggregate count or consumes an interactive permit.
internal sealed class AuthenticatedDeviceTrafficBudget(TimeProvider? clock = null)
{
    private readonly object sync = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<Guid, CompanionTrafficWindow> devices = [];
    private int calls;

    internal bool TryAcquire(Guid deviceId, bool bulk)
    {
        lock (sync)
        {
            if (++calls % 256 == 0)
                foreach (var key in devices.Where(item => item.Value.Stale).Select(item => item.Key).ToArray())
                    devices.Remove(key);
            if (!devices.TryGetValue(deviceId, out var state))
                devices.Add(deviceId, state = new(CompanionTrafficBudget.DeviceTotal, CompanionTrafficBudget.DeviceBulk, time));
            return state.TryAcquire(bulk, 1, out _);
        }
    }
}

internal sealed class CompanionTrafficWindow(int totalLimit, int bulkLimit, TimeProvider time)
{
    private readonly object sync = new();
    private bool began;
    private long started;
    private int total, bulk;
    private long successes, failures;

    private TimeSpan Refresh()
    {
        var now = time.GetTimestamp();
        var elapsed = began ? time.GetElapsedTime(started, now) : TimeSpan.Zero;
        if (!began || elapsed >= CompanionTrafficBudget.Window)
        {
            began = true;
            started = now;
            total = bulk = 0;
            elapsed = TimeSpan.Zero;
        }
        return CompanionTrafficBudget.Window - elapsed;
    }

    internal bool Stale
    {
        get { lock (sync) return began && time.GetElapsedTime(started) >= CompanionTrafficBudget.Window + CompanionTrafficBudget.Window; }
    }

    internal bool TryAcquire(bool isBulk, int permits, out TimeSpan retry)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(permits);
        lock (sync)
        {
            retry = Refresh();
            var available = Math.Min(totalLimit - total, isBulk ? bulkLimit - bulk : totalLimit);
            if (permits == 0 ? available <= 0 : permits > available)
            {
                failures++;
                return false;
            }
            total += permits;
            if (isBulk) bulk += permits;
            successes++;
            return true;
        }
    }

    internal RateLimiterStatistics Statistics(bool isBulk)
    {
        lock (sync)
        {
            _ = Refresh();
            return new()
            {
                CurrentAvailablePermits = Math.Min(totalLimit - total, isBulk ? bulkLimit - bulk : totalLimit),
                CurrentQueuedCount = 0,
                TotalSuccessfulLeases = successes,
                TotalFailedLeases = failures
            };
        }
    }
}

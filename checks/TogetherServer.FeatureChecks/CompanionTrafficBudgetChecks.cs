using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Actual production budget factories with synthetic routing metadata and time.
// No application, listener, process, credential, game or network is used.
internal static class CompanionTrafficBudgetChecks
{
    private const string Shared = "/api/companion/servers/{profileId:guid}/shared-world";
    private const string Chunk = Shared + "/{versionHash}/files/{fileIndex:int}/chunks/{offset:long}";
    private const string Peer = "/api/companion/world-peers/{groupKey}/exchange";
    private const string Control = "/api/companion/stop";

    internal static async Task RunAsync()
    {
        TrustedEndpointClassification();
        DeviceReserveAndAggregate();
        DeviceAlignedRollover();
        await GlobalReserveAndAggregateAsync();
        GlobalAlignedRollover();
        await ConcurrentGlobalDebitsAsync();
    }

    private static void TrustedEndpointClassification()
    {
        foreach (var pattern in new[] { Shared, Chunk, Shared + "/roster/revisions/current", Shared + "/authority",
            Shared + "/versions/{number:long}", Shared + "/receipts", Peer,
            "/api/companion/world-peers/{groupKey}/challenge" })
        {
            var context = Context(pattern);
            context.Request.Path = "/api/companion/start";
            context.Request.Headers["X-Traffic-Class"] = "control";
            Require(CompanionTrafficBudget.IsBulkTransfer(context),
                "a fixed original-Host or peer-transfer endpoint did not use the bulk budget");
        }
        foreach (var pattern in new[] { "/api/companion/start", Control, "/api/companion/restart", "/api/companion/replace",
            "/api/companion/heartbeat", "/api/companion/status", "/api/companion/operations/{operationId:guid}",
            "/api/companion/credential/renew", "/api/companion/servers/{profileId:guid}/logs",
            "/api/companion/servers/{profileId}/shared-world", Shared + "-other/chunks",
            "/api/companion/world-peers-other/{groupKey}/exchange", "/api/companion/world-peers",
            "/api/companion/unknown", "/unknown" })
        {
            var context = Context(pattern);
            context.Request.Path = "/api/companion/world-peers/requested/exchange";
            context.Request.QueryString = new("?operation=Chunk&name=shared-world");
            context.Request.Headers["X-Traffic-Class"] = "bulk";
            Require(!CompanionTrafficBudget.IsBulkTransfer(context),
                "a control, unknown or malformed endpoint was classified from an untrusted request path or group-prefix lookalike");
        }
        var unknown = new DefaultHttpContext();
        unknown.Request.Path = "/api/companion/world-peers/requested/exchange";
        Require(!CompanionTrafficBudget.IsBulkTransfer(unknown), "an unrouted request supplied its own bulk classification");
        unknown.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), Shared));
        Require(!CompanionTrafficBudget.IsBulkTransfer(unknown), "an endpoint display name replaced trusted route metadata");
    }

    private static void DeviceReserveAndAggregate()
    {
        var budget = new AuthenticatedDeviceTrafficBudget(new Clock());
        var device = Guid.NewGuid();
        for (var index = 0; index < 140; index++) Require(budget.TryAcquire(device, true), "bulk device quota rejected an allowed request");
        for (var index = 0; index < 1000; index++) Require(!budget.TryAcquire(device, true), "bulk device requests entered the control reserve");
        for (var index = 0; index < 40; index++) Require(budget.TryAcquire(device, false), "rejected bulk requests consumed a reserved control permit");
        Require(!budget.TryAcquire(device, false) && !budget.TryAcquire(device, true), "the original device total180 ceiling increased");
        Require(budget.TryAcquire(Guid.NewGuid(), true) && budget.TryAcquire(Guid.NewGuid(), false), "one verified device exhausted another device's budget");
        var controls = Guid.NewGuid();
        for (var index = 0; index < 180; index++) Require(budget.TryAcquire(controls, false), "control-only device traffic lost its original quota");
        Require(!budget.TryAcquire(controls, false) && !budget.TryAcquire(controls, true), "control traffic bypassed the aggregate device ceiling");
        var mixed = Guid.NewGuid();
        for (var index = 0; index < 100; index++) Require(budget.TryAcquire(mixed, false), "mixed controls were rejected early");
        for (var index = 0; index < 80; index++) Require(budget.TryAcquire(mixed, true), "mixed bulk was rejected before the total ceiling");
        Require(!budget.TryAcquire(mixed, true) && !budget.TryAcquire(mixed, false), "separate device classes increased aggregate capacity");
    }

    private static void DeviceAlignedRollover()
    {
        var clock = new Clock();
        var budget = new AuthenticatedDeviceTrafficBudget(clock);
        var device = Guid.NewGuid();
        Require(budget.TryAcquire(device, false), "initial control request failed");
        clock.Advance(TimeSpan.FromSeconds(59));
        for (var index = 0; index < 139; index++) Require(budget.TryAcquire(device, true), "first delayed bulk quota failed");
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var index = 0; index < 140; index++) Require(budget.TryAcquire(device, true), "bulk quota did not align with the reset total window");
        clock.Advance(TimeSpan.FromSeconds(59));
        Require(!budget.TryAcquire(device, true), "a later bulk-only replenishment stole the total window's control reserve");
        for (var index = 0; index < 40; index++) Require(budget.TryAcquire(device, false), "aligned device reset lost reserved controls");
        clock.MoveWall(TimeSpan.FromDays(7));
        Require(!budget.TryAcquire(device, false), "a wall-clock jump reset the monotonic device quota");
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(budget.TryAcquire(device, false), "the aligned next device window did not reset");
    }

    private static async Task GlobalReserveAndAggregateAsync()
    {
        using var limiter = CompanionTrafficBudget.CreateGlobalLimiter(new Clock());
        var chunk = Context(Chunk);
        var peer = Context(Peer);
        var control = Context(Control);
        for (var index = 0; index < 480; index++) Require(Acquire(limiter, index % 2 == 0 ? chunk : peer), "global bulk quota rejected an allowed endpoint");
        for (var index = 0; index < 1000; index++) Require(!Acquire(limiter, chunk), "bulk requests entered the host control reserve");
        var rejected = limiter.AcquireAsync(peer);
        Require(rejected.IsCompletedSuccessfully, "a saturated bulk request was queued instead of rejected");
        using (var lease = await rejected) Require(!lease.IsAcquired, "a saturated asynchronous bulk request was accepted");
        Require(limiter.GetStatistics(control)?.CurrentAvailablePermits == 120,
            "rejected original-Host/peer bulk requests consumed aggregate permits");
        for (var index = 0; index < 120; index++) Require(Acquire(limiter, control), "bulk saturation consumed a reserved host control permit");
        Require(!Acquire(limiter, control) && !Acquire(limiter, chunk), "the existing global600 ceiling increased");
        using var onlyControls = CompanionTrafficBudget.CreateGlobalLimiter(new Clock());
        for (var index = 0; index < 600; index++) Require(Acquire(onlyControls, control), "control-only host traffic lost its original quota");
        Require(!Acquire(onlyControls, control) && !Acquire(onlyControls, peer), "control-only traffic bypassed the global aggregate ceiling");
        using var mixed = CompanionTrafficBudget.CreateGlobalLimiter(new Clock());
        for (var index = 0; index < 200; index++) Require(Acquire(mixed, control), "mixed host controls were rejected early");
        for (var index = 0; index < 400; index++) Require(Acquire(mixed, chunk), "mixed host bulk was rejected before the total ceiling");
        Require(!Acquire(mixed, chunk) && !Acquire(mixed, control), "mixed host traffic exceeded the unchanged total ceiling");
    }

    private static void GlobalAlignedRollover()
    {
        var clock = new Clock();
        using var limiter = CompanionTrafficBudget.CreateGlobalLimiter(clock);
        var control = Context(Control);
        var chunk = Context(Chunk);
        Require(Acquire(limiter, control), "initial host control failed");
        clock.Advance(TimeSpan.FromSeconds(59));
        for (var index = 0; index < 479; index++) Require(Acquire(limiter, chunk), "initial delayed host bulk failed");
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var index = 0; index < 480; index++) Require(Acquire(limiter, chunk), "host bulk reset did not align with the total window");
        clock.Advance(TimeSpan.FromSeconds(59));
        Require(!Acquire(limiter, chunk), "phase-offset host bulk replenishment stole reserved controls");
        for (var index = 0; index < 120; index++) Require(Acquire(limiter, control), "aligned host reset lost reserved controls");
        Require(!Acquire(limiter, control), "aligned host counters exceeded600");
        clock.MoveWall(TimeSpan.FromDays(-7));
        Require(!Acquire(limiter, control), "wall-clock rollback reset the host budget");
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(Acquire(limiter, control) && Acquire(limiter, chunk), "aligned host window did not resume both classes");
    }

    private static async Task ConcurrentGlobalDebitsAsync()
    {
        using var limiter = CompanionTrafficBudget.CreateGlobalLimiter(new Clock());
        var bulk = await Task.WhenAll(Enumerable.Range(0, 800).Select(_ => Task.Run(() => Acquire(limiter, Context(Peer)))));
        var controls = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => Acquire(limiter, Context(Control)))));
        Require(bulk.Count(value => value) == 480 && controls.Count(value => value) == 120 && !Acquire(limiter, Context(Control)),
            "concurrent acquisitions leaked the host aggregate ceiling or reserved control permits");
        using var interleaved = CompanionTrafficBudget.CreateGlobalLimiter(new Clock());
        var mixed = await Task.WhenAll(Enumerable.Range(0, 1600).Select(index => Task.Run(() =>
            (Bulk: index % 2 == 0, Acquired: Acquire(interleaved, Context(index % 2 == 0 ? Chunk : Control))))));
        Require(mixed.Count(value => value.Acquired) == 600 && mixed.Count(value => value.Acquired && value.Bulk) <= 480 &&
            mixed.Count(value => value.Acquired && !value.Bulk) >= 120,
            "interleaved bulk/control acquisitions did not atomically respect both counters");
    }

    private static bool Acquire(PartitionedRateLimiter<HttpContext> limiter, HttpContext context)
    {
        using var lease = limiter.AttemptAcquire(context);
        return lease.IsAcquired;
    }

    private static DefaultHttpContext Context(string route)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(route), 0,
            new EndpointMetadataCollection(), "Synthetic traffic endpoint"));
        return context;
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        private DateTimeOffset utc = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => utc;
        internal void Advance(TimeSpan amount) { ticks += amount.Ticks; utc += amount; }
        internal void MoveWall(TimeSpan amount) => utc += amount;
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

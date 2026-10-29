using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class WorldPeerTransferObservationChecks
{
    internal static Task RunAsync()
    {
        PassiveSnapshotsAndRetainedFailure();
        LocalStagingIsDistinct();
        ReceiptIsExplicitAndHistorical();
        StaleAttemptsAndBounds();
        CapacityAndPrivacy();
        return Task.CompletedTask;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void PassiveSnapshotsAndRetainedFailure()
    {
        var clock = new ObservationClock();
        var store = new WorldPeerTransferObservationStore(clock);
        Require(store.Read("world", "route") is null && store.ReadWorld("world").Count == 0 && clock.UtcReads == 0,
            "Idle reads created an observation or read its mutation clock.");
        using var attempt = store.Begin("world", "route");
        attempt.Phase(WorldPeerTransferPhase.Pulling);
        attempt.Request(WorldPeerTransferOperation.Catalog, WorldPeerTransferHttpStep.Exchange);
        attempt.Response(503);
        var active = store.Read("world", "route")!.Current!;
        var utcReads = clock.UtcReads;
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(3));
        clock.MoveWallClock(TimeSpan.FromDays(-1));
        var polled = store.ReadWorld("world").Single().Current!;
        Require(polled.UpdatedUtc == active.UpdatedUtc && polled.StartedUtc == active.StartedUtc &&
            polled.ElapsedMilliseconds == 3000 && active.ElapsedMilliseconds == 0 && clock.UtcReads == utcReads,
            "Polling renewed mutation timestamps, mutated an earlier snapshot or measured elapsed time using UTC.");
        attempt.Fail(WorldPeerTransferError.HttpRejected);
        var failure = store.Read("world", "route")!.LastFailure!;
        Require(failure is
        {
            State: "Failed", Phase: "Pulling", Operation: "Catalog", HttpStep: "Exchange", HttpStatus: 503,
            Result: "Failed", Error: "HttpRejected", ElapsedMilliseconds: 3000
        } &&
            store.Read("world", "route")!.Current is null,
            "A terminal catalog failure lost its exact fixed request boundary.");
        clock.AdvanceMonotonic(TimeSpan.FromHours(1));
        utcReads = clock.UtcReads;
        Require(store.Read("world", "route")!.LastFailure == failure && clock.UtcReads == utcReads,
            "A terminal poll renewed or changed its stored failure.");
        using var metadata = store.Begin("world", "route");
        metadata.Phase(WorldPeerTransferPhase.Metadata);
        metadata.Response(200);
        Require(store.Read("world", "route")!.LastFailure == failure,
            "A new active attempt erased the prior payload failure.");
        metadata.Complete(WorldPeerTransferResult.NoPayload);
        var later = store.Read("world", "route")!;
        Require(later is { Current: null, LastResult.State: "Pending", LastResult.Result: "NoPayload" } &&
            later.LastFailure == failure,
            "A later metadata/no-payload result erased the retained catalog failure or claimed a receipt.");
        metadata.Dispose();
        attempt.Response(200);
        Require(store.Read("world", "route") == later,
            "Disposal or a stale callback changed terminal observations.");
    }

    private static void ReceiptIsExplicitAndHistorical()
    {
        var store = new WorldPeerTransferObservationStore();
        using (var submitted = store.Begin("world", "route"))
        {
            submitted.Phase(WorldPeerTransferPhase.VerifyingRemote);
            submitted.Expected(100, 1, 6);
            submitted.Submitted(100);
            submitted.Received(100);
            submitted.FileCompleted();
            submitted.Complete(WorldPeerTransferResult.CompleteReceiptAccepted);
        }
        var codeOnly = store.Read("world", "route")!.LastResult!;
        Require(codeOnly is
        {
            State: "Pending", SubmittedBytes: 100, ReceivedBytes: 100, CompletedFiles: 1,
            ExactReceiptAccepted: false, ReceiptPublicationNumber: null, PublicationNumber: 6
        },
            "All submitted/received bytes or an accepted result code fabricated exact receipt proof.");
        using var verified = store.Begin("world", "route");
        verified.AuthenticatedRole(WorldPeerTransferRecipientRole.OriginalHost);
        verified.Expected(100, 1, 5);
        verified.ReceiptAccepted(5);
        verified.Expected(200, 2, 6);
        verified.ReceiptAccepted(4);
        verified.Complete(WorldPeerTransferResult.KnownReceiptAccepted);
        var accepted = store.Read("world", "route")!;
        Require(accepted is
        {
            RecipientRole: "OriginalHost", LastResult.State: "Succeeded",
            LastResult.ExactReceiptAccepted: true, LastResult.ReceiptPublicationNumber: 5, LastResult.PublicationNumber: 6
        },
            "Explicit verified receipt history was lost, regressed, or incorrectly attributed to the newer current descriptor.");
        verified.Expected(1, 1, 99);
        verified.ReceiptAccepted(99);
        Require(store.Read("world", "route") == accepted,
            "A completed attempt accepted later descriptor or receipt callbacks.");
    }

    private static void LocalStagingIsDistinct()
    {
        var store = new WorldPeerTransferObservationStore();
        using var attempt = store.Begin("world", "local-staging");
        attempt.Phase(WorldPeerTransferPhase.Preparing);
        attempt.Expected(4096, 4, 10);
        attempt.LocalStaged(512);
        var first = store.Read("world", "local-staging")!.Current!;
        attempt.LocalStaged(1024);
        Require(first is { Phase: "Preparing", LocalStagedBytes: 512, ReceivedBytes: 0, SubmittedBytes: 0 } &&
            store.Read("world", "local-staging")!.Current is
            {
                LocalStagedBytes: 1536, ReceivedBytes: 0,
                SubmittedBytes: 0, PublicationNumber: 10, TotalBytes: 4096, ExactReceiptAccepted: false
            },
            "Measured local staging did not advance independently of network traffic and exact receipts.");
        attempt.Complete(WorldPeerTransferResult.WorkBudget);
        var paused = store.Read("world", "local-staging")!;
        attempt.LocalStaged(999);
        Require(paused is
        {
            Current: null, LastFailure: null,
            LastResult.State: "Pending", LastResult.Result: "WorkBudget", LastResult.Error: "None",
            LastResult.LocalStagedBytes: 1536, LastResult.ExactReceiptAccepted: false
        } &&
            store.Read("world", "local-staging") == paused,
            "A bounded local-work pause became a failed transfer, receipt, or mutable finished observation.");
    }

    private static void StaleAttemptsAndBounds()
    {
        var clock = new ObservationClock();
        var store = new WorldPeerTransferObservationStore(clock);
        using var stale = store.Begin("world", "route");
        stale.Phase(WorldPeerTransferPhase.Sending);
        using var current = store.Begin("world", "route");
        current.Phase(WorldPeerTransferPhase.Pulling);
        var before = store.Read("world", "route")!;
        stale.ReceiptAccepted(3);
        stale.Submitted(999);
        stale.LocalStaged(999);
        stale.Phase(WorldPeerTransferPhase.Receipt);
        stale.Fail(WorldPeerTransferError.Unexpected);
        stale.Dispose();
        Require(store.Read("world", "route") == before && before.LastFailure is { State: "Canceled", Result: "Interrupted" },
            "A stale token changed a newer active attempt or hid the interrupted attempt.");
        current.Phase((WorldPeerTransferPhase)999);
        current.Request((WorldPeerTransferOperation)999, (WorldPeerTransferHttpStep)999);
        current.AuthenticatedRole((WorldPeerTransferRecipientRole)999);
        current.Response(999);
        current.Expected(long.MaxValue, int.MaxValue, long.MaxValue);
        current.Requested(int.MaxValue, long.MaxValue);
        current.Submitted(long.MaxValue);
        current.Submitted(long.MaxValue);
        current.Received(long.MaxValue);
        current.LocalStaged(long.MaxValue);
        current.LocalStaged(long.MaxValue);
        current.FileCompleted(int.MaxValue);
        current.Submitted(-1);
        current.Received(-1);
        current.LocalStaged(-1);
        current.FileCompleted(-1);
        current.ReceiptAccepted(0);
        clock.AdvanceMonotonic(TimeSpan.FromDays(50));
        current.Complete((WorldPeerTransferResult)999);
        var bounded = store.Read("world", "route")!;
        Require(bounded is
        {
            RecipientRole: "Unknown", LastResult.State: "Failed", LastResult.Phase: "Pulling",
            LastResult.Operation: "None", LastResult.HttpStep: "None", LastResult.HttpStatus: null,
            LastResult.Result: "Failed", LastResult.PublicationNumber: null, LastResult.ReceiptPublicationNumber: null,
            LastResult.RequestedFileIndex: null
        } &&
            bounded.LastResult.SubmittedBytes == WorldPeerTransferObservationStore.MaximumBytes &&
            bounded.LastResult.ReceivedBytes == WorldPeerTransferObservationStore.MaximumBytes &&
            bounded.LastResult.LocalStagedBytes == WorldPeerTransferObservationStore.MaximumBytes &&
            bounded.LastResult.TotalBytes == WorldPeerTransferObservationStore.MaximumBytes &&
            bounded.LastResult.RequestedOffset == WorldPeerTransferObservationStore.MaximumBytes &&
            bounded.LastResult.CompletedFiles == WorldPeerTransferObservationStore.MaximumFiles &&
            bounded.LastResult.TotalFiles == WorldPeerTransferObservationStore.MaximumFiles &&
            bounded.LastResult.ElapsedMilliseconds == WorldPeerTransferObservationStore.MaximumElapsedMilliseconds,
            "Invalid enum/numeric input escaped fixed bounded status fields or overflowed counters.");
    }

    private static void CapacityAndPrivacy()
    {
        const string world = "private-world-identity";
        const string route = "https://private-endpoint.invalid:9876/raw-route-pin";
        var store = new WorldPeerTransferObservationStore();
        var active = Enumerable.Range(0, WorldPeerTransferObservationStore.MaximumRoutes)
            .Select(index => store.Begin(world, route + index)).ToArray();
        try
        {
            using var overflow = store.Begin(world, "overflow");
            overflow.Submitted(1);
            Require(store.ReadWorld(world).Count == WorldPeerTransferObservationStore.MaximumRoutes &&
                store.Read(world, "overflow") is null &&
                store.ReadWorld(world).Select(item => item.RouteOrdinal).SequenceEqual(Enumerable.Range(1, 256)),
                "Capacity evicted active transfers, retained an extra entry, or exposed an unbounded ordinal.");
            active[0].Fail(WorldPeerTransferError.Unreachable);
            using var replacement = store.Begin(world, "replacement");
            replacement.Phase(WorldPeerTransferPhase.Offering);
            var snapshot = store.ReadWorld(world);
            Require(snapshot.Count == 256 && store.Read(world, route + 0) is null &&
                store.Read(world, "replacement")?.RouteOrdinal == 1 && store.Read(world, route + 255) is not null,
                "Terminal capacity eviction failed to retain active entries or reuse only its opaque ordinal.");
            active[0].Submitted(999);
            var serialized = JsonSerializer.Serialize(snapshot);
            Require(!serialized.Contains(world, StringComparison.Ordinal) && !serialized.Contains("private-endpoint", StringComparison.Ordinal) &&
                !serialized.Contains("raw-route-pin", StringComparison.Ordinal) && !serialized.Contains("replacement", StringComparison.Ordinal) &&
                !serialized.Contains("overflow", StringComparison.Ordinal) && store.ReadWorld("another-world").Count == 0,
                "Snapshots leaked private world/route inputs or crossed the requested world scope.");
            using var invalid = store.Begin(new string('x', 129), "route");
            invalid.Fail(WorldPeerTransferError.Unexpected);
            Require(store.ReadWorld(new string('x', 129)).Count == 0,
                "An invalid observation key consumed storage.");
        }
        finally
        {
            foreach (var attempt in active) attempt.Dispose();
        }
    }

    private sealed class ObservationClock : TimeProvider
    {
        private DateTimeOffset utc = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
        private long timestamp;
        internal int UtcReads { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { UtcReads++; return utc; }
        public override long GetTimestamp() => timestamp;
        internal void AdvanceMonotonic(TimeSpan elapsed) => timestamp += elapsed.Ticks;
        internal void MoveWallClock(TimeSpan elapsed) => utc += elapsed;
    }
}

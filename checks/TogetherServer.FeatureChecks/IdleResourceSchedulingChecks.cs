using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Disposable signed worlds, fake HTTPS, pure scheduling time and display
// values only. No application, listener, game, native or desktop is started.
internal static class IdleResourceSchedulingChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync(string parent)
    {
        PendingProgressBounds();
        await FlightOwnedCancellationAsync();
        TrayPublicationBounds();
        await UnchangedIdleAsync(parent);
        await PeerOwnedLegacyConsentAsync(parent, currentPeerOperation: false);
        await PeerOwnedLegacyConsentAsync(parent, currentPeerOperation: true);
    }

    private static async Task FlightOwnedCancellationAsync()
    {
        const string route = "world:route";
        var clock = new CompletionClock();
        var schedule = new PeerReplicationSchedule();
        var progress = new PeerPendingProgress();
        var observations = new WorldPeerTransferObservationStore(clock);
        using var stale = observations.Begin("world", "route");
        stale.Complete(WorldPeerTransferResult.SeedPaused);
        var staleResult = stale.FinishedResult!;
        for (var index = 0; index < 3; index++)
            progress.Record(schedule, route, 1, clock.GetUtcNow(), staleResult, false, 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var current = observations.Begin("world", "route");
        current.Received(1024);
        // Subsequent outgoing/publication display changes retain accepted bytes.
        current.Expected(2048, 2, 13); current.Requested(null, null);
        var active = new HashSet<string>(StringComparer.Ordinal) { route };
        var completions = 0;
        long recordedGeneration = 0;
        WorldPeerTransferProgress? recordedResult = null;
        var owner = new PeerScheduledCompletion(1, (generation, result, failed) =>
        {
            active.Remove(route);
            progress.Record(schedule, route, generation, clock.GetUtcNow(), result, failed, 1);
            recordedGeneration = generation; recordedResult = result; completions++;
        });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = Task.Run(async () =>
        {
            await release.Task;
            current.Fail(WorldPeerTransferError.Canceled, WorldPeerTransferResult.Canceled);
            owner.Complete(current.FinishedResult, failed: true);
        });
        using var canceled = new CancellationTokenSource();
        var joiningWait = actual.WaitAsync(canceled.Token);
        try
        {
            canceled.Cancel();
            try { await joiningWait; throw new InvalidOperationException("The joining wait ignored its cancellation."); }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            Require(!actual.IsCompleted && completions == 0 && current.FinishedResult is null &&
                observations.Read("world", "route") is { Current.ReceivedBytes: 1024 } view && view.LastResult == staleResult,
                "Canceling a joining wait finalized its running owner or confused its accepted bytes with the old pause snapshot.");
            Require(schedule.NextDelay(clock.GetUtcNow(), 1, active.Contains) == TimeSpan.FromSeconds(60) &&
                schedule.NextDelay(clock.GetUtcNow(), 2, active.Contains) == TimeSpan.FromSeconds(60),
                "An active owner spun due polling or changed generations created another join before completion.");
            schedule.Track("other:route", 1, clock.GetUtcNow());
            Require(schedule.NextDelay(clock.GetUtcNow(), 2, active.Contains) == TimeSpan.FromSeconds(1),
                "An active owner hid another route's due material generation.");
            schedule.RetainWorlds(new HashSet<string>(StringComparer.Ordinal) { "world" });
        }
        finally { release.TrySetResult(); await actual; }
        var now = clock.GetUtcNow();
        Require(completions == 1 && recordedGeneration == 1 && recordedResult is { ReceivedBytes: 1024, Error: "Canceled" } &&
            !schedule.Due(route, 1, now.AddSeconds(1)) && schedule.Due(route, 1, now.AddSeconds(2)) &&
            schedule.Due(route, 2, now) && schedule.NextDelay(now, 2) == TimeSpan.FromSeconds(1),
            "Actual accepted-byte completion was not recorded once, lost prompt resumption, or consumed a newer material generation.");
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(!owner.Complete(staleResult, failed: true) && completions == 1 &&
            schedule.Due(route, 1, clock.GetUtcNow().AddSeconds(1)),
            "A replayed completion renewed stale counters or moved its owner's deadline.");

        using var empty = observations.Begin("world", "route");
        var zero = new PeerScheduledCompletion(2, (generation, result, failed) =>
            progress.Record(schedule, route, generation, clock.GetUtcNow(), result, failed, 1));
        empty.Fail(WorldPeerTransferError.Canceled, WorldPeerTransferResult.Canceled);
        Require(zero.Complete(empty.FinishedResult, failed: true) && !zero.Complete(recordedResult, failed: true) &&
            !schedule.Due(route, 2, clock.GetUtcNow().AddSeconds(19)) && schedule.Due(route, 2, clock.GetUtcNow().AddSeconds(20)),
            "A zero-byte canceled flight borrowed old accepted-byte progress instead of backing off.");
        var missing = new PeerScheduledCompletion(2, (generation, result, failed) =>
            progress.Record(schedule, "world:missing", generation, clock.GetUtcNow(), result, failed, 1));
        Require(missing.Complete(null, failed: false) && !schedule.Due("world:missing", 2, clock.GetUtcNow().AddSeconds(19)) &&
            schedule.Due("world:missing", 2, clock.GetUtcNow().AddSeconds(20)),
            "A finished owner without its own snapshot reused an older result or invented success.");
    }

    private sealed class CompletionClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-27T12:00:00Z").AddTicks(ticks);
        internal void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }

    private static void PendingProgressBounds()
    {
        var now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
        var pending = new PeerPendingProgress();
        var schedule = new PeerReplicationSchedule();
        var attempt = now;
        for (var index = 0; index < 10; index++)
        {
            var prompt = pending.Prompt("paused", 1, Result(attempt, "SeedPaused"));
            Require(prompt == (index < 2), "an unchanged paused recipient permanently retained the two-second retry");
            schedule.Completed("paused", 1, now, failed: !prompt, pending: prompt, jitter: 1);
            var due = index < 2 ? 2 : Math.Min(300, 10 * (1 << Math.Min(6, index - 1)));
            Require(!schedule.Due("paused", 1, now.AddSeconds(due - 1)) && schedule.Due("paused", 1, now.AddSeconds(due)),
                "a stalled recipient did not retain its bounded prompt burst and existing offline backoff");
            now = now.AddSeconds(due);
            attempt = attempt.AddSeconds(1);
        }
        Require(schedule.Due("paused", 2, now.AddSeconds(-1)) && pending.Prompt("paused", 2, Result(attempt, "SeedPaused")),
            "a material publication/reconnect did not invalidate a stalled recipient's deadline");
        for (var index = 0; index < 6; index++)
        {
            attempt = attempt.AddSeconds(1);
            var receiving = Result(attempt, "ByteBudget") with { ReceivedBytes = 1024 };
            var sending = Result(attempt, "WorkBudget") with
            { SubmittedBytes = 1024, RequestedFileIndex = 0, RequestedOffset = index * 1024, PublicationNumber = 9 };
            Require(pending.Prompt("incoming", 1, receiving) && pending.Prompt("outgoing", 1, sending),
                "accepted incoming bytes or an advancing outgoing cursor inherited offline backoff");
        }
        var held = Result(attempt, "ByteBudget") with { ReceivedBytes = 1024 };
        Require(pending.Prompt("held", 1, held) && pending.Prompt("held", 1, held) &&
            pending.Prompt("held", 1, held) && !pending.Prompt("held", 1, held),
            "re-reading an old transfer observation manufactured continuing byte progress");
        for (var index = 0; index < 5; index++)
        {
            attempt = attempt.AddSeconds(1);
            var resubmitted = Result(attempt, "SeedHistoryPending") with
            { SubmittedBytes = 1024, RequestedFileIndex = 0, RequestedOffset = 5120, PublicationNumber = 9 };
            Require(pending.Prompt("outgoing", 1, resubmitted) == (index < 2),
                "repeated submission of the same requested bytes was counted as recipient progress");
        }
        for (var index = 0; index < 4; index++)
        {
            attempt = attempt.AddSeconds(1);
            Require(!pending.Prompt("outgoing", 1, Result(attempt, "WorkBudget") with
            { SubmittedBytes = 1024, RequestedFileIndex = 0, RequestedOffset = index % 2 == 0 ? 0 : 5120, PublicationNumber = 9 }),
                "alternating older chunk requests erased the outgoing high-water mark and renewed fast retries");
        }
        Require(pending.Prompt("outgoing", 1, Result(attempt.AddSeconds(1), "WorkBudget") with
        { SubmittedBytes = 1024, RequestedFileIndex = 0, RequestedOffset = 0, PublicationNumber = 10 }),
            "a newly published outgoing save could not begin prompt resumable work");
        schedule.Track("useful", 2, now);
        schedule.Completed("paused", 2, now, failed: true, pending: false, jitter: 1);
        Require(schedule.Due("useful", 2, now) && schedule.NextDelay(now, 2) == TimeSpan.FromSeconds(1),
            "a paused recipient hid another due route behind its own backoff");
    }

    private static WorldPeerTransferProgress Result(DateTimeOffset at, string result) => new(
        "Finished", "Offering", "Seed", "Exchange", 200, result, "None", at, at, 1,
        0, 0, 0, null, null, false);

    private static void TrayPublicationBounds()
    {
        var coalescer = new DesktopTraySummaryCoalescer();
        var delivered = new List<DesktopTraySummary>();
        var initial = new DesktopTraySummary(0, 0, "Connected");
        coalescer.Publish(initial, delivered.Add);
        for (var index = 0; index < 100; index++) coalescer.Publish(initial with { }, delivered.Add);
        Require(delivered.Count == 1, "unchanged value-equal tray summaries queued repeated desktop dispatch");
        var changed = initial with { RunningServers = 1 };
        try
        {
            coalescer.Publish(changed, _ => throw new IOException("Generated failed display publication."));
            throw new InvalidOperationException("the generated failed tray publisher unexpectedly completed");
        }
        catch (IOException) { }
        coalescer.Publish(changed, delivered.Add);
        coalescer.Publish(initial, delivered.Add);
        Require(delivered.SequenceEqual([initial, changed, initial]),
            "failed publication or a resumed prior tray value lost a current update");
    }

    private static async Task UnchangedIdleAsync(string parent)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original");
        var service = fixture.TargetService;
        var now = DateTimeOffset.UtcNow;
        var first = new PublicProfile(fixture.Root.ProfileId, "Generated", "Ready", null,
            Kind: GameKinds.Valheim, OnlinePlayers: 0, PlayerCountObservedUtc: now);
        var second = first with { Id = Guid.NewGuid(), Name = "Generated second" };
        var connected = new FriendView("Friend", "Connected", "", "https://192.0.2.1:5131", now,
            true, true, true, [first, second], [CompanionProtocol.SharedWorldPeersCapability, "generated"], ConnectionId: Guid.NewGuid());
        var wakes = 0;
        service.BackgroundSyncRequested += () => wakes++;
        Require(service.ObserveFriendSnapshot(connected), "the generated initial connection did not request enrollment");
        _ = service.Views();
        Set(service, "scheduledSyncGeneration", Get<long>(service, "requestedSyncGeneration"));
        Set(service, "startedMembershipGeneration", Get<long>(service, "requestedMembershipGeneration"));
        Set(service, "nextBackgroundSyncUtc", now.AddMinutes(1));
        Set(service, "nextMembershipNetworkUtc", now.AddMinutes(1));
        Set(service, "nextMembershipRetryUtc", DateTimeOffset.MaxValue);
        var work = service.WorkSnapshot();
        var files = Files(fixture.Target);
        for (var index = 0; index < 100; index++)
        {
            Require(!service.ObserveFriendSnapshot(connected with
            {
                LastConnectedUtc = now.AddSeconds(index * 5),
                Profiles = [second with { OnlinePlayers = index }, first with { OnlinePlayers = index, PlayerCountObservedUtc = now.AddSeconds(index * 5) }],
                HostCapabilities = ["generated", CompanionProtocol.SharedWorldPeersCapability],
                Activity = [new(Guid.NewGuid(), now, "Players", "CountChanged", "Generated")]
            }), "unchanged heartbeat/player values or ordering invalidated idle scheduling");
            service.ScheduleSync(default);
        }
        await service.WaitForSyncAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var after = service.WorkSnapshot();
        Require(wakes == 1 && work.Membership == after.Membership &&
            work.Work.Where(item => item.Stage is "Inventory" or "SignedRegistry" or "PeerRequest")
                .SequenceEqual(after.Work.Where(item => item.Stage is "Inventory" or "SignedRegistry" or "PeerRequest")) &&
            fixture.SourceHandler.Operations.Count == 0 && fixture.RemoteHandler.Operations.Count == 0 && files == Files(fixture.Target),
            "unchanged idle repeated membership, source inventory, signature/HTTP work or protected writes");
        var generation = Get<long>(service, "requestedMembershipGeneration");
        var stopped = connected with
        {
            Activity = [new(Guid.NewGuid(), now, "Lifecycle", "Stopped", "Generated", ProfileId: first.Id,
                Visibility: ActivityVisibility.AssignedFriends)]
        };
        Require(service.ObserveFriendSnapshot(stopped) && wakes == 2 &&
            Get<long>(service, "requestedMembershipGeneration") == generation && !service.ObserveFriendSnapshot(stopped),
            "an assigned Stop failed to wake delivery once or repeated companion enrollment");
        var policy = stopped with
        {
            Activity = [.. stopped.Activity!, new(Guid.NewGuid(), now, "Backup", "SharedSavesDisabled", "Generated",
                ProfileId: first.Id, Visibility: ActivityVisibility.AssignedFriends)]
        };
        Require(service.ObserveFriendSnapshot(policy) && Get<long>(service, "requestedMembershipGeneration") == generation + 1 &&
            service.ObserveFriendSnapshot(policy with { State = "Disconnected/Unknown" }) && service.ObserveFriendSnapshot(policy) && wakes == 5,
            "material policy/reconnect observations lost their immediate enrollment and transfer wake");
        Require((await service.SetReceivingAsync(fixture.Root.GroupKey, false, default)).Ok && wakes == 6,
            "a current local consent withdrawal failed to wake replication immediately");
        var schedule = Get<PeerReplicationSchedule>(service, "replicationSchedule");
        var route = fixture.Root.GroupKey + ":" + new string('F', 64);
        var currentGeneration = Get<long>(service, "requestedSyncGeneration");
        now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 3; index++)
            schedule.Completed(route, currentGeneration - 1, now, failed: true, pending: false, jitter: 1);
        Set(service, "scheduledSyncGeneration", currentGeneration);
        Set(service, "nextBackgroundSyncUtc", now.AddMinutes(1));
        Set(service, "nextMembershipNetworkUtc", now.AddMinutes(1));
        var beforeLease = Files(fixture.Target);
        var outer = service.BeginHostingPreparation(fixture.Root);
        var inner = service.BeginHostingPreparation(fixture.Root);
        try
        {
            TimeSpan Delay() => (TimeSpan)typeof(WorldPeerService).GetMethod("NextPeerScheduleDelay", Private)!.Invoke(service, [])!;
            Require(Delay() == TimeSpan.FromSeconds(60), "a preparing world's known route spun its due generation");
            inner.Dispose();
            Require(wakes == 6 && Delay() == TimeSpan.FromSeconds(60), "an inner preparation release woke a still-owned outer lease");
        }
        finally { inner.Dispose(); outer.Dispose(); }
        outer.Dispose();
        Require(wakes == 7 && service.NextBackgroundSyncDelay <= TimeSpan.FromSeconds(1) &&
            Get<long>(service, "requestedSyncGeneration") == currentGeneration && beforeLease == Files(fixture.Target),
            "outer preparation release lost its prompt event-only wake, changed material generation or wrote protected state");
        schedule.Completed(route, currentGeneration, now, failed: true, pending: false, jitter: 1);
        Require(!schedule.Due(route, currentGeneration, now.AddSeconds(159)) && schedule.Due(route, currentGeneration, now.AddSeconds(160)),
            "preparation suppression or lease release reset the route's existing failure count");
    }

    private static async Task PeerOwnedLegacyConsentAsync(string parent, bool currentPeerOperation)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Branch");
        var friends = Get<FriendService>(fixture.TargetService, "friend");
        var links = Get<IEnumerable<(Guid Id, FriendLink Link)>>(friends, "links");
        var link = links.Single().Link;
        var view = new FriendView("Friend", "Connected", "", fixture.SourceRoute.Address, DateTimeOffset.UtcNow,
            true, true, true, [new(fixture.Root.ProfileId, "Generated", "Ready", null, Kind: GameKinds.Valheim)],
            [CompanionProtocol.SharedWorldPeersCapability]);
        Set(link, "view", view);
        var path = Path.Combine(fixture.Target.RootPath, WorldPeerReceiveConsentStore.StateName);
        File.WriteAllText(path, "\"invalid-generated-encrypted-consent\"");
        var before = Files(fixture.Target);
        link.ScheduleSharedCatchUp(default, root => root == fixture.Root);
        await link.ScheduledSharedCatchUp().WaitAsync(TimeSpan.FromSeconds(5));
        Require(Files(fixture.Target) == before && fixture.Target.Recovery.Notices.Count == 0,
            "a peer-owned legacy candidate read/quarantined protected consent before its cheap ownership exclusion");
        if (currentPeerOperation)
        {
            Require(await fixture.SeedAsync() is { Code: "SeedPaused", Receipt: null },
                "skipping legacy scheduling consent also bypassed the actual peer operation's fresh consent check");
        }
        else
        {
            link.ScheduleSharedCatchUp(default, _ => false);
            await link.ScheduledSharedCatchUp().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Require(!File.Exists(path) && fixture.Target.Recovery.Notices.Any(notice => notice.StateFile == WorldPeerReceiveConsentStore.StateName) &&
            fixture.Sidecars().Length == 0 && fixture.Target.LoadRuns().Count == 0,
            "a due legacy/current peer receive operation failed to read current protected consent and refuse the damaged decision");
    }

    private static T Get<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Private)!.SetValue(owner, value);
    private static string Files(LocalData data) => string.Join('|', Directory.EnumerateFiles(data.RootPath, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "host.lock")
        .Order(StringComparer.Ordinal).Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static void Require([DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

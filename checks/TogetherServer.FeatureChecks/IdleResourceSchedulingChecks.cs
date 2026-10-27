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
        TrayPublicationBounds();
        await UnchangedIdleAsync(parent);
        await PeerOwnedLegacyConsentAsync(parent, currentPeerOperation: false);
        await PeerOwnedLegacyConsentAsync(parent, currentPeerOperation: true);
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

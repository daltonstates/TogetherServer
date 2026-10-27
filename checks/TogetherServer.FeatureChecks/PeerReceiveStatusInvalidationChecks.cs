using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using TogetherServer;
using Fixture = TogetherServer.FeatureChecks.WorldPeerExistingCopyChecks.Fixture;

namespace TogetherServer.FeatureChecks;

// Generated signed files and the actual fake-HTTP peer receive path. The old
// legacy outcome is seeded through its production health accumulator, not a
// simulated app process. No listener, app, game or production data is opened.
internal static class PeerReceiveStatusInvalidationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Original");
        var receiver = fixture.RequesterService;
        var friends = (FriendService)typeof(WorldPeerService).GetField("friend", Private)!.GetValue(receiver)!;
        var links = (List<(Guid Id, FriendLink Link)>)typeof(FriendService).GetField("links", Private)!.GetValue(friends)!;
        var original = links.Single().Link;
        var saved = JsonSerializer.Deserialize<FriendConfiguration>(fixture.RequesterData.LoadProtected("friend.protected")!, Json)!;
        var sourceDevice = saved.DeviceId;
        var localAlias = Guid.NewGuid();
        saved.DeviceId = Guid.NewGuid();
        saved.PeerWorld = fixture.Root;
        saved.PeerWorldMemberDeviceId = sourceDevice;
        saved.PeerWorldLocalProfileId = localAlias;
        var aliasId = Guid.NewGuid();
        var aliasFile = $"friend-{aliasId:N}.protected";
        fixture.RequesterData.SaveProtected(aliasFile, JsonSerializer.SerializeToUtf8Bytes(saved, Json));
        var alias = new FriendLink(fixture.RequesterData, aliasFile,
            (_, _) => throw new HttpRequestException("Synthetic alias is offline."));
        links.Add((aliasId, alias)); // The fixture's FriendService owns disposal.

        var stale = new ReceivedSharedWorldResult(false, "NoPublishedSave", "Synthetic old Host has not published a save.");
        var oldAttempts = new[] { original, alias }.Select(link =>
        {
            var health = Health(link);
            var attempt = health.Begin(fixture.Root.ProfileId);
            health.Complete(fixture.Root.ProfileId, attempt, stale);
            Require(Progress(link).IsEmpty && link.SharedWorldStatus(fixture.Root.ProfileId) is
            { State: "Transfer unavailable", ThisPcVersion: null },
                "The synthetic NoPublishedSave failure did not occupy the legacy health projection.");
            return (Link: link, Health: health, Attempt: attempt);
        }).ToArray();

        // A rejected scope or failed commit must leave the old observation.
        var changed = false;
        Require(!await friends.CommitPeerReceiveAsync(fixture.Root with { GroupId = Guid.NewGuid() }, sourceDevice,
                () => changed = true, default, canonicalCopyCompleted: true) && !changed &&
            !await friends.CommitPeerReceiveAsync(fixture.Root, Guid.NewGuid(), () => changed = true, default,
                canonicalCopyCompleted: true) && !changed,
            "A different world/member invalidated or committed a received save.");
        try
        {
            await original.CommitPeerReceiveAsync(fixture.Root, sourceDevice,
                () => throw new InvalidDataException("Synthetic commit refused."), default, canonicalCopyCompleted: true);
            throw new InvalidOperationException("A failed peer commit unexpectedly completed.");
        }
        catch (InvalidDataException) { }
        Require(oldAttempts.All(item => item.Health.Issue(fixture.Root.ProfileId) is not null),
            "An unsuccessful peer commit cleared the retained legacy failure.");

        var group = typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(receiver, [fixture.Root.GroupKey]);
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ((Task)typeof(WorldPeerService).GetMethod("PullCatalogAsync", Private)!.Invoke(receiver,
            [group, fixture.SourceRoute, bounded.Token])!).WaitAsync(TimeSpan.FromSeconds(15));
        var exact = FriendLink.ReadReceivedLatest(Path.GetDirectoryName(fixture.CanonicalReceivedPointer)!);
        Require(exact is not null && exact.VersionHash == fixture.Descriptor.Version.VersionHash && SharedWorldService.VerifySignature(exact),
            "The actual signed fake-HTTP receive did not promote its exact completed canonical copy.");
        foreach (var item in oldAttempts)
        {
            Require(item.Health.Issue(fixture.Root.ProfileId) is null &&
                item.Link.SharedWorldStatus(fixture.Root.ProfileId) is { Error: null } status &&
                status.ThisPcVersion == exact.Number && status.State != "Transfer unavailable",
                "A completed canonical peer copy kept an obsolete legacy failure on a matching saved alias.");
            item.Health.Complete(fixture.Root.ProfileId, item.Attempt, stale);
            Publish(item.Link, fixture.Root.ProfileId, item.Attempt,
                new(true, exact.Number - 1, null, "Verifying", TransferPhase: "Verifying"));
            Require(item.Health.Issue(fixture.Root.ProfileId) is null && Progress(item.Link).IsEmpty &&
                item.Link.SharedWorldStatus(fixture.Root.ProfileId).ThisPcVersion == exact.Number,
                "A late legacy progress/completion restored its pre-peer-commit observation.");
        }

        // Receipt-only existing-copy callbacks cannot erase a genuinely newer
        // current legacy transfer or its later observations.
        var currentAttempt = Health(original).Begin(fixture.Root.ProfileId);
        var current = new ReceivedSharedWorldStatus(true, exact.Number + 1, exact.Number,
            "Receiving", ReceivedBytes: 4, TotalBytes: 8, TransferPhase: "Receiving");
        Publish(original, fixture.Root.ProfileId, currentAttempt, current);
        typeof(FriendLink).GetMethod("ClearSharedTransferStatus", Private)!.Invoke(original,
            [fixture.Root.ProfileId, oldAttempts[0].Attempt]);
        Require(await friends.CommitPeerReceiveAsync(fixture.Root, sourceDevice, () => changed = true, default) && changed &&
            original.SharedWorldStatus(fixture.Root.ProfileId) == current,
            "An existing-copy receipt commit hid a newer active legacy transfer.");
        Progress(original).TryRemove(fixture.Root.ProfileId, out _);
        Health(original).Complete(fixture.Root.ProfileId, currentAttempt,
            new(false, "NetworkUnavailable", "Synthetic newer request failed."));
        Require(Health(original).Issue(fixture.Root.ProfileId) is not null &&
            original.SharedWorldStatus(fixture.Root.ProfileId) is { State: "Transfer unavailable" } latest &&
            latest.ThisPcVersion == exact.Number && fixture.A.LoadRuns().Count == 0 && fixture.B.LoadRuns().Count == 0,
            "A genuinely new legacy result was suppressed or lost the verified received copy.");
    }

    private static SharedWorldTransferHealth Health(FriendLink link) =>
        (SharedWorldTransferHealth)typeof(FriendLink).GetField("sharedTransferHealth", Private)!.GetValue(link)!;
    private static ConcurrentDictionary<Guid, ReceivedSharedWorldStatus> Progress(FriendLink link) =>
        (ConcurrentDictionary<Guid, ReceivedSharedWorldStatus>)typeof(FriendLink).GetField("sharedTransfers", Private)!.GetValue(link)!;
    private static void Publish(FriendLink link, Guid profileId, SharedWorldTransferHealth.Attempt attempt,
        ReceivedSharedWorldStatus status) => typeof(FriendLink).GetMethod("PublishSharedTransferStatus", Private)!
            .Invoke(link, [profileId, attempt, status]);
    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

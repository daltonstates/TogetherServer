using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Generated registry/roster records and refusing fake clients only. Ownership
// is a scheduling snapshot, not a network probe or permission decision.
internal static class PeerReplicationOwnershipChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent)
    {
        await BootstrapFallbackAsync(parent);
        await LearnedRoutesAsync(parent);
    }

    private static async Task BootstrapFallbackAsync(string parent)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original");
        var service = fixture.RequesterService;
        var friends = (FriendService)typeof(WorldPeerService).GetField("friend", Private)!.GetValue(service)!;
        var links = (IEnumerable<(Guid Id, FriendLink Link)>)typeof(FriendService).GetField("links", Private)!.GetValue(friends)!;
        var link = links.Single().Link;
        typeof(FriendLink).GetField("view", Private)!.SetValue(link, new FriendView("Friend", "Connected", "", "", DateTimeOffset.UtcNow,
            true, true, true, [new(fixture.Root.ProfileId, "Generated", "Offline", null, Kind: GameKinds.Fixture)],
            [CompanionProtocol.SharedWorldsCapability, CompanionProtocol.SharedWorldPeersCapability]));
        _ = service.Views();
        Require(service.OwnsReplication(fixture.Root), "A valid enrolled pinned bootstrap did not own peer replication.");
        friends.ScheduleSharedCatchUp(default, service.OwnsReplication);
        Require(typeof(FriendLink).GetField("scheduledSharedTransfer", Private)!.GetValue(link) is null,
            "A valid peer transport started the duplicate legacy receiver.");
        var registry = JsonNode.Parse(fixture.RequesterData.LoadProtected("world-peer-memberships.protected")!)!;
        registry["groups"]![0]!["bootstrap"] = null;
        fixture.RequesterData.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(registry, Json));
        _ = service.Views();
        Require(!SharedWorldPeerTrust.ValidRoute(new("https://127.0.0.1:5131", new string('A', 64))) &&
            !service.OwnsReplication(fixture.Root), "A zero-route registered world suppressed its legacy loopback receiver.");
        friends.ScheduleSharedCatchUp(default, service.OwnsReplication);
        await friends.WaitForSharedCatchUpAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Require(typeof(FriendLink).GetField("scheduledSharedTransfer", Private)!.GetValue(link) is Task &&
            link.SharedWorldStatus(fixture.Root.ProfileId).Error is not null,
            "The zero-route fallback never entered the normal legacy receiver and its refusing fake client.");
        Require(service.WorkSnapshot().Work.Single(item => item.Stage == "PeerRequest").Count == 0 &&
            fixture.A.LoadRuns().Count == 0 && fixture.B.LoadRuns().Count == 0,
            "Reading ownership performed a peer probe or created a managed process.");
    }

    private static async Task LearnedRoutesAsync(string parent)
    {
        using var data = new LocalData(Path.Combine(parent, "learned-replication-owner-" + Guid.NewGuid().ToString("N")));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var self = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var peer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var localId = Guid.NewGuid(); var peerId = Guid.NewGuid();
        var members = new[]
        {
            new SharedWorldRosterMember(localId, Public(self), new(Receive: true, EligibleHost: true, Reshare: true), false),
            new SharedWorldRosterMember(peerId, Public(peer), new(Receive: true, EligibleHost: true, Reshare: true), false)
        };
        var draft = new SharedWorldRoster(1, Guid.NewGuid(), Guid.NewGuid(), 1, 1, true, Public(owner), members, "");
        var roster = draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
        var root = new SharedWorldPeerRoot(Public(owner), roster.ProfileId, roster.GroupId);
        data.SaveSettings(new() { Profiles = [], CompanionListeningEnabled = false });
        data.SaveProtected($"shared-world-pc-signing-{localId:N}.protected", self.ExportPkcs8PrivateKey());
        new SharedWorldRosterChainStore(data).Append(roster, root.OwnerPublicKey);
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Groups = new[] { new { Root = root, Roster = roster, DeviceId = localId, Name = "Generated peer-only world",
                ReceiveConsented = false, SharingEnabled = false, Bootstrap = (SharedWorldPeerRoute?)null } }
        }, Json));
        var requests = 0;
        using var friends = new FriendService(data, (_, _) => throw new HttpRequestException("Synthetic original Host is offline."));
        using var service = new WorldPeerService(data, new HostManager(data), friends, new PairingService(data), (_, _) =>
        {
            requests++;
            throw new HttpRequestException("Synthetic learned peer is unreachable.");
        });
        var store = new SharedWorldPeerStore(data, root,
            new(root, (id, key) => SharedWorldPeerTrust.AccessFromRoster(root, roster, id, key)));
        _ = service.Views();
        Require(!service.OwnsReplication(root), "Registration without a bootstrap/learned route acquired replication ownership.");
        var route = new SharedWorldPeerRoute("https://192.0.2.194:5131", new string('B', 64));
        store.Publish(peerId, peer, [route], [], [], []);
        await ProjectRoutesAsync(service, root);
        Require(service.OwnsReplication(root) && requests == 1,
            "An approved peer-only route required an original-Host bootstrap or was treated as proof of live reachability.");
        var before = requests;
        for (var index = 0; index < 100; index++) Require(service.OwnsReplication(root), "The published ownership snapshot was unstable.");
        Require(requests == before, "The ownership predicate performed a network read.");
        store.Publish(peerId, peer, [], [], [], []);
        await ProjectRoutesAsync(service, root);
        Require(!service.OwnsReplication(root) && requests == before,
            "Removing the final signed peer route did not explicitly restore the legacy fallback.");
        var card = new WorldPeerContactCard(1, root, peerId, Public(peer), 1, [route], "");
        card = card with { Signature = Convert.ToBase64String(peer.SignData(WorldPeerService.ContactBasis(card), HashAlgorithmName.SHA256)) };
        data.SaveProtected($"world-peer-contacts-{root.GroupKey}.protected", JsonSerializer.SerializeToUtf8Bytes(new[] { card }, Json));
        await ProjectRoutesAsync(service, root);
        Require(service.OwnsReplication(root), "A valid approved additional contact could not own peer-only replication.");
        var registry = JsonNode.Parse(data.LoadProtected("world-peer-memberships.protected")!)!;
        registry["groups"]![0]!["deviceId"] = peerId.ToString();
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(registry, Json));
        _ = service.Views();
        Require(!service.OwnsReplication(root), "A changed local source member reused another identity's learned-route ownership.");
        registry["groups"]![0]!["deviceId"] = localId.ToString();
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(registry, Json));
        _ = service.Views();
        Require(!service.OwnsReplication(root), "Restoring a source member revived its invalidated route snapshot without a current projection.");
        await ProjectRoutesAsync(service, root);
        Require(service.OwnsReplication(root), "The restored current source could not rediscover its approved peer route.");
        var unknown = card with { DeviceId = Guid.NewGuid(), PublicKey = Public(stranger), Signature = "" };
        unknown = unknown with { Signature = Convert.ToBase64String(stranger.SignData(WorldPeerService.ContactBasis(unknown), HashAlgorithmName.SHA256)) };
        data.SaveProtected($"world-peer-contacts-{root.GroupKey}.protected", JsonSerializer.SerializeToUtf8Bytes(new[] { unknown }, Json));
        await ProjectRoutesAsync(service, root);
        Require(!service.OwnsReplication(root), "An unapproved signed contact suppressed the legacy receiver before its membership was proved.");
        data.SaveProtected($"world-peer-contacts-{root.GroupKey}.protected", JsonSerializer.SerializeToUtf8Bytes(Array.Empty<WorldPeerContactCard>(), Json));
        await ProjectRoutesAsync(service, root);
        Require(!service.OwnsReplication(root) && data.LoadRuns().Count == 0,
            "An empty additional-route snapshot retained ownership or created a game process.");
    }

    private static Task ProjectRoutesAsync(WorldPeerService service, SharedWorldPeerRoot root)
    {
        var group = typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(service, [root.GroupKey]);
        return (Task)typeof(WorldPeerService).GetMethod("SyncScheduledGroupAsync", Private)!.Invoke(service,
            [group, CancellationToken.None, CancellationToken.None, false])!;
    }
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure projections and protected synthetic files only. No app, listener,
// process query, native desktop, game, or live HTTP transport is invoked.
internal static class ServerDirectoryChecks
{
    internal static Task RunAsync(string root)
    {
        CheckFixedRead();
        CheckCanonicalProjection();
        CheckSignedWorldIdentity();
        CheckSavedHosts(root);
        return Task.CompletedTask;
    }

    private static void CheckSignedWorldIdentity()
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo());
        var profile = Guid.NewGuid();
        var draft = new SharedWorldRoster(3, Guid.NewGuid(), profile, 1, 1, true, publicKey, [], "",
            SignerDeviceId: Guid.Empty, SignerPublicKey: publicKey);
        SharedWorldRoster Sign(SharedWorldRoster value) => value with
        {
            Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(value), HashAlgorithmName.SHA256))
        };
        var roster = Sign(draft);
        var original = new ServerDirectoryServer(profile, "Original world", GameKinds.Valheim,
            "Offline", null, null, false, null, null, false, 0);
        var successor = original with { Id = Guid.NewGuid(), Name = "Continued world" };
        var sourceRow = ServerDirectory.WithWorld(original, roster, false);
        var remoteRow = ServerDirectory.WithWorld(successor, roster, true);
        Require(sourceRow.SharedWorldId is { Length: 64 } && remoteRow.SharedWorldId == sourceRow.SharedWorldId &&
            remoteRow.OriginalProfileId == profile && remoteRow.IsSuccessor && !sourceRow.IsSuccessor,
            "A signed continuation did not retain its original world identity across local profile IDs.");
        var host = new ServerDirectoryHost(Guid.NewGuid(), Guid.NewGuid(), "Hosting PC", "Friend",
            "Connected", null, DateTimeOffset.UtcNow, [remoteRow]);
        var projected = ServerDirectory.Project([sourceRow], [host]);
        Require(projected.FriendHosts[0].Servers[0].SourceName == "Original world",
            "The remotely hosted world lost its original sidebar name.");
        var differentGroup = ServerDirectory.WithWorld(successor, Sign(draft with { GroupId = Guid.NewGuid() }), true);
        Require(differentGroup.SharedWorldId != sourceRow.SharedWorldId &&
            ServerDirectory.Project([sourceRow], [host with { Servers = [differentGroup] }])
                .FriendHosts[0].Servers[0].SourceName is null,
            "Matching names or profile IDs merged different signed world groups.");
        var forged = ServerDirectory.WithWorld(successor, roster with { GroupId = Guid.NewGuid() }, true);
        Require(forged.SharedWorldId is null && forged.OriginalProfileId is null && !forged.IsSuccessor,
            "Unverified sharing metadata acquired a sidebar grouping identity.");
        var json = JsonSerializer.Serialize(projected);
        Require(!json.Contains(publicKey, StringComparison.Ordinal) && !json.Contains(roster.Signature, StringComparison.Ordinal),
            "The directory exposed signing keys or signatures instead of an opaque world identity.");
    }

    private static void CheckFixedRead()
    {
        var context = new DefaultHttpContext();
        Require(ServerDirectoryRoutes.Reject(context) is IStatusCodeHttpResult { StatusCode: 403 },
            "server listing omitted the local-header boundary");
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        Require(ServerDirectoryRoutes.Reject(context) is null, "fixed directory read was rejected");
        context.Request.QueryString = new("?host=192.0.2.1");
        Require(ServerDirectoryRoutes.Reject(context) is IStatusCodeHttpResult { StatusCode: 400 },
            "server listing accepted arbitrary query input");
        context.Request.QueryString = default;
        context.Request.ContentLength = 1;
        Require(ServerDirectoryRoutes.Reject(context) is IStatusCodeHttpResult { StatusCode: 400 },
            "server listing accepted a declared request body");
        context.Request.ContentLength = null;
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        Require(ServerDirectoryRoutes.Reject(context) is IStatusCodeHttpResult { StatusCode: 400 },
            "server listing accepted a chunked request body");
    }

    private static void CheckCanonicalProjection()
    {
        var now = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        var ownerId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var settings = new HostSettings
        {
            Profiles =
        [
            new() { Id = ownerId, Name = "My world", Kind = GameKinds.Valheim,
                WorldDirectory = "C:\\Private\\world", ExecutablePath = "C:\\Private\\server.exe" },
            new() { Id = guestId, Name = "Guest local copy", Kind = GameKinds.Valheim,
                SeparateCopySourceProfileId = sourceId, SeparateCopyBranchHash = new string('B', 64) },
            new() { Id = unknownId, Name = "Waiting", Kind = GameKinds.MinecraftJava }
        ]
        };
        var runs = new RunView[]
        {
            new(ownerId, "Ready", "Private process details", 123, OnlinePlayers: 0, MaxPlayers: 10,
                PlayerCountTrusted: true, PlayerObservationSource: "SyntheticQuery", PlayerCountObservedUtc: now,
                PlayerCountRefreshing: true, PlayerCountFailureCount: 0),
            new(guestId, "Ready", "Private guest process details", 456, OnlinePlayers: 5, MaxPlayers: 10,
                PlayerCountTrusted: true, PlayerObservationSource: "SyntheticQuery", PlayerCountObservedUtc: now.AddSeconds(-11),
                PlayerCountRefreshing: false, PlayerCountFailureCount: 2),
            new(unknownId, "Unknown", "Private error", 789, OnlinePlayers: 0, MaxPlayers: 10,
                PlayerCountTrusted: false, PlayerCountObservedUtc: now)
        };
        var snapshot = new HostSnapshot(settings, runs, "Synthetic", "Host", new Dictionary<Guid, bool>(), "C:\\Private");
        var local = ServerDirectory.LocalRows(snapshot, now);
        Require(local[0].OnlinePlayers == 0 && local[0].PlayerCountTrusted && local[0].PlayerCountRefreshing,
            "fresh canonical zero or in-progress refresh metadata was lost");
        Require(local[1].State == "Unknown" && local[1].OnlinePlayers is null && !local[1].PlayerCountTrusted &&
            local[1].PlayerCountFailureCount == 2 && local[1].SeparateCopySourceProfileId == sourceId,
            "stale guest evidence stayed current or guest identity disappeared");
        Require(local[2].OnlinePlayers is null && !local[2].PlayerCountTrusted,
            "an untrusted count became a directory zero");
        Require(!ServerDirectory.Fresh(now.AddSeconds(1), now), "future player evidence was accepted");
        var friendSource = new ServerDirectoryServer(sourceId, "Original shared world", GameKinds.Valheim,
            "Unknown", null, null, false, null, null, false, 0);
        var host = new ServerDirectoryHost(Guid.NewGuid(), Guid.NewGuid(), "Morgan's PC", "Morgan",
            "Disconnected/Unknown", null, null, [friendSource]);
        var directory = ServerDirectory.Project(local, [host]);
        Require(directory.LocalServers[1].SourceName == "Original shared world" &&
            directory.LocalServers[1].SourceHostName == "Morgan", "guest origin was not shown from saved identity");
        var removed = ServerDirectory.Project(local, [host with { State = "Revoked", ConnectionCode = "Revoked" }]);
        Require(removed.LocalServers[1].SourceName is null && removed.LocalServers[1].SourceHostName is null,
            "removed Host supplied visible guest-origin metadata");
        var serialized = JsonSerializer.Serialize(directory, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var privateValue in new[] { "Private", "processId", "declaredPorts", "worldDirectory", "executablePath",
            "separateCopyBranchHash", "joinAddress", "credential", "canStop", "companionEndpoint" })
            Require(!serialized.Contains(privateValue, StringComparison.OrdinalIgnoreCase),
                "server directory leaked private or action-bearing data");
    }

    private static void CheckSavedHosts(string root)
    {
        using var data = new LocalData(Path.Combine(root, "directory-" + Guid.NewGuid().ToString("N")));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        data.SaveProtected("friend-connections.protected", JsonSerializer.SerializeToUtf8Bytes(new
        { ids, selectedId = ids[1] }, json));
        for (var index = 0; index < ids.Length; index++)
        {
            var configuration = new FriendConfiguration
            {
                DisplayName = index == 2 ? "Host 192.0.2.10" : "Friend PC " + index,
                HostProfileName = "Player " + index,
                HostId = Guid.NewGuid(),
                DeviceId = Guid.NewGuid(),
                Endpoint = "https://192.0.2.10:5131",
                Fingerprint = new string('A', 64),
                Credential = new string('C', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                KnownRevoked = index == 2,
                CachedProfiles = [new(Guid.NewGuid(), "Saved server " + index, "Ready", "192.0.2.99:2456",
                    Kind: GameKinds.Valheim, OnlinePlayers: 0, MaxPlayers: 10, CanStart: true, CanStop: true,
                    PlayerCountTrusted: true, PlayerCountObservedUtc: DateTimeOffset.UtcNow)]
            };
            data.SaveProtected("friend-" + ids[index].ToString("N") + ".protected",
                JsonSerializer.SerializeToUtf8Bytes(configuration, json));
        }
        var requests = 0;
        using var friend = new FriendService(data, (_, _) =>
        {
            requests++;
            throw new InvalidOperationException("A directory read must not create HTTP clients.");
        });
        var selected = friend.View().ConnectionId;
        var indexBefore = data.LoadProtected("friend-connections.protected");
        var hosts = friend.ServerDirectoryHosts();
        Require(hosts.Count == 3 && hosts[0].ConnectionId == ids[0] && hosts[1].ConnectionId == ids[1],
            "directory omitted saved unselected Host connections");
        Require(hosts[0].Servers is [
            {
                Name: "Saved server 0", State: "Unknown", OnlinePlayers: null,
                PlayerCountTrusted: false, PlayerCountObservedUtc: null
            }], "cached metadata revived a count after restart");
        Require(hosts[2].State == "Revoked" && hosts[2].Servers.Count == 0,
            "removed saved Host exposed its former assigned servers");
        Require(hosts[2].ConnectionName == "Player 2's Host" &&
            !JsonSerializer.Serialize(hosts, json).Contains("192.0.2.", StringComparison.Ordinal),
            "a generated Host label exposed its route address in the global list");
        Require(requests == 0 && friend.View().ConnectionId == selected && selected == ids[1] &&
            indexBefore!.SequenceEqual(data.LoadProtected("friend-connections.protected")!),
            "directory read refreshed the network or changed the selected saved Host");
        var manager = new HostManager(data);
        Require(manager.ServerDirectoryRows().Count == 0, "empty local cache invented a server");
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

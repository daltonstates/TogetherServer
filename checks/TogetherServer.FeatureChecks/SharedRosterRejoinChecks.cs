using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Real pairing, signing and FriendLink code over fake HTTP and disposable protected files.
// No app, process, listener, socket, browser, game or real world is used.
internal static partial class SharedRosterRejoinChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        foreach (var deleteOld in new[] { false, true })
            foreach (var sameConnection in new[] { false, true })
                foreach (var hostJournal in new[] { false, true })
                    await FreshDeviceAfterRevocationAsync(root, deleteOld, sameConnection, hostJournal);
        foreach (var deleteOld in new[] { false, true })
            foreach (var hostJournal in new[] { false, true })
                await RetainedPeerRegistryRejoinAsync(root, deleteOld, hostJournal);
        await DamagedRetainedHistoryAsync(root);
    }

    private static async Task FreshDeviceAfterRevocationAsync(string root, bool deleteOld, bool sameConnection,
        bool hostJournal)
    {
        using var host = new Host(root);
        using var friend = new LocalData(Path.Combine(root, "rejoin-friend-" + Guid.NewGuid().ToString("N")));
        using var first = await ConnectAsync(friend, host, "first.protected");
        var firstCredential = host.LastCredential!;
        var initial = await first.CheckSharedWorldSharingAsync(host.Profile.Id);
        Require(initial.Available, "the first authenticated sharing check failed: " + initial.Code);
        var firstRoster = host.Current;
        var firstMember = firstRoster.Members.Single(item => item.DeviceId == firstCredential.DeviceId);
        // Peer metadata retains the exact roster independently of the saved connection.
        if (hostJournal) host.Chain.Append(firstRoster, firstRoster.OwnerPublicKey);
        var retained = new SharedWorldRosterChainStore(friend);
        retained.Append(firstRoster, firstRoster.OwnerPublicKey);
        Require(host.Pairing.Revoke(firstCredential.DeviceId).Ok, "synthetic revocation failed");
        host.Publish();
        Require(host.Pairing.Authenticate(firstCredential.DeviceId, firstCredential.Credential, out _).Code == "Revoked",
            "the revoked credential still authenticated");
        var revoked = await first.PollAsync();
        Require(revoked.ConnectionCode == "Revoked" &&
            !(await first.LocalHostingAccessAsync(host.Profile.Id, true, default, permissionOnly: true)).Allowed,
            "the old connection retained hosting access after explicit revocation");
        if (deleteOld)
        {
            Require(host.Pairing.DeleteRevokedDevice(firstCredential.DeviceId).Ok, "revoked record deletion failed");
            host.Publish();
        }
        friend.SaveProtected("revoked.protected", friend.LoadProtected("first.protected")!);
        using var old = CreateLink(friend, host, "revoked.protected");
        var currentBeforeRejoin = host.Current;
        using var fresh = sameConnection ? null : await ConnectAsync(friend, host, "fresh.protected");
        var rejoinedLink = fresh ?? first;
        if (sameConnection)
        {
            Require((await first.PairAsync(host.ServerCode)).Ok, "the previously revoked connection refused a fresh server-code activation");
            Require((await first.PollAsync()).Profiles.Single().Id == host.Profile.Id,
                "the reactivated connection retained its old revoked view");
        }
        var freshCredential = host.LastCredential!;
        Require(freshCredential.DeviceId != firstCredential.DeviceId &&
            freshCredential.Credential != firstCredential.Credential,
            "a fresh invitation reused revoked device or credential identity");
        var rejoined = await rejoinedLink.CheckSharedWorldSharingAsync(host.Profile.Id);
        Require(rejoined.Available, "a fresh invited device could not catch up retained signed history: " +
            rejoined.Code + "; " + rejoined.Message);
        var access = await rejoinedLink.LocalHostingAccessAsync(host.Profile.Id, true, default, permissionOnly: true);
        var newMember = host.Current.Members.Single(item => item.DeviceId == freshCredential.DeviceId);
        Require(access.Allowed && newMember.PublicKey != firstMember.PublicKey &&
            newMember is { Revoked: false, Grants: { Receive: true, EligibleHost: true, Reshare: true } } &&
            host.Current.GroupId == firstRoster.GroupId && host.Current.OwnerPublicKey == firstRoster.OwnerPublicKey &&
            retained.Contains(host.Profile.Id, firstRoster) &&
            (!hostJournal || retained.Contains(host.Profile.Id, currentBeforeRejoin)) &&
            retained.Contains(host.Profile.Id, host.Current),
            "rejoin reset signed history, reused the revoked signing key, or missed new hosting permission");
        Require((await old.PollAsync()).ConnectionCode is "Revoked" or "Unauthorized" &&
            !(await old.LocalHostingAccessAsync(host.Profile.Id, false, default, permissionOnly: true)).Allowed,
            "granting the fresh device resurrected the old connection");
    }

    private static async Task RetainedPeerRegistryRejoinAsync(string root, bool deleteOld, bool hostJournal)
    {
        using var host = new Host(root);
        using var data = new LocalData(Path.Combine(root, "peer-registry-rejoin-" + Guid.NewGuid().ToString("N")));
        HttpClient Client(string endpoint, IEnumerable<string> pins) => CreateClient(host, endpoint, pins);
        using var friends = new FriendService(data, Client);
        Require((await friends.PairAsync(host.ServerCode)).Ok &&
            (await friends.PollAsync()).Profiles.Single().Id == host.Profile.Id,
            "the composed old connection did not pair and authenticate");
        var oldCredential = host.LastCredential!;
        var oldConnectionId = friends.View().ConnectionId;
        Require((await friends.CheckSharedWorldSharingAsync(host.Profile.Id)).Available,
            "the composed old connection could not verify its initial sharing membership");
        var initial = host.Current;
        var sharedRoot = new SharedWorldPeerRoot(initial.OwnerPublicKey, initial.ProfileId, initial.GroupId);
        if (hostJournal) host.Chain.Append(initial, initial.OwnerPublicKey);
        host.EnablePeerMetadata();
        var manager = new HostManager(data);
        using (var originalPeers = new WorldPeerService(data, manager, friends, new PairingService(data), Client))
        {
            await originalPeers.RefreshMembershipsAsync(false, default);
            await originalPeers.SyncNowAsync(default);
            var original = originalPeers.Views().Single();
            Require(original.SelfDeviceId == oldCredential.DeviceId && original.CanHost &&
                host.PeerOperations.Any(item => item.DeviceId == oldCredential.DeviceId &&
                    item.Operation == WorldPeerOperations.Metadata),
                "the composed fixture did not retain a genuine followed group and signed metadata cursor");
        }
        var oldKey = data.LoadProtected($"shared-world-pc-signing-{oldCredential.DeviceId:N}.protected")!;
        var retained = new SharedWorldRosterChainStore(data);
        Require(retained.Contains(host.Profile.Id, initial) && data.HasProtected("world-peer-memberships.protected") &&
            Directory.EnumerateFiles(data.RootPath, $"world-peer-cursor-{sharedRoot.GroupKey}-*.protected").Any(),
            "the composed fixture has no protected registry, signed journal or peer cursor to retain");

        // Reopen the peer registry with the old self identity and key still
        // present, as on a PC that followed metadata before losing access.
        using var peers = new WorldPeerService(data, manager, friends, new PairingService(data), Client);
        Require(peers.Views().Single().SelfDeviceId == oldCredential.DeviceId,
            "reopening the peer service discarded its old self identity");
        Require(host.Pairing.Revoke(oldCredential.DeviceId).Ok, "the composed old credential was not revoked");
        host.Publish();
        if (deleteOld)
        {
            Require(host.Pairing.DeleteRevokedDevice(oldCredential.DeviceId).Ok,
                "the composed revoked record could not be deleted");
            host.Publish();
        }
        await friends.PollAsync();
        var expectedOldCode = deleteOld ? "CredentialRejected" : "Revoked";
        Require(friends.View().Connections!.Single(item => item.ConnectionId == oldConnectionId).ConnectionCode == expectedOldCode,
            "the composed old connection did not observe its revoked access");
        var operationsBeforeRevokedSync = host.PeerOperations.Count;
        await peers.SyncNowAsync(default);
        Require(host.PeerChallenges.Any(item => item.DeviceId == oldCredential.DeviceId &&
                item.ResponseCode == "PeerRosterCatchUpRequired") &&
            host.PeerOperations.Count == operationsBeforeRevokedSync &&
            data.LoadProtected($"shared-world-pc-signing-{oldCredential.DeviceId:N}.protected")!.AsSpan().SequenceEqual(oldKey),
            "the revoked retained identity received metadata or lost its retained key");

        Require((await friends.PairAsync(host.ServerCode)).Ok &&
            (await friends.PollAsync()).Profiles.Single().Id == host.Profile.Id,
            "the composed fresh TS3 pairing did not authenticate");
        var freshCredential = host.LastCredential!;
        Require(freshCredential.DeviceId != oldCredential.DeviceId, "the composed rejoin reused the revoked identity");
        var sharing = await friends.CheckSharedWorldSharingAsync(host.Profile.Id);
        Require(sharing.Available, "retained peer state rejected the fresh signed sharing history: " +
            sharing.Code + "; " + sharing.Message);
        await peers.SyncNowAsync(default);
        var current = peers.Views().Single();
        var access = await friends.LocalHostingAccessAsync(host.Profile.Id, false, default);
        var newKey = data.LoadProtected($"shared-world-pc-signing-{freshCredential.DeviceId:N}.protected")!;
        Require(current.State.Root == sharedRoot && current.SelfDeviceId == freshCredential.DeviceId &&
            current.CanHost && current.CanReceive && access.Allowed &&
            host.PeerChallenges.Any(item => item.DeviceId == freshCredential.DeviceId && item.ResponseCode == "PeerChallengeReady") &&
            host.PeerOperations.Any(item => item.DeviceId == freshCredential.DeviceId && item.Operation == WorldPeerOperations.Metadata) &&
            !current.State.Peers.Any(item => item.DeviceId == oldCredential.DeviceId && item.Authorized) &&
            !newKey.AsSpan().SequenceEqual(oldKey) && retained.Contains(host.Profile.Id, initial) &&
            retained.Contains(host.Profile.Id, host.Current) &&
            host.PeerOperations.All(item => item.Operation is WorldPeerOperations.Metadata or WorldPeerOperations.Catalog),
            "fresh rejoin retained the old peer self identity, lost its signed history, or skipped the actual signed peer exchange");
        var freshConfig = data.LoadProtectedJson<FriendConfiguration>($"friend-{friends.View().ConnectionId:N}.protected")!;
        var floor = freshConfig.SharedRosterFloors[host.Profile.Id];
        using var reopened = new WorldPeerService(data, manager, friends, new PairingService(data), Client);
        Require(reopened.Views().Single().SelfDeviceId == freshCredential.DeviceId &&
            floor.GroupId == sharedRoot.GroupId && floor.Signature == host.Current.Signature &&
            (await friends.PollAsync()).Connections!.Single(item => item.ConnectionId == oldConnectionId).ConnectionCode == expectedOldCode &&
            !host.Pairing.Authenticate(oldCredential.DeviceId, oldCredential.Credential, out _).Ok,
            "reopening lost the fresh peer identity/floor or resurrected the old connection");
    }

    private static async Task DamagedRetainedHistoryAsync(string root)
    {
        foreach (var fault in new[] { "saved-lookup", "trusted-floor-lookup", "unreadable-page", "missing-parent", "bad-signature", "wrong-root", "fork" })
        {
            using var host = new Host(root);
            using var friend = new LocalData(Path.Combine(root, "damaged-rejoin-" + Guid.NewGuid().ToString("N")));
            using var first = await ConnectAsync(friend, host, "first.protected");
            Require((await first.CheckSharedWorldSharingAsync(host.Profile.Id)).Available, "initial synthetic history failed");
            var initial = host.Current;
            var oldCredential = host.LastCredential!;
            var retained = new SharedWorldRosterChainStore(friend);
            retained.Append(initial, initial.OwnerPublicKey);
            host.Chain.Append(initial, initial.OwnerPublicKey);
            Require(host.Pairing.Revoke(oldCredential.DeviceId).Ok, "synthetic revocation failed");
            host.Publish();
            using var fresh = await ConnectAsync(friend, host, "fresh.protected");
            var freshConfig = friend.LoadProtectedJson<FriendConfiguration>("fresh.protected")!;
            if (fault == "trusted-floor-lookup")
            {
                retained.Append(host.Current, host.Current.OwnerPublicKey);
                // Model an older retained floor/cache while another approved
                // metadata source has already advanced the shared journal.
                freshConfig.SharedRosterFloors[host.Profile.Id] = new(initial.GroupId, initial.Epoch,
                    initial.Revision, initial.Signature);
                freshConfig.SharedWorldSigningKeys[host.Profile.Id] = initial.OwnerPublicKey;
                freshConfig.ApprovedSharedWorldGroups[host.Profile.Id] = initial.GroupId;
                friend.SaveProtected("fresh.protected", JsonSerializer.SerializeToUtf8Bytes(freshConfig, Json));
            }
            if (fault is "saved-lookup" or "trusted-floor-lookup")
                friend.DeleteProtected($"shared-roster-lookup-{host.Profile.Id:N}-{SharedWorldRosterTrust.Hash(initial)}.protected");
            host.UnreadablePage = fault == "unreadable-page";
            host.PageTransform = page => fault switch
            {
                "missing-parent" => page.TakeLast(1).ToArray(),
                "bad-signature" => page.Select(item => item with { Signature = new string('A', 88) }).ToArray(),
                "wrong-root" => page.Select(item => host.OwnerSigned(item with { GroupId = Guid.NewGuid() })).ToArray(),
                "fork" => page.Select((item, index) => index == 0
                    ? host.OwnerSigned(item with { OwnerOverride = !item.OwnerOverride }) : item).ToArray(),
                _ => page
            };
            // Reload the synthetic protected configuration exactly as reopening
            // the saved fresh pairing would; retain its files and signing journal.
            using var reopened = CreateLink(friend, host, "fresh.protected");
            Require((await reopened.PollAsync()).Profiles.Count == 1, "the damaged-history pairing was not authenticated");
            var rejected = await reopened.CheckSharedWorldSharingAsync(host.Profile.Id);
            Require(!rejected.Available && rejected.Code == "RosterRejected" &&
                !(await reopened.LocalHostingAccessAsync(host.Profile.Id, false, default, permissionOnly: true)).Allowed,
                "a fresh invitation bypassed damaged, missing or forked retained sharing history: " + fault);
            if (fault is "saved-lookup" or "trusted-floor-lookup" or "unreadable-page" or "missing-parent" or "fork")
            {
                var expected = fault switch
                {
                    "saved-lookup" => "This PC could not read its saved sharing history.",
                    "trusted-floor-lookup" => "The final signed history could not be matched to this PC's trusted sharing record.",
                    "unreadable-page" => "The Host's signed sharing revisions could not be read.",
                    _ => "The received revisions could not be joined to this PC's saved sharing history."
                };
                Require(rejected.Message == "The signed sharing history failed verification. " + expected &&
                    !rejected.Message.Contains(friend.RootPath, StringComparison.Ordinal) &&
                    !rejected.Message.Contains(initial.OwnerPublicKey, StringComparison.Ordinal) &&
                    !rejected.Message.Contains("private-synthetic-host-path", StringComparison.Ordinal),
                    "the rejected history omitted its fixed verification step or exposed private exception data: " + fault);
            }
            var saved = friend.LoadProtectedJson<FriendConfiguration>("fresh.protected")!;
            Require(saved.SharedRosterFloors.Count == freshConfig.SharedRosterFloors.Count &&
                (fault != "trusted-floor-lookup" || saved.SharedRosterFloors[host.Profile.Id] == freshConfig.SharedRosterFloors[host.Profile.Id]) &&
                host.Pairing.Authenticate(oldCredential.DeviceId, oldCredential.Credential, out _).Code == "Revoked",
                "failed rejoin verification moved the trust floor or restored the old credential");
        }
    }

    private static async Task<FriendLink> ConnectAsync(LocalData data, Host host, string name)
    {
        var link = CreateLink(data, host, name);
        Require((await link.PairAsync(host.ServerCode)).Ok, "the current persistent TS3 server code was rejected");
        Require((await link.PollAsync()).Profiles.Single().Id == host.Profile.Id, "the fresh assignment was not visible");
        return link;
    }

    private static FriendLink CreateLink(LocalData data, Host host, string name) =>
        new(data, name, (endpoint, pins) => CreateClient(host, endpoint, pins));

    private static HttpClient CreateClient(Host host, string endpoint, IEnumerable<string> pins)
    {
        Require(endpoint.TrimEnd('/') == Host.Endpoint && pins.SequenceEqual([Host.Pin]),
            "the rejoin escaped its pinned transport");
        return new HttpClient(host, false) { BaseAddress = new(endpoint.TrimEnd('/') + "/") };
    }

    private sealed class Host : HttpMessageHandler
    {
        internal const string Endpoint = "https://192.0.2.111:5131";
        internal static readonly string Pin = new('A', 64);
        private readonly LocalData data;
        private readonly SharedWorldService worlds;
        private readonly SharedWorldEnrollmentNonces nonces = new();
        private readonly PairingInvite invite;
        private FriendService? peerFriends;
        private WorldPeerService? peerService;
        internal Guid HostId { get; } = Guid.NewGuid();
        internal string ServerCode => PairingPassword.Encode(invite);
        internal PairingCredential? LastCredential { get; private set; }
        internal ServerProfile Profile { get; }
        internal PairingService Pairing { get; }
        internal SharedWorldRosterChainStore Chain { get; }
        internal SharedWorldRoster Current { get; private set; }
        internal Func<IReadOnlyList<SharedWorldRoster>, IReadOnlyList<SharedWorldRoster>>? PageTransform { get; set; }
        internal bool UnreadablePage { get; set; }
        internal List<(Guid DeviceId, string ResponseCode)> PeerChallenges { get; } = [];
        internal List<(Guid DeviceId, string Operation)> PeerOperations { get; } = [];
        internal Func<WorldPeerExchangeRequest, Task>? AfterPeerExchange { get; set; }
        internal bool PeerOffline { get; set; }

        internal Host(string root)
        {
            data = new(Path.Combine(root, "rejoin-host-" + Guid.NewGuid().ToString("N")));
            Profile = new()
            {
                Id = Guid.NewGuid(),
                Kind = GameKinds.Valheim,
                WorldId = "SyntheticRejoin",
                SharedSavesEnabled = true
            };
            Profile.WorldDirectory = data.NewWorldDirectory(Profile.Id);
            data.SaveSettings(new() { Profiles = [Profile] });
            Pairing = new(data);
            worlds = new(data, new WorldBackupService(data, TimeProvider.System));
            Chain = new(data);
            invite = Pairing.IssueServer(Profile.Id, false, false, Endpoint, Pin, false);
            Current = worlds.PublishRoster(Profile, []);
        }

        internal void Publish()
        {
            Current = worlds.PublishRoster(Profile, Pairing.SharedRosterMembers(Profile.Id), applyHostingPolicy: true);
            Pairing.ConfirmSharedRosterPublished(Profile.Id, Current);
        }

        internal void EnablePeerMetadata()
        {
            // An authentic previously enabled owner membership, without
            // creating a CompanionServer or opening any listener.
            var root = new SharedWorldPeerRoot(Current.OwnerPublicKey, Profile.Id, Current.GroupId);
            data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 1,
                Groups = new[] { new { Root = root, Roster = Current, DeviceId = Guid.Empty, Name = "Synthetic owner",
                    ReceiveConsented = true, SharingEnabled = true, Bootstrap = (SharedWorldPeerRoute?)null } }
            }, Json));
            peerFriends = new(data);
            peerService = new(data, new HostManager(data), peerFriends, Pairing,
                (_, _) => throw new InvalidOperationException("the synthetic owner must not open another route"));
        }

        internal void DisableHosting()
        {
            Profile.SharedSavesEnabled = false;
            data.SaveSettings(new() { Profiles = [Profile] });
            Pairing.ReconcileSimpleSharedHosting(Profile.Id);
            Publish();
        }

        internal SharedWorldRoster OwnerSigned(SharedWorldRoster roster)
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(data.LoadProtected("shared-world-signing-key.protected")!, out _);
            return roster with
            {
                Signature = Convert.ToBase64String(key.SignData(
                SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256))
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/companion/world-peers/", StringComparison.Ordinal))
            {
                if (PeerOffline) throw new HttpRequestException("The synthetic peer is unavailable.");
                var service = peerService ?? throw new InvalidOperationException("peer metadata was not enabled in this fixture");
                Require(request.Method == HttpMethod.Post && request.Headers.Authorization is null &&
                    request.Headers.GetValues("X-TogetherServer-Capability").Single() == CompanionProtocol.SharedWorldPeersCapability,
                    "the composed peer request leaked a bearer credential or changed its fixed transport");
                await service.RefreshMembershipsAsync(false, token);
                var root = new SharedWorldPeerRoot(Current.OwnerPublicKey, Profile.Id, Current.GroupId);
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                if (path == $"/api/companion/world-peers/{root.GroupKey}/challenge")
                {
                    var challenge = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(bytes,
                        WorldPeerService.MaximumRosterHandshakeBytes);
                    var response = service.PrepareChallenge(root.GroupKey, challenge, Pin);
                    PeerChallenges.Add((challenge.DeviceId, response.Code));
                    return Reply(response);
                }
                Require(path == $"/api/companion/world-peers/{root.GroupKey}/exchange",
                    "the composed peer used an unexpected world or route");
                var exchange = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(bytes,
                    2 * 1024 * 1024);
                PeerOperations.Add((exchange.Request.Challenge.DeviceId, exchange.Request.Operation));
                var result = await service.ExchangeAsync(root.GroupKey, exchange.Request, exchange.Payload, token, Pin);
                if (AfterPeerExchange is not null) await AfterPeerExchange(exchange);
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(result)
                };
            }
            if (path == "/api/companion/pair")
            {
                var activation = await request.Content!.ReadFromJsonAsync<PairingActivation>(Json, token);
                Require(activation is not null && activation.ServerScope && activation.DeviceId == Profile.Id &&
                    activation.Code == invite.Code, "fake HTTP changed the reused persistent server code");
                LastCredential = Pairing.Activate(activation!) ??
                    throw new InvalidOperationException("fresh synthetic invitation activation failed");
                return Reply(LastCredential);
            }
            Require(Guid.TryParse(request.Headers.GetValues("X-Device-Id").Single(), out var deviceId),
                "fake HTTP lost the device identity");
            var authenticated = Pairing.Authenticate(deviceId, request.Headers.Authorization?.Parameter, out _);
            if (!authenticated.Ok) return Reply(authenticated, authenticated.Code switch
            {
                "RateLimited" => HttpStatusCode.TooManyRequests,
                "Revoked" or "ApprovalPending" or "AccessExpired" => HttpStatusCode.Forbidden,
                _ => HttpStatusCode.Unauthorized
            });
            Require(Pairing.CanAccess(PairingDevice(), Profile.Id), "fake HTTP lost the exact server assignment");
            PairedDevice PairingDevice()
            {
                Require(Pairing.TryGetActiveDevice(deviceId, out var current), "device changed during fake authentication");
                return current!;
            }
            if (path == "/api/companion/heartbeat")
                return Reply(new CompanionStatus(false, null,
                    [new(Profile.Id, "Synthetic rejoin", "Offline", null, Kind: GameKinds.Valheim)], false, false,
                    DateTimeOffset.UtcNow, new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                        peerService is null ? [CompanionProtocol.SharedWorldsCapability] :
                            [CompanionProtocol.SharedWorldsCapability, CompanionProtocol.SharedWorldPeersCapability])));
            if (path.EndsWith("/enrollment", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get) return Reply(new SharedWorldEnrollmentChallenge(nonces.Issue(deviceId, Profile.Id)));
                var enrollment = await request.Content!.ReadFromJsonAsync<SharedWorldEnrollmentRequest>(Json, token);
                Require(enrollment is not null && nonces.Consume(deviceId, Profile.Id, enrollment.Nonce),
                    "fake HTTP accepted a stale enrollment nonce");
                var bound = Pairing.BindSharedWorldKey(deviceId, enrollment!);
                if (bound.Ok) Publish();
                return Reply(bound, bound.Ok ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
            }
            if (path.EndsWith("/revisions/current", StringComparison.Ordinal)) return Reply(Current);
            if (path.EndsWith("/revisions", StringComparison.Ordinal))
            {
                var anchor = request.Headers.TryGetValues(SharedWorldRosterProtocol.AfterHeader, out var values) ? values.Single() : null;
                var offset = long.Parse(request.RequestUri.Query.AsSpan("?offset=".Length), System.Globalization.CultureInfo.InvariantCulture);
                IReadOnlyList<SharedWorldRoster>? page = Chain.HasState(Profile.Id)
                    ? anchor is null ? Chain.ReadPage(Profile.Id, offset) : Chain.ReadAfter(Profile.Id, anchor)
                    : anchor is null ? offset == 0 ? [Current] : []
                    : anchor == SharedWorldRosterTrust.Hash(Current) ? [] : null;
                if (page is not null && PageTransform is not null) page = PageTransform(page);
                var response = Reply(page ?? [], page is null ? HttpStatusCode.Conflict : HttpStatusCode.OK);
                if (UnreadablePage) response.Content = new UnreadableContent();
                if (anchor is not null) response.Headers.Add(SharedWorldRosterProtocol.AnchorHeader, anchor);
                return response;
            }
            throw new InvalidOperationException("Unexpected fake rejoin request: " + path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { peerService?.Dispose(); peerFriends?.Dispose(); data.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new UnreadableStream());
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("private-synthetic-host-path"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class UnreadableStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("private-synthetic-host-path"));
    }

    private static HttpResponseMessage Reply<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, options: Json) };
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

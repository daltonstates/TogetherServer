using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class HostingCoordinationChecks
{
    internal static async Task RunAsync(string parent)
    {
        var now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
        var schedule = new PeerReplicationSchedule();
        Require(schedule.Due("route", 0, now), "new route was not due");
        schedule.Completed("route", 0, now, false, false, 1);
        Require(!schedule.Due("route", 0, now.AddSeconds(59)) && schedule.Due("route", 0, now.AddSeconds(60)),
            "unchanged discovery did not idle for one minute");
        Require(schedule.Due("route", 1, now), "publication/reconnection did not invalidate idle deadline");
        Require(schedule.NextDelay(now, 1) == TimeSpan.FromSeconds(1), "an unvisited route lost immediate publication wake behind its old idle deadline");
        for (var index = 0; index < 8; index++) schedule.Completed("route", 1, now, true, false, 1);
        Require(!schedule.Due("route", 1, now.AddSeconds(299)) && schedule.Due("route", 1, now.AddSeconds(300)),
            "offline retry did not back off within five minutes");
        schedule.Completed("route", 1, now, false, true, 1);
        Require(!schedule.Due("route", 1, now.AddSeconds(1)) && schedule.Due("route", 1, now.AddSeconds(2)),
            "resumable active work inherited offline backoff");
        schedule.Track("world:removed", 1, now);
        schedule.RetainRoutes("world", new HashSet<string>());
        schedule.RetainWorlds(new HashSet<string>());
        Require(schedule.NextDelay(now) == TimeSpan.FromSeconds(60), "removed routes left a permanently due scheduling entry");
        var time = new TestClock();
        var ownership = new PeerPullOwnership(time);
        ownership.Served("world", "save", "member", 0, 1);
        Require(ownership.Active("world", "save", "member") && !ownership.Active("world", "other", "member") &&
            !ownership.Active("world", "save", "other"), "pull scheduling crossed exact save/recipient identity");
        time.Advance(TimeSpan.FromSeconds(60));
        Require(!ownership.Active("world", "save", "member"), "abandoned pull permanently prevented offer retry");
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original");
        var wakes = 0;
        fixture.TargetService.BackgroundSyncRequested += () => wakes++;
        var profile = new PublicProfile(Guid.NewGuid(), "Generated", "Ready", null, Kind: GameKinds.Valheim,
            OnlinePlayers: 0, PlayerCountObservedUtc: now);
        var connected = new FriendView("Friend", "Connected", "", "https://192.0.2.1:5131", now,
            true, true, true, [profile], [CompanionProtocol.SharedWorldPeersCapability], ConnectionId: Guid.NewGuid());
        Require(fixture.TargetService.ObserveFriendSnapshot(connected) && wakes == 1,
            "new pinned connection did not wake automatic enrollment");
        Require(!fixture.TargetService.ObserveFriendSnapshot(connected with
        {
            LastConnectedUtc = now.AddSeconds(5),
            Profiles = [profile with { OnlinePlayers = 2, PlayerCountObservedUtc = now.AddSeconds(5) }],
            Activity = [new(Guid.NewGuid(), now, "Players", "CountChanged", "Generated")]
        }) && wakes == 1,
            "routine heartbeat/player observations restarted enrollment or transfer work");
        var sharing = connected with
        {
            Activity = [new(Guid.NewGuid(), now, "Backup", "SharedSavesEnabled", "Generated",
            ProfileId: profile.Id, Visibility: ActivityVisibility.AssignedFriends)]
        };
        Require(fixture.TargetService.ObserveFriendSnapshot(sharing) && wakes == 2 &&
            !fixture.TargetService.ObserveFriendSnapshot(sharing), "new sharing policy was delayed by the idle schedule or replayed unchanged");
        var stopped = sharing with
        {
            Activity = [.. sharing.Activity!, new(Guid.NewGuid(), now, "Lifecycle", "Stopped", "Generated",
                ProfileId: profile.Id, Visibility: ActivityVisibility.AssignedFriends)]
        };
        Require(fixture.TargetService.ObserveFriendSnapshot(stopped) && wakes == 3 &&
            !fixture.TargetService.ObserveFriendSnapshot(stopped with { LastConnectedUtc = now.AddSeconds(10) }),
            "a newly stopped assigned shared world did not wake discovery exactly once");
        Require(!fixture.TargetService.ObserveFriendSnapshot(stopped with
        {
            Activity = [.. stopped.Activity!, new(Guid.NewGuid(), now, "Lifecycle", "Stopped", "Unassigned",
                ProfileId: Guid.NewGuid(), Visibility: ActivityVisibility.AssignedFriends)]
        }), "an unassigned stopped-world notice restarted this connection's enrollment");
        Require(fixture.TargetService.ObserveFriendSnapshot(stopped with { State = "Disconnected/Unknown" }) &&
            fixture.TargetService.ObserveFriendSnapshot(stopped) && wakes == 5,
            "authenticated reconnection did not invalidate enrollment scheduling");
        Require(fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "PeerRequest").Count == 0,
            "observing a scheduling snapshot itself executed a transfer");
        _ = fixture.TargetService.Views();
        var before = fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "SignedRegistry").Count;
        _ = fixture.TargetService.Views();
        Require(before > 0 && fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "SignedRegistry").Count == before,
            "unchanged exact protected bytes repeated registry signature verification");
        // Same length/mtime cannot authorize different signed registry content.
        var path = Path.Combine(fixture.Target.RootPath, "world-peer-memberships.protected");
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = fixture.Target.LoadProtected("world-peer-memberships.protected")!;
        using var document = JsonDocument.Parse(bytes);
        var signature = document.RootElement.GetProperty("groups")[0].GetProperty("roster").GetProperty("signature").GetString()!;
        var replacement = (signature[0] == 'A' ? "B" : "A") + signature[1..];
        var changed = JsonNode.Parse(bytes)!;
        changed["groups"]![0]!["roster"]!["signature"] = replacement;
        var encoded = JsonSerializer.SerializeToUtf8Bytes(changed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Require(encoded.Length == bytes.Length && !encoded.SequenceEqual(bytes), "the synthetic signed registry mutation did not change equal-length bytes");
        fixture.Target.SaveProtected("world-peer-memberships.protected", encoded);
        File.SetLastWriteTimeUtc(path, stamp);
        var refused = false;
        try { _ = fixture.TargetService.Views(); }
        catch (InvalidDataException) { refused = true; }
        Require(refused, "changed signed registry reused verification based on timestamps");
        MembershipRetryBounds();
        await EnrollmentRaceAsync(parent, unavailable: false);
        await EnrollmentRaceAsync(parent, unavailable: true);
        await PeerMembershipIndependentRefreshChecks.RunAsync(parent);
    }

    private static void MembershipRetryBounds()
    {
        var time = new TestClock();
        var retry = new PeerMembershipRetryBurst(time);
        Require(retry.NextDelay(true) is null, "idle registration started a fast retry without a material observation");
        retry.Begin();
        Require(retry.NextDelay(false) is null, "a completed registration was scheduled as pending");
        for (var index = 0; index < 8; index++) Require(retry.NextDelay(true) == TimeSpan.FromSeconds(2), "pending registration lost its bounded retry budget");
        Require(retry.NextDelay(true) is null, "unfinished registration retried permanently after exhausting its attempt budget");
        retry.Begin(); time.Advance(TimeSpan.FromSeconds(25));
        Require(retry.NextDelay(true) is null, "unfinished registration extended the material observation's retry deadline");
    }

    private static async Task EnrollmentRaceAsync(string parent, bool unavailable)
    {
        var folder = Path.Combine(parent, "registration-race-" + Guid.NewGuid().ToString("N"));
        using var hostData = new LocalData(Path.Combine(folder, "owner"));
        using var data = new LocalData(Path.Combine(folder, "receiver"));
        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device = Guid.NewGuid();
        var profile = Guid.NewGuid();
        using var host = new RegistrationRaceHost(hostData, profile, device,
            Convert.ToBase64String(member.ExportSubjectPublicKeyInfo()), unavailable);
        var configuration = new FriendConfiguration
        {
            DeviceId = device,
            HostId = Guid.NewGuid(),
            Credential = new string('E', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(60),
            Endpoint = "https://127.0.0.1:5131",
            Fingerprint = new string('A', 64)
        };
        data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(configuration, Json));
        data.SaveProtected($"shared-world-pc-signing-{device:N}.protected", member.ExportPkcs8PrivateKey());
        using var friends = new FriendService(data, (endpoint, pins) =>
        {
            Require(endpoint == configuration.Endpoint && pins.SequenceEqual([configuration.Fingerprint]), "registration escaped its saved exact origin/pin");
            return new(host, false) { BaseAddress = new Uri(endpoint + "/") };
        });
        using var peers = new WorldPeerService(data, new HostManager(data), friends, new PairingService(data),
            (_, _) => throw new InvalidOperationException("Loopback registration must not create a peer route."));
        Require(peers.ObserveFriendSnapshot(await friends.PollAsync()), "the generated connection did not open a material registration burst");
        peers.ScheduleSync(default);
        await peers.WaitForSyncAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Require(host.Advanced && host.Proofs == 1 && peers.Views().Count == 0,
            "the first enrollment did not cross the actual signed roster advance/transient publication boundary");
        var firstDiagnostic = peers.WorkSnapshot().Membership;
        Require(firstDiagnostic is
        {
            PassesStarted: 1, PassesCompleted: 1, PassOutcome: "Completed",
            LookupsStarted: 1, LookupsCompleted: 1, LookupReturnedRoster: false
        } &&
            firstDiagnostic.LookupOutcome == (unavailable ? "RosterUnavailable" : "RosterCatchUpPending"),
            "the read-only registration diagnostic lost the actual incomplete lookup outcome or pass boundary");
        var delay = peers.NextBackgroundSyncDelay;
        Require(delay <= TimeSpan.FromSeconds(2) && delay > TimeSpan.Zero,
            "a typed incomplete enrollment was discarded into the one-minute idle deadline");
        // Identical heartbeat state cannot supply another material wake. Only
        // the original pass's typed pending outcome can schedule this retry.
        Require(!peers.ObserveFriendSnapshot(await friends.PollAsync()), "the race fixture supplied an unrelated heartbeat wake");
        await Task.Delay(delay + TimeSpan.FromMilliseconds(50));
        peers.ScheduleSync(default);
        await peers.WaitForSyncAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var group = peers.Views().Single();
        Require(host.Proofs == 2 && group.State.Root == host.Root && group.SelfDeviceId == device &&
            group.ReceiveConsented && group.CanReceive && group.CanHost && group.CanReshare &&
            new SharedWorldRosterChainStore(data).Heads(profile).Single().Signature == host.Latest.Signature &&
            data.LoadRuns().Count == 0 && hostData.LoadRuns().Count == 0,
            "the bounded retry did not complete current signed enrollment/automatic consent without a game or another instruction");
        Require(peers.WorkSnapshot().Membership is
        {
            PassesStarted: 2, PassesCompleted: 2, PassOutcome: "Completed",
            LookupsStarted: 2, LookupsCompleted: 2, LookupOutcome: "PermissionDenied", LookupReturnedRoster: true
        },
            "the diagnostic confused a management-only denial with a missing verified hosting roster");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class RegistrationRaceHost : HttpMessageHandler
    {
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly SharedWorldRosterChainStore chain;
        private readonly SharedWorldRoster first;
        private readonly Guid profile, device;
        private readonly string member;
        private readonly bool unavailable;
        private string? nonce;
        private int currentReplies;
        internal int Proofs { get; private set; }
        internal bool Advanced { get; private set; }
        internal SharedWorldPeerRoot Root { get; }
        internal SharedWorldRoster Latest { get; }
        internal RegistrationRaceHost(LocalData data, Guid profile, Guid device, string member, bool unavailable)
        {
            this.profile = profile; this.device = device; this.member = member; this.unavailable = unavailable;
            var publicOwner = Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo());
            var members = new[] { new SharedWorldRosterMember(device, member, new(Receive: true, EligibleHost: true, Reshare: true), false) };
            first = Sign(new(3, Guid.NewGuid(), profile, 1, 1, true, publicOwner, members, "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: publicOwner, OwnerLocalBaselineMembers: members));
            var nextMembers = members.Append(new(Guid.NewGuid(), Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()),
                new(Receive: true, EligibleHost: true, Reshare: true), false)).OrderBy(item => item.DeviceId).ToArray();
            Latest = Sign(first with
            {
                Epoch = 2,
                Revision = 2,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(first),
                Members = nextMembers,
                OwnerLocalBaselineMembers = nextMembers,
                Signature = ""
            });
            Root = new(publicOwner, profile, first.GroupId);
            chain = new(data);
            chain.Append(first, publicOwner);
        }
        private SharedWorldRoster Sign(SharedWorldRoster value) => value with
        { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(value), HashAlgorithmName.SHA256)) };
        private void Advance()
        {
            if (Advanced) return;
            // A second approved PC's enrolled key legitimately advances the
            // owner-signed chain between this PC's current and page requests.
            chain.Append(Latest, Root.OwnerPublicKey);
            Advanced = true;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.Headers.Authorization?.Parameter == new string('E', 64) &&
                request.Headers.GetValues("X-Device-Id").Single() == device.ToString(), "registration lost its current saved device authentication");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/companion/heartbeat")
                return Reply(new CompanionStatus(false, null, [new(profile, "Generated", "Offline", null, Kind: GameKinds.Valheim)],
                    false, false, DateTimeOffset.UtcNow, new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                        [CompanionProtocol.SharedWorldsCapability, CompanionProtocol.SharedWorldPeersCapability])));
            var prefix = $"/api/companion/servers/{profile}/shared-world";
            if (path == prefix + "/enrollment" && request.Method == HttpMethod.Get)
            { nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); return Reply(new SharedWorldEnrollmentChallenge(nonce)); }
            if (path == prefix + "/enrollment" && request.Method == HttpMethod.Post)
            {
                var proof = await request.Content!.ReadFromJsonAsync<SharedWorldEnrollmentRequest>(Json, token);
                Require(proof is not null && proof.Nonce == nonce && proof.PublicKey == member && SharedWorldRosterTrust.VerifyEnrollment(device, proof),
                    "the generated enrollment did not prove its exact retained PC key");
                nonce = null; Proofs++;
                return Reply(new PairingDecision(true, "IdentityAlreadyEnrolled", "Generated"));
            }
            if (path == prefix + "/roster/revisions/current")
            {
                if (++currentReplies == 1)
                {
                    if (!unavailable) return Reply(first);
                    Advance();
                    return new(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
                }
                return Reply(Latest);
            }
            if (path == prefix + "/roster/revisions")
            {
                Advance();
                return Reply(chain.ReadPage(profile, 0));
            }
            throw new InvalidOperationException("Unexpected generated registration operation.");
        }
        private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) };
        protected override void Dispose(bool disposing) { if (disposing) { owner.Dispose(); other.Dispose(); } base.Dispose(disposing); }
    }

    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

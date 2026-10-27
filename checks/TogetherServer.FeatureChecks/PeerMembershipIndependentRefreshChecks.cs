using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Generated protected state and real signed peer/companion exchanges through
// one in-memory handler. No socket, listener, app or game process is created.
internal static class PeerMembershipIndependentRefreshChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Credential = "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";
    private static readonly SharedWorldPeerRoute Route = new("https://192.0.2.193:5131", new string('A', 64));

    internal static async Task RunAsync(string parent)
    {
        await RunAsync(parent, transientPending: false);
        await RunAsync(parent, transientPending: true);
    }

    private static async Task RunAsync(string parent, bool transientPending)
    {
        var folder = Path.Combine(parent, "independent-membership-" + Guid.NewGuid().ToString("N"));
        using var remoteData = new LocalData(Path.Combine(folder, "peer"));
        using var localData = new LocalData(Path.Combine(folder, "receiver"));
        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device = Guid.NewGuid();
        var profile = Guid.NewGuid();
        using var host = new HoldingHost(remoteData, profile, device,
            Convert.ToBase64String(member.ExportSubjectPublicKeyInfo()), transientPending);
        localData.SaveProtected($"shared-world-pc-signing-{device:N}.protected", member.ExportPkcs8PrivateKey());
        new SharedWorldRosterChainStore(localData).Append(host.Current, host.Root.OwnerPublicKey);
        localData.SaveProtected($"shared-world-roster-{device:N}-{profile:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(host.Current, Json));
        localData.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            DeviceId = device,
            HostId = Guid.NewGuid(),
            Credential = Credential,
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            Endpoint = Route.Address,
            Fingerprint = Route.CertificateSha256,
            ConsentedSharedWorldProfiles = [profile],
            ApprovedSharedWorldGroups = new() { [profile] = host.Root.GroupId },
            SharedWorldSigningKeys = new() { [profile] = host.Root.OwnerPublicKey },
            SharedRosterFloors = new()
            {
                [profile] = new(host.Root.GroupId, host.Current.Epoch,
                host.Current.Revision, host.Current.Signature)
            }
        }, Json));
        SaveRegistry(localData, host.Root, host.Current, device, Route);
        var ownerProfile = new ServerProfile
        {
            Id = Guid.NewGuid(),
            Name = "Generated local owner",
            Kind = GameKinds.Fixture,
            WorldId = "GeneratedOwner",
            WorldDirectory = Path.Combine(localData.RootPath, "generated-owner-world"),
            ExecutablePath = Path.Combine(localData.RootPath, "source-only.synthetic")
        };
        Directory.CreateDirectory(ownerProfile.WorldDirectory);
        File.WriteAllText(ownerProfile.ExecutablePath, "never executed");
        localData.SaveSettings(new HostSettings { Profiles = [ownerProfile] });
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint.TrimEnd('/') == Route.Address && pins.SequenceEqual([Route.CertificateSha256]),
                "the generated membership exchange escaped its exact pinned route");
            return new(host, false) { BaseAddress = new Uri(Route.Address + "/") };
        }
        using var friends = new FriendService(localData, Client);
        var manager = new HostManager(localData,
            new GameServerRegistry([new LocalHostingChecks.FilesOnlyDriver()], PortProbeMode.ObserveOnly));
        using var peers = new WorldPeerService(localData, manager, friends,
            new PairingService(localData), Client);
        using var stop = new CancellationTokenSource();
        try
        {
            var connected = await friends.PollAsync();
            Require(peers.ObserveFriendSnapshot(connected), "the generated initial connection did not invalidate membership");
            peers.ScheduleSync(stop.Token);
            await host.WireEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => peers.WorkSnapshot().Membership is { PassesCompleted: >= 1 });
            var before = peers.Views().Single();
            Require(host.PeerChallenges > 0 && host.PeerMetadataExchanges == 1 && host.Enrollments == 1 &&
                !before.CanReceive && !before.CanHost && !before.CanReshare && !host.ReleaseWire.Task.IsCompleted,
                "the fixture did not hold a real signed metadata response after a normal metadata-only registration");
            host.EnableHosting();
            ActivityEvent[] notices = [new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Backup", "SharedSavesEnabled", "Generated policy",
                ProfileId: profile, Visibility: ActivityVisibility.AssignedFriends)];
            var policy = connected with
            {
                Activity = notices,
                Connections = connected.Connections?.Select(connection => connection with { Activity = notices }).ToArray()
            };
            Require(peers.ObserveFriendSnapshot(policy), "the canonical policy change did not invalidate registration");
            peers.ScheduleSync(stop.Token);
            if (transientPending)
            {
                await UntilAsync(() => peers.WorkSnapshot().Membership is { PassesCompleted: 2 });
                Require(peers.WorkSnapshot().Membership is { LookupOutcome: "RosterUnavailable", LookupReturnedRoster: false } &&
                    peers.NextBackgroundSyncDelay <= TimeSpan.FromSeconds(2) && !host.ReleaseWire.Task.IsCompleted,
                    "the unfinished granted membership inherited the held peer pass's retry clock");
            }
            await UntilAsync(() =>
            {
                peers.ScheduleSync(stop.Token);
                var current = peers.Views().Single();
                return current.CanReceive && current.CanHost && current.CanReshare;
            });
            var expectedPasses = transientPending ? 3 : 2;
            await UntilAsync(() => peers.WorkSnapshot().Membership?.PassesCompleted == expectedPasses);
            Require(!host.ReleaseWire.Task.IsCompleted && host.PeerMetadataExchanges == 1 && host.Enrollments == expectedPasses &&
                peers.WorkSnapshot().Membership is { } membership && membership.PassesStarted == expectedPasses &&
                membership.PassesCompleted == expectedPasses &&
                new SharedWorldRosterChainStore(localData).Heads(profile).Single().Signature == host.Current.Signature &&
                localData.LoadRuns().Count == 0 && remoteData.LoadRuns().Count == 0,
                "new signed hosting permission waited for the older peer pass or repeated registration/started a process");
            if (!transientPending)
            {
                host.ReleaseWire.TrySetResult(true);
                await peers.WaitForSyncAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await NetworkRefreshCadenceAsync(peers, manager, host, policy, ownerProfile.Id, stop.Token);
            }
        }
        finally
        {
            stop.Cancel();
            host.ReleaseWire.TrySetResult(true);
            try { await peers.WaitForSyncAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task NetworkRefreshCadenceAsync(WorldPeerService peers, HostManager manager,
        HoldingHost host, FriendView priorPolicy, Guid ownerProfileId, CancellationToken cancellationToken)
    {
        var enrollments = host.Enrollments;
        var heartbeats = host.Heartbeats;
        var passes = peers.WorkSnapshot().Membership!.PassesCompleted;
        var notify = typeof(HostManager).GetMethod("NotifySharedSavePublished", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var index = 0; index < 3; index++)
        {
            // Start/Stop/publication dispatch this same fixed scheduling signal.
            // Exercise it without manufacturing a process or capturing a save.
            notify.Invoke(manager, []);
            peers.ScheduleSync(cancellationToken);
            await peers.WaitForSyncAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        Require(peers.WorkSnapshot().Membership!.PassesCompleted >= passes + 3 &&
            host.Enrollments == enrollments && host.Heartbeats == heartbeats,
            "publication/run wakes repeated companion enrollment or heartbeat instead of local registry refresh");

        Require((await manager.SetSimpleSharedHostingAsync(ownerProfileId, true, cancellationToken)).Ok,
            "the generated local owner's sharing policy did not commit");
        peers.ScheduleSync(cancellationToken);
        await peers.WaitForSyncAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Require(peers.Views().Any(group => group.ProfileId == ownerProfileId && group.SelfDeviceId == Guid.Empty && group.CanHost) &&
            host.Enrollments == enrollments && host.Heartbeats == heartbeats,
            "local-only policy refresh delayed the original owner registration or re-enrolled an unrelated saved Host");

        ActivityEvent[] notice = [new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Backup", "SharedSavesEnabled", "Generated later policy",
            ProfileId: host.Root.ProfileId, Visibility: ActivityVisibility.AssignedFriends)];
        var material = priorPolicy with
        {
            Activity = notice,
            Connections = priorPolicy.Connections?.Select(connection => connection with { Activity = notice }).ToArray()
        };
        Require(peers.ObserveFriendSnapshot(material), "the later Friend policy did not request a network roster check");
        peers.ScheduleSync(cancellationToken);
        await peers.WaitForSyncAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Require(host.Enrollments == ++enrollments && host.Heartbeats == heartbeats,
            "a changed Friend policy lost its network enrollment refresh or polled heartbeat again");

        // Move only the disposable service's periodic network deadline. No
        // clock, production setting, retry budget or transfer due time changes.
        typeof(WorldPeerService).GetField("nextMembershipNetworkUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(peers, DateTimeOffset.MinValue);
        peers.ScheduleSync(cancellationToken);
        await peers.WaitForSyncAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Require(host.Enrollments == ++enrollments && host.Heartbeats == heartbeats,
            "the periodic deadline did not refresh network membership exactly once");

        await peers.SyncNowAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Require(host.Enrollments == ++enrollments && host.Heartbeats == heartbeats,
            "explicit full Sync reused a local-only registration or added heartbeat work");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void SaveRegistry(LocalData data, SharedWorldPeerRoot root, SharedWorldRoster roster,
        Guid device, SharedWorldPeerRoute? bootstrap) => data.SaveProtected("world-peer-memberships.protected",
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 1,
                Groups = new[] { new { Root = root, Roster = roster, DeviceId = device, Name = "Generated metadata member",
                    ReceiveConsented = true, SharingEnabled = true, Bootstrap = bootstrap } }
            }, Json));

    private sealed class HoldingHost : HttpMessageHandler
    {
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa responder = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly LocalData data;
        private readonly Guid profile, device, responderDevice = Guid.NewGuid();
        private readonly string memberKey;
        private readonly bool transientPending;
        private readonly SharedWorldRosterChainStore chain;
        private readonly FriendService peerFriends;
        private readonly WorldPeerService peer;
        private SharedWorldRoster current;
        private string? nonce;
        private int enrollments, heartbeats, peerChallenges, peerMetadataExchanges;
        private int grantedCurrentReplies;
        internal int Enrollments => Volatile.Read(ref enrollments);
        internal int Heartbeats => Volatile.Read(ref heartbeats);
        internal int PeerChallenges => Volatile.Read(ref peerChallenges);
        internal int PeerMetadataExchanges => Volatile.Read(ref peerMetadataExchanges);
        internal SharedWorldRoster Current => Volatile.Read(ref current);
        internal SharedWorldPeerRoot Root { get; }
        internal TaskCompletionSource<bool> WireEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseWire { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HoldingHost(LocalData data, Guid profile, Guid device, string memberKey, bool transientPending)
        {
            this.data = data; this.profile = profile; this.device = device; this.memberKey = memberKey;
            this.transientPending = transientPending;
            var publicOwner = Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo());
            var members = new[]
            {
                new SharedWorldRosterMember(device, memberKey, new(), false),
                new SharedWorldRosterMember(responderDevice,
                    Convert.ToBase64String(responder.ExportSubjectPublicKeyInfo()), new(), false)
            }.OrderBy(member => member.DeviceId).ToArray();
            current = Sign(new(3, Guid.NewGuid(), profile, 1, 1, true, publicOwner, members, "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: publicOwner, OwnerLocalBaselineMembers: members));
            Root = new(publicOwner, profile, current.GroupId);
            chain = new(data);
            chain.Append(current, publicOwner);
            // This approved relay holds its own member key, never the original
            // owner's retained signing key. The companion response still carries
            // the owner's exact signed roster and full parent chain.
            data.SaveProtected($"shared-world-pc-signing-{responderDevice:N}.protected", responder.ExportPkcs8PrivateKey());
            SaveRegistry(data, Root, current, responderDevice, null);
            peerFriends = new(data, (_, _) => throw new HttpRequestException("Generated relay has no saved Host route."));
            peer = new(data, new HostManager(data), peerFriends, new PairingService(data));
        }

        internal void EnableHosting()
        {
            var old = Current;
            var members = old.Members.Select(member => member.DeviceId == device ? member with
            { Grants = new(Receive: true, EligibleHost: true, Reshare: true) } : member).ToArray();
            var next = Sign(old with
            {
                Epoch = old.Epoch + 1,
                Revision = old.Revision + 1,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(old),
                Members = members,
                OwnerLocalBaselineMembers = members,
                Signature = ""
            });
            chain.Append(next, Root.OwnerPublicKey);
            SaveRegistry(data, Root, next, responderDevice, null);
            Volatile.Write(ref current, next);
        }

        private SharedWorldRoster Sign(SharedWorldRoster roster) => roster with
        { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith($"/api/companion/world-peers/{Root.GroupKey}/", StringComparison.Ordinal))
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                if (path.EndsWith("/challenge", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref peerChallenges);
                    return Reply(peer.PrepareChallenge(Root.GroupKey,
                        WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(bytes, 2 * 1024 * 1024), Route.CertificateSha256));
                }
                var exchange = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(bytes, 2 * 1024 * 1024);
                var response = await peer.ExchangeAsync(Root.GroupKey, exchange.Request, exchange.Payload, token, Route.CertificateSha256);
                if (exchange.Request.Operation == WorldPeerOperations.Metadata && Interlocked.Increment(ref peerMetadataExchanges) == 1)
                {
                    WireEntered.TrySetResult(true);
                    await ReleaseWire.Task.WaitAsync(token);
                }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(response) };
            }
            Require(request.Headers.Authorization?.Parameter == Credential &&
                request.Headers.GetValues("X-Device-Id").Single() == device.ToString(),
                "the generated companion request lost its saved device authentication");
            if (path == "/api/companion/heartbeat")
            {
                Interlocked.Increment(ref heartbeats);
                return Reply(new CompanionStatus(false, null, [new(profile, "Generated", "Offline", null, Kind: GameKinds.Valheim)],
                    false, false, DateTimeOffset.UtcNow, new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                        [CompanionProtocol.SharedWorldsCapability, CompanionProtocol.SharedWorldPeersCapability])));
            }
            var prefix = $"/api/companion/servers/{profile}/shared-world";
            if (path == prefix + "/enrollment" && request.Method == HttpMethod.Get)
            { nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); return Reply(new SharedWorldEnrollmentChallenge(nonce)); }
            if (path == prefix + "/enrollment" && request.Method == HttpMethod.Post)
            {
                var proof = await request.Content!.ReadFromJsonAsync<SharedWorldEnrollmentRequest>(Json, token);
                Require(proof is not null && proof.Nonce == nonce && proof.PublicKey == memberKey &&
                    SharedWorldRosterTrust.VerifyEnrollment(device, proof), "the generated enrollment proof changed identity");
                nonce = null;
                Interlocked.Increment(ref enrollments);
                return Reply(new PairingDecision(true, "IdentityAlreadyEnrolled", "Generated"));
            }
            if (path == prefix + "/roster/revisions/current")
            {
                if (transientPending && Current.Revision > 1 && Interlocked.Increment(ref grantedCurrentReplies) == 1)
                    return new(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
                return Reply(Current);
            }
            if (path == prefix + "/roster/revisions") return Reply(chain.ReadPage(profile, 0));
            throw new InvalidOperationException("Unexpected generated membership operation.");
        }

        private static HttpResponseMessage Reply<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) };
        protected override void Dispose(bool disposing)
        {
            if (disposing) { ReleaseWire.TrySetResult(true); peer.Dispose(); peerFriends.Dispose(); owner.Dispose(); responder.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

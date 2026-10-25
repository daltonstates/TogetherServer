using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Protected disposable state and fixed fake HTTPS only. No listener, process,
// app, game, browser or production data is used by these discovery checks.
internal static class WorldPeerHostingDiscoveryChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent)
    {
        MaterialObservationFence(parent);
        await IndependentWorldAndWireProofsAsync(parent);
        await KnownHeadRefusalAsync(parent, fork: false);
        await KnownHeadRefusalAsync(parent, fork: true);
        await CurrentAccessRefusalAsync(parent, expired: false);
        await CurrentAccessRefusalAsync(parent, expired: true);
    }

    private static void MaterialObservationFence(string parent)
    {
        using var fixture = new Fixture(parent, "material-observation");
        var world = fixture.Target;
        var store = Store(fixture.Data, world.Root, world.Roster);
        var first = store.ReadPage().Announcements.Single(item => item.DeviceId == Guid.Empty);
        var session = new SharedWorldPeerSession(Guid.NewGuid(), first.Branches.Single().BranchHash, 1,
            "Hosting", world.Version.VersionHash, world.Version.VersionHash, DateTimeOffset.UtcNow);
        var current = store.Publish(Guid.Empty, fixture.Owner, first.Routes, [session], first.Branches,
            first.AvailableVersionHashes);
        var original = fixture.Service.LatestObservationHash(world.Root);
        Require(original is not null, "the material observation fixture did not have a valid initial fence");

        // Publish deliberately coalesces identical payloads. Import a correctly
        // chained, signed repeat to exercise a real newer announcement revision
        // without changing any projected copy, head, session or permission.
        var repeat = SharedWorldPeerTrust.Sign(current with
        {
            Revision = current.Revision + 1,
            PreviousHash = current.AnnouncementHash,
            AnnouncedUtc = current.AnnouncedUtc.AddTicks(1),
            AnnouncementHash = "",
            Signature = ""
        }, fixture.Owner);
        Require(store.Import(repeat).Changed && repeat.Revision > current.Revision &&
            repeat.AnnouncedUtc > current.AnnouncedUtc && fixture.Service.LatestObservationHash(world.Root) == original,
            "a signed repeat with only an advanced announcement revision/time invalidated the material save fence");

        var extraCopy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic additional advertised copy")));
        current = store.Publish(Guid.Empty, fixture.Owner, repeat.Routes, repeat.Sessions, repeat.Branches,
            [.. repeat.AvailableVersionHashes, extraCopy]);
        var copiesChanged = fixture.Service.LatestObservationHash(world.Root);
        Require(copiesChanged is not null && copiesChanged != original,
            "a newly claimed exact copy did not invalidate the material save fence");

        var head = SharedWorldService.SignVersion(world.Version with
        {
            Number = 2,
            ParentHash = world.Version.VersionHash,
            BackupId = Guid.NewGuid(),
            Files = [Fact(Encoding.UTF8.GetBytes("synthetic newer signed observation head"))]
        }, fixture.Owner);
        current = store.Publish(Guid.Empty, fixture.Owner, current.Routes, current.Sessions,
            [current.Branches.Single() with { HeadVersionHash = head.VersionHash,
                ParentVersionHash = world.Version.VersionHash, VersionNumber = 2 }], current.AvailableVersionHashes);
        var headChanged = fixture.Service.LatestObservationHash(world.Root);
        Require(headChanged is not null && headChanged != copiesChanged,
            "a new known signed head did not invalidate the material save fence");

        current = store.Publish(Guid.Empty, fixture.Owner, current.Routes,
            [session with { Revision = 2, State = "Stopped", HeadVersionHash = head.VersionHash,
                EndedUtc = session.StartedUtc!.Value.AddSeconds(1) }], current.Branches, current.AvailableVersionHashes);
        var sessionChanged = fixture.Service.LatestObservationHash(world.Root);
        Require(sessionChanged is not null && sessionChanged != headChanged,
            "a changed signed hosting session did not invalidate the material save fence");

        var consent = new WorldPeerReceiveConsentStore(fixture.Data);
        Require(consent.Set(world.Root, Guid.Empty, false), "the synthetic current-consent withdrawal failed");
        var consentChanged = fixture.Service.LatestObservationHash(world.Root);
        Require(consentChanged is null || consentChanged != sessionChanged,
            "current local consent withdrawal did not invalidate the material save fence");
        Require(consent.Set(world.Root, Guid.Empty, true) && fixture.Service.LatestObservationHash(world.Root) == sessionChanged,
            "restoring the same material consent left an unrelated consent-revision fence change");

        var competing = SharedWorldPeerTrust.Sign(current with
        { AnnouncedUtc = current.AnnouncedUtc.AddTicks(1), AnnouncementHash = "", Signature = "" }, fixture.Owner);
        Require(store.Import(competing).Code == "PeerConflictPreserved" && store.Project().Peers.Any(item => item.HasConflict),
            "the synthetic same-revision signed conflict was not preserved for review");
        var conflictChanged = fixture.Service.LatestObservationHash(world.Root);
        Require(conflictChanged is null || conflictChanged != sessionChanged,
            "a newly preserved signer conflict did not invalidate the material save fence");
        world.VerifyCachedCopy();
        Require(fixture.Data.LoadRuns().Count == 0, "observation-fence checks recorded a game run");
    }

    private static async Task IndependentWorldAndWireProofsAsync(string parent)
    {
        using var fixture = new Fixture(parent, "independent");
        fixture.Other.Handler.HoldMetadata = true;
        fixture.Other.Handler.HoldCatalog = true;
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var background = fixture.Service.SyncNowAsync(stopping.Token);
        try
        {
            await fixture.Other.Handler.MetadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var targetProgress = new List<WorldPeerSourceProgress>();
            var target = await fixture.Service.ResolveLatestPreparedSourceAsync(fixture.Target.Root,
                default, targetProgress.Add).WaitAsync(TimeSpan.FromSeconds(5));
            Require(target.Ok && target.Source?.Descriptor.Version.VersionHash == fixture.Target.Version.VersionHash &&
                !background.IsCompleted && targetProgress.All(item => item.Phase != "PreparingCopy") &&
                fixture.Target.Handler.OnlyMetadata && fixture.Other.Handler.PayloadRequests == 0,
                "another world's held metadata/global background work delayed the exact prepared target or caused a transfer");
            fixture.Target.VerifyCachedCopy();

            using (var cancel = new CancellationTokenSource())
            {
                var waiting = fixture.Service.ResolveLatestPreparedSourceAsync(fixture.Other.Root, cancel.Token);
                cancel.Cancel();
                await RequireCancellationAsync(waiting, cancel.Token);
            }
            // A same-world metadata wait exhausts the actual ten-second budget,
            // then only the unchanged strict cached-source checks may succeed.
            var budgeted = await fixture.Service.ResolveLatestPreparedSourceAsync(fixture.Other.Root, default)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Require(budgeted.Ok && budgeted.Source?.Descriptor.Version.VersionHash == fixture.Other.Version.VersionHash &&
                !background.IsCompleted && fixture.Other.Handler.PayloadRequests == 0,
                "the discovery budget excluded a metadata semaphore wait or bypassed strict cached-copy selection");

            fixture.Other.Handler.ReleaseMetadata.TrySetResult(true);
            await fixture.Other.Handler.CatalogEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var challenges = fixture.Other.Handler.Challenges;
            using (var cancel = new CancellationTokenSource())
            {
                var waiting = fixture.Service.ResolveLatestPreparedSourceAsync(fixture.Other.Root, cancel.Token);
                cancel.Cancel();
                await RequireCancellationAsync(waiting, cancel.Token);
            }
            Require(fixture.Other.Handler.Challenges == challenges,
                "prepared metadata replaced the pending same-world payload challenge while its exchange was in flight");
            var unrelated = await fixture.Service.ResolveLatestPreparedSourceAsync(fixture.Target.Root, default)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Require(unrelated.Ok && fixture.Target.Handler.OnlyMetadata && fixture.Other.Handler.PayloadRequests == 1,
                "a blocked other-world catalog held target metadata or prepared discovery sent payload operations");
        }
        finally
        {
            fixture.Other.Handler.ReleaseMetadata.TrySetResult(true);
            fixture.Other.Handler.ReleaseCatalog.TrySetResult(true);
            await background;
        }
        Require(fixture.Other.Handler.CompletedCatalogs > 0 && fixture.Other.Handler.ProofFailures == 0 &&
            fixture.Target.Handler.ProofFailures == 0 && fixture.Data.LoadRuns().Count == 0,
            "concurrent discovery invalidated a single-use payload challenge or recorded a game run");
        fixture.Target.VerifyCachedCopy();
        fixture.Other.VerifyCachedCopy();
    }

    private static async Task KnownHeadRefusalAsync(string parent, bool fork)
    {
        using var fixture = new Fixture(parent, fork ? "known-fork" : "known-missing-newer");
        var first = fixture.Target.Version;
        var remoteBytes = Encoding.UTF8.GetBytes("synthetic different completed head");
        var claim = SharedWorldService.SignVersion(first with
        {
            Number = 2,
            ParentHash = first.VersionHash,
            BackupId = Guid.NewGuid(),
            Files = [Fact(remoteBytes)]
        }, fixture.Owner);
        if (fork)
        {
            fixture.Capture.Set(fixture.Target.Profile, Encoding.UTF8.GetBytes("synthetic local competing head"));
            Require(fixture.Publisher.PublishAfterStop(fixture.Target.Profile, Guid.NewGuid()).Ok,
                "synthetic competing publication failed");
        }
        fixture.Target.Announce(claim);
        var result = await fixture.Service.ResolveLatestPreparedSourceAsync(fixture.Target.Root, default);
        Require(!result.Ok && result.Code == "SharedSaveHistoryNeedsReview" && result.Source is null &&
            fixture.Target.Handler.OnlyMetadata && fixture.Data.LoadRuns().Count == 0,
            "metadata-only prepared discovery started an older/incomplete/competing history or downloaded it silently");
        fixture.Target.VerifyCachedCopy();
    }

    private static async Task CurrentAccessRefusalAsync(string parent, bool expired)
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var data = new LocalData(Path.Combine(parent, "prepared-access-" + Guid.NewGuid().ToString("N")));
        var root = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var id = Guid.NewGuid();
        var roster = SignRoster(new(3, root.GroupId, root.ProfileId, 1, 1, true, root.OwnerPublicKey,
            [new(id, Public(member), new(Receive: true, EligibleHost: true, Reshare: true), false)], "",
            SignerDeviceId: Guid.Empty, SignerPublicKey: root.OwnerPublicKey), owner);
        var route = new SharedWorldPeerRoute("https://192.0.2.188:5131", new string('F', 64));
        data.SaveSettings(new() { Profiles = [] });
        data.SaveProtected($"shared-world-pc-signing-{id:N}.protected", member.ExportPkcs8PrivateKey());
        new SharedWorldRosterChainStore(data).Append(roster, root.OwnerPublicKey);
        SaveGroups(data, [Membership(root, roster, id, route)]);
        data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            DeviceId = id,
            Endpoint = route.Address,
            Fingerprint = route.CertificateSha256,
            Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(60),
            ConsentedSharedWorldProfiles = [root.ProfileId],
            ApprovedSharedWorldGroups = new() { [root.ProfileId] = root.GroupId },
            SharedWorldSigningKeys = new() { [root.ProfileId] = root.OwnerPublicKey },
            SharedRosterFloors = new() { [root.ProfileId] = new(root.GroupId, roster.Epoch, roster.Revision, roster.Signature) }
        }, Json));
        data.SaveProtected($"shared-world-roster-{id:N}-{root.ProfileId:N}.protected", JsonSerializer.SerializeToUtf8Bytes(roster, Json));
        var bytes = Encoding.UTF8.GetBytes("synthetic exact prepared member copy");
        var version = Version(root, owner, bytes);
        var lane = Lane(root, id);
        WriteCopy(Path.Combine(data.RootPath, "received-shared-worlds", id.ToString("N"), root.ProfileId.ToString("N")), version, bytes);
        var cached = WorldPeerRecoverySource.StorageRoot(data, root, lane, version.VersionHash);
        WriteCopy(cached, version, bytes);
        Store(data, root, roster).Publish(id, member, [], [], [new(lane, version.VersionHash, version.VersionHash, null, 1)]);
        HttpClient Offline(string _, IEnumerable<string> __) => throw new HttpRequestException("Synthetic peer is unavailable.");
        using var friends = new FriendService(data, Offline);
        using var service = new WorldPeerService(data, new HostManager(data), friends, new PairingService(data), Offline);
        Require((await service.ResolveLatestPreparedSourceAsync(root, default)).Ok,
            "the current-access fixture lacked an initially valid exact prepared copy");
        var permittedObservation = service.LatestObservationHash(root);
        Require(permittedObservation is not null, "current access did not produce a valid material observation fence");
        var members = roster.Members.Select(item => item with
        {
            Revoked = !expired,
            AccessExpiresUtc = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : null
        }).ToArray();
        var denied = SignRoster(roster with
        {
            Epoch = 2,
            Revision = 2,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(roster),
            Members = members,
            OwnerLocalBaselineMembers = members,
            Signature = ""
        }, owner);
        new SharedWorldRosterChainStore(data).Append(denied, root.OwnerPublicKey);
        SaveGroups(data, [Membership(root, denied, id, route)]);
        await friends.ApplyPeerRosterAsync(denied, default);
        var result = await service.ResolveLatestPreparedSourceAsync(root, default);
        var deniedObservation = service.LatestObservationHash(root);
        Require(!result.Ok && result.Code == "HostingPermissionRequired" && result.Source is null &&
            (deniedObservation is null || deniedObservation != permittedObservation) &&
            FriendLink.ReadReceivedLatest(cached)?.VersionHash == version.VersionHash && data.LoadRuns().Count == 0,
            "known revocation/expiry reused the prepared copy as hosting authority");
    }

    private static async Task RequireCancellationAsync(Task<WorldPeerLatestSourceResult> operation, CancellationToken token)
    {
        try { _ = await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        throw new InvalidOperationException("caller cancellation did not propagate from a prepared discovery gate wait");
    }

    private sealed class Fixture : IDisposable
    {
        internal ECDsa Owner { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal LocalData Data { get; }
        internal Capture Capture { get; }
        internal SharedWorldService Publisher { get; }
        internal World Target { get; }
        internal World Other { get; }
        internal WorldPeerService Service { get; }
        private readonly FriendService friends;

        internal Fixture(string parent, string name)
        {
            Data = new(Path.Combine(parent, "prepared-discovery-" + name + "-" + Guid.NewGuid().ToString("N")));
            var target = new ServerProfile
            {
                Kind = GameKinds.Fixture,
                SharedSavesEnabled = true,
                WorldId = "TargetFilesOnly",
                WorldDirectory = Path.Combine(Data.RootPath, "never-host-target")
            };
            var other = new ServerProfile
            {
                Kind = GameKinds.Fixture,
                SharedSavesEnabled = true,
                WorldId = "OtherFilesOnly",
                WorldDirectory = Path.Combine(Data.RootPath, "never-host-other"),
                GamePort = 2458
            };
            Data.SaveSettings(new() { Profiles = [other, target] });
            Data.SaveProtected("shared-world-signing-key.protected", Owner.ExportPkcs8PrivateKey());
            var pairing = new PairingService(Data);
            Capture = new(Data.RootPath);
            Publisher = new(Data, Capture);
            Target = new(this, pairing, target, "https://192.0.2.180:5131");
            Other = new(this, pairing, other, "https://192.0.2.181:5131");
            SaveGroups(Data, [Membership(Other.Root, Other.Roster, Guid.Empty, Other.Route),
                Membership(Target.Root, Target.Roster, Guid.Empty, Target.Route)]);
            HttpClient Client(string endpoint, IEnumerable<string> pins)
            {
                var world = endpoint.TrimEnd('/') == Target.Route.Address ? Target : Other;
                Require(endpoint.TrimEnd('/') == world.Route.Address && pins.SequenceEqual([world.Route.CertificateSha256]),
                    "prepared discovery changed its registered origin or TLS pin");
                return new(world.Handler, disposeHandler: false) { BaseAddress = new Uri(world.Route.Address + "/") };
            }
            friends = new(Data, Client);
            Service = new(Data, new HostManager(Data), friends, pairing, Client);
        }
        public void Dispose() { Service.Dispose(); friends.Dispose(); Target.Dispose(); Other.Dispose(); Data.Dispose(); Owner.Dispose(); }
    }

    private sealed class World : IDisposable
    {
        internal ServerProfile Profile { get; }
        internal SharedWorldPeerRoot Root { get; }
        internal SharedWorldRoster Roster { get; }
        internal SharedWorldVersion Version { get; }
        internal SharedWorldPeerRoute Route { get; }
        internal Handler Handler { get; }
        private readonly LocalData remote;
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly Guid memberId;
        private readonly SharedWorldPeerStore remoteStore;
        private readonly WorldPeerService remoteService;
        private readonly FriendService remoteFriends;
        private readonly byte[] bytes;
        private readonly string cached;

        internal World(Fixture fixture, PairingService pairing, ServerProfile profile, string endpoint)
        {
            Profile = profile;
            remote = new(Path.Combine(fixture.Data.RootPath, "remote-" + profile.Id.ToString("N")));
            remote.SaveSettings(new() { CompanionEndpoint = endpoint, Profiles = [] });
            using var certificate = new HostIdentity(remote).Ensure(endpoint);
            Route = new(endpoint, HostIdentity.Fingerprint(certificate));
            var invite = pairing.IssueServer(profile.Id, false, false, "https://192.0.2.189:5131", new string('F', 64), false);
            var credential = pairing.Activate(new(profile.Id, invite.Code, ServerScope: true))!;
            memberId = credential.DeviceId;
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Require(pairing.BindSharedWorldKey(memberId, new(nonce, Public(key), Convert.ToBase64String(key.SignData(
                SharedWorldRosterTrust.EnrollmentBasis(memberId, nonce, Public(key)), HashAlgorithmName.SHA256)))).Ok,
                "synthetic metadata peer enrollment failed");
            Roster = fixture.Publisher.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
            pairing.ConfirmSharedRosterPublished(profile.Id, Roster);
            Root = new(Roster.OwnerPublicKey, profile.Id, Roster.GroupId);
            bytes = Encoding.UTF8.GetBytes("synthetic cached " + profile.WorldId);
            fixture.Capture.Set(profile, bytes);
            var publication = fixture.Publisher.PublishAfterStop(profile, Guid.NewGuid());
            Require(publication is { Ok: true, Version: not null }, "synthetic cached publication failed");
            Version = publication.Version!;
            cached = WorldPeerRecoverySource.StorageRoot(fixture.Data, Root, Lane(Root, Guid.Empty), Version.VersionHash);
            WriteCopy(cached, Version, bytes);
            remote.SaveProtected($"shared-world-pc-signing-{memberId:N}.protected", key.ExportPkcs8PrivateKey());
            new SharedWorldRosterChainStore(remote).Append(Roster, Root.OwnerPublicKey);
            SaveGroups(remote, [Membership(Root, Roster, memberId, null)]);
            remoteStore = Store(remote, Root, Roster);
            Announce(Version);
            var localStore = Store(fixture.Data, Root, Roster);
            localStore.Publish(Guid.Empty, fixture.Owner, [], [],
                [new(Lane(Root, Guid.Empty), Version.VersionHash, Version.VersionHash, null, 1)], [Version.VersionHash]);
            Require(localStore.ImportPage(remoteStore.ReadPage()).Changed, "synthetic peer contact was not retained");
            HttpClient NoNetwork(string _, IEnumerable<string> __) => throw new InvalidOperationException("The synthetic responder must not connect outward.");
            remoteFriends = new(remote, NoNetwork);
            remoteService = new(remote, new HostManager(remote), remoteFriends, new PairingService(remote), NoNetwork);
            Handler = new(remoteService, Root, Route);
        }
        internal void Announce(SharedWorldVersion head) => remoteStore.Publish(memberId, key, [Route], [],
            [new(Lane(Root, Guid.Empty), Version.VersionHash, head.VersionHash, head.ParentHash, head.Number)], [head.VersionHash]);
        internal void VerifyCachedCopy() => Require(FriendLink.ReadReceivedLatest(cached)?.VersionHash == Version.VersionHash &&
            File.ReadAllBytes(Path.Combine(cached, Version.VersionHash, SharedWorldService.PayloadDirectory, "world.dat")).SequenceEqual(bytes),
            "prepared metadata discovery changed the exact retained payload/pointer");
        public void Dispose() { Handler.Dispose(); remoteService.Dispose(); remoteFriends.Dispose(); remote.Dispose(); key.Dispose(); }
    }

    private sealed class Handler(WorldPeerService service, SharedWorldPeerRoot root, SharedWorldPeerRoute route) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> operations = new();
        internal bool HoldMetadata { get; set; }
        internal bool HoldCatalog { get; set; }
        internal TaskCompletionSource<bool> MetadataEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> CatalogEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseMetadata { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseCatalog { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Challenges { get; private set; }
        internal int CompletedCatalogs { get; private set; }
        internal int ProofFailures { get; private set; }
        internal bool OnlyMetadata => operations.Count > 0 && operations.Keys.All(item => item == WorldPeerOperations.Metadata);
        internal int PayloadRequests => operations.Where(item => item.Key is WorldPeerOperations.Catalog or WorldPeerOperations.Chunk or WorldPeerOperations.Seed).Sum(item => item.Value);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/challenge", StringComparison.Ordinal))
            {
                Challenges++;
                var hello = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(bytes, 2 * 1024 * 1024);
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(service.PrepareChallenge(root.GroupKey, hello,
                    route.CertificateSha256), options: SharedWorldPeerTrust.Json)
                };
            }
            var envelope = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(bytes, 2 * 1024 * 1024);
            var operation = envelope.Request.Operation;
            operations.AddOrUpdate(operation, 1, (_, count) => count + 1);
            if (operation == WorldPeerOperations.Metadata && HoldMetadata)
            { MetadataEntered.TrySetResult(true); await ReleaseMetadata.Task.WaitAsync(token); }
            if (operation == WorldPeerOperations.Catalog && HoldCatalog)
            { CatalogEntered.TrySetResult(true); await ReleaseCatalog.Task.WaitAsync(token); }
            try
            {
                var result = await service.ExchangeAsync(root.GroupKey, envelope.Request, envelope.Payload, token, route.CertificateSha256);
                if (operation == WorldPeerOperations.Catalog) CompletedCatalogs++;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(result) };
            }
            catch (InvalidDataException) { ProofFailures++; throw; }
        }
    }

    private sealed class Capture(string root) : ISharedWorldCaptureAdapter
    {
        internal void Set(ServerProfile profile, byte[] bytes)
        { var path = Path.Combine(root, "synthetic-captures", profile.Id.ToString("N")); Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path, "world.dat"), bytes); }
        public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid id)
        {
            var path = Path.Combine(root, "synthetic-captures", profile.Id.ToString("N"));
            return new(1, SharedWorldCaptureKinds.PostStopBackup, id, DateTimeOffset.UtcNow,
                [Fact(File.ReadAllBytes(Path.Combine(path, "world.dat")))], path,
                new(3, profile.Id, profile.Kind, profile.WorldId, "Unknown", "SyntheticFilesOnly", [], [], profile.GamePort));
        }
    }
    private static object Membership(SharedWorldPeerRoot root, SharedWorldRoster roster, Guid id, SharedWorldPeerRoute? bootstrap) =>
        new { Root = root, Roster = roster, DeviceId = id, Name = "Synthetic prepared world", ReceiveConsented = true, SharingEnabled = true, Bootstrap = bootstrap };
    private static void SaveGroups(LocalData data, object[] groups) => data.SaveProtected("world-peer-memberships.protected",
        JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, Groups = groups }, Json));
    private static SharedWorldPeerStore Store(LocalData data, SharedWorldPeerRoot root, SharedWorldRoster roster) =>
        new(data, root, new SharedWorldPeerTrust(root, (id, key) => SharedWorldPeerTrust.AccessFromRoster(root, roster, id, key)));
    private static SharedWorldVersion Version(SharedWorldPeerRoot root, ECDsa owner, byte[] bytes) => SharedWorldService.SignVersion(new(4,
        root.GroupId, 1, null, root.ProfileId, GameKinds.Fixture, "PreparedFilesOnly", DateTimeOffset.UtcNow,
        SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(2456, false, "Unknown", [], []), [Fact(bytes)], "", "", ""), owner);
    private static SharedWorldRoster SignRoster(SharedWorldRoster roster, ECDsa owner) => roster with
    { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };
    private static SharedWorldFile Fact(byte[] bytes) => new("world.dat", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static string Lane(SharedWorldPeerRoot root, Guid id) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"TogetherServer world hosting lane v1\n{root.GroupKey}\n{id:N}")));
    private static void WriteCopy(string vault, SharedWorldVersion version, byte[] bytes)
    {
        var directory = Path.Combine(vault, version.VersionHash); Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
        File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(vault, version);
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

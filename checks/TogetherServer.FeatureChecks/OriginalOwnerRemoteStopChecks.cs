using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Disposable protected state, signed rosters and fake driver/HTTP adapters only.
// No listener, process, app, game, browser, native dialog or real world is opened.
internal static class OriginalOwnerRemoteStopChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        OwnerControlsRemainScoped(root);
        CurrentAccessStillWins(root);
        RootCollisionsFailClosed(root);
        PublishedProfilesRejectOtherRoots(root);
        WithdrawnHostingKeepsOwnerStop(root);
        TwoFreshEmptyObservations(root);
        await SuccessorProfileUsesOriginalRosterAsync(root);
    }

    private static void RootCollisionsFailClosed(string root)
    {
        foreach (var changeOwner in new[] { true, false })
        {
            // Legacy standalone rosters are valid without a schema-3 journal.
            // Both independently signed roots deliberately reuse one profile ID.
            using var fixture = new Fixture(root, legacyRoster: true);
            using var otherOwner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var signingKey = changeOwner ? otherOwner : fixture.Owner;
            var otherRoot = new SharedWorldPeerRoot(Public(signingKey), fixture.Source.ProfileId,
                changeOwner ? fixture.Source.GroupId : Guid.NewGuid());
            var draft = new SharedWorldRoster(2, otherRoot.GroupId, otherRoot.ProfileId, 1, 1, true,
                otherRoot.OwnerPublicKey, fixture.Roster.Members, "");
            var otherRoster = draft with
            {
                Signature = Convert.ToBase64String(signingKey.SignData(
                SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
            };
            SaveGroups(fixture.Data, (fixture.Source, fixture.Roster, fixture.MemberId),
                (otherRoot, otherRoster, fixture.MemberId));
            var profile = fixture.Data.LoadSettings().Profiles.Single();
            Require(SharedWorldRosterTrust.Verify(otherRoster) && SharedWorldSeparateCopyStore.Verify(fixture.Branch) &&
                WorldPeerService.SessionProfileMatchesRoot(fixture.Data, profile, fixture.Source, fixture.BranchHash) &&
                !WorldPeerService.SessionProfileMatchesRoot(fixture.Data, profile, otherRoot, fixture.BranchHash) &&
                fixture.Pairing.EnrollPeerWorld(otherRoot, otherRoster, Guid.Empty, otherRoot.OwnerPublicKey,
                    fixture.LocalProfileId, fixture.BranchHash) is null,
                "a different signed owner/group with the same source profile ID enrolled to this recovery session");
            Require(fixture.Authenticate(out var owner).Ok && fixture.Pairing.CanStop(owner!, fixture.LocalProfileId),
                "an unrelated accepted root removed the exact original owner's Stop access");

            // Model a credential issued by the older profile-ID-only check.
            // Current authorization must repair that unsafe state by denial.
            var state = fixture.Data.LoadPairingState();
            var oldAlias = state.Devices.Single();
            oldAlias.PeerWorldGroupKey = otherRoot.GroupKey;
            oldAlias.SharedWorldPublicKey = otherRoot.OwnerPublicKey;
            fixture.Data.SavePairingState(state);
            fixture.Pairing = new(fixture.Data, fixture.Clock);
            Require(fixture.Authenticate(out _).Code == "PeerMembershipReviewRequired" &&
                !fixture.Pairing.CanStop(owner!, fixture.LocalProfileId) &&
                !fixture.Pairing.Views().Single().ServerPermissions!.Single().CanStop,
                "a previously issued cross-root owner credential retained Stop after current authorization");
        }
    }

    private static void WithdrawnHostingKeepsOwnerStop(string root)
    {
        using var fixture = new Fixture(root);
        var draft = fixture.Roster with
        {
            Epoch = 2,
            Revision = 2,
            Signature = "",
            PreviousRosterHash = SharedWorldRosterTrust.Hash(fixture.Roster),
            Members = [fixture.Roster.Members.Single() with { Grants = new() }]
        };
        var withdrawn = draft with
        {
            Signature = Convert.ToBase64String(fixture.Owner.SignData(
            SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
        };
        new SharedWorldRosterChainStore(fixture.Data).Append(withdrawn, fixture.Source.OwnerPublicKey);
        SaveGroups(fixture.Data, (fixture.Source, withdrawn, fixture.MemberId));
        Require(!SharedWorldSeparateCopyStore.CandidateCurrentlyAuthorized(fixture.Data, fixture.Branch.Offer) &&
            fixture.Authenticate(out var owner).Ok && fixture.Pairing.CanStop(owner!, fixture.LocalProfileId),
            "withdrawing future candidate hosting permission prevented the original owner stopping its already-running world");
    }

    private static void PublishedProfilesRejectOtherRoots(string root)
    {
        using var data = new LocalData(Path.Combine(root, "owner-published-root-" + Guid.NewGuid().ToString("N")));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherOwner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = new ServerProfile { WorldId = "SyntheticPublishedOwner" };
        profile.WorldDirectory = data.NewWorldDirectory(profile.Id);
        data.SaveSettings(new() { Profiles = [profile] });
        data.SaveProtected("shared-world-signing-key.protected", owner.ExportPkcs8PrivateKey());
        var published = new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System)).PublishRoster(profile, []);
        var source = new SharedWorldPeerRoot(Public(owner), profile.Id, published.GroupId);
        var otherRoot = new SharedWorldPeerRoot(Public(otherOwner), profile.Id, published.GroupId);
        var draft = published with { OwnerPublicKey = otherRoot.OwnerPublicKey, Signature = "" };
        var otherRoster = draft with
        {
            Signature = Convert.ToBase64String(otherOwner.SignData(
            SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
        };
        SaveGroups(data, (source, published, Guid.Empty), (otherRoot, otherRoster, Guid.Empty));
        var pairing = new PairingService(data);
        var credential = pairing.EnrollPeerWorld(source, published, Guid.Empty, source.OwnerPublicKey, profile.Id, null);
        Require(credential is not null && pairing.Authenticate(credential.DeviceId, credential.Credential, out var current).Ok &&
            pairing.CanStop(current!, profile.Id) &&
            !WorldPeerService.SessionProfileMatchesRoot(data, profile, otherRoot, null) &&
            pairing.EnrollPeerWorld(otherRoot, otherRoster, Guid.Empty, otherRoot.OwnerPublicKey, profile.Id, null) is null,
            "a nonbranch local profile authorized another signed root sharing its profile and group GUIDs");
        var state = data.LoadPairingState();
        var alias = state.Devices.Single();
        alias.PeerWorldGroupKey = otherRoot.GroupKey;
        alias.SharedWorldPublicKey = otherRoot.OwnerPublicKey;
        data.SavePairingState(state);
        pairing = new(data);
        Require(pairing.Authenticate(credential!.DeviceId, credential.Credential, out _).Code == "PeerMembershipReviewRequired",
            "an older cross-root nonbranch credential survived the published source-binding check");
    }

    private static void OwnerControlsRemainScoped(string root)
    {
        using var fixture = new Fixture(root);
        Require(fixture.Authenticate(out var owner).Ok && owner is not null,
            "the proved original owner could not authenticate to the successor");
        Require(!owner!.CanStop && fixture.Pairing.CanStop(owner, fixture.LocalProfileId) &&
            RemoteActionPolicy.Allowed(fixture.Pairing, owner, fixture.LocalProfileId, RemoteActionKind.Stop),
            "an existing status-only owner credential could not request guarded Stop");
        Require(!fixture.Pairing.CanStop(owner, fixture.Source.ProfileId) &&
            !fixture.Pairing.CanStop(owner, Guid.NewGuid()) &&
            !fixture.Pairing.CanStart(owner, fixture.LocalProfileId) &&
            !fixture.Pairing.CanExtendTimer(owner, fixture.LocalProfileId) &&
            !fixture.Pairing.CanViewLogs(owner, fixture.LocalProfileId) &&
            !RemoteActionPolicy.Allowed(fixture.Pairing, owner, fixture.LocalProfileId, RemoteActionKind.Restart),
            "owner Stop leaked to another server or granted another control");
        var view = fixture.Pairing.Views().Single(item => item.Id == owner.Id);
        Require(view.CanStop && view.ServerPermissions!.Single().CanStop &&
            !view.CanStart && !view.CanExtendTimer && !view.CanViewLogs,
            "the authenticated owner permission projection disagreed with Stop authorization");
        var member = fixture.Pairing.EnrollPeerWorld(fixture.Source, fixture.Roster,
            fixture.MemberId, Public(fixture.Member), fixture.LocalProfileId, fixture.BranchHash);
        Require(member is not null && fixture.Pairing.Authenticate(member.DeviceId, member.Credential, out var friend).Ok &&
            !fixture.Pairing.CanStop(friend!, fixture.LocalProfileId),
            "a Receive/EligibleHost member acquired the original owner's Stop right");
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Require(fixture.Pairing.EnrollPeerWorld(fixture.Source, fixture.Roster, Guid.Empty,
            Public(impostor), fixture.LocalProfileId, fixture.BranchHash) is null &&
            fixture.Pairing.EnrollPeerWorld(fixture.Source, fixture.Roster, fixture.MemberId,
                fixture.Source.OwnerPublicKey, fixture.LocalProfileId, fixture.BranchHash) is null,
            "an empty member ID or copied owner public key bypassed pinned identity proof");
        fixture.Pairing = new(fixture.Data, fixture.Clock);
        Require(fixture.Authenticate(out owner).Ok && fixture.Pairing.CanStop(owner!, fixture.LocalProfileId),
            "reopening the successor lost the existing owner's guarded Stop right");
    }

    private static void CurrentAccessStillWins(string root)
    {
        using (var fixture = new Fixture(root))
        {
            Require(fixture.Authenticate(out var stale).Ok, "could not load the owner before expiry");
            var deadline = fixture.Clock.Now.AddHours(1);
            Require(fixture.Pairing.SetAccessExpiry(fixture.Credential.DeviceId,
                new(AccessExpiresUtc: deadline)).Ok, "could not restrict the local owner credential");
            fixture.Clock.Now = deadline;
            Require(!fixture.Pairing.CanStop(stale!, fixture.LocalProfileId) &&
                fixture.Authenticate(out _).Code == "AccessExpired" &&
                !fixture.Pairing.Views().Single().ServerPermissions!.Single().CanStop,
                "a stale original-owner object survived the exact access deadline");
            Require(fixture.Pairing.SetAccessExpiry(fixture.Credential.DeviceId, new(Clear: true)).Ok &&
                fixture.Authenticate(out var restored).Ok && fixture.Pairing.CanStop(restored!, fixture.LocalProfileId),
                "clearing a local deadline did not restore the same owner's credential");
            Require(fixture.Pairing.Revoke(fixture.Credential.DeviceId).Ok &&
                !fixture.Pairing.CanStop(stale!, fixture.LocalProfileId) &&
                fixture.Authenticate(out _).Code == "Revoked",
                "local revocation did not immediately deny an already loaded owner");
        }
        using (var fixture = new Fixture(root))
        {
            Require(fixture.Authenticate(out var stale).Ok, "could not load the owner before membership damage");
            fixture.Data.SaveProtected("world-peer-memberships.protected", "broken synthetic JSON"u8.ToArray());
            Require(!fixture.Pairing.CanStop(stale!, fixture.LocalProfileId) &&
                fixture.Authenticate(out _).Code == "PeerMembershipReviewRequired",
                "owner Stop survived damaged current membership");
        }
        using (var fixture = new Fixture(root))
        {
            Require(fixture.Authenticate(out var stale).Ok, "could not load the owner before assignment removal");
            var state = fixture.Data.LoadPairingState();
            state.Devices.Single().AssignedProfileIds = [];
            fixture.Data.SavePairingState(state);
            fixture.Pairing = new(fixture.Data, fixture.Clock);
            Require(!fixture.Pairing.CanStop(stale!, fixture.LocalProfileId) &&
                fixture.Authenticate(out _).Code == "Revoked",
                "owner Stop survived removal of the exact local server assignment");
        }
        using (var fixture = new Fixture(root))
        {
            Require(fixture.Authenticate(out var stale).Ok, "could not load the owner before credential expiry");
            fixture.Clock.Now = fixture.Credential.ExpiresUtc;
            Require(!fixture.Pairing.CanStop(stale!, fixture.LocalProfileId) &&
                fixture.Authenticate(out _).Code == "Expired",
                "owner Stop survived the transport credential deadline");
        }
    }

    private static void TwoFreshEmptyObservations(string root)
    {
        using var fixture = new Fixture(root);
        var zero = new GameHealthResult(true, "SyntheticCount", "Ready", "Synthetic count.",
            OnlinePlayers: 0, PlayerCountTrusted: true);
        foreach (var final in new[]
        {
            zero,
            zero with { OnlinePlayers = 1 },
            zero with { OnlinePlayers = null, PlayerCountTrusted = false },
            zero with { PlayerCountTrusted = false },
            zero with { State = "Unknown", Ok = false }
        })
        {
            var driver = new Driver(zero, final);
            var run = new ManagedRun { ProfileId = fixture.LocalProfileId, Kind = driver.Kind, OperationId = Guid.NewGuid() };
            var profile = new ServerProfile { Id = run.ProfileId, Kind = run.Kind };
            var snapshot = new HostSnapshot(new() { Profiles = [profile] },
                [new(run.ProfileId, "Ready", "Synthetic cached zero.", null,
                    OnlinePlayers: 0, PlayerCountTrusted: true)], "Synthetic", "Host", new Dictionary<Guid, bool>(), "");
            using var permit = RemoteStopSafety.TryAcquire(snapshot, run.ProfileId, fixture.Data,
                new GameServerRegistry([driver], PortProbeMode.ObserveOnly), [run]);
            Require(permit.Allowed && driver.Queries == 0 && permit.StillSafe(run) == (final == zero) && driver.Queries == 2,
                "guarded Stop did not require two fresh exact-zero driver observations after its cached display decision");
            Require(!permit.StillSafe(new() { OperationId = Guid.NewGuid() }) && driver.Queries == 2,
                "a Stop permit queried or authorized another managed operation");
        }
        var occupied = new Driver(zero with { OnlinePlayers = 1 }, zero);
        using var denied = StopPermit.AllowedFor(Guid.NewGuid(), occupied, "Synthetic gate.");
        Require(!denied.StillSafe(new() { OperationId = Guid.NewGuid() }) && occupied.Queries == 0,
            "the exact-run identity check happened after a driver query");
        var operationId = Guid.NewGuid();
        using var firstUnsafe = StopPermit.AllowedFor(operationId, occupied, "Synthetic gate.");
        Require(!firstUnsafe.StillSafe(new() { OperationId = operationId }) && occupied.Queries == 1,
            "an initially occupied server reached a second approval or Stop");
    }

    private static async Task SuccessorProfileUsesOriginalRosterAsync(string root)
    {
        using var fixture = new Fixture(root);
        using var local = new LocalData(Path.Combine(root, "owner-peer-roster-" + Guid.NewGuid().ToString("N")));
        local.SaveProtected("shared-world-signing-key.protected", fixture.Owner.ExportPkcs8PrivateKey());
        new SharedWorldRosterChainStore(local).Append(fixture.Roster, fixture.Source.OwnerPublicKey);
        var route = new SharedWorldPeerRoute("https://192.0.2.99:5131", new string('A', 64));
        using var handler = new Handler(fixture.LocalProfileId);
        Guid connectionId;
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint == route.Address && pins.Contains(route.CertificateSha256), "owner sharing changed the saved pinned origin");
            return new(handler, false) { BaseAddress = new(endpoint + "/") };
        }
        using (var service = new FriendService(local, Client))
        {
            var enrolled = await service.AdoptPeerConnectionAsync(fixture.Source, fixture.Roster,
                Guid.Empty, false, route, new(fixture.Credential, fixture.LocalProfileId,
                    Guid.NewGuid(), fixture.BranchHash, Guid.Empty), default);
            Require(enrolled.Ok, "owner successor connection could not be saved through fake authenticated status");
            connectionId = service.View().ConnectionId;
            var sharing = await service.CheckSharedWorldSharingAsync(fixture.LocalProfileId);
            Require(sharing.Available && sharing.Code == "OriginalOwner" && sharing.SelfDeviceId == Guid.Empty &&
                !sharing.CanManage && sharing.Revision == fixture.Roster.Revision && handler.Requests == 1,
                "the successor-local profile could not find its original owner's current signed roster");
        }
        using (var reopened = new FriendService(local, Client))
        {
            var sharing = await reopened.CheckSharedWorldSharingAsync(fixture.LocalProfileId);
            Require(sharing.Available && sharing.Code == "OriginalOwner" && handler.Requests == 1,
                "reopening lost the original-owner/local-profile roster binding or added a network enrollment");
            Require(!(await reopened.CheckSharedWorldSharingAsync(Guid.NewGuid())).Available,
                "an unrelated successor-local profile borrowed this world's sharing list");
        }
        var configName = $"friend-{connectionId:N}.protected";
        var config = local.LoadProtectedJson<FriendConfiguration>(configName)!;
        Require(config.PeerWorldLocalProfileId == fixture.LocalProfileId,
            "adoption did not preserve the exact successor-local server ID");
        config.PeerWorldLocalProfileId = null;
        local.SaveProtected(configName, JsonSerializer.SerializeToUtf8Bytes(config, Json));
        using (var legacy = new FriendService(local, Client))
        {
            Require(!(await legacy.CheckSharedWorldSharingAsync(fixture.LocalProfileId)).Available,
                "an unverified legacy status guessed a successor profile binding");
            await legacy.PollAsync();
            Require((await legacy.CheckSharedWorldSharingAsync(fixture.LocalProfileId)).Available,
                "an authenticated unambiguous legacy owner connection did not recover its profile binding");
        }
        config = local.LoadProtectedJson<FriendConfiguration>(configName)!;
        config.SharedRosterFloors[fixture.Source.ProfileId] = config.SharedRosterFloors[fixture.Source.ProfileId] with { Signature = "tampered" };
        local.SaveProtected(configName, JsonSerializer.SerializeToUtf8Bytes(config, Json));
        using (var changed = new FriendService(local, Client))
            Require(!(await changed.CheckSharedWorldSharingAsync(fixture.LocalProfileId)).Available,
                "the peer sharing view bypassed its exact protected roster floor");
        config.SharedRosterFloors[fixture.Source.ProfileId] = new(fixture.Source.GroupId,
            fixture.Roster.Epoch, fixture.Roster.Revision, fixture.Roster.Signature);
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        config.SharedWorldSigningKeys[fixture.Source.ProfileId] = Public(impostor);
        local.SaveProtected(configName, JsonSerializer.SerializeToUtf8Bytes(config, Json));
        using (var changed = new FriendService(local, Client))
            Require(!(await changed.CheckSharedWorldSharingAsync(fixture.LocalProfileId)).Available,
                "the peer sharing view changed its pinned original owner key");
        config.SharedWorldSigningKeys[fixture.Source.ProfileId] = fixture.Source.OwnerPublicKey;
        local.SaveProtected(configName, JsonSerializer.SerializeToUtf8Bytes(config, Json));
        local.SaveProtected("shared-world-signing-key.protected", impostor.ExportPkcs8PrivateKey());
        using (var changed = new FriendService(local, Client))
            Require(!(await changed.CheckSharedWorldSharingAsync(fixture.LocalProfileId)).Available,
                "an owner-session roster accepted this PC without possession of the pinned owner key");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly LocalData Data;
        internal readonly ECDsa Owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly ECDsa Member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly Clock Clock = new();
        internal readonly Guid LocalProfileId;
        internal readonly Guid MemberId = Guid.NewGuid();
        internal readonly string BranchHash;
        internal readonly SharedWorldPeerRoot Source;
        internal readonly SharedWorldRoster Roster;
        internal readonly WorldSeparateCopyBranch Branch;
        internal readonly PairingCredential Credential;
        internal PairingService Pairing;

        internal Fixture(string root, bool legacyRoster = false)
        {
            Data = new(Path.Combine(root, "owner-remote-stop-" + Guid.NewGuid().ToString("N")));
            Source = new(Public(Owner), Guid.NewGuid(), Guid.NewGuid());
            var draft = new SharedWorldRoster(legacyRoster ? 2 : 3, Source.GroupId, Source.ProfileId, 1, 1, true,
                Source.OwnerPublicKey, [new(MemberId, Public(Member), new(Receive: true, EligibleHost: true), false)], "",
                SignerDeviceId: legacyRoster ? null : Guid.Empty, SignerPublicKey: legacyRoster ? null : Source.OwnerPublicKey);
            Roster = draft with { Signature = Convert.ToBase64String(Owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            if (!legacyRoster) new SharedWorldRosterChainStore(Data).Append(Roster, Source.OwnerPublicKey);
            Data.SaveProtected($"shared-world-pc-signing-{MemberId:N}.protected", Member.ExportPkcs8PrivateKey());
            byte[] bytes = "synthetic completed original-owner world"u8.ToArray();
            var version = SharedWorldService.SignVersion(new(4, Source.GroupId, 1, null, Source.ProfileId,
                GameKinds.Fixture, "SyntheticOwnerWorld", Clock.Now, SharedWorldCaptureKinds.PostStopBackup,
                Guid.NewGuid(), new(2456, false, "Unknown", [], []),
                [new("world.dat", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)))], "", "", ""), Owner);
            var vault = Path.Combine(Data.RootPath, "synthetic-received-source");
            var payload = Path.Combine(vault, version.VersionHash, SharedWorldService.PayloadDirectory);
            Directory.CreateDirectory(payload);
            File.WriteAllBytes(Path.Combine(payload, "world.dat"), bytes);
            File.WriteAllBytes(Path.Combine(vault, version.VersionHash, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            var offer = SharedWorldSeparateCopyStore.PrepareSoloOffer(vault, Roster,
                new(Source.GroupId, Roster.Epoch, Roster.Revision, Roster.Signature), MemberId, Member,
                "https://192.0.2.99:5131", new string('A', 64), new WorldAuthorityStore(Data));
            Branch = new SharedWorldSeparateCopyStore(Data).Declare(offer, vault, Member, true);
            BranchHash = Branch.BranchHash;
            LocalProfileId = SharedWorldBranchLiveSource.LocalProfileId(BranchHash);
            Data.SaveSettings(new()
            {
                Profiles = [new() { Id = LocalProfileId, Kind = version.Game,
                WorldId = version.WorldId, WorldDirectory = Data.NewWorldDirectory(LocalProfileId),
                SeparateCopySourceProfileId = Source.ProfileId, SeparateCopyBranchHash = BranchHash }]
            });
            SaveGroups(Data, (Source, Roster, MemberId));
            Pairing = new(Data, Clock);
            Credential = Pairing.EnrollPeerWorld(Source, Roster, Guid.Empty, Source.OwnerPublicKey, LocalProfileId, BranchHash) ??
                throw new InvalidOperationException("synthetic original-owner enrollment failed");
        }

        internal PairingDecision Authenticate(out PairedDevice? device) =>
            Pairing.Authenticate(Credential.DeviceId, Credential.Credential, out device);
        public void Dispose() { Owner.Dispose(); Member.Dispose(); Data.Dispose(); }
    }

    private static void SaveGroups(LocalData data,
        params (SharedWorldPeerRoot Root, SharedWorldRoster Roster, Guid DeviceId)[] groups) =>
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Groups = groups.Select(group => new
            {
                group.Root,
                group.Roster,
                group.DeviceId,
                Name = "Synthetic shared world",
                ReceiveConsented = false,
                SharingEnabled = true,
                Bootstrap = (SharedWorldPeerRoute?)null
            }).ToArray()
        }, Json));

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Guid profileId) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/companion/heartbeat" &&
                request.Headers.Authorization?.Scheme == "Bearer", "owner sharing sent an unexpected remote request");
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CompanionStatus(true, null,
                    [new(profileId, "Synthetic successor", "Ready", null, CanStop: true)], false, true,
                    DateTimeOffset.UtcNow, CompanionProtocol.Describe()))
            });
        }
    }

    private sealed class Driver(params GameHealthResult[] observations) : IGameServerDriver
    {
        internal int Queries;
        public string Kind => "SyntheticOwnerStop";
        public string DisplayName => "Synthetic owner Stop";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => false;
        public GameHealthResult Health(ManagedRun run) => observations[Math.Min(Queries++, observations.Length - 1)];
        public string? ManagedSaveDirectory(ServerProfile profile) => throw new InvalidOperationException("No save access is allowed.");
        public string ManagedExecutablePath(ServerProfile profile) => throw new InvalidOperationException("No process inspection is allowed.");
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => throw new InvalidOperationException("No Start is allowed.");
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No Start is allowed.");
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No Start is allowed.");
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw new InvalidOperationException("No process Stop is allowed.");
    }

    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

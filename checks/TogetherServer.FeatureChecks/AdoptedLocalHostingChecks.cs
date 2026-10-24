using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Real adoption, signed rosters and protected disposable state over fake HTTP.
// Permission/consent reads never start an app, process, listener, browser or game.
internal static class AdoptedLocalHostingChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        foreach (var adopted in new[] { false, true })
        {
            using var fixture = await Fixture.CreateAsync(root, adopted);
            var access = await fixture.AccessAsync(refresh: adopted);
            Require(access.Allowed && access.Consented && access.Root == fixture.Root &&
                access.Game == GameKinds.Valheim, "a current receiver's permission did not follow its exact signed source profile: " + access.Code);
            if (adopted)
            {
                var permission = await fixture.Service.PermissionAsync(fixture.LocalProfileId);
                Require(permission.Allowed, "the actual hosting prompt rejected the enrolled successor profile: " + permission.Code);
            }
            fixture.AssertNoPreparation();
        }
        await OwnerProofAndExplicitConsentAsync(root);
        await MissingPeerKeysStayMissingAsync(root);
        await NormalizedVersionAsync(root);
        foreach (var fault in new[] { "wrong-profile", "wrong-root", "wrong-root-profile", "wrong-group", "stale-floor", "revoked-member",
            "expired-member", "receive-only", "known-revoked", "missing-key", "wrong-key", "bad-signature", "owner-placeholder" })
        {
            using var fixture = await Fixture.CreateAsync(root, adopted: true);
            var requested = fixture.LocalProfileId;
            switch (fault)
            {
                case "wrong-profile": requested = Guid.NewGuid(); break;
                case "wrong-root": fixture.Config.PeerWorld = fixture.Root with { OwnerPublicKey = Public(fixture.Member) }; break;
                case "wrong-root-profile": fixture.Config.PeerWorld = fixture.Root with { ProfileId = Guid.NewGuid() }; break;
                case "wrong-group": fixture.Config.PeerWorld = fixture.Root with { GroupId = Guid.NewGuid() }; break;
                case "stale-floor":
                    fixture.Config.SharedRosterFloors[fixture.Root.ProfileId] =
                        fixture.Config.SharedRosterFloors[fixture.Root.ProfileId] with { Revision = fixture.Roster.Revision + 1 };
                    break;
                case "revoked-member": fixture.ChangeMember(member => member with { Revoked = true }); break;
                case "expired-member": fixture.ChangeMember(member => member with { AccessExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }); break;
                case "receive-only": fixture.ChangeMember(member => member with { Grants = new(Receive: true) }); break;
                case "known-revoked": fixture.Config.KnownRevoked = true; break;
                case "missing-key": fixture.Data.DeleteProtected(fixture.KeyName); break;
                case "wrong-key": fixture.Data.SaveProtected(fixture.KeyName, fixture.Owner.ExportPkcs8PrivateKey()); break;
                case "bad-signature": fixture.SaveRoster(fixture.Roster with { Signature = new string('A', 88) }); break;
                case "owner-placeholder":
                    fixture.Config.PeerWorldMemberDeviceId = Guid.Empty;
                    fixture.Data.SaveProtected("shared-world-signing-key.protected", fixture.Member.ExportPkcs8PrivateKey());
                    fixture.Data.SaveProtected($"shared-world-pc-signing-{Guid.Empty:N}.protected", fixture.Member.ExportPkcs8PrivateKey());
                    fixture.Data.SaveProtected($"shared-world-roster-{Guid.Empty:N}-{fixture.Root.ProfileId:N}.protected",
                        JsonSerializer.SerializeToUtf8Bytes(fixture.Roster, Json));
                    break;
            }
            var access = await fixture.Link.LocalHostingAccessAsync(requested, false, default, permissionOnly: true);
            Require(!access.Allowed && (fault != "wrong-profile" || !access.Consented),
                "adopted profile normalization bypassed " + fault);
            fixture.AssertNoPreparation();
        }
    }

    private static async Task MissingPeerKeysStayMissingAsync(string root)
    {
        foreach (var owner in new[] { false, true })
        {
            using var fixture = await Fixture.CreateAsync(root, adopted: true, owner: owner);
            fixture.Data.DeleteProtected(fixture.KeyName);
            var requests = fixture.RequestCount;
            var sharing = await fixture.Friends.CheckSharedWorldSharingAsync(fixture.LocalProfileId);
            var permission = await fixture.Service.PermissionAsync(fixture.LocalProfileId);
            Require(!sharing.Available && sharing.Code == "HostingIdentityRequired" &&
                !permission.Allowed && permission.Code == "HostingIdentityRequired" && fixture.RequestCount == requests &&
                !fixture.Data.HasProtected(fixture.KeyName) &&
                !fixture.Data.HasProtected($"shared-world-pc-signing-{Guid.Empty:N}.protected"),
                "a public adopted sharing/permission read regenerated the missing enrolled member or original-owner key");
            fixture.AssertNoPreparation();
        }
    }

    private static async Task OwnerProofAndExplicitConsentAsync(string root)
    {
        using var owner = await Fixture.CreateAsync(root, adopted: true, owner: true);
        var access = await owner.AccessAsync(refresh: true);
        Require(access.Allowed && !access.Consented && access.Root == owner.Root,
            "original-owner Stop or owner0 implicitly granted hosting consent, or the real owner key was not accepted");
        Require(!owner.Data.HasProtected($"shared-world-pc-signing-{Guid.Empty:N}.protected"),
            "an owner permission read created a placeholder PC signing key");
        Require((await owner.Link.SetSharedWorldConsentAsync(owner.LocalProfileId, true)).Ok &&
            new WorldPeerReceiveConsentStore(owner.Data).Read(owner.Root, Guid.Empty).Enabled == true &&
            (await owner.AccessAsync()).Consented,
            "the exact enrolled local profile could not record explicit source-world owner consent");
        Require((await owner.Link.SetSharedWorldConsentAsync(owner.Root.ProfileId, false)).Ok &&
            !(await owner.AccessAsync()).Consented &&
            (await owner.AccessAsync()).Allowed,
            "a source-world withdrawal was lost through the adopted alias or became a permission grant change");
        Require((await owner.Link.SetSharedWorldConsentAsync(owner.LocalProfileId, true)).Ok &&
            (await owner.AccessAsync()).Consented,
            "an explicit current alias opt-in could not follow a source-world withdrawal");
        owner.Data.SaveProtected("shared-world-signing-key.protected", owner.Member.ExportPkcs8PrivateKey());
        Require(!(await owner.AccessAsync()).Allowed, "a different local owner's key authorized the pinned original owner");
        owner.AssertNoPreparation();

        using var member = await Fixture.CreateAsync(root, adopted: true);
        new WorldPeerReceiveConsentStore(member.Data).Set(member.Root, member.MemberId, false);
        var denied = await member.AccessAsync();
        Require(denied.Allowed && !denied.Consented &&
            (await member.Link.LocalHostingAccessAsync(member.Root.ProfileId, false, default, permissionOnly: true)) is { Allowed: true, Consented: false },
            "the adopted member's automatic hosting grant overrode its explicit root/member withdrawal");
        member.AssertNoPreparation();
    }

    private static async Task NormalizedVersionAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root, adopted: true);
        var version = fixture.Version(fixture.Root.ProfileId);
        fixture.Config.LastSharedHostManifests[fixture.Root.ProfileId] = version;
        var access = await fixture.Link.LocalHostingAccessAsync(fixture.LocalProfileId, false, default);
        Require(access.Allowed && access.Version == version && access.Version.ProfileId == fixture.Root.ProfileId &&
            access.Root == fixture.Root && !access.SaveVerified,
            "adopted permission did not preserve the signed source identity of its selected version");
        fixture.Config.LastSharedHostManifests[fixture.Root.ProfileId] = fixture.Version(fixture.LocalProfileId);
        access = await fixture.Link.LocalHostingAccessAsync(fixture.LocalProfileId, false, default);
        Require(access.Allowed && access.Version is null && !access.SaveVerified,
            "normalization relabeled or accepted a version signed for the successor-local profile");
        fixture.AssertNoPreparation();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly LocalData Data;
        internal readonly ECDsa Owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly ECDsa Member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly Guid MemberId;
        internal readonly Guid LocalProfileId;
        internal readonly SharedWorldPeerRoot Root;
        internal SharedWorldRoster Roster;
        internal FriendService Friends = null!;
        internal FriendLink Link = null!;
        internal FriendConfiguration Config = null!;
        internal FriendLocalHostingService Service = null!;
        private FriendService.LocalHostingConnection? connection;
        private readonly Handler handler;
        internal int RequestCount => handler.Requests;
        internal string KeyName => MemberId == Guid.Empty ? "shared-world-signing-key.protected" :
            $"shared-world-pc-signing-{MemberId:N}.protected";
        private string RosterName => $"shared-world-roster-{MemberId:N}-{Root.ProfileId:N}.protected";
        private const string Endpoint = "https://192.0.2.112:5131";
        private static readonly string Pin = new('A', 64);

        private Fixture(string root, bool adopted, bool owner)
        {
            Data = new(Path.Combine(root, "adopted-hosting-" + Guid.NewGuid().ToString("N")));
            Root = new(Public(Owner), Guid.NewGuid(), Guid.NewGuid());
            MemberId = owner ? Guid.Empty : Guid.NewGuid();
            LocalProfileId = adopted ? Guid.NewGuid() : Root.ProfileId;
            Roster = Sign(new(3, Root.GroupId, Root.ProfileId, 1, 1, true, Root.OwnerPublicKey,
                owner ? [] : [new(MemberId, Public(Member), new(Receive: true, EligibleHost: true, Reshare: true), false)], "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: Root.OwnerPublicKey));
            new SharedWorldRosterChainStore(Data).Append(Roster, Root.OwnerPublicKey);
            Data.SaveProtected(KeyName, owner ? Owner.ExportPkcs8PrivateKey() : Member.ExportPkcs8PrivateKey());
            handler = new(LocalProfileId);
        }

        internal static async Task<Fixture> CreateAsync(string root, bool adopted, bool owner = false)
        {
            var fixture = new Fixture(root, adopted, owner);
            var credential = new PairingCredential(Guid.NewGuid(), new string('C', 64), DateTimeOffset.UtcNow.AddDays(60));
            if (!adopted)
            {
                var config = new FriendConfiguration
                {
                    Endpoint = Endpoint,
                    Fingerprint = Pin,
                    AcceptedFingerprints = [Pin],
                    DeviceId = fixture.MemberId,
                    Credential = credential.Credential,
                    CredentialExpiresUtc = credential.ExpiresUtc,
                    SharedWorldSigningKeys = new() { [fixture.Root.ProfileId] = fixture.Root.OwnerPublicKey },
                    SharedRosterFloors = new() { [fixture.Root.ProfileId] = new(fixture.Root.GroupId, 1, 1, fixture.Roster.Signature) },
                    ApprovedSharedWorldGroups = new() { [fixture.Root.ProfileId] = fixture.Root.GroupId }
                };
                fixture.Data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(config, Json));
                fixture.SaveRoster(fixture.Roster);
            }
            fixture.Friends = new(fixture.Data, fixture.Client);
            if (adopted)
                Require((await fixture.Friends.AdoptPeerConnectionAsync(fixture.Root, fixture.Roster, fixture.MemberId, false,
                    new(Endpoint, Pin), new(credential, LocalProfileId: fixture.LocalProfileId, SessionId: Guid.NewGuid(),
                        BranchHash: new string('B', 64), MemberDeviceId: fixture.MemberId), default)).Ok,
                    "the synthetic signed session could not be adopted");
            else await fixture.Friends.PollAsync();
            fixture.connection = fixture.Friends.RetainLocalHostingConnection() ?? throw new InvalidOperationException("no adopted connection");
            fixture.Link = fixture.connection.Link;
            fixture.Config = (FriendConfiguration)typeof(FriendLink).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Link)!;
            fixture.Service = new(fixture.Data, fixture.Friends, new HostManager(fixture.Data));
            return fixture;
        }

        private HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint == Endpoint && pins.SequenceEqual([Pin]), "the permission check changed its pinned peer origin");
            return new(handler, false) { BaseAddress = new(endpoint + "/") };
        }
        internal Task<LocalHostingAccess> AccessAsync(bool refresh = false) =>
            Link.LocalHostingAccessAsync(LocalProfileId, refresh, default, permissionOnly: true);
        private SharedWorldRoster Sign(SharedWorldRoster roster) => roster with
        { Signature = Convert.ToBase64String(Owner.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };
        internal void SaveRoster(SharedWorldRoster roster) =>
            Data.SaveProtected(RosterName, JsonSerializer.SerializeToUtf8Bytes(roster, Json));
        internal void ChangeMember(Func<SharedWorldRosterMember, SharedWorldRosterMember> change)
        {
            var members = Roster.Members.Select(change).ToArray();
            Roster = Sign(Roster with
            {
                Revision = Roster.Revision + 1,
                Epoch = Roster.Epoch + 1,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(Roster),
                Members = members,
                OwnerLocalBaselineMembers = members,
                Signature = ""
            });
            new SharedWorldRosterChainStore(Data).Append(Roster, Root.OwnerPublicKey);
            Require(new WorldAuthorityStore(Data).TrustsCurrentRoster(Roster), "the synthetic current member revision is not fully trusted");
            SaveRoster(Roster);
            Config.SharedRosterFloors[Root.ProfileId] = new(Root.GroupId, Roster.Epoch, Roster.Revision, Roster.Signature);
        }
        internal SharedWorldVersion Version(Guid profileId) => SharedWorldService.SignVersion(new(4, Root.GroupId, 1, null,
            profileId, GameKinds.Valheim, "SyntheticAdopted", DateTimeOffset.UtcNow, SharedWorldCaptureKinds.PostStopBackup,
            Guid.NewGuid(), new(2456, false, "Unknown", [], []),
            [new("world.db", 3, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })))], "", "", ""), Owner);
        internal void AssertNoPreparation()
        {
            Require(Data.LoadSettings().Profiles.Count == 0 && !Data.LoadSettings().CompanionListeningEnabled &&
                !Directory.Exists(Path.Combine(Data.RootPath, "received-shared-worlds")) &&
                !Directory.Exists(Path.Combine(Data.RootPath, "received-shared-branches")),
                "a permission or consent check prepared a save, local server or listener");
        }
        public void Dispose() { connection?.Dispose(); Friends.Dispose(); handler.Dispose(); Owner.Dispose(); Member.Dispose(); Data.Dispose(); }
    }

    private sealed class Handler(Guid profileId) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/companion/heartbeat" &&
                request.Headers.Authorization?.Scheme == "Bearer", "the permission fixture requested world, path, lifecycle or listener work");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CompanionStatus(false, "Status only",
                    [new(profileId, "Synthetic adopted world", "Offline", null, Kind: GameKinds.Valheim)],
                    false, true, DateTimeOffset.UtcNow, new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                        [CompanionProtocol.SharedWorldsCapability, CompanionProtocol.SharedWorldPeersCapability])), options: Json)
            });
        }
    }
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

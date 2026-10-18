using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Protected synthetic identities only. No HTTP client, listener, game, app or
// process is started by these consent checks.
internal static class WorldPeerConsentChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        using (var aliases = new Fixture(root, "aliases", receive: true, originalConsent: true, aliasConsent: true))
        {
            Require((await aliases.Friends.PeerMembershipsAsync(false, default)).Single().ReceiveConsented &&
                aliases.Original.SharedWorldStatus(aliases.Root.ProfileId).Consented &&
                aliases.Alias.SharedWorldStatus(aliases.Root.ProfileId).Consented,
                "no-override migration lost existing original/alias consent");
            var gate = LinkGate(aliases.Original);
            await gate.WaitAsync();
            Task<ReceivedSharedWorldResult> withdrawal;
            var promoted = false;
            try
            {
                withdrawal = aliases.Original.SetSharedWorldConsentAsync(aliases.Root.ProfileId, false);
                Require(!withdrawal.IsCompleted &&
                    !new WorldPeerReceiveConsentStore(aliases.Data).Effective(aliases.Root, aliases.DeviceId, true),
                    "withdrawal waited for the original connection gate before persisting the global deny");
                Require(!await aliases.Alias.CommitPeerReceiveAsync(aliases.Root, aliases.DeviceId,
                        () => promoted = true, default) && !promoted &&
                    !aliases.Alias.SharedWorldStatus(aliases.Root.ProfileId).Consented &&
                    !aliases.Service.Views().Single().ReceiveConsented,
                    "a still-consented alias revived save promotion or projected consent while original withdrawal waited");
                var stage = Path.Combine(aliases.Data.RootPath, "disposable-consent-stage");
                Directory.CreateDirectory(stage);
                var destination = Path.Combine(aliases.Data.RootPath, "disposable-consent-final");
                Require(!aliases.Alias.CommitSharedReceipt(aliases.Root.ProfileId, stage, destination,
                        aliases.Data.RootPath, Encoding.UTF8.GetBytes("{}")) && Directory.Exists(stage) &&
                    !Directory.Exists(destination) && !File.Exists(Path.Combine(aliases.Data.RootPath, "latest.json")),
                    "legacy receipt promotion ignored the root/member deny");
            }
            finally { gate.Release(); }
            Require((await withdrawal.WaitAsync(TimeSpan.FromSeconds(5))).Ok &&
                !(await aliases.Friends.PeerMembershipsAsync(false, default)).Single().ReceiveConsented &&
                !await aliases.Friends.CommitPeerReceiveAsync(aliases.Root, aliases.DeviceId,
                    () => promoted = true, default) && !promoted,
                "service aggregation or fallback to a successor alias restored withdrawn consent");
            await aliases.Service.RefreshMembershipsAsync(false, default);
            Require(!aliases.Service.Views().Single().ReceiveConsented,
                "membership refresh restored withdrawn consent from a saved alias");

            Require((await aliases.Service.SetReceivingAsync(aliases.Root.GroupKey, true, default)).Ok &&
                (await aliases.Friends.PeerMembershipsAsync(false, default)).Single().ReceiveConsented &&
                aliases.Original.SharedWorldStatus(aliases.Root.ProfileId).Consented &&
                aliases.Alias.SharedWorldStatus(aliases.Root.ProfileId).Consented &&
                await aliases.Original.CommitPeerReceiveAsync(aliases.Root, aliases.DeviceId, () => promoted = true, default),
                "explicit world opt-in did not apply to all aliases or clear earlier local withdrawal markers");
            Require(promoted && !aliases.Data.LoadSettings().CompanionListeningEnabled &&
                !aliases.Service.Views().Single().SharingEnabled,
                "local receive opt-in changed inbound sharing/listener policy");

            // A previously queued allow cannot undo a more recent immediate
            // deny, even though its per-link config write finishes later.
            gate = LinkGate(aliases.Alias);
            await gate.WaitAsync();
            Task<ReceivedSharedWorldResult> queuedAllow;
            Task<ReceivedSharedWorldResult> laterDeny;
            try
            {
                queuedAllow = aliases.Alias.SetSharedWorldConsentAsync(aliases.Root.ProfileId, true);
                laterDeny = aliases.Alias.SetSharedWorldConsentAsync(aliases.Root.ProfileId, false);
                Require(!queuedAllow.IsCompleted && !laterDeny.IsCompleted,
                    "the synthetic allow/deny ordering did not wait at the actual link gate");
            }
            finally { gate.Release(); }
            var oldAllow = await queuedAllow.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!oldAllow.Ok && oldAllow.Code == "ConsentChanged" &&
                (await laterDeny.WaitAsync(TimeSpan.FromSeconds(5))).Ok &&
                !aliases.Service.Views().Single().ReceiveConsented,
                "an older queued allow defeated a newer requested withdrawal");
        }

        using (var metadata = new Fixture(root, "metadata", receive: false, originalConsent: false, aliasConsent: false))
        {
            var result = await metadata.Service.SetReceivingAsync(metadata.Root.GroupKey, true, default);
            Require(!result.Ok && result.Code == "ReceiveGrantRequired" &&
                !metadata.Service.Views().Single().CanReceive &&
                !metadata.Service.Views().Single().ReceiveConsented &&
                (await metadata.Friends.PeerMembershipsAsync(false, default)).Count == 1 &&
                new WorldPeerReceiveConsentStore(metadata.Data).Read(metadata.Root, metadata.DeviceId).Enabled is null,
                "metadata-only opt-in created Receive authority or lost following membership");
            Require((await metadata.Service.SetReceivingAsync(metadata.Root.GroupKey, false, default)).Ok &&
                !metadata.Data.LoadSettings().CompanionListeningEnabled,
                "withdrawing local receiving required a Receive grant or enabled a listener");
        }

        using (var following = new Fixture(root, "following", receive: true, originalConsent: false, aliasConsent: false))
        {
            Require(!following.Service.Views().Single().ReceiveConsented &&
                (await following.Service.SetReceivingAsync(following.Root.GroupKey, true, default)).Ok &&
                following.Original.SharedWorldStatus(following.Root.ProfileId).Consented &&
                following.Alias.SharedWorldStatus(following.Root.ProfileId).Consented,
                "a metadata follower with an existing Receive grant could not opt in independently of legacy profile consent");
        }

        using (var isolated = new Fixture(root, "isolation", receive: true, originalConsent: true, aliasConsent: true))
        {
            var store = new WorldPeerReceiveConsentStore(isolated.Data);
            var otherMember = Guid.NewGuid();
            var otherGroup = isolated.Root with { GroupId = Guid.NewGuid() };
            var otherProfile = isolated.Root with { ProfileId = Guid.NewGuid() };
            using var otherOwner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var otherRoot = isolated.Root with { OwnerPublicKey = Public(otherOwner) };
            store.Set(isolated.Root, isolated.DeviceId, false);
            Require(!store.Effective(isolated.Root, isolated.DeviceId, true) &&
                store.Effective(isolated.Root, otherMember, true) &&
                store.Effective(otherGroup, isolated.DeviceId, true) &&
                store.Effective(otherProfile, isolated.DeviceId, true) &&
                store.Effective(otherRoot, isolated.DeviceId, true),
                "receiving withdrawal crossed its source member, owner, profile or group boundary");
            store.Set(isolated.Root, Guid.Empty, false);
            Require(!store.Effective(isolated.Root, Guid.Empty, true), "owner0 could not record explicit consent withdrawal");
            store.Set(isolated.Root, Guid.Empty, true);
            Require(store.Effective(isolated.Root, Guid.Empty, false) &&
                !store.Effective(isolated.Root, isolated.DeviceId, true),
                "owner0 opt-in changed another member's decision");
            var observed = store.Read(isolated.Root, isolated.DeviceId);
            store.Set(isolated.Root, isolated.DeviceId, false);
            Require(!store.Set(isolated.Root, isolated.DeviceId, true, observed.Revision) &&
                !store.Effective(isolated.Root, isolated.DeviceId, true),
                "the protected decision failed to fence an obsolete allow revision");
        }

        using (var corrupt = new Fixture(root, "corrupt", receive: true, originalConsent: true, aliasConsent: true))
        {
            corrupt.Data.SaveProtected(WorldPeerReceiveConsentStore.StateName, Encoding.UTF8.GetBytes(
                "{\"schema\":1,\"overrides\":[],\"overrides\":[]}"));
            Require(!corrupt.Service.Views().Single().ReceiveConsented &&
                !(await corrupt.Friends.PeerMembershipsAsync(false, default)).Single().ReceiveConsented &&
                !corrupt.Alias.SharedWorldStatus(corrupt.Root.ProfileId).Consented &&
                !await corrupt.Alias.CommitPeerReceiveAsync(corrupt.Root, corrupt.DeviceId,
                    () => throw new InvalidOperationException("corrupt consent promoted a payload"), default),
                "corrupt consent state fell back to previously allowed alias configuration");
            Require(!(await corrupt.Service.SetReceivingAsync(corrupt.Root.GroupKey, true, default)).Ok &&
                (await corrupt.Service.SetReceivingAsync(corrupt.Root.GroupKey, false, default)).Ok &&
                !corrupt.Data.LoadSettings().CompanionListeningEnabled,
                "corrupt consent allowed opt-in, blocked a local deny, or enabled listening");
        }

        using (var ownerData = new LocalData(Path.Combine(root, "peer-consent-owner-" + Guid.NewGuid().ToString("N"))))
        {
            var profile = new ServerProfile
            {
                Id = Guid.NewGuid(),
                Kind = GameKinds.Fixture,
                Name = "Synthetic owner consent",
                WorldId = "ConsentOwner",
                SharedSavesEnabled = true,
                WorldDirectory = Path.Combine(ownerData.RootPath, "disposable-world")
            };
            Directory.CreateDirectory(profile.WorldDirectory);
            ownerData.SaveSettings(new HostSettings { Profiles = [profile], CompanionListeningEnabled = false });
            var roster = new SharedWorldService(ownerData,
                new WorldBackupService(ownerData, TimeProvider.System)).PublishRoster(profile, []);
            var source = new SharedWorldPeerRoot(roster.OwnerPublicKey, profile.Id, roster.GroupId);
            using var friends = new FriendService(ownerData, NoNetwork);
            using var service = new WorldPeerService(ownerData, new HostManager(ownerData), friends,
                new PairingService(ownerData), NoNetwork);
            await service.RefreshMembershipsAsync(false, default);
            Require(service.Views().Single().SelfDeviceId == Guid.Empty && service.Views().Single().ReceiveConsented,
                "a fresh original owner lost its existing default receive consent");
            Require((await service.SetReceivingAsync(source.GroupKey, false, default)).Ok,
                "owner0 could not withdraw world receiving through the local action");
            await service.RefreshMembershipsAsync(false, default);
            Require(!service.Views().Single().ReceiveConsented &&
                (await service.SetReceivingAsync(source.GroupKey, true, default)).Ok &&
                service.Views().Single().ReceiveConsented && !ownerData.LoadSettings().CompanionListeningEnabled,
                "owner membership refresh forced withdrawn receiving on, or explicit opt-in enabled a listener");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal LocalData Data { get; }
        internal Guid DeviceId { get; } = Guid.NewGuid();
        internal SharedWorldPeerRoot Root { get; }
        internal FriendLink Original { get; }
        internal FriendLink Alias { get; }
        internal FriendService Friends { get; }
        internal WorldPeerService Service { get; }
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa member = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        internal Fixture(string parent, string label, bool receive, bool originalConsent, bool aliasConsent)
        {
            Data = new(Path.Combine(parent, "peer-consent-" + label + "-" + Guid.NewGuid().ToString("N")));
            Data.SaveSettings(new HostSettings { Profiles = [], CompanionListeningEnabled = false });
            Root = new(Public(owner), Guid.NewGuid(), Guid.NewGuid());
            var draft = new SharedWorldRoster(3, Root.GroupId, Root.ProfileId, 1, 1, true, Root.OwnerPublicKey,
                [new(DeviceId, Public(member), new(Receive: receive, EligibleHost: receive), false)], "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: Root.OwnerPublicKey);
            var roster = draft with
            {
                Signature = Convert.ToBase64String(owner.SignData(
                SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
            };
            new SharedWorldRosterChainStore(Data).Append(roster, Root.OwnerPublicKey);
            Data.SaveProtected($"shared-world-pc-signing-{DeviceId:N}.protected", member.ExportPkcs8PrivateKey());
            Data.SaveProtected($"shared-world-roster-{DeviceId:N}-{Root.ProfileId:N}.protected",
                JsonSerializer.SerializeToUtf8Bytes(roster, Json));
            var aliasId = Guid.NewGuid();
            const string originalFile = "friend.protected";
            var aliasFile = $"friend-{aliasId:N}.protected";
            SaveConnection(originalFile, roster, DeviceId, originalConsent, alias: false);
            SaveConnection(aliasFile, roster, Guid.NewGuid(), aliasConsent, alias: true);
            Data.SaveProtected("friend-connections.protected", JsonSerializer.SerializeToUtf8Bytes(new
            { Ids = new[] { Guid.Empty, aliasId }, SelectedId = Guid.Empty }, Json));
            Data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 1,
                Groups = new[] { new { Root, Roster = roster, DeviceId,
                    Name = "Synthetic consent", ReceiveConsented = originalConsent || aliasConsent,
                    SharingEnabled = false, Bootstrap = (SharedWorldPeerRoute?)null } }
            }, Json));
            Original = new(Data, originalFile, NoNetwork);
            Alias = new(Data, aliasFile, NoNetwork);
            Friends = new(Data, NoNetwork);
            Service = new(Data, new HostManager(Data), Friends, new PairingService(Data), NoNetwork);
        }

        private void SaveConnection(string file, SharedWorldRoster roster, Guid authDeviceId, bool consent, bool alias)
        {
            Data.SaveProtected(file, JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
            {
                DisplayName = alias ? "Synthetic successor B" : "Synthetic original A",
                DeviceId = authDeviceId,
                Endpoint = alias ? "https://192.0.2.92:5131" : "https://192.0.2.91:5131",
                Fingerprint = new string(alias ? 'B' : 'A', 64),
                Credential = new string('C', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
                PeerWorld = alias ? Root : null,
                PeerWorldMemberDeviceId = alias ? DeviceId : null,
                ConsentedSharedWorldProfiles = consent ? [Root.ProfileId] : [],
                ApprovedSharedWorldGroups = new() { [Root.ProfileId] = Root.GroupId },
                SharedWorldSigningKeys = new() { [Root.ProfileId] = Root.OwnerPublicKey },
                SharedRosterFloors = new() { [Root.ProfileId] = new(Root.GroupId, roster.Epoch, roster.Revision, roster.Signature) }
            }, Json));
        }

        public void Dispose()
        { Service.Dispose(); Friends.Dispose(); Original.Dispose(); Alias.Dispose(); Data.Dispose(); member.Dispose(); owner.Dispose(); }
    }

    private static SemaphoreSlim LinkGate(FriendLink link) => (SemaphoreSlim)typeof(FriendLink)
        .GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(link)!;
    private static HttpClient NoNetwork(string endpoint, IEnumerable<string> pins) =>
        throw new InvalidOperationException("No network is allowed in receiving consent checks.");
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

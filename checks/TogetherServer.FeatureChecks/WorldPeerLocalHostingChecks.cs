using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Disposable protected records and held semaphores only. No app, listener,
// network, world payload or process is opened by these announcement checks.
internal static class WorldPeerLocalHostingChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent)
    {
        await BusyUnrelatedWorkAsync(parent);
        await StaleMembershipAsync(parent);
        await RetainedConsentAsync(parent);
        foreach (var fault in new[] { "unknown", "consent", "receive", "host", "revoked", "expired",
            "missing-key", "foreign-key", "governance", "fork", "foreign-root", "listener-off", "certificate", "canceled" })
            await RefusesAsync(parent, fault);
    }

    private static async Task RetainedConsentAsync(string parent)
    {
        foreach (var mode in new[] { "legacy", "automatic", "none" })
        {
            using var fixture = new Fixture(parent, automaticConsent: mode == "automatic", legacyConsent: mode == "legacy");
            Require(fixture.Friends.HasRetainedWorldReceiveConsent(fixture.Root, fixture.DeviceId) == (mode != "none") &&
                !fixture.Friends.HasRetainedWorldReceiveConsent(fixture.Root, Guid.NewGuid()) &&
                !fixture.Friends.HasRetainedWorldReceiveConsent(fixture.Root with { GroupId = Guid.NewGuid() }, fixture.DeviceId) &&
                !fixture.Friends.HasRetainedWorldReceiveConsent(fixture.Root with { ProfileId = Guid.NewGuid() }, fixture.DeviceId) &&
                !fixture.Friends.HasRetainedWorldReceiveConsent(fixture.Root with
                { OwnerPublicKey = fixture.Roster.Members.Single().PublicKey }, fixture.DeviceId),
                "retained consent lost legacy/automatic policy or crossed an exact world/member boundary");
            new WorldPeerReceiveConsentStore(fixture.Data).Set(fixture.Root, fixture.DeviceId, false);
            Require(!fixture.Friends.HasRetainedWorldReceiveConsent(fixture.Root, fixture.DeviceId),
                "legacy or automatic consent overrode the protected world/member withdrawal");
        }
        using (var missing = new Fixture(parent, automaticConsent: true, legacyConsent: false))
        {
            File.Delete(missing.KeyPath);
            Require(!missing.Friends.HasRetainedWorldReceiveConsent(missing.Root, missing.DeviceId) && !File.Exists(missing.KeyPath),
                "retained automatic consent created a missing sharing key instead of refusing");
        }
        foreach (var fault in new[] { "revoked", "pending", "conflict" })
        {
            using var fixture = new Fixture(parent, connectionFault: fault);
            Require(!fixture.Friends.HasRetainedWorldReceiveConsent(fixture.Root, fixture.DeviceId) && fixture.NetworkAttempts == 0,
                "retained consent ignored revoked, pending or conflicting source trust");
        }
        using var busy = new Fixture(parent, unrelated: true);
        using var source = busy.Friends.RetainLocalHostingConnection();
        Require(source is not null && busy.UnrelatedConnectionId is not null && busy.Friends.Select(busy.UnrelatedConnectionId.Value).Ok,
            "the synthetic unrelated saved connection was unavailable");
        using var unrelated = busy.Friends.RetainLocalHostingConnection();
        Require(unrelated is not null && busy.Friends.Select(Guid.Empty).Ok,
            "the synthetic source connection could not be restored");
        var sourceGate = Gate(source.Link, "gate");
        var unrelatedGate = Gate(unrelated.Link, "gate");
        await sourceGate.WaitAsync();
        await unrelatedGate.WaitAsync();
        Task<ReceivedSharedWorldResult>? withdrawal = null;
        try
        {
            Require(await Task.Run(() => busy.Friends.HasRetainedWorldReceiveConsent(busy.Root, busy.DeviceId))
                    .WaitAsync(TimeSpan.FromSeconds(5)),
                "retained source consent waited for its own or another saved connection's link gate");
            withdrawal = source.Link.SetSharedWorldConsentAsync(busy.Root.ProfileId, false);
            Require(!withdrawal.IsCompleted && !await Task.Run(() => busy.Friends.HasRetainedWorldReceiveConsent(busy.Root, busy.DeviceId))
                    .WaitAsync(TimeSpan.FromSeconds(5)) &&
                !new WorldPeerReceiveConsentStore(busy.Data).Effective(busy.Root, busy.DeviceId, true),
                "a blocked consent setter had not made its protected withdrawal effective before waiting for its link gate");
        }
        finally { unrelatedGate.Release(); sourceGate.Release(); }
        Require(withdrawal is not null && (await withdrawal.WaitAsync(TimeSpan.FromSeconds(5))).Ok &&
            !busy.Friends.HasRetainedWorldReceiveConsent(busy.Root, busy.DeviceId) && busy.NetworkAttempts == 0 &&
            busy.Driver.ProcessAttempts == 0 && busy.Data.LoadRuns().Count == 0,
            "retained consent revived withdrawal or crossed a network/process boundary after queued work completed");
    }

    private static async Task BusyUnrelatedWorkAsync(string parent)
    {
        using var fixture = new Fixture(parent);
        using var connection = fixture.Friends.RetainLocalHostingConnection();
        Require(connection is not null, "the synthetic saved connection was unavailable");
        var peerGate = Gate(fixture.Service, "refreshGate");
        var linkGate = Gate(connection.Link, "gate");
        await peerGate.WaitAsync();
        await linkGate.WaitAsync();
        try
        {
            var result = await fixture.Service.AnnounceLocalHostingAsync(fixture.Root, default)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Ok && result.Code == "HostingAddressPrepared" && fixture.SharingEnabled &&
                fixture.NetworkAttempts == 0 && fixture.Driver.ProcessAttempts == 0 && fixture.Data.LoadRuns().Count == 0 &&
                typeof(WorldPeerService).GetField("backgroundSync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(fixture.Service) is null,
                "local announcement waited for unrelated work, scheduled global work, or crossed a network/process boundary");
        }
        finally { linkGate.Release(); peerGate.Release(); }
    }

    private static async Task StaleMembershipAsync(string parent)
    {
        using var fixture = new Fixture(parent);
        using var connection = fixture.Friends.RetainLocalHostingConnection();
        Require(connection is not null, "the stale synthetic connection was unavailable");
        var linkGate = Gate(connection.Link, "gate");
        await linkGate.WaitAsync();
        Task refresh;
        try
        {
            refresh = fixture.Service.RefreshMembershipsAsync(false, default);
            Require(!refresh.IsCompleted, "the queued synthetic refresh did not wait for its captured Friend connection");
            var current = fixture.Advance(fixture.Roster.Members.Single());
            Require((await fixture.Service.AnnounceLocalHostingAsync(fixture.Root, default)
                .WaitAsync(TimeSpan.FromSeconds(5))).Ok && fixture.RegistrySignature == current.Signature && fixture.SharingEnabled,
                "exact local announcement failed to follow the newer signed journal without the blocked connection");
        }
        finally { linkGate.Release(); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.RegistrySignature == fixture.Head.Signature && fixture.SharingEnabled && fixture.NetworkAttempts == 0 &&
            fixture.Driver.ProcessAttempts == 0,
            "an older queued membership replaced the current roster or undid the explicit sharing enable");
    }

    private static async Task RefusesAsync(string parent, string fault)
    {
        using var fixture = new Fixture(parent, registered: fault != "unknown", listener: fault != "listener-off",
            certificate: fault != "certificate");
        var member = fixture.Roster.Members.Single();
        if (fault == "consent") new WorldPeerReceiveConsentStore(fixture.Data).Set(fixture.Root, fixture.DeviceId, false);
        else if (fault == "receive") fixture.Advance(member with { Grants = member.Grants with { Receive = false } });
        else if (fault == "host") fixture.Advance(member with { Grants = member.Grants with { EligibleHost = false } });
        else if (fault == "revoked") fixture.Advance(member with { Revoked = true });
        else if (fault == "expired") fixture.Advance(member with { AccessExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1) });
        else if (fault == "missing-key") File.Delete(fixture.KeyPath);
        else if (fault == "foreign-key")
        {
            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            fixture.Data.SaveProtected(fixture.KeyName, stranger.ExportPkcs8PrivateKey());
        }
        else if (fault == "governance")
        {
            var directory = Path.Combine(fixture.Data.RootPath, "shared-worlds", fixture.Root.ProfileId.ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "roster-dirty"), "synthetic unresolved owner edit");
        }
        else if (fault == "fork")
        {
            fixture.Advance(member);
            fixture.Append(fixture.Successor(fixture.Roster, member with { Grants = member.Grants with { Reshare = true } }));
        }
        using var cancellation = new CancellationTokenSource();
        if (fault == "canceled") cancellation.Cancel();
        var canceled = false;
        WorldPeerActionResult? result = null;
        try
        {
            result = await fixture.Service.AnnounceLocalHostingAsync(fault == "foreign-root" ?
                fixture.Root with { GroupId = Guid.NewGuid() } : fixture.Root, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { canceled = true; }
        var advisoryRoute = fault is "listener-off" or "certificate";
        Require((fault == "canceled" ? canceled : result is { Ok: false }) && fixture.SharingEnabled == advisoryRoute &&
            fixture.NetworkAttempts == 0 && fixture.Driver.ProcessAttempts == 0 && fixture.Data.LoadRuns().Count == 0 &&
            !Directory.Exists(Path.Combine(fixture.Data.RootPath, "never-read-world")),
            "local announcement bypassed trust, consent, key, governance or cancellation, or failed to arm outbound sharing for an advisory route");
        if (advisoryRoute)
            Require(result is { Code: "RecipientConnectionRequired" } &&
                fixture.Data.LoadSettings().CompanionListeningEnabled == (fault != "listener-off") &&
                (fixture.Data.LoadProtected("host-certificate.protected") is not null) == (fault != "certificate"),
                "an advisory route refusal enabled the listener, created a certificate, or lost its typed planned-move blocker");
        if (fault == "unknown")
            Require(fixture.GroupCount == 0, "an unknown root was implicitly registered by local announcement");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly LocalData Data;
        internal readonly Guid DeviceId = Guid.NewGuid();
        internal readonly SharedWorldPeerRoot Root;
        internal readonly SharedWorldRoster Roster;
        internal SharedWorldRoster Head { get; private set; }
        internal readonly LocalHostingChecks.FilesOnlyDriver Driver = new();
        internal readonly FriendService Friends;
        internal readonly WorldPeerService Service;
        internal readonly Guid? UnrelatedConnectionId;
        internal int NetworkAttempts;
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal string KeyName => $"shared-world-pc-signing-{DeviceId:N}.protected";
        internal string KeyPath => Path.Combine(Data.RootPath, KeyName);

        internal Fixture(string parent, bool registered = true, bool listener = true, bool certificate = true,
            bool automaticConsent = false, bool legacyConsent = true, string connectionFault = "", bool unrelated = false)
        {
            Data = new(Path.Combine(parent, "peer-local-hosting-" + Guid.NewGuid().ToString("N")));
            const string endpoint = "https://192.0.2.41:5131";
            Data.SaveSettings(new HostSettings
            {
                CompanionEndpoint = endpoint,
                CompanionBindAddress = "0.0.0.0",
                CompanionListeningEnabled = listener,
                Profiles = []
            });
            if (certificate) { using var identity = new HostIdentity(Data).Ensure(endpoint); }
            Root = new(Public(owner), Guid.NewGuid(), Guid.NewGuid());
            Roster = Sign(new(3, Root.GroupId, Root.ProfileId, 1, 1, true, Root.OwnerPublicKey,
                [new(DeviceId, Public(member), new(Receive: true, EligibleHost: true, Reshare: automaticConsent), false)], "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: Root.OwnerPublicKey));
            Head = Roster;
            Append(Roster);
            Data.SaveProtected(KeyName, member.ExportPkcs8PrivateKey());
            Data.SaveProtected($"shared-world-roster-{DeviceId:N}-{Root.ProfileId:N}.protected",
                JsonSerializer.SerializeToUtf8Bytes(Roster, Json));
            Data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
            {
                DeviceId = DeviceId,
                DisplayName = "Synthetic local announcement",
                Endpoint = endpoint,
                Fingerprint = new string('A', 64),
                Credential = new string('C', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                ConsentedSharedWorldProfiles = legacyConsent ? [Root.ProfileId] : [],
                KnownRevoked = connectionFault == "revoked",
                ApprovedSharedWorldGroups = new() { [Root.ProfileId] = Root.GroupId },
                SharedWorldSigningKeys = new() { [Root.ProfileId] = Root.OwnerPublicKey },
                SharedRosterFloors = new() { [Root.ProfileId] = new(Root.GroupId, 1, 1, Roster.Signature) },
                PendingSharedWorldGroups = connectionFault == "pending" ? new() { [Root.ProfileId] = Root.GroupId } : [],
                SharedWorldConflicts = connectionFault == "conflict" ? [Root.ProfileId] : []
            }, Json));
            if (unrelated)
            {
                UnrelatedConnectionId = Guid.NewGuid();
                Data.SaveProtected($"friend-{UnrelatedConnectionId.Value:N}.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
                {
                    DeviceId = Guid.NewGuid(),
                    DisplayName = "Synthetic unrelated connection",
                    Endpoint = "https://192.0.2.42:5131",
                    Fingerprint = new string('B', 64),
                    Credential = new string('E', 64),
                    CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
                }, Json));
                Data.SaveProtected("friend-connections.protected", JsonSerializer.SerializeToUtf8Bytes(new
                { Ids = new[] { Guid.Empty, UnrelatedConnectionId.Value }, SelectedId = Guid.Empty }, Json));
            }
            var groups = registered ? new[] { new { Root, Roster, DeviceId, Name = "Synthetic local announcement",
                ReceiveConsented = true, SharingEnabled = false, Bootstrap = (SharedWorldPeerRoute?)null } } : [];
            Data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, Groups = groups }, Json));
            Friends = new(Data, NoNetwork);
            Service = new(Data, new HostManager(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly)),
                Friends, new PairingService(Data), NoNetwork);
        }

        private HttpClient NoNetwork(string endpoint, IEnumerable<string> pins)
        { NetworkAttempts++; throw new InvalidOperationException("No network is permitted in local announcement checks."); }
        internal SharedWorldRoster Advance(SharedWorldRosterMember updated)
        {
            Head = Successor(Head, updated);
            Append(Head);
            return Head;
        }
        internal SharedWorldRoster Successor(SharedWorldRoster parent, SharedWorldRosterMember updated) => Sign(parent with
        {
            Epoch = parent.Epoch + 1,
            Revision = parent.Revision + 1,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(parent),
            Members = [updated],
            Signature = ""
        });
        internal void Append(SharedWorldRoster roster) => new SharedWorldRosterChainStore(Data).Append(roster, Root.OwnerPublicKey);
        private SharedWorldRoster Sign(SharedWorldRoster roster) => roster with
        { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };
        internal int GroupCount { get { using var document = Registry(); return document.RootElement.GetProperty("groups").GetArrayLength(); } }
        internal bool SharingEnabled
        {
            get
            {
                using var document = Registry(); return document.RootElement.GetProperty("groups")
            .EnumerateArray().Any(item => item.GetProperty("sharingEnabled").GetBoolean());
            }
        }
        internal string RegistrySignature
        {
            get
            {
                using var document = Registry(); return document.RootElement.GetProperty("groups")[0]
            .GetProperty("roster").GetProperty("signature").GetString()!;
            }
        }
        private JsonDocument Registry() => JsonDocument.Parse(Data.LoadProtected("world-peer-memberships.protected")!);
        public void Dispose() { Service.Dispose(); Friends.Dispose(); Data.Dispose(); owner.Dispose(); member.Dispose(); }
    }

    private static SemaphoreSlim Gate(object target, string name) => (SemaphoreSlim)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

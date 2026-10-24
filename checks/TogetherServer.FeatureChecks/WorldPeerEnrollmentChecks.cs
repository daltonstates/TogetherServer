using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure signed state and disposable protected files. No HTTP, listener, app,
// browser, game, process, native dialog or production data is involved.
internal static class WorldPeerEnrollmentChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Run(string root)
    {
        MetadataOnlyAndRestart(root);
        SignedDeadlineCanBeExtended(root);
        LocalDeadlineSurvivesEnrollment(root);
        TemporaryStateNeverRevokes(root);
        ExplicitRemovalIsImmediate(root);
    }

    private static void MetadataOnlyAndRestart(string root)
    {
        using var fixture = new Fixture(root);
        var alias = fixture.Alias;
        var authorized = fixture.Authenticate(out var device);
        Require(alias.DeviceId != fixture.Original.DeviceId && authorized.Ok,
            "a metadata-only member did not receive a separate usable transport credential");
        Require(device!.PeerWorldSourceDeviceId == fixture.Original.DeviceId &&
            device.Id == alias.DeviceId && fixture.Pairing.CanAccess(device, fixture.Source.ProfileId) &&
            !fixture.Pairing.CanStart(device, fixture.Source.ProfileId) &&
            !fixture.Pairing.CanStop(device, fixture.Source.ProfileId) &&
            !fixture.Pairing.CanExtendTimer(device, fixture.Source.ProfileId) &&
            !fixture.Pairing.CanViewLogs(device, fixture.Source.ProfileId) &&
            device.SaveReceiveProfileIds.Count == 0 && device.SharedWorldGrants.Count == 0,
            "session enrollment copied source control permissions or granted save access");
        Require(fixture.Pairing.SharedRosterMembers(fixture.Source.ProfileId).Single().DeviceId == fixture.Original.DeviceId,
            "an alias became another signed group member with a duplicate public key");
        Require(Bind(fixture.Pairing, fixture.Original.DeviceId, fixture.Member).Code == "IdentityAlreadyEnrolled" &&
            !fixture.Pairing.SharedRosterDirty(fixture.Source.ProfileId),
            "idempotent original key enrollment was rejected because a legitimate alias uses that same identity");
        Require(fixture.Pairing.SetReceiveSaves(alias.DeviceId, fixture.Source.ProfileId, true).Code == "PeerMembershipReadOnly" &&
            fixture.Pairing.SetSharedWorldGrants(alias.DeviceId, fixture.Source.ProfileId, new(Receive: true)).Code == "PeerMembershipReadOnly" &&
            !fixture.Pairing.SharedRosterDirty(fixture.Source.ProfileId),
            "editing a session alias mutated or dirtied the canonical signed membership");

        // An older legitimate alias can need harmless local normalization.
        // This must neither clear the shared identity nor dirty the global roster.
        var state = fixture.Data.LoadPairingState();
        state.Devices.Single(item => item.Id == alias.DeviceId).ServerPermissionOverrides = null;
        fixture.Data.SavePairingState(state);
        fixture.Pairing = new(fixture.Data, fixture.Clock);
        Require(fixture.Authenticate(out var restarted).Ok && restarted!.SharedWorldPublicKey == Public(fixture.Member) &&
            fixture.Pairing.SharedRosterMembers(fixture.Source.ProfileId).Count == 1 &&
            !fixture.Pairing.SharedRosterDirty(fixture.Source.ProfileId),
            "restart normalization revoked a legitimate alias or required unrelated signed membership publication");
    }

    private static void SignedDeadlineCanBeExtended(string root)
    {
        using var fixture = new Fixture(root);
        Require(fixture.Pairing.SetAccessExpiry(fixture.Original.DeviceId,
            new(AccessExpiresUtc: fixture.Clock.Now.AddHours(1))).Ok, "could not seed the signed owner deadline");
        fixture.Publish();
        fixture.Clock.Now = fixture.Clock.Now.AddHours(2);
        Require(fixture.Authenticate(out _).Code == "AccessExpired" && !fixture.View().Revoked,
            "a signed access deadline was reported as permanent revocation");
        Require(fixture.Pairing.SetAccessExpiry(fixture.Original.DeviceId,
            new(AccessExpiresUtc: fixture.Clock.Now.AddHours(8))).Ok, "the owner could not extend ended access");
        fixture.Publish();
        Require(fixture.Authenticate(out var device).Ok && device!.AccessExpiresUtc is null,
            "extending the signed deadline did not restore the existing alias credential or left a stale copied local deadline");
        fixture.Clock.Now = fixture.Clock.Now.AddDays(91);
        Require(fixture.Authenticate(out _).Code == "AccessExpired" && !fixture.View().Revoked,
            "an ended signed deadline became revoked after credential aging");

        using var expiredCredential = new Fixture(root);
        expiredCredential.Clock.Now = expiredCredential.Clock.Now.AddDays(91);
        Require(expiredCredential.Authenticate(out _).Code == "Expired" && !expiredCredential.View().Revoked,
            "ordinary canonical/alias credential expiry became permanent revocation");
    }

    private static void LocalDeadlineSurvivesEnrollment(string root)
    {
        using var fixture = new Fixture(root);
        var deadline = fixture.Clock.Now.AddHours(2);
        Require(fixture.Pairing.SetAccessExpiry(fixture.Alias.DeviceId, new(AccessExpiresUtc: deadline)).Ok &&
            !fixture.Pairing.SharedRosterDirty(fixture.Source.ProfileId), "a local alias deadline changed global signed membership");
        Require(fixture.Pairing.SetAccessExpiry(fixture.Original.DeviceId,
            new(AccessExpiresUtc: fixture.Clock.Now.AddHours(8))).Ok, "could not seed the longer owner deadline");
        fixture.Publish();
        var again = fixture.Pairing.EnrollPeerWorld(fixture.Source, fixture.Roster,
            fixture.Original.DeviceId, Public(fixture.Member), fixture.Source.ProfileId, null);
        Require(again is not null && again.DeviceId == fixture.Alias.DeviceId, "re-enrollment changed the existing scoped alias identity");
        fixture.Alias = again!;
        Require(fixture.Authenticate(out var device).Ok && device!.AccessExpiresUtc == deadline,
            "re-enrollment relaxed the successor Host's stricter local deadline");
        fixture.Clock.Now = fixture.Clock.Now.AddHours(3);
        Require(fixture.Authenticate(out _).Code == "AccessExpired" && !fixture.View().Revoked &&
            fixture.Pairing.EnrollPeerWorld(fixture.Source, fixture.Roster, fixture.Original.DeviceId,
                Public(fixture.Member), fixture.Source.ProfileId, null) is null,
            "re-enrollment revived an expired local Host restriction");
    }

    private static void TemporaryStateNeverRevokes(string root)
    {
        using (var fixture = new Fixture(root))
        {
            fixture.Pairing.RequireSharedRosterPublication(fixture.Source.ProfileId);
            Require(fixture.Authenticate(out _).Code == "PeerMembershipReviewRequired" && !fixture.View().Revoked,
                "a pending canonical signed roster was reported as revocation");
            fixture.Pairing.ConfirmSharedRosterPublished(fixture.Source.ProfileId, fixture.Roster);
            fixture.Data.SaveProtected("world-peer-memberships.protected", Encoding.UTF8.GetBytes("broken synthetic JSON"));
            Require(fixture.Authenticate(out _).Code == "PeerMembershipReviewRequired" && !fixture.View().Revoked,
                "damaged peer membership was reported as revocation");
            fixture.SaveMembership();
            Require(fixture.Authenticate(out _).Ok, "repairing temporary membership state did not restore the same credential");
            var newer = fixture.NextRoster(fixture.Roster.Members);
            new SharedWorldRosterChainStore(fixture.Data).Append(newer, fixture.Source.OwnerPublicKey);
            Require(fixture.Authenticate(out _).Code == "PeerMembershipReviewRequired" && !fixture.View().Revoked,
                "a newer verified roster floor permanently revoked an offline alias");
            fixture.Roster = newer; fixture.SaveMembership();
            Require(fixture.Authenticate(out _).Ok, "catching up the roster did not restore the saved alias");
        }
        using (var fixture = new Fixture(root))
        {
            var left = fixture.NextRoster([fixture.Roster.Members[0] with { AccessExpiresUtc = fixture.Clock.Now.AddHours(8) }]);
            var right = fixture.NextRoster([fixture.Roster.Members[0] with { AccessExpiresUtc = fixture.Clock.Now.AddHours(9) }]);
            var chain = new SharedWorldRosterChainStore(fixture.Data);
            chain.Append(left, fixture.Source.OwnerPublicKey);
            chain.Append(right, fixture.Source.OwnerPublicKey);
            Require(fixture.Authenticate(out _).Code == "PeerMembershipReviewRequired" && !fixture.View().Revoked,
                "a temporary signed roster fork became a permanent revocation");
        }
    }

    private static void ExplicitRemovalIsImmediate(string root)
    {
        using (var fixture = new Fixture(root))
        {
            var removed = fixture.NextRoster([fixture.Roster.Members[0] with { Revoked = true }]);
            new SharedWorldRosterChainStore(fixture.Data).Append(removed, fixture.Source.OwnerPublicKey);
            Require(fixture.Authenticate(out _).Code == "Revoked" && fixture.View().Revoked,
                "a complete newer signed revocation waited for cached metadata catch-up");
        }
        using (var fixture = new Fixture(root))
        {
            Require(fixture.Pairing.Revoke(fixture.Original.DeviceId).Ok && fixture.Authenticate(out _).Code == "Revoked",
                "canonical local revocation did not immediately deny its alias");
        }
        using (var fixture = new Fixture(root))
        {
            Require(fixture.Pairing.ResetSharedWorldKey(fixture.Original.DeviceId).Ok && fixture.Authenticate(out _).Code == "Revoked",
                "canonical signing-key removal did not revoke its old alias identity");
        }
        using (var fixture = new Fixture(root))
        {
            Require(fixture.Pairing.SetServerAccess(fixture.Original.DeviceId, [], null, [fixture.Source.ProfileId]).Ok &&
                fixture.Authenticate(out _).Code == "Revoked", "canonical server assignment removal left its alias usable");
        }
        using (var fixture = new Fixture(root))
        {
            Require(fixture.Pairing.Revoke(fixture.Alias.DeviceId).Ok &&
                !fixture.Pairing.SharedRosterDirty(fixture.Source.ProfileId) && fixture.Authenticate(out _).Code == "Revoked" &&
                fixture.Pairing.SharedRosterMembers(fixture.Source.ProfileId).Single().DeviceId == fixture.Original.DeviceId,
                "local alias removal changed canonical signed membership or failed to deny its credential");
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly LocalData Data;
        internal readonly ECDsa Owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly ECDsa Member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly Clock Clock = new(DateTimeOffset.UtcNow);
        internal readonly SharedWorldPeerRoot Source;
        internal readonly PairingCredential Original;
        internal PairingService Pairing;
        internal PairingCredential Alias;
        internal SharedWorldRoster Roster = null!;

        internal Fixture(string root)
        {
            Data = new(Path.Combine(root, "peer-enrollment-" + Guid.NewGuid().ToString("N")));
            var profile = new ServerProfile { Id = Guid.NewGuid(), SharedSavesEnabled = true, WorldId = "SyntheticPeerEnrollment" };
            profile.WorldDirectory = Data.NewWorldDirectory(profile.Id);
            Data.SaveSettings(new HostSettings { Profiles = [profile] });
            Data.SaveProtected("shared-world-signing-key.protected", Owner.ExportPkcs8PrivateKey());
            Pairing = new(Data, Clock);
            var invite = Pairing.IssueServer(profile.Id, true, false, "https://192.0.2.60:5131", new string('A', 64), false);
            Original = Pairing.Activate(new(profile.Id, invite.Code, true)) ?? throw new InvalidOperationException("synthetic original pairing failed");
            Require(Bind(Pairing, Original.DeviceId, Member).Ok, "synthetic canonical identity enrollment failed");
            Roster = new SharedWorldService(Data, new WorldBackupService(Data, Clock))
                .PublishRoster(profile, Pairing.SharedRosterMembers(profile.Id));
            Source = new(Public(Owner), profile.Id, Roster.GroupId);
            new SharedWorldRosterChainStore(Data).Append(Roster, Source.OwnerPublicKey);
            Pairing.ConfirmSharedRosterPublished(Source.ProfileId, Roster);
            SaveMembership();
            Alias = Pairing.EnrollPeerWorld(Source, Roster, Original.DeviceId, Public(Member), Source.ProfileId, null) ??
                throw new InvalidOperationException("synthetic metadata-only session enrollment failed");
        }

        internal SharedWorldRoster NextRoster(IReadOnlyList<SharedWorldRosterMember> members)
        {
            var revision = Roster is null ? 1 : Roster.Revision + 1;
            var draft = new SharedWorldRoster(3, Source.GroupId, Source.ProfileId, revision, revision, true,
                Source.OwnerPublicKey, members, "", PreviousRosterHash: Roster is null ? null : SharedWorldRosterTrust.Hash(Roster),
                SignerDeviceId: Guid.Empty, SignerPublicKey: Source.OwnerPublicKey, OwnerLocalBaselineMembers: members);
            return draft with { Signature = Convert.ToBase64String(Owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
        }

        internal void Publish()
        {
            Roster = NextRoster(Pairing.SharedRosterMembers(Source.ProfileId).OrderBy(member => member.DeviceId).ToArray());
            new SharedWorldRosterChainStore(Data).Append(Roster, Source.OwnerPublicKey);
            Pairing.ConfirmSharedRosterPublished(Source.ProfileId, Roster);
            SaveMembership();
        }

        internal void SaveMembership() => Data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Groups = new[] { new { Root = Source, Roster, DeviceId = Guid.Empty, Name = "Synthetic world",
                ReceiveConsented = true, SharingEnabled = true, Bootstrap = (SharedWorldPeerRoute?)null } }
        }, Json));
        internal PairingDecision Authenticate(out PairedDevice? device) => Pairing.Authenticate(Alias.DeviceId, Alias.Credential, out device);
        internal DeviceView View() => Pairing.Views().Single(item => item.Id == Alias.DeviceId);
        public void Dispose() { Owner.Dispose(); Member.Dispose(); Data.Dispose(); }
    }

    private static PairingDecision Bind(PairingService pairing, Guid id, ECDsa key)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var publicKey = Public(key);
        return pairing.BindSharedWorldKey(id, new(nonce, publicKey, Convert.ToBase64String(key.SignData(
            SharedWorldRosterTrust.EnrollmentBasis(id, nonce, publicKey), HashAlgorithmName.SHA256))));
    }
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

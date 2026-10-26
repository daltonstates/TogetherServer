using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Disposable signed/protected state and a fake count driver only. No app,
// listener, process, game, browser, native dialog or production data is opened.
internal static class ServerAccessRemovalChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Run(string root)
    {
        ScopedRemovalAndFreshPairing(root);
        LegacyEnrollmentRemoval(root, delegated: false);
        LegacyEnrollmentRemoval(root, delegated: true);
        HiddenTransportsKeepTheirAuthority(root);
        InheritedAssignmentRemoval(root);
    }

    private static void LegacyEnrollmentRemoval(string root, bool delegated)
    {
        using var fixture = new LegacyEnrollmentFixture(root);
        var chain = new SharedWorldRosterChainStore(fixture.Data);
        var first = chain.First(fixture.Main.Id)!;
        var latestLegacy = chain.Heads(fixture.Main.Id).Single();
        Require(first.Schema == 2 && first.Members.Count == 1 && latestLegacy.Schema == 2 &&
            latestLegacy.Members.Count == 2 && first.Members.All(member => member.DeviceId != fixture.PcB.DeviceId),
            "the legacy journal did not begin before the second PC's owner-signed enrollment");
        var retainedFiles = new[] { first, latestLegacy }.Select(roster => Path.Combine(fixture.Data.RootPath,
            "shared-worlds", fixture.Main.Id.ToString("N"), "roster-chain", SharedWorldRosterTrust.Hash(roster) + ".json"))
            .ToDictionary(path => path, File.ReadAllBytes);
        var siblingSignature = fixture.Shared.ReadRoster(fixture.Sibling)!.Signature;
        var expectedA = latestLegacy.Members.Single(member => member.DeviceId == fixture.PcA.DeviceId);
        if (delegated)
        {
            var revision = latestLegacy with
            {
                Schema = 3,
                Epoch = latestLegacy.Epoch + 1,
                Revision = latestLegacy.Revision + 1,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(latestLegacy),
                SignerDeviceId = fixture.PcB.DeviceId,
                SignerPublicKey = Public(fixture.KeyB),
                Members = latestLegacy.Members.Select(member => member.DeviceId == fixture.PcA.DeviceId
                    ? member with { Grants = member.Grants with { RecoveryVoter = true } } : member).ToArray(),
                Signature = ""
            };
            revision = revision with { Signature = Sign(fixture.KeyB, SharedWorldRosterTrust.Basis(revision)) };
            var accepted = fixture.Shared.CountersignDelegatedRoster(revision, DateTimeOffset.UtcNow);
            chain.Append(accepted, latestLegacy.OwnerPublicKey);
            expectedA = accepted.Members.Single(member => member.DeviceId == fixture.PcA.DeviceId);
            Require(accepted.OwnerLocalBaselineMembers is null && expectedA.Grants.RecoveryVoter &&
                !fixture.Pairing.SharedRosterMembers(fixture.Main.Id).Single(member => member.DeviceId == fixture.PcA.DeviceId).Grants.RecoveryVoter,
                "the delegated negative did not separate the signed role from the unchanged owner-local choice");
            var reconciled = fixture.Shared.PublishRoster(fixture.Main, fixture.Pairing.SharedRosterMembers(fixture.Main.Id), applyHostingPolicy: true);
            fixture.Pairing.ConfirmSharedRosterPublished(fixture.Main.Id, reconciled);
            Require(reconciled.Members.Single(member => member.DeviceId == fixture.PcA.DeviceId) == expectedA &&
                reconciled.OwnerLocalBaselineMembers!.SequenceEqual(fixture.Pairing.SharedRosterMembers(fixture.Main.Id).OrderBy(member => member.DeviceId)) &&
                chain.Contains(fixture.Main.Id, accepted),
                "reconciling a delegated null-baseline head reversed its signed edit or lost its exact proof");
        }
        var credentialBefore = fixture.Data.LoadPairingState().Devices.Single(device => device.Id == fixture.PcB.DeviceId).CredentialHash;
        Require(fixture.Pairing.RemoveServerAccess(fixture.PcB.DeviceId, fixture.Main.Id).Code == "ServerAccessRemoved",
            "the legacy enrollment fixture did not remove the second PC's exact scope");
        var published = fixture.Shared.PublishRoster(fixture.Main, fixture.Pairing.SharedRosterMembers(fixture.Main.Id), applyHostingPolicy: true);
        fixture.Pairing.ConfirmSharedRosterPublished(fixture.Main.Id, published);
        var bStillActive = published.Members.Any(member => member.DeviceId == fixture.PcB.DeviceId && !member.Revoked);
        var aUnchanged = published.Members.Single(member => member.DeviceId == fixture.PcA.DeviceId) == expectedA;
        var siblingUnchanged = fixture.Shared.ReadRoster(fixture.Sibling)!.Signature == siblingSignature;
        Require(!bStillActive && aUnchanged && siblingUnchanged &&
            chain.Contains(fixture.Main.Id, first) && chain.Contains(fixture.Main.Id, latestLegacy) &&
            retainedFiles.All(item => File.ReadAllBytes(item.Key).AsSpan().SequenceEqual(item.Value)),
            $"legacy scoped removal: bStillActive={bStillActive}, aUnchanged={aUnchanged}, siblingUnchanged={siblingUnchanged}");
        var removed = fixture.Data.LoadPairingState().Devices.Single(device => device.Id == fixture.PcB.DeviceId);
        Require(!removed.Revoked && removed.CredentialHash == credentialBefore &&
            fixture.Pairing.Authenticate(fixture.PcB.DeviceId, fixture.PcB.Credential, out var b).Ok &&
            !fixture.Pairing.CanAccess(b!, fixture.Main.Id) &&
            fixture.Pairing.Authenticate(fixture.PcA.DeviceId, fixture.PcA.Credential, out var a).Ok &&
            fixture.Pairing.CanAccess(a!, fixture.Main.Id) && fixture.Pairing.CanAccess(a!, fixture.Sibling.Id) &&
            !fixture.Pairing.SharedRosterDirty(fixture.Main.Id),
            "signed scope removal revoked a whole PC, changed sibling access, or left publication unconfirmed");
    }

    private sealed class LegacyEnrollmentFixture : IDisposable
    {
        internal LocalData Data { get; }
        internal PairingService Pairing { get; }
        internal SharedWorldService Shared { get; }
        internal ServerProfile Main { get; }
        internal ServerProfile Sibling { get; }
        internal PairingCredential PcA { get; }
        internal PairingCredential PcB { get; }
        internal ECDsa KeyB { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa keyA = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        internal LegacyEnrollmentFixture(string root)
        {
            Data = new(Path.Combine(root, "legacy-enrollment-removal-" + Guid.NewGuid().ToString("N")));
            Main = Profile("Legacy main"); Sibling = Profile("Untouched sibling");
            Data.SaveSettings(new() { Profiles = [Main, Sibling] });
            Data.SaveProtected("shared-world-signing-key.protected", owner.ExportPkcs8PrivateKey());
            Pairing = new(Data);
            var invite = Pairing.IssueServer(Main.Id, false, false, "https://192.0.2.115:5131", new string('A', 64), false);
            PcA = Pairing.Activate(new(Main.Id, invite.Code, true)) ?? throw new InvalidOperationException("legacy first PC pairing failed");
            Require(Pairing.SetServerAccess(PcA.DeviceId, [Main.Id, Sibling.Id], null, [Main.Id, Sibling.Id]).Ok &&
                Bind(Pairing, PcA.DeviceId, keyA).Ok, "legacy first PC assignment/enrollment failed");
            Shared = new(Data, new WorldBackupService(Data, TimeProvider.System));
            var first = Shared.PublishRoster(Main, Pairing.SharedRosterMembers(Main.Id));
            var chain = new SharedWorldRosterChainStore(Data);
            chain.Append(first, first.OwnerPublicKey);
            Pairing.ConfirmSharedRosterPublished(Main.Id, first);
            var sibling = Shared.PublishRoster(Sibling, Pairing.SharedRosterMembers(Sibling.Id));
            Pairing.ConfirmSharedRosterPublished(Sibling.Id, sibling);
            PcB = Pairing.Activate(new(Main.Id, invite.Code, true)) ?? throw new InvalidOperationException("legacy second PC pairing failed");
            Require(Bind(Pairing, PcB.DeviceId, KeyB).Ok && Pairing.SetSharedWorldGrants(PcB.DeviceId, Main.Id,
                new(Receive: true, EligibleHost: true, Reshare: true, ManageSharing: true)).Ok,
                "legacy second PC signing enrollment or sharing-manager setup failed");
            var latest = first with
            {
                Epoch = first.Epoch + 1,
                Revision = first.Revision + 1,
                Members = Pairing.SharedRosterMembers(Main.Id).OrderBy(member => member.DeviceId).ToArray(),
                Signature = ""
            };
            latest = latest with { Signature = Sign(owner, SharedWorldRosterTrust.Basis(latest)) };
            chain.Append(latest, latest.OwnerPublicKey);
            Pairing.ConfirmSharedRosterPublished(Main.Id, latest);
        }
        private ServerProfile Profile(string name)
        {
            var profile = new ServerProfile { Name = name, WorldId = name.Replace(" ", ""), SharedSavesEnabled = true };
            profile.WorldDirectory = Data.NewWorldDirectory(profile.Id);
            return profile;
        }
        public void Dispose() { KeyB.Dispose(); keyA.Dispose(); owner.Dispose(); Data.Dispose(); }
    }

    private static void ScopedRemovalAndFreshPairing(string root)
    {
        using var fixture = new Fixture(root);
        Require(fixture.Authenticate(out var stale).Ok, "the original PC did not authenticate");
        Require(fixture.Pairing.SetTemporaryHelper(fixture.Credential.DeviceId,
            new(DeviceAccessDurations.OneHour)).Ok, "could not configure temporary helper access");
        // A permission edit made after the caller loaded its PC must survive
        // the fixed removal action; no stale full assignment list is submitted.
        Require(fixture.Pairing.SetServerAccess(fixture.Credential.DeviceId, [fixture.A.Id, fixture.B.Id],
            [new(fixture.A.Id, true, false), new(fixture.B.Id, false, true, CanViewLogs: true)],
            [fixture.A.Id, fixture.B.Id]).Ok, "could not seed independent server permissions");
        fixture.PublishBoth();
        var before = fixture.Device(fixture.Credential.DeviceId);
        var beforeB = before.SharedWorldGrants[fixture.B.Id];
        var beforePermissionB = before.ServerPermissionOverrides!.Single(item => item.ProfileId == fixture.B.Id);
        var bSignature = fixture.RosterB.Signature;
        Require(fixture.Pairing.RemoveServerAccess(fixture.Credential.DeviceId, fixture.A.Id).Code == "ServerAccessRemoved",
            "the fixed action did not remove the selected server");
        var after = fixture.Device(fixture.Credential.DeviceId);
        Require(after.AssignedProfileIds!.SequenceEqual([fixture.B.Id]) && !after.Revoked &&
            !after.SharedWorldGrants.ContainsKey(fixture.A.Id) && after.SharedWorldGrants[fixture.B.Id] == beforeB &&
            !after.SaveReceiveProfileIds.Contains(fixture.A.Id) && after.SaveReceiveProfileIds.Contains(fixture.B.Id) &&
            after.ServerPermissionOverrides!.Count == 1 &&
            JsonSerializer.Serialize(after.ServerPermissionOverrides.Single(), Json) == JsonSerializer.Serialize(beforePermissionB, Json),
            "server removal discarded another assignment, grant or permission exception");
        Require(after.CredentialHash == before.CredentialHash && after.CredentialExpiresUtc == before.CredentialExpiresUtc &&
            after.PreviousCredentialHash == before.PreviousCredentialHash && after.InviteGeneration == before.InviteGeneration &&
            after.ProfileId == before.ProfileId && after.TemporaryHelperUntilUtc == before.TemporaryHelperUntilUtc &&
            after.CanStart == before.CanStart && after.CanStop == before.CanStop &&
            after.CanExtendTimer == before.CanExtendTimer && after.CanViewLogs == before.CanViewLogs,
            "server removal replaced credentials or changed global/default helper permissions");
        Require(fixture.Authenticate(out _).Ok && !fixture.Pairing.CanAccess(stale!, fixture.A.Id) &&
            !fixture.Pairing.CanStop(stale!, fixture.A.Id) && !fixture.Pairing.CanViewLogs(stale!, fixture.A.Id) &&
            fixture.Pairing.CanAccess(stale!, fixture.B.Id) && fixture.Pairing.CanStop(stale!, fixture.B.Id) &&
            fixture.Pairing.CanViewLogs(stale!, fixture.B.Id),
            "a stale PC object retained the removed scope or lost its other server");
        Require(fixture.Pairing.SharedRosterDirty(fixture.A.Id) && !fixture.Pairing.SharedRosterDirty(fixture.B.Id),
            "removing one server fenced an unrelated signed roster");
        fixture.PublishA();
        Require(fixture.RosterA.Members.All(member => member.DeviceId != fixture.Credential.DeviceId) &&
            fixture.RosterB.Signature == bSignature && fixture.Pairing.RemoveServerAccess(fixture.Credential.DeviceId, fixture.A.Id).Ok,
            "the targeted signed removal changed another group or was not idempotent");

        var invite = fixture.Pairing.IssueServer(fixture.A.Id, false, false,
            "https://192.0.2.110:5131", new string('A', 64), false, requireApproval: true);
        var replacement = fixture.Pairing.Activate(new(fixture.A.Id, invite.Code, true)) ??
            throw new InvalidOperationException("fresh server-code pairing was rejected");
        Require(replacement.DeviceId != fixture.Credential.DeviceId &&
            fixture.Pairing.Authenticate(replacement.DeviceId, replacement.Credential, out _).Code == "ApprovalPending" &&
            fixture.Pairing.Approve(replacement.DeviceId).Ok,
            "fresh server access reused an old credential or skipped configured approval");
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Require(Bind(fixture.Pairing, replacement.DeviceId, newKey).Ok, "the newly approved identity could not enroll");
        fixture.PublishA();
        Require(fixture.Pairing.Authenticate(replacement.DeviceId, replacement.Credential, out var joined).Ok &&
            fixture.Pairing.CanAccess(joined!, fixture.A.Id) && !fixture.Pairing.CanAccess(joined!, fixture.B.Id) &&
            joined!.TemporaryHelperUntilUtc is null && !joined.CanStart && !joined.CanStop &&
            fixture.RosterA.Members.Any(member => member.DeviceId == replacement.DeviceId && !member.Revoked) &&
            fixture.RosterA.Members.All(member => member.DeviceId != fixture.Credential.DeviceId) &&
            !fixture.Pairing.CanAccess(stale!, fixture.A.Id) && fixture.Pairing.CanAccess(stale!, fixture.B.Id),
            "fresh approved pairing revived old access or copied unrelated server/helper permissions");
        Require(fixture.Pairing.Revoke(fixture.Credential.DeviceId).Ok &&
            fixture.Authenticate(out _).Code == "Revoked" &&
            fixture.Pairing.Authenticate(replacement.DeviceId, replacement.Credential, out _).Ok,
            "explicit all-server removal stopped working or revoked the independent replacement credential");
    }

    private static void HiddenTransportsKeepTheirAuthority(string root)
    {
        using var fixture = new Fixture(root);
        Require(fixture.Pairing.SetName(fixture.Credential.DeviceId, "Shared world member").Ok,
            "could not test a normal PC with the same display name as an internal transport");
        Require(fixture.Pairing.OwnerPcViews().Single().Id == fixture.Credential.DeviceId &&
            fixture.Pairing.Views().Count == 2 && fixture.Pairing.Views().Any(view => view.Id == fixture.OwnerAlias.DeviceId),
            "the owner PC list exposed a transport or hid a normal PC by its name");
        Require(fixture.Pairing.Authenticate(fixture.OwnerAlias.DeviceId, fixture.OwnerAlias.Credential, out var owner).Ok &&
            fixture.Pairing.CanStop(owner!, fixture.A.Id) && !fixture.Pairing.CanStop(owner!, fixture.B.Id) &&
            fixture.Pairing.RemoveServerAccess(fixture.OwnerAlias.DeviceId, fixture.A.Id).Code == "UnknownDevice" &&
            fixture.Pairing.Authenticate(fixture.OwnerAlias.DeviceId, fixture.OwnerAlias.Credential, out _).Ok,
            "hiding an internal owner transport changed its authentication or exposed an ordinary-PC mutation");
        var driver = new CountOnlyDriver();
        var run = new ManagedRun { ProfileId = fixture.A.Id, Kind = driver.Kind, OperationId = Guid.NewGuid() };
        var snapshot = new HostSnapshot(new() { Profiles = [fixture.A] },
            [new(fixture.A.Id, "Ready", "Synthetic cached zero.", null, OnlinePlayers: 0, PlayerCountTrusted: true)],
            "Synthetic", "Host", new Dictionary<Guid, bool>(), "");
        using var permit = RemoteStopSafety.TryAcquire(snapshot, fixture.A.Id, fixture.Data,
            new GameServerRegistry([driver], PortProbeMode.ObserveOnly), [run]);
        Require(permit.Allowed && !permit.StillSafe(run) && driver.Queries == 1,
            "the hidden original owner's Stop bypassed a fresh Unknown player count");
        Require(fixture.Pairing.Revoke(fixture.OwnerAlias.DeviceId).Ok &&
            fixture.Pairing.Authenticate(fixture.OwnerAlias.DeviceId, fixture.OwnerAlias.Credential, out _).Code == "Revoked" &&
            !fixture.Pairing.CanStop(owner!, fixture.A.Id) && fixture.Pairing.OwnerPcViews().Count == 1 &&
            fixture.Pairing.EnrollPeerWorld(fixture.RootA, fixture.RosterA, Guid.Empty,
                fixture.RootA.OwnerPublicKey, fixture.A.Id, null) is null,
            "internal transport revocation was hidden from authorization or automatically revived");
    }

    private static void InheritedAssignmentRemoval(string root)
    {
        foreach (var damagedAuthority in new[] { false, true })
        {
            using var fixture = new Fixture(root);
            var inherited = fixture.InheritA();
            var floorName = $"authority-floor-{fixture.A.Id:N}.protected";
            if (damagedAuthority)
            {
                fixture.Data.SaveProtected(floorName, [1, 2, 3]);
                Require(AuthorityNeedsReview(fixture.Data, fixture.A.Id),
                    "the damaged inherited authority fixture remained valid");
            }
            var logPath = Path.Combine(fixture.Data.RootPath, "shared-worlds", fixture.A.Id.ToString("N"),
                "authority", "records.jsonl");
            var beforeLog = File.ReadAllBytes(logPath);
            var beforeFloor = fixture.Data.LoadProtected(floorName)!;
            var before = new SharedWorldRosterChainStore(fixture.Data).Heads(fixture.A.Id).Single().Signature;
            Require(fixture.Pairing.RemoveServerAccess(fixture.Credential.DeviceId, fixture.A.Id).Ok &&
                fixture.Authenticate(out var current).Ok && !fixture.Pairing.CanAccess(current!, fixture.A.Id) &&
                fixture.Pairing.CanAccess(current!, fixture.B.Id) && !fixture.Pairing.SharedRosterDirty(fixture.A.Id) &&
                new SharedWorldRosterChainStore(fixture.Data).Heads(fixture.A.Id).Single().Signature == before &&
                File.ReadAllBytes(logPath).AsSpan().SequenceEqual(beforeLog) &&
                fixture.Data.LoadProtected(floorName)!.AsSpan().SequenceEqual(beforeFloor),
                "removing an inherited local assignment required or changed signed membership or authority proof");
            Require(damagedAuthority ? AuthorityNeedsReview(fixture.Data, fixture.A.Id) :
                new WorldAuthorityStore(fixture.Data).LocalAuthorizedHead(fixture.A.Id)?.RecordHash == inherited.RecordHash,
                "local access removal repaired damaged authority or invalidated the verified successor");
            var state = JsonSerializer.Serialize(fixture.Data.LoadPairingState(), Json);
            Require(fixture.Pairing.RemoveServerAccess(Guid.NewGuid(), fixture.B.Id).Code == "UnknownDevice" &&
                fixture.Pairing.RemoveServerAccess(fixture.Credential.DeviceId, Guid.NewGuid()).Code == "UnknownServer" &&
                JsonSerializer.Serialize(fixture.Data.LoadPairingState(), Json) == state,
                "an unknown target changed saved access");
        }
    }

    private static bool AuthorityNeedsReview(LocalData data, Guid profileId)
    {
        try { _ = new WorldAuthorityStore(data).Read(profileId); return false; }
        catch (Exception error) when (error is InvalidDataException or JsonException) { return true; }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly LocalData Data;
        internal readonly ECDsa Owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly ECDsa PcKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal readonly ServerProfile A;
        internal readonly ServerProfile B;
        internal readonly PairingService Pairing;
        internal readonly PairingCredential Credential;
        internal readonly PairingCredential OwnerAlias;
        internal readonly SharedWorldService Shared;
        internal SharedWorldRoster RosterA = null!, RosterB = null!;
        internal SharedWorldPeerRoot RootA => new(RosterA.OwnerPublicKey, A.Id, RosterA.GroupId);

        internal Fixture(string root)
        {
            Data = new(Path.Combine(root, "server-access-removal-" + Guid.NewGuid().ToString("N")));
            A = Profile("SyntheticA"); B = Profile("SyntheticB");
            Data.SaveSettings(new() { Profiles = [A, B] });
            Data.SaveProtected("shared-world-signing-key.protected", Owner.ExportPkcs8PrivateKey());
            Pairing = new(Data);
            var invite = Pairing.IssueServer(A.Id, false, false, "https://192.0.2.110:5131", new string('A', 64), false);
            Credential = Pairing.Activate(new(A.Id, invite.Code, true)) ?? throw new InvalidOperationException("synthetic pairing failed");
            Require(Pairing.SetServerAccess(Credential.DeviceId, [A.Id, B.Id],
                [new(A.Id, true, false), new(B.Id, false, true)], [A.Id, B.Id]).Ok &&
                Bind(Pairing, Credential.DeviceId, PcKey).Ok, "synthetic two-server assignment or signing enrollment failed");
            Shared = new(Data, new WorldBackupService(Data, TimeProvider.System));
            PublishBoth();
            OwnerAlias = Pairing.EnrollPeerWorld(RootA, RosterA, Guid.Empty, RootA.OwnerPublicKey, A.Id, null) ??
                throw new InvalidOperationException("synthetic owner transport enrollment failed");
        }

        private ServerProfile Profile(string name)
        {
            var profile = new ServerProfile { Name = name, WorldId = name, SharedSavesEnabled = true };
            profile.WorldDirectory = Data.NewWorldDirectory(profile.Id);
            return profile;
        }
        internal void PublishBoth()
        {
            PublishA();
            RosterB = Shared.PublishRoster(B, Pairing.SharedRosterMembers(B.Id));
            if (!new SharedWorldRosterChainStore(Data).HasState(B.Id))
                new SharedWorldRosterChainStore(Data).Append(RosterB, RosterB.OwnerPublicKey);
            Pairing.ConfirmSharedRosterPublished(B.Id, RosterB);
        }
        internal void PublishA()
        {
            RosterA = Shared.PublishRoster(A, Pairing.SharedRosterMembers(A.Id));
            if (!new SharedWorldRosterChainStore(Data).HasState(A.Id))
                new SharedWorldRosterChainStore(Data).Append(RosterA, RosterA.OwnerPublicKey);
            Pairing.ConfirmSharedRosterPublished(A.Id, RosterA);
            Data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 1,
                Groups = new[] { new { Root = RootA, Roster = RosterA, DeviceId = Guid.Empty, Name = "SyntheticA",
                    ReceiveConsented = false, SharingEnabled = true, Bootstrap = (SharedWorldPeerRoute?)null } }
            }, Json));
        }
        internal WorldAuthorityRecord InheritA()
        {
            using var hosting = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Data.SaveProtected($"shared-world-pc-signing-{Credential.DeviceId:N}.protected", PcKey.ExportPkcs8PrivateKey());
            Data.SaveProtected(WorldAuthorityStore.HostingKeyName(A.Id), hosting.ExportPkcs8PrivateKey());
            byte[] bytes = [11, 22, 33];
            var version = SharedWorldService.SignVersion(new(4, RosterA.GroupId, 1, null, A.Id,
                A.Kind, A.WorldId, DateTimeOffset.UtcNow, SharedWorldCaptureKinds.PostStopBackup,
                Guid.NewGuid(), new(2456, false, "Unknown", [], []),
                [new("world.dat", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)))], "", "", ""), Owner);
            var proposal = new WorldAuthorityProposal(2, RosterA.GroupId, A.Id, 1, null,
                WorldAuthorityTrust.RosterHash(RosterA), version.VersionHash, Public(hosting),
                "https://192.0.2.110:5131", "Planned", Guid.Empty, Public(Owner), "");
            var binding = new WorldSuccessorBinding(Credential.DeviceId, Public(PcKey), Public(hosting), "");
            proposal = proposal with
            {
                SuccessorBinding = binding with
                {
                    Signature = Sign(PcKey, WorldAuthorityTrust.BindingBasis(proposal, binding))
                }
            };
            proposal = proposal with { Signature = Sign(Owner, WorldAuthorityTrust.ProposalBasis(proposal)) };
            var receipt = new SharedWorldReceipt(1, RosterA.GroupId, A.Id, version.VersionHash,
                Credential.DeviceId, RosterA.Epoch, RosterA.Revision, Guid.NewGuid(), "");
            receipt = receipt with { Signature = Sign(PcKey, SharedWorldReceiptTrust.Basis(receipt)) };
            var record = new WorldAuthorityRecord(1, proposal, RosterA, version, [],
                Sign(Owner, WorldAuthorityTrust.OwnerBasis(proposal)), "", receipt);
            record = record with { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(record)) };
            var authority = new WorldAuthorityStore(Data);
            authority.Append(record);
            authority.BindLocalSuccessor(A.Id, record.RecordHash, Credential.DeviceId);
            var inherited = authority.Read(A.Id).Single();
            Require(WorldAuthorityTrust.Verify(inherited) && authority.HasState(A.Id) &&
                authority.LocalAuthorizedHead(A.Id)?.RecordHash == inherited.RecordHash && !Pairing.SharedRosterDirty(A.Id),
                "the inherited fixture lacks authentic signed authority and successor key proof");
            return inherited;
        }
        internal PairedDevice Device(Guid id) => Data.LoadPairingState().Devices.Single(device => device.Id == id);
        internal PairingDecision Authenticate(out PairedDevice? device) => Pairing.Authenticate(Credential.DeviceId, Credential.Credential, out device);
        public void Dispose() { Owner.Dispose(); PcKey.Dispose(); Data.Dispose(); }
    }

    private sealed class CountOnlyDriver : IGameServerDriver
    {
        internal int Queries;
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Synthetic count-only driver";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => false;
        public GameHealthResult Health(ManagedRun run) { Queries++; return new(false, "SyntheticUnknown", "Unknown", "No current count."); }
        public string? ManagedSaveDirectory(ServerProfile profile) => throw new InvalidOperationException("No save access is allowed.");
        public string ManagedExecutablePath(ServerProfile profile) => throw new InvalidOperationException("No process inspection is allowed.");
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => throw new InvalidOperationException("No Start is allowed.");
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No Start is allowed.");
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No Start is allowed.");
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw new InvalidOperationException("No Stop is allowed.");
    }

    private static PairingDecision Bind(PairingService pairing, Guid deviceId, ECDsa key)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return pairing.BindSharedWorldKey(deviceId, new(nonce, publicKey, Convert.ToBase64String(key.SignData(
            SharedWorldRosterTrust.EnrollmentBasis(deviceId, nonce, publicKey), HashAlgorithmName.SHA256))));
    }
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static string Sign(ECDsa key, byte[] basis) => Convert.ToBase64String(key.SignData(basis, HashAlgorithmName.SHA256));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

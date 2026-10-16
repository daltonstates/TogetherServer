using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Signed synthetic saves, real source selection and fresh protected data only.
// No network, listener, fixture process, game, app or native dialog is used.
internal static class WorldPeerRecoverySelectionChecks
{
    internal static async Task RunAsync(string root)
    {
        using (var complete = new Fixture(root, "complete-gap"))
        {
            var before = complete.Store.Project();
            Require(before.Branches.Count == 2 && before.Branches.All(item => item.HasConflict) &&
                before.Peers.All(item => !item.HasConflict), "the fixture did not represent missed announcement2");
            var selected = await complete.Service.ResolveRecoverySourceAsync(complete.Root.GroupKey, complete.Lane, default);
            selected.Verify(complete.Data, complete.Root);
            Require(selected.Descriptor.Version.VersionHash == complete.Third.VersionHash &&
                FriendLink.ReadReceivedLatest(selected.Vault)?.VersionHash == complete.Third.VersionHash &&
                complete.Store.Project().Branches.Count == 2 &&
                new WorldAuthorityStore(complete.Data).Read(complete.Root.ProfileId).Count == 0,
                "complete1->2->3 ancestry did not select3 without erasing uncertain metadata or changing authority");
        }
        using (var missing = new Fixture(root, "missing-parent", includeSecond: false))
        {
            await RefusesAsync(missing, "a complete payload with missing signed parent2 resolved the lane");
            Require(FriendLink.ReadReceivedLatest(missing.Vault)?.VersionHash == missing.Third.VersionHash,
                "refused incomplete ancestry erased retained payload evidence");
        }
        using (var fork = new Fixture(root, "true-fork"))
        {
            var otherSecond = fork.Child(fork.First, "divergent second");
            var otherThird = fork.Child(otherSecond, "divergent third");
            var retained = Path.Combine(fork.Data.RootPath, "received-shared-histories", fork.Root.GroupKey, otherThird.VersionHash);
            fork.Write(retained, fork.First, "initial");
            fork.Write(retained, otherSecond, "divergent second");
            fork.Write(retained, otherThird, "divergent third");
            _ = fork.Publish(otherThird);
            await RefusesAsync(fork, "a same-number divergent signed3 was selected instead of preserving the fork");
            Require(FriendLink.ReadReceivedLatest(retained)?.VersionHash == otherThird.VersionHash,
                "reviewing a true fork erased its complete copy");
        }
        using (var crossed = new Fixture(root, "cross-root", foreignThird: true))
            await RefusesAsync(crossed, "a save signed by the same key but for another root/group was selected");
        using (var equivocated = new Fixture(root, "signer-equivocation"))
        {
            var changed = SharedWorldPeerTrust.Sign(equivocated.Last with
            {
                AnnouncedUtc = equivocated.Last.AnnouncedUtc.AddSeconds(1), AnnouncementHash = "", Signature = ""
            }, equivocated.Owner);
            Require(equivocated.Store.Import(changed).Changed && equivocated.Store.Project().Peers.Single().HasConflict,
                "the fixture did not retain actual same-revision signer equivocation");
            await RefusesAsync(equivocated, "valid payload ancestry silently repaired actual signer equivocation");
        }
        using (var corrupt = new Fixture(root, "damaged-payload"))
        {
            File.WriteAllText(Path.Combine(corrupt.Vault, corrupt.Third.VersionHash,
                SharedWorldService.PayloadDirectory, "world.dat"), "changed");
            await RefusesAsync(corrupt, "signed metadata ancestry selected a candidate with damaged payload bytes");
        }
    }

    private static async Task RefusesAsync(Fixture fixture, string message)
    {
        try
        {
            await fixture.Service.ResolveRecoverySourceAsync(fixture.Root.GroupKey, fixture.Lane, default);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or JsonException)
        {
            Require(!Directory.Exists(Path.Combine(fixture.Data.RootPath, "world-peer-recovery-sources")),
                "an ambiguous lane was normalized or promoted before proof resolution");
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        internal LocalData Data { get; }
        internal ECDsa Owner { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal SharedWorldPeerRoot Root { get; }
        internal string Lane { get; }
        internal string Vault { get; }
        internal SharedWorldVersion First { get; }
        internal SharedWorldVersion Third { get; }
        internal SharedWorldPeerStore Store { get; }
        internal SharedWorldPeerAnnouncement Last { get; }
        internal WorldPeerService Service { get; }
        private readonly FriendService friends;

        internal Fixture(string parent, string name, bool includeSecond = true, bool foreignThird = false)
        {
            Data = new LocalData(Path.Combine(parent, "recovery-selection-" + name + "-" + Guid.NewGuid().ToString("N")));
            Root = new(Convert.ToBase64String(Owner.ExportSubjectPublicKeyInfo()), Guid.NewGuid(), Guid.NewGuid());
            Lane = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic lane " + Root.GroupKey)));
            var draft = new SharedWorldRoster(3, Root.GroupId, Root.ProfileId, 1, 1, true, Root.OwnerPublicKey,
                [], "", SignerDeviceId: Guid.Empty, SignerPublicKey: Root.OwnerPublicKey);
            var roster = draft with { Signature = Convert.ToBase64String(Owner.SignData(
                SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            Data.SaveSettings(new HostSettings { Profiles = [] });
            Data.SaveProtected("shared-world-signing-key.protected", Owner.ExportPkcs8PrivateKey());
            new SharedWorldRosterChainStore(Data).Append(roster, Root.OwnerPublicKey);
            Data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 1, Groups = new[] { new { Root, Roster = roster, DeviceId = Guid.Empty,
                    Name = "Synthetic selection", ReceiveConsented = true, SharingEnabled = false,
                    Bootstrap = (SharedWorldPeerRoute?)null } }
            }, Json));
            First = SharedWorldService.SignVersion(new(4, Root.GroupId, 1, null, Root.ProfileId,
                GameKinds.Fixture, "SyntheticSelection", DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
                SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(2456, false, "Unknown", [], []),
                [Fact("initial")], "", "", ""), Owner);
            var second = Child(First, "second");
            Third = Child(second, "third");
            if (foreignThird) Third = SharedWorldService.SignVersion(Third with { GroupId = Guid.NewGuid() }, Owner);
            Vault = Path.Combine(Data.RootPath, "received-shared-worlds", Guid.Empty.ToString("N"), Root.ProfileId.ToString("N"));
            Write(Vault, First, "initial");
            if (includeSecond) Write(Vault, second, "second");
            Write(Vault, Third, "third");
            Store = new(Data, Root, new SharedWorldPeerTrust(Root,
                (id, key) => SharedWorldPeerTrust.AccessFromRoster(Root, roster, id, key)));
            _ = Publish(First);
            Last = Publish(Third);
            friends = new FriendService(Data, (_, _) => throw new InvalidOperationException("No network is allowed in selection checks."));
            Service = new(Data, new HostManager(Data), friends, new PairingService(Data),
                (_, _) => throw new InvalidOperationException("No network is allowed in selection checks."));
        }

        internal SharedWorldVersion Child(SharedWorldVersion parent, string payload) => SharedWorldService.SignVersion(parent with
        { Number = parent.Number + 1, ParentHash = parent.VersionHash, BackupId = Guid.NewGuid(), Files = [Fact(payload)] }, Owner);
        internal SharedWorldPeerAnnouncement Publish(SharedWorldVersion version) => Store.Publish(Guid.Empty, Owner,
            [], [], [new(Lane, First.VersionHash, version.VersionHash, version.ParentHash, version.Number)]);
        internal void Write(string root, SharedWorldVersion version, string payload)
        {
            var directory = Path.Combine(root, version.VersionHash);
            Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
            File.WriteAllText(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), payload);
            File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            FriendLink.KeepSignedManifest(root, version);
            File.WriteAllBytes(Path.Combine(root, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        }
        private static SharedWorldFile Fact(string payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            return new("world.dat", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        public void Dispose() { Service.Dispose(); friends.Dispose(); Data.Dispose(); Owner.Dispose(); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

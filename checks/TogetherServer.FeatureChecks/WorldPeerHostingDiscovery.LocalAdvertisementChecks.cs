using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class WorldPeerHostingDiscoveryChecks
{
    private static async Task LocalAdvertisementIdentityChecksAsync(string parent)
    {
        using (var fixture = new OriginalPublicationFixture(parent, "advertisement-existing-owner-key", fileCount: 3))
        {
            Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
                "the strict advertisement fixture could not register its existing owner");
            await KeysAsync(fixture.Data, fixture.Peers, fixture.Root, Guid.Empty);
        }
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var data = new LocalData(Path.Combine(parent, "advertisement-member-key-" + Guid.NewGuid().ToString("N")));
        data.SaveSettings(new() { Profiles = [] });
        var root = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var id = Guid.NewGuid();
        var roster = SignRoster(new(3, root.GroupId, root.ProfileId, 1, 1, true, root.OwnerPublicKey,
            [new(id, Public(member), new(Receive: true, EligibleHost: true, Reshare: true), false)], "",
            SignerDeviceId: Guid.Empty, SignerPublicKey: root.OwnerPublicKey), owner);
        new SharedWorldRosterChainStore(data).Append(roster, root.OwnerPublicKey);
        data.SaveProtected($"shared-world-pc-signing-{id:N}.protected", member.ExportPkcs8PrivateKey());
        SaveGroups(data, [Membership(root, roster, id, null)]);
        HttpClient NoNetwork(string _, IEnumerable<string> __) => throw new InvalidOperationException("Local advertisement checks cannot use a network.");
        using var friends = new FriendService(data, NoNetwork);
        using var peers = new WorldPeerService(data, new HostManager(data), friends, new PairingService(data), NoNetwork);
        await KeysAsync(data, peers, root, id);
        Require(!File.Exists(Path.Combine(data.RootPath, "shared-world-signing-key.protected")),
            "member advertisement inspection created an unrelated original-owner key");

        foreach (var change in new[] { "key", "membership", "consent" })
        {
            using var fixture = new OriginalPublicationFixture(parent, "advertisement-final-" + change, fileCount: 3);
            Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
                "the final advertisement fixture could not register its original world");
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var barrier = typeof(WorldPeerService).GetField("localAdvertisementBuiltForChecks", flags)!;
            Dictionary<string, byte[]>? expected = null;
            var observed = false;
            barrier.SetValue(fixture.Peers, (Action)(() =>
            {
                observed = true;
                if (change == "key") File.WriteAllText(Path.Combine(fixture.Data.RootPath, "shared-world-signing-key.protected"), "{malformed-after-snapshot");
                else if (change == "membership")
                    SaveGroups(fixture.Data, [Membership(fixture.Root, fixture.Base.Target.Roster, Guid.NewGuid(), null)]);
                else Require(new WorldPeerReceiveConsentStore(fixture.Data).Set(fixture.Root, Guid.Empty, false),
                    "the final advertisement consent withdrawal failed");
                expected = AdvertisementIdentityFiles(fixture.Data);
            }));
            var refused = false;
            try { refused = !await PublishLocalAdvertisementForChecksAsync(fixture.Peers, fixture.Root); }
            catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error)) { refused = true; }
            finally { barrier.SetValue(fixture.Peers, null); }
            Require(observed && refused && expected is not null,
                "an advertisement published after its awaited snapshot changed current " + change);
            RequireAdvertisementIdentityFiles(fixture.Data, expected);
            fixture.RequireNoCopyOrTransfer();
        }

        using (var fixture = new OriginalPublicationFixture(parent, "advertisement-canceled-wait", fileCount: 3))
        using (var canceled = new CancellationTokenSource())
        {
            Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
                "the canceled local advertisement fixture lacked its original membership");
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var gates = typeof(WorldPeerService).GetMethod("ExchangeGatesFor", flags)!.Invoke(fixture.Peers, [fixture.Root])!;
            var gate = (SemaphoreSlim)gates.GetType().GetProperty("LocalAdvertisement", flags)!.GetValue(gates)!;
            var expected = AdvertisementIdentityFiles(fixture.Data);
            await gate.WaitAsync();
            try
            {
                var resolving = fixture.Peers.ResolveLatestOriginalStartSourceAsync(fixture.Root, canceled.Token);
                Require(!resolving.IsCompleted, "the canceled instruction did not reach its held local metadata gate");
                canceled.Cancel();
                try { await resolving; throw new InvalidOperationException("Canceled local metadata preparation completed."); }
                catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            }
            finally { gate.Release(); }
            RequireAdvertisementIdentityFiles(fixture.Data, expected);
            fixture.RequireNoCopyOrTransfer();
        }

        static async Task KeysAsync(LocalData data, WorldPeerService service, SharedWorldPeerRoot root, Guid id)
        {
            var name = id == Guid.Empty ? "shared-world-signing-key.protected" : $"shared-world-pc-signing-{id:N}.protected";
            var path = Path.Combine(data.RootPath, name);
            var original = File.ReadAllBytes(path);
            foreach (var fault in new[] { "missing", "json", "base64", "ciphertext", "private-key" })
            {
                if (fault == "missing") File.Delete(path);
                else if (fault == "json") File.WriteAllText(path, "{malformed-protected-key");
                else if (fault == "base64") File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes("invalid base64", Json));
                else if (fault == "ciphertext") File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(Convert.ToBase64String(new byte[32]), Json));
                else data.SaveProtected(name, Encoding.UTF8.GetBytes("invalid private key"));
                var expected = AdvertisementIdentityFiles(data);
                try
                {
                    if (id == Guid.Empty)
                    {
                        var result = await service.ResolveLatestOriginalStartSourceAsync(root, default);
                        Require(!result.Ok && result.Source is null, "a malformed retained owner key authorized discovery");
                    }
                    else
                    {
                        var result = await service.ResolveLatestPreparedStartSourceAsync(root, Guid.NewGuid(), new string('A', 64), default);
                        Require(!result.Ok && result.Source is null, "a malformed retained member key authorized discovery");
                    }
                    RequireAdvertisementIdentityFiles(data, expected);
                }
                finally { File.WriteAllBytes(path, original); }
            }
            Require(await PublishLocalAdvertisementForChecksAsync(service, root),
                "the strict retained-key advertisement never accepted a valid existing identity");
        }
    }

    private static Dictionary<string, byte[]> AdvertisementIdentityFiles(LocalData data)
    {
        var files = OwnerIdentityFiles(data);
        var registry = Path.Combine(data.RootPath, "world-peer-memberships.protected");
        if (File.Exists(registry)) files.Add("world-peer-memberships.protected", File.ReadAllBytes(registry));
        return files;
    }

    private static void RequireAdvertisementIdentityFiles(LocalData data, IReadOnlyDictionary<string, byte[]> before)
    {
        var current = AdvertisementIdentityFiles(data);
        Require(before.Count == current.Count && before.All(item => current.TryGetValue(item.Key, out var bytes) && bytes.SequenceEqual(item.Value)),
            "local advertisement inspection created, quarantined or changed existing identity/membership records");
    }

    private static async Task<bool> PublishLocalAdvertisementForChecksAsync(WorldPeerService service, SharedWorldPeerRoot root)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var group = typeof(WorldPeerService).GetMethod("Find", flags)!.Invoke(service, [root.GroupKey])!;
        return await (Task<bool>)typeof(WorldPeerService).GetMethod("PublishLocalAdvertisementAsync", flags)!
            .Invoke(service, [group, CancellationToken.None, false, true])!;
    }
}

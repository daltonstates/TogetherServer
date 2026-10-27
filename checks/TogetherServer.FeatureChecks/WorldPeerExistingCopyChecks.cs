using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Generated signed publications/branches and in-memory HTTP handlers only.
// No socket, listener, app, game, process or production record is used.
internal static class WorldPeerExistingCopyChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync(string parent)
    {
        foreach (var shape in new[] { "Original", "Branch", "Retained" }) await ReuseAsync(parent, shape);
        foreach (var mutation in new[] { "Payload", "Consent", "Sharing", "Roster", "Sender", "Key", "MissingKey", "MalformedKey", "MissingParent" })
            await RefusalAsync(parent, mutation);
        await CurrentReceiptScopeAsync(parent);
        await LegacyReceiptAsync(parent);
        await ExactPairAsync(parent);
        await ForkAsync(parent);
        await OutboundOnlyAsync(parent);
        await ConsentLockOrderAsync(parent);
    }

    private static async Task ReuseAsync(string parent, string shape)
    {
        using var fixture = new Fixture(parent, shape);
        var before = fixture.Pointers();
        var hashes = 0;
        fixture.TargetService.ReceiptPayloadHashingForChecks = () => hashes++;
        using (fixture.TargetService.BeginHostingPreparation(fixture.Root))
        {
            var paused = await fixture.SeedAsync();
            Require(paused is { Code: "SeedPaused", Receipt: null } && hashes == 0 && fixture.Sidecars().Length == 0,
                "An explicit preparation did not defer new inbound existing-copy hashing/receipt work: " + shape);
        }
        await fixture.PullAsync();
        Require(hashes == 1 && fixture.RemoteHandler.Operations.Contains(WorldPeerOperations.Catalog) &&
            fixture.RemoteHandler.Operations.Contains(WorldPeerOperations.Receipt) &&
            !fixture.RemoteHandler.Operations.Contains(WorldPeerOperations.Chunk) && !Directory.Exists(fixture.DuplicateVault),
            "The real catalog path copied an exact already complete local publication instead of reusing it: " + shape);
        var sidecar = fixture.Sidecars().Single();
        var receiptBytes = File.ReadAllBytes(sidecar);
        var first = JsonSerializer.Deserialize<SharedWorldReceipt>(receiptBytes, Json)!;
        Require(fixture.ValidReceipt(first), "The reused copy did not create an exact current selfreceipt: " + shape);
        await fixture.PullAsync();
        Require(hashes == 1 && fixture.Pointers() == before && File.ReadAllBytes(sidecar).SequenceEqual(receiptBytes),
            "A confirmed receipt poll rehashed payload or changed copy pointers/receipt identity: " + shape);
        var seeded = await fixture.SeedAsync();
        Require(seeded is { Code: "SeedKnown", Receipt: not null } && seeded.Receipt == first && hashes == 2 &&
            !Directory.Exists(fixture.DuplicateVault) && fixture.Pointers() == before,
            "The initial inbound seed did not reverify and reuse the exact same receipt: " + shape);
        fixture.ReopenTarget();
        var reopened = await fixture.SeedAsync();
        Require(reopened is { Code: "SeedKnown", Receipt: not null } && reopened.Receipt == first &&
            fixture.Sidecars().Length == 1 && File.ReadAllBytes(sidecar).SequenceEqual(receiptBytes) &&
            fixture.Pointers() == before && fixture.A.LoadRuns().Count == 0 && fixture.B.LoadRuns().Count == 0,
            "Reopening regenerated a receipt, changed protected copy state, or created a run: " + shape);
        if (shape != "Original")
            Require(!File.Exists(Path.Combine(fixture.Target.RootPath, "shared-world-signing-key.protected")),
                "A branch/retained receipt required or created the original owner's private key.");
    }

    private static async Task RefusalAsync(string parent, string mutation)
    {
        using var fixture = new Fixture(parent, mutation is "MissingParent" or "Sender" ? "Original" : "Branch");
        var before = fixture.Pointers();
        var keyPath = fixture.KeyPath;
        byte[]? expectedKey = File.ReadAllBytes(keyPath);
        if (mutation == "Payload")
        {
            var changed = File.ReadAllBytes(fixture.PayloadPath); changed[0] ^= 1;
            File.WriteAllBytes(fixture.PayloadPath, changed);
        }
        else if (mutation == "MissingParent")
            File.Delete(Path.Combine(fixture.A.RootPath, "shared-worlds", fixture.Root.ProfileId.ToString("N"),
                fixture.Root.GroupId.ToString("N"), "1", "version.json"));
        else if (mutation == "MissingKey") { File.Delete(keyPath); expectedKey = null; }
        else if (mutation == "MalformedKey") { expectedKey = Encoding.UTF8.GetBytes("\"not-base64-key\""); File.WriteAllBytes(keyPath, expectedKey); }
        else fixture.TargetService.ReceiptPayloadHashingForChecks = () =>
        {
            fixture.TargetService.ReceiptPayloadHashingForChecks = null;
            if (mutation == "Consent") Require(fixture.TargetService.SetReceivingAsync(fixture.Root.GroupKey, false, default).Result.Ok, "Consent mutation failed.");
            else if (mutation == "Sharing") fixture.SaveMemberships(sharing: false, targetOnly: true);
            else if (mutation == "Roster") fixture.AdvanceRoster(receive: true);
            else if (mutation == "Sender") fixture.RevokeSender();
            else if (mutation == "Key")
            {
                using var changed = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                fixture.Target.SaveProtected(Path.GetFileName(keyPath), changed.ExportPkcs8PrivateKey());
                expectedKey = File.ReadAllBytes(keyPath);
            }
        };
        await RejectAsync(mutation is "MissingKey" or "MalformedKey" ? fixture.ReuseDirectAsync : fixture.SeedAsync,
            "A changed exact local copy/scope returned an accepted seed receipt: " + mutation);
        Require(fixture.Sidecars().Length == 0 && !Directory.Exists(fixture.DuplicateVault) && fixture.Pointers() == before &&
            (expectedKey is null ? !File.Exists(keyPath) : File.ReadAllBytes(keyPath).SequenceEqual(expectedKey)),
            "Refusing exact reuse rewrote a protected key, pointer/observation, receipt, or duplicate payload: " + mutation);
        if (mutation == "Payload")
        {
            await RejectAsync(fixture.PullAsync, "A damaged exact publication silently fell back to another download.");
            Require(!Directory.Exists(fixture.DuplicateVault) && fixture.RemoteHandler.Operations.All(item => item != WorldPeerOperations.Chunk),
                "A damaged exact publication was refilled through the receive path.");
        }
    }

    private static async Task CurrentReceiptScopeAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        var first = await fixture.SeedAsync();
        Require(first.Receipt is not null, "The first current-scope receipt was absent.");
        var original = fixture.Sidecars().Single();
        var originalBytes = File.ReadAllBytes(original);
        fixture.AdvanceRoster(receive: false);
        var paused = await fixture.SeedAsync();
        Require(paused is { Code: "SeedPaused", Receipt: null } && fixture.Sidecars().Length == 1,
            "A revoked receiving grant reused a stale receipt.");
        fixture.AdvanceRoster(receive: true);
        var renewed = await fixture.SeedAsync();
        Require(renewed is { Code: "SeedKnown", Receipt: not null } && fixture.ValidReceipt(renewed.Receipt) &&
            renewed.Receipt.ReceiptId != first.Receipt.ReceiptId && fixture.Sidecars().Length == 2 &&
            File.ReadAllBytes(original).SequenceEqual(originalBytes),
            "A fresh receiving grant could not create one new current-scope receipt while preserving the old receipt.");
        var current = fixture.Sidecars().Single(path => path != original);
        File.WriteAllText(current, "{malformed immutable receipt");
        var malformed = File.ReadAllBytes(current);
        await RejectAsync(fixture.SeedAsync, "A malformed immutable current receipt was regenerated.");
        Require(File.ReadAllBytes(current).SequenceEqual(malformed) && fixture.Sidecars().Length == 2,
            "A malformed current-scope receipt was replaced or accumulated new identities.");
    }

    private static async Task ExactPairAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Retained");
        // Only the retained branch remains inventoried after this generated
        // canonical pointer is removed. Its same-hash payload cannot alias null.
        File.Delete(fixture.CanonicalReceivedPointer);
        var response = await fixture.SeedCanonicalAsync();
        Require(response is { Code: "SeedComplete", Receipt: not null } && fixture.Sidecars().Length == 0,
            "A branch-only inventory skipped the normal canonical completion/receipt path.");
        var known = await fixture.SeedAsync();
        Require(known is { Code: "SeedKnown", Receipt: not null }, "The restored exact branch pair could not reuse its retained copy.");
    }

    private static async Task LegacyReceiptAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        var legacy = Path.Combine(fixture.DirectoryPath, "receipt.json");
        var malformed = Encoding.UTF8.GetBytes("{malformed legacy receipt");
        File.WriteAllBytes(legacy, malformed);
        await RejectAsync(fixture.SeedAsync, "A malformed legacy receipt was silently rebuilt.");
        Require(File.ReadAllBytes(legacy).SequenceEqual(malformed) && fixture.Sidecars().Length == 0,
            "The malformed legacy receipt or a replacement was written.");
        File.Delete(legacy); // Reset only this generated damaged test record.
        var created = await fixture.SeedAsync();
        Require(created.Receipt is not null, "The synthetic legacy receipt basis was absent.");
        var scoped = fixture.Sidecars().Single();
        File.Move(scoped, legacy); // Model an existing app version's selfreceipt.
        var legacyBytes = File.ReadAllBytes(legacy);
        var known = await fixture.SeedAsync();
        Require(known.Receipt == created.Receipt && fixture.Sidecars().Length == 0 && File.ReadAllBytes(legacy).SequenceEqual(legacyBytes),
            "A valid current legacy receipt was overwritten or assigned another identity.");
        fixture.AdvanceRoster(receive: true);
        var renewed = await fixture.SeedAsync();
        Require(renewed.Receipt is not null && fixture.ValidReceipt(renewed.Receipt) && renewed.Receipt.ReceiptId != created.Receipt.ReceiptId &&
            fixture.Sidecars().Length == 1 && File.ReadAllBytes(legacy).SequenceEqual(legacyBytes),
            "A fresh roster did not retain the protected legacy receipt while creating one current receipt.");
    }

    private static async Task ForkAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        var payload = File.ReadAllBytes(fixture.PayloadPath);
        var before = fixture.Pointers();
        var fork = await fixture.SeedSiblingAsync();
        Require(fork is { Code: "SeedChunk", Receipt: null, FileIndex: 0, ChunkOffset: 0 } &&
            fixture.Pointers() == before && File.ReadAllBytes(fixture.PayloadPath).SequenceEqual(payload) && fixture.Sidecars().Length == 0,
            "A different signed sibling was acknowledged as the existing exact copy or changed its published pointer/payload.");
    }

    private static async Task ConsentLockOrderAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        using var worldEntered = new ManualResetEventSlim(false);
        var receiptEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worldWasEntered = false;
        var acquiredWorld = false;
        var blocker = Task.Run(() =>
        {
            lock (fixture.ReceiptMonitor)
            {
                receiptEntered.SetResult(true);
                worldWasEntered = worldEntered.Wait(TimeSpan.FromSeconds(3));
                if (!worldWasEntered) return;
                acquiredWorld = Monitor.TryEnter(fixture.WorldMonitor, TimeSpan.FromSeconds(2));
                if (acquiredWorld) Monitor.Exit(fixture.WorldMonitor);
            }
        });
        await receiptEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var scope = fixture.CheckWorldScopeAsync(() => worldEntered.Set());
        await Task.WhenAll(blocker, scope).WaitAsync(TimeSpan.FromSeconds(8));
        Require(worldWasEntered && acquiredWorld && scope.Result,
            "A world-held existing-copy scope waited for the competing Friend receipt monitor, reversing consent/world lock order.");
        Require(await fixture.SeedAsync() is { Code: "SeedKnown", Receipt: not null },
            "The consent monitor regression left normal exact seed reuse blocked.");
    }

    private static async Task OutboundOnlyAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        fixture.SaveMemberships(sharing: false, targetOnly: true);
        var before = fixture.Pointers();
        await fixture.PullAsync();
        Require(fixture.Sidecars().Length == 1 && !Directory.Exists(fixture.DuplicateVault) && fixture.Pointers() == before &&
            !fixture.RemoteHandler.Operations.Contains(WorldPeerOperations.Chunk),
            "Turning incoming sharing off blocked an otherwise authorized outward-only exact-copy reuse.");
        await RejectAsync(fixture.SeedAsync, "Turning incoming sharing off still allowed an inbound seed.");
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or CryptographicException or JsonException or OperationCanceledException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require([DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static SharedWorldFile Fact(byte[] bytes) => new("world.dat", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));

    private sealed class Fixture : IDisposable
    {
        internal LocalData A { get; }
        internal LocalData B { get; }
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly PairingService pairing;
        private readonly PairingCredential credential;
        private readonly FriendService aFriends, bFriends;
        private WorldPeerService aService, bService;
        private readonly Handler aHandler, bHandler;
        private readonly bool original;
        private readonly SharedWorldPeerRoute aRoute = new("https://192.0.2.181:5131", new string('A', 64));
        private readonly SharedWorldPeerRoute bRoute = new("https://192.0.2.182:5131", new string('B', 64));
        internal SharedWorldPeerRoot Root { get; }
        private SharedWorldRoster roster;
        private readonly WorldPeerSaveDescriptor descriptor;
        internal string DirectoryPath { get; }
        internal string PayloadPath => Path.Combine(DirectoryPath, SharedWorldService.PayloadDirectory, "world.dat");
        internal string DuplicateVault { get; }
        internal LocalData Target => original ? A : B;
        internal WorldPeerService TargetService => original ? aService : bService;
        private WorldPeerService Sender => original ? bService : aService;
        private SharedWorldPeerRoute TargetRoute => original ? aRoute : bRoute;
        internal Handler RemoteHandler => original ? bHandler : aHandler;
        internal string KeyPath => Path.Combine(Target.RootPath, original ? "shared-world-signing-key.protected" : $"shared-world-pc-signing-{credential.DeviceId:N}.protected");
        internal string CanonicalReceivedPointer => Path.Combine(B.RootPath, "received-shared-worlds", credential.DeviceId.ToString("N"), Root.ProfileId.ToString("N"), "latest.json");
        internal object ReceiptMonitor
        {
            get
            {
                var links = (IEnumerable<(Guid Id, FriendLink Link)>)typeof(FriendService).GetField("links", Private)!.GetValue(bFriends)!;
                return typeof(FriendLink).GetField("sharedReceiptSync", Private)!.GetValue(links.Single().Link)!;
            }
        }
        internal object WorldMonitor => typeof(WorldPeerService).GetField("stateSync", Private)!.GetValue(TargetService)!;

        internal Fixture(string parent, string shape)
        {
            original = shape == "Original";
            var folder = Path.Combine(parent, "exact-copy-" + shape + "-" + Guid.NewGuid().ToString("N"));
            A = new(Path.Combine(folder, "owner")); B = new(Path.Combine(folder, "member"));
            var profile = new ServerProfile
            {
                Kind = GameKinds.Fixture,
                WorldId = "SyntheticExactCopy",
                SharedSavesEnabled = true,
                WorldDirectory = Path.Combine(A.RootPath, "unused-world"),
                GamePort = 2456
            };
            Directory.CreateDirectory(profile.WorldDirectory);
            A.SaveSettings(new() { Profiles = [profile] }); B.SaveSettings(new() { Profiles = [] });
            A.SaveProtected("shared-world-signing-key.protected", owner.ExportPkcs8PrivateKey());
            pairing = new(A);
            var invite = pairing.IssueServer(profile.Id, false, false, aRoute.Address, aRoute.CertificateSha256, false);
            credential = pairing.Activate(new(profile.Id, invite.Code, ServerScope: true))!;
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Require(pairing.BindSharedWorldKey(credential.DeviceId, new(nonce, Public(member), Convert.ToBase64String(member.SignData(
                SharedWorldRosterTrust.EnrollmentBasis(credential.DeviceId, nonce, Public(member)), HashAlgorithmName.SHA256)))).Ok,
                "Synthetic member binding failed.");
            var capture = new Capture(A.RootPath);
            var publisher = new SharedWorldService(A, capture);
            roster = publisher.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
            pairing.ConfirmSharedRosterPublished(profile.Id, roster);
            Root = new(roster.OwnerPublicKey, profile.Id, roster.GroupId);
            SaveMemberships();
            var first = publisher.PublishAfterStop(profile, Guid.NewGuid()).Version ?? throw new InvalidOperationException("Synthetic first publication failed.");
            var bytes = File.ReadAllBytes(Path.Combine(capture.Path, "world.dat"));
            if (original)
            {
                var second = publisher.PublishAfterStop(profile, Guid.NewGuid()).Version ?? throw new InvalidOperationException("Synthetic second publication failed.");
                descriptor = new(second);
                DirectoryPath = Path.Combine(A.RootPath, "shared-worlds", profile.Id.ToString("N"), Root.GroupId.ToString("N"), "2");
                DuplicateVault = Path.Combine(A.RootPath, "received-shared-worlds", Guid.Empty.ToString("N"), profile.Id.ToString("N"));
            }
            else
            {
                var received = Path.Combine(B.RootPath, "received-shared-worlds", credential.DeviceId.ToString("N"), profile.Id.ToString("N"));
                WriteCopy(received, first, bytes);
                var copies = new SharedWorldSeparateCopyStore(B);
                var offer = SharedWorldSeparateCopyStore.PrepareSoloOffer(received, roster,
                    new(Root.GroupId, roster.Epoch, roster.Revision, roster.Signature), credential.DeviceId, member,
                    bRoute.Address, bRoute.CertificateSha256, new WorldAuthorityStore(B));
                var branch = copies.Declare(offer, received, member, true);
                if (shape == "Retained") { descriptor = new(first, branch); DirectoryPath = Path.Combine(copies.RetainedRoot(branch), first.VersionHash); }
                else
                {
                    using var hosting = ECDsa.Create();
                    hosting.ImportPkcs8PrivateKey(B.LoadProtected(WorldAuthorityStore.HostingKeyName(profile.Id))!, out _);
                    var second = SharedWorldService.SignVersion(first with
                    {
                        Number = 2,
                        ParentHash = first.VersionHash,
                        BackupId = Guid.NewGuid(),
                        Files = [Fact(bytes)]
                    }, hosting);
                    WriteCopy(copies.PublishedRoot(branch), first, bytes);
                    WriteCopy(copies.PublishedRoot(branch), second, bytes);
                    descriptor = new(second, branch); DirectoryPath = Path.Combine(copies.PublishedRoot(branch), second.VersionHash);
                }
                DuplicateVault = SharedWorldBranchVault.Root(B, branch);
            }
            aFriends = new(A, Offline); bFriends = new(B, Offline);
            aService = NewService(A, aFriends, pairing); bService = NewService(B, bFriends, new(B));
            aHandler = new(aService, Root, aRoute); bHandler = new(bService, Root, bRoute);
            RemoteHandler.Catalog = new(Root, [descriptor]);
        }

        private static HttpClient Offline(string _, IEnumerable<string> __) => throw new HttpRequestException("Synthetic Host is offline.");
        private WorldPeerService NewService(LocalData data, FriendService friends, PairingService pairs) =>
            new(data, new HostManager(data), friends, pairs, Client);
        private HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            var route = endpoint.TrimEnd('/') == aRoute.Address ? aRoute : bRoute;
            Require(endpoint.TrimEnd('/') == route.Address && pins.SequenceEqual([route.CertificateSha256]), "Fake transport changed origin/pin.");
            return new(route == aRoute ? aHandler : bHandler, false)
            { BaseAddress = new Uri(route.Address + "/"), Timeout = TimeSpan.FromSeconds(6) };
        }
        internal void ReopenTarget()
        {
            TargetService.Dispose();
            if (original) { aService = NewService(A, aFriends, pairing); aHandler.Service = aService; }
            else { bService = NewService(B, bFriends, new(B)); bHandler.Service = bService; }
        }
        private object Group(WorldPeerService service) => typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(service, [Root.GroupKey])!;
        internal Task PullAsync() => (Task)typeof(WorldPeerService).GetMethod("PullCatalogAsync", Private)!
            .Invoke(TargetService, [Group(TargetService), original ? bRoute : aRoute, CancellationToken.None])!;
        internal Task<WorldPeerSeedResponse> SeedAsync() => (Task<WorldPeerSeedResponse>)typeof(WorldPeerService).GetMethod("SendSeedAsync", Private)!
            .Invoke(Sender, [Group(Sender), TargetRoute, descriptor, null, null, null, null, CancellationToken.None])!;
        internal Task<WorldPeerSeedResponse> SeedCanonicalAsync() => (Task<WorldPeerSeedResponse>)typeof(WorldPeerService).GetMethod("SendSeedAsync", Private)!
            .Invoke(Sender, [Group(Sender), TargetRoute, new WorldPeerSaveDescriptor(descriptor.Version), null, null, null, null, CancellationToken.None])!;
        internal Task<WorldPeerSeedResponse> SeedSiblingAsync()
        {
            using var hosting = ECDsa.Create();
            hosting.ImportPkcs8PrivateKey(B.LoadProtected(WorldAuthorityStore.HostingKeyName(Root.ProfileId))!, out _);
            var sibling = SharedWorldService.SignVersion(descriptor.Version with { BackupId = Guid.NewGuid() }, hosting);
            Require(sibling.VersionHash != descriptor.Version.VersionHash, "The synthetic sibling did not differ.");
            return (Task<WorldPeerSeedResponse>)typeof(WorldPeerService).GetMethod("SendSeedAsync", Private)!
                .Invoke(Sender, [Group(Sender), TargetRoute, new WorldPeerSaveDescriptor(sibling, descriptor.Branch), null, null, null, null, CancellationToken.None])!;
        }
        internal Task ReuseDirectAsync() => (Task)typeof(WorldPeerService).GetMethod("TryReuseExistingCopyAsync", Private)!
            .Invoke(TargetService, [Group(TargetService), descriptor, CancellationToken.None, null, null, null])!;
        internal Task<bool> CheckWorldScopeAsync(Action entered) => Task.Run(() =>
        {
            lock (WorldMonitor)
            {
                entered();
                return typeof(WorldPeerService).GetMethod("ExistingCopyScopeFor", Private)!
                    .Invoke(TargetService, [Group(TargetService), descriptor, CancellationToken.None]) is not null;
            }
        });
        internal void RevokeSender() => Require(pairing.Revoke(credential.DeviceId).Ok, "The synthetic sender could not be revoked.");
        internal string[] Sidecars() => Directory.EnumerateFiles(DirectoryPath, "self-receipt-*.json").ToArray();
        internal string Pointers() => string.Join('|', new[] { A, B }.SelectMany(data => Directory.EnumerateFiles(data.RootPath, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) is "latest.json" or "branch.json" || Path.GetFileName(path).StartsWith("world-peer-save-observation-", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        internal bool ValidReceipt(SharedWorldReceipt receipt) => receipt.GroupId == Root.GroupId && receipt.ProfileId == Root.ProfileId &&
            receipt.VersionHash == descriptor.Version.VersionHash && receipt.RosterEpoch == roster.Epoch && receipt.RosterRevision == roster.Revision &&
            (original ? receipt.DeviceId == Guid.Empty && SharedWorldReceiptTrust.VerifyPeerOwner(receipt, Root) :
                receipt.DeviceId == credential.DeviceId && SharedWorldReceiptTrust.Verify(receipt, Public(member)));
        internal void SaveMemberships(bool sharing = true, bool targetOnly = false)
        {
            foreach (var data in targetOnly ? new[] { Target } : new[] { A, B })
            {
                var ownerData = data == A;
                var id = ownerData ? Guid.Empty : credential.DeviceId;
                if (!ownerData && !File.Exists(Path.Combine(data.RootPath, $"shared-world-pc-signing-{id:N}.protected")))
                    data.SaveProtected($"shared-world-pc-signing-{id:N}.protected", member.ExportPkcs8PrivateKey());
                new SharedWorldRosterChainStore(data).Append(roster, Root.OwnerPublicKey);
                data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Schema = 1,
                    Groups = new[] { new { Root, Roster = roster, DeviceId = id, Name = "Exact synthetic copy", ReceiveConsented = true,
                        SharingEnabled = sharing, Bootstrap = ownerData ? bRoute : aRoute } }
                }, Json));
                if (ownerData) continue;
                data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
                {
                    DeviceId = id,
                    Endpoint = aRoute.Address,
                    Fingerprint = aRoute.CertificateSha256,
                    Credential = credential.Credential,
                    CredentialExpiresUtc = credential.ExpiresUtc,
                    ConsentedSharedWorldProfiles = [Root.ProfileId],
                    ApprovedSharedWorldGroups = new() { [Root.ProfileId] = Root.GroupId },
                    SharedWorldSigningKeys = new() { [Root.ProfileId] = Root.OwnerPublicKey },
                    SharedRosterFloors = new() { [Root.ProfileId] = new(Root.GroupId, roster.Epoch, roster.Revision, roster.Signature) }
                }, Json));
                data.SaveProtected($"shared-world-roster-{id:N}-{Root.ProfileId:N}.protected", JsonSerializer.SerializeToUtf8Bytes(roster, Json));
            }
        }
        internal void AdvanceRoster(bool receive)
        {
            Require(pairing.SetSharedWorldGrants(credential.DeviceId, Root.ProfileId,
                roster.Members.Single(item => item.DeviceId == credential.DeviceId).Grants with { Receive = receive }).Ok,
                "The synthetic owner's current receiving grant did not change.");
            var members = pairing.SharedRosterMembers(Root.ProfileId).OrderBy(item => item.DeviceId).ToArray();
            var draft = roster with
            {
                Epoch = roster.Epoch + 1,
                Revision = roster.Revision + 1,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(roster),
                OwnerLocalBaselineMembers = members,
                Members = members,
                Signature = ""
            };
            roster = draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            SaveMemberships();
            pairing.ConfirmSharedRosterPublished(Root.ProfileId, roster);
        }
        public void Dispose() { aService.Dispose(); bService.Dispose(); aHandler.Dispose(); bHandler.Dispose(); aFriends.Dispose(); bFriends.Dispose(); A.Dispose(); B.Dispose(); owner.Dispose(); member.Dispose(); }
    }

    private static void WriteCopy(string vault, SharedWorldVersion version, byte[] bytes)
    {
        var directory = Path.Combine(vault, version.VersionHash); Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
        File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(vault, version);
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }
    private sealed class Capture : ISharedWorldCaptureAdapter
    {
        internal string Path { get; }
        internal Capture(string root) { Path = System.IO.Path.Combine(root, "synthetic-completed-backup"); Directory.CreateDirectory(Path); File.WriteAllText(System.IO.Path.Combine(Path, "world.dat"), "exact synthetic immutable completed save"); }
        public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backup) => new(1,
            SharedWorldCaptureKinds.PostStopBackup, backup, DateTimeOffset.UtcNow, [Fact(File.ReadAllBytes(System.IO.Path.Combine(Path, "world.dat")))], Path,
            new(3, profile.Id, profile.Kind, profile.WorldId, "Unknown", "SyntheticFilesOnly", [], [], profile.GamePort));
    }
    internal sealed class Handler(WorldPeerService service, SharedWorldPeerRoot root, SharedWorldPeerRoute route) : HttpMessageHandler
    {
        internal WorldPeerService Service { get; set; } = service;
        internal WorldPeerCatalog? Catalog { get; set; }
        internal List<string> Operations { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/challenge", StringComparison.Ordinal))
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Service.PrepareChallenge(root.GroupKey,
                    WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(bytes, 2 * 1024 * 1024), route.CertificateSha256), options: Json)
                };
            var exchange = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(bytes, 2 * 1024 * 1024);
            Operations.Add(exchange.Request.Operation);
            var response = await Service.ExchangeAsync(root.GroupKey, exchange.Request, exchange.Payload, token, route.CertificateSha256);
            if (Catalog is not null && exchange.Request.Operation == WorldPeerOperations.Catalog) response = JsonSerializer.SerializeToUtf8Bytes(Catalog, Json);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(response) };
        }
    }
}

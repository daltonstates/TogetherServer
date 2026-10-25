using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Fixed fake HTTPS dispatch, signed synthetic saves and disposable protected
// state only. No socket, listener, app, game or process is started.
internal static class WorldPeerSeedingChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent)
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = Guid.NewGuid();
        var folder = Path.Combine(parent, "outbound-seed-" + Guid.NewGuid().ToString("N"));
        using var a = new LocalData(Path.Combine(folder, "returning-owner-a"));
        using var b = new LocalData(Path.Combine(folder, "outbound-only-b"));
        using var c = new LocalData(Path.Combine(folder, "new-host-c"));
        var aEndpoint = "https://192.0.2.91:5131";
        var cEndpoint = "https://192.0.2.93:5131";
        var originalWorld = Path.Combine(a.RootPath, "original-world");
        Directory.CreateDirectory(originalWorld);
        File.WriteAllText(Path.Combine(originalWorld, "world.dat"), "original live world remains untouched");
        a.SaveSettings(new HostSettings
        {
            CompanionEndpoint = aEndpoint,
            CompanionBindAddress = "0.0.0.0",
            CompanionListeningEnabled = true,
            Profiles = [new() { Id = profile, Kind = GameKinds.Fixture, WorldDirectory = originalWorld,
                WorldId = "OutboundSynthetic", SharedSavesEnabled = true }]
        });
        b.SaveSettings(new HostSettings { Profiles = [] });
        c.SaveSettings(new HostSettings { CompanionEndpoint = cEndpoint, CompanionBindAddress = "0.0.0.0", Profiles = [] });
        a.SaveProtected("shared-world-signing-key.protected", owner.ExportPkcs8PrivateKey());
        using var aCertificate = new HostIdentity(a).Ensure(aEndpoint);
        using var cCertificate = new HostIdentity(c).Ensure(cEndpoint);
        var aRoute = new SharedWorldPeerRoute(aEndpoint, HostIdentity.Fingerprint(aCertificate));
        var cRoute = new SharedWorldPeerRoute(cEndpoint, HostIdentity.Fingerprint(cCertificate));
        var pairing = new PairingService(a);
        var invite = pairing.IssueServer(profile, false, false, aEndpoint, aRoute.CertificateSha256, refresh: false);
        var bCredential = pairing.Activate(new(profile, invite.Code, ServerScope: true))!;
        var cCredential = pairing.Activate(new(profile, invite.Code, ServerScope: true))!;
        Bind(pairing, bCredential.DeviceId, bKey, profile);
        Bind(pairing, cCredential.DeviceId, cKey, profile);
        var completed = new CompletedFiles(a.RootPath);
        var publisher = new SharedWorldService(a, completed);
        var roster = publisher.PublishRoster(a.LoadSettings().Profiles.Single(),
            pairing.SharedRosterMembers(profile).OrderBy(item => item.DeviceId).ToArray());
        var root = new SharedWorldPeerRoot(roster.OwnerPublicKey, profile, roster.GroupId);
        Initialize(a, root, roster, Guid.Empty, owner, cRoute, null);
        Initialize(b, root, roster, bCredential.DeviceId, bKey, aRoute, bCredential);
        Initialize(c, root, roster, cCredential.DeviceId, cKey, aRoute, cCredential);
        pairing.ConfirmSharedRosterPublished(profile, roster);
        var bytes = new byte[SharedWorldService.ChunkBytes * 2 + 17];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 241);
        var predecessorBytes = Encoding.UTF8.GetBytes("A's first completed session");
        completed.Set(predecessorBytes);
        var predecessorPublication = publisher.PublishAfterStop(a.LoadSettings().Profiles.Single(), Guid.NewGuid());
        Require(predecessorPublication.Ok && predecessorPublication.Version is { Number: 1 },
            "the original owner's first signed completed-file publication failed");
        var predecessor = predecessorPublication.Version!;
        completed.Set(bytes);
        var originalPublication = publisher.PublishAfterStop(a.LoadSettings().Profiles.Single(), Guid.NewGuid());
        Require(originalPublication.Ok && originalPublication.Version is { Number: 2 } &&
            originalPublication.Version.ParentHash == predecessor.VersionHash,
            "the original owner's second signed completed-file publication did not continue its first");
        var first = originalPublication.Version!;
        WriteCopy(Vault(b, bCredential.DeviceId, profile), predecessor, predecessorBytes);
        WriteCopy(Vault(b, bCredential.DeviceId, profile), first, bytes);
        var cStore = Store(c, root, roster);
        var bStore = Store(b, root, roster);
        var aStore = Store(a, root, roster);
        HttpClient Offline(string _, IEnumerable<string> __) => throw new HttpRequestException("Synthetic peer is offline.");
        using var aFriends = new FriendService(a, Offline);
        using var bFriends = new FriendService(b, Offline);
        using var cFriends = new FriendService(c, Offline);
        using var cService = new WorldPeerService(c, new HostManager(c), cFriends, new PairingService(c), Offline,
            (_, _) => Task.CompletedTask);
        await cService.SyncNowAsync(default);
        Require(!c.LoadSettings().CompanionListeningEnabled && cStore.Project().Peers.Single().Routes.Contains(cRoute),
            "C did not advertise its signed configured contact while incoming connections remained off");
        _ = bStore.ImportPage(cStore.ReadPage());
        _ = aStore.ImportPage(cStore.ReadPage());
        using (var originalOwnerService = new WorldPeerService(a, new HostManager(a), aFriends, pairing, Offline,
            (_, _) => Task.CompletedTask))
            await originalOwnerService.SyncNowAsync(default);
        // All three had learned the owner's signed canonical head and contact
        // before its service stopped. The returned service must retain that
        // self-history while accepting a forwarded continuation from B.
        _ = bStore.ImportPage(aStore.ReadPage());
        _ = cStore.ImportPage(aStore.ReadPage());
        using var handler = new Handler(cService, root, cRoute);
        Handler? returnedOwnerHandler = null;
        var sourceOnline = true;
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            if (endpoint.TrimEnd('/') == aEndpoint && returnedOwnerHandler is not null)
            {
                Require(pins.SequenceEqual([aRoute.CertificateSha256]), "outbound seeding changed its pinned returning owner");
                return new(returnedOwnerHandler, disposeHandler: false) { BaseAddress = new Uri(aEndpoint + "/") };
            }
            if (endpoint.TrimEnd('/') != cEndpoint) return Offline(endpoint, pins);
            Require(pins.SequenceEqual([cRoute.CertificateSha256]), "outbound seeding changed its pinned recipient");
            if (!sourceOnline || !c.LoadSettings().CompanionListeningEnabled) return Offline(endpoint, pins);
            return new(handler, disposeHandler: false) { BaseAddress = new Uri(cEndpoint + "/") };
        }
        using var bService = new WorldPeerService(b, new HostManager(b), bFriends, new PairingService(b), Client,
            (_, _) => Task.CompletedTask);
        await bService.SyncNowAsync(default);
        Require(FriendLink.ReadReceivedLatest(Vault(c, cCredential.DeviceId, profile)) is null,
            "a configured address enabled incoming connections without local confirmation");
        var enabled = c.LoadSettings(); enabled.CompanionListeningEnabled = true; c.SaveSettings(enabled);
        await bService.SyncNowAsync(default);
        var cVault = Vault(c, cCredential.DeviceId, profile);
        Require(FriendLink.ReadReceivedLatest(cVault)?.VersionHash == first.VersionHash &&
            handler.Seeds >= 4 && !b.LoadSettings().CompanionListeningEnabled &&
            File.ReadAllBytes(Path.Combine(cVault, first.VersionHash, SharedWorldService.PayloadDirectory, "world.dat")).SequenceEqual(bytes),
            "outbound-only B could not automatically seed its complete verified copy to explicitly listening C");
        var cLatest = await cService.ResolveLatestRecoverySourceAsync(root, default);
        Require(cLatest.Ok && cLatest.Source?.Descriptor.Version.VersionHash == first.VersionHash &&
            cLatest.ObservationHash == cService.LatestObservationHash(root), "C could not prepare the seeded latest copy without a history choice");
        var floor = new SharedRosterFloor(root.GroupId, roster.Epoch, roster.Revision, roster.Signature);
        var offer = SharedWorldSeparateCopyStore.PrepareSoloOffer(cLatest.Source!.Vault, roster, floor,
            cCredential.DeviceId, cKey, cEndpoint, cRoute.CertificateSha256, new WorldAuthorityStore(c));
        var cCopies = new SharedWorldSeparateCopyStore(c);
        var cBranch = cCopies.Declare(offer, cLatest.Source.Vault, cKey, true);
        using var hostingKey = ECDsa.Create();
        hostingKey.ImportPkcs8PrivateKey(c.LoadProtected(WorldAuthorityStore.HostingKeyName(profile))!, out _);
        var changed = Encoding.UTF8.GetBytes("C's completed next session save");
        var next = SharedWorldService.SignVersion(first with
        {
            Number = first.Number + 1,
            ParentHash = first.VersionHash,
            BackupId = Guid.NewGuid(),
            CreatedUtc = first.CreatedUtc.AddDays(-1),
            Files = [Fact(changed)]
        }, hostingKey);
        WriteCopy(cCopies.PublishedRoot(cBranch), first, bytes);
        WriteCopy(cCopies.PublishedRoot(cBranch), next, changed);
        await cService.SyncNowAsync(default);
        await bService.SyncNowAsync(default);
        Require(FriendLink.ReadReceivedLatest(SharedWorldBranchVault.Root(b, cBranch))?.VersionHash == next.VersionHash,
            "B did not retain C's next completed continuation after supplying C's initial save");
        sourceOnline = false;
        var aManager = new HostManager(a);
        using var aService = new WorldPeerService(a, aManager, aFriends, pairing, Offline,
            (_, _) => Task.CompletedTask);
        using var ownerHandler = new Handler(aService, root, aRoute);
        returnedOwnerHandler = ownerHandler;
        await bService.SyncNowAsync(default);
        Require(FriendLink.ReadReceivedLatest(SharedWorldBranchVault.Root(a, cBranch))?.VersionHash == next.VersionHash,
            "listener-less B did not seed the descendant to returned A while producer C was unavailable: " +
            $"seeds={ownerHandler.Seeds}, exchangeFailure={ownerHandler.LastFailure?.Message}");
        var ownerLatest = await aService.ResolveLatestRecoverySourceAsync(root, default);
        var ownerFence = aService.CanonicalStartBlock(root, first.VersionHash);
        Require(ownerLatest.Ok && ownerLatest.Source?.Descriptor.Version.VersionHash == next.VersionHash &&
            ownerFence?.Code == "NewerSharedSaveAvailable",
            $"returning A catchup failed: result={ownerLatest.Code}, expectedSave={ownerLatest.Source?.Descriptor.Version.VersionHash == next.VersionHash}, " +
            $"startFence={ownerFence?.Code}, knownBranches={aService.Views().Single().State.Branches.Count}");
        Require(publisher.Status(a.LoadSettings().Profiles.Single()).Latest?.VersionHash == first.VersionHash,
            "receiving a descendant changed the original owner's signed publication");
        var ownerBranch = await aService.PrepareOwnerRecoveryAsync(root.GroupKey, ownerLatest.Source!.BranchHash,
            true, default, ownerLatest.Source);
        Require(ownerBranch.Ok && ownerBranch.Branch?.SourceBranch?.BranchHash == cBranch.BranchHash &&
            ownerBranch.Branch.Offer.Version.VersionHash == next.VersionHash &&
            File.ReadAllText(Path.Combine(originalWorld, "world.dat")) == "original live world remains untouched",
            "returning A could not prepare C's exact continuation in fresh retained storage without changing its original world");
        var ownerRetained = new SharedWorldSeparateCopyStore(a).RetainedRoot(ownerBranch.Branch!);
        Require(FriendLink.ReadReceivedManifest(ownerRetained, predecessor.Number, predecessor.VersionHash) is null &&
            (await aManager.PeerPublishedVersionAsync(root, predecessor.VersionHash, predecessor.Number))?.VersionHash == predecessor.VersionHash,
            "the regression needs an owner-base vault without the older exact manifest still retained in A's canonical publication");
        Require((await aService.AnnounceLocalHostingAsync(root, default)).Ok,
            "the returning owner could not announce its explicitly enabled local hosting contact");
        await aService.WaitForSyncAsync();
        // An incoming transfer can stage metadata for this same new lane
        // before it has either a complete pointer or promoted branch proof.
        var incomingOwnerVault = SharedWorldBranchVault.Root(a, ownerBranch.Branch!);
        Directory.CreateDirectory(incomingOwnerVault);
        FriendLink.KeepSignedManifest(incomingOwnerVault, next);
        var finalOwnerSource = await aService.ResolveLatestRecoverySourceAsync(root, default);
        Require(finalOwnerSource.Ok && finalOwnerSource.Source?.Descriptor.Version.VersionHash == next.VersionHash &&
            finalOwnerSource.Source.BranchHash == ownerBranch.Branch!.BranchHash &&
            finalOwnerSource.ObservationHash == aService.LatestObservationHash(root),
            $"returning owner's final latest recheck failed after recording its continuation: result={finalOwnerSource.Code}, " +
            $"expectedSave={finalOwnerSource.Source?.Descriptor.Version.VersionHash == next.VersionHash}");
        finalOwnerSource.Source!.Verify(a, root);
        Require(FriendLink.ReadReceivedManifest(finalOwnerSource.Source.Vault, predecessor.Number,
            predecessor.VersionHash)?.VersionHash == predecessor.VersionHash,
            "the exact owner source did not retain the verified older canonical manifest from elsewhere on this PC");
        Require(FriendLink.ReadReceivedLatest(incomingOwnerVault) is null,
            "preparing the owner source silently promoted the unfinished incoming copy");
        var corruptPointer = Path.Combine(incomingOwnerVault, "latest.json");
        File.WriteAllBytes(corruptPointer, JsonSerializer.SerializeToUtf8Bytes(next with { Signature = "" }, Json));
        try
        {
            var corruptLatest = await aService.ResolveLatestRecoverySourceAsync(root, default);
            Require(!corruptLatest.Ok && corruptLatest.Code == "SharedSaveHistoryNeedsReview",
                "a present corrupt incoming pointer was ignored in favor of another complete save");
        }
        finally { File.Delete(corruptPointer); }

        // An unchanged pointer is not a fresh verification claim after damage.
        sourceOnline = true;
        File.WriteAllText(Path.Combine(cVault, first.VersionHash, SharedWorldService.PayloadDirectory, "world.dat"), "damaged");
        var proof = cService.Challenge(root.GroupKey, bCredential.DeviceId, Public(bKey), cRoute.CertificateSha256);
        var seedBytes = JsonSerializer.SerializeToUtf8Bytes(new WorldPeerSeedRequest(new(first)), Json);
        var request = WorldPeerProtocol.Sign(proof, WorldPeerOperations.Seed, bKey, versionHash: first.VersionHash, payload: seedBytes);
        await Refuses(() => cService.ExchangeAsync(root.GroupKey, request, seedBytes, default, cRoute.CertificateSha256),
            "a damaged same-hash receiver reported SeedKnown without rechecking its bytes");
        File.WriteAllBytes(Path.Combine(cVault, first.VersionHash, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
        var newerBytes = bytes.ToArray(); newerBytes[^1] ^= 0x4F;
        var newerOriginal = SharedWorldService.SignVersion(first with
        {
            Number = first.Number + 1,
            ParentHash = first.VersionHash,
            BackupId = Guid.NewGuid(),
            Files = [Fact(newerBytes)]
        }, owner);
        WriteCopy(Vault(b, bCredential.DeviceId, profile), newerOriginal, newerBytes);
        var withdrew = false;
        handler.BeforeExchange = async envelope =>
        {
            if (withdrew || envelope.Request.Operation != WorldPeerOperations.Seed ||
                envelope.Request.VersionHash != newerOriginal.VersionHash ||
                envelope.Request.ChunkOffset != SharedWorldService.ChunkBytes * 2L) return;
            withdrew = true;
            Require((await cService.SetReceivingAsync(root.GroupKey, false, default)).Ok,
                "synthetic receiver could not withdraw its local save consent");
        };
        await bService.SyncNowAsync(default);
        Require(withdrew && FriendLink.ReadReceivedLatest(cVault)?.VersionHash == first.VersionHash,
            "withdrawal before the final seeded chunk still promoted the new save");
        Require(!b.LoadSettings().CompanionListeningEnabled && a.LoadSettings().CompanionListeningEnabled,
            "automatic storage, owner catchup or forwarding changed the explicit incoming-listener choices");
        await WorldPeerCompletedSaveDistributionChecks.RunAsync(parent);
    }

    private static async Task Refuses(Func<Task<byte[]>> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Bind(PairingService pairing, Guid id, ECDsa key, Guid profile)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Require(pairing.BindSharedWorldKey(id, new(nonce, Public(key), Convert.ToBase64String(key.SignData(
            SharedWorldRosterTrust.EnrollmentBasis(id, nonce, Public(key)), HashAlgorithmName.SHA256)))).Ok,
            "synthetic member enrollment failed");
        Require(pairing.SharedRosterMembers(profile).Single(member => member.DeviceId == id).Grants is
        { Receive: true, EligibleHost: true, Reshare: true }, "the global shared hosting policy did not enroll this approved PC");
    }
    private static void Initialize(LocalData data, SharedWorldPeerRoot root, SharedWorldRoster roster,
        Guid id, ECDsa key, SharedWorldPeerRoute bootstrap, PairingCredential? credential)
    {
        if (id != Guid.Empty) data.SaveProtected($"shared-world-pc-signing-{id:N}.protected", key.ExportPkcs8PrivateKey());
        new SharedWorldRosterChainStore(data).Append(roster, root.OwnerPublicKey);
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Groups = new[] { new { Root = root, Roster = roster, DeviceId = id, Name = "Synthetic seed world",
                ReceiveConsented = true, SharingEnabled = true, Bootstrap = bootstrap } }
        }, Json));
        if (credential is null) return;
        data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            DisplayName = "Synthetic A",
            DeviceId = id,
            Endpoint = bootstrap.Address,
            Fingerprint = bootstrap.CertificateSha256,
            Credential = credential.Credential,
            CredentialExpiresUtc = credential.ExpiresUtc,
            ConsentedSharedWorldProfiles = [root.ProfileId],
            ApprovedSharedWorldGroups = new() { [root.ProfileId] = root.GroupId },
            SharedWorldSigningKeys = new() { [root.ProfileId] = root.OwnerPublicKey },
            SharedRosterFloors = new() { [root.ProfileId] = new(root.GroupId, roster.Epoch, roster.Revision, roster.Signature) }
        }, Json));
        data.SaveProtected($"shared-world-roster-{id:N}-{root.ProfileId:N}.protected", JsonSerializer.SerializeToUtf8Bytes(roster, Json));
    }
    private static SharedWorldPeerStore Store(LocalData data, SharedWorldPeerRoot root, SharedWorldRoster roster) =>
        new(data, root, new SharedWorldPeerTrust(root, (id, key) => SharedWorldPeerTrust.AccessFromRoster(root, roster, id, key)));
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static SharedWorldFile Fact(byte[] bytes) => new("world.dat", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    private static string Vault(LocalData data, Guid id, Guid profile) => Path.Combine(data.RootPath, "received-shared-worlds", id.ToString("N"), profile.ToString("N"));
    private static void WriteCopy(string vault, SharedWorldVersion version, byte[] bytes)
    {
        var directory = Path.Combine(vault, version.VersionHash);
        Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
        File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(vault, version);
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private sealed class CompletedFiles(string root) : ISharedWorldCaptureAdapter
    {
        private readonly string directory = Path.Combine(root, "completed-synthetic-backup");
        internal void Set(byte[] bytes)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "world.dat"), bytes);
        }
        public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId)
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, "world.dat"));
            return new(1, SharedWorldCaptureKinds.PostStopBackup, backupId, DateTimeOffset.UtcNow,
                [Fact(bytes)], directory,
                new(3, profile.Id, profile.Kind, profile.WorldId, "Unknown", "SyntheticFilesOnly", [], [], profile.GamePort));
        }
    }
    private sealed class Handler(WorldPeerService service, SharedWorldPeerRoot root, SharedWorldPeerRoute route) : HttpMessageHandler
    {
        internal int Seeds { get; private set; }
        internal Exception? LastFailure { get; private set; }
        internal Func<WorldPeerExchangeRequest, Task>? BeforeExchange { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/challenge", StringComparison.Ordinal))
            {
                var hello = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(bytes, 2 * 1024 * 1024);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(service.PrepareChallenge(root.GroupKey, hello, route.CertificateSha256), options: SharedWorldPeerTrust.Json) };
            }
            var exchange = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(bytes, 2 * 1024 * 1024);
            if (exchange.Request.Operation == WorldPeerOperations.Seed) Seeds++;
            if (BeforeExchange is not null) await BeforeExchange(exchange);
            try
            {
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(await service.ExchangeAsync(root.GroupKey,
                    exchange.Request, exchange.Payload, token, route.CertificateSha256))
                };
            }
            catch (Exception error)
            {
                LastFailure = error;
                throw;
            }
        }
    }
}

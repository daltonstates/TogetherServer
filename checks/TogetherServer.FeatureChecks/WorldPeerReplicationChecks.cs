using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Actual peer service orchestration over a fake HTTPS handler. All signatures,
// protected state and payloads are disposable; no sockets/processes/game run.
internal static class WorldPeerReplicationChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string OwnerAddress = "https://192.0.2.80:5131";

    internal static async Task RunAsync(string rootPath)
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var dKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var eKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bId = Guid.NewGuid(); var cId = Guid.NewGuid(); var dId = Guid.NewGuid(); var eId = Guid.NewGuid();
        var source = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var draft = new SharedWorldRoster(3, source.GroupId, source.ProfileId, 1, 1, true, source.OwnerPublicKey,
            [new(bId, Public(bKey), new(Receive: true, EligibleHost: true, Reshare: true), false),
             new(cId, Public(cKey), new(Receive: true, EligibleHost: true), false),
             new(dId, Public(dKey), new(), false),
             new(eId, Public(eKey), new(Receive: true, EligibleHost: true), false)], "",
            SignerDeviceId: Guid.Empty, SignerPublicKey: source.OwnerPublicKey);
        var roster = SignRoster(draft, owner);
        var prefix = Path.Combine(rootPath, "peer-replication-" + Guid.NewGuid().ToString("N"));
        using var bData = new LocalData(Path.Combine(prefix, "resharing-b"));
        using var cData = new LocalData(Path.Combine(prefix, "receiving-c"));
        using var dData = new LocalData(Path.Combine(prefix, "metadata-d"));
        using var eData = new LocalData(Path.Combine(prefix, "withdrawn-e"));
        var bEndpoint = "https://192.0.2.81:5131";
        bData.SaveSettings(new HostSettings { CompanionEndpoint = bEndpoint, CompanionBindAddress = "0.0.0.0", CompanionListeningEnabled = true, Profiles = [] });
        cData.SaveSettings(new HostSettings { CompanionEndpoint = "https://127.0.0.1:5142", CompanionPort = 5142, Profiles = [] });
        dData.SaveSettings(new HostSettings { Profiles = [] });
        eData.SaveSettings(new HostSettings { Profiles = [] });
        using var bCertificate = new HostIdentity(bData).Ensure(bEndpoint);
        var bRoute = new SharedWorldPeerRoute(bEndpoint, HostIdentity.Fingerprint(bCertificate));
        var ownerRoute = new SharedWorldPeerRoute(OwnerAddress, new string('F', 64));
        Initialize(bData, source, roster, bId, bKey, consent: true, sharing: true, ownerRoute);
        Initialize(cData, source, roster, cId, cKey, consent: true, sharing: false, ownerRoute);
        Initialize(dData, source, roster, dId, dKey, consent: false, sharing: false, bRoute);
        Initialize(eData, source, roster, eId, eKey, consent: true, sharing: false, ownerRoute);
        SaveOwnerConnection(bData, source, roster, bId, true, ownerRoute);
        SaveOwnerConnection(cData, source, roster, cId, true, ownerRoute);
        SaveOwnerConnection(eData, source, roster, eId, true, ownerRoute);
        var bytes = new byte[SharedWorldService.ChunkBytes * 3 + 57];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
        var first = Version(source, owner, bytes);
        var bVault = Vault(bData, bId, source.ProfileId);
        WriteCopy(bVault, first, bytes);
        WriteCopy(Vault(eData, eId, source.ProfileId), first, bytes);
        var bStore = PeerStore(bData, source, roster);
        var advertised = bStore.Publish(bId, bKey, [bRoute], [], [], [first.VersionHash]);
        Require(PeerStore(cData, source, roster).Import(advertised).Changed &&
            PeerStore(eData, source, roster).Import(advertised).Changed,
            "the preapproved peers could not cache B's exact signed route");

        var ownerAttempts = 0;
        HttpClient Offline(string _, IEnumerable<string> __)
        { ownerAttempts++; throw new HttpRequestException("The synthetic original A route is unavailable."); }
        using var bFriends = new FriendService(bData, Offline);
        using var cFriends = new FriendService(cData, Offline);
        using var dFriends = new FriendService(dData, Offline);
        using var eFriends = new FriendService(eData, Offline);
        using var bService = new WorldPeerService(bData, new HostManager(bData), bFriends,
            new PairingService(bData), Offline);
        using var handler = new PeerHandler(bService, source, bRoute);
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            if (endpoint.TrimEnd('/') == OwnerAddress) return Offline(endpoint, pins);
            Require(endpoint.TrimEnd('/') == bRoute.Address && pins.SequenceEqual([bRoute.CertificateSha256]),
                "replication attempted an unapproved origin or pin");
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(bRoute.Address + "/") };
        }
        using var cService = new WorldPeerService(cData, new HostManager(cData), cFriends,
            new PairingService(cData), Client);
        using var dService = new WorldPeerService(dData, new HostManager(dData), dFriends,
            new PairingService(dData), Client);
        using var eService = new WorldPeerService(eData, new HostManager(eData), eFriends,
            new PairingService(eData), Client);

        handler.DropNextReceipt = true;
        await cService.SyncNowAsync(default);
        var cVault = Vault(cData, cId, source.ProfileId);
        Require(FriendLink.ReadReceivedLatest(cVault)?.VersionHash == first.VersionHash &&
            File.ReadAllBytes(Path.Combine(cVault, first.VersionHash, SharedWorldService.PayloadDirectory, "world.dat")).SequenceEqual(bytes),
            "C did not receive the exact complete owner-signed copy through B while A was unavailable");
        Require(handler.Requests.Any(item => item.Device == cId && item.Operation == WorldPeerOperations.Metadata) &&
            handler.Requests.Any(item => item.Device == cId && item.Operation == WorldPeerOperations.Catalog) &&
            handler.Requests.Count(item => item.Device == cId && item.Operation == WorldPeerOperations.Chunk && item.Version == first.VersionHash) == 4 &&
            handler.Requests.Any(item => item.Device == cId && item.Operation == WorldPeerOperations.Receipt),
            "actual service sync did not perform signed discovery, catalog, multi-chunk transfer and receipt");
        Require(!Receipts(bData, source).Any(item => item.DeviceId == cId),
            "the synthetic dropped receipt unexpectedly reached its serving peer");
        var droppedAttempts = handler.Requests.Count(item => item.Operation == WorldPeerOperations.Receipt);
        await cService.SyncNowAsync(default);
        Require(handler.Requests.Count(item => item.Operation == WorldPeerOperations.Receipt) == droppedAttempts,
            "a failed receipt delivery ignored its retry backoff on the next sync");
        var retainedReceiptPath = Path.Combine(cVault, first.VersionHash, "receipt.json");
        var retainedReceiptBytes = File.ReadAllBytes(retainedReceiptPath);
        var retainedReceipt = SharedWorldReceiptTrust.Parse(retainedReceiptBytes)!;
        var beforeRetryChunks = handler.Requests.Count(item => item.Operation == WorldPeerOperations.Chunk);
        using (var resumed = RestartReceiver())
        {
            await resumed.SyncNowAsync(default);
            Require(Receipts(bData, source).Single(item => item.DeviceId == cId).ReceiptId == retainedReceipt.ReceiptId &&
                handler.Requests.Count(item => item.Operation == WorldPeerOperations.Chunk) == beforeRetryChunks,
                "a restarted receiver did not acknowledge its already verified copy using the same signed receipt without redownloading");
            var acknowledged = handler.Requests.Count(item => item.Operation == WorldPeerOperations.Receipt);
            await resumed.SyncNowAsync(default);
            Require(handler.Requests.Count(item => item.Operation == WorldPeerOperations.Receipt) == acknowledged,
                "an acknowledged unchanged catalog caused another receipt on every sync");
        }
        // A retry cannot turn a metadata pointer, forged receipt, or subsequently
        // damaged payload into a new copy acknowledgement.
        var beforeInvalid = handler.Requests.Count(item => item.Operation == WorldPeerOperations.Receipt);
        File.WriteAllBytes(retainedReceiptPath, JsonSerializer.SerializeToUtf8Bytes(retainedReceipt with { DeviceId = eId }, Json));
        using (var invalid = RestartReceiver()) await invalid.SyncNowAsync(default);
        Require(handler.Requests.Count(item => item.Operation == WorldPeerOperations.Receipt) == beforeInvalid,
            "a receipt for another local identity was replayed");
        File.Delete(retainedReceiptPath);
        using (var missing = RestartReceiver()) await missing.SyncNowAsync(default);
        Require(handler.Requests.Count(item => item.Operation == WorldPeerOperations.Receipt) == beforeInvalid && !File.Exists(retainedReceiptPath),
            "an unchanged pointer fabricated a missing receipt");
        File.WriteAllBytes(retainedReceiptPath, retainedReceiptBytes);
        var retainedPayloadPath = Path.Combine(cVault, first.VersionHash, SharedWorldService.PayloadDirectory, "world.dat");
        var damaged = bytes.ToArray(); damaged[0] ^= 0xFF;
        File.WriteAllBytes(retainedPayloadPath, damaged);
        using (var corrupt = RestartReceiver()) await corrupt.SyncNowAsync(default);
        Require(handler.Requests.Count(item => item.Operation == WorldPeerOperations.Receipt) == beforeInvalid,
            "an authentic old receipt acknowledged a now-corrupted retained payload");
        File.WriteAllBytes(retainedPayloadPath, bytes);
        var receipt = Receipts(bData, source).Single(item => item.DeviceId == cId && item.VersionHash == first.VersionHash);
        Require(SharedWorldReceiptTrust.Verify(receipt, Public(cKey)) && receipt.GroupId == source.GroupId &&
            receipt.ProfileId == source.ProfileId && receipt.RosterRevision == roster.Revision,
            "B did not persist C's exact authorized signed completion receipt");
        WorldPeerService RestartReceiver() => new(cData, new HostManager(cData), cFriends, new PairingService(cData), Client);
        var prepared = await cService.PrepareSoloRecoveryOfferAsync(source.ProfileId, null, default);
        Require(prepared.Ok && prepared.Offer is { } offer && SharedWorldElection.VerifyOffer(offer) &&
            offer.Version.VersionHash == first.VersionHash && offer.Proposal.ProposerDeviceId == cId &&
            new WorldAuthorityStore(cData).Read(source.ProfileId).Count == 0 && ownerAttempts > 0,
            "C could not explicitly prepare its verified old copy for solo recovery with A offline");
        Require(await cService.MayBrowseRecoverySourceAsync(source.ProfileId, GameKinds.Fixture, default) &&
            !await cService.MayBrowseRecoverySourceAsync(source.ProfileId, GameKinds.Valheim, default) &&
            !await cService.MayBrowseRecoverySourceAsync(Guid.NewGuid(), GameKinds.Fixture, default),
            "offline recovery setup browsing lost its signed source-world scope or accepted a different world/game");

        await dService.SyncNowAsync(default);
        var dView = dService.Views().Single();
        Require(dView.State.Peers.Any(peer => peer.DeviceId == bId && peer.Routes.Contains(bRoute)) &&
            !dView.CanReceive && handler.Requests.Where(item => item.Device == dId)
                .All(item => item.Operation == WorldPeerOperations.Metadata) &&
            !Directory.Exists(Vault(dData, dId, source.ProfileId)),
            "an enrolled metadata-only D failed route discovery or requested save catalog/chunks");
        Require(!await dService.MayBrowseRecoverySourceAsync(source.ProfileId, GameKinds.Fixture, default),
            "metadata following alone authorized recovery setup browsing");

        // C misses an entire save while offline. Unknown intermediate metadata
        // must be fetched before classifying version 3 as a competing history.
        var intermediate = SharedWorldService.SignVersion(first with
        {
            Number = 2,
            ParentHash = first.VersionHash,
            BackupId = Guid.NewGuid()
        }, owner);
        WriteCopy(bVault, intermediate, bytes);
        var caughtUpBytes = bytes.ToArray(); caughtUpBytes[0] ^= 0x31;
        var caughtUp = SharedWorldService.SignVersion(intermediate with
        {
            Number = 3,
            ParentHash = intermediate.VersionHash,
            BackupId = Guid.NewGuid(),
            Files = [Fact(caughtUpBytes)]
        }, owner);
        WriteCopy(bVault, caughtUp, caughtUpBytes);
        await cService.SyncNowAsync(default);
        Require(FriendLink.ReadReceivedLatest(cVault)?.VersionHash == caughtUp.VersionHash &&
            FriendLink.ReadReceivedManifest(cVault, intermediate.Number, intermediate.VersionHash) is not null &&
            File.ReadAllBytes(Path.Combine(cVault, caughtUp.VersionHash, SharedWorldService.PayloadDirectory, "world.dat")).SequenceEqual(caughtUpBytes) &&
            Receipts(bData, source).Any(item => item.DeviceId == cId && item.VersionHash == caughtUp.VersionHash),
            "a returning receiver retained its old head after verifying a missed descendant through the peer");
        var freshRecovery = await cService.PrepareSoloRecoveryOfferAsync(source.ProfileId, null, default);
        Require(freshRecovery.Ok && freshRecovery.Offer?.Version.VersionHash == caughtUp.VersionHash,
            "default solo recovery did not use the newest complete verified descendant after catchup");

        // A retained competing save already copied on an earlier pass must not
        // starve another catalog entry. Both remain separate from the head.
        var forkHashes = new List<string>();
        for (var forkIndex = 0; forkIndex < 2; forkIndex++)
        {
            var forkBytes = bytes.ToArray(); forkBytes[0] ^= (byte)(0x51 + forkIndex);
            var fork = SharedWorldService.SignVersion(first with
            {
                Number = 2,
                ParentHash = first.VersionHash,
                BackupId = Guid.NewGuid(),
                Files = [Fact(forkBytes)]
            }, owner);
            forkHashes.Add(fork.VersionHash);
            var forkVault = Path.Combine(bData.RootPath, "received-shared-histories", source.GroupKey, fork.VersionHash);
            WriteCopy(forkVault, first, bytes); WriteCopy(forkVault, fork, forkBytes);
        }
        await cService.SyncNowAsync(default);
        await cService.SyncNowAsync(default);
        Require(FriendLink.ReadReceivedLatest(cVault)?.VersionHash == caughtUp.VersionHash &&
            forkHashes.All(hash => FriendLink.ReadReceivedLatest(Path.Combine(cData.RootPath,
                "received-shared-histories", source.GroupKey, hash))?.VersionHash == hash),
            "an already retained fork starved later catalog entries or silently replaced the current recovery head");

        // B learns a newer owner-signed removal. C's old local roster cannot
        // obtain a payload nonce or replace its retained verified version.
        var newerDraft = roster with
        {
            Epoch = 2,
            Revision = 2,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(roster),
            Members = roster.Members.Select(member => member.DeviceId == cId ? member with { Revoked = true } : member).ToArray(),
            Signature = ""
        };
        newerDraft = newerDraft with { OwnerLocalBaselineMembers = newerDraft.Members };
        var newer = SignRoster(newerDraft, owner);
        new SharedWorldRosterChainStore(bData).Append(newer, source.OwnerPublicKey);
        SaveMembership(bData, source, newer, bId, true, true, ownerRoute);
        var changedBytes = bytes.ToArray(); changedBytes[^1] ^= 0x7F;
        var second = SharedWorldService.SignVersion(caughtUp with
        {
            Number = 4,
            ParentHash = caughtUp.VersionHash,
            BackupId = Guid.NewGuid(),
            Files = [Fact(changedBytes)]
        }, owner);
        WriteCopy(bVault, second, changedBytes);
        await bService.SyncNowAsync(default);
        var beforeRevoked = handler.Requests.Count(item => item.Device == cId && item.Operation == WorldPeerOperations.Chunk);
        await cService.SyncNowAsync(default);
        Require(FriendLink.ReadReceivedLatest(cVault)?.VersionHash == caughtUp.VersionHash &&
            handler.Requests.Count(item => item.Device == cId && item.Operation == WorldPeerOperations.Chunk) == beforeRevoked &&
            !Receipts(bData, source).Any(item => item.DeviceId == cId && item.VersionHash == second.VersionHash),
            "a revoked peer used its older roster to copy or promote a new save");
        // A peer's revision acknowledgment alone is not owner-signed removal
        // evidence. Once C actually learns that signed roster, local access must
        // close immediately, without waiting for its next registry refresh.
        new SharedWorldRosterChainStore(cData).Append(newer, source.OwnerPublicKey);
        Require(!await cService.MayBrowseRecoverySourceAsync(source.ProfileId, GameKinds.Fixture, default),
            "known signed revocation left recovery setup browsing authorized");

        // Consent is withdrawn after the final authenticated response was read,
        // before its bytes can be committed as a complete new local version.
        var withdrawn = false;
        handler.AfterChunk = async request =>
        {
            if (withdrawn || request.Challenge.DeviceId != eId || request.VersionHash != second.VersionHash ||
                request.ChunkOffset != SharedWorldService.ChunkBytes * 3L) return;
            withdrawn = true;
            Require((await eFriends.SetSharedWorldConsentAsync(source.ProfileId, false)).Ok,
                "the actual saved source connection did not withdraw receive consent");
        };
        await eService.SyncNowAsync(default);
        Require(withdrawn && FriendLink.ReadReceivedLatest(Vault(eData, eId, source.ProfileId))?.VersionHash == first.VersionHash &&
            !Receipts(bData, source).Any(item => item.DeviceId == eId && item.VersionHash == second.VersionHash),
            $"withdrawal after a chunk response failed: callback={withdrawn}, " +
            $"retained={FriendLink.ReadReceivedLatest(Vault(eData, eId, source.ProfileId))?.VersionHash}, " +
            $"newReceipt={Receipts(bData, source).Any(item => item.DeviceId == eId && item.VersionHash == second.VersionHash)}");
        var eChunks = handler.Requests.Count(item => item.Device == eId && item.Operation == WorldPeerOperations.Chunk);
        await eService.SyncNowAsync(default);
        Require(handler.Requests.Count(item => item.Device == eId && item.Operation == WorldPeerOperations.Chunk) == eChunks &&
            !eService.Views().Single().ReceiveConsented &&
            !cData.LoadSettings().CompanionListeningEnabled && !dData.LoadSettings().CompanionListeningEnabled &&
            !eData.LoadSettings().CompanionListeningEnabled,
            "withdrawn reception resumed payloads or outbound replication enabled a listener");
        await WorldPeerSeedingChecks.RunAsync(rootPath);
    }

    private static void Initialize(LocalData data, SharedWorldPeerRoot source, SharedWorldRoster roster,
        Guid deviceId, ECDsa key, bool consent, bool sharing, SharedWorldPeerRoute bootstrap)
    {
        data.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected", key.ExportPkcs8PrivateKey());
        new SharedWorldRosterChainStore(data).Append(roster, source.OwnerPublicKey);
        SaveMembership(data, source, roster, deviceId, consent, sharing, bootstrap);
    }
    private static void SaveMembership(LocalData data, SharedWorldPeerRoot source, SharedWorldRoster roster,
        Guid deviceId, bool consent, bool sharing, SharedWorldPeerRoute bootstrap) =>
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Groups = new[] { new { Root = source, Roster = roster, DeviceId = deviceId,
                Name = "Synthetic replication world", ReceiveConsented = consent, SharingEnabled = sharing, Bootstrap = bootstrap } }
        }, Json));
    private static void SaveOwnerConnection(LocalData data, SharedWorldPeerRoot source, SharedWorldRoster roster,
        Guid deviceId, bool consent, SharedWorldPeerRoute ownerRoute)
    {
        data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            DisplayName = "Synthetic offline original A",
            DeviceId = deviceId,
            Endpoint = ownerRoute.Address,
            Fingerprint = ownerRoute.CertificateSha256,
            Credential = new string('C', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            ConsentedSharedWorldProfiles = consent ? [source.ProfileId] : [],
            ApprovedSharedWorldGroups = new() { [source.ProfileId] = source.GroupId },
            SharedWorldSigningKeys = new() { [source.ProfileId] = source.OwnerPublicKey },
            SharedRosterFloors = new() { [source.ProfileId] = new(source.GroupId, roster.Epoch, roster.Revision, roster.Signature) }
        }, Json));
        data.SaveProtected($"shared-world-roster-{deviceId:N}-{source.ProfileId:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(roster, Json));
    }
    private static SharedWorldPeerStore PeerStore(LocalData data, SharedWorldPeerRoot source, SharedWorldRoster roster) =>
        new(data, source, new SharedWorldPeerTrust(source, (id, key) => SharedWorldPeerTrust.AccessFromRoster(source, roster, id, key)));
    private static string Vault(LocalData data, Guid device, Guid profile) =>
        Path.Combine(data.RootPath, "received-shared-worlds", device.ToString("N"), profile.ToString("N"));
    private static SharedWorldVersion Version(SharedWorldPeerRoot source, ECDsa owner, byte[] bytes) =>
        SharedWorldService.SignVersion(new(4, source.GroupId, 1, null, source.ProfileId,
            GameKinds.Fixture, "SyntheticPeerRecovery", DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
            SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(2456, false, "Unknown", [], []),
            [Fact(bytes)], "", "", ""), owner);
    private static SharedWorldFile Fact(byte[] bytes) => new("world.dat", bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(bytes)));
    private static SharedWorldRoster SignRoster(SharedWorldRoster draft, ECDsa owner) => draft with
    { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void WriteCopy(string vault, SharedWorldVersion version, byte[] bytes)
    {
        var directory = Path.Combine(vault, version.VersionHash);
        Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
        File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(vault, version);
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }
    private static List<SharedWorldReceipt> Receipts(LocalData data, SharedWorldPeerRoot source)
    {
        var bytes = data.LoadProtected($"world-peer-receipts-{source.GroupKey}.protected");
        return bytes is null ? [] : JsonSerializer.Deserialize<List<SharedWorldReceipt>>(bytes, Json) ?? [];
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed record RequestSeen(Guid Device, string Operation, string? Version);
    private sealed class PeerHandler(WorldPeerService service, SharedWorldPeerRoot source,
        SharedWorldPeerRoute route) : HttpMessageHandler
    {
        internal List<RequestSeen> Requests { get; } = [];
        internal Func<WorldPeerRequest, Task>? AfterChunk { get; set; }
        internal bool DropNextReceipt { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.Method == HttpMethod.Post && request.Headers.Authorization is null &&
                request.RequestUri!.GetLeftPart(UriPartial.Authority) == route.Address && request.RequestUri.Query.Length == 0 &&
                request.Headers.GetValues(CompanionProtocol.HeaderName).Single() ==
                    CompanionProtocol.Current.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                request.Headers.GetValues("X-TogetherServer-Capability").Single() == CompanionProtocol.SharedWorldPeersCapability,
                "replication bypassed a fixed pinned peer route or reused a Host bearer credential");
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            var prefix = $"/api/companion/world-peers/{source.GroupKey}/";
            if (request.RequestUri.AbsolutePath == prefix + "challenge")
            {
                var hello = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeRequest>(bytes, 2 * 1024 * 1024);
                var challenge = service.PrepareChallenge(source.GroupKey, hello, route.CertificateSha256);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(challenge, options: SharedWorldPeerTrust.Json) };
            }
            Require(request.RequestUri.AbsolutePath == prefix + "exchange", "an arbitrary peer operation path was used");
            var envelope = WorldPeerProtocol.DecodeEnvelope<WorldPeerExchangeRequest>(bytes, 2 * 1024 * 1024);
            Requests.Add(new(envelope.Request.Challenge.DeviceId, envelope.Request.Operation, envelope.Request.VersionHash));
            if (envelope.Request.Operation == WorldPeerOperations.Receipt && DropNextReceipt)
            {
                DropNextReceipt = false;
                throw new HttpRequestException("Synthetic first receipt delivery was lost.");
            }
            var reply = await service.ExchangeAsync(source.GroupKey, envelope.Request, envelope.Payload, token, route.CertificateSha256);
            if (envelope.Request.Operation == WorldPeerOperations.Chunk && AfterChunk is not null)
                await AfterChunk(envelope.Request);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(reply) };
        }
    }
}

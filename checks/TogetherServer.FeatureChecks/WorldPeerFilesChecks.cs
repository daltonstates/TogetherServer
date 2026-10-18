using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Signed synthetic manifests/files and the actual consent gate only. No app,
// listener, socket, process, browser, game, dialog or production data is opened.
internal static class WorldPeerFilesChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "peer-files-" + Guid.NewGuid().ToString("N")));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var source = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var bytes = new byte[SharedWorldService.ChunkBytes * 4 + 37];
        RandomNumberGenerator.Fill(bytes);
        var first = Version(source, owner, bytes);
        ChunkIntegrity(data, first, bytes);
        CompletionIntegrity(data, first, bytes);
        await LongHistoryAsync(data, source, owner, first);
        var stranger = SharedWorldService.SignVersion(first, other);
        Require(!WorldPeerFileTransfer.TrustedHistoricSigner(source, stranger, []),
            "a valid self-signed foreign save became an authorized branch base");
        Require(WorldPeerFileTransfer.TrustedHistoricSigner(source, first, []),
            "the pinned owner's signed base was refused");
        await WithdrawalBeforePromotionAsync(data, source);
    }

    private static void ChunkIntegrity(LocalData data, SharedWorldVersion version, byte[] bytes)
    {
        var root = Path.Combine(data.RootPath, "chunk-inventory");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "world.dat");
        File.WriteAllBytes(path, bytes);
        var inventory = new WorldPeerChunkInventory();
        for (long offset = 0; offset < bytes.Length; offset += SharedWorldService.ChunkBytes)
        {
            var result = inventory.Read(data.RootPath, path, version.Files[0], offset, default);
            Require(result.AsSpan().SequenceEqual(bytes.AsSpan((int)offset, result.Length)),
                "a served peer chunk differed from the signed payload");
        }
        Require(inventory.FullyHashedBytes == bytes.Length,
            "the peer rehashed a complete file for each chunk instead of caching its verified inventory");
        var modified = bytes.ToArray();
        modified[SharedWorldService.ChunkBytes + 3] ^= 0xff;
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, modified);
        File.SetLastWriteTimeUtc(path, stamp);
        Reject(() => inventory.Read(data.RootPath, path, version.Files[0], SharedWorldService.ChunkBytes, default),
            "same-length peer tampering with a restored timestamp bypassed the cached chunk digest");
        Reject(() => new WorldPeerChunkInventory().Read(data.RootPath, path, version.Files[0], 0, default),
            "an unverified replacement produced a new cached inventory");
        Reject(() => inventory.Read(data.RootPath, path, version.Files[0], 1, default),
            "a misaligned chunk offset was accepted");
    }

    private static void CompletionIntegrity(LocalData data, SharedWorldVersion version, byte[] bytes)
    {
        var vault = Path.Combine(data.RootPath, "completion");
        var destination = Path.Combine(vault, version.VersionHash);
        var payload = Path.Combine(destination, SharedWorldService.PayloadDirectory);
        Directory.CreateDirectory(payload);
        var path = Path.Combine(payload, "world.dat");
        File.WriteAllBytes(Path.Combine(destination, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 1).ToArray());
        Reject(() => { using var lease = WorldPeerVerifiedCopy.Open(data.RootPath, destination, version, default); },
            "a partial pre-existing destination passed receiver verification");
        Require(!File.Exists(Path.Combine(vault, "latest.json")), "a rejected partial destination became current");
        var changed = bytes.ToArray(); changed[^1] ^= 1;
        File.WriteAllBytes(path, changed);
        Reject(() => { using var lease = WorldPeerVerifiedCopy.Open(data.RootPath, destination, version, default); },
            "a same-length tampered destination passed full receiver verification");
        Require(!File.Exists(Path.Combine(vault, "latest.json")), "a rejected changed destination became current");
        File.WriteAllBytes(path, bytes);
        FriendLink.KeepSignedManifest(vault, version);
        using (var lease = WorldPeerVerifiedCopy.Open(data.RootPath, destination, version, default))
        {
            RejectIo(() => File.WriteAllBytes(path, changed),
                "a verified receiver file remained writable during pointer promotion");
            WorldPeerFileTransfer.VerifyHistory(vault, new(version), null);
            WorldPeerFileTransfer.Promote(vault, new(version));
        }
        Require(FriendLink.ReadReceivedLatest(vault)?.VersionHash == version.VersionHash,
            "the fully verified receiver copy was not promoted");
        var oldPointer = File.ReadAllBytes(Path.Combine(vault, "latest.json"));
        File.WriteAllBytes(path, changed);
        Reject(() => { using var lease = WorldPeerVerifiedCopy.Open(data.RootPath, destination, version, default); },
            "a later-tampered existing destination passed promotion verification");
        Require(File.ReadAllBytes(Path.Combine(vault, "latest.json")).SequenceEqual(oldPointer),
            "failed existing-destination verification rewrote the prior pointer");

        var partial = Path.Combine(data.RootPath, ".partial-synthetic", SharedWorldService.PayloadDirectory);
        Directory.CreateDirectory(partial);
        var partialFile = Path.Combine(partial, "world.dat");
        File.WriteAllBytes(partialFile, changed);
        Reject(() => WorldPeerFileTransfer.VerifyStage(data.RootPath, partial, version),
            "a corrupt partial file passed final verification");
        Require(new FileInfo(partialFile).Length == 0 && File.ReadAllBytes(path).SequenceEqual(changed),
            "failed partial verification changed a completed copy or prevented a clean retry");
    }

    private static async Task LongHistoryAsync(LocalData data, SharedWorldPeerRoot source,
        ECDsa owner, SharedWorldVersion first)
    {
        var vault = Path.Combine(data.RootPath, "long-history");
        Directory.CreateDirectory(vault);
        var versions = new List<SharedWorldVersion> { first };
        for (var number = 2; number <= WorldAuthorityTrust.ChainVersionsPerCheck * 3 + 7; number++)
            versions.Add(SharedWorldService.SignVersion(first with
            {
                Number = number,
                ParentHash = versions[^1].VersionHash,
                BackupId = Guid.NewGuid()
            }, owner));
        var byHash = versions.ToDictionary(version => version.VersionHash);
        var calls = new Dictionary<string, int>();
        Task<SharedWorldVersion?> Fetch(string hash, long number, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls[hash] = calls.GetValueOrDefault(hash) + 1;
            Require(byHash[hash].Number == number, "history paging changed the requested signed version number");
            return Task.FromResult<SharedWorldVersion?>(byHash[hash]);
        }
        var complete = false;
        var passes = 0;
        while (!complete && passes++ < 8)
            complete = await WorldPeerFileTransfer.StageHistoryAsync(source, vault, versions[^1], null, Fetch, default);
        Require(complete && passes == 4 && calls.Count == versions.Count - 1 && calls.Values.All(count => count == 1),
            "peer ancestry did not resume past the first 128 parents across bounded passes");
        WorldPeerFileTransfer.VerifyHistory(vault, new(versions[^1]), null);
        Require(WorldPeerFileTransfer.KnownDescendant(vault, first, versions[^1]),
            "complete cached ancestry did not establish the existing head as a parent");
        var fork = SharedWorldService.SignVersion(versions[^1] with { BackupId = Guid.NewGuid() }, owner);
        Require(!WorldPeerFileTransfer.KnownDescendant(vault, versions[^1], fork),
            "a divergent equal-number history could silently replace the verified head");
        var missing = Directory.EnumerateFiles(Path.Combine(vault, "signed-history"), "*.json", SearchOption.AllDirectories)
            .Single(path => JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json)!.Number == 2);
        File.WriteAllText(missing, "tampered signed ancestor");
        Reject(() => WorldPeerFileTransfer.VerifyHistory(vault, new(versions[^1]), null),
            "a persisted history cursor bypassed complete ancestor verification before promotion");
        Require(!File.Exists(Path.Combine(vault, "latest.json")), "history staging or verification selected a current payload");
    }

    private static async Task WithdrawalBeforePromotionAsync(LocalData data, SharedWorldPeerRoot source)
    {
        const string saved = "peer-consent.synthetic.protected";
        var deviceId = Guid.NewGuid();
        data.SaveProtected(saved, JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            DeviceId = deviceId,
            Endpoint = "https://192.0.2.41:5131",
            Fingerprint = new string('A', 64),
            Credential = new string('B', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            ConsentedSharedWorldProfiles = [source.ProfileId],
            ApprovedSharedWorldGroups = new() { [source.ProfileId] = source.GroupId },
            SharedWorldSigningKeys = new() { [source.ProfileId] = source.OwnerPublicKey }
        }, Json));
        using var link = new FriendLink(data, saved, (_, _) => throw new InvalidOperationException("no HTTP is allowed in the consent check"));
        var gate = (SemaphoreSlim)typeof(FriendLink).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(link)!;
        await gate.WaitAsync();
        var promoted = false;
        Task<bool> commit;
        Task<ReceivedSharedWorldResult> withdraw;
        try
        {
            commit = link.CommitPeerReceiveAsync(source, deviceId, () => promoted = true, default);
            withdraw = link.SetSharedWorldConsentAsync(source.ProfileId, false);
            Require(!commit.IsCompleted && !withdraw.IsCompleted, "the synthetic consent race did not queue behind the real source gate");
        }
        finally { gate.Release(); }
        Require(!await commit.WaitAsync(TimeSpan.FromSeconds(5)) && !promoted &&
            (await withdraw.WaitAsync(TimeSpan.FromSeconds(5))).Ok,
            "a queued peer promotion passed an already-requested consent withdrawal");
        Require(!await link.CommitPeerReceiveAsync(source, deviceId, () => promoted = true, default) && !promoted,
            "a completed consent withdrawal permitted later pointer promotion");
    }

    private static SharedWorldVersion Version(SharedWorldPeerRoot source, ECDsa owner, byte[] bytes) =>
        SharedWorldService.SignVersion(new(1, source.GroupId, 1, null, source.ProfileId,
            GameKinds.Valheim, "synthetic-peer-copy", DateTimeOffset.UtcNow, SharedWorldCaptureKinds.PostStopBackup,
            Guid.NewGuid(), new(2456, false), [new("world.dat", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))],
            "", "", ""), owner);

    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void RejectIo(Action action, string message)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException(message);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;
using Fixture = TogetherServer.FeatureChecks.WorldPeerExistingCopyChecks.Fixture;

namespace TogetherServer.FeatureChecks;

// Exact signed fake HTTP operations over generated files/identities. No socket,
// game, process, app, browser or production state is opened.
internal static class WorldPeerChunkOperationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent)
    {
        foreach (var shape in new[] { "Original", "Branch", "Retained", "Received", "ReceivedBranch", "History" }) await OneInventoryAsync(parent, shape);
        foreach (var shape in new[] { "Branch", "Retained", "Received", "ReceivedBranch", "History" }) await ChangedPointerAsync(parent, shape);
        foreach (var moment in new[] { "Inventory", "Warm", "AfterRead" })
            foreach (var mutation in new[] { "Latest", "Registry", "Authority", "Receive", "Reshare", "Consent", "Key", "Sharing", "Canceled" })
                await ChangedSourceAsync(parent, mutation, moment);
        await ChangedRosterAsync(parent);
        await ChunkDigestAndFinalPayloadAsync(parent);
        await CursorAsync(parent);
    }

    private static async Task OneInventoryAsync(string parent, string shape)
    {
        using var fixture = new Fixture(parent, shape is "ReceivedBranch" or "History" ? "Branch" : shape == "Received" ? "Retained" : shape);
        if (shape == "Retained") File.Delete(fixture.CanonicalReceivedPointer);
        if (shape is "ReceivedBranch" or "History") StageAlternateSource(fixture, shape == "History");
        var inventories = 0;
        fixture.TargetService.LocalSaveInventoryForChecks = () => inventories++;
        var bytes = await ChunkAsync(fixture, fixture.Descriptor.Version, 0);
        var repeated = await ChunkAsync(fixture, fixture.Descriptor.Version, 0);
        Require(inventories == 1 && bytes.SequenceEqual(File.ReadAllBytes(fixture.PayloadPath)) && repeated.SequenceEqual(bytes) &&
            fixture.SourceHandler.Operations.Count(operation => operation == WorldPeerOperations.Chunk) == 2 &&
            fixture.A.LoadRuns().Count == 0 && fixture.B.LoadRuns().Count == 0,
            "An unchanged signed chunk rebuilt its inventory or lost exact source bytes: " + shape);
        var old = fixture.TargetService;
        fixture.ReopenTarget();
        Require(((System.Collections.IDictionary)typeof(WorldPeerService).GetField("chunkCatalogs", Private)!.GetValue(old)!).Count == 0,
            "Disposed peer service retained its chunk source hints.");
        fixture.TargetService.LocalSaveInventoryForChecks = () => inventories++;
        var reopened = await ChunkAsync(fixture, fixture.Descriptor.Version, 0);
        Require(inventories == 2 && reopened.SequenceEqual(bytes), "Reopened source failed fresh inventory and signed chunk verification: " + shape);
    }

    private static async Task ChangedPointerAsync(string parent, string shape)
    {
        using var fixture = new Fixture(parent, shape is "ReceivedBranch" or "History" ? "Branch" : shape == "Received" ? "Retained" : shape);
        if (shape == "Retained") File.Delete(fixture.CanonicalReceivedPointer);
        if (shape is "ReceivedBranch" or "History") StageAlternateSource(fixture, shape == "History");
        var inventories = 0;
        fixture.TargetService.LocalSaveInventoryForChecks = () => inventories++;
        await ChunkAsync(fixture, fixture.Descriptor.Version, 0);
        var pointer = shape switch
        {
            "Received" => fixture.CanonicalReceivedPointer,
            "ReceivedBranch" => Path.Combine(SharedWorldBranchVault.Root(fixture.Target, fixture.Descriptor.Branch!), "latest.json"),
            "History" => Path.Combine(fixture.Target.RootPath, "received-shared-histories", fixture.Root.GroupKey,
                fixture.Descriptor.Version.VersionHash, "latest.json"),
            _ => Path.Combine(Path.GetDirectoryName(fixture.DirectoryPath)!, "latest.json")
        };
        Require(File.Exists(pointer), "The generated selected source pointer was missing: " + shape);
        File.Delete(pointer);
        await RejectAsync(() => ChunkAsync(fixture, fixture.Descriptor.Version, 0),
            "A warm hint silently selected another copy after its exact source pointer vanished: " + shape);
        Require(inventories == 1, "A stale hint rescanned all vaults before rejecting its changed selected source: " + shape);
    }

    private static async Task ChangedSourceAsync(string parent, string mutation, string moment)
    {
        using var fixture = new Fixture(parent, mutation is "Reshare" or "Consent" ? "Retained" : "Branch");
        if (mutation is "Reshare" or "Consent") File.Delete(fixture.CanonicalReceivedPointer);
        var beforePayload = File.ReadAllBytes(fixture.PayloadPath);
        var inventories = 0;
        var changed = false;
        byte[]? authorityBytes = null;
        using var canceled = new CancellationTokenSource();
        fixture.TargetService.LocalSaveInventoryForChecks = () => inventories++;
        void Mutate()
        {
            switch (mutation)
            {
                case "Latest":
                    PublishChild(fixture, beforePayload);
                    break;
                case "Registry":
                    File.Delete(Path.Combine(fixture.Target.RootPath, $"shared-world-separate-{fixture.Root.ProfileId:N}.protected"));
                    break;
                case "Authority":
                    var name = $"authority-floor-{fixture.Root.ProfileId:N}.protected";
                    fixture.Target.SaveProtected(name, Encoding.UTF8.GetBytes("{damaged generated authority floor"));
                    authorityBytes = File.ReadAllBytes(Path.Combine(fixture.Target.RootPath, name));
                    break;
                case "Receive": fixture.AdvanceRoster(receive: false); break;
                case "Reshare": fixture.AdvanceRoster(receive: true, reshare: false); break;
                case "Consent":
                    var saved = fixture.TargetService.SetReceivingAsync(fixture.Root.GroupKey, false, default).GetAwaiter().GetResult();
                    if (!saved.Ok) throw new InvalidOperationException("The generated consent change failed.");
                    break;
                case "Key":
                    using (var replacement = ECDsa.Create())
                        fixture.Target.SaveProtected(Path.GetFileName(fixture.KeyPath), replacement.ExportPkcs8PrivateKey());
                    break;
                case "Sharing": fixture.SaveMemberships(sharing: false, targetOnly: true); break;
                case "Canceled": canceled.Cancel(); break;
            }
            changed = true;
        }
        if (moment != "Inventory") await ChunkAsync(fixture, fixture.Descriptor.Version, 0);
        if (moment == "Warm") Mutate();
        else if (moment == "AfterRead") fixture.TargetService.ChunkPayloadReadForChecks = Mutate;
        else fixture.TargetService.LocalSaveInventoryForChecks = () => { inventories++; Mutate(); };
        await RejectAsync(() => ChunkAsync(fixture, fixture.Descriptor.Version, 0, canceled.Token),
            "A source hint served after a selected source/current permission change: " + moment + "/" + mutation);
        var expectedInventories = moment == "Warm" && mutation is "Receive" or "Reshare" or "Consent" ? 2 : 1;
        Require(changed && inventories == expectedInventories && File.ReadAllBytes(fixture.PayloadPath).SequenceEqual(beforePayload),
            "The source-change check lost its mutation witness, rebuilt inventory or changed the old immutable payload: " + moment + "/" + mutation);
        if (mutation == "Authority")
            Require(File.ReadAllBytes(Path.Combine(fixture.Target.RootPath, $"authority-floor-{fixture.Root.ProfileId:N}.protected"))
                .SequenceEqual(authorityBytes!), "The damaged authority record was repaired while refusing a chunk.");
    }

    private static async Task ChangedRosterAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        var inventories = 0;
        fixture.TargetService.LocalSaveInventoryForChecks = () => inventories++;
        var bytes = await ChunkAsync(fixture, fixture.Descriptor.Version, 0);
        fixture.AdvanceRoster(receive: true);
        var current = await ChunkAsync(fixture, fixture.Descriptor.Version, 0);
        Require(inventories == 2 && current.SequenceEqual(bytes), "An advanced valid roster failed to invalidate and rebuild its source hints.");
    }

    private static async Task ChunkDigestAndFinalPayloadAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        var payload = new byte[SharedWorldService.ChunkBytes * 2 + 57];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 239);
        var version = PublishChild(fixture, payload);
        var path = Path.Combine(Path.GetDirectoryName(fixture.DirectoryPath)!, version.VersionHash,
            SharedWorldService.PayloadDirectory, "world.dat");
        var inventoryCount = 0;
        fixture.TargetService.LocalSaveInventoryForChecks = () => inventoryCount++;
        var first = await ChunkAsync(fixture, version, 0);
        Require(inventoryCount == 1 && first.SequenceEqual(payload.AsSpan(0, first.Length).ToArray()),
            "A first multi-chunk source operation rebuilt inventory or returned unsigned bytes.");
        var stamp = File.GetLastWriteTimeUtc(path);
        var changed = payload.ToArray(); changed[SharedWorldService.ChunkBytes + 3] ^= 0xFF;
        File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, stamp);
        await RejectAsync(() => ChunkAsync(fixture, version, SharedWorldService.ChunkBytes),
            "A same-length/timestamp payload change bypassed the per-chunk digest after catalog reuse.");
        Require(inventoryCount == 1, "The rejected chunk rebuilt inventory or bypassed the actual source path.");
        File.WriteAllBytes(path, payload); File.SetLastWriteTimeUtc(path, stamp);
        fixture.TargetService.LocalSaveInventoryForChecks = null;
        var before = fixture.SourceHandler.Operations.Count(operation => operation == WorldPeerOperations.Chunk);
        await ((Task)typeof(WorldPeerService).GetMethod("PullCatalogAsync", Private)!.Invoke(fixture.RequesterService,
            [Group(fixture.RequesterService, fixture.Root), fixture.SourceRoute, CancellationToken.None])!);
        var vault = SharedWorldBranchVault.Root(fixture.RequesterData, fixture.Descriptor.Branch!);
        var received = FriendLink.ReadReceivedLatest(vault);
        Require(received is not null && received.VersionHash == version.VersionHash && SharedWorldService.VerifySignature(received) &&
            File.ReadAllBytes(Path.Combine(vault, version.VersionHash, SharedWorldService.PayloadDirectory, "world.dat")).SequenceEqual(payload) &&
            fixture.SourceHandler.Operations.Count(operation => operation == WorldPeerOperations.Chunk) == before + 3 &&
            fixture.A.LoadRuns().Count == 0 && fixture.B.LoadRuns().Count == 0,
            "The actual receive path failed its complete exact signed payload after chunk inventory reuse.");
    }

    private static async Task CursorAsync(string parent)
    {
        using var fixture = new Fixture(parent, "Branch");
        await CatalogAsync(fixture);
        var routeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            fixture.SourceRoute.Address.TrimEnd('/') + "\n" + fixture.SourceRoute.CertificateSha256)));
        var name = $"world-peer-cursor-{fixture.Root.GroupKey}-{routeKey}.protected";
        var path = Path.Combine(fixture.RequesterData.RootPath, name);
        var initialBytes = File.ReadAllBytes(path);
        var sentinel = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, sentinel);
        await CatalogAsync(fixture);
        Require(File.ReadAllBytes(path).SequenceEqual(initialBytes) && File.GetLastWriteTimeUtc(path) == sentinel,
            "An unchanged authenticated roster handshake rewrote its protected cursor.");
        fixture.AdvanceRoster(receive: true);
        await CatalogAsync(fixture);
        var advanced = File.ReadAllBytes(path);
        using (var cursor = JsonDocument.Parse(fixture.RequesterData.LoadProtected(name)!))
            Require(cursor.RootElement.GetProperty("knownRemoteRosterRevision").GetInt64() == 2 &&
                !advanced.SequenceEqual(initialBytes) && File.GetLastWriteTimeUtc(path) != sentinel,
                "A newly verified remote roster revision was not persisted in the protected cursor.");
        File.SetLastWriteTimeUtc(path, sentinel);
        fixture.ReopenRequester();
        await CatalogAsync(fixture);
        Require(File.ReadAllBytes(path).SequenceEqual(advanced) && File.GetLastWriteTimeUtc(path) == sentinel,
            "A reopened requester lost its verified roster cursor or rewrote an unchanged revision.");
    }

    private static SharedWorldVersion PublishChild(Fixture fixture, byte[] payload)
    {
        using var hosting = ECDsa.Create();
        hosting.ImportPkcs8PrivateKey(fixture.Target.LoadProtected(WorldAuthorityStore.HostingKeyName(fixture.Root.ProfileId))!, out _);
        var prior = fixture.Descriptor.Version;
        var version = SharedWorldService.SignVersion(prior with
        {
            Number = prior.Number + 1,
            ParentHash = prior.VersionHash,
            BackupId = Guid.NewGuid(),
            Files = [new("world.dat", payload.Length, Convert.ToHexString(SHA256.HashData(payload)))]
        }, hosting);
        var vault = Path.GetDirectoryName(fixture.DirectoryPath)!;
        var folder = Path.Combine(vault, version.VersionHash);
        Directory.CreateDirectory(Path.Combine(folder, SharedWorldService.PayloadDirectory));
        File.WriteAllBytes(Path.Combine(folder, SharedWorldService.PayloadDirectory, "world.dat"), payload);
        File.WriteAllBytes(Path.Combine(folder, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(vault, version);
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        return version;
    }

    private static void StageAlternateSource(Fixture fixture, bool history)
    {
        var branch = fixture.Descriptor.Branch!;
        var version = fixture.Descriptor.Version;
        File.Delete(Path.Combine(Path.GetDirectoryName(fixture.DirectoryPath)!, "latest.json"));
        var vault = history ? Path.Combine(fixture.Target.RootPath, "received-shared-histories", fixture.Root.GroupKey, version.VersionHash) :
            SharedWorldBranchVault.Root(fixture.Target, branch);
        var directory = Path.Combine(vault, version.VersionHash);
        Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
        File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), File.ReadAllBytes(fixture.PayloadPath));
        File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(vault, branch.Offer.Version);
        FriendLink.KeepSignedManifest(vault, version);
        File.WriteAllBytes(Path.Combine(vault, "branch.json"), JsonSerializer.SerializeToUtf8Bytes(branch, Json));
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
    }

    private static object Group(WorldPeerService service, SharedWorldPeerRoot root) =>
        typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(service, [root.GroupKey])!;
    private static Task<byte[]> ChunkAsync(Fixture fixture, SharedWorldVersion version, long offset, CancellationToken cancellationToken = default) =>
        (Task<byte[]>)typeof(WorldPeerService).GetMethod("SendPeerAsync", Private)!.Invoke(fixture.RequesterService,
            [Group(fixture.RequesterService, fixture.Root), fixture.SourceRoute, WorldPeerOperations.Chunk, cancellationToken,
                null, 0, version.VersionHash, 0, offset])!;
    private static Task<byte[]> CatalogAsync(Fixture fixture) =>
        (Task<byte[]>)typeof(WorldPeerService).GetMethod("SendPeerAsync", Private)!.Invoke(fixture.RequesterService,
            [Group(fixture.RequesterService, fixture.Root), fixture.SourceRoute, WorldPeerOperations.Catalog, CancellationToken.None,
                null, 0, null, null, null])!;
    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or CryptographicException or OperationCanceledException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require([DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

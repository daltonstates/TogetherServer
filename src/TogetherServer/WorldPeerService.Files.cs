using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

public sealed record WorldPeerSaveDescriptor(SharedWorldVersion Version,
    WorldSeparateCopyBranch? Branch = null, string? SessionBranchHash = null,
    bool TestOnly = false, LiveSaveTestArtifact? TestArtifact = null);
public sealed record WorldPeerCatalog(SharedWorldPeerRoot Root,
    IReadOnlyList<WorldPeerSaveDescriptor> Saves);

internal sealed partial class WorldPeerService
{
    private sealed record LocalSave(WorldPeerSaveDescriptor Descriptor, string? Vault,
        bool ProducedHere);
    private readonly WorldPeerChunkInventory peerChunks = new();
    private readonly Dictionary<string, int> liveTestPositions = new(StringComparer.Ordinal);
    private sealed record ReceiptDelivery(DateTimeOffset AttemptedUtc, bool Confirmed);
    private readonly Dictionary<(string Group, string Route, string Pin, Guid Receipt), ReceiptDelivery> receiptDeliveries = [];
    // Optional synthetic-check observation of an actual receipt hash attempt.
    // It receives no identity or path and never authorizes a copy or receipt.
    internal Action? ReceiptPayloadHashingForChecks { get; set; }

    private WorldPeerVerifiedCopy VerifyReceiptPayload(string vault, SharedWorldVersion version,
        CancellationToken cancellationToken)
    {
        try { ReceiptPayloadHashingForChecks?.Invoke(); }
        catch (Exception) { /* Observations cannot change payload verification. */ }
        return WorldPeerVerifiedCopy.Open(data.RootPath,
            SharedWorldService.SafeChild(vault, version.VersionHash), version, cancellationToken);
    }

    private static string RetainedHistoryParent(LocalData data, Group group) => Path.Combine(
        data.RootPath, "received-shared-histories", group.Root.GroupKey);

    private static SharedWorldVersion? ReadPeerPointer(string root)
    {
        var path = Path.Combine(root, "latest.json");
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > SharedWorldService.MaximumManifestBytes)
            throw new InvalidDataException("The peer copy pointer is linked or oversized.");
        var version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json);
        if (version is null || !SharedWorldService.VerifySignature(version) ||
            !Directory.Exists(SharedWorldService.SafeChild(root, version.VersionHash)))
            throw new InvalidDataException("The signed peer copy pointer failed verification.");
        // This is metadata only. Serving verifies each chunk against an inventory
        // bound to the full signed file hash; completion always rehashes all files.
        return version;
    }

    private static string ReceivedRoot(LocalData data, Group group) => Path.Combine(data.RootPath,
        "received-shared-worlds", group.DeviceId.ToString("N"), group.Root.ProfileId.ToString("N"));

    private bool MatchesRoot(Group group, SharedWorldVersion version) =>
        version.ProfileId == group.Root.ProfileId && version.GroupId == group.Root.GroupId &&
        version.CaptureKind != LiveSaveAcceptanceTrust.TestCaptureKind &&
        SharedWorldService.VerifySignature(version);

    private bool AuthorizedDescriptor(Group group, WorldPeerSaveDescriptor descriptor)
    {
        if (descriptor is null || descriptor.Version is null) return false;
        if (descriptor.TestOnly || descriptor.TestArtifact is not null) return AuthorizedLiveTest(group, descriptor);
        var version = descriptor.Version;
        if (!MatchesRoot(group, version)) return false;
        var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
        if (descriptor.Branch is not { } branch)
            return WorldPeerFileTransfer.TrustedHistoricSigner(group.Root, version, records);
        if (!SharedWorldSeparateCopyStore.Verify(branch) ||
            branch.Offer.Roster.OwnerPublicKey != group.Root.OwnerPublicKey ||
            branch.Offer.Roster.GroupId != group.Root.GroupId ||
            branch.Offer.Roster.ProfileId != group.Root.ProfileId ||
            !SharedWorldSeparateCopyStore.VerifySnapshotVersion(branch, version)) return false;
        // A candidate signature cannot authorize an arbitrary save signer.
        for (var source = branch; ; source = source.SourceBranch!)
        {
            if (source.SourceBranch is not null) continue;
            return WorldPeerFileTransfer.TrustedHistoricSigner(group.Root, source.Offer.Version, records);
        }
    }

    private async Task<IReadOnlyList<LocalSave>> LocalSavesAsync(Group group,
        CancellationToken cancellationToken, string? retainedPublicKey = null)
    {
        var result = new List<LocalSave>();
        using var localKey = retainedPublicKey is null ? LocalKey(group) : null;
        var localPublicKey = retainedPublicKey ?? (localKey is null ? null : Convert.ToBase64String(localKey.ExportSubjectPublicKeyInfo()));
        var published = await manager.PeerPublishedVersionAsync(group.Root);
        if (published is not null && MatchesRoot(group, published)) result.Add(new(new(published), null, true));
        if (Directory.Exists(ReceivedRoot(data, group)))
        {
            var vault = ReceivedRoot(data, group);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
            var received = ReadPeerPointer(vault);
            if (received is not null && AuthorizedDescriptor(group, new(received)))
                result.Add(new(new(received), vault, false));
        }
        var separate = new SharedWorldSeparateCopyStore(data);
        foreach (var branch in separate.Read(group.Root.ProfileId).Where(branch =>
            branch.Offer.Roster.OwnerPublicKey == group.Root.OwnerPublicKey &&
            branch.Offer.Roster.GroupId == group.Root.GroupId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publishedRoot = separate.PublishedRoot(branch);
            var retainedRoot = separate.RetainedRoot(branch);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, publishedRoot);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, retainedRoot);
            var snapshot = ReadPeerPointer(publishedRoot) ?? ReadPeerPointer(retainedRoot);
            if (snapshot is not null && AuthorizedDescriptor(group, new(snapshot, branch)))
                result.Add(new(new(snapshot, branch),
                    Directory.Exists(Path.Combine(publishedRoot, snapshot.VersionHash)) ? publishedRoot : retainedRoot,
                    snapshot.SigningPublicKey == branch.Offer.Proposal.CandidatePublicKey &&
                    branch.Offer.Proposal.ProposerDeviceId == group.DeviceId && branch.CandidatePublicKey == localPublicKey));
        }
        if (Directory.Exists(Path.Combine(data.RootPath, "received-shared-branches", group.Root.GroupKey)))
        {
            var parent = Path.Combine(data.RootPath, "received-shared-branches", group.Root.GroupKey);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, parent);
            if (Directory.Exists(parent))
                foreach (var path in Directory.EnumerateDirectories(parent).Take(513))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var hash = Path.GetFileName(path);
                    if (!SharedWorldPeerTrust.ValidHash(hash)) continue;
                    var version = ReadPeerPointer(path);
                    // History staging creates the vault before its complete
                    // pointer and branch proof are promoted. It is not a
                    // local copy yet; a present pointer still verifies above.
                    if (version is null) continue;
                    var branch = SharedWorldBranchVault.ReadBranch(data, group.Roster, hash);
                    if (AuthorizedDescriptor(group, new(version, branch)))
                        result.Add(new(new(version, branch), path, false));
                }
        }
        var histories = RetainedHistoryParent(data, group);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, histories);
        if (Directory.Exists(histories))
            foreach (var path in Directory.EnumerateDirectories(histories).Take(513))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SharedWorldPeerTrust.ValidHash(Path.GetFileName(path))) continue;
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
                var version = ReadPeerPointer(path);
                var branchPath = Path.Combine(path, "branch.json");
                WorldSeparateCopyBranch? branch = null;
                if (File.Exists(branchPath))
                {
                    SharedWorldService.EnsureUnlinkedRoot(data.RootPath, branchPath);
                    if (new FileInfo(branchPath).Length > SharedWorldService.MaximumManifestBytes)
                        throw new InvalidDataException("The retained history proof is oversized.");
                    branch = JsonSerializer.Deserialize<WorldSeparateCopyBranch>(File.ReadAllBytes(branchPath), Json);
                    if (!SharedWorldSeparateCopyStore.Verify(branch))
                        throw new InvalidDataException("The retained history proof changed.");
                }
                if (version is not null && AuthorizedDescriptor(group, new(version, branch)))
                    result.Add(new(new(version, branch), path, false));
            }
        return result.GroupBy(item => (item.Descriptor.Branch?.BranchHash, item.Descriptor.Version.VersionHash))
            .Select(items => items.OrderByDescending(item => item.ProducedHere).First()).ToArray();
    }

    private async Task<WorldPeerCatalog> LocalCatalogAsync(Group group, CancellationToken cancellationToken,
        bool includeTests = true)
    {
        group = Find(group.Root.GroupKey) ?? throw new InvalidDataException("World membership changed.");
        using var key = LocalKey(group);
        if (key is null || !group.SharingEnabled) return new(group.Root, []);
        var access = Access(group, group.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        var local = await LocalSavesAsync(group, cancellationToken);
        var saves = local.Where(item => item.ProducedHere ||
            group.ReceiveConsented && access is { Receive: true, Reshare: true })
            .Select(item => item.Descriptor).Take(32).ToList();
        if (includeTests) saves.AddRange((await LocalLiveTestDescriptorsAsync(group, cancellationToken)).Take(32 - saves.Count));
        // Bounded catalogs advertise fewer complete entries rather than emitting
        // a truncated JSON document. Other retained branches remain on disk.
        while (saves.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(new WorldPeerCatalog(group.Root, saves), Json).Length >
            WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Catalog)) saves.RemoveAt(saves.Count - 1);
        return new(group.Root, saves);
    }

    private async Task<SharedWorldVersion?> FindManifestAsync(Group group, string versionHash,
        long? number, CancellationToken cancellationToken, IReadOnlyList<LocalSave>? retainedInventory = null)
    {
        // Metadata followers can fetch signed authority proof manifests, but no payload.
        var authority = new WorldAuthorityStore(data);
        var record = authority.Read(group.Root.ProfileId).SingleOrDefault(item => item.RecordHash == versionHash &&
            item.Roster.OwnerPublicKey == group.Root.OwnerPublicKey && item.Proposal.GroupId == group.Root.GroupId);
        if (record is not null)
            return number is null || number == record.Version.Number ? record.Version :
                authority.ReadReviewProofVersion(record, number.Value);
        var local = retainedInventory ?? await LocalSavesAsync(group, cancellationToken);
        foreach (var item in local)
        {
            if (item.Descriptor.Version.VersionHash == versionHash &&
                (number is null || item.Descriptor.Version.Number == number)) return item.Descriptor.Version;
            if (item.Descriptor.Branch?.Offer.Version is { } branchBase && branchBase.VersionHash == versionHash &&
                (number is null || branchBase.Number == number)) return branchBase;
            if (number is null) continue;
            if (item.Vault is not null)
            {
                var found = FriendLink.ReadReceivedManifest(item.Vault, number.Value, versionHash);
                if (found is not null && MatchesRoot(group, found)) return found;
            }
            else
            {
                var found = await manager.PeerPublishedVersionAsync(group.Root, versionHash, number);
                if (found is not null) return found;
            }
        }
        return await FindLiveTestManifestAsync(group, versionHash, cancellationToken);
    }

    private async Task<byte[]?> ReadChunkAsync(Group group, string versionHash,
        int fileIndex, long offset, CancellationToken cancellationToken)
    {
        if (KnownLiveTest(group, versionHash) is not null)
            return await ReadLiveTestChunkAsync(group, versionHash, fileIndex, offset, cancellationToken);
        var catalog = await LocalCatalogAsync(group, cancellationToken, includeTests: false);
        var descriptor = catalog.Saves.FirstOrDefault(item => item.Version.VersionHash == versionHash);
        if (descriptor?.TestOnly == true)
            return await ReadLiveTestChunkAsync(group, versionHash, fileIndex, offset, cancellationToken);
        if (descriptor is null || fileIndex < 0 || fileIndex >= descriptor.Version.Files.Count ||
            offset < 0 || offset % SharedWorldService.ChunkBytes != 0) return null;
        var item = (await LocalSavesAsync(group, cancellationToken)).First(save =>
            save.Descriptor.Version.VersionHash == versionHash);
        if (!await MayServeSaveAsync(group, item.ProducedHere, cancellationToken)) return null;
        byte[]? result;
        if (item.Vault is null)
            result = await manager.ReadPeerPublishedChunkAsync(group.Root, versionHash, fileIndex, offset);
        else
        {
            var file = descriptor.Version.Files[fileIndex];
            var path = SharedWorldService.SafeChild(Path.Combine(item.Vault, versionHash,
                SharedWorldService.PayloadDirectory), file.Path);
            if (offset >= file.Length) return null;
            result = peerChunks.Read(data.RootPath, path, file, offset, cancellationToken);
        }
        return await MayServeSaveAsync(group, item.ProducedHere, cancellationToken) ? result : null;
    }

    private async Task<bool> MayServeSaveAsync(Group group, bool producedHere,
        CancellationToken cancellationToken)
    {
        var current = Find(group.Root.GroupKey);
        using var key = current is null ? null : LocalKey(current);
        if (current is null || key is null || !current.SharingEnabled) return false;
        var access = Access(current, current.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        if (access?.Receive != true) return false;
        return producedHere || access.Reshare && await ReceiveAllowedAsync(current, cancellationToken);
    }

    private static string ReceiptName(Group group) => $"world-peer-receipts-{group.Root.GroupKey}.protected";
    private WorldPeerActionResult AcceptReceipt(Group group, byte[] payload)
    {
        var receipt = SharedWorldReceiptTrust.Parse(payload);
        lock (stateSync)
        {
            var current = Find(group.Root.GroupKey);
            if (current is null || receipt is null || !AuthorizedCopyReceipt(current, receipt))
                return new(false, "PeerReceiptRejected", "The copy receipt is not authorized for this world.");
            group = current;
            var bytes = data.LoadProtected(ReceiptName(group));
            var old = bytes is null ? [] : JsonSerializer.Deserialize<List<SharedWorldReceipt>>(bytes, Json) ?? [];
            if (old.Any(item => item.ReceiptId == receipt.ReceiptId))
                return new(true, "PeerReceiptKnown", "This copy receipt is already recorded.");
            if (old.Count >= 4096) return new(false, "PeerReceiptCapacity", "Copy receipt capacity needs review; existing receipts were kept.");
            old.Add(receipt);
            data.SaveProtected(ReceiptName(group), JsonSerializer.SerializeToUtf8Bytes(old, Json));
        }
        return new(true, "PeerReceiptStored", "The exact verified copy receipt was recorded.");
    }

    private bool AuthorizedCopyReceipt(Group group, SharedWorldReceipt receipt)
    {
        var publicKey = receipt.DeviceId == Guid.Empty ? group.Root.OwnerPublicKey :
            group.Roster.Members.SingleOrDefault(item => item.DeviceId == receipt.DeviceId)?.PublicKey;
        return publicKey is not null &&
            Access(group, receipt.DeviceId, publicKey)?.Receive == true &&
            receipt.GroupId == group.Root.GroupId && receipt.ProfileId == group.Root.ProfileId &&
            receipt.RosterEpoch == group.Roster.Epoch && receipt.RosterRevision == group.Roster.Revision &&
            (receipt.DeviceId == Guid.Empty ? SharedWorldReceiptTrust.VerifyPeerOwner(receipt, group.Root) :
                SharedWorldReceiptTrust.Verify(receipt, publicKey));
    }

    private Task<bool> ReceiveAllowedAsync(Group group, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Find(group.Root.GroupKey);
        using var key = current is null ? null : LocalKey(current);
        if (current is null || key is null || !current.ReceiveConsented ||
            Access(current, current.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))?.Receive != true)
            return Task.FromResult(false);
        return Task.FromResult(current.DeviceId == Guid.Empty ||
            friend.HasRetainedWorldReceiveConsent(current.Root, current.DeviceId));
    }

    private async Task PullCatalogAsync(Group group, SharedWorldPeerRoute route,
        CancellationToken cancellationToken)
    {
        if (!await ReceiveAllowedAsync(group, cancellationToken)) return;
        var bytes = await SendPeerAsync(group, route, WorldPeerOperations.Catalog, cancellationToken);
        var catalog = JsonSerializer.Deserialize<WorldPeerCatalog>(bytes, Json);
        if (catalog is null || catalog.Root != group.Root || catalog.Saves is null || catalog.Saves.Count > 32 ||
            catalog.Saves.Any(item => !AuthorizedDescriptor(group, item)))
            throw new InvalidDataException("The peer save catalog does not match this trusted world.");
        var localCopies = await ExistingCopyInventoryAsync(group, cancellationToken);
        // Scheduling a later numbered payload first avoids an older original
        // entry delaying a continuation. Ancestry alone still selects history.
        foreach (var descriptor in catalog.Saves.Where(item => !item.TestOnly).OrderByDescending(item => item.Version.Number))
        {
            if (await TryReuseExistingCopyAsync(group, descriptor, cancellationToken, localCopies, route) is not null) continue;
            var vault = descriptor.Branch is { } branch ? SharedWorldBranchVault.Root(data, branch) : ReceivedRoot(data, group);
            var version = descriptor.Version;
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
            await FriendLink.ReceiveGateFor(vault).WaitAsync(cancellationToken);
            try
            {
                if (!await ReceiveAllowedAsync(group, cancellationToken)) return;
                var existing = ReadPeerPointer(vault);
                if (existing?.VersionHash == version.VersionHash)
                {
                    await RetryRetainedReceiptAsync(group, route, descriptor, vault, cancellationToken);
                    continue;
                }
                // Missing local parents do not establish a fork. Fetch and
                // verify bounded manifest pages before deciding whether a
                // returning receiver can advance its ordinary recovery head.
                if (existing is not null &&
                    !WorldPeerFileTransfer.KnownDescendant(vault, existing, version) &&
                    !await StagePeerHistoryAsync(group, route, descriptor, vault, cancellationToken)) return;
                if (existing is not null && WorldPeerFileTransfer.KnownDescendant(vault, version, existing)) continue;
                // A peer can preserve another signed history, but cannot silently
                // replace the current received head with a fork or an older copy.
                if (!WorldPeerFileTransfer.KnownDescendant(vault, existing, version))
                {
                    var retained = SharedWorldService.SafeChild(RetainedHistoryParent(data, group), version.VersionHash);
                    SharedWorldService.EnsureUnlinkedRoot(data.RootPath, retained);
                    await FriendLink.ReceiveGateFor(retained).WaitAsync(cancellationToken);
                    try
                    {
                        var kept = ReadPeerPointer(retained);
                        if (kept?.VersionHash == version.VersionHash)
                        {
                            await RetryRetainedReceiptAsync(group, route, descriptor, retained, cancellationToken);
                            continue;
                        }
                        await ReceivePeerSaveAsync(group, route, descriptor, retained, kept, cancellationToken);
                    }
                    finally { FriendLink.ReceiveGateFor(retained).Release(); }
                    return;
                }
                await ReceivePeerSaveAsync(group, route, descriptor, vault, existing, cancellationToken);
                return; // One completed payload per group/pass keeps other groups responsive.
            }
            finally { FriendLink.ReceiveGateFor(vault).Release(); }
        }
        var tests = catalog.Saves.Where(item => item.TestOnly).ToArray();
        if (tests.Length > 0)
        {
            var position = liveTestPositions.GetValueOrDefault(group.Root.GroupKey) % tests.Length;
            liveTestPositions[group.Root.GroupKey] = (position + 1) % tests.Length;
            await PullLiveTestAsync(group, route, tests[position], cancellationToken);
        }
    }

    private async Task<bool> StagePeerHistoryAsync(Group group, SharedWorldPeerRoute route,
        WorldPeerSaveDescriptor descriptor, string vault, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(vault);
        var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
        return await WorldPeerFileTransfer.StageDescriptorHistoryAsync(group.Root, vault, descriptor, records,
            async (hash, number, token) =>
            {
                if (!await ReceiveAllowedAsync(group, token)) throw new OperationCanceledException("Save consent changed.");
                var manifest = await SendPeerAsync(group, route, WorldPeerOperations.Version, token,
                    versionHash: hash, chunkOffset: number);
                if (!await ReceiveAllowedAsync(group, token)) throw new OperationCanceledException("Save consent changed.");
                return JsonSerializer.Deserialize<SharedWorldVersion>(manifest, Json);
            }, cancellationToken);
    }

    private async Task ReceivePeerSaveAsync(Group group, SharedWorldPeerRoute route,
        WorldPeerSaveDescriptor descriptor, string vault, SharedWorldVersion? existing,
        CancellationToken cancellationToken)
    {
        var version = descriptor.Version;
        var observation = transferAttempt.Value?.Attempt;
        observation?.Phase(WorldPeerTransferPhase.Pulling);
        observation?.Expected(version.Files.Sum(file => file.Length), version.Files.Count, version.Number);
        Directory.CreateDirectory(vault);
        var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
        var anchor = descriptor.Branch?.Offer.Version ?? WorldPeerFileTransfer.TrustedHistoricAnchor(group.Root, version, records);
        if (!await StagePeerHistoryAsync(group, route, descriptor, vault, cancellationToken)) return;
        if (descriptor.Branch is not null && !WorldPeerFileTransfer.KnownDescendant(vault, existing, version))
            throw new InvalidDataException("The peer branch does not extend the completed local branch; both signed histories were kept.");
        var total = SharedWorldService.BoundedTotalBytes(version.Files);
        if (new DriveInfo(Path.GetPathRoot(vault)!).AvailableFreeSpace < total * 2 + 1024L * 1024 * 1024)
            throw new IOException("The peer copy needs space for complete verification and the disk reserve.");
        var stage = SharedWorldService.SafeChild(vault, ".partial-" + version.VersionHash);
        Directory.CreateDirectory(stage);
        var payloadRoot = Path.Combine(stage, SharedWorldService.PayloadDirectory);
        Directory.CreateDirectory(payloadRoot);
        long transferred = 0;
        for (var index = 0; index < version.Files.Count; index++)
        {
            var file = version.Files[index];
            var path = SharedWorldService.SafeChild(payloadRoot, file.Path);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            if (output.Length == file.Length) continue;
            var resume = output.Length <= file.Length ? output.Length / SharedWorldService.ChunkBytes * SharedWorldService.ChunkBytes : 0;
            output.SetLength(resume); output.Position = resume;
            while (output.Position < file.Length)
            {
                if (!await ReceiveAllowedAsync(group, cancellationToken)) throw new OperationCanceledException("Save consent changed.");
                observation?.Requested(index, output.Position);
                var chunk = await SendPeerAsync(group, route, WorldPeerOperations.Chunk, cancellationToken,
                    versionHash: version.VersionHash, fileIndex: index, chunkOffset: output.Position);
                if (!await ReceiveAllowedAsync(group, cancellationToken)) throw new OperationCanceledException("Save consent changed.");
                if (chunk.Length != Math.Min(SharedWorldService.ChunkBytes, file.Length - output.Position))
                    throw new InvalidDataException("The peer save chunk length changed.");
                await output.WriteAsync(chunk, cancellationToken);
                observation?.Received(chunk.Length);
                if (output.Position == file.Length) observation?.FileCompleted();
                transferred += chunk.Length;
                if (transferred >= WorldPeerFileTransfer.MaximumPayloadBytesPerPass &&
                    (output.Position < file.Length || index < version.Files.Count - 1)) return;
            }
        }
        observation?.Phase(WorldPeerTransferPhase.VerifyingLocal);
        var receipt = await CompletePeerSaveAsync(group, descriptor, vault, stage, anchor, cancellationToken);
        if (receipt is not null)
            await DeliverReceiptAsync(group, route, receipt, JsonSerializer.SerializeToUtf8Bytes(receipt, Json), cancellationToken);
    }

    // Pull and outbound-only seeding use the same full-payload verification,
    // immutable lease, current consent and exact signed pointer promotion.
    private async Task<SharedWorldReceipt?> CompletePeerSaveAsync(Group group, WorldPeerSaveDescriptor descriptor,
        string vault, string stage, SharedWorldVersion? anchor, CancellationToken cancellationToken,
        Func<bool>? additionalAuthorization = null)
    {
        var version = descriptor.Version;
        var destination = SharedWorldService.SafeChild(vault, version.VersionHash);
        if (!Directory.Exists(destination))
            WorldPeerFileTransfer.VerifyStage(data.RootPath, Path.Combine(stage, SharedWorldService.PayloadDirectory), version, cancellationToken);
        if (!await ReceiveAllowedAsync(group, cancellationToken))
            throw new OperationCanceledException("World access changed before copy completion.");
        if (!Directory.Exists(destination))
        {
            File.WriteAllBytes(Path.Combine(stage, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            Directory.Move(stage, destination);
        }
        // Verify existing destinations too, before latest.json can name them.
        // Read leases keep every verified file unchanged through the promotion.
        using var verified = WorldPeerVerifiedCopy.Open(data.RootPath, destination, version, cancellationToken);
        SharedWorldReceipt? receipt = null;
        void Commit()
        {
            lock (stateSync)
            {
                var current = Find(group.Root.GroupKey);
                using var signingKey = current is null ? null : LocalKey(current);
                if (current is null || signingKey is null || !current.ReceiveConsented ||
                    current.DeviceId != group.DeviceId ||
                    Access(current, current.DeviceId, Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo()))?.Receive != true ||
                    !AuthorizedDescriptor(current, descriptor))
                    throw new OperationCanceledException("World membership or save consent changed before promotion.");
                if (additionalAuthorization?.Invoke() == false)
                    throw new InvalidDataException("The forwarding PC's current permission changed before promotion.");
                WorldPeerFileTransfer.VerifyHistory(vault, descriptor, anchor);
                var previousObservation = data.LoadProtected(SaveObservationName(current.Root)) ?? [];
                data.SaveProtected(SaveObservationName(current.Root), SHA256.HashData(
                    previousObservation.Concat(System.Text.Encoding.UTF8.GetBytes(version.VersionHash + "\n" + descriptor.Branch?.BranchHash)).ToArray()));
                WorldPeerFileTransfer.Promote(vault, descriptor);
                var draft = new SharedWorldReceipt(current.DeviceId == Guid.Empty ? SharedWorldReceiptTrust.PeerOwnerSchema : 1,
                    current.Root.GroupId, current.Root.ProfileId, version.VersionHash,
                    current.DeviceId, current.Roster.Epoch, current.Roster.Revision, Guid.NewGuid(), "");
                receipt = draft with
                {
                    Signature = Convert.ToBase64String(signingKey.SignData(
                    current.DeviceId == Guid.Empty ? SharedWorldReceiptTrust.PeerOwnerBasis(draft, current.Root) :
                        SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256))
                };
            }
        }
        if (group.DeviceId == Guid.Empty) Commit();
        else if (!await friend.CommitPeerReceiveAsync(group.Root, group.DeviceId, Commit, cancellationToken))
            throw new OperationCanceledException("Save consent changed before copy completion.");
        if (receipt is not null)
            File.WriteAllBytes(Path.Combine(destination, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
        return receipt;
    }

    private bool ReceiptDeliveryDue(Group group, SharedWorldPeerRoute route, SharedWorldReceipt receipt)
    {
        var id = (group.Root.GroupKey, route.Address, route.CertificateSha256, receipt.ReceiptId);
        lock (stateSync)
            return !receiptDeliveries.TryGetValue(id, out var delivery) ||
                !delivery.Confirmed && DateTimeOffset.UtcNow - delivery.AttemptedUtc >= TimeSpan.FromSeconds(15);
    }

    // A completed copy may have arrived through a forwarding peer, or its first
    // acknowledgement may have been lost. Reuse only its authentic receipt;
    // an unchanged catalog entry does not authorize inventing a new receipt.
    private async Task RetryRetainedReceiptAsync(Group group, SharedWorldPeerRoute route,
        WorldPeerSaveDescriptor descriptor, string vault, CancellationToken cancellationToken)
    {
        if (!await ReceiveAllowedAsync(group, cancellationToken)) return;
        var receipt = await ReadRetainedReceiptMetadataAsync(group, descriptor, vault, cancellationToken);
        if (receipt is null || !ReceiptDeliveryDue(group, route, receipt)) return;
        using var verified = VerifyReceiptPayload(vault, descriptor.Version, cancellationToken);
        var current = await RetainedReceiptForVerifiedCopyAsync(group, descriptor, vault, verified, cancellationToken);
        if (current is null || current != receipt) return;
        await DeliverReceiptAsync(group, route, current, JsonSerializer.SerializeToUtf8Bytes(current, Json), cancellationToken);
    }

    // Validate cheap protected metadata before deciding whether an outbound
    // acknowledgement is due. This alone never authorizes returning a receipt.
    private async Task<SharedWorldReceipt?> ReadRetainedReceiptMetadataAsync(Group group,
        WorldPeerSaveDescriptor descriptor, string vault, CancellationToken cancellationToken)
    {
        if (!await ReceiveAllowedAsync(group, cancellationToken)) return null;
        var destination = SharedWorldService.SafeChild(vault, descriptor.Version.VersionHash);
        var path = Path.Combine(destination, "receipt.json");
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if ((File.GetAttributes(input.SafeFileHandle) & FileAttributes.ReparsePoint) != 0) return null;
        var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(input, input.Length, cancellationToken);
        if (bytes is null) return null;
        var receipt = SharedWorldReceiptTrust.Parse(bytes);
        var current = Find(group.Root.GroupKey);
        using var key = current is null ? null : LocalKey(current);
        if (receipt is null || current is null || key is null || receipt.DeviceId != current.DeviceId ||
            receipt.VersionHash != descriptor.Version.VersionHash || !AuthorizedCopyReceipt(current, receipt) ||
            !AuthorizedDescriptor(current, descriptor)) return null;
        return receipt;
    }

    // Both outbound retries and SeedKnown must hold this exact fully verified
    // copy through the current permission/history checks and acknowledgement.
    private async Task<SharedWorldReceipt?> RetainedReceiptForVerifiedCopyAsync(Group group,
        WorldPeerSaveDescriptor descriptor, string vault, WorldPeerVerifiedCopy verified,
        CancellationToken cancellationToken)
    {
        var destination = SharedWorldService.SafeChild(vault, descriptor.Version.VersionHash);
        if (!verified.Covers(data.RootPath, destination, descriptor.Version))
            throw new InvalidDataException("The receipt requires its exact currently verified copy.");
        var receipt = await ReadRetainedReceiptMetadataAsync(group, descriptor, vault, cancellationToken);
        if (receipt is null) return null;
        var current = Find(group.Root.GroupKey);
        if (current is null) return null;
        var records = new WorldAuthorityStore(data).Read(current.Root.ProfileId);
        var anchor = descriptor.Branch?.Offer.Version ?? WorldPeerFileTransfer.TrustedHistoricAnchor(current.Root, descriptor.Version, records);
        WorldPeerFileTransfer.VerifyHistory(vault, descriptor, anchor);
        if (!await ReceiveAllowedAsync(current, cancellationToken)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateSync)
        {
            current = Find(group.Root.GroupKey);
            return current is not null && current.ReceiveConsented && current.DeviceId == receipt.DeviceId &&
                AuthorizedCopyReceipt(current, receipt) && AuthorizedDescriptor(current, descriptor) ? receipt : null;
        }
    }

    private async Task DeliverReceiptAsync(Group group, SharedWorldPeerRoute route, SharedWorldReceipt receipt,
        byte[] bytes, CancellationToken cancellationToken)
    {
        if (!ReceiptDeliveryDue(group, route, receipt) || !await ReceiveAllowedAsync(group, cancellationToken)) return;
        transferAttempt.Value?.Attempt.Phase(WorldPeerTransferPhase.Receipt);
        var id = (group.Root.GroupKey, route.Address, route.CertificateSha256, receipt.ReceiptId);
        lock (stateSync)
        {
            if (receiptDeliveries.Count >= 512 && !receiptDeliveries.ContainsKey(id))
                receiptDeliveries.Remove(receiptDeliveries.MinBy(item => item.Value.AttemptedUtc).Key);
            receiptDeliveries[id] = new(DateTimeOffset.UtcNow, false);
        }
        var reply = await SendPeerAsync(group, route, WorldPeerOperations.Receipt, cancellationToken, bytes,
            versionHash: receipt.VersionHash);
        var result = JsonSerializer.Deserialize<WorldPeerActionResult>(reply, Json);
        if (result is { Ok: true, Code: "PeerReceiptStored" or "PeerReceiptKnown" })
            lock (stateSync) receiptDeliveries[id] = new(DateTimeOffset.UtcNow, true);
    }

    private static string SessionBranchHash(Group group, WorldPeerSaveDescriptor descriptor) =>
        descriptor.Branch?.BranchHash ?? descriptor.SessionBranchHash ?? NormalSessionBranchHash(group);

    private static string NormalSessionBranchHash(Group group) => Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"TogetherServer world hosting lane v1\n{group.Root.GroupKey}\n{group.DeviceId:N}")));

    private async Task<WorldPeerAdvertisement> BuildAdvertisementAsync(Group group,
        CancellationToken cancellationToken, bool retainedRoutesOnly = false)
    {
        using var key = retainedRoutesOnly ? RetainedAdvertisementKey(group) : LocalKey(group);
        if (key is null) return new([], [], [], []);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var access = Access(group, group.DeviceId, publicKey);
        var snapshot = await manager.SnapshotAsync();
        var previous = Store(group).Project();
        var routes = retainedRoutesOnly ? previous.Peers.Where(peer => peer.DeviceId == group.DeviceId && peer.PublicKey == publicKey)
            .SelectMany(peer => peer.Routes).ToList() : new List<SharedWorldPeerRoute>();
        // A signed configured contact is an address hint, not listener or
        // reachability evidence. Retaining it while incoming connections are
        // off lets an outbound-only receiver discover this PC after its local
        // owner explicitly enables hosting, even with the original Host offline.
        if (!retainedRoutesOnly && group.SharingEnabled && HostIdentity.TryEndpoint(snapshot.Settings.CompanionEndpoint, out var configured) &&
            configured.Port == snapshot.Settings.CompanionPort &&
            System.Net.IPAddress.TryParse(configured.Host, out var advertisedAddress) &&
            System.Net.IPAddress.TryParse(snapshot.Settings.CompanionBindAddress, out var boundAddress) &&
            !System.Net.IPAddress.IsLoopback(boundAddress) &&
            (boundAddress.Equals(advertisedAddress) ||
             boundAddress.Equals(System.Net.IPAddress.Any) && advertisedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
             boundAddress.Equals(System.Net.IPAddress.IPv6Any) && advertisedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
        {
            using var certificate = new HostIdentity(data).Load() ?? new HostIdentity(data).Ensure(snapshot.Settings.CompanionEndpoint);
            if (certificate is not null)
            {
                var route = new SharedWorldPeerRoute(snapshot.Settings.CompanionEndpoint.TrimEnd('/'),
                    HostIdentity.Fingerprint(certificate));
                if (SharedWorldPeerTrust.ValidRoute(route)) routes.Add(route);
            }
        }
        var local = await LocalSavesAsync(group, cancellationToken, retainedRoutesOnly ? publicKey : null);
        var branches = new List<SharedWorldPeerBranch>();
        var sessions = new List<SharedWorldPeerSession>();
        var producedLanes = local.Where(item => item.ProducedHere)
            .Select(item => SessionBranchHash(group, item.Descriptor)).ToHashSet(StringComparer.Ordinal);
        // A newly restored branch still holds its owner's signed base until
        // its first publication. Its locally signed hosting lane is already
        // valid metadata; the retained base remains received payload below.
        // A received older copy of our own lane remains available payload, but
        // must not duplicate the lane's current local publication in metadata.
        foreach (var item in local.Where(item => item.ProducedHere ||
            item.Descriptor.Branch is { } branch &&
            branch.Offer.Proposal.ProposerDeviceId == group.DeviceId &&
            branch.CandidatePublicKey == publicKey &&
            !producedLanes.Contains(SessionBranchHash(group, item.Descriptor)) &&
            SharedWorldSeparateCopyStore.Verify(branch)))
        {
            var descriptor = item.Descriptor;
            var hash = SessionBranchHash(group, descriptor);
            var oldBranch = previous.Branches.FirstOrDefault(branch => branch.DeviceId == group.DeviceId &&
                branch.PublicKey == publicKey && branch.Branch.BranchHash == hash)?.Branch;
            branches.Add(new(hash, descriptor.Branch?.Offer.Version.VersionHash ?? oldBranch?.BaseVersionHash ??
                descriptor.Version.VersionHash, descriptor.Version.VersionHash, descriptor.Version.ParentHash,
                descriptor.Version.Number));
            var profiles = snapshot.Settings.Profiles.Where(profile => descriptor.Branch is { } branch
                ? profile.SeparateCopySourceProfileId == group.Root.ProfileId && profile.SeparateCopyBranchHash == branch.BranchHash
                : profile.Id == group.Root.ProfileId && profile.SeparateCopySourceProfileId is null);
            foreach (var profile in profiles)
            {
                var run = snapshot.Runs.SingleOrDefault(run => run.ProfileId == profile.Id);
                if (run?.RunOperationId is not { } runId) continue;
                var state = run.State == "Ready" ? "Hosting" :
                    run.State is "Starting" or "Process running" or "Listening" ? "Preparing" : "Interrupted";
                var old = previous.Sessions.FirstOrDefault(session => session.DeviceId == group.DeviceId &&
                    session.PublicKey == publicKey && session.Session.SessionId == runId)?.Session;
                var revision = old is null ? 1 : old.State == state && old.HeadVersionHash == descriptor.Version.VersionHash
                    ? old.Revision : old.Revision + 1;
                sessions.Add(new(runId, hash, revision, state, branches[^1].BaseVersionHash,
                    descriptor.Version.VersionHash, old?.StartedUtc ?? DateTimeOffset.UtcNow));
            }
        }
        foreach (var old in previous.Sessions.Where(item => item.DeviceId == group.DeviceId &&
            item.PublicKey == publicKey).Select(item => item.Session))
        {
            if (sessions.Any(item => item.SessionId == old.SessionId)) continue;
            var finalBranch = branches.FirstOrDefault(branch => branch.BranchHash == old.BranchHash &&
                branch.BaseVersionHash == old.BaseVersionHash);
            // Interrupted with no end is a still-managed uncertain session,
            // not an immutable terminal record. A later completed Stop closes
            // it with the same final head as a previously Ready session.
            var closed = old.State == "Stopped" || old.State == "Interrupted" && old.EndedUtc is not null;
            var finalHead = old.HeadVersionHash;
            if (!closed && finalBranch is not null)
            {
                var candidate = local.FirstOrDefault(item =>
                    SessionBranchHash(group, item.Descriptor) == old.BranchHash &&
                    item.Descriptor.Version.VersionHash == finalBranch.HeadVersionHash);
                if (candidate is not null)
                    try
                    {
                        // A missing publication can expose the retained older
                        // base, and a signed higher counter can still be a fork.
                        // Only exact signed ancestry may advance this session.
                        _ = await WorldPeerLatestSelection.SelectAsync(group.Root, [candidate.Descriptor],
                            [old.HeadVersionHash ?? old.BaseVersionHash], (_, _) => Task.CompletedTask,
                            (hash, number, token) => FindManifestAsync(group, hash, number, token,
                                retainedRoutesOnly ? local : null), cancellationToken);
                        finalHead = candidate.Descriptor.Version.VersionHash;
                    }
                    catch (Exception error) when (PeerFailure(error))
                    { /* Preserve the already announced head when descent cannot be proved. */ }
            }
            var stopped = closed ? old : old with
            {
                Revision = old.Revision + 1,
                State = "Stopped",
                EndedUtc = DateTimeOffset.UtcNow,
                HeadVersionHash = finalHead
            };
            sessions.Add(stopped);
            if (branches.All(branch => branch.BranchHash != stopped.BranchHash))
            {
                var retained = previous.Branches.FirstOrDefault(item => item.DeviceId == group.DeviceId &&
                    item.PublicKey == publicKey && item.Branch.BranchHash == stopped.BranchHash)?.Branch;
                if (retained is not null) branches.Add(retained);
            }
        }
        var available = local.Where(item => item.ProducedHere || group.ReceiveConsented &&
                access is { Receive: true, Reshare: true })
            .Select(item => item.Descriptor.Version.VersionHash).Distinct().Take(128).ToArray();
        if (access is not { Receive: true, EligibleHost: true }) { sessions.Clear(); branches.Clear(); }
        var keptBranches = branches.Take(32).ToArray();
        return new(routes, sessions.Where(session => keptBranches.Any(branch => branch.BranchHash == session.BranchHash &&
            branch.BaseVersionHash == session.BaseVersionHash)).TakeLast(32).ToArray(), keptBranches,
            access?.Receive == true ? available : []);
    }
}

// An inventory is computed once against the signed whole-file digest. Every
// returned chunk is checked against that inventory, including after a same-size
// replacement or an edit that preserves timestamps. Cache eviction affects only
// performance; it cannot turn changed bytes into a valid chunk.
internal sealed class WorldPeerChunkInventory
{
    private const int MaximumEntries = 16;
    private const long MaximumHashBytes = 32L * 1024 * 1024;
    private readonly object sync = new();
    private readonly Dictionary<(string Hash, long Length), byte[][]> entries = [];
    private long hashBytes;
    internal long FullyHashedBytes { get; private set; }

    internal byte[] Read(string dataRoot, string path, SharedWorldFile file, long offset,
        CancellationToken cancellationToken)
    {
        SharedWorldService.EnsureUnlinkedRoot(dataRoot, path);
        if (offset < 0 || offset >= file.Length || offset % SharedWorldService.ChunkBytes != 0 ||
            file.Length > SharedWorldService.MaximumSharedWorldBytes)
            throw new InvalidDataException("The peer chunk offset is invalid.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != file.Length ||
            (File.GetAttributes(input.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The shared peer file is linked or changed.");
        byte[][] hashes;
        lock (sync)
        {
            var key = (file.Sha256.ToUpperInvariant(), file.Length);
            if (!entries.TryGetValue(key, out hashes!))
            {
                var count = checked((int)((file.Length + SharedWorldService.ChunkBytes - 1) / SharedWorldService.ChunkBytes));
                hashes = new byte[count][];
                var buffer = new byte[SharedWorldService.ChunkBytes];
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = (int)Math.Min(buffer.Length, file.Length - input.Position);
                    input.ReadExactly(buffer.AsSpan(0, length));
                    digest.AppendData(buffer.AsSpan(0, length));
                    hashes[index] = SHA256.HashData(buffer.AsSpan(0, length));
                }
                if (!Convert.ToHexString(digest.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The shared peer file digest changed.");
                FullyHashedBytes += file.Length;
                var cost = (long)hashes.Length * 40;
                while (entries.Count > 0 && (entries.Count >= MaximumEntries || hashBytes + cost > MaximumHashBytes))
                {
                    var evicted = entries.First();
                    hashBytes -= (long)evicted.Value.Length * 40;
                    entries.Remove(evicted.Key);
                }
                entries.Add(key, hashes);
                hashBytes += cost;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        input.Position = offset;
        var bytes = new byte[(int)Math.Min(SharedWorldService.ChunkBytes, file.Length - offset)];
        input.ReadExactly(bytes);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), hashes[checked((int)(offset / SharedWorldService.ChunkBytes))]))
            throw new InvalidDataException("The shared peer chunk changed.");
        return bytes;
    }
}

internal static class WorldPeerFileTransfer
{
    internal const long MaximumPayloadBytesPerPass = 64L * 1024 * 1024;
    private sealed record HistoryCursor(int Schema, string HeadHash, string? AnchorHash,
        long Number, string VersionHash);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static SharedWorldVersion? TrustedHistoricAnchor(SharedWorldPeerRoot root,
        SharedWorldVersion version, IReadOnlyList<WorldAuthorityRecord> records)
    {
        if (version.SigningPublicKey == root.OwnerPublicKey) return null;
        // These records were read through the protected, owner-rooted authority
        // store. An old authorized signer can preserve a divergent history; this
        // does not select that history as the group's current hosting authority.
        return records.Where(record => record.Roster.OwnerPublicKey == root.OwnerPublicKey &&
                record.Proposal.GroupId == root.GroupId && record.Proposal.ProfileId == root.ProfileId &&
                WorldAuthorityTrust.Verify(record) && record.Version.Game == version.Game &&
                record.Version.WorldId == version.WorldId &&
                (record.Version.VersionHash == version.VersionHash ||
                 record.Proposal.CandidatePublicKey == version.SigningPublicKey &&
                 record.Version.Number < version.Number &&
                 (record.Version.Number + 1 != version.Number || version.ParentHash == record.Version.VersionHash)))
            .OrderByDescending(record => record.Version.Number).Select(record => record.Version).FirstOrDefault();
    }

    internal static bool TrustedHistoricSigner(SharedWorldPeerRoot root, SharedWorldVersion version,
        IReadOnlyList<WorldAuthorityRecord> records) => version.ProfileId == root.ProfileId &&
        version.GroupId == root.GroupId && SharedWorldService.VerifySignature(version) &&
        (version.SigningPublicKey == root.OwnerPublicKey || TrustedHistoricAnchor(root, version, records) is not null);

    internal static bool KnownDescendant(string vault, SharedWorldVersion? existing, SharedWorldVersion version)
    {
        if (existing is null || existing.VersionHash == version.VersionHash) return true;
        if (existing.Number >= version.Number) return false;
        var current = version;
        while (current.Number > existing.Number)
        {
            if (current.Number == existing.Number + 1) return current.ParentHash == existing.VersionHash;
            if (current.ParentHash is null) return false;
            var previous = FriendLink.ReadReceivedManifest(vault, current.Number - 1, current.ParentHash);
            if (previous is null) return false;
            current = previous;
        }
        return current.VersionHash == existing.VersionHash;
    }

    // Carry metadata ancestry through every hosting continuation, including
    // source histories whose older payloads are no longer on the sending PC.
    // Without these signed intermediate manifests a returning member could
    // not prove that C includes B and the original A save.
    internal static async Task<bool> StageDescriptorHistoryAsync(SharedWorldPeerRoot root, string vault,
        WorldPeerSaveDescriptor descriptor, IReadOnlyList<WorldAuthorityRecord> records,
        Func<string, long, CancellationToken, Task<SharedWorldVersion?>> fetch,
        CancellationToken cancellationToken)
    {
        var current = descriptor.Version;
        var branch = descriptor.Branch;
        for (var depth = 0; depth <= 16; depth++)
        {
            var anchor = branch?.Offer.Version ?? TrustedHistoricAnchor(root, current, records);
            if (!await StageHistoryAsync(root, vault, current, anchor,
                (hash, number, token) => anchor?.VersionHash == hash && anchor.Number == number ? Task.FromResult<SharedWorldVersion?>(anchor) :
                    fetch(hash, number, token), cancellationToken)) return false;
            VerifyHistory(vault, new(current, branch), anchor);
            if (branch is null) return true;
            current = branch.Offer.Version;
            branch = branch.SourceBranch;
        }
        throw new InvalidDataException("The continuation ancestry exceeds its verified depth.");
    }

    internal static async Task<bool> StageHistoryAsync(SharedWorldPeerRoot root, string vault,
        SharedWorldVersion head, SharedWorldVersion? anchor,
        Func<string, long, CancellationToken, Task<SharedWorldVersion?>> fetch,
        CancellationToken cancellationToken)
    {
        var cursorPath = SharedWorldService.SafeChild(vault, ".peer-history-" + head.VersionHash + ".json");
        FriendLink.KeepSignedManifest(vault, head);
        var current = head;
        if (File.Exists(cursorPath))
        {
            if ((File.GetAttributes(cursorPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(cursorPath).Length > 1024)
                throw new InvalidDataException("The peer history cursor is linked or oversized.");
            var cursor = JsonSerializer.Deserialize<HistoryCursor>(File.ReadAllBytes(cursorPath), Json);
            if (cursor is { Schema: 1 } && cursor.HeadHash == head.VersionHash && cursor.AnchorHash == anchor?.VersionHash &&
                cursor.Number >= 1 && cursor.Number <= head.Number && SharedWorldPeerTrust.ValidHash(cursor.VersionHash))
                current = FriendLink.ReadReceivedManifest(vault, cursor.Number, cursor.VersionHash) ?? head;
        }
        for (var work = 0; work < WorldAuthorityTrust.ChainVersionsPerCheck; work++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FriendLink.KeepSignedManifest(vault, current);
            if (anchor is null ? current.Number == 1 : current.VersionHash == anchor.VersionHash)
                return true;
            if (current.ParentHash is null || current.Number <= 1 || anchor is not null && current.Number <= anchor.Number)
                throw new InvalidDataException("The peer save does not reach its trusted history boundary.");
            var previous = FriendLink.ReadReceivedManifest(vault, current.Number - 1, current.ParentHash) ??
                await fetch(current.ParentHash, current.Number - 1, cancellationToken);
            if (previous is null || previous.ProfileId != root.ProfileId || previous.GroupId != root.GroupId ||
                !SharedWorldService.VerifySignature(previous) || previous.Number != current.Number - 1 ||
                previous.VersionHash != current.ParentHash || previous.Game != head.Game || previous.WorldId != head.WorldId)
                throw new InvalidDataException("The peer save parent changed.");
            FriendLink.KeepSignedManifest(vault, previous);
            current = previous;
            var temporary = cursorPath + ".new";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new HistoryCursor(1,
                head.VersionHash, anchor?.VersionHash, current.Number, current.VersionHash), Json));
            File.Move(temporary, cursorPath, true);
        }
        // The next attempt begins at this exact signed cursor, not at the head.
        // A cursor is only a performance hint: VerifyHistory always walks the
        // complete signed head-to-boundary chain again before pointer promotion.
        return anchor is null ? current.Number == 1 : current.VersionHash == anchor.VersionHash;
    }

    internal static void VerifyHistory(string vault, WorldPeerSaveDescriptor descriptor, SharedWorldVersion? anchor)
    {
        var head = descriptor.Version;
        if (anchor?.VersionHash == head.VersionHash)
        {
            if (descriptor.Branch is { } baseBranch && !SharedWorldSeparateCopyStore.VerifySnapshotVersion(baseBranch, head))
                throw new InvalidDataException("The recovery copy's signed base changed.");
            return;
        }
        foreach (var version in FriendLink.ReadVerifiedReceivedManifestLineage(vault, head, anchor))
            if (descriptor.Branch is { } branch ? !SharedWorldSeparateCopyStore.VerifySnapshotVersion(branch, version) :
                version.SigningPublicKey != head.SigningPublicKey)
                throw new InvalidDataException("The peer save does not extend its complete authorized history.");
    }

    internal static void VerifyStage(string dataRoot, string payloadRoot, SharedWorldVersion version,
        CancellationToken cancellationToken = default, Action<long, long, int, int>? progress = null)
    {
        var total = SharedWorldService.BoundedTotalBytes(version.Files);
        long verified = 0;
        var files = 0;
        foreach (var file in version.Files)
        {
            var path = SharedWorldService.SafeChild(payloadRoot, file.Path);
            SharedWorldService.EnsureUnlinkedRoot(dataRoot, path);
            try
            {
                WorldPeerPayloadHash.VerifyFile(path, file, cancellationToken,
                    bytes => WorldPeerPayloadHash.Report(progress, verified + bytes, total, files, version.Files.Count));
                verified += file.Length;
                WorldPeerPayloadHash.Report(progress, verified, total, ++files, version.Files.Count);
            }
            catch (InvalidDataException)
            {
                // Only a disposable partial file is reset. A corrupt completed
                // destination is preserved for review and never becomes latest.
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    using (var reset = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None)) { }
                throw;
            }
        }
    }

    // Caller holds the received-vault lock, source FriendLink consent gate and
    // current signed membership mutation lock, plus a WorldPeerVerifiedCopy lease.
    internal static void Promote(string vault, WorldPeerSaveDescriptor descriptor)
    {
        if (descriptor.Branch is not null)
        {
            var branchTemp = Path.Combine(vault, "branch.json.new");
            File.WriteAllBytes(branchTemp, JsonSerializer.SerializeToUtf8Bytes(descriptor.Branch, Json));
            File.Move(branchTemp, Path.Combine(vault, "branch.json"), true);
        }
        var temporary = Path.Combine(vault, "latest.json.new");
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(descriptor.Version, Json));
        File.Move(temporary, Path.Combine(vault, "latest.json"), true);
    }
}

// All completed payload files remain open without write/delete sharing until
// latest.json is promoted. The verification includes a pre-existing destination,
// and does not depend on latest.json already pointing at that destination.
internal sealed class WorldPeerVerifiedCopy : IDisposable
{
    private readonly List<FileStream> files = [];
    private readonly List<SafeFileHandle> directories = [];
    private string? verifiedRoot;
    private string? verifiedDirectory;
    private string? verifiedHash;

    internal bool Covers(string dataRoot, string destination, SharedWorldVersion version) =>
        files.Count > 0 && verifiedHash == version.VersionHash &&
        string.Equals(verifiedRoot, Path.GetFullPath(dataRoot), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(verifiedDirectory, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);

    internal static WorldPeerVerifiedCopy Open(string dataRoot, string destination,
        SharedWorldVersion version, CancellationToken cancellationToken,
        Action<long, long, int, int>? progress = null)
    {
        var lease = new WorldPeerVerifiedCopy();
        try
        {
            SharedWorldService.EnsureUnlinkedRoot(dataRoot, destination);
            var parent = OpenAnchor(dataRoot, 0x80000000, 3 /* read/write, no delete */,
                IntPtr.Zero, 3, 0x02200000 /* directory + open reparse point */, IntPtr.Zero);
            lease.directories.Add(parent);
            if (parent.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((File.GetAttributes(parent) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("The peer storage anchor is not a plain directory.");
            foreach (var part in Path.GetRelativePath(dataRoot, destination).Split(Path.DirectorySeparatorChar))
            {
                parent = NativeSnapshotDirectory.OpenDirectory(parent, part, namespaceWrites: true);
                lease.directories.Add(parent);
            }
            var metadata = new FileStream(NativeSnapshotDirectory.OpenReadFile(parent, "version.json"), FileAccess.Read);
            lease.files.Add(metadata);
            if (metadata.Length > SharedWorldService.MaximumManifestBytes ||
                (File.GetAttributes(metadata.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The completed peer manifest is linked or oversized.");
            var recorded = JsonSerializer.Deserialize<SharedWorldVersion>(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (recorded?.VersionHash != version.VersionHash || !SharedWorldService.VerifySignature(recorded))
                throw new InvalidDataException("The completed peer manifest changed.");
            var payload = NativeSnapshotDirectory.OpenDirectory(parent, SharedWorldService.PayloadDirectory, namespaceWrites: true);
            lease.directories.Add(payload);
            var opened = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase) { [""] = payload };
            var total = SharedWorldService.BoundedTotalBytes(version.Files);
            long verified = 0;
            var fileCount = 0;
            foreach (var file in version.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parts = file.Path.Split('/');
                var relative = "";
                var directory = payload;
                for (var index = 0; index < parts.Length - 1; index++)
                {
                    relative += "/" + parts[index];
                    if (!opened.TryGetValue(relative, out var child))
                    {
                        child = NativeSnapshotDirectory.OpenDirectory(directory, parts[index], namespaceWrites: true);
                        opened.Add(relative, child); lease.directories.Add(child);
                    }
                    directory = child;
                }
                var stream = new FileStream(NativeSnapshotDirectory.OpenReadFile(directory, parts[^1]), FileAccess.Read);
                lease.files.Add(stream);
                if (stream.Length != file.Length || (File.GetAttributes(stream.SafeFileHandle) & FileAttributes.ReparsePoint) != 0 ||
                    !WorldPeerPayloadHash.Hash(stream, cancellationToken,
                        bytes => WorldPeerPayloadHash.Report(progress, verified + bytes, total, fileCount, version.Files.Count))
                        .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The completed peer payload failed full verification.");
                verified += file.Length;
                WorldPeerPayloadHash.Report(progress, verified, total, ++fileCount, version.Files.Count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            lease.verifiedRoot = Path.GetFullPath(dataRoot);
            lease.verifiedDirectory = Path.GetFullPath(destination);
            lease.verifiedHash = version.VersionHash;
            return lease;
        }
        catch (Win32Exception error)
        {
            lease.Dispose();
            throw new IOException("The completed peer copy namespace could not be held for verification.", error);
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        verifiedHash = null;
        for (var index = files.Count - 1; index >= 0; index--) files[index].Dispose();
        files.Clear();
        for (var index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
        directories.Clear();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenAnchor(string path, uint access, uint sharing,
        IntPtr security, uint creation, uint flags, IntPtr template);
}

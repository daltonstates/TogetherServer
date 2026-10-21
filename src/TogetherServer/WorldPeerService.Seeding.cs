using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record WorldPeerSeedRequest(WorldPeerSaveDescriptor Descriptor,
    SharedWorldVersion? Manifest = null, byte[]? Chunk = null);
internal sealed record WorldPeerSeedResponse(string Code, string VersionHash,
    string? ManifestHash = null, long? ManifestNumber = null,
    int? FileIndex = null, long? ChunkOffset = null, SharedWorldReceipt? Receipt = null);

internal sealed partial class WorldPeerService
{
    // A receiver does not need an incoming listener to forward its verified
    // copy. It connects outward to an already enabled member's pinned listener.
    // The receiving PC chooses each exact missing signed manifest/chunk; the
    // sender never submits a path, command, live world or installation setting.
    private async Task SeedPeerAsync(Group group, SharedWorldPeerRoute route, CancellationToken cancellationToken)
    {
        using var key = LocalKey(group);
        if (key is null || !await ReceiveAllowedAsync(group, cancellationToken) ||
            Access(group, group.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) is not { Receive: true, Reshare: true }) return;
        var catalog = await LocalCatalogAsync(group, cancellationToken, includeTests: false);
        // Prefer a continuation before its older original-source copy. This is
        // transfer scheduling only; selection still requires verified ancestry.
        foreach (var descriptor in catalog.Saves.OrderByDescending(item => item.Version.Number))
        {
            var response = await SendSeedAsync(group, route, descriptor, null, null, null, null, cancellationToken);
            long sent = 0;
            for (var work = 0; work < 256; work++)
            {
                if (response.VersionHash != descriptor.Version.VersionHash)
                    throw new InvalidDataException("The peer seed response changed the offered save.");
                if (response.Code == "SeedKnown") break;
                if (response.Code == "SeedComplete")
                {
                    if (response.Receipt is { } receipt)
                    {
                        var current = Find(group.Root.GroupKey) ?? throw new InvalidDataException("World membership changed.");
                        var accepted = AcceptReceipt(current, JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
                        if (!accepted.Ok) throw new InvalidDataException("The seeded-copy receipt did not verify.");
                    }
                    return; // One completed payload per peer/pass.
                }
                if (response.Code == "SeedManifest" && response.ManifestHash is { } hash && response.ManifestNumber is { } number)
                {
                    var manifest = await FindManifestAsync(group, hash, number, cancellationToken);
                    if (manifest is null) return;
                    response = await SendSeedAsync(group, route, descriptor, manifest, null, null, null, cancellationToken);
                }
                else if (response.Code == "SeedChunk" && response.FileIndex is { } file && response.ChunkOffset is { } offset)
                {
                    var bytes = await ReadChunkAsync(group, descriptor.Version.VersionHash, file, offset, cancellationToken);
                    if (bytes is null) return;
                    response = await SendSeedAsync(group, route, descriptor, null, bytes, file, offset, cancellationToken);
                    sent += bytes.Length;
                    if (sent >= WorldPeerFileTransfer.MaximumPayloadBytesPerPass) return;
                }
                else if (response.Code is "SeedPaused" or "SeedHistoryPending") return;
                else throw new InvalidDataException("The peer returned an invalid fixed seed request.");
            }
        }
    }

    private async Task<WorldPeerSeedResponse> SendSeedAsync(Group group, SharedWorldPeerRoute route,
        WorldPeerSaveDescriptor descriptor, SharedWorldVersion? manifest, byte[]? bytes,
        int? fileIndex, long? offset, CancellationToken cancellationToken)
    {
        if (!await ReceiveAllowedAsync(group, cancellationToken)) throw new OperationCanceledException("Save consent changed.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new WorldPeerSeedRequest(descriptor, manifest, bytes), Json);
        var response = await SendPeerAsync(group, route, WorldPeerOperations.Seed, cancellationToken, payload,
            versionHash: descriptor.Version.VersionHash, fileIndex: fileIndex, chunkOffset: offset);
        return WorldPeerProtocol.DecodeEnvelope<WorldPeerSeedResponse>(response, WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Seed));
    }

    private async Task<WorldPeerSeedResponse> AcceptSeedAsync(Group group, WorldPeerRequest request,
        WorldPeerSeedRequest seed, CancellationToken cancellationToken)
    {
        var descriptor = seed.Descriptor;
        if (descriptor is null || descriptor.TestOnly || descriptor.TestArtifact is not null ||
            !AuthorizedDescriptor(group, descriptor) || descriptor.Version.VersionHash != request.VersionHash ||
            seed.Manifest is not null && seed.Chunk is not null ||
            seed.Chunk is not null && (request.FileIndex is null || request.ChunkOffset is null) ||
            seed.Chunk is null && (request.FileIndex is not null || request.ChunkOffset is not null))
            throw new InvalidDataException("The offered peer save is not an authorized fixed copy.");
        var version = descriptor.Version;
        if (!await ReceiveAllowedAsync(group, cancellationToken)) return new("SeedPaused", version.VersionHash);
        var vault = descriptor.Branch is { } branch ? SharedWorldBranchVault.Root(data, branch) : ReceivedRoot(data, group);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
        await FriendLink.ReceiveGateFor(vault).WaitAsync(cancellationToken);
        try
        {
            var existing = ReadPeerPointer(vault);
            if (existing?.VersionHash == version.VersionHash || existing is not null &&
                WorldPeerFileTransfer.KnownDescendant(vault, version, existing))
            {
                using var verified = WorldPeerVerifiedCopy.Open(data.RootPath, Path.Combine(vault, existing!.VersionHash), existing, cancellationToken);
                return new("SeedKnown", version.VersionHash);
            }
            var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
            var anchor = descriptor.Branch?.Offer.Version ?? WorldPeerFileTransfer.TrustedHistoricAnchor(group.Root, version, records);
            var pending = await StageSeedHistoryAsync(group, descriptor, seed.Manifest, vault, anchor, cancellationToken);
            if (pending is not null) return pending;
            if (existing is not null && !WorldPeerFileTransfer.KnownDescendant(vault, existing, version))
            {
                // A fork is retained in its own exact-hash vault. It cannot
                // replace the ordinary received pointer, even in one lane.
                var retained = SharedWorldService.SafeChild(RetainedHistoryParent(data, group), version.VersionHash);
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, retained);
                await FriendLink.ReceiveGateFor(retained).WaitAsync(cancellationToken);
                try
                {
                    if (ReadPeerPointer(retained)?.VersionHash == version.VersionHash)
                    {
                        using var verified = WorldPeerVerifiedCopy.Open(data.RootPath, Path.Combine(retained, version.VersionHash), version, cancellationToken);
                        return new("SeedKnown", version.VersionHash);
                    }
                    pending = await StageSeedHistoryAsync(group, descriptor, seed.Manifest, retained, anchor, cancellationToken);
                    return pending ?? await AcceptSeedPayloadAsync(group, request, seed, retained, anchor, cancellationToken);
                }
                finally { FriendLink.ReceiveGateFor(retained).Release(); }
            }
            return await AcceptSeedPayloadAsync(group, request, seed, vault, anchor, cancellationToken);
        }
        finally { FriendLink.ReceiveGateFor(vault).Release(); }
    }

    private async Task<WorldPeerSeedResponse?> StageSeedHistoryAsync(Group group, WorldPeerSaveDescriptor descriptor,
        SharedWorldVersion? supplied, string vault, SharedWorldVersion? anchor, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(vault);
        WorldPeerSeedResponse? missing = null;
        try
        {
            var complete = await WorldPeerFileTransfer.StageDescriptorHistoryAsync(group.Root, vault, descriptor,
                new WorldAuthorityStore(data).Read(group.Root.ProfileId),
                async (hash, number, token) =>
                {
                    var version = supplied?.VersionHash == hash && supplied.Number == number ? supplied :
                        await FindManifestAsync(group, hash, number, token);
                    if (version is null) missing = new("SeedManifest", descriptor.Version.VersionHash, hash, number);
                    return version;
                }, cancellationToken);
            if (!complete) return new("SeedHistoryPending", descriptor.Version.VersionHash);
            WorldPeerFileTransfer.VerifyHistory(vault, descriptor, anchor);
            return null;
        }
        catch (InvalidDataException) when (missing is not null) { return missing; }
    }

    private async Task<WorldPeerSeedResponse> AcceptSeedPayloadAsync(Group group, WorldPeerRequest request,
        WorldPeerSeedRequest seed, string vault, SharedWorldVersion? anchor, CancellationToken cancellationToken)
    {
        var descriptor = seed.Descriptor;
        var version = descriptor.Version;
        var total = SharedWorldService.BoundedTotalBytes(version.Files);
        if (new DriveInfo(Path.GetPathRoot(vault)!).AvailableFreeSpace < total * 2 + 1024L * 1024 * 1024)
            return new("SeedPaused", version.VersionHash);
        var stage = SharedWorldService.SafeChild(vault, ".partial-" + version.VersionHash);
        var payload = Path.Combine(stage, SharedWorldService.PayloadDirectory);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, payload);
        Directory.CreateDirectory(payload);
        if (!Directory.Exists(Path.Combine(vault, version.VersionHash)))
            for (var index = 0; index < version.Files.Count; index++)
            {
                var file = version.Files[index];
                var path = SharedWorldService.SafeChild(payload, file.Path);
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                if (output.Length == file.Length) continue;
                var offset = output.Length <= file.Length ? output.Length / SharedWorldService.ChunkBytes * SharedWorldService.ChunkBytes : 0;
                output.SetLength(offset);
                if (seed.Chunk is null || request.FileIndex != index || request.ChunkOffset != offset)
                    return new("SeedChunk", version.VersionHash, FileIndex: index, ChunkOffset: offset);
                if (seed.Chunk.Length != Math.Min(SharedWorldService.ChunkBytes, file.Length - offset))
                    throw new InvalidDataException("The seeded save chunk has the wrong length.");
                if (!SeedSenderAllowed(group, request) || !await ReceiveAllowedAsync(group, cancellationToken))
                    return new("SeedPaused", version.VersionHash);
                output.Position = offset;
                await output.WriteAsync(seed.Chunk, cancellationToken);
                if (output.Position < file.Length)
                    return new("SeedChunk", version.VersionHash, FileIndex: index, ChunkOffset: output.Position);
            }
        var receipt = await CompletePeerSaveAsync(group, descriptor, vault, stage, anchor, cancellationToken,
            () => SeedSenderAllowed(group, request));
        return new("SeedComplete", version.VersionHash, Receipt: receipt);
    }

    private bool SeedSenderAllowed(Group group, WorldPeerRequest request)
    {
        var current = Find(group.Root.GroupKey);
        return current is not null && current.SharingEnabled &&
            Access(current, request.Challenge.DeviceId, request.Challenge.PublicKey) is { Receive: true, Reshare: true };
    }
}

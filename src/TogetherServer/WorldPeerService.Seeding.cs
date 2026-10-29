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
    private readonly PeerPullOwnership inboundPulls = new();
    private static readonly TimeSpan PublicationOfferBudget = TimeSpan.FromSeconds(10);
    private sealed record LocalPublicationDelivery(WorldPeerTransferResult Result, bool PendingProgress,
        WorldPeerSaveDescriptor? Descriptor = null, int? File = null, long? Offset = null);

    private HashSet<string> AcknowledgedByResponder(Group group)
    {
        var responder = transferAttempt.Value;
        var current = Find(group.Root.GroupKey);
        if (current is null || responder?.ResponderPublicKey is null) return [];
        var bytes = data.LoadProtected(ReceiptName(current));
        if (bytes is null) return [];
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Peer receipts need review.");
        var receipts = JsonSerializer.Deserialize<List<SharedWorldReceipt>>(bytes, Json) ?? [];
        if (receipts.Count > 4096) throw new InvalidDataException("Peer receipt capacity needs review.");
        return receipts.Where(receipt => AuthorizedCopyReceipt(current, receipt) &&
            responder.ReceiptFromResponder(current, receipt)).Select(receipt => receipt.VersionHash).ToHashSet(StringComparer.Ordinal);
    }

    // A receiver does not need an incoming listener to forward its verified
    // copy. It connects outward to an already enabled member's pinned listener.
    // The receiving PC chooses each exact missing signed manifest/chunk; the
    // sender never submits a path, command, live world or installation setting.
    private async Task<WorldPeerTransferResult> SeedPeerAsync(Group group, SharedWorldPeerRoute route, CancellationToken cancellationToken)
    {
        var catalog = await LocalCatalogAsync(group, cancellationToken, includeTests: false);
        return await SeedDescriptorsAsync(group, route, catalog.Saves, cancellationToken);
    }

    // One local publication gets a bounded outgoing turn before incoming
    // catch-up. The same flight, exact source fences and receiver-driven Seed
    // conversation still own all bytes; an incoming pull of that exact save
    // keeps its ownership. A stalled offer leaves time for competing history.
    private async Task<LocalPublicationDelivery?> SeedNewestLocalPublicationAsync(Group group,
        SharedWorldPeerRoute route, IReadOnlyList<LocalSave> sources, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(PublicationOfferBudget);
        var progressed = false;
        WorldPeerSaveDescriptor? offered = null;
        int? acceptedFile = null;
        long? acceptedOffset = null;
        try
        {
            // Numbers order transfer work only, never resolve competing histories.
            var publication = sources.Where(item => item.ProducedHere)
                .OrderByDescending(item => item.Descriptor.Version.Number)
                .ThenByDescending(item => item.Descriptor.Version.CreatedUtc).FirstOrDefault();
            if (publication is null) return null;
            var descriptor = publication.Descriptor;
            if (AcknowledgedByResponder(group).Contains(descriptor.Version.VersionHash))
            {
                transferAttempt.Value?.Attempt.ReceiptAccepted(descriptor.Version.Number);
                return null;
            }
            if (transferAttempt.Value?.ResponderPublicKey is { } recipient &&
                inboundPulls.Active(group.Root.GroupKey, descriptor.Version.VersionHash, recipient))
                return new(WorldPeerTransferResult.SeedPaused, false, descriptor);
            offered = descriptor;
            var result = await SeedDescriptorsAsync(group, route, [descriptor], bounded.Token, (file, offset) =>
            {
                progressed = true;
                if (file is not null) { acceptedFile = file; acceptedOffset = offset; }
            });
            var pending = progressed && result is WorldPeerTransferResult.WorkBudget or
                WorldPeerTransferResult.ByteBudget or WorldPeerTransferResult.SeedHistoryPending;
            var incomplete = result is not (WorldPeerTransferResult.CompleteReceiptAccepted or WorldPeerTransferResult.KnownReceiptAccepted);
            // A pull can begin between the initial hint check and the common
            // Seed loop. Its skipped offer remains pending for this exact head.
            if (result == WorldPeerTransferResult.NoPayload) result = WorldPeerTransferResult.SeedPaused;
            return new(result, pending, incomplete ? descriptor : null, acceptedFile, acceptedOffset);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new(progressed ? WorldPeerTransferResult.WorkBudget : WorldPeerTransferResult.SeedPaused,
                progressed, offered, acceptedFile, acceptedOffset);
        }
    }

    private async Task<WorldPeerTransferResult> SeedDescriptorsAsync(Group group, SharedWorldPeerRoute route,
        IReadOnlyList<WorldPeerSaveDescriptor> descriptors, CancellationToken cancellationToken,
        Action<int?, long?>? progress = null)
    {
        using var key = LocalKey(group);
        if (key is null || !await ReceiveAllowedAsync(group, cancellationToken) ||
            Access(group, group.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) is not { Receive: true, Reshare: true })
            return WorldPeerTransferResult.SeedPaused;
        var observationContext = transferAttempt.Value;
        var observation = observationContext?.Attempt;
        var acknowledged = AcknowledgedByResponder(group);
        var outcome = WorldPeerTransferResult.NoPayload;
        // Prefer a continuation before its older original-source copy. This is
        // transfer scheduling only; selection still requires verified ancestry.
        foreach (var descriptor in descriptors.OrderByDescending(item => item.Version.Number))
        {
            if (acknowledged.Contains(descriptor.Version.VersionHash))
            {
                observation?.ReceiptAccepted(descriptor.Version.Number);
                outcome = WorldPeerTransferResult.KnownReceiptAccepted;
                continue;
            }
            if (observationContext?.ResponderPublicKey is { } recipient &&
                inboundPulls.Active(group.Root.GroupKey, descriptor.Version.VersionHash, recipient)) continue;
            observation?.Phase(WorldPeerTransferPhase.Offering);
            observation?.Expected(descriptor.Version.Files.Sum(file => file.Length), descriptor.Version.Files.Count,
                descriptor.Version.Number, descriptor.Version.VersionHash);
            observation?.Requested(null, null);
            var response = await SendSeedAsync(group, route, descriptor, null, null, null, null, cancellationToken);
            long sent = 0;
            for (var work = 0; work < 256; work++)
            {
                if (response.VersionHash != descriptor.Version.VersionHash)
                    throw new InvalidDataException("The peer seed response changed the offered save.");
                if (response.Code is "SeedKnown" or "SeedComplete")
                {
                    if (response.Receipt is { } receipt)
                    {
                        if (receipt.VersionHash != descriptor.Version.VersionHash)
                            throw new InvalidDataException("The seeded-copy receipt names another save.");
                        var current = Find(group.Root.GroupKey) ?? throw new InvalidDataException("World membership changed.");
                        var accepted = AcceptReceipt(current, JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
                        if (!accepted.Ok) throw new InvalidDataException("The seeded-copy receipt did not verify.");
                        // A valid forwarded receipt remains useful, but is not
                        // proof that this route's authenticated recipient has it.
                        if (observationContext?.ReceiptFromResponder(current, receipt) == true)
                        {
                            observation?.ReceiptAccepted(descriptor.Version.Number);
                            outcome = response.Code == "SeedKnown" ? WorldPeerTransferResult.KnownReceiptAccepted :
                                WorldPeerTransferResult.CompleteReceiptAccepted;
                        }
                        else if (outcome == WorldPeerTransferResult.NoPayload) outcome = WorldPeerTransferResult.KnownWithoutReceipt;
                    }
                    else if (outcome == WorldPeerTransferResult.NoPayload) outcome = WorldPeerTransferResult.KnownWithoutReceipt;
                    if (response.Code == "SeedKnown") break;
                    return outcome; // One completed payload per peer/pass.
                }
                if (response.Code == "SeedManifest" && response.ManifestHash is { } hash && response.ManifestNumber is { } number)
                {
                    var manifest = await FindManifestAsync(group, hash, number, cancellationToken);
                    if (manifest is null) return WorldPeerTransferResult.ManifestUnavailable;
                    response = await SendSeedAsync(group, route, descriptor, manifest, null, null, null, cancellationToken);
                    if (response.Code != "SeedPaused" && !(response.Code == "SeedManifest" &&
                        response.ManifestHash == hash && response.ManifestNumber == number)) progress?.Invoke(null, null);
                }
                else if (response.Code == "SeedChunk" && response.FileIndex is { } file && response.ChunkOffset is { } offset)
                {
                    observation?.Phase(WorldPeerTransferPhase.Sending);
                    observation?.Requested(file, offset);
                    var bytes = await ReadChunkAsync(group, descriptor.Version.VersionHash, file, offset, cancellationToken);
                    if (bytes is null) return WorldPeerTransferResult.ChunkUnavailable;
                    observation?.Submitted(bytes.Length);
                    if (file == descriptor.Version.Files.Count - 1 && offset + bytes.Length == descriptor.Version.Files[file].Length)
                        observation?.Phase(WorldPeerTransferPhase.VerifyingRemote);
                    response = await SendSeedAsync(group, route, descriptor, null, bytes, file, offset, cancellationToken);
                    if (response.Code is "SeedComplete" or "SeedKnown" || response.Code == "SeedChunk" &&
                        response.FileIndex is { } nextFile && response.ChunkOffset is { } nextOffset &&
                        (nextFile > file || nextFile == file && nextOffset > offset)) progress?.Invoke(file, offset + bytes.Length);
                    sent += bytes.Length;
                    if (sent >= WorldPeerFileTransfer.MaximumPayloadBytesPerPass) return WorldPeerTransferResult.ByteBudget;
                }
                else if (response.Code == "SeedPaused") return WorldPeerTransferResult.SeedPaused;
                else if (response.Code == "SeedHistoryPending") return WorldPeerTransferResult.SeedHistoryPending;
                else throw new InvalidDataException("The peer returned an invalid fixed seed request.");
                if (work == 255) outcome = WorldPeerTransferResult.WorkBudget;
            }
        }
        return outcome;
    }

    private async Task<WorldPeerSeedResponse> SendSeedAsync(Group group, SharedWorldPeerRoute route,
        WorldPeerSaveDescriptor descriptor, SharedWorldVersion? manifest, byte[]? bytes,
        int? fileIndex, long? offset, CancellationToken cancellationToken)
    {
        var current = Find(group.Root.GroupKey);
        using var key = current is null ? null : LocalKey(current);
        if (current is null || key is null || !current.SharingEnabled ||
            !await ReceiveAllowedAsync(current, cancellationToken) ||
            Access(current, current.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) is not { Receive: true, Reshare: true })
            throw new OperationCanceledException("Save consent or forwarding permission changed.");
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
        WorldPeerSeedResponse PendingHistory(WorldPeerSeedResponse response)
        {
            SharedSaveTransferProgress.Pending(data, group.Root, version.VersionHash, response.Code,
                "Signed save history needs another bounded check before payload completion or receipt confirmation. Background work may continue it.");
            SharedSaveTransferProgress.Clear(data, group.Root, version.VersionHash);
            return response;
        }
        if (!await ReceiveAllowedAsync(group, cancellationToken)) return new("SeedPaused", version.VersionHash);
        lock (stateSync)
            if (worldExchangeGates.TryGetValue(group.Root.GroupKey, out var preparation) && preparation.Preparations > 0)
                return new("SeedPaused", version.VersionHash);
        IReadOnlyList<LocalSave>? localCopies = seed.Manifest is null && seed.Chunk is null
            ? await ExistingCopyInventoryAsync(group, cancellationToken) : null;
        if (seed.Manifest is null && seed.Chunk is null &&
            await TryReuseExistingCopyAsync(group, descriptor, cancellationToken,
                inventory: localCopies,
                additionalAuthorization: () => SeedSenderAllowed(group, request)) is { } reused)
            return new("SeedKnown", version.VersionHash, Receipt: reused.Receipt);
        using var activity = SharedSaveTransferProgress.Begin(data, group.Root, version.VersionHash, group.Name, version.WorldId);
        var vault = descriptor.Branch is { } branch ? SharedWorldBranchVault.Root(data, branch) : ReceivedRoot(data, group);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
        var receiveGate = FriendLink.ReceiveGateFor(vault);
        // An existing pull owns this writer while it awaits remote chunks.
        // Report pending immediately rather than queueing an inbound request
        // beyond its sender's deadline. No partial copy is acknowledged here.
        if (!await receiveGate.WaitAsync(0, cancellationToken)) return new("SeedPaused", version.VersionHash);
        try
        {
            var stagedPayload = Path.Combine(SharedWorldService.SafeChild(vault, ".partial-" + version.VersionHash),
                SharedWorldService.PayloadDirectory);
            SharedSaveTransferProgress.Report(data, group.Root, version, "Preparing",
                StagedPayloadLengths(stagedPayload, version).Sum(), SharedWorldService.BoundedTotalBytes(version.Files));
            var existing = ReadPeerPointer(vault);
            if (existing?.VersionHash == version.VersionHash || existing is not null &&
                WorldPeerFileTransfer.KnownDescendant(vault, version, existing))
            {
                using var verified = VerifyReceiptPayload(vault, existing!, cancellationToken);
                var receipt = existing.VersionHash == version.VersionHash
                    ? await RetainedReceiptForVerifiedCopyAsync(group, descriptor, vault, verified, cancellationToken) : null;
                if (existing.VersionHash == version.VersionHash)
                {
                    var verifiedBytes = SharedWorldService.BoundedTotalBytes(version.Files);
                    SharedSaveTransferProgress.Report(data, group.Root, version, "Receipt", verifiedBytes, verifiedBytes);
                    SharedSaveTransferProgress.Complete(data, group.Root, version.VersionHash);
                }
                else
                {
                    SharedSaveTransferProgress.Pending(data, group.Root, version.VersionHash, "NewerLocalCopyAvailable",
                        "This PC already has a verified newer descendant. The older offered copy was not downloaded or acknowledged.");
                    SharedSaveTransferProgress.Clear(data, group.Root, version.VersionHash);
                }
                return new("SeedKnown", version.VersionHash, Receipt: receipt);
            }
            var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
            var anchor = descriptor.Branch?.Offer.Version ?? WorldPeerFileTransfer.TrustedHistoricAnchor(group.Root, version, records);
            var pending = await StageSeedHistoryAsync(group, descriptor, seed.Manifest, vault, anchor, cancellationToken);
            if (pending is not null) return PendingHistory(pending);
            if (existing is not null && !WorldPeerFileTransfer.KnownDescendant(vault, existing, version))
            {
                // A fork is retained in its own exact-hash vault. It cannot
                // replace the ordinary received pointer, even in one lane.
                var retained = SharedWorldService.SafeChild(RetainedHistoryParent(data, group), version.VersionHash);
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, retained);
                var retainedGate = FriendLink.ReceiveGateFor(retained);
                if (!await retainedGate.WaitAsync(0, cancellationToken)) return new("SeedPaused", version.VersionHash);
                try
                {
                    if (ReadPeerPointer(retained)?.VersionHash == version.VersionHash)
                    {
                        using var verified = VerifyReceiptPayload(retained, version, cancellationToken);
                        var receipt = await RetainedReceiptForVerifiedCopyAsync(group, descriptor, retained, verified, cancellationToken);
                        var verifiedBytes = SharedWorldService.BoundedTotalBytes(version.Files);
                        SharedSaveTransferProgress.Report(data, group.Root, version, "Receipt", verifiedBytes, verifiedBytes);
                        SharedSaveTransferProgress.Complete(data, group.Root, version.VersionHash);
                        return new("SeedKnown", version.VersionHash, Receipt: receipt);
                    }
                    pending = await StageSeedHistoryAsync(group, descriptor, seed.Manifest, retained, anchor, cancellationToken);
                    return pending is not null ? PendingHistory(pending) :
                        await AcceptSeedPayloadAsync(group, request, seed, retained, anchor, cancellationToken, localCopies);
                }
                finally { retainedGate.Release(); }
            }
            return await AcceptSeedPayloadAsync(group, request, seed, vault, anchor, cancellationToken, localCopies);
        }
        catch (Exception error)
        {
            SharedSaveTransferProgress.ReceiveFailure(data, group.Root, version.VersionHash,
                error, cancellationToken, WorldPeerTransferOperation.Seed, WorldPeerTransferPhase.Preparing,
                IsReceiveWorkBudgetExpired);
            SharedSaveTransferProgress.Clear(data, group.Root, version.VersionHash);
            throw;
        }
        finally { receiveGate.Release(); }
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
        WorldPeerSeedRequest seed, string vault, SharedWorldVersion? anchor, CancellationToken cancellationToken,
        IReadOnlyList<LocalSave>? localCopies)
    {
        var descriptor = seed.Descriptor;
        var version = descriptor.Version;
        var total = SharedWorldService.BoundedTotalBytes(version.Files);
        if (ReceiveFreeBytes(vault) < total * 2 + 1024L * 1024 * 1024)
        {
            SharedSaveTransferProgress.Fail(data, group.Root, version.VersionHash, "InsufficientSpace",
                "This PC needs space for the completed save, verification and disk reserve. No completed copy or receipt was confirmed.");
            SharedSaveTransferProgress.Clear(data, group.Root, version.VersionHash);
            return new("SeedPaused", version.VersionHash);
        }
        var stage = SharedWorldService.SafeChild(vault, ".partial-" + version.VersionHash);
        var payload = Path.Combine(stage, SharedWorldService.PayloadDirectory);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, payload);
        Directory.CreateDirectory(payload);
        if (seed.Chunk is null)
        {
            localCopies ??= await ExistingCopyInventoryAsync(group, cancellationToken);
            await ReuseMatchingFilesAsync(group, descriptor, vault, payload, anchor, localCopies, cancellationToken,
                () => SeedSenderAllowed(group, request));
        }
        // Every seed request is a separate bounded transport pass. Retained
        // logical file bytes are its baseline, including completed files after
        // an earlier file in the manifest. Never publish a synthetic zero at
        // each request or count JSON/base64/gzip wire bytes as save progress.
        var retainedLengths = StagedPayloadLengths(payload, version);
        long received = Directory.Exists(Path.Combine(vault, version.VersionHash)) ? total : retainedLengths.Sum();
        SharedSaveTransferProgress.Report(data, group.Root, version, "Receiving", received, total);
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
                received += offset - retainedLengths[index];
                SharedSaveTransferProgress.Report(data, group.Root, version, "Receiving", received, total);
                if (seed.Chunk is null || request.FileIndex != index || request.ChunkOffset != offset)
                    return new("SeedChunk", version.VersionHash, FileIndex: index, ChunkOffset: offset);
                if (seed.Chunk.Length != Math.Min(SharedWorldService.ChunkBytes, file.Length - offset))
                    throw new InvalidDataException("The seeded save chunk has the wrong length.");
                if (!SeedSenderAllowed(group, request) || !await ReceiveAllowedAsync(group, cancellationToken))
                {
                    SharedSaveTransferProgress.Pending(data, group.Root, version.VersionHash, "SeedPaused",
                        "Receiving or forwarding permission changed. The save is paused; no completed copy or receipt was confirmed.");
                    SharedSaveTransferProgress.Clear(data, group.Root, version.VersionHash);
                    return new("SeedPaused", version.VersionHash);
                }
                output.Position = offset;
                await output.WriteAsync(seed.Chunk, cancellationToken);
                received += seed.Chunk.Length;
                SharedSaveTransferProgress.Report(data, group.Root, version, "Receiving", received, total);
                if (output.Position < file.Length)
                    return new("SeedChunk", version.VersionHash, FileIndex: index, ChunkOffset: output.Position);
            }
        SharedSaveTransferProgress.Report(data, group.Root, version, "Verifying", total, total);
        var receipt = await CompletePeerSaveAsync(group, descriptor, vault, stage, anchor, cancellationToken,
            () => SeedSenderAllowed(group, request));
        SharedSaveTransferProgress.Complete(data, group.Root, version.VersionHash);
        return new("SeedComplete", version.VersionHash, Receipt: receipt);
    }

    private bool SeedSenderAllowed(Group group, WorldPeerRequest request)
    {
        var current = Find(group.Root.GroupKey);
        return current is not null && current.SharingEnabled &&
            Access(current, request.Challenge.DeviceId, request.Challenge.PublicKey) is { Receive: true, Reshare: true };
    }
}

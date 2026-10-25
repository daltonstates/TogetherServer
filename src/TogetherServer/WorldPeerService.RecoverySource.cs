using System.Globalization;
using System.Security.Cryptography;

namespace TogetherServer;

// Internal provenance only. No local HTTP or peer request accepts a vault path.
internal sealed record WorldPeerRecoverySource(SharedWorldPeerRoot Root, string BranchHash,
    WorldPeerSaveDescriptor Descriptor, string Vault)
{
    internal static string StorageRoot(LocalData data, SharedWorldPeerRoot root, string branchHash,
        string versionHash) => Path.Combine(data.RootPath, "world-peer-recovery-sources", root.GroupKey,
            branchHash, versionHash);

    internal void Verify(LocalData data, SharedWorldPeerRoot expectedRoot, CancellationToken cancellationToken = default,
        Action<long, long, int, int>? progress = null)
    {
        if (Root != expectedRoot || !SharedWorldPeerTrust.ValidRoot(Root) || Descriptor is null || Descriptor.Version is null ||
            !SharedWorldPeerTrust.ValidHash(BranchHash) ||
            Descriptor.TestOnly || Descriptor.TestArtifact is not null ||
            Descriptor.Version.CaptureKind == LiveSaveAcceptanceTrust.TestCaptureKind ||
            !Path.GetFullPath(Vault).Equals(StorageRoot(data, Root, BranchHash, Descriptor.Version.VersionHash),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected recovery source does not belong to this exact shared world.");
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, Vault);
        var latest = FriendLink.ReadReceivedLatest(Vault, cancellationToken, progress);
        if (latest?.VersionHash != Descriptor.Version.VersionHash || latest.ProfileId != Root.ProfileId || latest.GroupId != Root.GroupId)
            throw new InvalidDataException("The explicitly selected recovery copy changed after review.");
        var records = new WorldAuthorityStore(data).Read(Root.ProfileId);
        if (Descriptor.Branch is { } branch)
        {
            if (!SharedWorldSeparateCopyStore.Verify(branch) ||
                branch.Offer.Roster.OwnerPublicKey != Root.OwnerPublicKey ||
                branch.Offer.Roster.GroupId != Root.GroupId || branch.Offer.Roster.ProfileId != Root.ProfileId ||
                !SharedWorldSeparateCopyStore.VerifyReceivedBranch(Vault, branch, latest))
                throw new InvalidDataException("The selected recovery branch proof or complete ancestry changed.");
            var source = branch;
            while (source.SourceBranch is not null) source = source.SourceBranch;
            if (!WorldPeerFileTransfer.TrustedHistoricSigner(Root, source.Offer.Version, records))
                throw new InvalidDataException("The selected branch has no authorized historical source signer.");
        }
        else
        {
            if (!WorldPeerFileTransfer.TrustedHistoricSigner(Root, latest, records))
                throw new InvalidDataException("The selected save signer is not authorized for this shared world.");
            WorldPeerFileTransfer.VerifyHistory(Vault, Descriptor,
                WorldPeerFileTransfer.TrustedHistoricAnchor(Root, latest, records));
        }
    }
}

internal sealed record WorldPeerRecoverySelectionCandidate(WorldPeerSaveDescriptor Descriptor,
    string Directory);

// Metadata can miss intermediate saves. Resolve its uncertainty only with the
// selected world's complete signed parent chain and a fully verified payload.
// Actual signer equivocation is never repaired by a larger save number.
internal static class WorldPeerRecoverySelection
{
    internal static void RequireNoEquivocation(SharedWorldPeerGroupState state, string branchHash)
    {
        foreach (var claim in state.Branches.Where(item => item.Branch.BranchHash == branchHash))
            if (state.Peers.SingleOrDefault(peer => peer.DeviceId == claim.DeviceId &&
                peer.PublicKey == claim.PublicKey) is not { HasConflict: false })
                throw new InvalidDataException("This history's signer has conflicting metadata. Keep every copy for review.");
    }

    internal static async Task<int> SelectAsync(LocalData data, SharedWorldPeerRoot root, string branchHash,
        SharedWorldPeerGroupState state, IReadOnlyList<WorldPeerRecoverySelectionCandidate> candidates,
        Func<int, long, string, CancellationToken, Task<SharedWorldVersion?>> readManifest,
        CancellationToken cancellationToken)
    {
        if (state.Root != root || state.GroupKey != root.GroupKey || !SharedWorldPeerTrust.ValidHash(branchHash) ||
            candidates.Count > SharedWorldPeerStore.MaximumKnownBranches)
            throw new InvalidDataException("The selected recovery lane crossed its approved world or capacity.");
        RequireNoEquivocation(state, branchHash);
        var claims = state.Branches.Where(item => item.Branch.BranchHash == branchHash).ToArray();
        if (claims.Length == 0 || claims.Length > SharedWorldPeerStore.MaximumKnownBranches ||
            claims.Select(item => item.Branch.BaseVersionHash).Distinct().Count() != 1)
            throw new InvalidDataException("This lane has no unique signed source base.");
        var hashes = claims.Select(item => item.Branch.HeadVersionHash).ToHashSet(StringComparer.Ordinal);
        var records = new WorldAuthorityStore(data).Read(root.ProfileId);
        var winners = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var descriptor = candidate.Descriptor;
            var head = descriptor.Version;
            var origin = descriptor.Branch;
            while (origin?.SourceBranch is not null) origin = origin.SourceBranch;
            if (!hashes.Contains(head.VersionHash) || descriptor.TestOnly || descriptor.TestArtifact is not null ||
                !WorldPeerFileTransfer.TrustedHistoricSigner(root,
                    origin?.Offer.Version ?? head, records)) continue;
            try
            {
                var anchor = descriptor.Branch?.Offer.Version ?? WorldPeerFileTransfer.TrustedHistoricAnchor(root, head, records);
                if (descriptor.Branch is { } branch && (branch.BranchHash != branchHash ||
                    !SharedWorldSeparateCopyStore.Verify(branch) || branch.Offer.Roster.OwnerPublicKey != root.OwnerPublicKey ||
                    branch.Offer.Proposal.GroupId != root.GroupId || branch.Offer.Proposal.ProfileId != root.ProfileId ||
                    !SharedWorldSeparateCopyStore.VerifySnapshotVersion(branch, head))) continue;
                using var verified = WorldPeerVerifiedCopy.Open(data.RootPath, candidate.Directory, head, cancellationToken);
                var remaining = new HashSet<string>(hashes, StringComparer.Ordinal);
                var current = head;
                var baseSeen = false;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!SharedWorldService.VerifySignature(current) || current.GroupId != root.GroupId ||
                        current.ProfileId != root.ProfileId || current.Game != head.Game || current.WorldId != head.WorldId)
                        break;
                    var atAnchor = anchor?.VersionHash == current.VersionHash;
                    if (!atAnchor && (descriptor.Branch is { } signedBranch ?
                            !SharedWorldSeparateCopyStore.VerifySnapshotVersion(signedBranch, current) :
                            current.SigningPublicKey != head.SigningPublicKey)) break;
                    var currentClaims = claims.Where(item => item.Branch.HeadVersionHash == current.VersionHash).ToArray();
                    if (currentClaims.Any(item => item.Branch.VersionNumber != current.Number ||
                        item.Branch.ParentVersionHash != current.ParentHash)) break;
                    remaining.Remove(current.VersionHash);
                    baseSeen |= current.VersionHash == claims[0].Branch.BaseVersionHash;
                    if (atAnchor || anchor is null && current.Number == 1)
                    {
                        if (remaining.Count == 0 && baseSeen && (atAnchor || current.ParentHash is null))
                            winners.TryAdd(head.VersionHash, index);
                        break;
                    }
                    if (current.Number <= 1 || current.ParentHash is not { } parentHash ||
                        anchor is not null && current.Number <= anchor.Number) break;
                    var previous = await readManifest(index, current.Number - 1, parentHash, cancellationToken);
                    if (previous is null || previous.VersionHash != parentHash || previous.Number != current.Number - 1) break;
                    current = previous;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or
                UnauthorizedAccessException or System.Text.Json.JsonException or CryptographicException or
                ArgumentException or System.ComponentModel.Win32Exception)
            { /* A missing or changed proof cannot resolve metadata uncertainty. */ }
        }
        if (winners.Count != 1)
            throw new InvalidDataException("This history has divergent or incomplete signed ancestry. Keep every copy for review.");
        return winners.Values.Single();
    }
}

internal sealed partial class WorldPeerService
{
    // Callers may already hold refreshGate. Only the exact selected source vault
    // is locked here; no discovery, listener, game action or default selection occurs.
    internal async Task<WorldPeerRecoverySource> ResolveRecoverySourceAsync(string groupKey,
        string? branchHash, CancellationToken cancellationToken, string? exactVersionHash = null,
        Action<WorldPeerSourceProgress>? progress = null)
    {
        var group = Find(groupKey) ?? throw new InvalidDataException("Choose a known shared world.");
        if (!await RecoveryAllowedAsync(group, cancellationToken))
            throw new InvalidDataException("This PC needs current Receive, Eligible host and local save consent.");
        if (branchHash is not null && !SharedWorldPeerTrust.ValidHash(branchHash))
            throw new InvalidDataException("Choose a signed saved history.");
        var local = (await LocalSavesAsync(group, cancellationToken)).Where(item =>
            !item.Descriptor.TestOnly && item.Descriptor.TestArtifact is null && AuthorizedDescriptor(group, item.Descriptor)).ToArray();
        LocalSave? selected;
        HashSet<string>? reviewedHeads = null;
        if (exactVersionHash is not null)
        {
            if (!SharedWorldPeerTrust.ValidHash(exactVersionHash) || branchHash is null)
                throw new InvalidDataException("Choose an exact verified source save and history.");
            selected = local.FirstOrDefault(item => item.Descriptor.Version.VersionHash == exactVersionHash &&
                (item.Descriptor.Branch is null || item.Descriptor.Branch.BranchHash == branchHash));
        }
        else if (branchHash is null)
        {
            // Preserve the ordinary default-source behavior. An explicit history
            // selection never falls back to this branch when its bytes are absent.
            selected = local.FirstOrDefault(item => item.Vault == ReceivedRoot(data, group) && item.Descriptor.Branch is null) ??
                local.FirstOrDefault(item => item.ProducedHere && item.Descriptor.Branch is null);
            branchHash = selected?.Descriptor.Branch?.BranchHash ?? NormalSessionBranchHash(group);
        }
        else
        {
            var state = Store(group).Project();
            var claims = state.Branches.Where(item => item.Branch.BranchHash == branchHash).ToArray();
            var hashes = claims.Select(item => item.Branch.HeadVersionHash).Distinct().ToArray();
            reviewedHeads = hashes.ToHashSet(StringComparer.Ordinal);
            WorldPeerRecoverySelection.RequireNoEquivocation(state, branchHash);
            var candidates = local.Where(item => item.Descriptor.Branch?.BranchHash == branchHash &&
                    (hashes.Length == 0 || hashes.Contains(item.Descriptor.Version.VersionHash)))
                .Concat(local.Where(item => item.Descriptor.Branch is null && hashes.Contains(item.Descriptor.Version.VersionHash)))
                .ToArray();
            if (hashes.Length > 1 || claims.Any(item => item.HasConflict))
            {
                var verifiedCandidates = candidates.Select(item => new WorldPeerRecoverySelectionCandidate(item.Descriptor,
                    RecoverySourceDirectory(item))).ToArray();
                var chosen = await WorldPeerRecoverySelection.SelectAsync(data, group.Root, branchHash, state, verifiedCandidates,
                    async (index, number, hash, token) => candidates[index].Vault is { } received ?
                        FriendLink.ReadReceivedManifest(received, number, hash) :
                        await manager.PeerPublishedVersionAsync(group.Root, hash, number), cancellationToken);
                selected = candidates[chosen];
            }
            else selected = candidates.FirstOrDefault();
        }
        if (selected is null)
            throw new InvalidDataException("The selected history has no complete copy on this PC. Receive that exact history before recovery.");
        var descriptor = selected.Descriptor;
        var version = descriptor.Version;
        var selectedLane = branchHash ?? throw new InvalidDataException("The selected recovery lane is unavailable.");
        var verificationMessage = "Rechecking the retained hosting copy against its signed file hashes.";
        void VerificationProgress(long bytes, long total, int files, int count) =>
            WorldPeerSourceProgress.Report(progress, new("Verifying", verificationMessage,
                bytes, total, files, count));
        void RequireReviewedHeads(Group current)
        {
            if (reviewedHeads is null) return;
            var state = Store(current).Project();
            WorldPeerRecoverySelection.RequireNoEquivocation(state, selectedLane);
            if (state.Branches.Any(item => item.Branch.BranchHash == selectedLane &&
                !reviewedHeads.Contains(item.Branch.HeadVersionHash)))
                throw new InvalidDataException("This lane learned another signed head during recovery. Review the updated history first.");
        }
        var vault = WorldPeerRecoverySource.StorageRoot(data, group.Root, selectedLane, version.VersionHash);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
        await FriendLink.ReceiveGateFor(vault).WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(vault);
            var source = new WorldPeerRecoverySource(group.Root, selectedLane, descriptor, vault);
            if (ReadPeerPointer(vault)?.VersionHash == version.VersionHash)
            {
                source.Verify(data, group.Root, cancellationToken, VerificationProgress);
                if (!await RecoveryAllowedAsync(group, cancellationToken))
                    throw new InvalidDataException("Recovery permission or local save consent changed.");
                RequireReviewedHeads(Find(groupKey) ?? throw new InvalidDataException("World membership changed."));
                return source;
            }
            var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
            var anchor = descriptor.Branch?.Offer.Version ?? WorldPeerFileTransfer.TrustedHistoricAnchor(group.Root, version, records);
            if (!await WorldPeerFileTransfer.StageDescriptorHistoryAsync(group.Root, vault, descriptor, records,
                async (hash, number, token) =>
                {
                    var manifest = anchor?.VersionHash == hash && anchor.Number == number ? anchor :
                        selected.Vault is not null ? FriendLink.ReadReceivedManifest(selected.Vault, number, hash) : null;
                    // A retained branch base may keep only its current source
                    // segment. Complete older segments from exact signed
                    // metadata elsewhere in this world; the selected lane and
                    // payload stay pinned, and staging verifies every parent.
                    return manifest ?? await FindManifestAsync(group, hash, number, token);
                }, cancellationToken))
                throw new InvalidDataException("The selected signed history is still being copied. Prepare recovery again to continue.");
            WorldPeerFileTransfer.VerifyHistory(vault, descriptor, anchor);
            var sourceDirectory = RecoverySourceDirectory(selected);
            var total = SharedWorldService.BoundedTotalBytes(version.Files);
            if (new DriveInfo(Path.GetPathRoot(vault)!).AvailableFreeSpace < total * 2 + 1024L * 1024 * 1024)
                throw new IOException("The selected recovery source needs space for verified copies and the disk reserve.");
            var destination = SharedWorldService.SafeChild(vault, version.VersionHash);
            if (!Directory.Exists(destination))
            {
                verificationMessage = "Checking the received source before making the protected hosting copy.";
                using var inputLease = WorldPeerVerifiedCopy.Open(data.RootPath, sourceDirectory, version, cancellationToken,
                    VerificationProgress);
                var stage = SharedWorldService.SafeChild(vault, ".partial-" + version.VersionHash);
                var payload = Path.Combine(stage, SharedWorldService.PayloadDirectory);
                Directory.CreateDirectory(payload);
                long prepared = 0;
                var filesPrepared = 0;
                void CopyProgress(long bytes) => WorldPeerSourceProgress.Report(progress,
                    new("PreparingCopy", "Preparing the protected hosting copy on this PC. Final verification is still required.",
                        prepared + bytes, total, filesPrepared, version.Files.Count));
                CopyProgress(0);
                var buffer = new byte[512 * 1024];
                foreach (var file in version.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await RecoveryAllowedAsync(group, cancellationToken))
                        throw new InvalidDataException("Recovery permission or local save consent changed.");
                    var target = SharedWorldService.SafeChild(payload, file.Path);
                    SharedWorldService.EnsureUnlinkedRoot(data.RootPath, target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = new FileStream(SharedWorldService.SafeChild(Path.Combine(sourceDirectory,
                        SharedWorldService.PayloadDirectory), file.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                    if (output.Length != file.Length)
                    {
                        var resume = output.Length <= file.Length ? output.Length / SharedWorldService.ChunkBytes * SharedWorldService.ChunkBytes : 0;
                        output.SetLength(resume); output.Position = resume; input.Position = resume;
                        CopyProgress(resume);
                        while (input.Position < file.Length)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var read = await input.ReadAsync(buffer, cancellationToken);
                            if (read == 0) throw new InvalidDataException("The selected source ended before its signed length.");
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            CopyProgress(output.Position);
                        }
                    }
                    prepared += file.Length;
                    filesPrepared++;
                    CopyProgress(0);
                }
                verificationMessage = "Checking every staged hosting file against its signed hash.";
                WorldPeerFileTransfer.VerifyStage(data.RootPath, payload, version, cancellationToken, VerificationProgress);
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllBytes(Path.Combine(stage, "version.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(version, Json));
                Directory.Move(stage, destination);
            }
            verificationMessage = "Verifying and holding the complete hosting copy for final permission checks.";
            using var complete = WorldPeerVerifiedCopy.Open(data.RootPath, destination, version, cancellationToken,
                VerificationProgress);
            void Commit()
            {
                lock (stateSync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = Find(groupKey);
                    using var key = current is null ? null : LocalKey(current);
                    if (current is null || key is null || current.DeviceId != group.DeviceId || !current.ReceiveConsented ||
                        Access(current, current.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) is not
                        { Receive: true, EligibleHost: true } || !AuthorizedDescriptor(current, descriptor))
                        throw new InvalidDataException("Current recovery membership or the selected save changed.");
                    RequireReviewedHeads(current);
                    WorldPeerFileTransfer.VerifyHistory(vault, descriptor, anchor);
                    WorldPeerFileTransfer.Promote(vault, descriptor);
                }
            }
            if (group.DeviceId == Guid.Empty) Commit();
            else if (!await friend.CommitPeerReceiveAsync(group.Root, group.DeviceId, Commit, cancellationToken))
                throw new InvalidDataException("Local save consent changed before the selected source was recorded.");
            verificationMessage = "Rechecking the committed protected hosting copy and signed history.";
            source.Verify(data, group.Root, cancellationToken, VerificationProgress);
            return source;
        }
        finally { FriendLink.ReceiveGateFor(vault).Release(); }
    }

    private string RecoverySourceDirectory(LocalSave source) => source.Vault is not null ?
        Path.Combine(source.Vault, source.Descriptor.Version.VersionHash) : Path.Combine(data.RootPath,
            "shared-worlds", source.Descriptor.Version.ProfileId.ToString("N"),
            source.Descriptor.Version.GroupId.ToString("N"), source.Descriptor.Version.Number.ToString(CultureInfo.InvariantCulture));

    private async Task<bool> RecoveryAllowedAsync(Group group, CancellationToken cancellationToken)
    {
        var current = Find(group.Root.GroupKey);
        using var key = current is null ? null : LocalKey(current);
        return current is not null && key is not null &&
            Access(current, current.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) is
            { Receive: true, EligibleHost: true } && await ReceiveAllowedAsync(current, cancellationToken);
    }

    internal async Task<WorldAuthorityOfferResult> PrepareSoloRecoveryOfferAsync(Guid profileId,
        string? sourceBranchHash, CancellationToken cancellationToken)
    {
        if (sourceBranchHash is null) return await friend.PrepareSoloRecoveryOfferAsync(profileId);
        try
        {
            var matches = Groups().Where(group => group.Root.ProfileId == profileId &&
                (Store(group).Project().Branches.Any(item => item.Branch.BranchHash == sourceBranchHash) ||
                 new SharedWorldSeparateCopyStore(data).Read(profileId).Any(branch => branch.BranchHash == sourceBranchHash &&
                     branch.Offer.Roster.GroupId == group.Root.GroupId && branch.Offer.Roster.OwnerPublicKey == group.Root.OwnerPublicKey))).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Choose one exact signed source world and history.");
            var source = await ResolveRecoverySourceAsync(matches[0].Root.GroupKey, sourceBranchHash, cancellationToken);
            return await friend.PrepareSelectedSoloRecoveryAsync(source, cancellationToken);
        }
        catch (Exception error) when (PeerFailure(error))
        { return new(false, "RecoverySourceUnavailable", error.Message); }
    }
}

public sealed partial class FriendService
{
    internal async Task<WorldAuthorityOfferResult> PrepareSelectedSoloRecoveryAsync(WorldPeerRecoverySource source,
        CancellationToken cancellationToken)
    {
        if (!await SelectWorldSourceAsync(source.Root, cancellationToken))
            return new(false, "RecoverySourceUnavailable", "Choose an approved source-world connection with local save consent.");
        var link = SelectedLink();
        return link is null ? new(false, "RecoverySourceUnavailable", "The selected world connection changed.") :
            await link.PrepareSoloRecoveryOfferAsync(source.Root.ProfileId, source.BranchHash, source);
    }
}

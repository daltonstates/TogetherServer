using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    private sealed record ExistingCopyScope(Group Group, string PublicKey, string RosterHash);
    private sealed record ExistingCopyReuse(SharedWorldReceipt? Receipt);
    private sealed record HistoricalCopySource(LocalSave Copy, SharedWorldVersion Head, string? OtherHead = null);
    // Normally null observation at the held-payload/final-commit boundary.
    // It supplies no path or proof and cannot authorize receipt creation.
    internal Action? HistoricalCopyCommittingForChecks { get; set; }

    private ExistingCopyScope ExistingCopyScopeFor(Group expected, WorldPeerSaveDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Find(expected.Root.GroupKey);
        using var key = current is null ? null : RetainedAdvertisementKey(current);
        if (current is null || current.Root != expected.Root || current.DeviceId != expected.DeviceId || key is null ||
            descriptor.TestOnly || descriptor.TestArtifact is not null || !current.ReceiveConsented ||
            KnownRosterRevision(current) < current.Roster.Revision || !AuthorizedDescriptor(current, descriptor))
            throw new InvalidDataException("Current membership does not authorize the exact existing copy.");
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        if (Access(current, current.DeviceId, publicKey)?.Receive != true)
            throw new InvalidDataException("Current receiving consent does not authorize an existing-copy receipt.");
        return new(current, publicKey, SharedWorldRosterTrust.Hash(current.Roster));
    }

    private void RequireExistingCopyScope(ExistingCopyScope scope, WorldPeerSaveDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var current = ExistingCopyScopeFor(scope.Group, descriptor, cancellationToken);
        if (current.PublicKey != scope.PublicKey || current.RosterHash != scope.RosterHash)
            throw new InvalidDataException("The existing copy's current signing identity or roster changed.");
    }

    private IReadOnlyList<LocalSave> ExactLocalCopies(IReadOnlyList<LocalSave> inventory,
        WorldPeerSaveDescriptor descriptor) => inventory.Where(item => !item.Descriptor.TestOnly &&
            item.Descriptor.TestArtifact is null && item.Descriptor.Branch?.BranchHash == descriptor.Branch?.BranchHash &&
            item.Descriptor.Version.VersionHash == descriptor.Version.VersionHash).ToArray();

    private async Task<IReadOnlyList<LocalSave>> ExistingCopyInventoryAsync(Group group, CancellationToken cancellationToken)
    {
        using var key = RetainedAdvertisementKey(group) ?? throw new InvalidDataException("The retained member key is unavailable.");
        return await LocalSavesAsync(group, cancellationToken, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    // Only the exact offered descriptor's deterministic known roots are inspected.
    // Historical payloads are deliberately absent from the advertised head
    // inventory; an old offer must not cause their bytes to be received again.
    private HistoricalCopySource? FindHistoricalCopy(Group group, WorldPeerSaveDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.TestOnly || descriptor.TestArtifact is not null)
            return null;
        var candidates = new List<HistoricalCopySource>();
        bool Present(string vault)
        {
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
            var path = SharedWorldService.SafeChild(vault, descriptor.Version.VersionHash);
            if (!Path.Exists(path)) return false;
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
            if (!Directory.Exists(path)) throw new InvalidDataException("The exact historical copy is not a directory.");
            return true;
        }
        void AddVault(string vault, LocalSaveOrigin origin, bool producedHere = false, string? otherHead = null)
        {
            if (!Present(vault)) return;
            var head = ReadPeerPointer(vault);
            // An unpointed receive destination is an orphan, not a historical
            // source. Normal completion still verifies its full payload and
            // ancestry before restoring its exact branch/pointer/receipt.
            if (head is null && origin is LocalSaveOrigin.Received or LocalSaveOrigin.ReceivedBranch or LocalSaveOrigin.History)
                return;
            if (head is null || !AuthorizedDescriptor(group, new(head, descriptor.Branch)))
                throw new InvalidDataException("The exact historical copy has no authorized retained source head.");
            candidates.Add(new(new(descriptor, vault, producedHere, origin), head, otherHead));
        }
        if (descriptor.Branch is { } offered)
        {
            var copies = new SharedWorldSeparateCopyStore(data);
            var branches = copies.Read(group.Root.ProfileId).Where(branch => branch.BranchHash == offered.BranchHash &&
                branch.Offer.Roster.OwnerPublicKey == group.Root.OwnerPublicKey &&
                branch.Offer.Roster.GroupId == group.Root.GroupId).ToArray();
            if (branches.Length > 1) throw new InvalidDataException("The exact historical branch is ambiguous.");
            if (branches.SingleOrDefault() is { } branch)
            {
                var published = copies.PublishedRoot(branch);
                var retained = copies.RetainedRoot(branch);
                using var key = RetainedAdvertisementKey(group);
                var producedHere = key is not null && descriptor.Version.SigningPublicKey == branch.Offer.Proposal.CandidatePublicKey &&
                    branch.Offer.Proposal.ProposerDeviceId == group.DeviceId &&
                    branch.CandidatePublicKey == Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
                if (Present(published)) AddVault(published, LocalSaveOrigin.Separate, producedHere, ReadPeerPointer(retained)?.VersionHash);
                if (Present(retained)) AddVault(retained, LocalSaveOrigin.Separate, producedHere, ReadPeerPointer(published)?.VersionHash);
            }
            else if (Present(copies.PublishedRoot(offered)) || Present(copies.RetainedRoot(offered)))
                throw new InvalidDataException("The exact historical copy lost its protected branch provenance.");
            var received = SharedWorldBranchVault.Root(data, offered);
            if (Present(received) && ReadPeerPointer(received) is not null)
            {
                if (SharedWorldBranchVault.ReadBranch(data, group.Roster, offered.BranchHash).BranchHash != offered.BranchHash)
                    throw new InvalidDataException("The exact historical received branch proof changed.");
                AddVault(received, LocalSaveOrigin.ReceivedBranch);
            }
        }
        else
        {
            AddVault(ReceivedRoot(data, group), LocalSaveOrigin.Received);
            var published = new LocalSave(descriptor, null, true, LocalSaveOrigin.Published);
            var directory = RecoverySourceDirectory(published);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, directory);
            if (Path.Exists(directory))
            {
                if (!Directory.Exists(directory)) throw new InvalidDataException("The exact historical publication is not a directory.");
                var recorded = ReadHistoricalCopyManifest(null, descriptor.Version, descriptor.Version.Number,
                    descriptor.Version.VersionHash, requireHash: false) ??
                    throw new InvalidDataException("The numbered historical publication lost its signed manifest.");
                // A valid different history at the same number is not this
                // offered copy. Keep it and let the normal fork receiver work.
                if (recorded.VersionHash == descriptor.Version.VersionHash)
                {
                    var head = manager.PeerPublishedVersion(group.Root);
                    if (head is null || !AuthorizedDescriptor(group, new(head)))
                        throw new InvalidDataException("The exact historical publication lost its current source provenance.");
                    candidates.Add(new(published, head));
                }
            }
        }
        var history = SharedWorldService.SafeChild(RetainedHistoryParent(data, group), descriptor.Version.VersionHash);
        if (Present(history) && ReadPeerPointer(history) is not null)
        {
            var branchPath = Path.Combine(history, "branch.json");
            WorldSeparateCopyBranch? branch = null;
            if (File.Exists(branchPath))
            {
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, branchPath);
                if (new FileInfo(branchPath).Length > SharedWorldService.MaximumManifestBytes)
                    throw new InvalidDataException("The exact historical copy's branch proof is oversized.");
                branch = JsonSerializer.Deserialize<WorldSeparateCopyBranch>(File.ReadAllBytes(branchPath), Json);
                if (!SharedWorldSeparateCopyStore.Verify(branch))
                    throw new InvalidDataException("The exact historical copy's branch proof changed.");
            }
            if (branch?.BranchHash != descriptor.Branch?.BranchHash)
                throw new InvalidDataException("The exact historical copy belongs to another signed branch.");
            AddVault(history, LocalSaveOrigin.History);
        }
        if (candidates.Count > 1) throw new InvalidDataException("The exact historical copy is ambiguous.");
        return candidates.SingleOrDefault();
    }

    private void RequireHistoricalCopySource(Group group, WorldPeerSaveDescriptor descriptor,
        HistoricalCopySource? expected, CancellationToken cancellationToken)
    {
        if (expected is null) return;
        var current = FindHistoricalCopy(group, descriptor, cancellationToken);
        if (current is null || current.Head.VersionHash != expected.Head.VersionHash || current.OtherHead != expected.OtherHead ||
            current.Copy.ProducedHere != expected.Copy.ProducedHere || current.Copy.Origin != expected.Copy.Origin ||
            (current.Copy.Vault is null ? expected.Copy.Vault is not null :
                expected.Copy.Vault is null || !SameChunkVault(current.Copy.Vault, expected.Copy.Vault)))
            throw new InvalidDataException("The exact historical copy's protected source or head changed.");
    }

    private void VerifyHistoricalCopyLineage(Group group, HistoricalCopySource source,
        WorldPeerSaveDescriptor descriptor, CancellationToken cancellationToken)
    {
        // Follow exact signed parents only, without auditing all archive entries
        // or hashing unrelated payloads. Exceeding the bounded metadata budget
        // blocks this reuse; it never falls through to a second payload copy.
        const int maximumParents = 4096;
        var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
        var offered = descriptor.Version;
        var anchor = descriptor.Branch?.Offer.Version ?? WorldPeerFileTransfer.TrustedHistoricAnchor(group.Root, offered, records);
        var current = source.Head;
        var offeredSeen = false;
        for (var work = 0; work <= maximumParents; work++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Game != offered.Game || current.WorldId != offered.WorldId ||
                (descriptor.Branch is { } branch ? !SharedWorldSeparateCopyStore.VerifySnapshotVersion(branch, current) :
                    !WorldPeerFileTransfer.TrustedHistoricSigner(group.Root, current, records)))
                throw new InvalidDataException("The historical copy's signed branch ancestry changed.");
            offeredSeen |= current.VersionHash == offered.VersionHash && current.Number == offered.Number;
            if (anchor is null ? current.Number == 1 : current.VersionHash == anchor.VersionHash)
            {
                if (!offeredSeen || anchor is null && current.ParentHash is not null)
                    throw new InvalidDataException("The exact historical copy is outside its retained head's ancestry.");
                return;
            }
            if (current.Number <= (anchor?.Number ?? 1) || current.ParentHash is not { } parent)
                throw new InvalidDataException("The historical copy's signed branch ancestry ended early.");
            if (work == maximumParents) break;
            var previous = ReadHistoricalCopyManifest(source.Copy.Vault, offered, current.Number - 1, parent) ??
                throw new InvalidDataException("The historical copy's exact signed parent is unavailable.");
            if (descriptor.Branch is null && previous.SigningPublicKey != current.SigningPublicKey &&
                WorldPeerFileTransfer.TrustedHistoricAnchor(group.Root, current,
                    records.Where(record => record.Proposal.CandidatePublicKey == current.SigningPublicKey).ToArray())?.VersionHash != previous.VersionHash)
                throw new InvalidDataException("The historical copy changed signer outside its exact signed authority anchor.");
            current = previous;
        }
        throw new InvalidDataException("The historical copy's signed ancestry exceeds the bounded lookup budget.");
    }

    private SharedWorldVersion? ReadHistoricalCopyManifest(string? vault, SharedWorldVersion offered, long number, string hash,
        bool requireHash = true)
    {
        if (vault is not null) SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
        var archived = vault is null ? null : FriendLink.ReadRetainedSignedManifest(vault, number, hash);
        if (archived is not null) return archived;
        var directory = vault is not null ? SharedWorldService.SafeChild(vault, hash) :
            Path.Combine(data.RootPath, "shared-worlds", offered.ProfileId.ToString("N"), offered.GroupId.ToString("N"),
                number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var path = Path.Combine(directory, "version.json");
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > SharedWorldService.MaximumManifestBytes ||
            (File.GetAttributes(input.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The historical copy's exact manifest is linked or oversized.");
        var version = JsonSerializer.Deserialize<SharedWorldVersion>(input, Json);
        if (version is null || requireHash && version.VersionHash != hash || version.Number != number || !SharedWorldService.VerifySignature(version))
            throw new InvalidDataException("The historical copy's exact manifest changed.");
        return version;
    }

    // Null means there is no exact known copy. An inconsistent matching
    // copy throws: never hide damage by downloading another copy over it.
    private async Task<ExistingCopyReuse?> TryReuseExistingCopyAsync(Group group,
        WorldPeerSaveDescriptor descriptor, CancellationToken cancellationToken,
        IReadOnlyList<LocalSave>? inventory = null, SharedWorldPeerRoute? receiptRoute = null,
        Func<bool>? additionalAuthorization = null)
    {
        // Consent takes receipt -> world when both are needed. Never call a
        // Friend consent read from the world-held scope checks below.
        if (group.DeviceId != Guid.Empty && !friend.HasRetainedWorldReceiveConsent(group.Root, group.DeviceId))
            throw new InvalidDataException("Current receiving consent does not authorize an existing-copy receipt.");
        inventory ??= await ExistingCopyInventoryAsync(group, cancellationToken);
        var matches = ExactLocalCopies(inventory, descriptor);
        ExistingCopyScope scope;
        lock (stateSync) scope = ExistingCopyScopeFor(group, descriptor, cancellationToken);
        HistoricalCopySource? historical = null;
        if (matches.Count == 0)
        {
            historical = FindHistoricalCopy(scope.Group, descriptor, cancellationToken);
            if (historical is null) return null;
            matches = [historical.Copy];
        }
        if (matches.Count != 1) throw new InvalidDataException("The exact local copy is ambiguous.");
        var local = matches[0];
        var directory = RecoverySourceDirectory(local);
        var recorded = ReadExistingCopyReceipt(scope, descriptor, directory);
        // Like RetryRetainedReceiptAsync, a confirmed prior delivery needs no
        // new payload hash. This returns no receipt and grants no Start proof.
        if (receiptRoute is not null && recorded is not null && !ReceiptDeliveryDue(group, receiptRoute, recorded))
        {
            lock (stateSync)
            {
                RequireExistingCopyScope(scope, descriptor, cancellationToken);
                RequireHistoricalCopySource(scope.Group, descriptor, historical, cancellationToken);
            }
            return new(null);
        }
        var observation = transferAttempt.Value?.Attempt;
        observation?.Phase(WorldPeerTransferPhase.VerifyingLocal);
        observation?.Expected(SharedWorldService.BoundedTotalBytes(descriptor.Version.Files),
            descriptor.Version.Files.Count, descriptor.Version.Number);
        if (historical is not null) VerifyHistoricalCopyLineage(scope.Group, historical, descriptor, cancellationToken);
        using var verified = VerifyReceiptPayloadDirectory(directory, descriptor.Version, cancellationToken);
        if (historical is null) VerifyExistingCopyHistory(scope.Group, local, descriptor, cancellationToken);
        else
        {
            RequireHistoricalCopySource(scope.Group, descriptor, historical, cancellationToken);
        }
        SharedWorldReceipt? receipt = null;
        void Commit()
        {
            lock (stateSync)
            {
                RequireExistingCopyScope(scope, descriptor, cancellationToken);
                if (additionalAuthorization?.Invoke() == false ||
                    !verified.Covers(data.RootPath, directory, descriptor.Version))
                    throw new InvalidDataException("The exact existing copy or forwarding permission changed.");
                if (historical is null) VerifyExistingCopyHistory(scope.Group, local, descriptor, cancellationToken);
                else
                {
                    RequireHistoricalCopySource(scope.Group, descriptor, historical, cancellationToken);
                    // Payload handles do not hold the separate ancestry archive.
                    // Repeat the bounded exact-parent proof inside final commit,
                    // after any wait for current receiving consent.
                    VerifyHistoricalCopyLineage(scope.Group, historical, descriptor, cancellationToken);
                }
                receipt = ReadExistingCopyReceipt(scope, descriptor, directory);
                if (receipt is not null) return;
                using var key = RetainedAdvertisementKey(scope.Group) ??
                    throw new InvalidDataException("The retained receipt signing key is unavailable.");
                if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != scope.PublicKey)
                    throw new InvalidDataException("The receipt signing identity changed.");
                var draft = new SharedWorldReceipt(scope.Group.DeviceId == Guid.Empty ? SharedWorldReceiptTrust.PeerOwnerSchema : 1,
                    group.Root.GroupId, group.Root.ProfileId, descriptor.Version.VersionHash, group.DeviceId,
                    scope.Group.Roster.Epoch, scope.Group.Roster.Revision, Guid.NewGuid(), "");
                receipt = draft with
                {
                    Signature = Convert.ToBase64String(key.SignData(group.DeviceId == Guid.Empty
                    ? SharedWorldReceiptTrust.PeerOwnerBasis(draft, group.Root) : SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256))
                };
                var path = ExistingCopyReceiptPath(scope, descriptor, directory);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        output.Write(JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
                        output.Flush(true);
                    }
                    File.Move(temporary, path, overwrite: false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                receipt = ReadExistingCopyReceipt(scope, descriptor, directory) ??
                    throw new InvalidDataException("The exact current-scope receipt was not retained.");
            }
        }
        if (historical is not null)
        {
            try { HistoricalCopyCommittingForChecks?.Invoke(); }
            catch (Exception) { /* Observations cannot authorize the receipt. */ }
        }
        if (group.DeviceId == Guid.Empty) Commit();
        else if (!await friend.CommitPeerReceiveAsync(group.Root, group.DeviceId, Commit, cancellationToken))
            throw new InvalidDataException("Receiving consent changed before the existing copy was acknowledged.");
        if (receipt is null) throw new InvalidDataException("The exact existing copy has no current receipt.");
        if (receiptRoute is not null)
            await DeliverReceiptAsync(group, receiptRoute, receipt, JsonSerializer.SerializeToUtf8Bytes(receipt, Json), cancellationToken);
        return new(receipt);
    }

    private string ExistingCopyReceiptPath(ExistingCopyScope scope, WorldPeerSaveDescriptor descriptor, string directory)
    {
        var binding = string.Join('\n', scope.Group.Root.GroupKey, descriptor.Branch?.BranchHash ?? "Canonical",
            descriptor.Version.VersionHash, scope.RosterHash, scope.Group.DeviceId.ToString("N"), scope.PublicKey);
        return SharedWorldService.SafeChild(directory, "self-receipt-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding))) + ".json");
    }

    private SharedWorldReceipt? ReadExistingCopyReceipt(ExistingCopyScope scope,
        WorldPeerSaveDescriptor descriptor, string directory)
    {
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, directory);
        var scoped = ExistingCopyReceiptPath(scope, descriptor, directory);
        SharedWorldReceipt? currentReceipt = null;
        foreach (var path in new[] { scoped, Path.Combine(directory, "receipt.json") })
        {
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
            if (!File.Exists(path)) continue;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > SharedWorldReceiptTrust.MaximumRequestBytes ||
                (File.GetAttributes(input.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The retained copy receipt is linked or oversized.");
            var receipt = SharedWorldReceiptTrust.Parse(ReadReceiptBytes(input));
            if (receipt is null) throw new InvalidDataException("The retained copy receipt is malformed.");
            if (receipt.GroupId != scope.Group.Root.GroupId || receipt.ProfileId != scope.Group.Root.ProfileId ||
                receipt.VersionHash != descriptor.Version.VersionHash || receipt.DeviceId != scope.Group.DeviceId ||
                receipt.RosterEpoch > scope.Group.Roster.Epoch || receipt.RosterRevision > scope.Group.Roster.Revision ||
                !(scope.Group.DeviceId == Guid.Empty ? SharedWorldReceiptTrust.VerifyPeerOwner(receipt, scope.Group.Root) :
                    SharedWorldReceiptTrust.Verify(receipt, scope.PublicKey)))
                throw new InvalidDataException("The retained selfreceipt has an inconsistent identity, signature or roster.");
            if (AuthorizedCopyReceipt(scope.Group, receipt)) { currentReceipt ??= receipt; continue; }
            if (path == scoped) throw new InvalidDataException("The immutable current-scope receipt is inconsistent.");
            // Only a genuine same-self older-roster receipt is preserved here.
            // A new full verification may create a current-scope receipt.
        }
        return currentReceipt;
    }

    private static byte[] ReadReceiptBytes(Stream input)
    {
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private void VerifyExistingCopyHistory(Group group, LocalSave local,
        WorldPeerSaveDescriptor descriptor, CancellationToken cancellationToken)
    {
        var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
        var anchor = descriptor.Branch?.Offer.Version ?? WorldPeerFileTransfer.TrustedHistoricAnchor(group.Root, descriptor.Version, records);
        if (local.Vault is not null)
        {
            WorldPeerFileTransfer.VerifyHistory(local.Vault, descriptor, anchor);
            return;
        }
        // Canonical publications use numbered directories, not received-vault
        // archives. Keep the existing publisher's current authority/fence read.
        var head = descriptor.Version;
        var current = manager.PeerPublishedVersion(group.Root, head.VersionHash, head.Number) ??
            throw new InvalidDataException("The canonical publication is no longer available for this world.");
        while (anchor is null ? current.Number > 1 : current.VersionHash != anchor.VersionHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.SigningPublicKey != head.SigningPublicKey || current.ParentHash is not { } parent || current.Number <= 1)
                throw new InvalidDataException("The canonical publication ancestry changed signer or ended early.");
            var previous = manager.PeerPublishedVersion(group.Root, parent, current.Number - 1);
            if (previous is null || previous.VersionHash != parent || previous.Number != current.Number - 1 ||
                previous.Game != head.Game || previous.WorldId != head.WorldId)
                throw new InvalidDataException("The canonical publication ancestry is incomplete.");
            current = previous;
        }
        if (anchor is null && (current.Number != 1 || current.ParentHash is not null || current.SigningPublicKey != head.SigningPublicKey))
            throw new InvalidDataException("The canonical publication does not reach its complete signed origin.");
    }
}

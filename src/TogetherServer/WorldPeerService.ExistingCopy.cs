using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    private sealed record ExistingCopyScope(Group Group, string PublicKey, string RosterHash);
    private sealed record ExistingCopyReuse(SharedWorldReceipt? Receipt);

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

    // Null means there is no exact inventoried copy. An inconsistent matching
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
        if (matches.Count == 0) return null;
        if (matches.Count != 1) throw new InvalidDataException("The exact local copy is ambiguous.");
        var local = matches[0];
        ExistingCopyScope scope;
        lock (stateSync) scope = ExistingCopyScopeFor(group, descriptor, cancellationToken);
        var directory = RecoverySourceDirectory(local);
        var recorded = ReadExistingCopyReceipt(scope, descriptor, directory);
        // Like RetryRetainedReceiptAsync, a confirmed prior delivery needs no
        // new payload hash. This returns no receipt and grants no Start proof.
        if (receiptRoute is not null && recorded is not null && !ReceiptDeliveryDue(group, receiptRoute, recorded))
        {
            lock (stateSync) RequireExistingCopyScope(scope, descriptor, cancellationToken);
            return new(null);
        }
        var observation = transferAttempt.Value?.Attempt;
        observation?.Phase(WorldPeerTransferPhase.VerifyingLocal);
        observation?.Expected(SharedWorldService.BoundedTotalBytes(descriptor.Version.Files),
            descriptor.Version.Files.Count, descriptor.Version.Number);
        try { ReceiptPayloadHashingForChecks?.Invoke(); }
        catch (Exception) { /* Observations cannot authorize the existing copy. */ }
        using var verified = WorldPeerVerifiedCopy.Open(data.RootPath, directory, descriptor.Version, cancellationToken);
        VerifyExistingCopyHistory(scope.Group, local, descriptor, cancellationToken);
        SharedWorldReceipt? receipt = null;
        void Commit()
        {
            lock (stateSync)
            {
                RequireExistingCopyScope(scope, descriptor, cancellationToken);
                if (additionalAuthorization?.Invoke() == false ||
                    !verified.Covers(data.RootPath, directory, descriptor.Version))
                    throw new InvalidDataException("The exact existing copy or forwarding permission changed.");
                VerifyExistingCopyHistory(scope.Group, local, descriptor, cancellationToken);
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

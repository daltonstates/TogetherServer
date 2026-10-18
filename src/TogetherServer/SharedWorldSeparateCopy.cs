using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record WorldSeparateCopyBranch(int Schema, Guid BranchId,
    WorldAuthorityOffer Offer, bool WarningAccepted, string CandidatePublicKey,
    string Signature, string BranchHash, WorldSeparateCopyBranch? SourceBranch = null);
public sealed record WorldSeparateCopyResult(bool Ok, string Code, string Message,
    WorldSeparateCopyBranch? Branch = null);
public sealed record WorldSeparateCopyConfirmation(bool AcceptSplitWarning);
public sealed record WorldSeparateCopySnapshot(WorldSeparateCopyBranch Branch,
    SharedWorldVersion Version);

// This ledger records an explicit, warned fork. It never enters the authority
// log, changes the authoritative head, or permits Start of the shared profile.
internal sealed class SharedWorldSeparateCopyStore(LocalData data)
{
    private sealed record BranchSet(int Schema, Guid GroupId, Guid ProfileId,
        IReadOnlyList<WorldSeparateCopyBranch> Branches,
        IReadOnlyList<string>? ReturnedBranches = null);
    private sealed record BranchFloor(int Schema, int Count, string Sha256);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = SharedWorldMutationGate.For(data.RootPath);
    private static string Name(Guid profileId) => $"shared-world-separate-{profileId:N}.protected";
    private static string FloorName(Guid profileId) => $"shared-world-separate-floor-{profileId:N}.protected";
    internal string RetainedRoot(WorldSeparateCopyBranch branch) => Path.Combine(data.RootPath,
        "separate-copy-vaults", "received-shared-worlds", branch.BranchId.ToString("N"),
        branch.Offer.Proposal.ProfileId.ToString("N"));
    private static byte[] Basis(WorldSeparateCopyBranch branch) => Encoding.UTF8.GetBytes(
        $"TogetherServer warned separate copy v{branch.Schema}\n{branch.BranchId:N}\n" +
        $"{WorldAuthorityTrust.ProposalHash(branch.Offer.Proposal)}\n" +
        $"{branch.Offer.Version.VersionHash}\n{branch.CandidatePublicKey}\n" +
        (branch.WarningAccepted ? "warning accepted" : "warning not accepted") +
        (branch.Schema >= 2 ? "\n" + branch.SourceBranch?.BranchHash : ""));
    private static string Hash(WorldSeparateCopyBranch branch) =>
        Convert.ToHexString(SHA256.HashData(branch.Schema == 1 ? JsonSerializer.SerializeToUtf8Bytes(new
        {
            branch.Schema,
            branch.BranchId,
            branch.Offer,
            branch.WarningAccepted,
            branch.CandidatePublicKey,
            branch.Signature
        }, Json) : JsonSerializer.SerializeToUtf8Bytes(new
        {
            branch.Schema,
            branch.BranchId,
            branch.Offer,
            branch.WarningAccepted,
            branch.CandidatePublicKey,
            branch.Signature,
            branch.SourceBranch
        }, Json)));

    internal static bool Verify(WorldSeparateCopyBranch? branch)
    {
        try
        {
            var count = 0;
            for (var current = branch; current is not null; current = current.SourceBranch)
            {
                if (++count > 16 || current.Schema is not (1 or 2 or 3) ||
                    current.Schema != 3 && (current.Schema == 2) != (current.SourceBranch is not null) ||
                    (current.Schema == 3) != IsOwnerOffer(current.Offer) ||
                    current.BranchId == Guid.Empty || !current.WarningAccepted ||
                    !VerifySeparateOffer(current.Offer, requireCurrentAccess: false) ||
                    current.CandidatePublicKey != WorldAuthorityTrust.CandidateDevicePublicKey(current.Offer.Proposal) ||
                    current.BranchHash != Hash(current)) return false;
                if (current.SourceBranch is { } source &&
                    (source.Offer.Roster.OwnerPublicKey != current.Offer.Roster.OwnerPublicKey ||
                     !VerifySnapshotVersion(source, current.Offer.Version))) return false;
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(current.CandidatePublicKey), out _);
                if (!key.VerifyData(Basis(current), Convert.FromBase64String(current.Signature),
                    HashAlgorithmName.SHA256)) return false;
            }
            return branch is not null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or
                                   ArgumentException or NullReferenceException or JsonException)
        { return false; }
    }

    // Owner recovery is deliberately a different proposal kind and envelope.
    // None of the canonical election, receipt or authority validators accept it.
    internal static bool IsOwnerOffer(WorldAuthorityOffer? offer) =>
        offer?.Proposal is { Schema: 4, Kind: "OwnerSeparate" };

    internal static bool VerifySeparateOffer(WorldAuthorityOffer? offer,
        bool requireCurrentAccess = true)
    {
        if (!IsOwnerOffer(offer)) return SharedWorldElection.VerifyOffer(offer, requireCurrentAccess);
        try
        {
            var proposal = offer!.Proposal;
            var roster = offer.Roster;
            var receipt = offer.CandidateReceipt;
            var binding = proposal.SuccessorBinding;
            if (!SharedWorldRosterTrust.Verify(roster) || !SharedWorldService.VerifySignature(offer.Version) ||
                proposal.GroupId != roster.GroupId || proposal.ProfileId != roster.ProfileId ||
                offer.Version.GroupId != roster.GroupId || offer.Version.ProfileId != roster.ProfileId ||
                proposal.Epoch < 1 || proposal.ParentAuthorityHash is not null &&
                    !SharedWorldPeerTrust.ValidHash(proposal.ParentAuthorityHash) ||
                proposal.RosterHash != WorldAuthorityTrust.RosterHash(roster) ||
                proposal.VersionHash != offer.Version.VersionHash ||
                offer.Version.CaptureKind == LiveSaveAcceptanceTrust.TestCaptureKind ||
                proposal.CompetingHeadHashes is not null ||
                proposal.ProposerDeviceId != Guid.Empty || proposal.ProposerPublicKey != roster.OwnerPublicKey ||
                binding is null || binding.DeviceId != Guid.Empty || binding.DevicePublicKey != roster.OwnerPublicKey ||
                binding.HostingPublicKey != proposal.CandidatePublicKey ||
                proposal.CandidatePublicKey == roster.OwnerPublicKey ||
                !SharedWorldRosterTrust.ValidKey(proposal.CandidatePublicKey) ||
                !HostIdentity.TryEndpoint(proposal.CandidateAddress, out var endpoint) ||
                endpoint.GetLeftPart(UriPartial.Authority) != proposal.CandidateAddress.TrimEnd('/') ||
                offer.CandidateTlsFingerprint is not { Length: 64 } ||
                !offer.CandidateTlsFingerprint.All(Uri.IsHexDigit) ||
                receipt is not { Schema: 1 } || receipt.DeviceId != Guid.Empty || receipt.ReceiptId == Guid.Empty ||
                receipt.GroupId != roster.GroupId || receipt.ProfileId != roster.ProfileId ||
                receipt.VersionHash != offer.Version.VersionHash || receipt.RosterEpoch != roster.Epoch ||
                receipt.RosterRevision != roster.Revision || offer.Ancestors is not { Count: 0 }) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(roster.OwnerPublicKey), out _);
            bool Signed(byte[] basis, string signature) => signature is { Length: >= 64 and <= 256 } &&
                key.VerifyData(basis, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
            return Signed(WorldAuthorityTrust.BindingBasis(proposal, binding), binding.Signature) &&
                Signed(WorldAuthorityTrust.ProposalBasis(proposal), proposal.Signature) &&
                Signed(SharedWorldReceiptTrust.Basis(receipt), receipt.Signature) &&
                Signed(Encoding.UTF8.GetBytes("TogetherServer authority candidate TLS pin v1\n" +
                    WorldAuthorityTrust.ProposalHash(proposal) + "\n" +
                    offer.CandidateTlsFingerprint.ToUpperInvariant()), offer.CandidateTlsProof);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or
            NullReferenceException or JsonException)
        { return false; }
    }

    private sealed record PeerMembership(SharedWorldPeerRoot Root, SharedWorldRoster Roster);
    private sealed record PeerMemberships(int Schema, IReadOnlyList<PeerMembership> Groups);

    // A branch captures history, not a perpetual grant. Resolve the latest
    // locally verified roster every time an action could use a saved world.
    // Conflicting heads and an incompletely caught-up floor fail closed.
    internal static SharedWorldRoster CurrentRoster(LocalData data, SharedWorldRoster captured)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            if (!SharedWorldRosterTrust.Verify(captured))
                throw new InvalidDataException("The captured recovery roster is invalid.");
            var chain = new SharedWorldRosterChainStore(data);
            var hasChain = chain.HasState(captured.ProfileId);
            var current = captured;
            if (hasChain)
            {
                var heads = chain.Heads(captured.ProfileId);
                if (heads.Count != 1 || heads[0].OwnerPublicKey != captured.OwnerPublicKey ||
                    heads[0].GroupId != captured.GroupId || heads[0].Revision < captured.Revision ||
                    heads[0].Revision == captured.Revision && heads[0].Signature != captured.Signature)
                    throw new InvalidDataException("The current signed recovery roster needs review.");
                current = heads[0];
            }
            const string registryName = "world-peer-memberships.protected";
            var bytes = data.LoadProtected(registryName);
            if (bytes is null && (data.HasProtected(registryName) ||
                data.Recovery.Notices.Any(notice => notice.StateFile == registryName)))
                throw new InvalidDataException("The current world membership is unavailable.");
            if (bytes is not null)
            {
                if (bytes.Length > 8 * 1024 * 1024)
                    throw new InvalidDataException("The current world membership is oversized.");
                var registry = JsonSerializer.Deserialize<PeerMemberships>(bytes, Json);
                if (registry is not { Schema: 1 } || registry.Groups is null || registry.Groups.Count > 128)
                    throw new InvalidDataException("The current world membership is invalid.");
                var matches = registry.Groups.Where(group => group.Root.OwnerPublicKey == captured.OwnerPublicKey &&
                    group.Root.ProfileId == captured.ProfileId && group.Root.GroupId == captured.GroupId).ToArray();
                if (matches.Length > 1) throw new InvalidDataException("Conflicting world membership needs review.");
                if (matches.SingleOrDefault() is { } saved)
                {
                    var roster = saved.Roster;
                    if (!SharedWorldRosterTrust.Verify(roster) || roster.OwnerPublicKey != captured.OwnerPublicKey ||
                        roster.ProfileId != captured.ProfileId || roster.GroupId != captured.GroupId ||
                        roster.Revision == current.Revision && roster.Signature != current.Signature ||
                        hasChain && roster.Revision > current.Revision)
                        throw new InvalidDataException("The current signed world membership needs catch-up or review.");
                    if (roster.Revision > current.Revision) current = roster;
                }
            }
            if (new WorldAuthorityStore(data).GovernanceUnresolved(captured.ProfileId))
                throw new InvalidDataException("Review the pending local world-access change first.");
            return current;
        }
    }

    internal static bool CandidateCurrentlyAuthorized(LocalData data, WorldAuthorityOffer offer,
        DateTimeOffset? now = null)
    {
        try
        {
            if (!VerifySeparateOffer(offer, requireCurrentAccess: false)) return false;
            var roster = CurrentRoster(data, offer.Roster);
            if (IsOwnerOffer(offer)) return offer.Proposal.ProposerPublicKey == roster.OwnerPublicKey;
            var candidate = roster.Members.SingleOrDefault(member =>
                member.DeviceId == offer.Proposal.ProposerDeviceId);
            return candidate is { Revoked: false, Grants: { Receive: true, EligibleHost: true } } &&
                candidate.PublicKey == WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal) &&
                (candidate.AccessExpiresUtc is null || candidate.AccessExpiresUtc > (now ?? DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException or NullReferenceException)
        { return false; }
    }

    internal static string CandidateSigningKeyName(WorldSeparateCopyBranch branch) =>
        IsOwnerOffer(branch.Offer) ? "shared-world-signing-key.protected" :
            $"shared-world-pc-signing-{branch.Offer.Proposal.ProposerDeviceId:N}.protected";

    internal static bool HasLocalBranchKeys(LocalData data, WorldSeparateCopyBranch branch)
    {
        if (!Verify(branch)) return false;
        if (!IsOwnerOffer(branch.Offer))
            return new WorldAuthorityStore(data).HasLocalSuccessorKeys(branch.Offer.Proposal.ProfileId,
                branch.Offer.Proposal, branch.Offer.Proposal.ProposerDeviceId);
        try
        {
            var candidateBytes = data.LoadProtected(CandidateSigningKeyName(branch));
            var hostBytes = data.LoadProtected(WorldAuthorityStore.HostingKeyName(branch.Offer.Proposal.ProfileId));
            if (candidateBytes is null || hostBytes is null) return false;
            using var candidate = ECDsa.Create();
            using var host = ECDsa.Create();
            candidate.ImportPkcs8PrivateKey(candidateBytes, out var candidateRead);
            host.ImportPkcs8PrivateKey(hostBytes, out var hostRead);
            return candidateRead == candidateBytes.Length && hostRead == hostBytes.Length &&
                Convert.ToBase64String(candidate.ExportSubjectPublicKeyInfo()) == branch.CandidatePublicKey &&
                Convert.ToBase64String(host.ExportSubjectPublicKeyInfo()) == branch.Offer.Proposal.CandidatePublicKey;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or
            UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    internal WorldSeparateCopyBranch Declare(SharedWorldHostLoss loss, WorldAuthorityOffer offer,
        string receivedRoot, ECDsa candidateKey, bool acceptSplitWarning)
        => Declare(offer, receivedRoot, candidateKey, acceptSplitWarning);

    // An explicit recovery branch does not claim the shared world's authority.
    // The original Host can remain reachable (or keep its own game running).
    internal WorldSeparateCopyBranch Declare(WorldAuthorityOffer offer,
        string receivedRoot, ECDsa candidateKey, bool acceptSplitWarning,
        WorldSeparateCopyBranch? sourceBranch = null)
    {
        lock (sync)
        {
            if (!acceptSplitWarning || !VerifySeparateOffer(offer) ||
                !CandidateCurrentlyAuthorized(data, offer) ||
                FriendLink.ReadReceivedLatest(receivedRoot)?.VersionHash != offer.Version.VersionHash ||
                Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo()) !=
                    WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal))
                throw new InvalidDataException("The separate copy warning or hash-verified post-Stop file copy is missing.");
            var authority = new WorldAuthorityStore(data).Read(offer.Proposal.ProfileId);
            var selectedParent = sourceBranch is null ? SoloSourceParent(receivedRoot, offer.Version,
                offer.Roster.OwnerPublicKey, authority) : null;
            if (offer.Proposal.ParentAuthorityHash != selectedParent?.RecordHash ||
                offer.Proposal.Epoch != (selectedParent?.Proposal.Epoch ?? 0) + 1)
                throw new InvalidDataException("The selected copy's signed source changed after review.");
            var branches = Read(offer.Proposal.ProfileId);
            var prior = branches.FirstOrDefault(branch =>
                WorldAuthorityTrust.ProposalHash(branch.Offer.Proposal) ==
                    WorldAuthorityTrust.ProposalHash(offer.Proposal));
            if (prior is not null)
            {
                RetainBaseCopy(prior, receivedRoot);
                return prior;
            }
            if (branches.Count >= 20) throw new InvalidDataException("Too many separate histories need review.");
            if (sourceBranch is not null &&
                (!Verify(sourceBranch) || !VerifyReceivedBranch(receivedRoot, sourceBranch, offer.Version)))
                throw new InvalidDataException("The prior branch's complete verified ancestry is unavailable.");
            var branch = Sign(offer, candidateKey, sourceBranch);
            RetainBaseCopy(branch, receivedRoot);
            var set = new BranchSet(1, offer.Roster.GroupId, offer.Roster.ProfileId,
                branches.Append(branch).ToArray(), ReadSet(offer.Proposal.ProfileId)?.ReturnedBranches);
            SaveSet(offer.Proposal.ProfileId, set);
            return branch;
        }
    }

    private void RetainBaseCopy(WorldSeparateCopyBranch branch, string receivedRoot)
    {
        var version = branch.Offer.Version;
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, receivedRoot);
        var root = RetainedRoot(branch);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        if (FriendLink.ReadReceivedLatest(root)?.VersionHash == version.VersionHash) return;
        if (Directory.Exists(root) || File.Exists(root))
            throw new InvalidDataException("An incomplete retained recovery copy needs review.");
        var required = SharedWorldService.BoundedTotalBytes(version.Files);
        if (!SharedWorldReadiness.HasSpaceForCopies(root, required, 1))
            throw new IOException("Keep at least 1 GiB free after retaining the recovery copy.");
        var payload = Path.Combine(root, version.VersionHash, SharedWorldService.PayloadDirectory);
        Directory.CreateDirectory(payload);
        foreach (var file in version.Files)
        {
            var source = SharedWorldService.SafeChild(Path.Combine(receivedRoot,
                version.VersionHash, SharedWorldService.PayloadDirectory), file.Path);
            SharedWorldService.VerifyFile(source, file);
            var destination = SharedWorldService.SafeChild(payload, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, SharedWorldService.ChunkBytes, FileOptions.WriteThrough))
                input.CopyTo(output);
            SharedWorldService.VerifyFile(destination, file);
        }
        if (FriendLink.ReadReceivedLatest(receivedRoot)?.VersionHash != version.VersionHash)
            throw new InvalidDataException("The received save changed while retaining the recovery copy.");
        File.WriteAllBytes(Path.Combine(root, version.VersionHash, "version.json"),
            JsonSerializer.SerializeToUtf8Bytes(version, Json));
        var parent = branch.SourceBranch?.Offer.Version ?? new WorldAuthorityStore(data)
            .Read(version.ProfileId).SingleOrDefault(record =>
                record.RecordHash == branch.Offer.Proposal.ParentAuthorityHash)?.Version;
        if (parent is not null) FriendLink.KeepSignedManifest(root, parent);
        if (parent?.VersionHash != version.VersionHash)
            foreach (var ancestor in FriendLink.ReadVerifiedReceivedLineage(receivedRoot, version, parent))
                FriendLink.KeepSignedManifest(root, ancestor);
        else FriendLink.KeepSignedManifest(root, version);
        File.WriteAllBytes(Path.Combine(root, "latest.json"),
            JsonSerializer.SerializeToUtf8Bytes(version, Json));
        if (FriendLink.ReadReceivedLatest(root)?.VersionHash != version.VersionHash)
            throw new InvalidDataException("The retained recovery copy failed verification.");
    }

    // Reuses the signed proposal/receipt format without opening the election
    // inbox, collecting votes, or treating Host reachability as authorization.
    internal static WorldAuthorityOffer PrepareSoloOffer(string receivedRoot,
        SharedWorldRoster roster, SharedRosterFloor floor, Guid candidateId,
        ECDsa candidateKey, string candidateAddress, string candidateTlsFingerprint,
        WorldAuthorityStore authority, WorldSeparateCopyBranch? sourceBranch = null)
        => PrepareSeparateOffer(receivedRoot, roster, floor, candidateId, candidateKey,
            candidateAddress, candidateTlsFingerprint, authority, sourceBranch, ownerSeparate: false);

    internal static WorldAuthorityOffer PrepareOwnerOffer(string receivedRoot,
        SharedWorldRoster roster, ECDsa ownerKey, string candidateAddress,
        string candidateTlsFingerprint, WorldAuthorityStore authority,
        WorldSeparateCopyBranch? sourceBranch = null)
        => PrepareSeparateOffer(receivedRoot, roster,
            new(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature), Guid.Empty,
            ownerKey, candidateAddress, candidateTlsFingerprint, authority, sourceBranch, ownerSeparate: true);

    private static WorldAuthorityOffer PrepareSeparateOffer(string receivedRoot,
        SharedWorldRoster roster, SharedRosterFloor floor, Guid candidateId,
        ECDsa candidateKey, string candidateAddress, string candidateTlsFingerprint,
        WorldAuthorityStore authority, WorldSeparateCopyBranch? sourceBranch, bool ownerSeparate)
    {
        var version = FriendLink.ReadReceivedLatest(receivedRoot) ??
            throw new InvalidDataException("Receive a complete hash-verified save first.");
        if (version.CaptureKind == LiveSaveAcceptanceTrust.TestCaptureKind)
            throw new InvalidDataException("A test-only acceptance copy cannot become a playable recovery branch.");
        var deviceKey = Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo());
        if (!SharedWorldRosterTrust.Verify(roster) || !authority.TrustsCurrentRoster(roster) ||
            roster.GroupId != floor.GroupId || roster.Epoch != floor.Epoch ||
            roster.Revision != floor.Revision || roster.Signature != floor.Signature ||
            roster.GroupId != version.GroupId || roster.ProfileId != version.ProfileId ||
            (ownerSeparate ? candidateId != Guid.Empty || deviceKey != roster.OwnerPublicKey :
                !SharedWorldRosterTrust.HasRole(roster, candidateId, deviceKey,
                    grants => grants.Receive && grants.EligibleHost)) ||
            !HostIdentity.TryEndpoint(candidateAddress, out var endpoint) ||
            endpoint.GetLeftPart(UriPartial.Authority) != candidateAddress.TrimEnd('/') ||
            candidateTlsFingerprint is not { Length: 64 } ||
            !candidateTlsFingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("The approved roster, local hosting grant, or reviewed address is unavailable.");
        var records = authority.Read(roster.ProfileId);
        if (sourceBranch is not null &&
            (sourceBranch.Offer.Roster.OwnerPublicKey != roster.OwnerPublicKey ||
             !Verify(sourceBranch) || !VerifyReceivedBranch(receivedRoot, sourceBranch, version)))
            throw new InvalidDataException("Review the signed world history before preparing a recovery branch.");
        var parent = sourceBranch is null ?
            SoloSourceParent(receivedRoot, version, roster.OwnerPublicKey, records) : null;
        var hostingKey = authority.PrepareLocalHostingKey(roster.ProfileId);
        var draft = new WorldAuthorityProposal(ownerSeparate ? 4 : 2, roster.GroupId, roster.ProfileId,
            (parent?.Proposal.Epoch ?? 0) + 1, parent?.RecordHash,
            WorldAuthorityTrust.RosterHash(roster), version.VersionHash, hostingKey,
            candidateAddress, ownerSeparate ? "OwnerSeparate" : "Quorum", candidateId, deviceKey, "");
        var binding = new WorldSuccessorBinding(candidateId, deviceKey, hostingKey, "");
        draft = draft with
        {
            SuccessorBinding = binding with
            {
                Signature = Convert.ToBase64String(candidateKey.SignData(
                    WorldAuthorityTrust.BindingBasis(draft, binding), HashAlgorithmName.SHA256))
            }
        };
        var proposal = draft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
                WorldAuthorityTrust.ProposalBasis(draft), HashAlgorithmName.SHA256))
        };
        var receiptDraft = new SharedWorldReceipt(1, roster.GroupId, roster.ProfileId,
            version.VersionHash, candidateId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
        var receipt = receiptDraft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
                SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256))
        };
        var transportBasis = Encoding.UTF8.GetBytes("TogetherServer authority candidate TLS pin v1\n" +
            WorldAuthorityTrust.ProposalHash(proposal) + "\n" + candidateTlsFingerprint.ToUpperInvariant());
        var offer = new WorldAuthorityOffer(proposal, roster, version, receipt, [],
            candidateTlsFingerprint, Convert.ToBase64String(candidateKey.SignData(
                transportBasis, HashAlgorithmName.SHA256)));
        if (!VerifySeparateOffer(offer))
            throw new InvalidDataException("The signed recovery branch offer failed verification.");
        return offer;
    }

    // The selected copy can belong to any already proven authority branch or
    // the original owner's complete history. Concurrent heads are retained;
    // this lookup never resolves them or chooses a group default.
    private static WorldAuthorityRecord? SoloSourceParent(string root, SharedWorldVersion version,
        string ownerKey, IReadOnlyList<WorldAuthorityRecord> records)
    {
        foreach (var record in records.OrderByDescending(item => item.Proposal.Epoch))
        {
            if (record.Roster.OwnerPublicKey != ownerKey || record.Proposal.GroupId != version.GroupId ||
                record.Proposal.ProfileId != version.ProfileId) continue;
            if (record.Version.VersionHash == version.VersionHash) return record;
            if (record.Version.Number >= version.Number ||
                record.Proposal.CandidatePublicKey != version.SigningPublicKey) continue;
            try
            {
                if (FriendLink.ReadVerifiedReceivedLineage(root, version, record.Version)
                    .All(item => item.SigningPublicKey == record.Proposal.CandidatePublicKey)) return record;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                CryptographicException or UnauthorizedAccessException)
            { /* Another retained authority can anchor the explicitly selected copy. */ }
        }
        if (version.SigningPublicKey == ownerKey &&
            FriendLink.ReadVerifiedReceivedLineage(root, version, null)
                .All(item => item.SigningPublicKey == ownerKey)) return null;
        throw new InvalidDataException("The selected save has no complete verified source lineage in this approved world.");
    }

    internal static WorldSeparateCopyBranch Sign(WorldAuthorityOffer offer, ECDsa candidateKey,
        WorldSeparateCopyBranch? sourceBranch = null)
    {
        if (!VerifySeparateOffer(offer) ||
            Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo()) !=
                WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal))
            throw new InvalidDataException("The candidate identity or signed offer is invalid.");
        if (sourceBranch is not null && (!Verify(sourceBranch) ||
            sourceBranch.Offer.Roster.OwnerPublicKey != offer.Roster.OwnerPublicKey ||
            !VerifySnapshotVersion(sourceBranch, offer.Version)))
            throw new InvalidDataException("The selected source branch is invalid.");
        var draft = new WorldSeparateCopyBranch(IsOwnerOffer(offer) ? 3 : sourceBranch is null ? 1 : 2,
            Guid.NewGuid(), offer, true,
            WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal), "", "", sourceBranch);
        var signed = draft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            Basis(draft), HashAlgorithmName.SHA256))
        };
        return signed with { BranchHash = Hash(signed) };
    }

    internal static bool VerifySnapshotVersion(WorldSeparateCopyBranch branch,
        SharedWorldVersion version)
    {
        var source = branch.Offer.Version;
        return SharedWorldService.VerifySignature(version) && version.GroupId == source.GroupId &&
            version.ProfileId == source.ProfileId && version.Game == source.Game &&
            version.WorldId == source.WorldId &&
            (version.VersionHash == source.VersionHash || version.Number > source.Number &&
                version.SigningPublicKey == branch.Offer.Proposal.CandidatePublicKey &&
                (version.Number != source.Number + 1 || version.ParentHash == source.VersionHash));
    }

    internal static bool VerifyReceivedBranch(string root, WorldSeparateCopyBranch branch,
        SharedWorldVersion version)
    {
        if (!VerifySnapshotVersion(branch, version) ||
            FriendLink.ReadReceivedLatest(root)?.VersionHash != version.VersionHash) return false;
        if (version.VersionHash == branch.Offer.Version.VersionHash) return true;
        return FriendLink.ReadVerifiedReceivedLineage(root, version, branch.Offer.Version)
            .All(item => VerifySnapshotVersion(branch, item));
    }

    internal string PublishedRoot(WorldSeparateCopyBranch branch) => Path.Combine(data.RootPath,
        "shared-world-separate-published", branch.Offer.Proposal.ProfileId.ToString("N"), branch.BranchHash);

    internal WorldSeparateCopySnapshot? ReadPublished(WorldSeparateCopyBranch branch)
    {
        lock (sync)
        {
            if (!Verify(branch)) throw new InvalidDataException("The signed recovery branch is invalid.");
            var root = PublishedRoot(branch);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
            var latest = FriendLink.ReadReceivedLatest(root);
            if (latest is null) return null;
            if (!VerifyReceivedBranch(root, branch, latest))
                throw new InvalidDataException("The recovery snapshots do not extend this branch's verified base.");
            return new(branch, latest);
        }
    }

    // Verify the published pointer and its signed ancestry independently of the
    // payload. Freshness may then fall back along that exact chain when the
    // newest payload is incomplete or corrupt; this is not a playable-copy claim.
    internal SharedWorldVersion? ReadPublishedManifestHead(WorldSeparateCopyBranch branch)
    {
        lock (sync)
        {
            if (!Verify(branch)) throw new InvalidDataException("The signed recovery branch is invalid.");
            var root = PublishedRoot(branch);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
            if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "latest.json"))) return null;
            using var directory = new SharedWorldBranchLiveSource.DirectoryPins(data, root);
            using var input = new FileStream(NativeSnapshotDirectory.OpenReadFile(directory.Handle, "latest.json"), FileAccess.Read);
            if (input.Length > SharedWorldService.MaximumManifestBytes)
                throw new InvalidDataException("The published recovery metadata is oversized.");
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            var latest = JsonSerializer.Deserialize<SharedWorldVersion>(bytes, Json);
            if (latest is null || !VerifySnapshotVersion(branch, latest) ||
                latest.VersionHash != branch.Offer.Version.VersionHash &&
                !FriendLink.ReadVerifiedReceivedManifestLineage(root, latest, branch.Offer.Version)
                    .All(version => VerifySnapshotVersion(branch, version)))
                throw new InvalidDataException("The recovery metadata does not extend this branch's verified base.");
            return latest;
        }
    }

    internal SharedWorldResult PublishAfterStop(WorldSeparateCopyBranch branch,
        ServerProfile profile, Guid backupId, WorldBackupService backups)
    {
        lock (sync)
        {
            try
            {
                RequirePublicationBranch(branch, profile);
                var source = new PostStopBackupCaptureAdapter(backups).ReadVerified(profile, backupId);
                if (source.Setup is null || source.Kind != SharedWorldCaptureKinds.PostStopBackup)
                    throw new InvalidDataException("The completed recovery backup lacks a reviewed setup checkpoint.");
                return PublishVerifiedPayload(branch, profile, source, 4, () => RequirePublicationBranch(branch, profile),
                    nativeCopy: false, CancellationToken.None);
            }
            catch (Exception ex) when (SharedWorldBranchLiveSource.ExpectedFailure(ex))
            {
                return new(false, "RecoverySnapshotPublishFailed",
                    "The completed backup remains local; the recovery branch snapshot needs review.");
            }
        }
    }

    // A record alone is not evidence. The managed store must verify its protected
    // seal, exact native run and current setup before any sealed bytes are used.
    // The caller supplies the protected six-stage acceptance recheck; it cannot
    // be omitted or replaced by a game-wide enable switch.
    internal SharedWorldResult PublishLiveCapture(WorldSeparateCopyBranch branch, ServerProfile profile,
        ManagedRun run, ImmutableLiveSaveSnapshot snapshot, ManagedLiveSnapshotStore snapshots,
        Action requireAcceptedScope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requireAcceptedScope);
        lock (sync)
        {
            try
            {
                var context = SharedWorldBranchLiveSource.Resolve(data, profile, requireStarted: true);
                if (context.Branch?.BranchHash != branch.BranchHash)
                    throw new InvalidDataException("The sealed capture belongs to another recovery branch.");
                void VerifySource()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = SharedWorldBranchLiveSource.Resolve(data, profile, requireStarted: true);
                    if (!SharedWorldBranchLiveSource.SameSource(context, current) ||
                        !snapshots.Verify(profile, run, snapshot, cancellationToken))
                        throw new InvalidDataException("The exact sealed recovery snapshot, native run or current access changed.");
                    requireAcceptedScope();
                }
                VerifySource();
                var setup = ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data));
                var source = new VerifiedSharedWorldCapture(1, SharedWorldCaptureKinds.LiveSave,
                    snapshot.SnapshotId, snapshot.Completion.CompletedUtc, snapshot.Files, snapshot.SnapshotDirectory, setup);
                return PublishVerifiedPayload(branch, profile, source, 5, VerifySource, nativeCopy: true, cancellationToken);
            }
            catch (Exception ex) when (SharedWorldBranchLiveSource.ExpectedFailure(ex))
            {
                return new(false, "RecoveryLiveSnapshotPublishFailed",
                    "The sealed copy remains private or retained for review; no recovery or default-world pointer was advanced.");
            }
        }
    }

    private void RequirePublicationBranch(WorldSeparateCopyBranch branch, ServerProfile profile)
    {
        if (!Verify(branch) || !CandidateCurrentlyAuthorized(data, branch.Offer) || !HasLocalBranchKeys(data, branch) ||
            profile.SeparateCopySourceProfileId != branch.Offer.Proposal.ProfileId ||
            profile.SeparateCopyBranchHash != branch.BranchHash || profile.SharedSavesEnabled ||
            profile.Kind != branch.Offer.Version.Game || profile.WorldId != branch.Offer.Version.WorldId)
            throw new InvalidDataException("The local world does not match the signed recovery branch.");
    }

    private SharedWorldResult PublishVerifiedPayload(WorldSeparateCopyBranch branch, ServerProfile profile,
        VerifiedSharedWorldCapture source, int schema, Action verifySource, bool nativeCopy, CancellationToken token)
    {
        verifySource();
        RequirePublicationBranch(branch, profile);
        if (source.Setup is null || source.BackupId == Guid.Empty ||
            (schema == 5 ? source.Kind != SharedWorldCaptureKinds.LiveSave : source.Kind != SharedWorldCaptureKinds.PostStopBackup))
            throw new InvalidDataException("Choose a verified completed recovery capture with its reviewed setup.");
        var root = PublishedRoot(branch);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        var previous = ReadPublished(branch)?.Version ?? branch.Offer.Version;
        RequireCommittedPublications(root, branch, previous);
        if (previous.BackupId == source.BackupId && previous.CaptureKind == source.Kind)
            return new(true, "RecoverySnapshotPublished", "This verified recovery snapshot is already available.", previous);
        var portable = SharedWorldPortableSetupReader.Capture(source.Setup);
        if (nativeCopy && SharedWorldFreshness.SameCapture(source.Files, portable, previous.Files, previous.PortableSetup))
        {
            VerifyPublishedPayload(branch, previous, token);
            verifySource();
            return new(true, "RecoverySnapshotUnchanged",
                "The sealed capture matches this branch's complete verified copy. Its original save date remains visible.", previous);
        }
        if (nativeCopy && source.CapturedUtc <= previous.CreatedUtc)
            throw new InvalidDataException("The sealed capture predates the recovery branch's published head.");
        var keyBytes = data.LoadProtected(WorldAuthorityStore.HostingKeyName(branch.Offer.Proposal.ProfileId));
        if (keyBytes is null) throw new InvalidDataException("This PC's recovery hosting key is missing.");
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(keyBytes, out var consumed);
        if (consumed != keyBytes.Length || Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != branch.Offer.Proposal.CandidatePublicKey)
            throw new InvalidDataException("This PC's recovery hosting key changed.");
        var version = SharedWorldService.SignVersion(new SharedWorldVersion(schema, branch.Offer.Version.GroupId,
            checked(previous.Number + 1), previous.VersionHash, branch.Offer.Version.ProfileId,
            profile.Kind, profile.WorldId, source.CapturedUtc, source.Kind, source.BackupId, portable, source.Files, "", "", ""), key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(version, Json);
        if (bytes.Length > SharedWorldService.MaximumManifestBytes || !VerifySnapshotVersion(branch, version) ||
            !SharedWorldReadiness.HasSpaceForCopies(root, SharedWorldService.BoundedTotalBytes(version.Files), 1))
            throw new InvalidDataException("The recovery snapshot is oversized, invalid, or needs more free space.");
        using var rootPin = nativeCopy ? new SharedWorldBranchLiveSource.DirectoryPins(data, root, create: true) : null;
        if (!nativeCopy) Directory.CreateDirectory(root);
        var stage = Path.Combine(root, ".stage-" + version.VersionHash);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, stage);
        if (Directory.Exists(stage) || File.Exists(stage)) throw new InvalidDataException("An interrupted recovery publication needs review.");
        using (var stagePin = nativeCopy ? new SharedWorldBranchLiveSource.DirectoryPins(data, stage, create: true) : null)
        {
            if (!nativeCopy) Directory.CreateDirectory(stage);
            foreach (var file in version.Files)
            {
                token.ThrowIfCancellationRequested();
                var input = SharedWorldService.SafeChild(source.PayloadRoot, file.Path);
                var target = SharedWorldService.SafeChild(Path.Combine(stage, SharedWorldService.PayloadDirectory), file.Path);
                CopyPublicationFile(input, target, file, nativeCopy, token);
            }
            if (nativeCopy)
            {
                VerifyNativePayload(Path.Combine(stage, SharedWorldService.PayloadDirectory), version.Files, token);
                WriteNativeFile(stage, "version.json", bytes);
                WriteNativeFile(stage, "branch.json", JsonSerializer.SerializeToUtf8Bytes(branch, Json));
            }
            else
            {
                File.WriteAllBytes(Path.Combine(stage, "version.json"), bytes);
                File.WriteAllBytes(Path.Combine(stage, "branch.json"), JsonSerializer.SerializeToUtf8Bytes(branch, Json));
            }
            verifySource();
        }
        var destination = Path.Combine(root, version.VersionHash);
        Directory.Move(stage, destination);
        // Keep a signed interrupted directory for review if any later check fails.
        FriendLink.KeepSignedManifest(root, version);
        if (nativeCopy) VerifyNativePayload(Path.Combine(destination, SharedWorldService.PayloadDirectory), version.Files, token);
        var pointerName = "latest.json.new-" + Guid.NewGuid().ToString("N");
        var pointer = Path.Combine(root, pointerName);
        if (nativeCopy) WriteNativeFile(root, pointerName, bytes);
        else File.WriteAllBytes(pointer, bytes);
        verifySource();
        RequirePublicationBranch(branch, profile);
        token.ThrowIfCancellationRequested();
        File.Move(pointer, Path.Combine(root, "latest.json"), true);
        return new(true, "RecoverySnapshotPublished",
            "The complete verified snapshot is available only within its recovery branch. Other histories remain preserved.", version);
    }

    private void RequireCommittedPublications(string root, WorldSeparateCopyBranch branch, SharedWorldVersion previous)
    {
        if (!Directory.Exists(root)) return;
        var committed = previous.VersionHash == branch.Offer.Version.VersionHash ? new Dictionary<long, string>() :
            FriendLink.ReadVerifiedReceivedManifestLineage(root, previous, branch.Offer.Version).ToDictionary(item => item.Number, item => item.VersionHash);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (name == "signed-history") continue;
            if (!SharedWorldPeerTrust.ValidHash(name)) throw new InvalidDataException("An interrupted recovery publication needs review.");
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, directory);
            var path = Path.Combine(directory, "version.json");
            if (!File.Exists(path) || new FileInfo(path).Length > SharedWorldService.MaximumManifestBytes ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("An interrupted recovery publication needs review.");
            var recorded = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json);
            if (recorded is null || !VerifySnapshotVersion(branch, recorded) ||
                !committed.TryGetValue(recorded.Number, out var hash) || hash != name || recorded.VersionHash != name)
                throw new InvalidDataException("An uncommitted or conflicting recovery publication needs review.");
        }
    }

    private void VerifyPublishedPayload(WorldSeparateCopyBranch branch, SharedWorldVersion version, CancellationToken token)
    {
        var root = version.VersionHash == branch.Offer.Version.VersionHash ? RetainedRoot(branch) : PublishedRoot(branch);
        VerifyNativePayload(Path.Combine(root, version.VersionHash, SharedWorldService.PayloadDirectory), version.Files, token);
    }

    private void WriteNativeFile(string directory, string name, byte[] bytes)
    {
        using var parent = new SharedWorldBranchLiveSource.DirectoryPins(data, directory);
        using var output = new FileStream(NativeSnapshotDirectory.CreateNewFile(parent.Handle, name), FileAccess.Write);
        output.Write(bytes); output.Flush(true);
    }

    private void CopyPublicationFile(string source, string target, SharedWorldFile expected, bool nativeCopy, CancellationToken token)
    {
        if (!nativeCopy)
        {
            SharedWorldService.VerifyFile(source, expected);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, SharedWorldService.ChunkBytes, FileOptions.WriteThrough))
                input.CopyTo(output);
            SharedWorldService.VerifyFile(target, expected);
            return;
        }
        using var sourceParent = new SharedWorldBranchLiveSource.DirectoryPins(data, Path.GetDirectoryName(source)!);
        using var targetParent = new SharedWorldBranchLiveSource.DirectoryPins(data, Path.GetDirectoryName(target)!, create: true);
        using var inputFile = new FileStream(NativeSnapshotDirectory.OpenReadFile(sourceParent.Handle, Path.GetFileName(source)), FileAccess.Read);
        using var outputFile = new FileStream(NativeSnapshotDirectory.CreateNewFile(targetParent.Handle, Path.GetFileName(target)), FileAccess.Write);
        if (inputFile.Length != expected.Length || (File.GetAttributes(inputFile.SafeFileHandle) & FileAttributes.ReadOnly) == 0)
            throw new InvalidDataException("The protected sealed snapshot changed.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; long copied = 0; int count;
        while ((count = inputFile.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested(); copied = checked(copied + count);
            if (copied > expected.Length) throw new InvalidDataException("The sealed source grew during copying.");
            hash.AppendData(buffer, 0, count); outputFile.Write(buffer, 0, count);
        }
        if (copied != expected.Length || Convert.ToHexString(hash.GetHashAndReset()) != expected.Sha256)
            throw new InvalidDataException("The held sealed source does not match its protected hash.");
        outputFile.Flush(true);
        File.SetAttributes(outputFile.SafeFileHandle, File.GetAttributes(outputFile.SafeFileHandle) | FileAttributes.ReadOnly);
    }

    private void VerifyNativePayload(string payload, IReadOnlyList<SharedWorldFile> files, CancellationToken token)
    {
        using var payloadPin = new SharedWorldBranchLiveSource.DirectoryPins(data, payload);
        var expected = files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((payload, 0)); var directories = 0;
        while (pending.TryDequeue(out var entry))
        {
            token.ThrowIfCancellationRequested();
            if (++directories > 4096 || entry.Depth > 64) throw new InvalidDataException("The recovery payload has too many directories.");
            using var directory = new SharedWorldBranchLiveSource.DirectoryPins(data, entry.Path);
            foreach (var path in Directory.EnumerateFileSystemEntries(entry.Path))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The recovery payload contains a link.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Enqueue((path, entry.Depth + 1)); continue; }
                var relative = Path.GetRelativePath(payload, path).Replace('\\', '/');
                if (!expected.Remove(relative, out var file)) throw new InvalidDataException("The recovery payload contains an unexpected file.");
                using var input = new FileStream(NativeSnapshotDirectory.OpenReadFile(directory.Handle, Path.GetFileName(path)), FileAccess.Read);
                if (input.Length != file.Length) throw new InvalidDataException("The recovery payload length changed.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024]; int count;
                while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
                if (Convert.ToHexString(hash.GetHashAndReset()) != file.Sha256) throw new InvalidDataException("The recovery payload hash changed.");
            }
        }
        if (expected.Count != 0) throw new InvalidDataException("The recovery payload is incomplete.");
    }

    internal SharedWorldVersion ReadPublishedManifest(WorldSeparateCopyBranch branch,
        long number, string versionHash, bool verifyPayload = true)
    {
        lock (sync)
        {
            if (!Verify(branch)) throw new InvalidDataException("The signed recovery branch is invalid.");
            if (branch.Offer.Version is { } source && source.Number == number && source.VersionHash == versionHash)
            {
                if (verifyPayload && FriendLink.ReadReceivedLatest(RetainedRoot(branch))?.VersionHash != versionHash)
                    throw new InvalidDataException("The retained recovery base is unavailable.");
                return source;
            }
            var latest = verifyPayload ? ReadPublished(branch)?.Version : ReadPublishedManifestHead(branch);
            if (latest is null || versionHash is not { Length: 64 } || !versionHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("The selected recovery snapshot is unavailable.");
            if (latest.VersionHash == versionHash && latest.Number == number) return latest;
            var root = PublishedRoot(branch);
            var manifest = FriendLink.ReadVerifiedReceivedManifestLineage(root, latest, branch.Offer.Version)
                .SingleOrDefault(item => item.VersionHash == versionHash && item.Number == number);
            return manifest ?? throw new InvalidDataException("The selected snapshot does not belong to this recovery branch.");
        }
    }

    internal byte[] ReadPublishedChunk(WorldSeparateCopyBranch branch, long number,
        string versionHash, int fileIndex, long offset)
    {
        lock (sync)
        {
            if (!CandidateCurrentlyAuthorized(data, branch.Offer))
                throw new InvalidDataException("The recovery host's current save and hosting permissions are unavailable.");
            var version = ReadPublishedManifest(branch, number, versionHash);
            if (fileIndex < 0 || fileIndex >= version.Files.Count || offset < 0 ||
                offset % SharedWorldService.ChunkBytes != 0 || offset >= version.Files[fileIndex].Length)
                throw new InvalidDataException("The recovery chunk selection is invalid.");
            var file = version.Files[fileIndex];
            var root = version.VersionHash == branch.Offer.Version.VersionHash
                ? RetainedRoot(branch) : PublishedRoot(branch);
            var path = SharedWorldService.SafeChild(Path.Combine(root, versionHash,
                SharedWorldService.PayloadDirectory), file.Path);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
            SharedWorldService.VerifyFile(path, file);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            input.Position = offset;
            var bytes = new byte[(int)Math.Min(SharedWorldService.ChunkBytes, file.Length - offset)];
            input.ReadExactly(bytes);
            return bytes;
        }
    }

    internal WorldSeparateCopySnapshot ReadLatestSnapshot(WorldSeparateCopyBranch branch)
    {
        lock (sync)
        {
            var published = ReadPublished(branch);
            if (published is not null) return published;
            var source = ReadPublishedManifest(branch, branch.Offer.Version.Number, branch.Offer.Version.VersionHash);
            return new(branch, source);
        }
    }

    internal IReadOnlyList<WorldSeparateCopyBranch> Read(Guid profileId)
    {
        lock (sync) return ReadSet(profileId)?.Branches ?? [];
    }

    // A response from the original Host is retained for review. It does not
    // revoke a manually declared branch or authorize stopping either history.
    internal void MarkHostReturned(Guid profileId)
    {
        lock (sync)
        {
            var set = ReadSet(profileId);
            if (set is null || set.Branches.Count == 0) return;
            var returned = set.ReturnedBranches ?? [];
            if (set.Branches.All(branch => returned.Contains(branch.BranchHash))) return;
            SaveSet(profileId, set with
            {
                ReturnedBranches = set.Branches.Select(branch => branch.BranchHash).ToArray()
            });
        }
    }

    internal bool HostReturned(Guid profileId, string branchHash)
    {
        lock (sync)
        {
            var set = ReadSet(profileId);
            return set is null || !set.Branches.Any(branch => branch.BranchHash == branchHash) ||
                (set.ReturnedBranches?.Contains(branchHash) ?? false);
        }
    }

    private void SaveSet(Guid profileId, BranchSet set)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(set, Json);
        if (bytes.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Separate history record is oversized.");
        // Advance the protected floor first. A crash before the branch set
        // write blocks Start instead of hiding a returned Host or a fork.
        data.SaveProtected(FloorName(profileId),
            JsonSerializer.SerializeToUtf8Bytes(new BranchFloor(1, set.Branches.Count,
                Convert.ToHexString(SHA256.HashData(bytes))), Json));
        data.SaveProtected(Name(profileId), bytes);
    }

    private BranchSet? ReadSet(Guid profileId)
    {
        var bytes = data.LoadProtected(Name(profileId));
        var floorBytes = data.LoadProtected(FloorName(profileId));
        if (bytes is null)
        {
            if (data.HasProtected(Name(profileId)) || floorBytes is not null ||
                data.HasProtected(FloorName(profileId)))
                throw new InvalidDataException("Separate history could not be read.");
            return null;
        }
        if (bytes.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Separate history is oversized.");
        var floor = floorBytes is null ? null : JsonSerializer.Deserialize<BranchFloor>(floorBytes, Json);
        var set = JsonSerializer.Deserialize<BranchSet>(bytes, Json);
        if (floor is null || floor.Schema != 1 ||
            floor.Sha256 != Convert.ToHexString(SHA256.HashData(bytes)) ||
            set is null || floor.Count != set.Branches.Count ||
            set.Schema != 1 || set.ProfileId != profileId ||
            set.GroupId == Guid.Empty || set.Branches.Count > 20 ||
            set.Branches.Select(branch => branch.BranchId).Distinct().Count() != set.Branches.Count ||
            set.ReturnedBranches is { } returned &&
                (returned.Distinct(StringComparer.Ordinal).Count() != returned.Count ||
                 returned.Any(hash => !set.Branches.Any(branch => branch.BranchHash == hash))) ||
            set.Branches.Any(branch => branch.Offer.Roster.GroupId != set.GroupId ||
                branch.Offer.Roster.ProfileId != profileId || !Verify(branch)))
            throw new InvalidDataException("Separate history failed verification.");
        return set;
    }
}

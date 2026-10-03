using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

// Authority changes are explicit decisions about an exact, signed save head.
// They do not copy, merge, restore, or start a game server.
public sealed record WorldAuthorityProposal(int Schema, Guid GroupId, Guid ProfileId,
    long Epoch, string? ParentAuthorityHash, string RosterHash, string VersionHash,
    string CandidatePublicKey, string CandidateAddress, string Kind,
    Guid ProposerDeviceId, string ProposerPublicKey, string Signature,
    WorldSuccessorBinding? SuccessorBinding = null,
    IReadOnlyList<string>? CompetingHeadHashes = null);
public sealed record WorldSuccessorBinding(Guid DeviceId, string DevicePublicKey,
    string HostingPublicKey, string Signature);
public sealed record WorldAuthorityVote(int Schema, string ProposalHash, Guid VoterDeviceId,
    string VoterPublicKey, string Signature);
public sealed record WorldAuthorityRecord(int Schema, WorldAuthorityProposal Proposal,
    SharedWorldRoster Roster, SharedWorldVersion Version,
    IReadOnlyList<WorldAuthorityVote> Votes, string? OwnerSignature, string RecordHash,
    SharedWorldReceipt? SuccessorReceipt = null,
    IReadOnlyList<SharedWorldVersion>? VersionLineage = null,
    string? VersionLineageDigest = null);

internal static class WorldAuthorityTrust
{
    internal static bool IsResolution(WorldAuthorityRecord record) => record.Schema == 2;
    internal static WorldAuthorityRecord[] EffectiveHeads(IReadOnlyList<WorldAuthorityRecord> records)
    {
        var heads = new Dictionary<string, WorldAuthorityRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (IsResolution(record))
            {
                if (!ExactResolutionHeads(record, heads.Values.ToArray()))
                    throw new InvalidDataException("The resolution does not name every competing authority head.");
                foreach (var hash in record.Proposal.CompetingHeadHashes!) heads.Remove(hash);
            }
            else if (record.Proposal.ParentAuthorityHash is { } parent)
                heads.Remove(parent);
            heads.Add(record.RecordHash, record);
        }
        return heads.Values.ToArray();
    }
    internal static bool ExactResolutionHeads(WorldAuthorityRecord record,
        IReadOnlyList<WorldAuthorityRecord> heads)
    {
        if (!IsResolution(record) || record.Proposal.CompetingHeadHashes is not { } hashes ||
            heads.Count < 2 || hashes.Count != heads.Count ||
            record.Proposal.Epoch != heads.Max(head => head.Proposal.Epoch) + 1 ||
            !hashes.SequenceEqual(heads.Select(head => head.RecordHash)
                .Order(StringComparer.Ordinal)) ||
            heads.Any(head => head.Proposal.GroupId != record.Proposal.GroupId ||
                head.Proposal.ProfileId != record.Proposal.ProfileId ||
                head.Roster.OwnerPublicKey != record.Roster.OwnerPublicKey ||
                head.Roster.Epoch > record.Roster.Epoch ||
                head.Roster.Revision > record.Roster.Revision ||
                head.Roster.Epoch == record.Roster.Epoch &&
                head.Roster.Revision == record.Roster.Revision &&
                head.Roster.Signature != record.Roster.Signature)) return false;
        return heads.Any(head => head.RecordHash == record.Proposal.ParentAuthorityHash);
    }
    internal static string CandidateDevicePublicKey(WorldAuthorityProposal proposal) =>
        proposal.SuccessorBinding?.DevicePublicKey ?? proposal.CandidatePublicKey;
    internal const int PageSize = 4;
    internal const int MaximumAuthorityRecords = 128;
    internal const int MaximumProofVersions = 4096;
    internal const int ProofVersionsPerCheck = 128;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string RosterHash(SharedWorldRoster roster) => Hash(JsonSerializer.SerializeToUtf8Bytes(roster, Json));
    internal static byte[] BindingBasis(WorldAuthorityProposal proposal, WorldSuccessorBinding binding) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer successor hosting binding v1", proposal.GroupId,
            proposal.ProfileId, proposal.Epoch, proposal.ParentAuthorityHash,
            proposal.RosterHash, proposal.VersionHash, binding.DeviceId,
            binding.DevicePublicKey, binding.HostingPublicKey
        }, Json);
    internal static byte[] ProposalBasis(WorldAuthorityProposal proposal) => proposal.Schema == 3
        ? JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "TogetherServer authority resolution proposal v3", proposal.Schema,
        proposal.GroupId, proposal.ProfileId, proposal.Epoch, proposal.ParentAuthorityHash,
        proposal.RosterHash, proposal.VersionHash, proposal.CandidatePublicKey,
        proposal.CandidateAddress, proposal.Kind, proposal.ProposerDeviceId,
        proposal.ProposerPublicKey, proposal.SuccessorBinding, proposal.CompetingHeadHashes
    }, Json) : proposal.Schema == 1
        ? JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "TogetherServer authority proposal v1", proposal.Schema, proposal.GroupId,
        proposal.ProfileId, proposal.Epoch, proposal.ParentAuthorityHash, proposal.RosterHash,
        proposal.VersionHash, proposal.CandidatePublicKey, proposal.CandidateAddress,
        proposal.Kind, proposal.ProposerDeviceId, proposal.ProposerPublicKey
    }, Json) : JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "TogetherServer authority proposal v2", proposal.Schema, proposal.GroupId,
        proposal.ProfileId, proposal.Epoch, proposal.ParentAuthorityHash, proposal.RosterHash,
        proposal.VersionHash, proposal.CandidatePublicKey, proposal.CandidateAddress,
        proposal.Kind, proposal.ProposerDeviceId, proposal.ProposerPublicKey,
        proposal.SuccessorBinding
    }, Json);
    internal static string ProposalHash(WorldAuthorityProposal proposal) => Hash(ProposalBasis(proposal));
    internal static byte[] VoteBasis(WorldAuthorityVote vote) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "TogetherServer authority vote v1", vote.Schema, vote.ProposalHash,
        vote.VoterDeviceId, vote.VoterPublicKey
    }, Json);
    internal static byte[] OwnerBasis(WorldAuthorityProposal proposal) => Encoding.UTF8.GetBytes(
        "TogetherServer authority owner approval v1\n" + ProposalHash(proposal));
    internal static byte[] RecordBasis(WorldAuthorityRecord record) => record.Schema == 2
        ? JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer authority resolution record v2", record.Schema,
            record.Proposal, record.Roster, record.Version, record.Votes,
            record.OwnerSignature, record.VersionLineageDigest
        }, Json) :
        record.VersionLineageDigest is not null && record.SuccessorReceipt is null
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                domain = "TogetherServer authority record v2", record.Schema, record.Proposal,
                record.Roster, record.Version, record.Votes, record.OwnerSignature,
                record.VersionLineageDigest
            }, Json)
            : record.VersionLineageDigest is not null
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                domain = "TogetherServer authority record v2", record.Schema, record.Proposal,
                record.Roster, record.Version, record.Votes, record.OwnerSignature,
                record.SuccessorReceipt, record.VersionLineageDigest
            }, Json)
            : record.SuccessorReceipt is null && record.VersionLineage is null
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                domain = "TogetherServer authority record v1", record.Schema, record.Proposal,
                record.Roster, record.Version, record.Votes, record.OwnerSignature
            }, Json)
            : record.SuccessorReceipt is not null && record.VersionLineage is null
                ? JsonSerializer.SerializeToUtf8Bytes(new
                {
                    domain = "TogetherServer authority record v1", record.Schema, record.Proposal,
                    record.Roster, record.Version, record.Votes, record.OwnerSignature,
                    record.SuccessorReceipt
                }, Json)
                : record.SuccessorReceipt is null
                    ? JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        domain = "TogetherServer authority record v1", record.Schema, record.Proposal,
                        record.Roster, record.Version, record.Votes, record.OwnerSignature,
                        record.VersionLineage
                    }, Json)
                    : JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        domain = "TogetherServer authority record v1", record.Schema, record.Proposal,
                        record.Roster, record.Version, record.Votes, record.OwnerSignature,
                        record.SuccessorReceipt, record.VersionLineage
                    }, Json);
    private static bool Signature(string key, byte[] basis, string signature)
    {
        try
        {
            if (!SharedWorldRosterTrust.ValidKey(key)) return false;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            return ecdsa.VerifyData(basis, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }
    // A fixed-size commitment to an arbitrarily long, ordered sequence of signed manifests.
    // Each manifest remains a separately bounded proof piece.
    internal static string LineageDigest(IEnumerable<SharedWorldVersion> versions)
    {
        var digest = LineageSeed();
        foreach (var version in versions)
        {
            if (!SharedWorldService.VerifySignature(version) || version.VersionHash.Length != 64)
                throw new InvalidDataException("Authority lineage manifest is invalid.");
            digest = AdvanceLineage(digest, version);
        }
        return Convert.ToHexString(digest);
    }
    private static byte[] LineageSeed() =>
        SHA256.HashData(Encoding.UTF8.GetBytes("TogetherServer version lineage v1"));
    private static byte[] AdvanceLineage(byte[] digest, SharedWorldVersion version) =>
        SHA256.HashData([.. digest, .. Convert.FromHexString(version.VersionHash)]);
    internal static bool VerifyProposal(WorldAuthorityProposal proposal, SharedWorldRoster roster)
    {
        if (proposal.Schema is not (1 or 2 or 3) || proposal.Epoch < 1 || proposal.GroupId != roster.GroupId ||
            proposal.ProfileId != roster.ProfileId || proposal.RosterHash != RosterHash(roster) ||
            !Signature(proposal.ProposerPublicKey, ProposalBasis(proposal), proposal.Signature)) return false;
        if (proposal.Schema != 3 && proposal.CompetingHeadHashes is not null) return false;
        if (proposal.Schema == 3 && (proposal.Kind is not ("ResolutionQuorum" or "ResolutionOwnerOverride") ||
            proposal.CompetingHeadHashes is not { Count: >= 2 and <= MaximumAuthorityRecords } hashes ||
            hashes.Any(hash => hash.Length != 64 || !hash.All(Uri.IsHexDigit)) ||
            hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Count ||
            !hashes.SequenceEqual(hashes.Order(StringComparer.Ordinal)) ||
            !hashes.Contains(proposal.ParentAuthorityHash))) return false;
        if (proposal.Schema == 1 && proposal.SuccessorBinding is not null) return false;
        if (proposal.Schema is 2 or 3)
        {
            var binding = proposal.SuccessorBinding;
            if (binding is null || binding.DeviceId == Guid.Empty ||
                binding.HostingPublicKey != proposal.CandidatePublicKey ||
                binding.HostingPublicKey == roster.OwnerPublicKey ||
                !SharedWorldRosterTrust.ValidKey(binding.HostingPublicKey) ||
                roster.Members.SingleOrDefault(member => member.DeviceId == binding.DeviceId) is not
                    { Revoked: false, Grants.EligibleHost: true } candidate ||
                candidate.PublicKey != binding.DevicePublicKey ||
                !Signature(binding.DevicePublicKey, BindingBasis(proposal, binding), binding.Signature))
                return false;
        }
        if (proposal.Kind == "Planned")
            return proposal.ProposerPublicKey == roster.OwnerPublicKey &&
                proposal.ProposerDeviceId == Guid.Empty;
        var proposer = roster.Members.SingleOrDefault(member => member.DeviceId == proposal.ProposerDeviceId);
        return proposal.Kind is "Quorum" or "OwnerOverride" or
            "ResolutionQuorum" or "ResolutionOwnerOverride" &&
            proposer is { Revoked: false, Grants.EligibleHost: true } &&
            proposer.PublicKey == proposal.ProposerPublicKey;
    }
    internal static bool VerifyVote(WorldAuthorityVote vote, WorldAuthorityProposal proposal,
        SharedWorldRoster roster)
    {
        if (vote.Schema != 1 || vote.ProposalHash != ProposalHash(proposal)) return false;
        var member = roster.Members.SingleOrDefault(item => item.DeviceId == vote.VoterDeviceId);
        return member is { Revoked: false, Grants.RecoveryVoter: true } &&
            member.PublicKey == vote.VoterPublicKey &&
            Signature(vote.VoterPublicKey, VoteBasis(vote), vote.Signature);
    }
    internal static bool Verify(WorldAuthorityRecord? record)
    {
        try
        {
            if (record is null || record.Schema is not (1 or 2) ||
                record.Proposal.Schema is not (1 or 2 or 3) ||
                (record.Schema == 2) != (record.Proposal.Schema == 3) ||
                record.Schema == 2 && (record.VersionLineage is not null ||
                    record.SuccessorReceipt is not null) ||
                record.Proposal.GroupId == Guid.Empty || record.Proposal.ProfileId == Guid.Empty ||
                record.Proposal.Epoch < 1 || record.Votes.Count > 128 ||
                record.VersionLineage is { Count: > 64 } ||
                record.VersionLineageDigest is not null &&
                    (record.VersionLineage is not null || record.VersionLineageDigest.Length != 64 ||
                     !record.VersionLineageDigest.All(Uri.IsHexDigit)) ||
                record.VersionLineage is not null && record.VersionLineage.Any(version =>
                    !SharedWorldService.VerifySignature(version)) ||
                !SharedWorldRosterTrust.Verify(record.Roster) ||
                !SharedWorldService.VerifySignature(record.Version) ||
                record.Proposal.GroupId != record.Roster.GroupId ||
                record.Proposal.GroupId != record.Version.GroupId ||
                record.Proposal.ProfileId != record.Roster.ProfileId ||
                record.Proposal.ProfileId != record.Version.ProfileId ||
                record.Proposal.VersionHash != record.Version.VersionHash ||
                record.Proposal.RosterHash != RosterHash(record.Roster) ||
                !SharedWorldRosterTrust.ValidKey(record.Proposal.CandidatePublicKey) ||
                record.Proposal.CandidateAddress.Length is < 3 or > 255 ||
                !Uri.TryCreate(record.Proposal.CandidateAddress, UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0 ||
                !VerifyProposal(record.Proposal, record.Roster) ||
                record.RecordHash != Hash(RecordBasis(record))) return false;
            var candidate = record.Roster.Members.SingleOrDefault(member =>
                member.PublicKey == (record.Proposal.SuccessorBinding?.DevicePublicKey ??
                    record.Proposal.CandidatePublicKey));
            if (candidate is not { Revoked: false, Grants.EligibleHost: true }) return false;
            var ownerApproved = record.OwnerSignature is not null &&
                Signature(record.Roster.OwnerPublicKey, OwnerBasis(record.Proposal), record.OwnerSignature);
            if (record.Proposal.Kind == "Planned")
            {
                var receipt = record.SuccessorReceipt;
                return ownerApproved && record.Votes.Count == 0 &&
                    record.Proposal.ProposerPublicKey == record.Roster.OwnerPublicKey &&
                    receipt is not null && receipt.GroupId == record.Proposal.GroupId &&
                    receipt.ProfileId == record.Proposal.ProfileId &&
                    receipt.VersionHash == record.Version.VersionHash &&
                    receipt.DeviceId == candidate.DeviceId &&
                    receipt.RosterEpoch == record.Roster.Epoch &&
                    receipt.RosterRevision == record.Roster.Revision &&
                    candidate.Grants.Receive && SharedWorldReceiptTrust.Verify(receipt, candidate.PublicKey);
            }
            if (record.SuccessorReceipt is not null) return false;
            if (record.Proposal.Kind is not ("Quorum" or "OwnerOverride" or
                "ResolutionQuorum" or "ResolutionOwnerOverride")) return false;
            if (record.Proposal.Kind is "OwnerOverride" or "ResolutionOwnerOverride")
                return record.Roster.OwnerOverride && ownerApproved && record.Votes.Count == 0;
            if (record.OwnerSignature is not null ||
                record.Votes.Select(vote => vote.VoterDeviceId).Distinct().Count() != record.Votes.Count ||
                record.Votes.Any(vote => !VerifyVote(vote, record.Proposal, record.Roster))) return false;
            // Eligibility is fixed by the signed roster in the record. An access
            // deadline passing later must not invalidate an already accepted fence.
            var voters = record.Roster.Members.Count(member => !member.Revoked &&
                member.Grants.RecoveryVoter);
            return voters > 0 && record.Votes.Count >= voters / 2 + 1;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   NullReferenceException or JsonException or OverflowException)
        { return false; }
    }
    internal static bool VerifyLineage(WorldAuthorityRecord record, WorldAuthorityRecord? parent,
        IEnumerable<SharedWorldVersion>? externalLineage = null)
    {
        if (record.VersionLineageDigest is not null && externalLineage is null) return false;
        var digest = record.VersionLineageDigest is null ? null : LineageSeed();
        IEnumerable<SharedWorldVersion>? lineage = record.VersionLineageDigest is null ?
            record.VersionLineage : externalLineage;
        if (parent is null)
        {
            if (record.Proposal.Epoch != 1 || record.Proposal.ParentAuthorityHash is not null ||
                record.Version.SigningPublicKey != record.Roster.OwnerPublicKey) return false;
            // Legacy first decisions carry only the exact head. New decisions can
            // commit the entire earlier owner history so a successor can serve it.
            if (lineage is null) return record.VersionLineageDigest is null;
            SharedWorldVersion? previousOwner = null;
            foreach (var version in lineage)
            {
                if (version.Number != (previousOwner?.Number ?? 0) + 1 ||
                    version.ParentHash != previousOwner?.VersionHash ||
                    version.SigningPublicKey != record.Roster.OwnerPublicKey ||
                    version.GroupId != record.Version.GroupId ||
                    version.ProfileId != record.Version.ProfileId ||
                    version.Game != record.Version.Game || version.WorldId != record.Version.WorldId ||
                    !SharedWorldService.VerifySignature(version)) return false;
                if (digest is not null) digest = AdvanceLineage(digest, version);
                previousOwner = version;
            }
            return previousOwner?.VersionHash == record.Version.VersionHash &&
                (digest is null || Convert.ToHexString(digest) == record.VersionLineageDigest);
        }
        if (record.Proposal.ParentAuthorityHash != parent.RecordHash ||
            (record.Schema == 1 && record.Proposal.Epoch != parent.Proposal.Epoch + 1 ||
             record.Schema == 2 && record.Proposal.Epoch <= parent.Proposal.Epoch) ||
            record.Proposal.GroupId != parent.Proposal.GroupId ||
            record.Version.Game != parent.Version.Game ||
            record.Version.WorldId != parent.Version.WorldId) return false;
        if (record.Version.VersionHash == parent.Version.VersionHash)
            return lineage is null &&
                record.Version.SigningPublicKey == parent.Version.SigningPublicKey;
        if (lineage is null) return false;
        var previous = parent.Version;
        var hadVersion = false;
        foreach (var version in lineage)
        {
            hadVersion = true;
            if (version.SigningPublicKey != parent.Proposal.CandidatePublicKey ||
                version.GroupId != previous.GroupId || version.ProfileId != previous.ProfileId ||
                version.Game != previous.Game || version.WorldId != previous.WorldId ||
                version.ParentHash != previous.VersionHash || version.Number != previous.Number + 1 ||
                !SharedWorldService.VerifySignature(version)) return false;
            if (digest is not null) digest = AdvanceLineage(digest, version);
            previous = version;
        }
        return hadVersion && previous.VersionHash == record.Version.VersionHash &&
            previous.SigningPublicKey == record.Version.SigningPublicKey &&
            (digest is null || Convert.ToHexString(digest) == record.VersionLineageDigest);
    }
}

internal sealed class WorldAuthorityStore(LocalData data, TimeProvider? clock = null)
{
    private sealed record Floor(int Schema, int Count, string LogHash);
    private sealed record PendingAppend(int Schema, int OldCount, string OldHash,
        int OldLength, int NewCount, string NewHash, string Line);
    private sealed record LocalVoteEntry(string? Parent, long Epoch, Guid Voter, WorldAuthorityVote Vote);
    private sealed record LocalHostBinding(int Schema, Guid GroupId, string RecordHash,
        Guid DeviceId, string DevicePublicKey, string HostingPublicKey);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private string Root(Guid profileId)
    {
        var root = Path.Combine(data.RootPath, "shared-worlds", profileId.ToString("N"), "authority");
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        return root;
    }
    private static string FloorName(Guid profileId) => $"authority-floor-{profileId:N}.protected";
    private static string VoteFloorName(Guid profileId) => $"authority-vote-floor-{profileId:N}.protected";
    private static string HostBindingName(Guid profileId) => $"authority-host-{profileId:N}.protected";
    private static string PendingResolutionName(Guid profileId) =>
        $"authority-resolution-offers-{profileId:N}.protected";
    internal WorldAuthorityOwnerApproval OwnerApproval(Guid profileId,
        WorldAuthorityProposal proposal, Func<WorldAuthorityOwnerApproval> sign)
    {
        lock (sync)
        {
            var hash = WorldAuthorityTrust.ProposalHash(proposal);
            var name = $"authority-resolution-approval-{profileId:N}-{hash}.protected";
            var prior = data.LoadProtected(name);
            if (prior is null && data.HasProtected(name))
                throw new InvalidDataException("Saved owner approval is unreadable.");
            if (prior is not null)
            {
                var saved = JsonSerializer.Deserialize<WorldAuthorityOwnerApproval>(prior, Json);
                if (saved?.ProposalHash != hash)
                    throw new InvalidDataException("Saved owner approval changed.");
                return saved;
            }
            var approval = sign();
            if (approval.ProposalHash != hash)
                throw new InvalidDataException("Owner approval did not bind this proposal.");
            data.SaveProtected(name, JsonSerializer.SerializeToUtf8Bytes(approval, Json));
            return approval;
        }
    }
    internal IReadOnlyList<WorldAuthorityOffer> PendingResolutionOffers(Guid profileId)
    {
        var bytes = data.LoadProtected(PendingResolutionName(profileId));
        if (bytes is null)
        {
            if (data.HasProtected(PendingResolutionName(profileId)))
                throw new InvalidDataException("Pending resolutions are unreadable.");
            return [];
        }
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Pending resolutions are oversized.");
        var offers = JsonSerializer.Deserialize<List<WorldAuthorityOffer>>(bytes, Json) ??
            throw new InvalidDataException("Pending resolutions are invalid.");
        if (offers.Count > 16 || offers.Any(offer => !SharedWorldElection.VerifyOffer(offer) ||
                offer.Proposal.Kind != "ResolutionOwnerOverride" ||
                offer.Proposal.ProfileId != profileId))
            throw new InvalidDataException("Pending resolutions failed verification.");
        var heads = WorldAuthorityTrust.EffectiveHeads(Read(profileId));
        return offers.Where(offer => WorldAuthorityTrust.ExactResolutionHeads(
            new WorldAuthorityRecord(2, offer.Proposal, offer.Roster, offer.Version,
                [], null, ""), heads)).ToArray();
    }
    internal void StageResolutionOffer(WorldAuthorityOffer offer)
    {
        lock (sync)
        {
            var profileId = offer.Proposal.ProfileId;
            if (!SharedWorldElection.VerifyOffer(offer) ||
                offer.Proposal.Kind != "ResolutionOwnerOverride" ||
                !offer.Roster.OwnerOverride ||
                !WorldAuthorityTrust.ExactResolutionHeads(new WorldAuthorityRecord(2,
                    offer.Proposal, offer.Roster, offer.Version, [], null, ""),
                    WorldAuthorityTrust.EffectiveHeads(Read(profileId))))
                throw new InvalidDataException("The owner offer does not match the verified split.");
            var existing = PendingResolutionOffers(profileId);
            if (existing.Any(item => WorldAuthorityTrust.ProposalHash(item.Proposal) ==
                    WorldAuthorityTrust.ProposalHash(offer.Proposal))) return;
            if (existing.Count >= 16) throw new InvalidDataException("Pending resolution limit reached.");
            data.SaveProtected(PendingResolutionName(profileId),
                JsonSerializer.SerializeToUtf8Bytes(existing.Append(offer).ToArray(), Json));
        }
    }
    private string LogPath(Guid profileId) => Path.Combine(Root(profileId), "records.jsonl");
    private string PendingPath(Guid profileId) => Path.Combine(Root(profileId), "append.pending");
    private string VotePath(Guid profileId) => Path.Combine(Root(profileId), "local-votes.jsonl");
    private string ProofRoot(Guid profileId, string recordHash) =>
        Path.Combine(Root(profileId), "proof-" + recordHash);
    private string StagedProofRoot(WorldAuthorityRecord record)
    {
        if (record.RecordHash is not { Length: 64 } || !record.RecordHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Authority proof record hash is invalid.");
        return Path.Combine(Root(record.Proposal.ProfileId), "proof-stage-" + record.RecordHash);
    }
    internal static long FirstProofNumber(WorldAuthorityRecord record, WorldAuthorityRecord? parent)
    {
        if (parent is not null && parent.Version.Number >= record.Version.Number)
            throw new InvalidDataException("Authority lineage length is invalid.");
        var first = parent is null && record.Proposal.ParentAuthorityHash is null ? 1 :
            parent?.Version.Number + 1 ?? throw new InvalidDataException("Authority parent is missing.");
        if (record.Version.Number < first ||
            record.Version.Number - first + 1 > WorldAuthorityTrust.MaximumProofVersions)
            throw new InvalidDataException("Authority lineage exceeds the supported proof window.");
        return first;
    }
    internal SharedWorldVersion? ReadStagedProofVersion(WorldAuthorityRecord record, long number)
    {
        var root = StagedProofRoot(record);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        var path = SharedWorldService.SafeChild(root,
            number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > SharedWorldService.MaximumManifestBytes)
            throw new InvalidDataException("Staged authority proof is oversized.");
        var version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json);
        if (version is null || !SharedWorldService.VerifySignature(version) ||
            version.Number != number || version.GroupId != record.Version.GroupId ||
            version.ProfileId != record.Version.ProfileId || version.Game != record.Version.Game ||
            version.WorldId != record.Version.WorldId)
            throw new InvalidDataException("Staged authority proof failed verification.");
        return version;
    }
    internal void StageReceivedProofVersion(WorldAuthorityRecord record, SharedWorldVersion version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorldAuthorityTrust.Verify(record) || !SharedWorldService.VerifySignature(version) ||
            version.GroupId != record.Version.GroupId || version.ProfileId != record.Version.ProfileId ||
            version.Game != record.Version.Game || version.WorldId != record.Version.WorldId ||
            version.Number < 1 || version.Number > record.Version.Number)
            throw new InvalidDataException("Authority proof piece is invalid.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(version, Json);
        if (bytes.Length > SharedWorldService.MaximumManifestBytes)
            throw new InvalidDataException("Authority proof piece is oversized.");
        var root = StagedProofRoot(record);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        Directory.CreateDirectory(root);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        var name = version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json";
        var path = SharedWorldService.SafeChild(root, name);
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Staged authority proof changed.");
            return;
        }
        var temporary = SharedWorldService.SafeChild(root, Guid.NewGuid().ToString("N") + ".new");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
            SharedWorldService.SafeChild(root, name);
            File.Move(temporary, path, false);
        }
        finally
        {
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
            SharedWorldService.SafeChild(root, Path.GetFileName(temporary));
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    internal IEnumerable<SharedWorldVersion> ReadStagedProof(WorldAuthorityRecord record,
        WorldAuthorityRecord? parent)
    {
        var first = FirstProofNumber(record, parent);
        for (var number = first; ; number++)
        {
            yield return ReadStagedProofVersion(record, number) ??
                throw new InvalidDataException("Staged authority proof is incomplete.");
            if (number == record.Version.Number) yield break;
        }
    }
    internal void ClearStagedProof(WorldAuthorityRecord record, WorldAuthorityRecord? parent)
    {
        var root = StagedProofRoot(record);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        if (!Directory.Exists(root)) return;
        var first = FirstProofNumber(record, parent);
        for (var number = first; ; number++)
        {
            var path = SharedWorldService.SafeChild(root,
                number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
            if (File.Exists(path)) File.Delete(path);
            if (number == record.Version.Number) break;
        }
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }
    private IEnumerable<SharedWorldVersion>? ReadProof(WorldAuthorityRecord record,
        WorldAuthorityRecord? parent)
    {
        if (record.VersionLineageDigest is null) return null;
        if (parent is not null && parent.Version.Number >= record.Version.Number)
            throw new InvalidDataException("Authority lineage length is invalid.");
        var firstNumber = parent is null ? 1 : parent.Version.Number + 1;
        if (record.Version.Number < firstNumber)
            throw new InvalidDataException("Authority lineage length is invalid.");
        var root = ProofRoot(record.Proposal.ProfileId, record.RecordHash);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Authority lineage proof is missing or linked.");
        return Enumerate();

        IEnumerable<SharedWorldVersion> Enumerate()
        {
            for (var number = firstNumber; ; number++)
            {
                var path = Path.Combine(root, number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
                if (!File.Exists(path) || new FileInfo(path).Length > SharedWorldService.MaximumManifestBytes ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Authority lineage proof is missing or linked.");
                yield return JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json) ??
                    throw new InvalidDataException("Authority lineage proof is invalid.");
                if (number == record.Version.Number) yield break;
            }
        }
    }
    private IEnumerable<SharedWorldVersion> ReadLocalPublishedLineage(
        WorldAuthorityRecord record, WorldAuthorityRecord? parent)
    {
        if (parent is not null && parent.Version.Number >= record.Version.Number)
            throw new InvalidDataException("Authority lineage length is invalid.");
        var firstNumber = parent is null ? 1 : parent.Version.Number + 1;
        if (record.Version.Number < firstNumber)
            throw new InvalidDataException("Authority lineage length is invalid.");
        for (var number = firstNumber; ; number++)
        {
            var path = Path.Combine(data.RootPath, "shared-worlds",
                record.Proposal.ProfileId.ToString("N"), record.Proposal.GroupId.ToString("N"),
                number.ToString(System.Globalization.CultureInfo.InvariantCulture), "version.json");
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || new FileInfo(path).Length > SharedWorldService.MaximumManifestBytes ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A published authority lineage manifest is missing or linked.");
            yield return JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json) ??
                throw new InvalidDataException("A published authority lineage manifest is invalid.");
            if (number == record.Version.Number) yield break;
        }
    }
    internal bool HasState(Guid profileId) => data.HasProtected(FloorName(profileId)) ||
        File.Exists(LogPath(profileId)) || File.Exists(PendingPath(profileId));
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private IReadOnlyList<LocalVoteEntry> ReadLocalVotes(Guid profileId)
    {
        var path = VotePath(profileId);
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("Local vote log is oversized.");
        var floorBytes = data.LoadProtected(VoteFloorName(profileId));
        if (floorBytes is null)
        {
            if (bytes.Length != 0) throw new InvalidDataException("Local vote floor is missing.");
            return [];
        }
        var floor = JsonSerializer.Deserialize<Floor>(floorBytes, Json);
        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (floor is null || floor.Schema != 1 || floor.Count != lines.Length ||
            floor.LogHash != Digest(bytes))
            throw new InvalidDataException("Local vote history changed or was rolled back.");
        var votes = new List<LocalVoteEntry>();
        foreach (var line in lines)
        {
            var entry = JsonSerializer.Deserialize<LocalVoteEntry>(line, Json);
            if (entry is null || entry.Epoch < 1 || entry.Voter == Guid.Empty ||
                entry.Vote.VoterDeviceId != entry.Voter ||
                votes.Any(prior => prior.Voter == entry.Voter && prior.Epoch == entry.Epoch &&
                    prior.Parent == entry.Parent))
                throw new InvalidDataException("Saved local vote is invalid.");
            votes.Add(entry);
        }
        return votes;
    }
    private void RecoverPending(Guid profileId)
    {
        var pendingPath = PendingPath(profileId);
        if (!File.Exists(pendingPath)) return;
        if ((File.GetAttributes(pendingPath) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(pendingPath).Length > 512 * 1024)
            throw new InvalidDataException("Authority append journal is invalid.");
        var pending = JsonSerializer.Deserialize<PendingAppend>(File.ReadAllBytes(pendingPath), Json);
        var floorBytes = data.LoadProtected(FloorName(profileId));
        var floor = floorBytes is null ? null : JsonSerializer.Deserialize<Floor>(floorBytes, Json);
        if (pending is null || pending.Schema != 1 || floor is null || floor.Schema != 1 ||
            pending.OldCount < 0 || pending.NewCount != pending.OldCount + 1 ||
            pending.OldLength < 0 || pending.Line.Length > 400_000 ||
            !pending.Line.EndsWith('\n'))
            throw new InvalidDataException("Authority append journal failed verification.");
        var record = JsonSerializer.Deserialize<WorldAuthorityRecord>(pending.Line, Json);
        if (!WorldAuthorityTrust.Verify(record) || record!.Proposal.ProfileId != profileId)
            throw new InvalidDataException("Authority append journal has an invalid record.");
        var log = LogPath(profileId);
        if (File.Exists(log) && (File.GetAttributes(log) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Authority log is linked.");
        var bytes = File.Exists(log) ? File.ReadAllBytes(log) : [];
        if (floor.Count == pending.NewCount && floor.LogHash == pending.NewHash &&
            Digest(bytes) == pending.NewHash)
        {
            File.Delete(pendingPath);
            return;
        }
        if (floor.Count != pending.OldCount || floor.LogHash != pending.OldHash ||
            bytes.Length < pending.OldLength ||
            Digest(bytes[..pending.OldLength]) != pending.OldHash)
            throw new InvalidDataException("Authority append journal does not match the protected floor.");
        var line = Encoding.UTF8.GetBytes(pending.Line);
        var expected = new byte[pending.OldLength + line.Length];
        bytes.AsSpan(0, pending.OldLength).CopyTo(expected);
        line.CopyTo(expected, pending.OldLength);
        if (Digest(expected) != pending.NewHash || bytes.Length > expected.Length ||
            !bytes.AsSpan().SequenceEqual(expected.AsSpan(0, bytes.Length)))
            throw new InvalidDataException("Authority append journal does not match the log.");
        using (var stream = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(expected);
            stream.Flush(true);
        }
        data.SaveProtected(FloorName(profileId),
            JsonSerializer.SerializeToUtf8Bytes(new Floor(1, pending.NewCount, pending.NewHash), Json));
        File.Delete(pendingPath);
    }
    private bool EligibleAtAcceptance(WorldAuthorityRecord record)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        bool Active(Guid id) => record.Roster.Members.Single(member => member.DeviceId == id)
            .AccessExpiresUtc is not { } end || end > now;
        var candidate = record.Roster.Members.Single(member =>
            member.PublicKey == (record.Proposal.SuccessorBinding?.DevicePublicKey ??
                record.Proposal.CandidatePublicKey));
        return Active(candidate.DeviceId) &&
            (record.Proposal.Kind == "Planned" || Active(record.Proposal.ProposerDeviceId)) &&
            record.Votes.All(vote => Active(vote.VoterDeviceId));
    }
    private bool MatchesLocalSuccessor(WorldAuthorityRecord record)
    {
        var bytes = data.LoadProtected(HostBindingName(record.Proposal.ProfileId));
        if (bytes is null) return false;
        var binding = JsonSerializer.Deserialize<LocalHostBinding>(bytes, Json);
        if (binding is null || binding.Schema is not (1 or 2) ||
            binding.GroupId != record.Proposal.GroupId ||
            binding.RecordHash != record.RecordHash ||
            binding.HostingPublicKey != record.Proposal.CandidatePublicKey ||
            (binding.Schema == 2 && (record.Proposal.SuccessorBinding is not { } signed ||
                signed.DeviceId != binding.DeviceId || signed.DevicePublicKey != binding.DevicePublicKey) ||
             binding.Schema == 1 && (record.Proposal.Schema != 1 ||
                record.Proposal.Kind != "Planned" || record.SuccessorReceipt?.DeviceId != binding.DeviceId ||
                binding.DevicePublicKey != record.Proposal.CandidatePublicKey)))
            return false;
        var member = record.Roster.Members.SingleOrDefault(item => item.DeviceId == binding.DeviceId);
        if (member?.PublicKey != binding.DevicePublicKey) return false;
        var keyBytes = data.LoadProtected($"shared-world-pc-signing-{binding.DeviceId:N}.protected");
        if (keyBytes is null) return false;
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(keyBytes, out _);
        if (!ProvesPossession(key, binding.DevicePublicKey))
            return false;
        if (binding.Schema == 1) return true;
        var hostBytes = data.LoadProtected(HostingKeyName(record.Proposal.ProfileId));
        if (hostBytes is null) return false;
        using var hostKey = ECDsa.Create();
        hostKey.ImportPkcs8PrivateKey(hostBytes, out _);
        return ProvesPossession(hostKey, binding.HostingPublicKey);
    }
    private static bool ProvesPossession(ECDsa privateKey, string expectedPublicKey)
    {
        if (Convert.ToBase64String(privateKey.ExportSubjectPublicKeyInfo()) != expectedPublicKey)
            return false;
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(expectedPublicKey), out _);
        var challenge = RandomNumberGenerator.GetBytes(32);
        return verifier.VerifyData(challenge,
            privateKey.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256);
    }
    internal static string HostingKeyName(Guid profileId) =>
        $"shared-world-host-signing-{profileId:N}.protected";
    internal string PrepareLocalHostingKey(Guid profileId)
    {
        lock (sync)
        {
            var bytes = data.LoadProtected(HostingKeyName(profileId));
            if (bytes is null)
            {
                using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                bytes = created.ExportPkcs8PrivateKey();
                data.SaveProtected(HostingKeyName(profileId), bytes);
            }
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(bytes, out _);
            return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
    }
    internal WorldAuthorityRecord? LocalAuthorizedHead(Guid profileId)
    {
        var records = Read(profileId);
        if (records.Count == 0) return null;
        var heads = WorldAuthorityTrust.EffectiveHeads(records);
        return heads.Length == 1 && MatchesLocalSuccessor(heads[0]) ? heads[0] : null;
    }
    internal void BindLocalSuccessor(Guid profileId, string recordHash, Guid deviceId)
    {
        lock (sync)
        {
            var record = Read(profileId).SingleOrDefault(item => item.RecordHash == recordHash)
                ?? throw new InvalidDataException("The verified authority record is missing.");
            var member = record.Roster.Members.SingleOrDefault(item => item.DeviceId == deviceId);
            var plannedLegacy = record.Proposal.Schema == 1 &&
                record.Proposal.Kind == "Planned" && record.SuccessorReceipt?.DeviceId == deviceId &&
                member?.PublicKey == record.Proposal.CandidatePublicKey;
            var signed = record.Proposal.SuccessorBinding;
            if (!plannedLegacy && (record.Proposal.Schema is not (2 or 3) || signed is null ||
                signed.DeviceId != deviceId || member?.PublicKey != signed.DevicePublicKey))
                throw new InvalidDataException("This PC is not the signed successor.");
            var binding = new LocalHostBinding(plannedLegacy ? 1 : 2,
                record.Proposal.GroupId, recordHash, deviceId, member!.PublicKey,
                signed?.HostingPublicKey ?? member.PublicKey);
            // The proof of possession is checked before writing and again at each Start.
            var keyBytes = data.LoadProtected($"shared-world-pc-signing-{deviceId:N}.protected");
            if (keyBytes is null) throw new InvalidDataException("Successor PC identity is missing.");
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(keyBytes, out _);
            if (!ProvesPossession(key, member.PublicKey))
                throw new InvalidDataException("Successor PC identity does not match the signed roster.");
            if (!plannedLegacy && PrepareLocalHostingKey(profileId) != signed!.HostingPublicKey)
                throw new InvalidDataException("This PC does not own the authorized hosting key.");
            data.SaveProtected(HostBindingName(profileId), JsonSerializer.SerializeToUtf8Bytes(binding, Json));
        }
    }
    internal void AppendReceived(WorldAuthorityRecord record, Guid expectedProfileId,
        Guid expectedGroupId, string pinnedOwnerPublicKey,
        IEnumerable<SharedWorldVersion>? externalLineage = null)
    {
        if (!SharedWorldRosterTrust.ValidKey(pinnedOwnerPublicKey) ||
            record.Roster.OwnerPublicKey != pinnedOwnerPublicKey ||
            record.Proposal.ProfileId != expectedProfileId ||
            record.Proposal.GroupId != expectedGroupId)
            throw new InvalidDataException("Authority does not match the pinned shared world.");
        // A received historical decision is verified by signatures and lineage.
        // Its participants' grants may have expired since it was accepted.
        Append(record, enforceCurrentGrants: false, externalLineage: externalLineage);
    }
    internal IReadOnlyList<WorldAuthorityRecord> Read(Guid profileId)
    {
        lock (sync)
        {
            RecoverPending(profileId);
            var path = LogPath(profileId);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Authority log is linked.");
            var floorBytes = data.LoadProtected(FloorName(profileId));
            if (floorBytes is null)
            {
                if (File.Exists(path)) throw new InvalidDataException("Authority floor is missing.");
                return [];
            }
            var floor = JsonSerializer.Deserialize<Floor>(floorBytes, Json);
            if (floor is null || floor.Schema != 1 || floor.Count < 0)
                throw new InvalidDataException("Authority floor is invalid.");
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != floor.Count || floor.LogHash != Digest(bytes))
                throw new InvalidDataException("Authority log changed or was rolled back.");
            var records = new List<WorldAuthorityRecord>();
            foreach (var line in lines)
            {
                var record = JsonSerializer.Deserialize<WorldAuthorityRecord>(line, Json);
                if (!WorldAuthorityTrust.Verify(record) || record!.Proposal.ProfileId != profileId)
                    throw new InvalidDataException("Authority record failed verification.");
                var parent = records.SingleOrDefault(item =>
                    item.RecordHash == record.Proposal.ParentAuthorityHash);
                if (!WorldAuthorityTrust.IsResolution(record) &&
                    records.Any(WorldAuthorityTrust.IsResolution) &&
                    (parent is null || !WorldAuthorityTrust.EffectiveHeads(records)
                        .Any(head => head.RecordHash == parent.RecordHash)))
                    throw new InvalidDataException("A retired authority head cannot gain a new child.");
                if (WorldAuthorityTrust.IsResolution(record) &&
                    !WorldAuthorityTrust.ExactResolutionHeads(record,
                        WorldAuthorityTrust.EffectiveHeads(records)))
                    throw new InvalidDataException("Authority resolution head set failed verification.");
                if (!WorldAuthorityTrust.VerifyLineage(record, parent, ReadProof(record, parent)))
                    throw new InvalidDataException("Authority save signer lineage failed verification.");
                records.Add(record);
            }
            return records;
        }
    }
    internal WorldAuthorityRecord? ReadUniqueHead(Guid profileId)
    {
        var heads = WorldAuthorityTrust.EffectiveHeads(Read(profileId));
        return heads.Length == 1 ? heads[0] : null;
    }

    internal SharedWorldVersion? FindProvenVersion(Guid profileId, long number)
    {
        var records = Read(profileId);
        var effective = WorldAuthorityTrust.EffectiveHeads(records);
        if (effective.Length == 1)
        {
            var head = effective[0];
            if (head.Version.Number == number) return head.Version;
            var parent = records.SingleOrDefault(item =>
                item.RecordHash == head.Proposal.ParentAuthorityHash);
            var proof = head.VersionLineageDigest is null ? head.VersionLineage :
                ReadProof(head, parent);
            var chosen = proof?.FirstOrDefault(item => item.Number == number);
            if (chosen is not null) return chosen;
        }
        foreach (var record in records)
        {
            if (record.Version.Number == number) return record.Version;
            var parent = records.SingleOrDefault(item =>
                item.RecordHash == record.Proposal.ParentAuthorityHash);
            var lineage = record.VersionLineageDigest is null ? record.VersionLineage :
                ReadProof(record, parent);
            var version = lineage?.FirstOrDefault(item => item.Number == number);
            if (version is not null) return version;
        }
        return null;
    }
    internal void Append(WorldAuthorityRecord record, bool stopAfterLogForChecks = false,
        bool stopAfterJournalForChecks = false, bool enforceCurrentGrants = true,
        IEnumerable<SharedWorldVersion>? externalLineage = null)
    {
        lock (sync)
        {
            if (!WorldAuthorityTrust.Verify(record)) throw new InvalidDataException("Authority proof is invalid.");
            var existing = Read(record.Proposal.ProfileId);
            if (existing.Any(item => item.RecordHash == record.RecordHash)) return;
            if (existing.Count >= WorldAuthorityTrust.MaximumAuthorityRecords)
                throw new InvalidDataException("Authority history reached its supported record limit.");
            if (enforceCurrentGrants && !EligibleAtAcceptance(record))
                throw new InvalidDataException("A successor, proposer, or voter grant has expired.");
            var parentRecord = existing.SingleOrDefault(item =>
                item.RecordHash == record.Proposal.ParentAuthorityHash);
            if (!WorldAuthorityTrust.IsResolution(record) &&
                existing.Any(WorldAuthorityTrust.IsResolution) &&
                (parentRecord is null || !WorldAuthorityTrust.EffectiveHeads(existing)
                    .Any(head => head.RecordHash == parentRecord.RecordHash)))
                throw new InvalidDataException("A retired authority head cannot gain a new child.");
            if (WorldAuthorityTrust.IsResolution(record) &&
                !WorldAuthorityTrust.ExactResolutionHeads(record,
                    WorldAuthorityTrust.EffectiveHeads(existing)))
                throw new InvalidDataException("Resolution must name the complete verified head set.");
            if (record.VersionLineageDigest is not null)
                FirstProofNumber(record, parentRecord);
            if (record.VersionLineageDigest is not null && externalLineage is null && enforceCurrentGrants)
                externalLineage = ReadLocalPublishedLineage(record, parentRecord);
            if (!WorldAuthorityTrust.VerifyLineage(record, parentRecord, externalLineage))
                throw new InvalidDataException("Authority save signer lineage is invalid.");
            if (existing.Any(item => item.Proposal.GroupId != record.Proposal.GroupId ||
                item.Roster.OwnerPublicKey != record.Roster.OwnerPublicKey))
                throw new InvalidDataException("Authority group identity changed.");
            if (parentRecord is not null && (record.Roster.Epoch < parentRecord.Roster.Epoch ||
                record.Roster.Revision < parentRecord.Roster.Revision))
                throw new InvalidDataException("Authority uses an older roster.");
            var path = LogPath(record.Proposal.ProfileId);
            Directory.CreateDirectory(Root(record.Proposal.ProfileId));
            if (record.VersionLineageDigest is not null)
            {
                var proofRoot = ProofRoot(record.Proposal.ProfileId, record.RecordHash);
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, proofRoot);
                Directory.CreateDirectory(proofRoot);
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, proofRoot);
                foreach (var version in externalLineage!)
                {
                    SharedWorldService.EnsureUnlinkedRoot(data.RootPath, proofRoot);
                    var proofPath = SharedWorldService.SafeChild(proofRoot,
                        version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
                    var proofBytes = JsonSerializer.SerializeToUtf8Bytes(version, Json);
                    if (proofBytes.Length > SharedWorldService.MaximumManifestBytes)
                        throw new InvalidDataException("Authority lineage manifest is oversized.");
                    if (File.Exists(proofPath))
                    {
                        if (!File.ReadAllBytes(proofPath).AsSpan().SequenceEqual(proofBytes))
                            throw new InvalidDataException("Authority lineage proof changed.");
                    }
                    else
                    {
                        using var proof = new FileStream(proofPath, FileMode.CreateNew,
                            FileAccess.Write, FileShare.None);
                        proof.Write(proofBytes);
                        proof.Flush(true);
                    }
                }
            }
            if (!data.HasProtected(FloorName(record.Proposal.ProfileId)))
                data.SaveProtected(FloorName(record.Proposal.ProfileId),
                    JsonSerializer.SerializeToUtf8Bytes(new Floor(1, 0, Digest([])), Json));
            var line = JsonSerializer.Serialize(record, Json) + "\n";
            var old = File.Exists(path) ? File.ReadAllBytes(path) : [];
            var next = new byte[old.Length + Encoding.UTF8.GetByteCount(line)];
            old.CopyTo(next, 0);
            Encoding.UTF8.GetBytes(line).CopyTo(next, old.Length);
            var pending = new PendingAppend(1, existing.Count, Digest(old), old.Length,
                existing.Count + 1, Digest(next), line);
            var journalPath = PendingPath(record.Proposal.ProfileId);
            var journalBytes = JsonSerializer.SerializeToUtf8Bytes(pending, Json);
            if (journalBytes.Length > 512 * 1024 || line.Length > 400_000)
                throw new InvalidDataException("Authority record or append journal is oversized.");
            using (var journal = new FileStream(journalPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                journal.Write(journalBytes);
                journal.Flush(true);
            }
            if (stopAfterJournalForChecks) return;
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (stopAfterLogForChecks) return;
            data.SaveProtected(FloorName(record.Proposal.ProfileId),
                JsonSerializer.SerializeToUtf8Bytes(new Floor(1, existing.Count + 1, Digest(next)), Json));
            File.Delete(journalPath);
        }
    }
    internal WorldAuthorityVote SignLocalVote(Guid profileId, WorldAuthorityProposal proposal,
        SharedWorldRoster roster, Guid voterId, ECDsa signer)
    {
        lock (sync)
        {
            if (!SharedWorldRosterTrust.Verify(roster) || !WorldAuthorityTrust.VerifyProposal(proposal, roster) ||
                proposal.Kind is not ("Quorum" or "ResolutionQuorum") ||
                proposal.ProfileId != profileId)
                throw new InvalidDataException("Vote roster or group is invalid.");
            var key = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(proposal), voterId, key, "");
            var vote = draft with { Signature = Convert.ToBase64String(signer.SignData(
                WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256)) };
            if (!WorldAuthorityTrust.VerifyVote(vote, proposal, roster))
                throw new InvalidDataException("Voter is not approved in this roster.");
            if (roster.Members.Single(member => member.DeviceId == voterId).AccessExpiresUtc is { } expiry &&
                expiry <= (clock ?? TimeProvider.System).GetUtcNow())
                throw new InvalidDataException("This PC's recovery vote access expired.");
            var existing = ReadLocalVotes(profileId);
            var voteScope = proposal.Schema == 3
                ? "resolution:" + string.Join(',', proposal.CompetingHeadHashes!)
                : proposal.ParentAuthorityHash;
            var prior = existing.SingleOrDefault(item => item.Voter == voterId &&
                item.Epoch == proposal.Epoch && item.Parent == voteScope);
            if (prior is not null)
            {
                if (prior.Vote.ProposalHash == vote.ProposalHash) return prior.Vote;
                throw new InvalidDataException("This PC already voted for a competing proposal.");
            }
            var path = VotePath(profileId);
            Directory.CreateDirectory(Root(profileId));
            if (!data.HasProtected(VoteFloorName(profileId)))
                data.SaveProtected(VoteFloorName(profileId),
                    JsonSerializer.SerializeToUtf8Bytes(new Floor(1, 0, Digest([])), Json));
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new LocalVoteEntry(
                voteScope, proposal.Epoch, voterId, vote), Json) + "\n");
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            // A crash before the protected floor advances fails closed; it can
            // never let this PC sign a different proposal for the same epoch.
            data.SaveProtected(VoteFloorName(profileId), JsonSerializer.SerializeToUtf8Bytes(
                new Floor(1, existing.Count + 1, Digest(File.ReadAllBytes(path))), Json));
            return vote;
        }
    }
    internal bool Fenced(Guid profileId, string localPublicKey, out string reason)
    {
        try
        {
            var records = Read(profileId);
            var heads = WorldAuthorityTrust.EffectiveHeads(records);
            if (heads.Length == 0) { reason = ""; return false; }
            // A public key string alone is no proof that this installation owns
            // the successor identity. Require the protected local binding.
            if (heads.Length > 1 || heads.Any(head => !MatchesLocalSuccessor(head)))
            {
                reason = "A verified takeover or competing authority was recorded. Keep this copy and gracefully stop the exact managed server before reviewing the histories.";
                return true;
            }
            reason = "";
            return false;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or CryptographicException)
        {
            reason = "Shared world authority could not be verified. Keep this world offline until its history is reviewed.";
            return true;
        }
    }
}

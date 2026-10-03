using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

// Authority changes are explicit decisions about an exact, signed save head.
// They do not copy, merge, restore, or start a game server.
public sealed record WorldAuthorityProposal(int Schema, Guid GroupId, Guid ProfileId,
    long Epoch, string? ParentAuthorityHash, string RosterHash, string VersionHash,
    string CandidatePublicKey, string CandidateAddress, string Kind,
    Guid ProposerDeviceId, string ProposerPublicKey, string Signature,
    WorldSuccessorBinding? SuccessorBinding = null);
public sealed record WorldSuccessorBinding(Guid DeviceId, string DevicePublicKey,
    string HostingPublicKey, string Signature);
public sealed record WorldAuthorityVote(int Schema, string ProposalHash, Guid VoterDeviceId,
    string VoterPublicKey, string Signature);
public sealed record WorldAuthorityRecord(int Schema, WorldAuthorityProposal Proposal,
    SharedWorldRoster Roster, SharedWorldVersion Version,
    IReadOnlyList<WorldAuthorityVote> Votes, string? OwnerSignature, string RecordHash,
    IReadOnlyList<SharedWorldVersion>? VersionLineage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? HostAcceptedUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? HostAcceptanceSignature = null);

internal static class WorldAuthorityTrust
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string RosterHash(SharedWorldRoster roster) => Hash(JsonSerializer.SerializeToUtf8Bytes(roster, Json));
    internal static byte[] HostAcceptanceBasis(WorldAuthorityRecord record, DateTimeOffset acceptedUtc) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer authority Host acceptance v1",
            record.RecordHash, record.Proposal.GroupId, record.Proposal.ProfileId,
            record.Proposal.Epoch, record.Proposal.ParentAuthorityHash,
            record.Proposal.RosterHash, record.Proposal.VersionHash,
            AcceptedUtc = acceptedUtc
        }, Json);
    internal static bool VerifyHostAcceptance(WorldAuthorityRecord record)
    {
        if (record.HostAcceptedUtc is not { } accepted ||
            record.HostAcceptanceSignature is null ||
            !Signature(record.Proposal.CandidatePublicKey,
                HostAcceptanceBasis(record, accepted), record.HostAcceptanceSignature) ||
            record.Version.CreatedUtc - accepted > TimeSpan.FromMinutes(5)) return false;
        bool Active(Guid id) => record.Roster.Members.Single(member => member.DeviceId == id)
            .AccessExpiresUtc is not { } expiry || expiry > accepted;
        try
        {
            var candidate = record.Proposal.SuccessorBinding?.DeviceId ??
                record.Roster.Members.Single(member =>
                    member.PublicKey == record.Proposal.CandidatePublicKey).DeviceId;
            return Active(candidate) &&
                (record.Proposal.Kind == "Planned" || Active(record.Proposal.ProposerDeviceId)) &&
                record.Votes.All(vote => Active(vote.VoterDeviceId));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        { return false; }
    }
    internal static byte[] BindingBasis(WorldAuthorityProposal proposal, WorldSuccessorBinding binding) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer successor hosting binding v1", proposal.GroupId,
            proposal.ProfileId, proposal.Epoch, proposal.ParentAuthorityHash,
            proposal.RosterHash, proposal.VersionHash, binding.DeviceId,
            binding.DevicePublicKey, binding.HostingPublicKey
        }, Json);
    internal static byte[] ProposalBasis(WorldAuthorityProposal proposal) => proposal.Schema == 1
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
    internal static byte[] RecordBasis(WorldAuthorityRecord record) => record.VersionLineage is null
        ? JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer authority record v1", record.Schema, record.Proposal,
            record.Roster, record.Version, record.Votes, record.OwnerSignature
        }, Json)
        : JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer authority record v1", record.Schema, record.Proposal,
            record.Roster, record.Version, record.Votes, record.OwnerSignature,
            record.VersionLineage
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
    internal static bool VerifyProposal(WorldAuthorityProposal proposal, SharedWorldRoster roster)
    {
        if (proposal.Schema is not (1 or 2) || proposal.Epoch < 1 || proposal.GroupId != roster.GroupId ||
            proposal.ProfileId != roster.ProfileId || proposal.RosterHash != RosterHash(roster) ||
            !Signature(proposal.ProposerPublicKey, ProposalBasis(proposal), proposal.Signature)) return false;
        if (proposal.Schema == 1 && proposal.SuccessorBinding is not null) return false;
        if (proposal.Schema == 2)
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
        return proposal.Kind is "Quorum" or "OwnerOverride" &&
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
    internal static bool Verify(WorldAuthorityRecord? record, bool rosterChainVerified = false)
    {
        try
        {
            if (record is null || record.Schema != 1 || record.Proposal.Schema is not (1 or 2) ||
                record.Proposal.GroupId == Guid.Empty || record.Proposal.ProfileId == Guid.Empty ||
                record.Proposal.Epoch < 1 || record.Votes.Count > 128 ||
                record.VersionLineage is { Count: > 64 } ||
                record.VersionLineage is not null && record.VersionLineage.Any(version =>
                    !SharedWorldService.VerifySignature(version)) ||
                !(rosterChainVerified && record.Roster.Schema == 3
                    ? SharedWorldRosterTrust.VerifySignature(record.Roster)
                    : SharedWorldRosterTrust.Verify(record.Roster)) ||
                !SharedWorldService.VerifySignature(record.Version) ||
                record.Proposal.GroupId != record.Roster.GroupId ||
                record.Proposal.GroupId != record.Version.GroupId ||
                record.Proposal.ProfileId != record.Roster.ProfileId ||
                record.Proposal.ProfileId != record.Version.ProfileId ||
                record.Proposal.VersionHash != record.Version.VersionHash ||
                record.Proposal.RosterHash != RosterHash(record.Roster) ||
                record.Roster.Schema == 3 && !VerifyHostAcceptance(record) ||
                record.Roster.Schema < 3 &&
                    (record.HostAcceptedUtc is not null || record.HostAcceptanceSignature is not null) &&
                    !VerifyHostAcceptance(record) ||
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
                return ownerApproved && record.Votes.Count == 0 &&
                    record.Proposal.ProposerPublicKey == record.Roster.OwnerPublicKey;
            if (record.Proposal.Kind is not ("Quorum" or "OwnerOverride")) return false;
            if (record.Proposal.Kind == "OwnerOverride")
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
    internal static bool VerifyLineage(WorldAuthorityRecord record, WorldAuthorityRecord? parent)
    {
        if (parent is null)
            return record.Proposal.Epoch == 1 && record.Proposal.ParentAuthorityHash is null &&
                record.Version.SigningPublicKey == record.Roster.OwnerPublicKey &&
                record.VersionLineage is null;
        if (record.Proposal.ParentAuthorityHash != parent.RecordHash ||
            record.Proposal.Epoch != parent.Proposal.Epoch + 1 ||
            record.Proposal.GroupId != parent.Proposal.GroupId ||
            record.Version.Game != parent.Version.Game ||
            record.Version.WorldId != parent.Version.WorldId) return false;
        if (record.Version.VersionHash == parent.Version.VersionHash)
            return record.VersionLineage is null &&
                record.Version.SigningPublicKey == parent.Version.SigningPublicKey;
        if (record.VersionLineage is not { Count: > 0 } chain ||
            chain[^1].VersionHash != record.Version.VersionHash) return false;
        var previous = parent.Version;
        foreach (var version in chain)
        {
            if (version.SigningPublicKey != parent.Proposal.CandidatePublicKey ||
                version.GroupId != previous.GroupId || version.ProfileId != previous.ProfileId ||
                version.Game != previous.Game || version.WorldId != previous.WorldId ||
                version.ParentHash != previous.VersionHash || version.Number != previous.Number + 1 ||
                !SharedWorldService.VerifySignature(version)) return false;
            previous = version;
        }
        return previous.VersionHash == record.Version.VersionHash &&
            previous.SigningPublicKey == record.Version.SigningPublicKey;
    }
}

// Pairing membership writes and authority publication share one in-process gate.
// The first authority append is the point at which a PC loses owner roster writes.
internal static class SharedWorldMutationGate
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    internal static object For(string root) => Gates.GetOrAdd(Path.GetFullPath(root), _ => new object());
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
    private static string HostBindingName(Guid profileId) => $"authority-host-{profileId:N}.protected";
    private string LogPath(Guid profileId) => Path.Combine(Root(profileId), "records.jsonl");
    private string PendingPath(Guid profileId) => Path.Combine(Root(profileId), "append.pending");
    private string VotePath(Guid profileId) => Path.Combine(Root(profileId), "local-votes.jsonl");
    internal bool HasState(Guid profileId) => data.HasProtected(FloorName(profileId)) ||
        File.Exists(LogPath(profileId)) || File.Exists(PendingPath(profileId));
    internal bool GovernanceUnresolved(Guid profileId) => File.Exists(Path.Combine(
        data.RootPath, "shared-worlds", profileId.ToString("N"), "roster-dirty"));
    private bool TrustedRoster(SharedWorldRoster roster, bool requireCurrent)
    {
        var chain = new SharedWorldRosterChainStore(data, clock);
        if (!chain.HasState(roster.ProfileId))
            return SharedWorldRosterTrust.Verify(roster);
        var history = chain.Read(roster.ProfileId);
        var heads = chain.Heads(roster.ProfileId);
        if (heads.Count != 1 || history.Count == 0 ||
            history[0].OwnerPublicKey != roster.OwnerPublicKey ||
            history[0].GroupId != roster.GroupId ||
            history.All(item => SharedWorldRosterTrust.Hash(item) != SharedWorldRosterTrust.Hash(roster)))
            return false;
        return !requireCurrent ||
            SharedWorldRosterTrust.Hash(heads[0]) == SharedWorldRosterTrust.Hash(roster);
    }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
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
        if (record is null || !TrustedRoster(record.Roster, false) ||
            !WorldAuthorityTrust.Verify(record, true) || record.Proposal.ProfileId != profileId)
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
    private WorldAuthorityRecord AttestLocalAcceptance(WorldAuthorityRecord record)
    {
        if (record.Roster.Schema != 3 || record.HostAcceptanceSignature is not null ||
            record.HostAcceptedUtc is not null) return record;
        var bytes = data.LoadProtected(HostingKeyName(record.Proposal.ProfileId));
        if (bytes is null)
            throw new InvalidDataException("The candidate Host must attest this delegated authority decision.");
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(bytes, out _);
        if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != record.Proposal.CandidatePublicKey)
            throw new InvalidDataException("The candidate Host key does not match the authority proposal.");
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return record with { HostAcceptedUtc = now,
            HostAcceptanceSignature = Convert.ToBase64String(key.SignData(
                WorldAuthorityTrust.HostAcceptanceBasis(record, now), HashAlgorithmName.SHA256)) };
    }
    private bool MatchesLocalSuccessor(WorldAuthorityRecord record)
    {
        var bytes = data.LoadProtected(HostBindingName(record.Proposal.ProfileId));
        if (bytes is null) return false;
        var binding = JsonSerializer.Deserialize<LocalHostBinding>(bytes, Json);
        if (binding is null || binding.Schema != 2 || binding.GroupId != record.Proposal.GroupId ||
            binding.RecordHash != record.RecordHash ||
            binding.HostingPublicKey != record.Proposal.CandidatePublicKey ||
            record.Proposal.SuccessorBinding is not { } signed ||
            signed.DeviceId != binding.DeviceId || signed.DevicePublicKey != binding.DevicePublicKey)
            return false;
        var member = record.Roster.Members.SingleOrDefault(item => item.DeviceId == binding.DeviceId);
        if (member?.PublicKey != binding.DevicePublicKey) return false;
        var keyBytes = data.LoadProtected($"shared-world-pc-signing-{binding.DeviceId:N}.protected");
        if (keyBytes is null) return false;
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(keyBytes, out _);
        if (!ProvesPossession(key, binding.DevicePublicKey))
            return false;
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
        var heads = records.Where(record => !records.Any(child =>
            child.Proposal.ParentAuthorityHash == record.RecordHash)).ToArray();
        return heads.Length == 1 && MatchesLocalSuccessor(heads[0]) ? heads[0] : null;
    }
    internal void BindLocalSuccessor(Guid profileId, string recordHash, Guid deviceId)
    {
        lock (sync)
        {
            var record = Read(profileId).SingleOrDefault(item => item.RecordHash == recordHash)
                ?? throw new InvalidDataException("The verified authority record is missing.");
            var member = record.Roster.Members.SingleOrDefault(item => item.DeviceId == deviceId);
            if (record.Proposal.Schema != 2 || record.Proposal.SuccessorBinding is not { } signed ||
                signed.DeviceId != deviceId || member?.PublicKey != signed.DevicePublicKey)
                throw new InvalidDataException("This PC is not the signed successor.");
            var binding = new LocalHostBinding(2, record.Proposal.GroupId, recordHash,
                deviceId, member.PublicKey, signed.HostingPublicKey);
            // The proof of possession is checked before writing and again at each Start.
            var keyBytes = data.LoadProtected($"shared-world-pc-signing-{deviceId:N}.protected");
            if (keyBytes is null) throw new InvalidDataException("Successor PC identity is missing.");
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(keyBytes, out _);
            if (!ProvesPossession(key, member.PublicKey))
                throw new InvalidDataException("Successor PC identity does not match the signed roster.");
            if (PrepareLocalHostingKey(profileId) != signed.HostingPublicKey)
                throw new InvalidDataException("This PC does not own the authorized hosting key.");
            data.SaveProtected(HostBindingName(profileId), JsonSerializer.SerializeToUtf8Bytes(binding, Json));
        }
    }
    internal void AppendReceived(WorldAuthorityRecord record, Guid expectedProfileId,
        Guid expectedGroupId, string pinnedOwnerPublicKey)
    {
        if (!SharedWorldRosterTrust.ValidKey(pinnedOwnerPublicKey) ||
            record.Roster.OwnerPublicKey != pinnedOwnerPublicKey ||
            record.Proposal.ProfileId != expectedProfileId ||
            record.Proposal.GroupId != expectedGroupId)
            throw new InvalidDataException("Authority does not match the pinned shared world.");
        // A received historical decision is verified by signatures and lineage.
        // Its participants' grants may have expired since it was accepted.
        Append(record, enforceCurrentGrants: false);
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
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Authority log is oversized.");
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != floor.Count || floor.LogHash != Digest(bytes))
                throw new InvalidDataException("Authority log changed or was rolled back.");
            var records = new List<WorldAuthorityRecord>();
            foreach (var line in lines)
            {
                var record = JsonSerializer.Deserialize<WorldAuthorityRecord>(line, Json);
                if (record is null || !TrustedRoster(record.Roster, false) ||
                    !WorldAuthorityTrust.Verify(record, true) || record.Proposal.ProfileId != profileId)
                    throw new InvalidDataException("Authority record failed verification.");
                var parent = records.SingleOrDefault(item =>
                    item.RecordHash == record.Proposal.ParentAuthorityHash);
                if (!WorldAuthorityTrust.VerifyLineage(record, parent))
                    throw new InvalidDataException("Authority save signer lineage failed verification.");
                records.Add(record);
            }
            return records;
        }
    }
    internal void Append(WorldAuthorityRecord record, bool stopAfterLogForChecks = false,
        bool stopAfterJournalForChecks = false, bool enforceCurrentGrants = true)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        lock (sync)
        {
            if (enforceCurrentGrants && GovernanceUnresolved(record.Proposal.ProfileId))
                throw new InvalidDataException("Signed membership is unresolved; local takeover authority is blocked.");
            if (enforceCurrentGrants) record = AttestLocalAcceptance(record);
            if (enforceCurrentGrants && record.HostAcceptedUtc is { } acceptedUtc &&
                Math.Abs((acceptedUtc - (clock ?? TimeProvider.System).GetUtcNow()).TotalMinutes) > 5)
                throw new InvalidDataException("Host acceptance time is outside the local decision window.");
            if (!TrustedRoster(record.Roster, enforceCurrentGrants) ||
                !WorldAuthorityTrust.Verify(record, true))
                throw new InvalidDataException("Authority proof or current roster is invalid.");
            var existing = Read(record.Proposal.ProfileId);
            if (existing.Any(item => item.RecordHash == record.RecordHash)) return;
            // Historical decisions remain readable, but an unseen decision
            // cannot arrive after the current roster revoked its participants.
            // Candidate-signed time is not proof of an earlier decision.
            if (!TrustedRoster(record.Roster, true) || !EligibleAtAcceptance(record))
                throw new InvalidDataException("A first authority receipt needs the current roster and active participant grants.");
            var parentRecord = existing.SingleOrDefault(item =>
                item.RecordHash == record.Proposal.ParentAuthorityHash);
            if (!WorldAuthorityTrust.VerifyLineage(record, parentRecord))
                throw new InvalidDataException("Authority save signer lineage is invalid.");
            if (existing.Any(item => item.Proposal.GroupId != record.Proposal.GroupId ||
                item.Roster.OwnerPublicKey != record.Roster.OwnerPublicKey))
                throw new InvalidDataException("Authority group identity changed.");
            if (parentRecord is not null && (record.Roster.Epoch < parentRecord.Roster.Epoch ||
                record.Roster.Revision < parentRecord.Roster.Revision))
                throw new InvalidDataException("Authority uses an older roster.");
            var path = LogPath(record.Proposal.ProfileId);
            Directory.CreateDirectory(Root(record.Proposal.ProfileId));
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
            if (journalBytes.Length > 512 * 1024 || line.Length > 400_000 ||
                next.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Authority proof exceeds the bounded log.");
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
        lock (SharedWorldMutationGate.For(data.RootPath))
        lock (sync)
        {
            if (GovernanceUnresolved(profileId))
                throw new InvalidDataException("Signed membership is unresolved; this PC cannot vote on a takeover.");
            if (!TrustedRoster(roster, true) || !WorldAuthorityTrust.VerifyProposal(proposal, roster) ||
                proposal.Kind != "Quorum" || proposal.ProfileId != profileId)
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
            var path = VotePath(profileId);
            Directory.CreateDirectory(Root(profileId));
            if (File.Exists(path))
                foreach (var line in File.ReadLines(path))
                {
                    var prior = JsonSerializer.Deserialize<LocalVoteEntry>(line, Json)
                        ?? throw new InvalidDataException("Saved local vote is invalid.");
                    if (prior.Voter == voterId && prior.Epoch == proposal.Epoch &&
                        prior.Parent == proposal.ParentAuthorityHash)
                    {
                        if (prior.Vote.ProposalHash == vote.ProposalHash) return prior.Vote;
                        throw new InvalidDataException("This PC already voted for a competing proposal.");
                    }
                }
            File.AppendAllText(path, JsonSerializer.Serialize(new LocalVoteEntry(
                proposal.ParentAuthorityHash, proposal.Epoch, voterId, vote), Json) + "\n");
            return vote;
        }
    }
    internal bool Fenced(Guid profileId, string localPublicKey, out string reason)
    {
        try
        {
            var records = Read(profileId);
            var heads = records.Where(record => !records.Any(child =>
                child.Proposal.ParentAuthorityHash == record.RecordHash)).ToArray();
            if (heads.Length == 0) { reason = ""; return false; }
            if (heads.Length > 1 || heads.Any(head => head.Proposal.Schema == 2
                ? !MatchesLocalSuccessor(head)
                : head.Proposal.CandidatePublicKey != localPublicKey))
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

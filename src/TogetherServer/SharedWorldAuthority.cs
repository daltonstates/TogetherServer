using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

// Authority changes are explicit decisions about an exact, signed save head.
// They do not copy, merge, restore, or start a game server.
public sealed record WorldAuthorityProposal(int Schema, Guid GroupId, Guid ProfileId,
    long Epoch, string? ParentAuthorityHash, string RosterHash, string VersionHash,
    string CandidatePublicKey, string CandidateAddress, string Kind,
    Guid ProposerDeviceId, string ProposerPublicKey, string Signature);
public sealed record WorldAuthorityVote(int Schema, string ProposalHash, Guid VoterDeviceId,
    string VoterPublicKey, string Signature);
public sealed record WorldAuthorityRecord(int Schema, WorldAuthorityProposal Proposal,
    SharedWorldRoster Roster, SharedWorldVersion Version,
    IReadOnlyList<WorldAuthorityVote> Votes, string? OwnerSignature, string RecordHash,
    SharedWorldReceipt? SuccessorReceipt = null,
    IReadOnlyList<SharedWorldVersion>? VersionLineage = null);

internal static class WorldAuthorityTrust
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string RosterHash(SharedWorldRoster roster) => Hash(JsonSerializer.SerializeToUtf8Bytes(roster, Json));
    internal static byte[] ProposalBasis(WorldAuthorityProposal proposal) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "TogetherServer authority proposal v1", proposal.Schema, proposal.GroupId,
        proposal.ProfileId, proposal.Epoch, proposal.ParentAuthorityHash, proposal.RosterHash,
        proposal.VersionHash, proposal.CandidatePublicKey, proposal.CandidateAddress,
        proposal.Kind, proposal.ProposerDeviceId, proposal.ProposerPublicKey
    }, Json);
    internal static string ProposalHash(WorldAuthorityProposal proposal) => Hash(ProposalBasis(proposal));
    internal static byte[] VoteBasis(WorldAuthorityVote vote) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "TogetherServer authority vote v1", vote.Schema, vote.ProposalHash,
        vote.VoterDeviceId, vote.VoterPublicKey
    }, Json);
    internal static byte[] OwnerBasis(WorldAuthorityProposal proposal) => Encoding.UTF8.GetBytes(
        "TogetherServer authority owner approval v1\n" + ProposalHash(proposal));
    internal static byte[] RecordBasis(WorldAuthorityRecord record) =>
        record.SuccessorReceipt is null && record.VersionLineage is null
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
    internal static bool VerifyProposal(WorldAuthorityProposal proposal, SharedWorldRoster roster)
    {
        if (proposal.Schema != 1 || proposal.Epoch < 1 || proposal.GroupId != roster.GroupId ||
            proposal.ProfileId != roster.ProfileId || proposal.RosterHash != RosterHash(roster) ||
            !Signature(proposal.ProposerPublicKey, ProposalBasis(proposal), proposal.Signature)) return false;
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
    internal static bool Verify(WorldAuthorityRecord? record)
    {
        try
        {
            if (record is null || record.Schema != 1 || record.Proposal.Schema != 1 ||
                record.Proposal.GroupId == Guid.Empty || record.Proposal.ProfileId == Guid.Empty ||
                record.Proposal.Epoch < 1 || record.Votes.Count > 128 ||
                record.VersionLineage is { Count: > 64 } ||
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
                member.PublicKey == record.Proposal.CandidatePublicKey);
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

internal sealed class WorldAuthorityStore(LocalData data, TimeProvider? clock = null)
{
    private sealed record Floor(int Schema, int Count, string LogHash);
    private sealed record PendingAppend(int Schema, int OldCount, string OldHash,
        int OldLength, int NewCount, string NewHash, string Line);
    private sealed record LocalVoteEntry(string? Parent, long Epoch, Guid Voter, WorldAuthorityVote Vote);
    private sealed record LocalHostBinding(int Schema, Guid GroupId, string RecordHash,
        Guid DeviceId, string PublicKey);
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
    private string LogPath(Guid profileId) => Path.Combine(Root(profileId), "records.jsonl");
    private string PendingPath(Guid profileId) => Path.Combine(Root(profileId), "append.pending");
    private string VotePath(Guid profileId) => Path.Combine(Root(profileId), "local-votes.jsonl");
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
            member.PublicKey == record.Proposal.CandidatePublicKey);
        return Active(candidate.DeviceId) &&
            (record.Proposal.Kind == "Planned" || Active(record.Proposal.ProposerDeviceId)) &&
            record.Votes.All(vote => Active(vote.VoterDeviceId));
    }
    private bool MatchesLocalSuccessor(WorldAuthorityRecord record)
    {
        var bytes = data.LoadProtected(HostBindingName(record.Proposal.ProfileId));
        if (bytes is null) return false;
        var binding = JsonSerializer.Deserialize<LocalHostBinding>(bytes, Json);
        if (binding is null || binding.Schema != 1 || binding.GroupId != record.Proposal.GroupId ||
            binding.RecordHash != record.RecordHash ||
            binding.PublicKey != record.Proposal.CandidatePublicKey) return false;
        var member = record.Roster.Members.SingleOrDefault(item => item.DeviceId == binding.DeviceId);
        if (member?.PublicKey != binding.PublicKey) return false;
        var keyBytes = data.LoadProtected($"shared-world-pc-signing-{binding.DeviceId:N}.protected");
        if (keyBytes is null) return false;
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(keyBytes, out _);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) == binding.PublicKey;
    }
    internal void BindLocalSuccessor(Guid profileId, string recordHash, Guid deviceId)
    {
        lock (sync)
        {
            var record = Read(profileId).SingleOrDefault(item => item.RecordHash == recordHash)
                ?? throw new InvalidDataException("The verified authority record is missing.");
            var member = record.Roster.Members.SingleOrDefault(item => item.DeviceId == deviceId);
            if (member?.PublicKey != record.Proposal.CandidatePublicKey)
                throw new InvalidDataException("This PC is not the signed successor.");
            var binding = new LocalHostBinding(1, record.Proposal.GroupId, recordHash,
                deviceId, member.PublicKey);
            // The proof of possession is checked before writing and again at each Start.
            var keyBytes = data.LoadProtected($"shared-world-pc-signing-{deviceId:N}.protected");
            if (keyBytes is null) throw new InvalidDataException("Successor PC identity is missing.");
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(keyBytes, out _);
            if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != member.PublicKey)
                throw new InvalidDataException("Successor PC identity does not match the signed roster.");
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
        Append(record);
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
                if (!WorldAuthorityTrust.Verify(record) || record!.Proposal.ProfileId != profileId)
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
    internal WorldAuthorityRecord? ReadUniqueHead(Guid profileId)
    {
        var records = Read(profileId);
        var heads = records.Where(item => !records.Any(child =>
            child.Proposal.ParentAuthorityHash == item.RecordHash)).ToArray();
        return heads.Length == 1 ? heads[0] : null;
    }
    internal void Append(WorldAuthorityRecord record, bool stopAfterLogForChecks = false,
        bool stopAfterJournalForChecks = false)
    {
        lock (sync)
        {
            if (!WorldAuthorityTrust.Verify(record)) throw new InvalidDataException("Authority proof is invalid.");
            var existing = Read(record.Proposal.ProfileId);
            if (existing.Any(item => item.RecordHash == record.RecordHash)) return;
            if (!EligibleAtAcceptance(record))
                throw new InvalidDataException("A successor, proposer, or voter grant has expired.");
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
        lock (sync)
        {
            if (!SharedWorldRosterTrust.Verify(roster) || !WorldAuthorityTrust.VerifyProposal(proposal, roster) ||
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
            var existing = ReadLocalVotes(profileId);
            var prior = existing.SingleOrDefault(item => item.Voter == voterId &&
                item.Epoch == proposal.Epoch && item.Parent == proposal.ParentAuthorityHash);
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
                proposal.ParentAuthorityHash, proposal.Epoch, voterId, vote), Json) + "\n");
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
            var heads = records.Where(record => !records.Any(child =>
                child.Proposal.ParentAuthorityHash == record.RecordHash)).ToArray();
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

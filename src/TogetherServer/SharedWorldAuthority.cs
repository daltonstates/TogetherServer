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
    IReadOnlyList<WorldAuthorityVote> Votes, string? OwnerSignature, string RecordHash);

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
    internal static byte[] RecordBasis(WorldAuthorityRecord record) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "TogetherServer authority record v1", record.Schema, record.Proposal,
        record.Roster, record.Version, record.Votes, record.OwnerSignature
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
                !SharedWorldRosterTrust.Verify(record.Roster) ||
                !SharedWorldService.VerifySignature(record.Version) ||
                record.Proposal.GroupId != record.Roster.GroupId ||
                record.Proposal.GroupId != record.Version.GroupId ||
                record.Proposal.ProfileId != record.Roster.ProfileId ||
                record.Proposal.ProfileId != record.Version.ProfileId ||
                record.Proposal.VersionHash != record.Version.VersionHash ||
                record.Proposal.RosterHash != RosterHash(record.Roster) ||
                record.Version.SigningPublicKey != record.Roster.OwnerPublicKey ||
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
}

internal sealed class WorldAuthorityStore(LocalData data)
{
    private sealed record Floor(int Schema, int Count, string LogHash);
    private sealed record LocalVoteEntry(string? Parent, long Epoch, Guid Voter, WorldAuthorityVote Vote);
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
    private string LogPath(Guid profileId) => Path.Combine(Root(profileId), "records.jsonl");
    private string VotePath(Guid profileId) => Path.Combine(Root(profileId), "local-votes.jsonl");
    internal bool HasState(Guid profileId) => data.HasProtected(FloorName(profileId)) ||
        File.Exists(LogPath(profileId));
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
    internal IReadOnlyList<WorldAuthorityRecord> Read(Guid profileId)
    {
        lock (sync)
        {
            var path = LogPath(profileId);
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
                if (record.Proposal.Epoch != 1 && !records.Any(parent =>
                    parent.RecordHash == record.Proposal.ParentAuthorityHash &&
                    parent.Proposal.Epoch + 1 == record.Proposal.Epoch))
                    throw new InvalidDataException("Authority history has a missing parent.");
                if (record.Proposal.Epoch == 1 && record.Proposal.ParentAuthorityHash is not null)
                    throw new InvalidDataException("First authority has an invalid parent.");
                records.Add(record);
            }
            return records;
        }
    }
    internal void Append(WorldAuthorityRecord record)
    {
        lock (sync)
        {
            if (!WorldAuthorityTrust.Verify(record)) throw new InvalidDataException("Authority proof is invalid.");
            var existing = Read(record.Proposal.ProfileId);
            if (existing.Any(item => item.RecordHash == record.RecordHash)) return;
            if (record.Proposal.Epoch == 1 ? record.Proposal.ParentAuthorityHash is not null :
                !existing.Any(parent => parent.RecordHash == record.Proposal.ParentAuthorityHash &&
                    parent.Proposal.Epoch + 1 == record.Proposal.Epoch))
                throw new InvalidDataException("Authority parent is unknown.");
            if (existing.Any(item => item.Proposal.GroupId != record.Proposal.GroupId ||
                item.Roster.OwnerPublicKey != record.Roster.OwnerPublicKey))
                throw new InvalidDataException("Authority group identity changed.");
            var parentRecord = existing.SingleOrDefault(item =>
                item.RecordHash == record.Proposal.ParentAuthorityHash);
            if (parentRecord is not null && (record.Roster.Epoch < parentRecord.Roster.Epoch ||
                record.Roster.Revision < parentRecord.Roster.Revision))
                throw new InvalidDataException("Authority uses an older roster.");
            var path = LogPath(record.Proposal.ProfileId);
            Directory.CreateDirectory(Root(record.Proposal.ProfileId));
            if (!data.HasProtected(FloorName(record.Proposal.ProfileId)))
                data.SaveProtected(FloorName(record.Proposal.ProfileId),
                    JsonSerializer.SerializeToUtf8Bytes(new Floor(1, 0, Digest([])), Json));
            var line = JsonSerializer.Serialize(record, Json) + "\n";
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes);
                stream.Flush(true);
            }
            var all = File.ReadAllBytes(path);
            data.SaveProtected(FloorName(record.Proposal.ProfileId),
                JsonSerializer.SerializeToUtf8Bytes(new Floor(1, existing.Count + 1, Digest(all)), Json));
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
                expiry <= DateTimeOffset.UtcNow)
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
            if (heads.Length > 1 || heads.Any(head => head.Proposal.CandidatePublicKey != localPublicKey))
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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record WorldAuthorityOfferRequest(int Schema, Guid GroupId, Guid ProfileId,
    string ProposalHash, Guid VoterDeviceId, string VoterPublicKey,
    string Nonce, string Signature);
public sealed record WorldAuthorityChallenge(string Nonce);
public sealed record WorldAuthorityVoteResult(bool Ok, string Code,
    int Votes = 0, int Required = 0, WorldAuthorityRecord? Decision = null);
public sealed record WorldRecoveryStatus(string State, int Votes, int Required,
    long? Version, string? VersionHash, string? CandidateAddress,
    Guid? CandidateDeviceId, bool MajorityReached,
    int SeparateCopies);

// Candidate state is inert until a local user deliberately arms an offer and
// enables this PC's ordinary companion listener. Voters require only outbound
// pinned HTTPS. Neither this inbox nor its routes start a game server.
internal sealed class SharedWorldVoteInbox(LocalData data)
{
    private sealed record InboxState(int Schema, WorldAuthorityOffer Offer,
        IReadOnlyList<WorldAuthorityVote> Votes, bool Retired = false, bool Completed = false);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = Locks.GetOrAdd(data.RootPath, _ => new object());
    private readonly ConcurrentDictionary<(Guid Profile, Guid Device), (string Nonce, DateTimeOffset Expires)>
        challenges = new();
    private readonly WorldAuthorityStore authority = new(data);
    private static string Name(Guid profileId) => $"authority-offer-{profileId:N}.protected";
    private const string IndexName = "authority-offer-index.protected";
    internal static byte[] RequestBasis(WorldAuthorityOfferRequest request) => Encoding.UTF8.GetBytes(
        $"TogetherServer authority offer request v1\n{request.GroupId:N}\n{request.ProfileId:N}\n" +
        $"{request.ProposalHash}\n{request.VoterDeviceId:N}\n{request.Nonce}");

    internal WorldAuthorityOffer Arm(WorldAuthorityOffer offer, string receivedRoot)
    {
        lock (sync)
        {
            if (!SharedWorldElection.VerifyOffer(offer) ||
                FriendLink.ReadReceivedLatest(receivedRoot)?.VersionHash != offer.Version.VersionHash)
                throw new InvalidDataException("The candidate offer or local verified copy is invalid.");
            var candidate = offer.Roster.Members.SingleOrDefault(item =>
                item.DeviceId == offer.CandidateReceipt.DeviceId);
            if (candidate?.PublicKey != offer.Proposal.CandidatePublicKey ||
                !SharedWorldRosterTrust.HasRole(offer.Roster, candidate.DeviceId,
                    candidate.PublicKey, grants => grants.EligibleHost && grants.Receive))
                throw new InvalidDataException("This PC is not an eligible successor.");
            var keyBytes = data.LoadProtected($"shared-world-pc-signing-{candidate.DeviceId:N}.protected");
            if (keyBytes is null) throw new InvalidDataException("The candidate PC identity is unavailable.");
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(keyBytes, out _);
            if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != candidate.PublicKey)
                throw new InvalidDataException("The candidate PC identity does not match the signed roster.");
            if (!OfferExtendsHead(offer, Heads(authority.Read(offer.Proposal.ProfileId))))
                throw new InvalidDataException("The signed offer no longer extends the current authority head.");
            var existing = ReadState(offer.Proposal.ProfileId);
            if (existing is { Retired: false } &&
                WorldAuthorityTrust.ProposalHash(existing.Offer.Proposal) !=
                WorldAuthorityTrust.ProposalHash(offer.Proposal))
                throw new InvalidDataException("A competing offer is already armed on this PC.");
            if (existing is { Retired: true })
            {
                if (existing.Completed &&
                    (offer.Proposal.ParentAuthorityHash is not { } parentHash ||
                     offer.Proposal.Epoch != existing.Offer.Proposal.Epoch + 1 ||
                     !authority.Read(offer.Proposal.ProfileId).Any(record =>
                         record.RecordHash == parentHash &&
                         WorldAuthorityTrust.ProposalHash(record.Proposal) ==
                         WorldAuthorityTrust.ProposalHash(existing.Offer.Proposal))))
                    throw new InvalidDataException("A completed offer requires a signed child authority epoch.");
                if (!existing.Completed &&
                    (offer.Proposal.ParentAuthorityHash != existing.Offer.Proposal.ParentAuthorityHash ||
                     offer.Proposal.Epoch != existing.Offer.Proposal.Epoch))
                    throw new InvalidDataException("A canceled offer cannot change its authority epoch.");
                if (!existing.Completed && existing.Votes.Count > 0)
                    throw new InvalidDataException(
                        "A canceled offer with recorded votes requires conflict review before another proposal.");
            }
            if (existing is { Retired: false })
            {
                if (existing.Offer.CandidateTlsFingerprint != offer.CandidateTlsFingerprint ||
                    existing.Offer.Roster.Signature != offer.Roster.Signature ||
                    existing.Offer.Version.VersionHash != offer.Version.VersionHash)
                    throw new InvalidDataException("The armed offer's identity or save changed.");
                EnsureIndexed(offer.Proposal.ProfileId);
                return existing.Offer;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new InboxState(1, offer, []), Json);
            if (bytes.Length > 512 * 1024)
                throw new InvalidDataException("Candidate offer is oversized.");
            EnsureIndexed(offer.Proposal.ProfileId);
            data.SaveProtected(Name(offer.Proposal.ProfileId), bytes);
            return offer;
        }
    }

    internal bool HasArmedOffer(string endpoint)
    {
        lock (sync) return ReadIndex().Any(id =>
            CurrentArmed(id) is { } state &&
            state.Offer.Proposal.CandidateAddress == endpoint);
    }

    internal void Retire(Guid profileId)
    {
        lock (sync)
        {
            var state = ReadState(profileId);
            if (state is { Retired: false })
                data.SaveProtected(Name(profileId), JsonSerializer.SerializeToUtf8Bytes(
                    state with { Retired = true }, Json));
            foreach (var challenge in challenges.Keys.Where(key => key.Profile == profileId))
                challenges.TryRemove(challenge, out _);
        }
    }

    internal WorldAuthorityChallenge? Challenge(Guid profileId, string proposalHash, Guid deviceId)
    {
        lock (sync)
        {
            var state = CurrentArmed(profileId);
            if (state is null || WorldAuthorityTrust.ProposalHash(state.Offer.Proposal) != proposalHash ||
                state.Offer.Roster.Members.SingleOrDefault(item => item.DeviceId == deviceId) is
                    not { Revoked: false, Grants.RecoveryVoter: true } member ||
                !SharedWorldRosterTrust.HasRole(state.Offer.Roster, deviceId, member.PublicKey,
                    grants => grants.RecoveryVoter)) return null;
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            challenges[(profileId, deviceId)] = (nonce, DateTimeOffset.UtcNow.AddMinutes(5));
            return new(nonce);
        }
    }

    internal WorldAuthorityOffer? ReadOffer(WorldAuthorityOfferRequest request)
    {
        lock (sync)
        {
            var state = CurrentArmed(request.ProfileId);
            if (state is null || request.Schema != 1 || request.GroupId != state.Offer.Roster.GroupId ||
                request.ProposalHash != WorldAuthorityTrust.ProposalHash(state.Offer.Proposal) ||
                !challenges.TryRemove((request.ProfileId, request.VoterDeviceId), out var challenge) ||
                challenge.Expires < DateTimeOffset.UtcNow || challenge.Nonce != request.Nonce ||
                !SharedWorldRosterTrust.HasRole(state.Offer.Roster, request.VoterDeviceId,
                    request.VoterPublicKey, grants => grants.RecoveryVoter)) return null;
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.VoterPublicKey), out _);
                return key.VerifyData(RequestBasis(request), Convert.FromBase64String(request.Signature),
                    HashAlgorithmName.SHA256) ? state.Offer : null;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
            { return null; }
        }
    }

    internal WorldAuthorityVoteResult AcceptVote(Guid profileId, string proposalHash,
        WorldAuthorityVote vote, Func<bool>? stillUnreachable = null)
    {
        lock (sync)
        {
            var state = CurrentArmed(profileId);
            if (state is null || stillUnreachable?.Invoke() == false ||
                WorldAuthorityTrust.ProposalHash(state.Offer.Proposal) != proposalHash ||
                !WorldAuthorityTrust.VerifyVote(vote, state.Offer.Proposal, state.Offer.Roster) ||
                !SharedWorldRosterTrust.HasRole(state.Offer.Roster, vote.VoterDeviceId,
                    vote.VoterPublicKey, grants => grants.RecoveryVoter))
                return new(false, "VoteRejected");
            var prior = state.Votes.SingleOrDefault(item => item.VoterDeviceId == vote.VoterDeviceId);
            if (prior is not null && prior != vote) return new(false, "CompetingVoteRejected");
            var votes = prior is null ? state.Votes.Append(vote).ToArray() : state.Votes;
            if (prior is null)
                data.SaveProtected(Name(profileId), JsonSerializer.SerializeToUtf8Bytes(
                    state with { Votes = votes }, Json));
            var total = state.Offer.Roster.Members.Count(item => !item.Revoked &&
                item.Grants.RecoveryVoter);
            var required = total / 2 + 1;
            if (votes.Count < required) return new(true, "VoteRecorded", votes.Count, required);
            var record = SharedWorldElection.ConfirmQuorum(state.Offer, votes, authority);
            authority.BindLocalSuccessor(profileId, record.RecordHash,
                state.Offer.CandidateReceipt.DeviceId);
            data.SaveProtected(Name(profileId), JsonSerializer.SerializeToUtf8Bytes(
                state with { Votes = votes, Retired = true, Completed = true }, Json));
            return new(true, "MajorityRecorded", votes.Count, required, record);
        }
    }

    internal WorldAuthorityOffer? Armed(Guid profileId)
    {
        lock (sync) return CurrentArmed(profileId)?.Offer;
    }

    internal WorldRecoveryStatus Status(Guid profileId, Guid? localDeviceId = null)
    {
        lock (sync)
        {
            var state = ReadState(profileId);
            var records = authority.Read(profileId);
            var heads = Heads(records);
            var separate = new SharedWorldSeparateCopyStore(data).Read(profileId).Count;
            if (heads.Length > 1)
                return new("HistoryReviewRequired", 0, 0, null, null, null, null, false, separate);
            var head = heads.SingleOrDefault();
            if (state is { Retired: false } && OfferExtendsHead(state.Offer, heads))
            {
                var required = Required(state.Offer.Roster);
                return new("OfferArmed", state.Votes.Count, required, state.Offer.Version.Number,
                    state.Offer.Version.VersionHash, state.Offer.Proposal.CandidateAddress,
                    state.Offer.Proposal.ProposerDeviceId, false, separate);
            }
            if (head?.Proposal.Kind == "Quorum")
            {
                var isCandidate = localDeviceId is { } id && id != Guid.Empty &&
                    id == head.Proposal.ProposerDeviceId && BindingMatches(profileId, head, id);
                return new(isCandidate ? "MajorityRecorded" : "ObservedMajority",
                    head.Votes.Count, Required(head.Roster), head.Version.Number,
                    head.Version.VersionHash, head.Proposal.CandidateAddress,
                    head.Proposal.ProposerDeviceId, true, separate);
            }
            var historical = state?.Offer;
            if (historical is not null)
                return new(head is null && state!.Retired ? "OfferClosed" : "HistoricalRecovery",
                    0, 0, historical.Version.Number, historical.Version.VersionHash,
                    historical.Proposal.CandidateAddress, historical.Proposal.ProposerDeviceId,
                    false, separate);
            return new(records.Any(record => record.Proposal.Kind == "Quorum") ?
                "HistoricalRecovery" : "NoOffer", 0, 0, null, null, null, null, false, separate);
        }
    }

    private static WorldAuthorityRecord[] Heads(IReadOnlyList<WorldAuthorityRecord> records) =>
        records.Where(record => !records.Any(child =>
            child.Proposal.ParentAuthorityHash == record.RecordHash)).ToArray();

    private static bool OfferExtendsHead(WorldAuthorityOffer offer,
        IReadOnlyList<WorldAuthorityRecord> heads)
    {
        if (heads.Count > 1) return false;
        var head = heads.SingleOrDefault();
        return offer.Proposal.ParentAuthorityHash == head?.RecordHash &&
            offer.Proposal.Epoch == (head?.Proposal.Epoch ?? 0) + 1 &&
            (head is null || head.Proposal.GroupId == offer.Proposal.GroupId);
    }

    private InboxState? CurrentArmed(Guid profileId)
    {
        var state = ReadState(profileId);
        return state is { Retired: false } &&
            OfferExtendsHead(state.Offer, Heads(authority.Read(profileId))) ? state : null;
    }

    private static int Required(SharedWorldRoster roster) =>
        roster.Members.Count(item => !item.Revoked && item.Grants.RecoveryVoter) / 2 + 1;

    private bool BindingMatches(Guid profileId, WorldAuthorityRecord head, Guid localDeviceId)
    {
        var name = $"authority-host-{profileId:N}.protected";
        if (!data.HasProtected(name)) return false;
        try
        {
            var bytes = data.LoadProtected(name);
            if (bytes is null || bytes.Length > 4096) return false;
            var binding = JsonSerializer.Deserialize<LocalCandidateBinding>(bytes, Json);
            return binding is { Schema: 1 } && binding.GroupId == head.Proposal.GroupId &&
                binding.RecordHash == head.RecordHash && binding.DeviceId == localDeviceId &&
                SharedWorldRosterTrust.ValidKey(binding.PublicKey) &&
                (binding.PublicKey == head.Proposal.CandidatePublicKey ||
                 head.Roster.Members.Any(member => member.DeviceId == localDeviceId &&
                     member.PublicKey == binding.PublicKey));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException)
        { return false; }
    }

    private sealed record LocalCandidateBinding(int Schema, Guid GroupId, string RecordHash,
        Guid DeviceId, string PublicKey);

    private InboxState? ReadState(Guid profileId)
    {
        var bytes = data.LoadProtected(Name(profileId));
        if (bytes is null)
        {
            if (data.HasProtected(Name(profileId)))
                throw new InvalidDataException("Candidate offer could not be read.");
            return null;
        }
        if (bytes.Length > 512 * 1024) throw new InvalidDataException("Candidate offer is oversized.");
        var state = JsonSerializer.Deserialize<InboxState>(bytes, Json);
        if (state is null || state.Schema != 1 || !SharedWorldElection.VerifyOffer(state.Offer) ||
            state.Offer.Proposal.ProfileId != profileId || state.Votes.Count > 128 ||
            state.Votes.Select(vote => vote.VoterDeviceId).Distinct().Count() != state.Votes.Count ||
            state.Votes.Any(vote => !WorldAuthorityTrust.VerifyVote(vote,
                state.Offer.Proposal, state.Offer.Roster)))
            throw new InvalidDataException("Candidate offer or votes failed verification.");
        if (authority.Read(profileId).Any(record =>
                WorldAuthorityTrust.ProposalHash(record.Proposal) ==
                WorldAuthorityTrust.ProposalHash(state.Offer.Proposal)))
            return state with { Retired = true, Completed = true };
        return state;
    }

    private List<Guid> ReadIndex()
    {
        var bytes = data.LoadProtected(IndexName);
        if (bytes is null)
        {
            if (data.HasProtected(IndexName))
                throw new InvalidDataException("Candidate offer index is unavailable.");
            return [];
        }
        var index = JsonSerializer.Deserialize<List<Guid>>(bytes, Json);
        if (index is null || index.Count > 20 || index.Any(id => id == Guid.Empty) ||
            index.Distinct().Count() != index.Count)
            throw new InvalidDataException("Candidate offer index is invalid.");
        return index;
    }

    private void EnsureIndexed(Guid profileId)
    {
        var index = ReadIndex();
        if (index.Contains(profileId)) return;
        if (index.Count >= 20) throw new InvalidDataException("Too many candidate offers are armed.");
        index.Add(profileId);
        data.SaveProtected(IndexName, JsonSerializer.SerializeToUtf8Bytes(index, Json));
    }
}

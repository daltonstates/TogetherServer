using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record WorldSeparateCopyBranch(int Schema, Guid BranchId,
    WorldAuthorityOffer Offer, bool WarningAccepted, string CandidatePublicKey,
    string Signature, string BranchHash);
public sealed record WorldSeparateCopyResult(bool Ok, string Code, string Message,
    WorldSeparateCopyBranch? Branch = null);
public sealed record WorldSeparateCopyConfirmation(bool AcceptSplitWarning);

// This ledger records an explicit, warned fork. It never enters the authority
// log, changes the authoritative head, or permits Start of the shared profile.
internal sealed class SharedWorldSeparateCopyStore(LocalData data)
{
    private sealed record BranchSet(int Schema, Guid GroupId, Guid ProfileId,
        IReadOnlyList<WorldSeparateCopyBranch> Branches,
        IReadOnlyList<string>? ReturnedBranches = null);
    private sealed record BranchFloor(int Schema, int Count, string Sha256);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private static string Name(Guid profileId) => $"shared-world-separate-{profileId:N}.protected";
    private static string FloorName(Guid profileId) => $"shared-world-separate-floor-{profileId:N}.protected";
    private static byte[] Basis(WorldSeparateCopyBranch branch) => Encoding.UTF8.GetBytes(
        $"TogetherServer warned separate copy v1\n{branch.BranchId:N}\n" +
        $"{WorldAuthorityTrust.ProposalHash(branch.Offer.Proposal)}\n" +
        $"{branch.Offer.Version.VersionHash}\n{branch.CandidatePublicKey}\n" +
        (branch.WarningAccepted ? "warning accepted" : "warning not accepted"));
    private static string Hash(WorldSeparateCopyBranch branch) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            branch.Schema,
            branch.BranchId,
            branch.Offer,
            branch.WarningAccepted,
            branch.CandidatePublicKey,
            branch.Signature
        }, Json)));

    internal static bool Verify(WorldSeparateCopyBranch? branch)
    {
        try
        {
            if (branch is null || branch.Schema != 1 || branch.BranchId == Guid.Empty ||
                !branch.WarningAccepted || !SharedWorldElection.VerifyOffer(branch.Offer) ||
                branch.CandidatePublicKey != WorldAuthorityTrust.CandidateDevicePublicKey(branch.Offer.Proposal) ||
                branch.BranchHash != Hash(branch)) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(branch.CandidatePublicKey), out _);
            return key.VerifyData(Basis(branch), Convert.FromBase64String(branch.Signature),
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or
                                   ArgumentException or NullReferenceException or JsonException)
        { return false; }
    }

    internal WorldSeparateCopyBranch Declare(SharedWorldHostLoss loss, WorldAuthorityOffer offer,
        string receivedRoot, ECDsa candidateKey, bool acceptSplitWarning)
    {
        lock (sync)
        {
            if (!acceptSplitWarning || !loss.MayPropose || !SharedWorldElection.VerifyOffer(offer) ||
                FriendLink.ReadReceivedLatest(receivedRoot)?.VersionHash != offer.Version.VersionHash ||
                Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo()) !=
                    WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal))
                throw new InvalidDataException("The separate copy warning or hash-verified post-Stop file copy is missing.");
            var authority = new WorldAuthorityStore(data).Read(offer.Proposal.ProfileId);
            if (authority.Any(record => record.Proposal.Epoch >= offer.Proposal.Epoch))
                throw new InvalidDataException("Review the recorded authority before making a separate copy.");
            if (ReadSet(offer.Proposal.ProfileId)?.ReturnedBranches is { Count: > 0 })
                throw new InvalidDataException("The old Host returned. Review the retained histories before another separate copy.");
            var branches = Read(offer.Proposal.ProfileId);
            var prior = branches.FirstOrDefault(branch =>
                WorldAuthorityTrust.ProposalHash(branch.Offer.Proposal) ==
                    WorldAuthorityTrust.ProposalHash(offer.Proposal));
            if (prior is not null) return prior;
            if (branches.Count >= 20) throw new InvalidDataException("Too many separate histories need review.");
            var branch = Sign(offer, candidateKey);
            var set = new BranchSet(1, offer.Roster.GroupId, offer.Roster.ProfileId,
                branches.Append(branch).ToArray(), ReadSet(offer.Proposal.ProfileId)?.ReturnedBranches);
            SaveSet(offer.Proposal.ProfileId, set);
            return branch;
        }
    }

    internal static WorldSeparateCopyBranch Sign(WorldAuthorityOffer offer, ECDsa candidateKey)
    {
        if (!SharedWorldElection.VerifyOffer(offer) ||
            Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo()) !=
                WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal))
            throw new InvalidDataException("The candidate identity or signed offer is invalid.");
        var draft = new WorldSeparateCopyBranch(1, Guid.NewGuid(), offer, true,
            WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal), "", "");
        var signed = draft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            Basis(draft), HashAlgorithmName.SHA256))
        };
        return signed with { BranchHash = Hash(signed) };
    }

    internal IReadOnlyList<WorldSeparateCopyBranch> Read(Guid profileId)
    {
        lock (sync) return ReadSet(profileId)?.Branches ?? [];
    }

    // A response from the old Host closes the split escape hatch permanently.
    // The signed fork and its files remain available for later group review.
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

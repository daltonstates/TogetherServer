using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record SeparateCopyRouteChallenge(int Schema, Guid ProfileId,
    string BranchHash, string Nonce, DateTimeOffset IssuedUtc, Guid ObserverDeviceId,
    string ObserverPublicKey, string Signature);
public sealed record SeparateCopyRouteProof(int Schema, Guid ProfileId,
    string BranchHash, string Nonce, string Endpoint, string TlsFingerprint,
    string Signature);
public sealed record SeparateCopyRouteReceipt(int Schema, Guid GroupId,
    Guid ProfileId, string BranchHash, string Endpoint, string TlsFingerprint,
    string ChallengeHash, string ProofHash, Guid ObserverDeviceId,
    string ObserverPublicKey, string Signature);
public sealed record SeparateCopyRouteConfirmation(SeparateCopyRouteChallenge Challenge,
    SeparateCopyRouteProof Proof, SeparateCopyRouteReceipt Receipt);
public sealed record SeparateCopyRouteRequest(WorldSeparateCopyBranch Branch);

internal sealed record SeparateCopyRouteNonce(string Nonce, DateTimeOffset ConfirmedUtc);
internal sealed record SeparateCopyRouteObservation(int Schema, string BranchHash,
    SeparateCopyRouteConfirmation Confirmation, string TlsFingerprint,
    DateTimeOffset ObservedUtc,
    IReadOnlyList<SeparateCopyRouteNonce> UsedNonces);

// These proofs attest only that a second approved PC reached the candidate's
// pinned direct-IP listener. They never create a majority or authority record.
internal static class SharedWorldSeparateRoute
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static byte[] ChallengeBasis(SeparateCopyRouteChallenge item) => Encoding.UTF8.GetBytes(
        $"TogetherServer separate-copy route observer v1\n{item.ProfileId:N}\n" +
        $"{item.BranchHash}\n{item.Nonce}\n{item.IssuedUtc:O}\n{item.ObserverDeviceId:N}\n" +
        item.ObserverPublicKey);

    internal static byte[] ProofBasis(SeparateCopyRouteProof item) => Encoding.UTF8.GetBytes(
        $"TogetherServer separate-copy direct route v1\n{item.ProfileId:N}\n" +
        $"{item.BranchHash}\n{item.Nonce}\n{item.Endpoint}\n{item.TlsFingerprint}");

    internal static SeparateCopyRouteChallenge SignChallenge(WorldSeparateCopyBranch branch,
        Guid observerId, ECDsa observerKey)
    {
        if (!SharedWorldSeparateCopyStore.Verify(branch) || observerId == Guid.Empty)
            throw new InvalidDataException("The separate-copy proof is invalid.");
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var draft = new SeparateCopyRouteChallenge(1, branch.Offer.Proposal.ProfileId,
            branch.BranchHash, nonce, DateTimeOffset.UtcNow, observerId,
            Convert.ToBase64String(observerKey.ExportSubjectPublicKeyInfo()), "");
        return draft with
        {
            Signature = Convert.ToBase64String(observerKey.SignData(
            ChallengeBasis(draft), HashAlgorithmName.SHA256))
        };
    }

    internal static bool VerifyChallenge(SeparateCopyRouteChallenge? challenge,
        WorldSeparateCopyBranch branch, DateTimeOffset now)
    {
        if (challenge is null || challenge.Schema != 1 || !ValidNonce(challenge.Nonce) ||
            challenge.IssuedUtc > now.AddMinutes(1) ||
            now - challenge.IssuedUtc > TimeSpan.FromMinutes(10) ||
            !SharedWorldSeparateCopyStore.Verify(branch) ||
            challenge.ProfileId != branch.Offer.Proposal.ProfileId ||
            challenge.BranchHash != branch.BranchHash ||
            challenge.ObserverDeviceId == branch.Offer.Proposal.ProposerDeviceId)
            return false;
        var member = branch.Offer.Roster.Members.SingleOrDefault(item =>
            item.DeviceId == challenge.ObserverDeviceId);
        if (member is not { Revoked: false } ||
            member.PublicKey != challenge.ObserverPublicKey ||
            !(member.Grants.Receive || member.Grants.RecoveryVoter) ||
            member.AccessExpiresUtc is { } end && end <= now) return false;
        return Signature(member.PublicKey, ChallengeBasis(challenge), challenge.Signature);
    }

    internal static SeparateCopyRouteProof SignProof(WorldSeparateCopyBranch branch,
        SeparateCopyRouteChallenge challenge, ECDsa candidateKey)
    {
        if (!VerifyChallenge(challenge, branch, DateTimeOffset.UtcNow) ||
            Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo()) !=
                branch.CandidatePublicKey)
            throw new InvalidDataException("The separate-copy route challenge is invalid.");
        var draft = new SeparateCopyRouteProof(1, branch.Offer.Proposal.ProfileId,
            branch.BranchHash, challenge.Nonce, branch.Offer.Proposal.CandidateAddress,
            branch.Offer.CandidateTlsFingerprint, "");
        return draft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            ProofBasis(draft), HashAlgorithmName.SHA256))
        };
    }

    internal static bool VerifyProof(SeparateCopyRouteProof? proof,
        SeparateCopyRouteChallenge challenge, WorldSeparateCopyBranch branch)
    {
        return proof is { Schema: 1 } && SharedWorldSeparateCopyStore.Verify(branch) &&
            proof.ProfileId == branch.Offer.Proposal.ProfileId &&
            proof.BranchHash == branch.BranchHash && proof.Nonce == challenge.Nonce &&
            proof.Endpoint == branch.Offer.Proposal.CandidateAddress &&
            proof.TlsFingerprint == branch.Offer.CandidateTlsFingerprint &&
            SharedWorldRouteTrust.DirectIpAddress(proof.Endpoint) &&
            Signature(branch.CandidatePublicKey, ProofBasis(proof), proof.Signature);
    }

    internal static string ChallengeHash(SeparateCopyRouteChallenge challenge) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(challenge, Json)));

    internal static string ProofHash(SeparateCopyRouteProof proof) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(proof, Json)));

    internal static byte[] ReceiptBasis(SeparateCopyRouteReceipt receipt) => Encoding.UTF8.GetBytes(
        $"TogetherServer separate-copy observed route v1\n{receipt.GroupId:N}\n" +
        $"{receipt.ProfileId:N}\n{receipt.BranchHash}\n{receipt.Endpoint}\n" +
        $"{receipt.TlsFingerprint}\n{receipt.ChallengeHash}\n{receipt.ProofHash}\n" +
        $"{receipt.ObserverDeviceId:N}\n{receipt.ObserverPublicKey}");

    // The observer signs only after its pinned HTTPS request has returned and
    // the candidate's exact proof has verified. The candidate cannot mint it.
    internal static SeparateCopyRouteConfirmation SignConfirmation(
        WorldSeparateCopyBranch branch, SeparateCopyRouteChallenge challenge,
        SeparateCopyRouteProof proof, ECDsa observerKey)
    {
        if (!VerifyChallenge(challenge, branch, DateTimeOffset.UtcNow) ||
            !VerifyProof(proof, challenge, branch) ||
            Convert.ToBase64String(observerKey.ExportSubjectPublicKeyInfo()) !=
                challenge.ObserverPublicKey)
            throw new InvalidDataException("A different approved Friend PC must verify the pinned route.");
        var draft = new SeparateCopyRouteReceipt(1, branch.Offer.Proposal.GroupId,
            branch.Offer.Proposal.ProfileId, branch.BranchHash,
            branch.Offer.Proposal.CandidateAddress, branch.Offer.CandidateTlsFingerprint,
            ChallengeHash(challenge), ProofHash(proof), challenge.ObserverDeviceId,
            challenge.ObserverPublicKey, "");
        var signed = draft with
        {
            Signature = Convert.ToBase64String(observerKey.SignData(
            ReceiptBasis(draft), HashAlgorithmName.SHA256))
        };
        return new(challenge, proof, signed);
    }

    internal static bool VerifyConfirmation(SeparateCopyRouteConfirmation? confirmation,
        WorldSeparateCopyBranch branch, DateTimeOffset now)
    {
        if (confirmation?.Challenge is not { } challenge ||
            confirmation.Proof is not { } proof || confirmation.Receipt is not { } receipt ||
            !VerifyChallenge(challenge, branch, now) ||
            !VerifyProof(proof, challenge, branch) || receipt.Schema != 1 ||
            receipt.GroupId != branch.Offer.Proposal.GroupId ||
            receipt.ProfileId != branch.Offer.Proposal.ProfileId ||
            receipt.BranchHash != branch.BranchHash ||
            receipt.Endpoint != branch.Offer.Proposal.CandidateAddress ||
            receipt.TlsFingerprint != branch.Offer.CandidateTlsFingerprint ||
            receipt.ChallengeHash != ChallengeHash(challenge) ||
            receipt.ProofHash != ProofHash(proof) ||
            receipt.ObserverDeviceId != challenge.ObserverDeviceId ||
            receipt.ObserverPublicKey != challenge.ObserverPublicKey)
            return false;
        return Signature(challenge.ObserverPublicKey, ReceiptBasis(receipt), receipt.Signature);
    }

    private static bool Signature(string publicKey, byte[] basis, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(basis, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    private static bool ValidNonce(string value)
    {
        if (value.Length != 43 || value.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
            return false;
        try { return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=").Length == 32; }
        catch (FormatException) { return false; }
    }
}

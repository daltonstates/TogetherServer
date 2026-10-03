using System.Security.Cryptography;
using System.Text;

namespace TogetherServer;

public sealed record SeparateCopyRouteChallenge(int Schema, Guid ProfileId,
    string BranchHash, string Nonce, Guid ObserverDeviceId,
    string ObserverPublicKey, string Signature);
public sealed record SeparateCopyRouteProof(int Schema, Guid ProfileId,
    string BranchHash, string Nonce, string Endpoint, string TlsFingerprint,
    string Signature);
public sealed record SeparateCopyRouteConfirmation(SeparateCopyRouteChallenge Challenge,
    SeparateCopyRouteProof Proof);
public sealed record SeparateCopyRouteRequest(WorldSeparateCopyBranch Branch);

internal sealed record SeparateCopyRouteObservation(int Schema, string BranchHash,
    SeparateCopyRouteChallenge Challenge, string TlsFingerprint, DateTimeOffset ObservedUtc);

// These proofs attest only that a second approved PC reached the candidate's
// pinned direct-IP listener. They never create a majority or authority record.
internal static class SharedWorldSeparateRoute
{
    internal static byte[] ChallengeBasis(SeparateCopyRouteChallenge item) => Encoding.UTF8.GetBytes(
        $"TogetherServer separate-copy route observer v1\n{item.ProfileId:N}\n" +
        $"{item.BranchHash}\n{item.Nonce}\n{item.ObserverDeviceId:N}\n" +
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
            branch.BranchHash, nonce, observerId,
            Convert.ToBase64String(observerKey.ExportSubjectPublicKeyInfo()), "");
        return draft with { Signature = Convert.ToBase64String(observerKey.SignData(
            ChallengeBasis(draft), HashAlgorithmName.SHA256)) };
    }

    internal static bool VerifyChallenge(SeparateCopyRouteChallenge? challenge,
        WorldSeparateCopyBranch branch, DateTimeOffset now)
    {
        if (challenge is null || challenge.Schema != 1 || !ValidNonce(challenge.Nonce) ||
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
        return draft with { Signature = Convert.ToBase64String(candidateKey.SignData(
            ProofBasis(draft), HashAlgorithmName.SHA256)) };
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

using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TogetherServer;

public sealed record SharedWorldRouteProof(int Schema, Guid ProfileId, string RecordHash,
    string Nonce, string Endpoint, string TlsFingerprint, string Signature);
public sealed record SharedWorldRouteChallenge(int Schema, Guid ProfileId, string RecordHash,
    string Nonce, Guid ObserverDeviceId, string ObserverPublicKey, string Signature);
public sealed record SharedWorldRouteRequest(string RecordHash, string TlsFingerprint);
public sealed record SharedWorldRouteConfirmation(SharedWorldRouteChallenge Challenge,
    SharedWorldRouteProof Proof);
public sealed record SharedWorldRouteCheck(bool ControlRouteObserved, string Code, string Message,
    DateTimeOffset CheckedUtc, string? RecordHash = null);

internal static class SharedWorldRouteTrust
{
    internal static byte[] ChallengeBasis(SharedWorldRouteChallenge challenge) => Encoding.UTF8.GetBytes(
        $"TogetherServer successor route observer v1\n{challenge.ProfileId:N}\n" +
        $"{challenge.RecordHash}\n{challenge.Nonce}\n{challenge.ObserverDeviceId:N}\n" +
        challenge.ObserverPublicKey);

    internal static SharedWorldRouteChallenge SignChallenge(WorldAuthorityRecord record,
        string nonce, Guid observerId, ECDsa observerKey)
    {
        if (!ValidNonce(nonce) || observerId == Guid.Empty) throw new InvalidDataException("Invalid route challenge.");
        var draft = new SharedWorldRouteChallenge(1, record.Proposal.ProfileId,
            record.RecordHash, nonce, observerId,
            Convert.ToBase64String(observerKey.ExportSubjectPublicKeyInfo()), "");
        return draft with
        {
            Signature = Convert.ToBase64String(observerKey.SignData(
            ChallengeBasis(draft), HashAlgorithmName.SHA256))
        };
    }

    internal static bool VerifyChallenge(SharedWorldRouteChallenge? challenge,
        WorldAuthorityRecord record, DateTimeOffset now)
    {
        if (challenge is null || challenge.Schema != 1 || !ValidNonce(challenge.Nonce) ||
            challenge.ProfileId != record.Proposal.ProfileId ||
            challenge.RecordHash != record.RecordHash || !WorldAuthorityTrust.Verify(record)) return false;
        var observer = record.Roster.Members.SingleOrDefault(item =>
            item.DeviceId == challenge.ObserverDeviceId);
        if (observer is not { Revoked: false } ||
            observer.PublicKey != challenge.ObserverPublicKey ||
            !(observer.Grants.Receive || observer.Grants.RecoveryVoter) ||
            observer.AccessExpiresUtc is { } end && end <= now ||
            observer.PublicKey == WorldAuthorityTrust.CandidateDevicePublicKey(record.Proposal)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(observer.PublicKey), out _);
            return key.VerifyData(ChallengeBasis(challenge),
                Convert.FromBase64String(challenge.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    internal static byte[] Basis(SharedWorldRouteProof proof) => Encoding.UTF8.GetBytes(
        $"TogetherServer successor direct route v1\n{proof.ProfileId:N}\n{proof.RecordHash}\n" +
        $"{proof.Nonce}\n{proof.Endpoint}\n{proof.TlsFingerprint}");

    internal static bool DirectIpAddress(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port is < 1 or > 65535 ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !IPAddress.TryParse(uri.Host, out var address) || IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        return true;
    }

    internal static SharedWorldRouteProof Sign(Guid profileId, string recordHash,
        string nonce, string endpoint, string fingerprint, ECDsa candidateKey)
    {
        if (profileId == Guid.Empty || recordHash.Length != 64 || !recordHash.All(Uri.IsHexDigit) ||
            !ValidNonce(nonce)) throw new InvalidDataException("Invalid route challenge.");
        var draft = new SharedWorldRouteProof(1, profileId, recordHash, nonce,
            endpoint, fingerprint, "");
        return draft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            Basis(draft), HashAlgorithmName.SHA256))
        };
    }

    internal static bool Verify(SharedWorldRouteProof? proof, WorldAuthorityRecord record,
        string nonce, string fingerprint)
    {
        if (proof is null || proof.Schema != 1 || !ValidNonce(nonce) ||
            proof.Nonce != nonce || proof.ProfileId != record.Proposal.ProfileId ||
            proof.RecordHash != record.RecordHash || proof.Endpoint != record.Proposal.CandidateAddress ||
            proof.TlsFingerprint != fingerprint || !DirectIpAddress(proof.Endpoint) ||
            !WorldAuthorityTrust.Verify(record)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(
                record.Proposal.CandidatePublicKey), out _);
            return key.VerifyData(Basis(proof), Convert.FromBase64String(proof.Signature),
                HashAlgorithmName.SHA256);
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

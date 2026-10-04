using System.Security.Cryptography;
using System.Text;

namespace TogetherServer;

public sealed record SuccessorEnrollmentChallenge(string Nonce, string RecordHash,
    Guid GroupId, string OwnerPublicKey, string SuccessorAddress, string TlsFingerprint);
public sealed record SuccessorEnrollmentRequest(Guid DeviceId, string Nonce, string RecordHash,
    Guid GroupId, string OwnerPublicKey, string SuccessorAddress, string TlsFingerprint,
    string DevicePublicKey, string Signature);

internal static class SharedWorldSuccessorEnrollment
{
    internal static byte[] Basis(SuccessorEnrollmentRequest request) => Encoding.UTF8.GetBytes(
        $"TogetherServer successor enrollment v1\n{request.DeviceId:N}\n{request.Nonce}\n" +
        $"{request.RecordHash}\n{request.GroupId:N}\n{request.OwnerPublicKey}\n" +
        $"{request.SuccessorAddress}\n{request.TlsFingerprint}\n{request.DevicePublicKey}");

    internal static bool Verify(SuccessorEnrollmentRequest? request,
        WorldAuthorityRecord record, string fingerprint)
    {
        if (request is null || request.DeviceId == Guid.Empty || request.Nonce.Length != 44 ||
            request.RecordHash != record.RecordHash || request.GroupId != record.Proposal.GroupId ||
            request.OwnerPublicKey != record.Roster.OwnerPublicKey ||
            request.SuccessorAddress != record.Proposal.CandidateAddress ||
            request.TlsFingerprint != fingerprint || !WorldAuthorityTrust.Verify(record) ||
            record.Roster.Members.SingleOrDefault(item => item.DeviceId == request.DeviceId) is not
            { Revoked: false, Grants.Receive: true } member ||
            member.PublicKey != request.DevicePublicKey ||
            member.AccessExpiresUtc is { } expires && expires <= DateTimeOffset.UtcNow)
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(member.PublicKey), out _);
            return key.VerifyData(Basis(request), Convert.FromBase64String(request.Signature),
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }
}

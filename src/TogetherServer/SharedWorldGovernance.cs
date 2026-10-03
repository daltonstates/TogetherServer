using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

public sealed record SharedWorldGrants(bool Receive = false, bool EligibleHost = false,
    bool RecoveryVoter = false, bool ManageSharing = false);
public sealed record SharedWorldRosterMember(Guid DeviceId, string PublicKey,
    SharedWorldGrants Grants, bool Revoked, DateTimeOffset? AccessExpiresUtc = null);
public sealed record SharedWorldRoster(int Schema, Guid GroupId, Guid ProfileId,
    long Epoch, long Revision, bool OwnerOverride, string OwnerPublicKey,
    IReadOnlyList<SharedWorldRosterMember> Members, string Signature);
public sealed record SharedWorldEnrollmentChallenge(string Nonce);
public sealed record SharedWorldEnrollmentRequest(
    [property: JsonRequired] string Nonce, [property: JsonRequired] string PublicKey,
    [property: JsonRequired] string Signature);
public sealed record SharedWorldGovernanceRequest(bool? OwnerOverride = null,
    bool ReviewSourceChange = false);
public sealed record SharedWorldDeviceGrantsRequest([property: JsonRequired] SharedWorldGrants Grants);

internal static class SharedWorldRosterTrust
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static byte[] Basis(SharedWorldRoster roster) => roster.Schema == 1
        ? JsonSerializer.SerializeToUtf8Bytes(new
        {
            roster.Schema,
            roster.GroupId,
            roster.ProfileId,
            roster.Epoch,
            roster.Revision,
            roster.OwnerOverride,
            roster.OwnerPublicKey,
            Members = roster.Members.Select(member => new
            { member.DeviceId, member.PublicKey, member.Grants, member.Revoked }).ToArray()
        }, Json)
        : JsonSerializer.SerializeToUtf8Bytes(new
        {
            roster.Schema,
            roster.GroupId,
            roster.ProfileId,
            roster.Epoch,
            roster.Revision,
            roster.OwnerOverride,
            roster.OwnerPublicKey,
            roster.Members
        }, Json);

    internal static bool Verify(SharedWorldRoster? roster)
    {
        try
        {
            if (roster is null || roster.Schema is not (1 or 2) || roster.GroupId == Guid.Empty ||
                roster.ProfileId == Guid.Empty || roster.Epoch < 1 || roster.Revision < 1 ||
                roster.Members.Count > 128 || roster.Members.Any(member => member.DeviceId == Guid.Empty ||
                    member.Grants is null || !ValidKey(member.PublicKey)) ||
                roster.Members.Select(member => member.DeviceId).Distinct().Count() != roster.Members.Count)
                return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(roster.OwnerPublicKey), out var read);
            return read == Convert.FromBase64String(roster.OwnerPublicKey).Length && key.KeySize == 256 &&
                key.VerifyData(Basis(roster), Convert.FromBase64String(roster.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { return false; }
    }

    internal static bool ValidKey(string? value)
    {
        try
        {
            if (value is null || value.Length is < 80 or > 512) return false;
            var bytes = Convert.FromBase64String(value);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            return read == bytes.Length && key.KeySize == 256;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    internal static byte[] EnrollmentBasis(Guid deviceId, string nonce, string publicKey) =>
        Encoding.UTF8.GetBytes($"TogetherServer shared-world enrollment v1\n{deviceId:N}\n{nonce}\n{publicKey}");

    internal static bool VerifyEnrollment(Guid deviceId, SharedWorldEnrollmentRequest request)
    {
        try
        {
            if (deviceId == Guid.Empty || request.Nonce.Length != 44 || !ValidKey(request.PublicKey)) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKey), out _);
            return key.VerifyData(EnrollmentBasis(deviceId, request.Nonce, request.PublicKey),
                Convert.FromBase64String(request.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { return false; }
    }

    internal static bool Accept(SharedWorldRoster roster, Guid profileId, Guid deviceId,
        string deviceKey, string pinnedOwnerKey, long floorEpoch, long floorRevision,
        TimeProvider? clock = null) =>
        Verify(roster) && roster.ProfileId == profileId && roster.OwnerPublicKey == pinnedOwnerKey &&
        roster.Epoch >= floorEpoch && roster.Revision >= floorRevision &&
        roster.Members.SingleOrDefault(member => member.DeviceId == deviceId) is { Revoked: false } member &&
        member.PublicKey == deviceKey && member.Grants.Receive &&
        HasActiveAccess(member, clock);

    internal static bool HasRole(SharedWorldRoster roster, Guid deviceId, string deviceKey,
        Func<SharedWorldGrants, bool> role, TimeProvider? clock = null) =>
        roster.Schema == 2 && Verify(roster) &&
        roster.Members.SingleOrDefault(member => member.DeviceId == deviceId) is
        { Revoked: false } member && member.PublicKey == deviceKey &&
        HasActiveAccess(member, clock) && role(member.Grants);

    private static bool HasActiveAccess(SharedWorldRosterMember member, TimeProvider? clock) =>
        member.AccessExpiresUtc is not { } expires || expires > (clock ?? TimeProvider.System).GetUtcNow();
}

internal sealed class SharedWorldEnrollmentNonces
{
    private readonly ConcurrentDictionary<(Guid Device, Guid Profile), (string Nonce, DateTimeOffset Expires)> pending = new();

    internal string Issue(Guid deviceId, Guid profileId)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        pending[(deviceId, profileId)] = (nonce, DateTimeOffset.UtcNow.AddMinutes(5));
        return nonce;
    }

    internal bool Consume(Guid deviceId, Guid profileId, string nonce) =>
        pending.TryRemove((deviceId, profileId), out var challenge) &&
        challenge.Expires >= DateTimeOffset.UtcNow &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(challenge.Nonce),
            Encoding.UTF8.GetBytes(nonce));
}

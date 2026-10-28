using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

public sealed record SharedWorldGrants(bool Receive = false, bool EligibleHost = false,
    bool RecoveryVoter = false, bool ManageSharing = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Reshare = false);
public sealed record SharedWorldRosterMember(Guid DeviceId, string PublicKey,
    SharedWorldGrants Grants, bool Revoked, DateTimeOffset? AccessExpiresUtc = null);
public sealed record SharedWorldRoster(int Schema, Guid GroupId, Guid ProfileId,
    long Epoch, long Revision, bool OwnerOverride, string OwnerPublicKey,
    IReadOnlyList<SharedWorldRosterMember> Members, string Signature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PreviousRosterHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? SignerDeviceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SignerPublicKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SharedWorldRosterMember>? OwnerLocalBaselineMembers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? HostAcceptedUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? HostAcceptanceSignature = null);
public sealed record SharedWorldOwnerEdit(Guid DeviceId,
    SharedWorldGrants? Grants = null, bool? Receive = null,
    bool? Revoked = null);
public sealed record SharedWorldEnrollmentChallenge(string Nonce);
public sealed record SharedWorldEnrollmentRequest(
    [property: JsonRequired] string Nonce, [property: JsonRequired] string PublicKey,
    [property: JsonRequired] string Signature);
public sealed record SharedWorldGovernanceRequest(bool? OwnerOverride = null,
    bool ReviewSourceChange = false);
public sealed record SharedWorldDeviceGrantsRequest([property: JsonRequired] SharedWorldGrants Grants);
public sealed record SharedWorldDelegateChangeRequest(Guid DeviceId, bool Receive,
    bool EligibleHost, bool RecoveryVoter, bool Revoked);

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
        : roster.Schema == 2 ? JsonSerializer.SerializeToUtf8Bytes(new
        {
            roster.Schema,
            roster.GroupId,
            roster.ProfileId,
            roster.Epoch,
            roster.Revision,
            roster.OwnerOverride,
            roster.OwnerPublicKey,
            roster.Members
        }, Json) : roster.OwnerLocalBaselineMembers is null ? JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer shared roster revision v3",
            roster.Schema,
            roster.GroupId,
            roster.ProfileId,
            roster.Epoch,
            roster.Revision,
            roster.OwnerOverride,
            roster.OwnerPublicKey,
            roster.Members,
            roster.PreviousRosterHash,
            roster.SignerDeviceId,
            roster.SignerPublicKey
        }, Json) : JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer shared roster revision v3",
            roster.Schema,
            roster.GroupId,
            roster.ProfileId,
            roster.Epoch,
            roster.Revision,
            roster.OwnerOverride,
            roster.OwnerPublicKey,
            roster.Members,
            roster.PreviousRosterHash,
            roster.SignerDeviceId,
            roster.SignerPublicKey,
            roster.OwnerLocalBaselineMembers
        }, Json);

    internal static string Hash(SharedWorldRoster roster) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(roster, Json)));

    internal static byte[] HostAcceptanceBasis(SharedWorldRoster roster, DateTimeOffset acceptedUtc) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer delegated roster Host acceptance v1",
            roster.ProfileId,
            roster.GroupId,
            roster.Epoch,
            roster.Revision,
            roster.PreviousRosterHash,
            roster.SignerDeviceId,
            roster.Signature,
            AcceptedUtc = acceptedUtc
        }, Json);

    internal static bool VerifyHostAcceptance(SharedWorldRoster roster)
    {
        try
        {
            if (roster.HostAcceptedUtc is not { } accepted ||
                string.IsNullOrEmpty(roster.HostAcceptanceSignature)) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(roster.OwnerPublicKey), out _);
            return key.VerifyData(HostAcceptanceBasis(roster, accepted),
                Convert.FromBase64String(roster.HostAcceptanceSignature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    internal static bool Verify(SharedWorldRoster? roster) => VerifySignature(roster) &&
        (roster!.Schema < 3 || roster.SignerDeviceId == Guid.Empty &&
            roster.SignerPublicKey == roster.OwnerPublicKey || VerifyHostAcceptance(roster));

    internal static bool VerifySignature(SharedWorldRoster? roster)
    {
        try
        {
            if (roster is null || roster.Schema is not (1 or 2 or 3) || roster.GroupId == Guid.Empty ||
                roster.ProfileId == Guid.Empty || roster.Epoch < 1 || roster.Revision < 1 ||
                roster.Members.Count > 128 || roster.Members.Any(member => member.DeviceId == Guid.Empty ||
                    member.Grants is null || !ValidKey(member.PublicKey)) ||
                roster.Members.Select(member => member.DeviceId).Distinct().Count() != roster.Members.Count ||
                roster.Members.Select(member => member.PublicKey).Distinct(StringComparer.Ordinal).Count() !=
                    roster.Members.Count ||
                roster.Schema == 3 && (roster.SignerDeviceId is null ||
                    !ValidKey(roster.SignerPublicKey) ||
                    roster.OwnerLocalBaselineMembers is { Count: > 128 } ||
                    roster.PreviousRosterHash is not null &&
                    (roster.PreviousRosterHash.Length != 64 ||
                     !roster.PreviousRosterHash.All(Uri.IsHexDigit))))
                return false;
            using var key = ECDsa.Create();
            var signer = roster.Schema == 3 ? roster.SignerPublicKey! : roster.OwnerPublicKey;
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signer), out var read);
            return ValidKey(roster.OwnerPublicKey) &&
                read == Convert.FromBase64String(signer).Length && key.KeySize == 256 &&
                key.VerifyData(Basis(roster), Convert.FromBase64String(roster.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { return false; }
    }

    internal static bool VerifyRevision(SharedWorldRoster roster, SharedWorldRoster? parent,
        string pinnedOwnerPublicKey, DateTimeOffset acceptedUtc, bool firstAcceptance)
    {
        if (!ValidKey(pinnedOwnerPublicKey) || !VerifySignature(roster) ||
            roster.OwnerPublicKey != pinnedOwnerPublicKey ||
            parent is not null && !VerifySignature(parent)) return false;
        if (parent is null)
            return roster.Schema == 3 && roster.PreviousRosterHash is null &&
                roster.Epoch == 1 && roster.Revision == 1 &&
                roster.SignerDeviceId == Guid.Empty && roster.SignerPublicKey == pinnedOwnerPublicKey;
        if (roster.Schema != 3 || roster.PreviousRosterHash != Hash(parent) ||
            roster.GroupId != parent.GroupId || roster.ProfileId != parent.ProfileId ||
            roster.OwnerPublicKey != parent.OwnerPublicKey ||
            roster.Epoch != parent.Epoch + 1 || roster.Revision != parent.Revision + 1)
            return false;
        if (roster.SignerDeviceId == Guid.Empty)
            return roster.SignerPublicKey == pinnedOwnerPublicKey;
        var signer = parent.Members.SingleOrDefault(member => member.DeviceId == roster.SignerDeviceId);
        if (roster.HostAcceptedUtc is not null || roster.HostAcceptanceSignature is not null)
        {
            if (!VerifyHostAcceptance(roster)) return false;
        }
        if (signer is null || signer.Revoked || !signer.Grants.ManageSharing ||
            signer.PublicKey != roster.SignerPublicKey ||
            firstAcceptance && signer.AccessExpiresUtc is { } expiry &&
                expiry <= (roster.HostAcceptedUtc ?? acceptedUtc) ||
            roster.OwnerOverride != parent.OwnerOverride ||
            !((roster.OwnerLocalBaselineMembers ?? []).SequenceEqual(parent.OwnerLocalBaselineMembers ?? [])) ||
            roster.Members.Count != parent.Members.Count) return false;
        var before = parent.Members.ToDictionary(member => member.DeviceId);
        foreach (var member in roster.Members)
        {
            if (!before.TryGetValue(member.DeviceId, out var old) ||
                member.PublicKey != old.PublicKey ||
                member.AccessExpiresUtc != old.AccessExpiresUtc ||
                member.Grants.ManageSharing != old.Grants.ManageSharing ||
                old.Grants.ManageSharing && member.Revoked != old.Revoked ||
                member.DeviceId == signer.DeviceId && member != old)
                return false;
        }
        return true;
    }

    // Older owners could publish the same local edit first as a standalone
    // legacy snapshot and later as an exact schema-3 child of an older snapshot.
    // This proves a representation change, never a changed grant or a fork.
    // Keep both signed records and the child's real signed parent as evidence.
    internal static bool VerifyLegacyOwnerProjection(SharedWorldRoster roster,
        SharedWorldRoster legacy, SharedWorldRoster parent, string pinnedOwnerPublicKey)
    {
        static bool PlainLegacy(SharedWorldRoster value) => value.Schema == 2 &&
            value.PreviousRosterHash is null && value.SignerDeviceId is null &&
            value.SignerPublicKey is null && value.OwnerLocalBaselineMembers is null &&
            value.HostAcceptedUtc is null && value.HostAcceptanceSignature is null;
        return roster is not null && legacy is not null && parent is not null &&
            VerifySignature(roster) && PlainLegacy(legacy) && PlainLegacy(parent) && Verify(legacy) && Verify(parent) &&
            roster.Schema == 3 && roster.SignerDeviceId == Guid.Empty &&
            roster.SignerPublicKey == pinnedOwnerPublicKey &&
            roster.HostAcceptedUtc is null && roster.HostAcceptanceSignature is null &&
            roster.ProfileId == legacy.ProfileId && roster.GroupId == legacy.GroupId &&
            roster.OwnerPublicKey == legacy.OwnerPublicKey &&
            roster.OwnerPublicKey == pinnedOwnerPublicKey &&
            roster.Epoch == legacy.Epoch && roster.Revision == legacy.Revision &&
            roster.OwnerOverride == legacy.OwnerOverride &&
            roster.Members.SequenceEqual(legacy.Members) &&
            roster.OwnerLocalBaselineMembers is not null &&
            roster.OwnerLocalBaselineMembers.SequenceEqual(legacy.Members) &&
            VerifyRevision(roster, parent, pinnedOwnerPublicKey, DateTimeOffset.MinValue, false);
    }

    internal static bool ValidKey(string? value) => CanonicalPublicKeyParsingCache.Shared.IsValid(value);

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
        roster.Schema is 2 or 3 && Verify(roster) &&
        roster.Members.SingleOrDefault(member => member.DeviceId == deviceId) is
        { Revoked: false } member && member.PublicKey == deviceKey &&
        HasActiveAccess(member, clock) && role(member.Grants);

    private static bool HasActiveAccess(SharedWorldRosterMember member, TimeProvider? clock) =>
        member.AccessExpiresUtc is not { } expires || expires > (clock ?? TimeProvider.System).GetUtcNow();
}

internal sealed class SharedWorldEnrollmentNonces(TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<(Guid Device, Guid Profile), (string Nonce, DateTimeOffset Expires)> pending = new();
    private DateTimeOffset UtcNow => (clock ?? TimeProvider.System).GetUtcNow();

    internal string Issue(Guid deviceId, Guid profileId)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        pending[(deviceId, profileId)] = (nonce, UtcNow.AddMinutes(5));
        return nonce;
    }

    internal bool Consume(Guid deviceId, Guid profileId, string nonce)
    {
        var key = (deviceId, profileId);
        if (!pending.TryGetValue(key, out var challenge) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(challenge.Nonce),
                Encoding.UTF8.GetBytes(nonce))) return false;
        // Remove only the challenge we matched; a newer Issue must survive.
        return pending.TryRemove(new(key, challenge)) && challenge.Expires >= UtcNow;
    }
}

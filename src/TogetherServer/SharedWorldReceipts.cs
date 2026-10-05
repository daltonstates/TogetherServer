using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record SharedWorldReceipt(int Schema, Guid GroupId, Guid ProfileId,
    string VersionHash, Guid DeviceId, long RosterEpoch, long RosterRevision,
    Guid ReceiptId, string Signature);
public sealed record SharedWorldReceiptResult(bool Ok, string Code);

internal static class SharedWorldReceiptTrust
{
    internal const int MaximumRequestBytes = 4096;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    internal static SharedWorldReceipt? Parse(byte[] body)
    {
        try { return JsonSerializer.Deserialize<SharedWorldReceipt>(body, WireJson); }
        catch (JsonException) { return null; }
    }

    internal static async Task<byte[]?> ReadBoundedAsync(Stream body, long? declaredLength,
        CancellationToken cancellationToken) =>
        await ReadBoundedAsync(body, declaredLength, MaximumRequestBytes, cancellationToken);

    internal static async Task<byte[]?> ReadBoundedAsync(Stream body, long? declaredLength,
        int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > 2 * 1024 * 1024 || declaredLength > maximumBytes) return null;
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        return length == 0 || length > maximumBytes ? null : buffer[..length];
    }

    internal static byte[] Basis(SharedWorldReceipt receipt) => Encoding.UTF8.GetBytes(
        FormattableString.Invariant(
            $"TogetherServer shared-world receipt v1\n{receipt.GroupId:N}\n{receipt.ProfileId:N}\n{receipt.VersionHash}\n{receipt.DeviceId:N}\n{receipt.RosterEpoch}\n{receipt.RosterRevision}\n{receipt.ReceiptId:N}"));

    internal static bool Verify(SharedWorldReceipt? receipt, string publicKey)
    {
        try
        {
            if (receipt is null || receipt.Schema != 1 || receipt.GroupId == Guid.Empty ||
                receipt.ProfileId == Guid.Empty || receipt.DeviceId == Guid.Empty ||
                receipt.ReceiptId == Guid.Empty || receipt.RosterEpoch < 1 || receipt.RosterRevision < 1 ||
                receipt.VersionHash.Length != 64 || !receipt.VersionHash.All(Uri.IsHexDigit) ||
                receipt.Signature.Length is < 64 or > 256 || !SharedWorldRosterTrust.ValidKey(publicKey))
                return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Basis(receipt), Convert.FromBase64String(receipt.Signature),
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { return false; }
    }
}

internal sealed partial class SharedWorldService
{
    private sealed record ReceiptSet(int Schema, Guid GroupId, Guid ProfileId,
        string VersionHash, IReadOnlyList<SharedWorldReceipt> Receipts);
    private string ReceiptsPath(Guid profileId) => Path.Combine(Root(profileId), "receipts.json");

    private ReceiptSet? ReadReceipts(Guid profileId)
    {
        var path = ReceiptsPath(profileId);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumManifestBytes ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Shared receipt record is oversized or linked.");
        var set = JsonSerializer.Deserialize<ReceiptSet>(File.ReadAllBytes(path), Json);
        if (set is null || set.Schema != 1 || set.ProfileId != profileId ||
            set.GroupId == Guid.Empty || set.VersionHash is null || set.VersionHash.Length != 64 ||
            set.Receipts is null || set.Receipts.Count > 128 ||
            set.Receipts.Any(item => item is null) ||
            set.Receipts.Select(item => item.DeviceId).Distinct().Count() != set.Receipts.Count)
            throw new InvalidDataException("Shared receipt record is invalid.");
        return set;
    }

    private int CountReceipts(ServerProfile profile, SharedWorldVersion version)
    {
        try
        {
            var set = ReadReceipts(version.ProfileId);
            if (set?.GroupId != version.GroupId || set.VersionHash != version.VersionHash) return 0;
            var roster = ReadRoster(profile);
            if (roster is null || roster.GroupId != version.GroupId) return 0;
            return set.Receipts.Count(receipt => receipt.GroupId == version.GroupId &&
                receipt.ProfileId == version.ProfileId && receipt.VersionHash == version.VersionHash &&
                roster.Members.SingleOrDefault(member => member.DeviceId == receipt.DeviceId) is
                { Revoked: false, Grants: { Receive: true } } member &&
                SharedWorldReceiptTrust.Verify(receipt, member.PublicKey));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { return 0; }
    }

    internal SharedWorldReceiptResult ConfirmReceipt(ServerProfile profile, Guid transportDeviceId,
        SharedWorldReceipt receipt)
    {
        lock (sync)
        {
            var status = Status(profile);
            var version = status.Latest;
            if (!status.Enabled || version is null || receipt.GroupId != version.GroupId ||
                receipt.ProfileId != profile.Id || receipt.VersionHash != version.VersionHash ||
                receipt.DeviceId != transportDeviceId)
                return new(false, "StaleOrWrongVersion");
            var roster = ReadRoster(profile);
            var member = roster?.Members.SingleOrDefault(item => item.DeviceId == transportDeviceId);
            if (roster is null || member is null || member.Revoked || !member.Grants.Receive ||
                roster.GroupId != receipt.GroupId || roster.Epoch != receipt.RosterEpoch ||
                roster.Revision != receipt.RosterRevision ||
                !SharedWorldReceiptTrust.Verify(receipt, member.PublicKey))
                return new(false, "ReceiptDenied");
            var existing = ReadReceipts(profile.Id);
            var entries = existing?.GroupId == version.GroupId && existing.VersionHash == version.VersionHash
                ? existing.Receipts.ToList() : [];
            var prior = entries.SingleOrDefault(item => item.DeviceId == transportDeviceId);
            if (prior is not null)
            {
                if (prior == receipt) return new(true, "AlreadyConfirmed");
                if (receipt.RosterEpoch < prior.RosterEpoch ||
                    receipt.RosterEpoch == prior.RosterEpoch && receipt.RosterRevision <= prior.RosterRevision)
                    return new(false, "ReceiptReplay");
                entries.Remove(prior);
            }
            if (entries.Count >= 128) return new(false, "ReceiptLimit");
            entries.Add(receipt);
            var path = ReceiptsPath(profile.Id);
            Directory.CreateDirectory(Root(profile.Id));
            var stage = path + ".new";
            File.WriteAllBytes(stage, JsonSerializer.SerializeToUtf8Bytes(
                new ReceiptSet(1, version.GroupId, profile.Id, version.VersionHash, entries), Json));
            File.Move(stage, path, true);
            return new(true, "CopyConfirmed");
        }
    }

    internal SharedWorldReceipt? VerifiedReceipt(ServerProfile profile, SharedWorldVersion version,
        Guid deviceId, SharedWorldRoster roster, bool requireEligibleHost = true)
    {
        lock (sync)
        {
            var set = ReadReceipts(profile.Id);
            if (set?.GroupId != version.GroupId || set.VersionHash != version.VersionHash ||
                roster.GroupId != version.GroupId || roster.ProfileId != profile.Id) return null;
            var member = roster.Members.SingleOrDefault(item => item.DeviceId == deviceId);
            var receipt = set.Receipts.SingleOrDefault(item => item.DeviceId == deviceId);
            return member is { Revoked: false, Grants.Receive: true } &&
                   (!requireEligibleHost || member.Grants.EligibleHost) &&
                   (member.AccessExpiresUtc is null || member.AccessExpiresUtc > DateTimeOffset.UtcNow) &&
                   receipt is not null && receipt.GroupId == version.GroupId &&
                   receipt.ProfileId == profile.Id && receipt.VersionHash == version.VersionHash &&
                   receipt.RosterEpoch == roster.Epoch && receipt.RosterRevision == roster.Revision &&
                   SharedWorldReceiptTrust.Verify(receipt, member.PublicKey) ? receipt : null;
        }
    }
}

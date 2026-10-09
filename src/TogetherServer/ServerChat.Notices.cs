using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

// The owner keeps only the current immutable revision. A clear is a signed
// revision too, retained independently of chat history and room membership.
public sealed record PinnedServerNotice(Guid HostId, Guid ProfileId, long Revision,
    DateTimeOffset UpdatedUtc, string? Text, string Signature);
public sealed record PinnedNoticeChange(string? Text, long? ExpectedRevision = null);
public sealed class PinnedNoticeConflictException() : InvalidOperationException(
    "The pinned notice changed. Reload it before saving.");

public sealed partial class ServerChat
{
    public const string PinnedNoticeCapability = "server-notices-v1";
    public const int MaximumNoticeTextLength = 2000;
    // Notice revisions are also decoded by JavaScript; require exact integers.
    public const long MaximumNoticeRevision = 9_007_199_254_740_991;
    private sealed record NoticeCopy(PinnedServerNotice Notice, string PublicKey);

    private static string OwnerNoticeFile(Guid hostId, Guid profileId) =>
        $"chat-notice-owner-{hostId:N}-{profileId:N}.protected";
    private static string NoticeCopyFile(Guid hostId, Guid profileId) =>
        $"chat-notice-copy-{hostId:N}-{profileId:N}.protected";
    private static string NoticeReviewFile(Guid hostId, Guid profileId, bool owner) =>
        $"chat-notice-{(owner ? "owner" : "copy")}-review-{hostId:N}-{profileId:N}.protected";

    public static bool ValidNoticeText(string? text) =>
        text is { Length: >= 1 and <= MaximumNoticeTextLength } &&
        !string.IsNullOrWhiteSpace(text) && !text.Any(c =>
            char.IsControl(c) && c is not '\n' and not '\t' ||
            c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069');

    private static byte[] NoticeBasis(PinnedServerNotice notice) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            purpose = PinnedNoticeCapability,
            notice.HostId,
            notice.ProfileId,
            notice.Revision,
            notice.UpdatedUtc,
            notice.Text
        }, Json);

    public static bool ValidNoticePublicKey(string? publicKey)
    {
        if (publicKey?.Length is not (>= 1 and <= 200)) return false;
        try
        {
            using var key = ECDsa.Create();
            var encoded = Convert.FromBase64String(publicKey);
            key.ImportSubjectPublicKeyInfo(encoded, out var consumed);
            return consumed == encoded.Length && key.KeySize == 256 &&
                key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    public static bool VerifyNotice(PinnedServerNotice? notice, string? publicKey,
        Guid hostId, Guid profileId)
    {
        if (notice is null || hostId == Guid.Empty || profileId == Guid.Empty ||
            notice.HostId != hostId || notice.ProfileId != profileId ||
            notice.Revision is < 1 or > MaximumNoticeRevision ||
            notice.UpdatedUtc < DateTimeOffset.UnixEpoch || notice.UpdatedUtc.Offset != TimeSpan.Zero ||
            notice.UpdatedUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
            notice.Text is not null && !ValidNoticeText(notice.Text) ||
            notice.Signature?.Length is not (>= 1 and <= 200) ||
            publicKey?.Length is not (>= 1 and <= 200)) return false;
        try
        {
            using var key = ECDsa.Create();
            var encodedKey = Convert.FromBase64String(publicKey);
            key.ImportSubjectPublicKeyInfo(encodedKey, out var consumed);
            var signature = Convert.FromBase64String(notice.Signature);
            return consumed == encodedKey.Length && key.KeySize == 256 &&
                key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value &&
                signature.Length == 64 && key.VerifyData(NoticeBasis(notice), signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    private InvalidDataException NoticeNeedsReview(Guid hostId, Guid profileId, bool owner)
    {
        data.SaveProtected(NoticeReviewFile(hostId, profileId, owner), [1]);
        return new InvalidDataException("The saved pinned notice needs owner review.");
    }

    private static void CheckNoticeRoom(Guid hostId, Guid profileId)
    {
        if (hostId == Guid.Empty || profileId == Guid.Empty)
            throw new ArgumentException("Choose a saved server room.");
    }

    public PinnedServerNotice? ReadOwnerNotice(Guid hostId, Guid profileId)
    {
        CheckNoticeRoom(hostId, profileId);
        lock (sync)
        {
            if (data.HasProtected(NoticeReviewFile(hostId, profileId, true)))
                throw new InvalidDataException("The saved pinned notice needs owner review.");
            var file = OwnerNoticeFile(hostId, profileId);
            var existed = data.HasProtected(file);
            var notice = data.LoadProtectedJson<PinnedServerNotice>(file);
            if (!existed && notice is null) return null;
            if (!VerifyNotice(notice, OwnerPublicKey(), hostId, profileId))
                throw NoticeNeedsReview(hostId, profileId, true);
            return notice;
        }
    }

    // Only the loopback owner route calls this. Sync inputs have no notice field,
    // and a recovered Friend chat log never replaces the owner's current notice.
    public PinnedServerNotice SetOwnerNotice(Guid hostId, Guid profileId, string? text,
        long? expectedRevision = null)
    {
        CheckNoticeRoom(hostId, profileId);
        if (text is not null && !ValidNoticeText(text))
            throw new ArgumentException("Write a pinned notice of at most 2000 characters.");
        if (expectedRevision is < 0 or > MaximumNoticeRevision)
            throw new ArgumentException("The pinned notice revision is invalid.");
        lock (sync)
        {
            var current = ReadOwnerNotice(hostId, profileId);
            if (expectedRevision is { } expected && expected != (current?.Revision ?? 0))
                throw new PinnedNoticeConflictException();
            if (current is not null && current.Text == text) return current;
            if (current?.Revision >= MaximumNoticeRevision)
                throw new InvalidDataException("The pinned notice needs owner review.");
            var updated = DateTimeOffset.UtcNow;
            if (current is not null && updated <= current.UpdatedUtc)
                updated = current.UpdatedUtc.AddTicks(1);
            var notice = new PinnedServerNotice(hostId, profileId, (current?.Revision ?? 0) + 1,
                updated, text, "");
            using var key = OwnerKey();
            notice = notice with
            {
                Signature = Convert.ToBase64String(key.SignData(NoticeBasis(notice), HashAlgorithmName.SHA256))
            };
            data.SaveProtected(OwnerNoticeFile(hostId, profileId), JsonSerializer.SerializeToUtf8Bytes(notice, Json));
            return notice;
        }
    }

    private NoticeCopy? LoadNoticeCopy(Guid hostId, Guid profileId)
    {
        if (data.HasProtected(NoticeReviewFile(hostId, profileId, false)))
            throw new InvalidDataException("The saved pinned notice needs owner review.");
        var file = NoticeCopyFile(hostId, profileId);
        var existed = data.HasProtected(file);
        var copy = data.LoadProtectedJson<NoticeCopy>(file);
        if (!existed && copy is null) return null;
        if (copy is null || !VerifyNotice(copy.Notice, copy.PublicKey, hostId, profileId))
            throw NoticeNeedsReview(hostId, profileId, false);
        return copy;
    }

    public PinnedServerNotice? ReadNoticeCopy(Guid hostId, Guid profileId, string? pinnedPublicKey)
    {
        CheckNoticeRoom(hostId, profileId);
        lock (sync)
        {
            var copy = LoadNoticeCopy(hostId, profileId);
            if (copy is not null && copy.PublicKey != pinnedPublicKey)
                throw new InvalidDataException("The pinned notice signing identity did not match this saved connection.");
            return copy?.Notice;
        }
    }

    // A Friend accepts only the current owner's signed response. Lower revisions,
    // missing data after an observed revision, and edits reusing a revision fail
    // closed. Notice copies never participate in the bidirectional chat merge.
    public bool CanAcceptNoticeCopy(Guid hostId, Guid profileId, PinnedServerNotice? notice, string publicKey)
    {
        if (hostId == Guid.Empty || profileId == Guid.Empty ||
            !ValidNoticePublicKey(publicKey) ||
            notice is not null && !VerifyNotice(notice, publicKey, hostId, profileId)) return false;
        lock (sync)
        {
            var current = LoadNoticeCopy(hostId, profileId);
            if (current is not null && current.PublicKey != publicKey) return false;
            if (notice is null) return current is null;
            if (current is not null)
            {
                if (notice.Revision < current.Notice.Revision) return false;
                if (notice.Revision == current.Notice.Revision) return notice == current.Notice;
                if (notice.UpdatedUtc <= current.Notice.UpdatedUtc) return false;
            }
            return true;
        }
    }

    public bool AcceptNoticeCopy(Guid hostId, Guid profileId, PinnedServerNotice? notice, string publicKey)
    {
        lock (sync)
        {
            if (!CanAcceptNoticeCopy(hostId, profileId, notice, publicKey)) return false;
            if (notice is not null)
                data.SaveProtected(NoticeCopyFile(hostId, profileId),
                    JsonSerializer.SerializeToUtf8Bytes(new NoticeCopy(notice, publicKey), Json));
            return true;
        }
    }

    private void ForgetNoticeCopy(Guid hostId, Guid profileId)
    {
        data.DeleteProtected(NoticeCopyFile(hostId, profileId));
        data.DeleteProtected(NoticeReviewFile(hostId, profileId, false));
    }
}

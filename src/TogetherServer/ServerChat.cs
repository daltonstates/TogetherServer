using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

// A room is identified by the Host identity and one saved server. Every accepted
// entry is immutable and signed by a key that survives TLS certificate rotation.
// Copies on Friend PCs can repopulate a Host whose chat log was lost.
public sealed record ChatEntry(Guid Id, Guid HostId, Guid ProfileId, Guid AuthorId,
    string Author, DateTimeOffset SentUtc, string Text, string Signature);
public sealed record ChatDraft(Guid Id, string Text);
public sealed record ChatSyncRequest(IReadOnlyList<ChatEntry>? Entries,
    IReadOnlyList<ChatDraft>? Drafts);
public sealed record ChatSyncResponse(bool Ok, string Code, string Message,
    Guid HostId, Guid ProfileId, string? PublicKey, IReadOnlyList<ChatEntry> Entries);
public sealed record ChatMember(Guid DeviceId, string Name, bool Allowed);
public sealed record ChatRoomView(bool Ok, string Code, string Message, Guid HostId,
    Guid ProfileId, IReadOnlyList<ChatEntry> Entries, IReadOnlyList<ChatDraft> Pending,
    IReadOnlyList<ChatMember>? Members = null);
public sealed record ChatPostRequest(string? Text);
public sealed record ChatMemberChange(bool Allowed);

public sealed class ServerChat(LocalData data)
{
    public const int MaximumTextLength = 500;
    public const int MaximumEntries = 200;
    public const int MaximumDrafts = 20;
    public const int MaximumWireBytes = 768 * 1024;
    private const string KeyFile = "chat-owner-key.protected";
    private const string KeyBlockedFile = "chat-owner-key-review.protected";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = SharedWorldMutationGate.For(data.RootPath);

    private static string RoomFile(Guid hostId, Guid profileId) =>
        $"chat-{hostId:N}-{profileId:N}.protected";
    private static string PendingFile(Guid hostId, Guid profileId, Guid deviceId) =>
        $"chat-pending-{hostId:N}-{profileId:N}-{deviceId:N}.protected";
    private static string MembersFile(Guid profileId) => $"chat-members-{profileId:N}.protected";
    private static string MembersBlockedFile(Guid profileId) => $"chat-members-review-{profileId:N}.protected";

    public static bool ValidText(string? value) => value is { Length: >= 1 and <= MaximumTextLength } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsControl(c) && c is not '\n' and not '\t');

    private static string AuthorLabel(string? name)
    {
        var label = new string((name ?? "").Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        return label.Length > 0 ? label : "Friend PC";
    }

    private ECDsa OwnerKey()
    {
        if (data.HasProtected(KeyBlockedFile))
            throw new InvalidDataException("The chat signing key needs owner review.");
        var existed = data.HasProtected(KeyFile);
        var bytes = data.LoadProtected(KeyFile);
        if (bytes is null)
        {
            if (existed)
            {
                data.SaveProtected(KeyBlockedFile, [1]);
                throw new InvalidDataException("The chat signing key needs owner review.");
            }
            using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            bytes = created.ExportPkcs8PrivateKey();
            data.SaveProtected(KeyFile, bytes);
        }
        var key = ECDsa.Create();
        try { key.ImportPkcs8PrivateKey(bytes, out _); return key; }
        catch
        {
            key.Dispose();
            data.SaveProtected(KeyBlockedFile, [1]);
            throw new InvalidDataException("The chat signing key needs owner review.");
        }
    }

    public string OwnerPublicKey()
    {
        lock (sync)
        {
            using var key = OwnerKey();
            return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
    }

    private static byte[] Basis(ChatEntry entry) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = 1,
        entry.Id,
        entry.HostId,
        entry.ProfileId,
        entry.AuthorId,
        entry.Author,
        entry.SentUtc,
        entry.Text
    }, Json);

    public static bool Verify(ChatEntry? entry, string? publicKey, Guid hostId, Guid profileId)
    {
        if (entry is null || entry.Id == Guid.Empty || entry.HostId != hostId ||
            entry.ProfileId != profileId || entry.Author?.Length is not (>= 1 and <= 80) ||
            entry.Author.Any(char.IsControl) || !ValidText(entry.Text) ||
            entry.SentUtc < DateTimeOffset.UtcNow.AddDays(-31) ||
            entry.SentUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
            entry.Signature?.Length is not (>= 1 and <= 200) ||
            publicKey is null || publicKey.Length > 200)
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Basis(entry), Convert.FromBase64String(entry.Signature),
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    private ChatEntry Sign(Guid hostId, Guid profileId, Guid id, Guid authorId,
        string author, string text)
    {
        var entry = new ChatEntry(id, hostId, profileId, authorId, author,
            DateTimeOffset.UtcNow, text, "");
        using var key = OwnerKey();
        return entry with { Signature = Convert.ToBase64String(key.SignData(Basis(entry), HashAlgorithmName.SHA256)) };
    }

    private List<ChatEntry> Load(Guid hostId, Guid profileId) =>
        data.LoadProtectedJson<List<ChatEntry>>(RoomFile(hostId, profileId)) ?? [];

    private static List<ChatEntry> Retain(IEnumerable<ChatEntry> entries) => entries
        .Where(entry => entry.SentUtc >= DateTimeOffset.UtcNow.AddDays(-30))
        .OrderBy(entry => entry.SentUtc).ThenBy(entry => entry.Id)
        .TakeLast(MaximumEntries).ToList();

    public IReadOnlyList<ChatEntry> Read(Guid hostId, Guid profileId)
    {
        lock (sync) return Retain(Load(hostId, profileId));
    }

    public ChatEntry Post(Guid hostId, Guid profileId, Guid authorId,
        string author, string text, Guid? id = null)
    {
        if (!ValidText(text))
            throw new ArgumentException("Write a chat message of at most 500 characters.");
        author = AuthorLabel(author);
        lock (sync)
        {
            var entries = Retain(Load(hostId, profileId));
            var messageId = id ?? Guid.NewGuid();
            if (messageId == Guid.Empty) throw new ArgumentException("The message ID is invalid.");
            var existing = entries.SingleOrDefault(entry => entry.Id == messageId);
            if (existing is not null)
            {
                if (existing.AuthorId != authorId || existing.Text != text)
                    throw new InvalidDataException("The message ID was already used.");
                return existing;
            }
            var signed = Sign(hostId, profileId, messageId, authorId, author, text);
            entries.Add(signed);
            data.SaveProtected(RoomFile(hostId, profileId),
                JsonSerializer.SerializeToUtf8Bytes(Retain(entries), Json));
            return signed;
        }
    }

    // The Host verifies its own signatures before accepting a returned copy.
    // This recovers history without trusting a Friend to choose authors or times.
    public bool Merge(Guid hostId, Guid profileId, IReadOnlyList<ChatEntry>? received,
        string publicKey)
    {
        if (received is null || received.Count > MaximumEntries ||
            received.Any(entry => !Verify(entry, publicKey, hostId, profileId))) return false;
        lock (sync)
        {
            var loaded = Load(hostId, profileId);
            var entries = Retain(loaded);
            var known = entries.ToDictionary(entry => entry.Id);
            var changed = entries.Count != loaded.Count;
            foreach (var entry in received)
            {
                if (known.TryGetValue(entry.Id, out var existing))
                {
                    if (existing != entry) return false;
                }
                else { entries.Add(entry); known.Add(entry.Id, entry); changed = true; }
            }
            var retained = Retain(entries);
            if (changed)
                data.SaveProtected(RoomFile(hostId, profileId), JsonSerializer.SerializeToUtf8Bytes(retained, Json));
            return true;
        }
    }

    public bool IsMember(Guid profileId, Guid deviceId)
    {
        lock (sync)
        {
            if (data.HasProtected(MembersBlockedFile(profileId))) return false;
            var file = MembersFile(profileId);
            var existed = data.HasProtected(file);
            var excluded = data.LoadProtectedJson<List<Guid>>(file);
            if (existed && excluded is null)
            {
                data.SaveProtected(MembersBlockedFile(profileId), [1]);
                return false;
            }
            return (!existed || excluded is not null) && excluded?.Contains(deviceId) != true;
        }
    }

    public void SetMember(Guid profileId, Guid deviceId, bool allowed)
    {
        lock (sync)
        {
            if (data.HasProtected(MembersBlockedFile(profileId)))
                throw new InvalidDataException("The chat member list needs owner review.");
            var file = MembersFile(profileId);
            var existed = data.HasProtected(file);
            var saved = data.LoadProtectedJson<List<Guid>>(file);
            if (existed && saved is null)
            {
                data.SaveProtected(MembersBlockedFile(profileId), [1]);
                throw new InvalidDataException("The chat member list needs owner review.");
            }
            var excluded = (saved ?? []).ToHashSet();
            if (allowed) excluded.Remove(deviceId);
            else excluded.Add(deviceId);
            data.SaveProtected(MembersFile(profileId),
                JsonSerializer.SerializeToUtf8Bytes(excluded.Order().ToList(), Json));
        }
    }

    public IReadOnlyList<ChatDraft> Pending(Guid hostId, Guid profileId, Guid deviceId)
    {
        lock (sync)
        {
            var file = PendingFile(hostId, profileId, deviceId);
            var existed = data.HasProtected(file);
            var pending = data.LoadProtectedJson<List<ChatDraft>>(file);
            if (existed && pending is null)
                throw new InvalidDataException("The saved chat drafts need review.");
            return pending ?? [];
        }
    }

    public ChatDraft Queue(Guid hostId, Guid profileId, Guid deviceId, string text)
    {
        if (!ValidText(text)) throw new ArgumentException("Write a chat message of at most 500 characters.");
        lock (sync)
        {
            var drafts = Pending(hostId, profileId, deviceId).ToList();
            if (drafts.Count >= MaximumDrafts) throw new InvalidOperationException("Send queued messages before adding more.");
            var draft = new ChatDraft(Guid.NewGuid(), text);
            drafts.Add(draft);
            data.SaveProtected(PendingFile(hostId, profileId, deviceId),
                JsonSerializer.SerializeToUtf8Bytes(drafts, Json));
            return draft;
        }
    }

    public void Confirm(Guid hostId, Guid profileId, Guid deviceId)
    {
        lock (sync)
        {
            var signed = Read(hostId, profileId).Select(entry => entry.Id).ToHashSet();
            var drafts = Pending(hostId, profileId, deviceId).Where(draft => !signed.Contains(draft.Id)).ToList();
            data.SaveProtected(PendingFile(hostId, profileId, deviceId),
                JsonSerializer.SerializeToUtf8Bytes(drafts, Json));
        }
    }

    public void Forget(Guid hostId, Guid deviceId, IEnumerable<Guid> profileIds,
        bool preserveSharedRoomCopies = false)
    {
        lock (sync)
            foreach (var profileId in profileIds.Distinct())
            {
                if (!preserveSharedRoomCopies) data.DeleteProtected(RoomFile(hostId, profileId));
                data.DeleteProtected(PendingFile(hostId, profileId, deviceId));
            }
    }
}

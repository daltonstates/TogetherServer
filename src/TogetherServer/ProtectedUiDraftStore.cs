using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record ProtectedUiDraftIdentity(string Purpose, Guid ProfileId, Guid? ConnectionId, string Key);
public sealed record ProtectedUiDraftSave(ProtectedUiDraftIdentity Identity, string Text, long ExpectedRevision);
public sealed record ProtectedUiDraftClear(ProtectedUiDraftIdentity Identity, long ExpectedRevision);
public sealed record ProtectedUiDraftResult(bool Ok, string? Text, long Revision, string Message);

// This store is local review material only. It cannot select a file, change settings or send chat.
public sealed class ProtectedUiDraftStore
{
    public const int MaximumEntries = 128;
    public const int MaximumDraftBytes = 64 * 1024;
    public const int MaximumFileDraftBytes = 2 * 1024 * 1024;
    public const int MaximumStoreBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const string FileName = "ui-drafts.protected";
    private const long MaximumRevision = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly Func<byte[]?> load;
    private readonly Action<byte[]> save;
    private readonly Func<DateTimeOffset> clock;
    private State? state;
    private bool unavailable;

    public ProtectedUiDraftStore(LocalData data, Func<DateTimeOffset>? clock = null)
        : this(() =>
        {
            if (data.Recovery.Notices.Any(notice => notice.StateFile == FileName))
                throw new InvalidDataException("Protected drafts need local recovery review.");
            var existed = data.HasProtected(FileName);
            var bytes = data.LoadProtected(FileName);
            if (existed && bytes is null) throw new InvalidDataException("Protected drafts could not be recovered.");
            return bytes;
        }, bytes => data.SaveProtected(FileName, bytes), clock)
    { }

    // Injected storage is used by pure/synthetic checks; production always uses CurrentUser protection above.
    internal ProtectedUiDraftStore(Func<byte[]?> load, Action<byte[]> save, Func<DateTimeOffset>? clock = null)
    {
        this.load = load;
        this.save = save;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ProtectedUiDraftResult Read(ProtectedUiDraftIdentity identity)
    {
        ValidateIdentity(identity);
        lock (sync)
        {
            if (!TryLoad()) return Unavailable();
            try
            {
                PruneExpired();
                return Current(identity, true, "Draft checked. Recover it only after reviewing the current server.");
            }
            catch (Exception error) when (StorageFailure(error)) { state = null; return Unavailable(); }
        }
    }

    public ProtectedUiDraftResult Save(ProtectedUiDraftIdentity identity, string text, long expectedRevision)
    {
        ValidateIdentity(identity);
        ValidateRevision(expectedRevision);
        if (text is null || Encoding.UTF8.GetByteCount(text) > MaximumBytesFor(identity))
            throw new InvalidDataException("Draft text exceeds this editor's protected storage limit.");
        lock (sync)
        {
            if (!TryLoad()) return Unavailable();
            try
            {
                PruneExpired();
                var current = Current(identity, true, "Draft checked.");
                if (current.Revision != expectedRevision)
                    return current with { Ok = false, Message = "This draft changed. Review the recovered copy before saving again." };
                var copy = Clone();
                var entry = copy.Entries.SingleOrDefault(item => item.Identity == identity);
                if (entry is null && copy.Entries.Count == MaximumEntries)
                {
                    // Cleared entries can be compacted, but never silently evict an unfinished draft.
                    var oldestClear = copy.Entries.Where(item => item.Text is null).OrderBy(item => item.UpdatedUtc).FirstOrDefault();
                    if (oldestClear is null)
                        return current with { Ok = false, Message = "Protected draft storage is full. Review or discard an older draft first." };
                    copy.Entries.Remove(oldestClear);
                    AdvanceAbsenceFloor(copy);
                }
                copy.Entries.RemoveAll(item => item.Identity == identity);
                var revision = Advance(copy);
                copy.Entries.Add(new Entry(identity, text, revision, clock()));
                if (!TryPersist(copy))
                    return current with { Ok = false, Message = "Protected draft storage reached its 4 MiB limit. Keep a smaller edit." };
                return new(true, text, revision, "Draft saved securely on this PC.");
            }
            catch (Exception error) when (StorageFailure(error)) { state = null; return Unavailable(); }
        }
    }

    public ProtectedUiDraftResult Clear(ProtectedUiDraftIdentity identity, long expectedRevision)
    {
        ValidateIdentity(identity);
        ValidateRevision(expectedRevision);
        lock (sync)
        {
            if (!TryLoad()) return Unavailable();
            try
            {
                PruneExpired();
                var current = Current(identity, true, "Draft checked.");
                if (current.Revision != expectedRevision)
                    return current with { Ok = false, Message = "This draft changed. Review it before discarding the newer copy." };
                var copy = Clone();
                var exists = copy.Entries.Any(item => item.Identity == identity);
                if (!exists && copy.Entries.Count == MaximumEntries)
                {
                    var oldestClear = copy.Entries.Where(item => item.Text is null).OrderBy(item => item.UpdatedUtc).FirstOrDefault();
                    if (oldestClear is not null)
                    {
                        copy.Entries.Remove(oldestClear);
                        AdvanceAbsenceFloor(copy);
                    }
                    else
                    {
                        // No slot is available: compact this absence-only
                        // tombstone directly into the replay-rejection floor.
                        var absentRevision = AdvanceAbsenceFloor(copy);
                        if (!TryPersist(copy)) return Unavailable();
                        return new(true, null, absentRevision, "Draft cleared.");
                    }
                }
                copy.Entries.RemoveAll(item => item.Identity == identity);
                var revision = Advance(copy);
                copy.Entries.Add(new Entry(identity, null, revision, clock()));
                if (!TryPersist(copy)) return Unavailable();
                return new(true, null, revision, "Draft cleared.");
            }
            catch (Exception error) when (StorageFailure(error)) { state = null; return Unavailable(); }
        }
    }

    public static ProtectedUiDraftIdentity ParseIdentity(JsonElement input)
    {
        RequireFields(input, ["purpose", "profileId", "key"], ["connectionId"]);
        return ParseIdentityFields(input);
    }

    public static ProtectedUiDraftSave ParseSave(JsonElement input)
    {
        RequireFields(input, ["purpose", "profileId", "key", "text", "expectedRevision"], ["connectionId"]);
        var identity = ParseIdentityFields(input);
        var text = ReadString(input, "text", MaximumBytesFor(identity));
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytesFor(identity)) throw new InvalidDataException("Draft text is too large.");
        return new(identity, text, ReadRevision(input, "expectedRevision"));
    }

    public static ProtectedUiDraftClear ParseClear(JsonElement input)
    {
        RequireFields(input, ["purpose", "profileId", "key", "expectedRevision"], ["connectionId"]);
        return new(ParseIdentityFields(input), ReadRevision(input, "expectedRevision"));
    }

    public static void ValidateIdentity(ProtectedUiDraftIdentity identity)
    {
        if (identity.Purpose is not ("file" or "settings" or "list" or "chat") ||
            identity.Key is null || identity.Key.Length is < 1 or > 96 ||
            identity.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not ':') ||
            (identity.ConnectionId == Guid.Empty && (identity.Purpose != "chat" || identity.Key != "compose")) ||
            (identity.ProfileId == Guid.Empty &&
             (identity.Purpose != "settings" || identity.Key != "host-setup" || identity.ConnectionId is not null)))
            throw new InvalidDataException("The draft identity is not a reviewed local server or setup key.");
    }

    public static int MaximumBytesFor(ProtectedUiDraftIdentity identity) =>
        identity.Purpose == "file" ? MaximumFileDraftBytes : MaximumDraftBytes;

    private static ProtectedUiDraftIdentity ParseIdentityFields(JsonElement input)
    {
        var purpose = ReadString(input, "purpose", 12);
        var profile = ReadGuid(input, "profileId");
        Guid? connection = null;
        if (input.TryGetProperty("connectionId", out var field) && field.ValueKind != JsonValueKind.Null)
            connection = ReadGuid(input, "connectionId");
        var identity = new ProtectedUiDraftIdentity(purpose, profile, connection, ReadString(input, "key", 96));
        ValidateIdentity(identity);
        return identity;
    }

    private bool TryLoad()
    {
        if (unavailable) return false;
        if (state is not null) return true;
        try
        {
            var bytes = load();
            if (bytes is null) { state = new State(); return true; }
            if (bytes.Length > MaximumStoreBytes) throw new InvalidDataException("Draft storage is too large.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schemaField) ||
                schemaField.ValueKind != JsonValueKind.Number ||
                !schemaField.TryGetInt32(out var schema) || schema is not (1 or 2))
                throw new InvalidDataException("Draft schema is unavailable.");
            RequireFields(root, schema == 1 ? ["schema", "counter", "entries"] : ["schema", "counter", "absenceFloor", "entries"], []);
            var loaded = new State { Counter = ReadRevision(root, "counter") };
            // Version 1 used the allocation counter for every absent identity.
            // Preserve its final absence revision on migration so omitted old
            // content/tombstones cannot be revived by a saved pre-upgrade CAS.
            loaded.AbsenceFloor = schema == 1 ? loaded.Counter : ReadRevision(root, "absenceFloor");
            if (loaded.AbsenceFloor > loaded.Counter)
                throw new InvalidDataException("Draft absence revision is invalid.");
            var entries = root.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaximumEntries)
                throw new InvalidDataException("Draft storage has too many entries.");
            foreach (var value in entries.EnumerateArray())
            {
                RequireFields(value, ["identity", "text", "revision", "updatedUtc"], []);
                var identity = ParseIdentity(value.GetProperty("identity"));
                var textValue = value.GetProperty("text");
                var text = textValue.ValueKind == JsonValueKind.Null ? null : ReadString(value, "text", MaximumBytesFor(identity));
                var revision = ReadRevision(value, "revision");
                if (revision == 0 || revision > loaded.Counter ||
                    (text is not null && Encoding.UTF8.GetByteCount(text) > MaximumBytesFor(identity)) ||
                    loaded.Entries.Any(item => item.Identity == identity) ||
                    value.GetProperty("updatedUtc").ValueKind != JsonValueKind.String ||
                    !value.GetProperty("updatedUtc").TryGetDateTimeOffset(out var updated) || updated.Offset != TimeSpan.Zero)
                    throw new InvalidDataException("Draft storage contains an invalid entry.");
                loaded.Entries.Add(new Entry(identity, text, revision, updated));
            }
            state = loaded;
            return true;
        }
        catch (Exception error) when (StorageFailure(error)) { unavailable = true; return false; }
    }

    private void PruneExpired()
    {
        var cutoff = clock() - Retention;
        if (!state!.Entries.Any(item => item.UpdatedUtc <= cutoff)) return;
        var copy = Clone();
        copy.Entries.RemoveAll(item => item.UpdatedUtc <= cutoff);
        // Advance the absence floor before removing old content/tombstones; stale saves cannot revive either.
        AdvanceAbsenceFloor(copy);
        if (!TryPersist(copy)) throw new InvalidDataException("Draft retention could not be stored.");
    }

    private State Clone() => new() { Counter = state!.Counter, AbsenceFloor = state.AbsenceFloor, Entries = state.Entries.ToList() };
    private bool TryPersist(State copy)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(copy, Json);
        if (bytes.Length > MaximumStoreBytes) return false;
        try { save(bytes); }
        catch
        {
            // A write may have committed before its acknowledgement failed.
            // Reload protected canonical state before another CAS decision.
            state = null;
            throw;
        }
        state = copy;
        return true;
    }
    private ProtectedUiDraftResult Current(ProtectedUiDraftIdentity identity, bool ok, string message)
    {
        var entry = state!.Entries.SingleOrDefault(item => item.Identity == identity);
        return new(ok, entry?.Text, entry?.Revision ?? state.AbsenceFloor, message);
    }
    private ProtectedUiDraftResult Unavailable() => new(false, null, state?.Counter ?? 0,
        "Protected drafts are unavailable on this PC. Keep this edit open and try again after reviewing app recovery.");
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or JsonException or System.Security.Cryptography.CryptographicException or
        PlatformNotSupportedException or OverflowException;
    private static long Advance(State copy)
    {
        if (copy.Counter >= MaximumRevision) throw new InvalidDataException("Draft revision limit reached.");
        return ++copy.Counter;
    }
    private static long AdvanceAbsenceFloor(State copy) => copy.AbsenceFloor = Advance(copy);
    private static void ValidateRevision(long value)
    {
        if (value is < 0 or > MaximumRevision) throw new InvalidDataException("Draft revision is invalid.");
    }
    private static long ReadRevision(JsonElement input, string name)
    {
        if (input.GetProperty(name).ValueKind != JsonValueKind.Number ||
            !input.GetProperty(name).TryGetInt64(out var value)) throw new InvalidDataException("Draft revision is invalid.");
        ValidateRevision(value);
        return value;
    }
    private static Guid ReadGuid(JsonElement input, string name)
    {
        var text = ReadString(input, name, 36);
        if (!Guid.TryParseExact(text, "D", out var value)) throw new InvalidDataException("Draft scope is invalid.");
        return value;
    }
    private static string ReadString(JsonElement input, string name, int maximum)
    {
        var field = input.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text || text.Length > maximum)
            throw new InvalidDataException("Draft text field is invalid.");
        return text;
    }
    private static void RequireFields(JsonElement input, string[] required, string[] optional)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Draft input must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
            if ((!required.Contains(property.Name) && !optional.Contains(property.Name)) || !names.Add(property.Name))
                throw new InvalidDataException("Draft input contains unknown or duplicate fields.");
        if (required.Any(name => !names.Contains(name))) throw new InvalidDataException("Draft input is incomplete.");
    }
    private sealed class State
    {
        public int Schema { get; init; } = 2;
        public long Counter { get; set; }
        public long AbsenceFloor { get; set; }
        public List<Entry> Entries { get; set; } = [];
    }
    private sealed record Entry(ProtectedUiDraftIdentity Identity, string? Text, long Revision, DateTimeOffset UpdatedUtc);
}

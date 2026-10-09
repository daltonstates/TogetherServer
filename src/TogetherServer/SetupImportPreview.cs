using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

public sealed record SetupImportSourceFile(string Name, long? Bytes, DateTimeOffset? ModifiedUtc);
public sealed record SetupImportPreview(Guid SelectionId, Guid ProfileId, string Kind, string WorldId,
    IReadOnlyList<SetupImportSourceFile> SourceFiles, long? TotalBytes, DateTimeOffset? ModifiedUtc,
    DateTimeOffset ExpiresUtc);
public sealed record SetupImportPreviewResult(bool Ok, string Code, string Message,
    SetupImportPreview? Preview = null);
public sealed record SetupImportConfirmation(Guid SelectionId, Guid ProfileId, string Kind);

// Only Program's native owner picker supplies a path. Never serialize the resolved source to the browser.
internal sealed record SetupImportResolution(bool Ok, string Code, string Message,
    [property: JsonIgnore] string? SelectedPath = null);

internal sealed class SetupImportSelections(LocalData data, bool freshWorldsOnly,
    TimeProvider? timeProvider = null)
{
    internal const int MaximumEntries = 32;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<Guid, PendingSelection> selections = [];
    private sealed record PendingSelection(string SelectedPath, SetupImportPreview Preview,
        DateTimeOffset PreparedUtc, long PreparedTimestamp);

    internal SetupImportPreviewResult Prepare(string kind, Guid profileId, string? selectedPath)
    {
        if (freshWorldsOnly) return Disabled();
        if (!SetupImportRequestParser.ValidKind(kind) || profileId == Guid.Empty)
            return new(false, "InvalidImportSelection", "Choose a supported game and server first.");
        if (selectedPath is null) return new(false, "Canceled", "No source file was selected.");
        try
        {
            var source = ValidateSource(kind, selectedPath);
            var files = SourceFiles(kind, source);
            var facts = files.Select(SetupImportSourceFacts.Read).ToArray();
            var now = clock.GetUtcNow();
            lock (sync)
            {
                Prune(now);
                if (selections.Count >= MaximumEntries)
                    return new(false, "TooManyImportSelections", "There are too many pending selections. Confirm one or wait for it to expire.");
                var token = Guid.NewGuid();
                var preview = new SetupImportPreview(token, profileId, kind,
                    Path.GetFileNameWithoutExtension(source), facts,
                    SetupImportSourceFacts.TotalBytes(facts), SetupImportSourceFacts.ModifiedUtc(facts),
                    now + Lifetime);
                selections.Add(token, new(source, preview, now, clock.GetTimestamp()));
                return new(true, "ImportPreviewReady", "Review the selected source before copying. The original stays unchanged.", preview);
            }
        }
        catch (Exception ex) when (SetupImportSourceFacts.IsSourceException(ex))
        {
            return new(false, "ImportSourceUnavailable", "Choose an existing original save file with the supported extension and no filesystem links. No copy was made.");
        }
    }

    // A token is one-use and scoped to the exact game/profile. The ordinary importer remains authoritative.
    internal SetupImportResolution Resolve(Guid token, string kind, Guid profileId)
    {
        if (freshWorldsOnly)
            return new(false, "StagingFreshWorldRequired", "Development cannot import an existing world. Use separate fresh development storage.");
        PendingSelection pending;
        lock (sync)
        {
            Prune(clock.GetUtcNow());
            if (token == Guid.Empty || profileId == Guid.Empty || !SetupImportRequestParser.ValidKind(kind) ||
                !selections.TryGetValue(token, out var found) || found.Preview.ProfileId != profileId ||
                found.Preview.Kind != kind)
                return new(false, "ImportSelectionExpired", "This source selection is unavailable or expired. Browse and review it again.");
            pending = found;
            selections.Remove(token);
        }
        try
        {
            var source = ValidateSource(kind, pending.SelectedPath);
            var current = SourceFiles(kind, source).Select(SetupImportSourceFacts.Read).ToArray();
            if (current.Length != pending.Preview.SourceFiles.Count ||
                current.Where((file, index) => !SetupImportSourceFacts.SameKnownFacts(
                    pending.Preview.SourceFiles[index], file)).Any())
                return new(false, "ImportSourceChanged", "The source files changed after the preview. Browse and review them again. No copy was made.");
            return new(true, "ImportSelectionResolved", "The reviewed source is ready for the ordinary verified importer.", source);
        }
        catch (Exception ex) when (SetupImportSourceFacts.IsSourceException(ex))
        {
            return new(false, "ImportSourceUnavailable", "The selected source is unavailable or linked. Browse and review it again. No copy was made.");
        }
    }

    private string ValidateSource(string kind, string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) || selectedPath.Length > 4096 ||
            !Path.IsPathFullyQualified(selectedPath))
            throw new InvalidDataException("Invalid native selection.");
        var source = Path.GetFullPath(selectedPath);
        var extension = kind == GameKinds.Factorio ? ".zip" : ".wld";
        if (!Path.GetExtension(source).Equals(extension, StringComparison.OrdinalIgnoreCase) ||
            !ValheimSetup.ValidWorldId(Path.GetFileNameWithoutExtension(source)) ||
            AppInstance.ContainsPath(data.RootPath, source))
            throw new InvalidDataException("Unsupported or managed source.");
        SetupImportSourceFacts.EnsurePlainFile(source);
        return source;
    }

    private static string[] SourceFiles(string kind, string source)
    {
        if (kind != GameKinds.Terraria) return [source];
        var backup = source + ".bak";
        try
        {
            // The existing importer copies this fixed companion when present; preview that same inventory.
            File.GetAttributes(backup);
            SetupImportSourceFacts.EnsurePlainFile(backup);
            return [source, backup];
        }
        catch (FileNotFoundException) { return [source]; }
        catch (DirectoryNotFoundException) { return [source]; }
    }

    private void Prune(DateTimeOffset now)
    {
        var timestamp = clock.GetTimestamp();
        foreach (var entry in selections.Where(entry => now >= entry.Value.Preview.ExpiresUtc ||
                     now < entry.Value.PreparedUtc ||
                     clock.GetElapsedTime(entry.Value.PreparedTimestamp, timestamp) >= Lifetime).ToArray())
            selections.Remove(entry.Key);
    }

    private static SetupImportPreviewResult Disabled() => new(false, "StagingFreshWorldRequired",
        "Development cannot import an existing world. Use separate fresh development storage.");
}

internal static class SetupImportSourceFacts
{
    internal static bool IsSourceException(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    // Reject a linked selected file and linked ancestors without recursively scanning the source folder.
    internal static void EnsurePlainFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The source must be a plain file.");
        var parent = Path.GetDirectoryName(path);
        for (var depth = 0; parent is not null; depth++)
        {
            if (depth >= 128) throw new InvalidDataException("The source hierarchy is too deep.");
            attributes = File.GetAttributes(parent);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The source hierarchy is linked.");
            parent = Path.GetDirectoryName(parent);
        }
    }

    internal static SetupImportSourceFile Read(string path)
    {
        long? bytes = null;
        DateTimeOffset? modified = null;
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (info.Exists)
            {
                try { bytes = info.Length; }
                catch (Exception ex) when (IsSourceException(ex)) { }
                try
                {
                    var utc = info.LastWriteTimeUtc;
                    // File APIs may use 1601 as a missing timestamp. Never present it as an observed date.
                    if (utc > DateTime.FromFileTimeUtc(0)) modified = new DateTimeOffset(utc);
                }
                catch (Exception ex) when (IsSourceException(ex)) { }
            }
        }
        catch (Exception ex) when (IsSourceException(ex)) { }
        return new(Path.GetFileName(path), bytes, modified);
    }

    internal static long? TotalBytes(IReadOnlyList<SetupImportSourceFile> files)
    {
        if (files.Count == 0 || files.Any(file => file.Bytes is null or < 0)) return null;
        try { return files.Aggregate(0L, (total, file) => checked(total + file.Bytes!.Value)); }
        catch (OverflowException) { return null; }
    }

    internal static DateTimeOffset? ModifiedUtc(IReadOnlyList<SetupImportSourceFile> files) =>
        files.Count == 0 || files.Any(file => file.ModifiedUtc is null)
            ? null : files.Max(file => file.ModifiedUtc);

    internal static bool SameKnownFacts(SetupImportSourceFile expected, SetupImportSourceFile current) =>
        expected.Name == current.Name && (expected.Bytes is null || expected.Bytes == current.Bytes) &&
        (expected.ModifiedUtc is null || expected.ModifiedUtc == current.ModifiedUtc);
}

public static class SetupImportRequestParser
{
    public const int MaximumRequestBytes = 512;
    public const int MaximumPrepareRequestBytes = 128;
    internal static bool ValidKind(string? kind) => kind is GameKinds.Factorio or GameKinds.Terraria;

    public static bool TryParse(ReadOnlyMemory<byte> bytes, out SetupImportConfirmation? request)
    {
        request = null;
        if (!TryObject(bytes, MaximumRequestBytes, ["selectionId", "profileId", "kind"], out var document)) return false;
        using (document)
        {
            var root = document!.RootElement;
            var kind = root.GetProperty("kind");
            if (!TryGuid(root.GetProperty("selectionId"), out var selection) ||
                !TryGuid(root.GetProperty("profileId"), out var profile) ||
                kind.ValueKind != JsonValueKind.String || !ValidKind(kind.GetString())) return false;
            request = new(selection, profile, kind.GetString()!);
            return true;
        }
    }

    public static bool TryParsePrepare(ReadOnlyMemory<byte> bytes, out Guid profileId)
    {
        profileId = Guid.Empty;
        if (!TryObject(bytes, MaximumPrepareRequestBytes, ["profileId"], out var document)) return false;
        using (document) return TryGuid(document!.RootElement.GetProperty("profileId"), out profileId);
    }

    private static bool TryGuid(JsonElement value, out Guid id)
    {
        id = Guid.Empty;
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: 36 } text &&
            Guid.TryParseExact(text, "D", out id) && id != Guid.Empty;
    }

    private static bool TryObject(ReadOnlyMemory<byte> bytes, int maximum, string[] fields,
        out JsonDocument? document)
    {
        document = null;
        if (bytes.Length < 2 || bytes.Length > maximum) return false;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
                if (names.Length == fields.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
                    names.All(name => fields.Contains(name, StringComparer.Ordinal))) return true;
            }
            document.Dispose(); document = null;
            return false;
        }
        catch (JsonException) { document?.Dispose(); document = null; return false; }
    }
}

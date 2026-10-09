using System.Text.Json;

namespace TogetherServer;

// Strict fixed payloads for the new local routes. Duplicate and unknown properties fail closed.
public static class FeatureRequestParser
{
    public static bool TryVersion(ReadOnlyMemory<byte> bytes, out string? version)
    {
        version = null;
        if (!TryObject(bytes, 256, ["version"], out var document)) return false;
        using (document)
        {
            var value = document!.RootElement.GetProperty("version");
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String || !GameCompatibility.ValidVersion(value.GetString())) return false;
            version = value.GetString(); return true;
        }
    }
    public static bool TryBookmark(ReadOnlyMemory<byte> bytes, out UpdateBackupBookmarkRequest? request)
    {
        request = null;
        if (!TryObject(bytes, 512, ["label", "pinned"], out var document)) return false;
        using (document)
        {
            var root = document!.RootElement;
            if (root.GetProperty("label").ValueKind != JsonValueKind.String || root.GetProperty("pinned").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            request = new(root.GetProperty("label").GetString(), root.GetProperty("pinned").GetBoolean()); return true;
        }
    }
    public static bool TryNotice(ReadOnlyMemory<byte> bytes, out PinnedNoticeChange? request)
    {
        request = null;
        if (!TryObject(bytes, 16 * 1024, ["text", "expectedRevision"], out var document)) return false;
        using (document)
        {
            var root = document!.RootElement; var text = root.GetProperty("text"); var revision = root.GetProperty("expectedRevision");
            if (text.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || revision.ValueKind != JsonValueKind.Number ||
                !revision.TryGetInt64(out var number) || number is < 0 or > ServerChat.MaximumNoticeRevision) return false;
            var value = text.ValueKind == JsonValueKind.Null ? null : text.GetString();
            if (value?.Length > ServerChat.MaximumNoticeTextLength) return false;
            request = new(value, number); return true;
        }
    }
    private static bool TryObject(ReadOnlyMemory<byte> bytes, int maximum, string[] keys, out JsonDocument? document)
    {
        document = null;
        if (bytes.Length is < 2 || bytes.Length > maximum) return false;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var names = document.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
                if (names.Length == keys.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
                    names.All(name => keys.Contains(name, StringComparer.Ordinal))) return true;
            }
            document.Dispose(); document = null; return false;
        }
        catch (JsonException) { document?.Dispose(); document = null; return false; }
    }
}

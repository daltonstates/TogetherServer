using System.Text;
using System.Text.Json;

namespace TogetherServer;

internal static class SharedWorldPortableSetupReader
{
    private const int MaximumEntries = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static SharedWorldPortableSetup Capture(ServerSetupSnapshot snapshot)
    {
        if (snapshot.Version != 2 || snapshot.AddOns is null || snapshot.Files is null)
            throw new InvalidDataException("The backup lacks a current reviewed setup checkpoint.");
        var addons = snapshot.AddOns.Where(item => item.Enabled).Select(item =>
        {
            if (item.Type == "External shared pack")
                throw new InvalidDataException("An active Bedrock shared pack has no verified package identity.");
            string? id = null;
            if (item.Type is "behavior pack" or "resource pack")
            {
                var prefix = item.Type == "behavior pack" ? "behavior:" : "resource:";
                if (!item.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                    !Guid.TryParse(item.Key[prefix.Length..], out var parsed) || parsed == Guid.Empty)
                    throw new InvalidDataException("An active Bedrock pack has an invalid package ID.");
                id = parsed.ToString("D");
            }
            return new SharedWorldPortableAddOn(item.Name, item.Version,
                item.RequiredGameVersion, item.Type, id);
        }).ToArray();
        var settings = ReadSettings(snapshot);
        var setup = new SharedWorldPortableSetup(snapshot.GamePort, snapshot.Crossplay,
            snapshot.GameVersion, addons, ReadAllowlist(snapshot), snapshot.PublicListing,
            settings.MaxPlayers, settings.GameMode, settings.Difficulty, settings.AllowlistEnabled);
        if (!Valid(snapshot.Kind, setup))
            throw new InvalidDataException("Portable setup has unsupported or oversized values.");
        return setup;
    }

    internal static bool LegacyFieldsEmpty(SharedWorldPortableSetup setup, int schema) =>
        (schema != 1 || setup.GameVersion is null && setup.AddOns is null && setup.Allowlist is null) &&
        !setup.PublicListing && setup.MaxPlayers is null && setup.GameMode is null &&
        setup.Difficulty is null && setup.AllowlistEnabled is null &&
        (schema != 2 || setup.AddOns is null || setup.AddOns.All(item => item?.Id is null));

    internal static bool Valid(string game, SharedWorldPortableSetup setup, int schema = 3)
    {
        if (game is not (GameKinds.Valheim or GameKinds.MinecraftJava or
            GameKinds.MinecraftBedrock or GameKinds.Factorio or GameKinds.Terraria or GameKinds.Fixture) ||
            setup.GamePort is < 1 or > 65535 || !VersionText(setup.GameVersion) ||
            setup.AddOns is null || setup.Allowlist is null ||
            setup.AddOns.Count > 128 || setup.Allowlist.Count > 128 ||
            setup.MaxPlayers is < 0 or > 500 ||
            setup.GameMode is not null && setup.GameMode is not ("survival" or "creative" or "adventure" or "spectator") ||
            setup.Difficulty is not null && setup.Difficulty is not ("peaceful" or "easy" or "normal" or "hard"))
            return false;
        if (setup.AddOns.Any(item => item is null || !Name(item.Name, 100) ||
            !VersionText(item.Version) || !VersionText(item.RequiredGameVersion) ||
            item.Type is not ("Factorio mod" or "behavior pack" or
                "resource pack" or "External shared pack"))) return false;
        if (setup.Allowlist.Any(item => item is null || !Name(item.Name, 128) ||
            item.Id is not null && !Identifier(item.Id))) return false;
        if (setup.AddOns.Select(item => (item.Type, item.Name)).Distinct().Count() != setup.AddOns.Count ||
            setup.Allowlist.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != setup.Allowlist.Count)
            return false;
        if (game != GameKinds.Factorio && game != GameKinds.MinecraftBedrock && setup.AddOns.Count != 0 ||
            game is not (GameKinds.Valheim or GameKinds.MinecraftJava or GameKinds.MinecraftBedrock) &&
            setup.Allowlist.Count != 0) return false;
        if (schema == 3 && (game is not (GameKinds.MinecraftJava or GameKinds.MinecraftBedrock) &&
                (setup.GameMode is not null || setup.Difficulty is not null || setup.AllowlistEnabled is not null) ||
            game is GameKinds.Valheim or GameKinds.Fixture && setup.MaxPlayers is not null)) return false;
        if (schema == 3 && setup.AddOns.Any(item =>
            item.Type == "External shared pack" ||
            game == GameKinds.MinecraftBedrock &&
            (!Guid.TryParse(item.Id, out _) || item.Type is not ("behavior pack" or "resource pack")) ||
            game == GameKinds.Factorio && item.Id is not null)) return false;
        if (schema == 3 && game == GameKinds.MinecraftBedrock &&
            setup.AddOns.Select(item => (item.Type, item.Id)).Distinct().Count() != setup.AddOns.Count)
            return false;
        return true;
    }

    private static IReadOnlyList<SharedWorldPortableAllowEntry> ReadAllowlist(ServerSetupSnapshot snapshot)
    {
        if (snapshot.Kind is not (GameKinds.Valheim or GameKinds.MinecraftJava or GameKinds.MinecraftBedrock))
            return [];
        var key = snapshot.Kind == GameKinds.Valheim ? "permit-list" : "allow-list";
        var content = ReadText(snapshot, key);
        if (content is null) return [];
        if (snapshot.Kind == GameKinds.Valheim)
            return content.Split('\n').Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Select(line => new SharedWorldPortableAllowEntry(line, null)).ToArray();
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > MaximumEntries)
                throw new InvalidDataException("The player allowlist is malformed or too long.");
            var result = new List<SharedWorldPortableAllowEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("The player allowlist has an invalid entry.");
                string? id = null;
                if (snapshot.Kind == GameKinds.MinecraftJava && item.TryGetProperty("uuid", out var uuid))
                {
                    if (uuid.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(uuid.GetString(), out var parsed))
                        throw new InvalidDataException("The Java player ID is invalid.");
                    id = parsed.ToString("D");
                }
                result.Add(new(name.GetString() ?? "", id));
            }
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException("The player allowlist is malformed.", ex); }
    }

    private sealed record PortableSettings(int? MaxPlayers, string? GameMode,
        string? Difficulty, bool? AllowlistEnabled);

    private static PortableSettings ReadSettings(ServerSetupSnapshot snapshot)
    {
        var key = snapshot.Kind switch
        {
            GameKinds.MinecraftJava or GameKinds.MinecraftBedrock => "server-properties",
            GameKinds.Factorio => "factorio-settings",
            GameKinds.Terraria => "terraria-config",
            _ => null
        };
        if (key is null || ReadText(snapshot, key) is not { } content)
            return new(null, null, null, null);
        if (snapshot.Kind == GameKinds.Factorio)
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The Factorio settings checkpoint is invalid.");
                return new(root.TryGetProperty("max_players", out var count) ? ReadCount(count) : null,
                    null, null, null);
            }
            catch (JsonException ex) { throw new InvalidDataException("The Factorio settings checkpoint is invalid.", ex); }
        }
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var separator = trimmed.IndexOf('=');
            if (separator < 1) continue;
            if (!values.TryAdd(trimmed[..separator].Trim(), trimmed[(separator + 1)..].Trim()))
                throw new InvalidDataException("A reviewed setup setting is repeated.");
        }
        var maxKey = snapshot.Kind == GameKinds.Terraria ? "maxplayers" : "max-players";
        int? max = null;
        if (values.TryGetValue(maxKey, out var maxText))
        {
            if (!int.TryParse(maxText, out var parsed) || parsed is < 0 or > 500)
                throw new InvalidDataException("The reviewed player limit is invalid.");
            max = parsed;
        }
        if (snapshot.Kind == GameKinds.Terraria) return new(max, null, null, null);
        return new(max, EnumValue(values, "gamemode", "survival", "creative", "adventure", "spectator"),
            EnumValue(values, "difficulty", "peaceful", "easy", "normal", "hard"),
            BoolValue(values, snapshot.Kind == GameKinds.MinecraftJava ? "white-list" : "allow-list"));
    }

    private static int ReadCount(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count is >= 0 and <= 500
            ? count : throw new InvalidDataException("The reviewed player limit is invalid.");

    private static string? EnumValue(Dictionary<string, string> values, string key, params string[] allowed)
    {
        if (!values.TryGetValue(key, out var value)) return null;
        var canonical = value.ToLowerInvariant();
        if (int.TryParse(canonical, out var numeric) && numeric >= 0 && numeric < allowed.Length)
            canonical = allowed[numeric];
        return allowed.Contains(canonical, StringComparer.Ordinal) ? canonical :
            throw new InvalidDataException("A reviewed game setting is invalid.");
    }

    private static bool? BoolValue(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) return null;
        return bool.TryParse(value, out var parsed) ? parsed :
            throw new InvalidDataException("A reviewed allowlist setting is invalid.");
    }

    private static string? ReadText(ServerSetupSnapshot snapshot, string key)
    {
        var entry = snapshot.Files.SingleOrDefault(file => file.Key == key);
        if (entry?.Content is null) return null;
        if (entry.Content.Length > 32 * 1024)
            throw new InvalidDataException("A reviewed setup file is too large to share.");
        try { return StrictUtf8.GetString(entry.Content).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException ex)
        { throw new InvalidDataException("A reviewed setup file is not plain text.", ex); }
    }

    private static bool VersionText(string? value) =>
        value is not null && value.Length is >= 1 and <= 48 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool Name(string? value, int maximum) =>
        value is not null && value.Length >= 1 && value.Length <= maximum &&
        value[0] != ' ' && value[^1] != ' ' &&
        value.All(character => char.IsLetterOrDigit(character) || character is ' ' or '_' or '-' or '.');

    private static bool Identifier(string value) =>
        value.Length is >= 1 and <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ':');
}

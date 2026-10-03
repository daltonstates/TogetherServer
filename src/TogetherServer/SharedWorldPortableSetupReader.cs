using System.Text.Json;

namespace TogetherServer;

internal static class SharedWorldPortableSetupReader
{
    private const int MaximumEntries = 128;
    private const int MaximumAllowlistBytes = 32 * 1024;

    internal static SharedWorldPortableSetup Capture(LocalData data, ServerProfile profile)
    {
        var inventory = ServerAddOns.List(data, profile);
        if (!inventory.Ok) throw new InvalidDataException("The add-on inventory cannot be reviewed.");
        var addons = inventory.Items.Where(item => item.Enabled)
            .Select(item => new SharedWorldPortableAddOn(item.Name, item.Version,
                item.RequiredGameVersion, item.Type)).ToArray();
        var allowlist = ReadAllowlist(data, profile);
        var setup = new SharedWorldPortableSetup(profile.GamePort, profile.Crossplay,
            inventory.GameVersion, addons, allowlist);
        if (!Valid(profile.Kind, setup))
            throw new InvalidDataException("Portable setup has unsupported or oversized values.");
        return setup;
    }

    internal static bool Valid(string game, SharedWorldPortableSetup setup)
    {
        if (game is not (GameKinds.Valheim or GameKinds.MinecraftJava or
            GameKinds.MinecraftBedrock or GameKinds.Factorio or GameKinds.Terraria or GameKinds.Fixture) ||
            setup.GamePort is < 1 or > 65535 || !VersionText(setup.GameVersion) ||
            setup.AddOns is null || setup.Allowlist is null ||
            setup.AddOns.Count > MaximumEntries || setup.Allowlist.Count > MaximumEntries)
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
        return true;
    }

    private static IReadOnlyList<SharedWorldPortableAllowEntry> ReadAllowlist(LocalData data, ServerProfile profile)
    {
        var key = profile.Kind == GameKinds.Valheim ? "permit-list" : "allow-list";
        if (profile.Kind is not (GameKinds.Valheim or GameKinds.MinecraftJava or GameKinds.MinecraftBedrock))
            return [];
        var path = ServerFiles.FilePath(profile, key);
        if (path is null)
        {
            if (ServerFiles.SnapshotPaths(profile).TryGetValue(key, out var expected) && File.Exists(expected))
                throw new InvalidDataException("The player allowlist is linked or unavailable.");
            return [];
        }
        if (new FileInfo(path).Length > MaximumAllowlistBytes)
            throw new InvalidDataException("The player allowlist is too large to share.");
        var read = ServerFiles.Read(data, profile, key);
        if (!read.Ok || read.Content is null)
            throw new InvalidDataException("The player allowlist cannot be reviewed.");
        if (profile.Kind == GameKinds.Valheim)
            return read.Content.Split('\n').Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Select(line => new SharedWorldPortableAllowEntry(line, null)).ToArray();
        try
        {
            using var document = JsonDocument.Parse(read.Content);
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
                if (profile.Kind == GameKinds.MinecraftJava && item.TryGetProperty("uuid", out var uuid))
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

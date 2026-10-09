using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TogetherServer;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameSettingsValues(string Difficulty, int MaximumPlayers, string GameMode,
    bool AllowListEnabled, bool ForceGameMode);
public sealed record GameAccessListSummary(string Key, string Label, bool Available);
public sealed record GameSettingsView(bool Ok, string Code, string Message, string Kind,
    string? Sha256, GameSettingsValues? Settings, bool CanUndo,
    IReadOnlyList<GameAccessListSummary> Lists);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameSettingsChangeRequest(string ExpectedSha256, GameSettingsValues Settings);
public sealed record GameSettingChange(string Key, string Label, string? Before, string? After);
public sealed record GameSettingsPreview(bool Ok, string Code, string Message, string Key,
    string? ExpectedSha256, string? ProposedSha256, IReadOnlyList<GameSettingChange> Changes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameAccessEntry(string? Identity, string? Name, bool? IgnoresPlayerLimit);
public sealed record GameAccessListView(bool Ok, string Code, string Message, string Kind,
    string Key, string Label, string? Sha256, IReadOnlyList<GameAccessEntry> Entries, bool CanUndo);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameAccessListChangeRequest(string ExpectedSha256, IReadOnlyList<GameAccessEntry> Entries);

// All request decoders also reject duplicate properties. System.Text.Json's normal
// record binding alone would accept an extra or repeated setting from a local caller.
public static class GameSettingsRequestParser
{
    public const int MaximumSettingsRequestBytes = 2048;
    public const int MaximumListRequestBytes = 64 * 1024;
    public const int MaximumUndoRequestBytes = 256;

    public static bool TryParseSettings(ReadOnlyMemory<byte> bytes,
        out GameSettingsChangeRequest? request)
    {
        request = null;
        if (bytes.Length is 0 or > MaximumSettingsRequestBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
            var root = document.RootElement;
            if (!ExactObject(root, "expectedSha256", "settings") ||
                !Hash(root.GetProperty("expectedSha256"), out var hash)) return false;
            var settings = root.GetProperty("settings");
            if (!ExactObject(settings, "difficulty", "maximumPlayers", "gameMode", "allowListEnabled", "forceGameMode") ||
                !Text(settings.GetProperty("difficulty"), 16, out var difficulty) ||
                !Text(settings.GetProperty("gameMode"), 16, out var gameMode) ||
                difficulty is not ("peaceful" or "easy" or "normal" or "hard") ||
                gameMode is not ("survival" or "creative" or "adventure" or "spectator") ||
                !settings.GetProperty("maximumPlayers").TryGetInt32Safe(out var maximum) ||
                maximum is < 1 or > GameSettings.MaximumPlayers ||
                !Flag(settings.GetProperty("allowListEnabled"), out var allowList) ||
                !Flag(settings.GetProperty("forceGameMode"), out var force)) return false;
            request = new(hash!, new(difficulty!, maximum, gameMode!, allowList, force));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    public static bool TryParseList(ReadOnlyMemory<byte> bytes, out GameAccessListChangeRequest? request)
    {
        request = null;
        if (bytes.Length is 0 or > MaximumListRequestBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
            var root = document.RootElement;
            if (!ExactObject(root, "expectedSha256", "entries") ||
                !Hash(root.GetProperty("expectedSha256"), out var hash)) return false;
            var source = root.GetProperty("entries");
            if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() > GameSettings.MaximumEntries)
                return false;
            var entries = new List<GameAccessEntry>();
            foreach (var item in source.EnumerateArray())
            {
                if (!ExactObject(item, "identity", "name", "ignoresPlayerLimit") ||
                    !NullableText(item.GetProperty("identity"), 128, out var identity) ||
                    !NullableText(item.GetProperty("name"), 32, out var name)) return false;
                var limitValue = item.GetProperty("ignoresPlayerLimit");
                bool? limit = null;
                if (limitValue.ValueKind != JsonValueKind.Null)
                {
                    if (!Flag(limitValue, out var parsed)) return false;
                    limit = parsed;
                }
                entries.Add(new(identity, name, limit));
            }
            request = new(hash!, entries);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    public static bool TryParseUndo(ReadOnlyMemory<byte> bytes, out ServerFileUndoRequest? request)
    {
        request = null;
        if (bytes.Length is 0 or > MaximumUndoRequestBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 2 });
            if (!ExactObject(document.RootElement, "expectedSha256") ||
                !Hash(document.RootElement.GetProperty("expectedSha256"), out var hash)) return false;
            request = new(hash!);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    internal static bool ExactObject(JsonElement source, params string[] fields)
    {
        if (source.ValueKind != JsonValueKind.Object) return false;
        var present = new HashSet<string>(StringComparer.Ordinal);
        return source.EnumerateObject().All(property => fields.Contains(property.Name, StringComparer.Ordinal) &&
            present.Add(property.Name)) && present.Count == fields.Length;
    }

    internal static bool Hash(JsonElement value, out string? hash) =>
        Text(value, 64, out hash) && GameSettings.IsHash(hash);
    internal static bool Text(JsonElement value, int maximum, out string? text)
    {
        text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text is not null && text.Length <= maximum;
    }
    internal static bool NullableText(JsonElement value, int maximum, out string? text)
    {
        text = null;
        return value.ValueKind == JsonValueKind.Null || Text(value, maximum, out text);
    }
    private static bool Flag(JsonElement value, out bool flag)
    {
        flag = value.ValueKind == JsonValueKind.True;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
    private static bool TryGetInt32Safe(this JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }
}

internal static class GameSettings
{
    internal const int MaximumPlayers = 200;
    internal const int MaximumEntries = 128;
    private const string PropertiesKey = "server-properties";
    private static readonly string[] Difficulties = ["peaceful", "easy", "normal", "hard"];
    private static readonly string[] Modes = ["survival", "creative", "adventure", "spectator"];
    private sealed record Line(string Text, string Ending);
    private sealed record Property(int Line, string Key, string Value, string Prefix, string Suffix);
    private sealed record ParsedList(IReadOnlyList<GameAccessEntry> Entries,
        IReadOnlyList<string> RawEntries, IReadOnlyList<Line>? Lines);
    internal sealed record Proposal(GameSettingsPreview Preview, string? Content = null);

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static bool SupportsProperties(string kind) => kind is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock;
    internal static bool SupportsList(string kind, string key) => kind switch
    {
        GameKinds.MinecraftJava or GameKinds.MinecraftBedrock => key == "allow-list",
        GameKinds.Valheim => key is "admin-list" or "ban-list" or "permit-list",
        _ => false
    };

    internal static string ListLabel(string key) => key switch
    {
        "allow-list" => "Allowed players",
        "admin-list" => "Administrators",
        "ban-list" => "Banned players",
        "permit-list" => "Permitted players",
        _ => "Access list"
    };

    internal static GameSettingsView Read(LocalData data, ServerProfile profile)
    {
        var lists = ServerFiles.List(data, profile).Files.Where(file => SupportsList(profile.Kind, file.Key))
            .Select(file => new GameAccessListSummary(file.Key, ListLabel(file.Key), file.Available)).ToList();
        if (!SupportsProperties(profile.Kind))
            return new(profile.Kind == GameKinds.Valheim, profile.Kind == GameKinds.Valheim ? "GameSettingsReady" :
                "GameSettingsUnsupported", profile.Kind == GameKinds.Valheim ? "Manage this server's access lists below." :
                "This game has no reviewed simple settings. Use its saved setup or reviewed file editor.",
                profile.Kind, null, null, false, lists);
        var file = ServerFiles.Read(data, profile, PropertiesKey);
        if (!file.Ok || file.Content is null)
            return new(false, file.Code, file.Message, profile.Kind, null, null, false, lists);
        if (!TryProperties(profile, file.Content, out _, out _, out var values, out var error))
            return new(false, "SettingsNeedFileReview", error!, profile.Kind, null, null, false, lists);
        return new(true, "GameSettingsReady", "These settings apply after the next server Start.",
            profile.Kind, file.Sha256, values, file.CanUndo, lists);
    }

    internal static Proposal ProposeSettings(ServerProfile profile, ServerFileContentResult file,
        GameSettingsChangeRequest request)
    {
        if (!SupportsProperties(profile.Kind)) return Failed(PropertiesKey, "GameSettingsUnsupported",
            "This game has no reviewed property settings.");
        if (request is null || request.Settings is null || !IsHash(request.ExpectedSha256) ||
            !ValidValues(profile.Kind, request.Settings)) return Failed(PropertiesKey, "InvalidSettings",
                "Choose a supported difficulty and game mode, and 1 to 200 maximum players.");
        if (!file.Ok || file.Content is null || !string.Equals(file.Sha256, request.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase)) return Failed(PropertiesKey, "FileChanged",
                    "The settings file changed or is unavailable. Reload before reviewing changes.");
        if (!TryProperties(profile, file.Content, out var lines, out var properties, out var before, out var error))
            return Failed(PropertiesKey, "SettingsNeedFileReview", error!);
        var desired = request.Settings;
        var changes = new List<GameSettingChange>();
        var replacements = new Dictionary<int, string>();
        var additions = new List<string>();
        void Change(string key, string label, bool changed, string value)
        {
            if (!changed) return;
            if (properties.TryGetValue(key, out var existing))
            {
                var after = existing.Prefix + value + existing.Suffix;
                replacements[existing.Line] = after;
                changes.Add(new(key, label, lines[existing.Line].Text, after));
            }
            else
            {
                var after = key + "=" + value;
                additions.Add(after);
                changes.Add(new(key, label, null, after));
            }
        }
        Change("difficulty", "Difficulty", before!.Difficulty != desired.Difficulty, desired.Difficulty);
        Change("max-players", "Maximum players", before.MaximumPlayers != desired.MaximumPlayers,
            desired.MaximumPlayers.ToString(CultureInfo.InvariantCulture));
        Change("gamemode", "Game mode", before.GameMode != desired.GameMode, desired.GameMode);
        Change(profile.Kind == GameKinds.MinecraftJava ? "white-list" : "allow-list", "Require allowed players",
            before.AllowListEnabled != desired.AllowListEnabled, desired.AllowListEnabled ? "true" : "false");
        Change("force-gamemode", "Apply game mode when players join", before.ForceGameMode != desired.ForceGameMode,
            desired.ForceGameMode ? "true" : "false");
        var content = new StringBuilder();
        foreach (var (line, index) in lines.Select((line, index) => (line, index)))
            content.Append(replacements.GetValueOrDefault(index, line.Text)).Append(line.Ending);
        var newline = lines.Select(line => line.Ending).FirstOrDefault(ending => ending.Length > 0) ?? "\n";
        foreach (var addition in additions)
        {
            if (content.Length > 0 && content[^1] is not '\n' and not '\r') content.Append(newline);
            content.Append(addition).Append(newline);
        }
        return CheckedProposal(profile, PropertiesKey, file, content.ToString(), changes);
    }

    internal static GameAccessListView ReadList(LocalData data, ServerProfile profile, string key)
    {
        if (!SupportsList(profile.Kind, key)) return new(false, "AccessListUnsupported",
            "Choose one of this game's reviewed access lists.", profile.Kind, key, ListLabel(key), null, [], false);
        var file = ServerFiles.Read(data, profile, key);
        if (!file.Ok || file.Content is null) return new(false, file.Code, file.Message,
            profile.Kind, key, ListLabel(key), null, [], false);
        if (!TryList(profile.Kind, file.Content, out var parsed)) return new(false, "ListNeedFileReview",
            "This list contains an unsupported entry, repeated identity, or more than 128 players. Use the file editor to review it.",
            profile.Kind, key, ListLabel(key), null, [], false);
        return new(true, "AccessListReady", "List changes apply after the next server Start.", profile.Kind,
            key, ListLabel(key), file.Sha256, parsed!.Entries, file.CanUndo);
    }

    internal static Proposal ProposeList(ServerProfile profile, string key, ServerFileContentResult file,
        GameAccessListChangeRequest request)
    {
        if (!SupportsList(profile.Kind, key)) return Failed(key, "AccessListUnsupported",
            "Choose one of this game's reviewed access lists.");
        if (request is null || !IsHash(request.ExpectedSha256) || !ValidEntries(profile.Kind, request.Entries))
            return Failed(key, "InvalidAccessList", "Use at most 128 distinct, valid players for this game.");
        if (!file.Ok || file.Content is null || !string.Equals(file.Sha256, request.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase)) return Failed(key, "FileChanged",
                    "This list changed or is unavailable. Reload before reviewing changes.");
        if (!TryList(profile.Kind, file.Content, out var parsed)) return Failed(key, "ListNeedFileReview",
            "Review this list in the file editor before using simple controls.");
        var entries = request.Entries;
        if (parsed!.Entries.Count == entries.Count && parsed.Entries.All(entries.Contains))
            return CheckedProposal(profile, key, file, file.Content, []);
        var changes = new List<GameSettingChange>();
        foreach (var entry in parsed.Entries.Where(entry => !entries.Contains(entry)))
            changes.Add(new(key, "Remove player", DisplayEntry(profile.Kind, entry), null));
        foreach (var entry in entries.Where(entry => !parsed.Entries.Contains(entry)))
            changes.Add(new(key, "Add or update player", null, DisplayEntry(profile.Kind, entry)));
        string content;
        if (profile.Kind == GameKinds.Valheim)
        {
            var lines = parsed.Lines!;
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                var identity = line.Text.Trim();
                if (identity.Length == 0 || identity.StartsWith('#') || identity.StartsWith("//", StringComparison.Ordinal) ||
                    entries.Any(entry => entry.Identity == identity)) builder.Append(line.Text).Append(line.Ending);
            }
            var newline = lines.Select(line => line.Ending).FirstOrDefault(ending => ending.Length > 0) ?? "\n";
            foreach (var entry in entries.Where(entry => !parsed.Entries.Contains(entry)))
            {
                if (builder.Length > 0 && builder[^1] is not '\n' and not '\r') builder.Append(newline);
                builder.Append(entry.Identity).Append(newline);
            }
            content = builder.ToString();
        }
        else
        {
            var records = entries.Select(entry =>
            {
                var index = parsed.Entries.ToList().IndexOf(entry);
                return index >= 0 ? parsed.RawEntries[index] : DisplayEntry(profile.Kind, entry);
            });
            var newline = file.Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            content = "[" + newline + string.Join("," + newline, records.Select(record => "  " + record)) + newline + "]" + newline;
        }
        return CheckedProposal(profile, key, file, content, changes);
    }

    private static Proposal CheckedProposal(ServerProfile profile, string key, ServerFileContentResult file,
        string content, IReadOnlyList<GameSettingChange> changes)
    {
        var validation = ServerFiles.ValidateChange(profile, key, new(file.Sha256!, content));
        if (!validation.Ok) return Failed(key, validation.Code,
            "The reviewed file checks rejected these changes. Use the file editor to review the configuration.");
        var beforeBytes = Encoding.UTF8.GetBytes(file.Content!);
        var sourceHasBom = !Convert.ToHexString(SHA256.HashData(beforeBytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
        var proposedBytes = Encoding.UTF8.GetBytes(content);
        if (sourceHasBom) proposedBytes = [.. Encoding.UTF8.Preamble, .. proposedBytes];
        return new(new(true, changes.Count == 0 ? "NoChanges" : "SettingsPreviewReady",
            changes.Count == 0 ? "There are no changes to save." :
                "Review these exact changes. Saving requires maintenance, Offline status, and a checkpoint.",
            key, file.Sha256, Convert.ToHexString(SHA256.HashData(proposedBytes)), changes), content);
    }

    private static Proposal Failed(string key, string code, string message) =>
        new(new(false, code, message, key, null, null, []));

    private static bool ValidValues(string kind, GameSettingsValues values) =>
        Difficulties.Contains(values.Difficulty, StringComparer.Ordinal) &&
        Modes.Take(kind == GameKinds.MinecraftJava ? 4 : 3).Contains(values.GameMode, StringComparer.Ordinal) &&
        values.MaximumPlayers is >= 1 and <= MaximumPlayers;

    private static bool TryProperties(ServerProfile profile, string content, out IReadOnlyList<Line> lines,
        out Dictionary<string, Property> properties, out GameSettingsValues? values, out string? error)
    {
        lines = SplitLines(content);
        properties = new(StringComparer.Ordinal);
        values = null;
        error = "Repeated, escaped, continued, or invalid settings need review in the file editor first.";
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reviewed = new HashSet<string>(["difficulty", "max-players", "gamemode", "force-gamemode",
            "white-list", "allow-list", "level-name", "server-port", "server-portv6"], StringComparer.OrdinalIgnoreCase);
        var java = profile.Kind == GameKinds.MinecraftJava;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].Text;
            // Java Properties whitespace is exactly space, tab and form feed.
            // Unicode whitespace can be part of a different key or its value.
            var trimmed = java ? line.TrimStart(' ', '\t', '\f') : line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!') continue;
            var firstLine = index;
            var logical = trimmed;
            while (java && HasContinuation(logical))
            {
                if (++index >= lines.Count) return false;
                logical = logical[..^1] + lines[index].Text.TrimStart(' ', '\t', '\f');
            }
            var match = Regex.Match(logical, java
                ? @"^(?<key>(?:\\.|[^ \t\f=:])+)(?<separator>[ \t\f]*(?:[=:][ \t\f]*|[ \t\f]+))(?<value>.*)$"
                : @"^(?<key>(?:\\.|[^\s=:])+)(?<separator>\s*(?:[=:]\s*|\s+))(?<value>.*)$",
                RegexOptions.CultureInvariant);
            // A delimiter-free Java property has an empty value; account for its
            // semantic key so an escaped duplicate cannot evade the preview.
            var rawKey = match.Success ? match.Groups["key"].Value : logical;
            if (!TryDecodeKey(rawKey, java, out var key) || !keys.Add(key))
                return false;
            // The inherited raw editor's validation trims Unicode whitespace.
            // Refuse a lookalike reviewed key instead of editing Java's ignored
            // key or adding a key that the inherited guard could conflate with it.
            if (java && key != key.Trim() && reviewed.Contains(key.Trim())) return false;
            if (!reviewed.Contains(key)) continue;
            if (!match.Success || firstLine != index || rawKey != key ||
                key != key.ToLowerInvariant() || line.Length > 512 ||
                !match.Groups["separator"].Value.Contains('=') ||
                match.Groups["separator"].Value.Contains(':')) return false;
            var rawValue = match.Groups["value"].Value;
            // Java keeps every remaining character, including trailing spaces.
            // Do not project a trimmed enum/bool and then preserve an ineffective
            // suffix on Save. The raw file editor can resolve this layout.
            if (java && rawValue != rawValue.Trim()) return false;
            var value = java ? rawValue : rawValue.Trim();
            if (value.Contains('\\') || value.Length > 128) return false;
            var valueOffset = line.Length - rawValue.Length;
            var leading = java ? 0 : rawValue.Length - rawValue.TrimStart().Length;
            var trailing = java ? 0 : rawValue.Length - rawValue.TrimEnd().Length;
            properties[key] = new(firstLine, key, value, line[..(valueOffset + leading)],
                trailing == 0 ? "" : rawValue[^trailing..]);
        }
        if (!properties.TryGetValue("level-name", out var world) || world.Value != profile.WorldId ||
            !properties.TryGetValue("server-port", out var port) ||
            !int.TryParse(port.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number != profile.GamePort)
        {
            error = "The file's world and game port must match the saved setup. Review them in the file editor.";
            return false;
        }
        var parsedProperties = properties;
        string Value(string key, string fallback) => parsedProperties.TryGetValue(key, out var property) ? property.Value : fallback;
        var difficulty = Value("difficulty", "easy");
        var gameMode = Value("gamemode", "survival");
        if (profile.Kind == GameKinds.MinecraftJava)
        {
            if (int.TryParse(difficulty, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric is >= 0 and < 4)
                difficulty = Difficulties[numeric];
            if (int.TryParse(gameMode, NumberStyles.None, CultureInfo.InvariantCulture, out numeric) && numeric is >= 0 and < 4)
                gameMode = Modes[numeric];
        }
        if (!int.TryParse(Value("max-players", profile.Kind == GameKinds.MinecraftJava ? "20" : "10"),
                NumberStyles.None, CultureInfo.InvariantCulture, out var maximum) ||
            !bool.TryParse(Value(profile.Kind == GameKinds.MinecraftJava ? "white-list" : "allow-list", "false"), out var allowed) ||
            !bool.TryParse(Value("force-gamemode", "false"), out var force)) return false;
        values = new(difficulty, maximum, gameMode, allowed, force);
        return ValidValues(profile.Kind, values);
    }

    private static bool HasContinuation(string value)
    {
        var count = 0;
        for (var index = value.Length - 1; index >= 0 && value[index] == '\\'; index--) count++;
        return count % 2 != 0;
    }

    private static bool TryDecodeKey(string raw, bool java, out string key)
    {
        if (!java) { key = raw; return true; }
        var output = new StringBuilder();
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character != '\\') { output.Append(character); continue; }
            if (++index >= raw.Length) { key = ""; return false; }
            character = raw[index];
            if (character == 'u')
            {
                if (index + 4 >= raw.Length || !ushort.TryParse(raw.AsSpan(index + 1, 4),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unicode)) { key = ""; return false; }
                output.Append((char)unicode);
                index += 4;
            }
            else output.Append(character switch { 't' => '\t', 'r' => '\r', 'n' => '\n', 'f' => '\f', _ => character });
        }
        key = output.ToString();
        return true;
    }

    private static IReadOnlyList<Line> SplitLines(string content)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] is not '\n' and not '\r') continue;
            var end = index;
            if (content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
            lines.Add(new(content[start..end], content[end..(index + 1)]));
            start = index + 1;
        }
        if (start < content.Length) lines.Add(new(content[start..], ""));
        return lines;
    }

    private static bool TryList(string kind, string content, out ParsedList? parsed)
    {
        parsed = null;
        if (kind == GameKinds.Valheim)
        {
            var lines = SplitLines(content);
            var entries = lines.Select(line => line.Text.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#') &&
                    !line.StartsWith("//", StringComparison.Ordinal))
                .Select(identity => new GameAccessEntry(identity, null, null)).ToList();
            if (!ValidEntries(kind, entries)) return false;
            parsed = new(entries, [], lines);
            return true;
        }
        try
        {
            using var document = JsonDocument.Parse(content, new() { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > MaximumEntries) return false;
            var entries = new List<GameAccessEntry>();
            var raw = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (kind == GameKinds.MinecraftJava)
                {
                    if (!GameSettingsRequestParser.ExactObject(item, "uuid", "name") ||
                        !GameSettingsRequestParser.Text(item.GetProperty("uuid"), 36, out var identity) ||
                        !GameSettingsRequestParser.Text(item.GetProperty("name"), 16, out var name)) return false;
                    entries.Add(new(identity, name, null));
                }
                else
                {
                    if (item.ValueKind != JsonValueKind.Object) return false;
                    var fields = item.EnumerateObject().Select(property => property.Name).ToArray();
                    if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length ||
                        fields.Any(field => field is not ("name" or "xuid" or "ignoresPlayerLimit")) ||
                        !item.TryGetProperty("name", out var nameValue) ||
                        !GameSettingsRequestParser.Text(nameValue, 32, out var name)) return false;
                    string? identity = null;
                    if (item.TryGetProperty("xuid", out var xuid) &&
                        !GameSettingsRequestParser.Text(xuid, 20, out identity)) return false;
                    var ignores = false;
                    if (item.TryGetProperty("ignoresPlayerLimit", out var limit))
                    {
                        if (limit.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                        ignores = limit.GetBoolean();
                    }
                    entries.Add(new(identity, name, ignores));
                }
                raw.Add(item.GetRawText());
            }
            if (!ValidEntries(kind, entries)) return false;
            parsed = new(entries, raw, null);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    internal static bool ValidEntries(string kind, IReadOnlyList<GameAccessEntry>? entries)
    {
        if (entries is null || entries.Count > MaximumEntries) return false;
        var identities = new HashSet<string>(kind == GameKinds.Valheim ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null) return false;
            if (kind == GameKinds.Valheim)
            {
                if (entry.Name is not null || entry.IgnoresPlayerLimit is not null || entry.Identity is null ||
                    !Regex.IsMatch(entry.Identity, @"\A[A-Za-z][A-Za-z0-9]{0,19}_[A-Za-z0-9-]{1,100}\z", RegexOptions.CultureInvariant) ||
                    !identities.Add(entry.Identity)) return false;
            }
            else if (kind == GameKinds.MinecraftJava)
            {
                if (entry.IgnoresPlayerLimit is not null || entry.Identity is null ||
                    !Guid.TryParseExact(entry.Identity, "D", out var uuid) || uuid == Guid.Empty ||
                    entry.Name is null || !Regex.IsMatch(entry.Name, @"\A[A-Za-z0-9_]{1,16}\z", RegexOptions.CultureInvariant) ||
                    !identities.Add(entry.Identity) || !names.Add(entry.Name)) return false;
            }
            else if (kind == GameKinds.MinecraftBedrock)
            {
                if (entry.Name is null || entry.Name.Length is < 1 or > 32 || entry.Name.Trim() != entry.Name ||
                    !WellFormed(entry.Name) ||
                    entry.Name.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format) ||
                    entry.IgnoresPlayerLimit is null || !names.Add(entry.Name) ||
                    entry.Identity is not null && (!Regex.IsMatch(entry.Identity, @"\A[0-9]{1,20}\z", RegexOptions.CultureInvariant) ||
                        !identities.Add(entry.Identity))) return false;
            }
            else return false;
        }
        return true;
    }

    private static bool WellFormed(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }

    private static string DisplayEntry(string kind, GameAccessEntry entry)
    {
        if (kind == GameKinds.Valheim) return entry.Identity!;
        if (kind == GameKinds.MinecraftJava) return JsonSerializer.Serialize(new { uuid = entry.Identity, name = entry.Name });
        var fields = new Dictionary<string, object?> { ["name"] = entry.Name, ["ignoresPlayerLimit"] = entry.IgnoresPlayerLimit };
        if (entry.Identity is not null) fields["xuid"] = entry.Identity;
        return JsonSerializer.Serialize(fields);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record ServerFileLocation(string Key, string Label, string Path, bool Available);
public sealed record ServerFileEntry(string Key, string Label, string Path, bool Available);
public sealed record ServerFilesView(bool Ok, string Code, string Message,
    IReadOnlyList<ServerFileLocation> Locations, IReadOnlyList<ServerFileEntry> Files);
public sealed record ServerFileContentResult(bool Ok, string Code, string Message, string Key,
    string? Content = null, string? Sha256 = null, bool CanUndo = false);
public sealed record ServerFileChangeRequest(string ExpectedSha256, string Content);
public sealed record ServerFileUndoRequest(string ExpectedSha256);
public sealed record ServerFileChangeResult(bool Ok, string Code, string Message, string Key,
    string? Sha256 = null, bool CanUndo = false);

internal static class ServerFiles
{
    private const int MaximumFileBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private sealed record FileDefinition(string Key, string Label, string Path);
    private sealed record PreviousVersion(Guid ProfileId, string Kind, string Key, string Path,
        byte[] Before, string AfterSha256);

    public static ServerFilesView List(LocalData data, ServerProfile profile)
    {
        var locations = new List<ServerFileLocation>();
        var serverFolder = profile.Kind is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock
            ? profile.WorldDirectory : DirectoryOf(profile.ExecutablePath);
        var saveFolder = profile.Kind switch
        {
            GameKinds.MinecraftJava => Path.Combine(profile.WorldDirectory, profile.WorldId),
            GameKinds.MinecraftBedrock => Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId),
            _ => profile.WorldDirectory
        };
        AddLocation(locations, "server", "Server app", serverFolder);
        AddLocation(locations, "save", "World and saves", saveFolder);
        AddLocation(locations, "logs", "TogetherServer logs", data.LogsRoot);
        AddLocation(locations, "backups", "World backups",
            Path.Combine(data.BackupsRoot, profile.Id.ToString("N")));
        if (profile.Kind == GameKinds.MinecraftBedrock)
        {
            AddLocation(locations, "behavior-packs", "Behavior packs",
                Path.Combine(profile.WorldDirectory, "behavior_packs"));
            AddLocation(locations, "resource-packs", "Resource packs",
                Path.Combine(profile.WorldDirectory, "resource_packs"));
            AddLocation(locations, "world-behavior-packs", "This world's behavior packs",
                Path.Combine(saveFolder, "behavior_packs"));
            AddLocation(locations, "world-resource-packs", "This world's resource packs",
                Path.Combine(saveFolder, "resource_packs"));
        }
        if (profile.Kind == GameKinds.Factorio)
            AddLocation(locations, "mods", "Managed mods", Path.Combine(profile.WorldDirectory, "mods"));
        var files = Definitions(profile).Select(file => new ServerFileEntry(file.Key, file.Label,
            file.Path, IsPlainFile(file.Path))).ToList();
        return new(true, "ServerFilesReady", "These are the files used by this saved server.",
            locations, files);
    }

    public static string? Location(LocalData data, ServerProfile profile, string key) =>
        List(data, profile).Locations.SingleOrDefault(location =>
            location.Key == key && location.Available)?.Path;

    public static string? FilePath(ServerProfile profile, string key)
    {
        var file = Definition(profile, key);
        return file is not null && IsPlainFile(file.Path) ? file.Path : null;
    }

    internal static IReadOnlyDictionary<string, string> SnapshotPaths(ServerProfile profile) =>
        Definitions(profile).ToDictionary(item => item.Key, item => item.Path, StringComparer.Ordinal);

    public static string? CheckActiveConfiguration(LocalData data, ServerProfile profile, string key)
    {
        var file = Definition(profile, key);
        if (file is null || !File.Exists(file.Path)) return null;
        var read = Read(data, profile, key);
        if (!read.Ok || read.Content is null) return read.Message;
        if (key == "server-properties") return ValidateMinecraftProperties(profile, read.Content);
        return ValidateStructuredFile(profile, key, read.Content);
    }

    public static ServerFileContentResult Read(LocalData data, ServerProfile profile, string key)
    {
        var file = Definition(profile, key);
        if (file is null) return new(false, "FileUnsupported", "This server has no editable file by that name.", key);
        try
        {
            var bytes = ReadBytes(file.Path);
            var content = Decode(bytes);
            var sha = Sha(bytes);
            return new(true, "ServerFileReady", "Review the file before making changes.", key, content, sha,
                CanUndo(data, profile, file, sha));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            return new(false, "FileUnavailable",
                "This file is missing, linked, too large, or not plain UTF-8 text. Open its folder to inspect it locally.", key);
        }
    }

    public static ServerFileChangeResult ValidateChange(ServerProfile profile, string key,
        ServerFileChangeRequest request)
    {
        if (Definition(profile, key) is null)
            return new(false, "FileUnsupported", "This server has no editable file by that name.", key);
        if (string.IsNullOrWhiteSpace(request.ExpectedSha256) || request.Content is null)
            return new(false, "InvalidEdit", "Reload the file before saving changes.", key);
        try
        {
            var bytes = StrictUtf8.GetBytes(request.Content);
            if (bytes.Length > MaximumFileBytes || request.Content.Contains('\0') ||
                request.Content.Contains('\uFEFF') ||
                request.Content.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
                return new(false, "InvalidEdit", "Use plain UTF-8 text no larger than 256 KB.", key);
            if (key == "server-properties" && ValidateMinecraftProperties(profile, request.Content) is { } issue)
                return new(false, "InvalidConfiguration", issue, key);
            if (ValidateStructuredFile(profile, key, request.Content) is { } structuredIssue)
                return new(false, "InvalidConfiguration", structuredIssue, key);
            return new(true, "EditReady", "The proposed file passed local checks.", key);
        }
        catch (EncoderFallbackException)
        {
            return new(false, "InvalidEdit", "The file contains text that cannot be saved as UTF-8.", key);
        }
    }

    public static ServerFileChangeResult Save(LocalData data, ServerProfile profile, string key,
        ServerFileChangeRequest request)
    {
        var validation = ValidateChange(profile, key, request);
        if (!validation.Ok) return validation;
        var file = Definition(profile, key)!;
        try
        {
            var before = ReadBytes(file.Path);
            if (!Sha(before).Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return new(false, "FileChanged", "The file changed on disk. Reload it before saving.", key);
            var encoded = StrictUtf8.GetBytes(request.Content);
            var withBom = before.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var after = withBom ? [.. Encoding.UTF8.Preamble, .. encoded] : encoded;
            if (after.Length > MaximumFileBytes)
                return new(false, "InvalidEdit", "Use plain UTF-8 text no larger than 256 KB.", key);
            if (after.AsSpan().SequenceEqual(before))
                return new(true, "NoChanges", "The file already matches your draft.", key, Sha(before),
                    CanUndo(data, profile, file, Sha(before)));
            var afterSha = Sha(after);
            var revisionName = RevisionName(profile.Id, key);
            var earlierRevision = data.LoadProtected(revisionName);
            data.SaveProtected(revisionName, JsonSerializer.SerializeToUtf8Bytes(
                new PreviousVersion(profile.Id, profile.Kind, key, file.Path, before, afterSha)));
            try { Replace(file.Path, after, Sha(before)); }
            catch
            {
                if (earlierRevision is null) data.DeleteProtected(revisionName);
                else data.SaveProtected(revisionName, earlierRevision);
                throw;
            }
            return new(true, "FileSaved", "File saved after an offline world checkpoint. Start the server and check the game.",
                key, afterSha, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   CryptographicException or ArgumentException or NotSupportedException)
        {
            return new(false, "FileSaveFailed", "The file could not be saved. Its existing content was kept.", key);
        }
    }

    public static ServerFileChangeResult Undo(LocalData data, ServerProfile profile, string key,
        ServerFileUndoRequest request)
    {
        var file = Definition(profile, key);
        if (file is null) return new(false, "FileUnsupported", "This server has no editable file by that name.", key);
        try
        {
            var current = ReadBytes(file.Path);
            var sha = Sha(current);
            if (!sha.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return new(false, "FileChanged", "The file changed on disk. Reload it before undoing.", key);
            var revision = LoadRevision(data, profile, file);
            if (revision is null || !revision.AfterSha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
                return new(false, "UndoUnavailable", "There is no matching saved version to restore.", key);
            Replace(file.Path, revision.Before, sha);
            data.DeleteProtected(RevisionName(profile.Id, key));
            return new(true, "FileRestored", "The previous file version was restored after an offline world checkpoint.",
                key, Sha(revision.Before));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   CryptographicException or ArgumentException or NotSupportedException)
        {
            return new(false, "UndoFailed", "The previous file version could not be restored.", key);
        }
    }

    public static ServerFileChangeResult CreateConfiguration(LocalData data, ServerProfile profile, string key)
    {
        if (key is not ("factorio-settings" or "terraria-config") || Definition(profile, key) is not { } file)
            return new(false, "FileUnsupported", "This server has no creatable configuration file by that name.", key);
        if (profile.Kind == GameKinds.Factorio && !FactorioSetup.IsImportedCopy(data, profile) ||
            profile.Kind == GameKinds.Terraria && !TerrariaSetup.IsImportedCopy(data, profile))
            return new(false, "ManagedSaveRequired", "Create the managed save before adding a configuration file.", key);
        try
        {
            if (!IsPlainDirectory(profile.WorldDirectory) || File.Exists(file.Path))
                return new(false, "FileExists", "Reload the existing configuration file.", key);
            var content = key == "factorio-settings"
                ? JsonSerializer.Serialize(new
                {
                    name = profile.ServerName,
                    description = "",
                    tags = Array.Empty<string>(),
                    max_players = 0,
                    visibility = new { @public = false, lan = false },
                    require_user_verification = true
                }, new JsonSerializerOptions { WriteIndented = true }) + "\n"
                : $"world={Path.Combine(profile.WorldDirectory, profile.WorldId + ".wld")}\nport={profile.GamePort}\nmaxplayers=8\nupnp=0\n";
            if (ValidateStructuredFile(profile, key, content) is { } issue)
                return new(false, "InvalidConfiguration", issue, key);
            var bytes = StrictUtf8.GetBytes(content);
            using (var stream = new FileStream(file.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            return new(true, "FileCreated", "Configuration created for this server. Review it before starting.",
                key, Sha(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   ArgumentException or NotSupportedException)
        { return new(false, "FileCreateFailed", "The configuration file could not be created.", key); }
    }

    private static IEnumerable<FileDefinition> Definitions(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.WorldDirectory) ||
            !Path.IsPathFullyQualified(profile.WorldDirectory)) yield break;
        if (profile.Kind is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock)
            yield return new("server-properties", "server.properties",
                Path.Combine(profile.WorldDirectory, "server.properties"));
        if (profile.Kind == GameKinds.MinecraftJava)
        {
            yield return new("operators", "ops.json", Path.Combine(profile.WorldDirectory, "ops.json"));
            yield return new("allow-list", "whitelist.json", Path.Combine(profile.WorldDirectory, "whitelist.json"));
            yield return new("player-bans", "banned-players.json", Path.Combine(profile.WorldDirectory, "banned-players.json"));
            yield return new("ip-bans", "banned-ips.json", Path.Combine(profile.WorldDirectory, "banned-ips.json"));
        }
        if (profile.Kind == GameKinds.MinecraftBedrock)
        {
            yield return new("allow-list", "allowlist.json", Path.Combine(profile.WorldDirectory, "allowlist.json"));
            yield return new("permissions", "permissions.json", Path.Combine(profile.WorldDirectory, "permissions.json"));
        }
        if (profile.Kind == GameKinds.Valheim)
        {
            yield return new("admin-list", "adminlist.txt", Path.Combine(profile.WorldDirectory, "adminlist.txt"));
            yield return new("ban-list", "bannedlist.txt", Path.Combine(profile.WorldDirectory, "bannedlist.txt"));
            yield return new("permit-list", "permittedlist.txt", Path.Combine(profile.WorldDirectory, "permittedlist.txt"));
        }
        if (profile.Kind == GameKinds.Factorio)
            yield return new("factorio-settings", "server-settings.json",
                Path.Combine(profile.WorldDirectory, "server-settings.json"));
        if (profile.Kind == GameKinds.Terraria)
            yield return new("terraria-config", "serverconfig.txt",
                Path.Combine(profile.WorldDirectory, "serverconfig.txt"));
    }

    private static FileDefinition? Definition(ServerProfile profile, string key) =>
        Definitions(profile).SingleOrDefault(file => file.Key == key);

    private static void AddLocation(List<ServerFileLocation> result, string key, string label, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            result.Add(new(key, label, full, IsPlainDirectory(full)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
    }

    private static string? DirectoryOf(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static bool IsPlainDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   NotSupportedException)
        { return false; }
    }

    private static bool IsPlainFile(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                   IsPlainDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   NotSupportedException)
        { return false; }
    }

    private static byte[] ReadBytes(string path)
    {
        if (!IsPlainFile(path)) throw new InvalidDataException("The file is missing or linked.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("The file is too large.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (memory.Length > MaximumFileBytes) throw new InvalidDataException("The file is too large.");
        return memory.ToArray();
    }

    private static string Decode(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string RevisionName(Guid id, string key) => $"server-file-{id:N}-{key}.protected";

    private static PreviousVersion? LoadRevision(LocalData data, ServerProfile profile, FileDefinition file)
    {
        var protectedBytes = data.LoadProtected(RevisionName(profile.Id, file.Key));
        if (protectedBytes is null) return null;
        try
        {
            var revision = JsonSerializer.Deserialize<PreviousVersion>(protectedBytes);
            return revision is not null && revision.Before is not null && revision.AfterSha256 is not null &&
                   revision.ProfileId == profile.Id && revision.Kind == profile.Kind &&
                   revision.Key == file.Key && string.Equals(revision.Path, file.Path, StringComparison.OrdinalIgnoreCase) &&
                   revision.Before.Length <= MaximumFileBytes ? revision : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool CanUndo(LocalData data, ServerProfile profile, FileDefinition file, string sha) =>
        LoadRevision(data, profile, file) is { } revision &&
        revision.AfterSha256.Equals(sha, StringComparison.OrdinalIgnoreCase);

    private static void Replace(string path, byte[] bytes, string expectedSha256)
    {
        if (!IsPlainFile(path)) throw new InvalidDataException("The file is missing or linked.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (!IsPlainFile(path) || !Sha(ReadBytes(path)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The file changed while saving.");
            File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? ValidateMinecraftProperties(ServerProfile profile, string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var separator = trimmed.IndexOf('=');
            if (separator < 1) continue;
            var key = trimmed[..separator].Trim();
            if (!values.TryAdd(key, trimmed[(separator + 1)..].Trim()))
                return $"Remove the repeated {key} setting before saving.";
        }
        if (!values.TryGetValue("level-name", out var world) || world != profile.WorldId)
            return "level-name must match the world selected in TogetherServer.";
        if (!values.TryGetValue("server-port", out var port) ||
            !int.TryParse(port, out var number) || number != profile.GamePort)
            return "server-port must match the port selected in TogetherServer.";
        if (profile.Kind == GameKinds.MinecraftBedrock)
        {
            if (values.TryGetValue("server-portv6", out var v6) &&
                (!int.TryParse(v6, out var v6Port) || v6Port is < 1 or > 65535))
                return "server-portv6 must be a port from 1 to 65535.";
            if (values.TryGetValue("enable-lan-visibility", out var lan) &&
                !lan.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                !lan.Equals("false", StringComparison.OrdinalIgnoreCase))
                return "enable-lan-visibility must be true or false.";
        }
        return null;
    }

    private static string? ValidateStructuredFile(ServerProfile profile, string key, string content)
    {
        if (key == "terraria-config") return ValidateTerrariaConfig(profile, content);
        if (key is not ("operators" or "allow-list" or "player-bans" or "ip-bans" or
                        "permissions" or "factorio-settings")) return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (key == "factorio-settings")
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return "Factorio settings must be a JSON object.";
                if (root.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.String)
                    return "Factorio server name must be text.";
                if (root.TryGetProperty("max_players", out var maximum) &&
                    (maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetInt32(out var count) || count < 0))
                    return "Factorio max_players must be zero or more.";
                if (root.TryGetProperty("visibility", out var visibility))
                {
                    if (visibility.ValueKind != JsonValueKind.Object) return "Factorio visibility must be an object.";
                    if (visibility.TryGetProperty("public", out var publicValue) && publicValue.ValueKind == JsonValueKind.True &&
                        !profile.PublicListing) return "Enable public listing in TogetherServer before publishing this game.";
                }
                return null;
            }
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return "This player list must be a JSON array.";
            if (document.RootElement.GetArrayLength() > 512) return "This player list is too long.";
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return "Each player list entry must be an object.";
                var identity = key == "ip-bans" ? "ip" :
                    key == "permissions" ? "xuid" : "name";
                if (!item.TryGetProperty(identity, out var value) || value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 128)
                    return $"Each player list entry needs a valid {identity}.";
                if (!names.Add(value.GetString()!)) return $"Remove the repeated {identity} entry.";
                if (key == "permissions" && (!item.TryGetProperty("permission", out var permission) ||
                    permission.ValueKind != JsonValueKind.String ||
                    permission.GetString() is not ("visitor" or "member" or "operator")))
                    return "Each Bedrock permission must be visitor, member, or operator.";
                if (key == "operators" && item.TryGetProperty("level", out var level) &&
                    (level.ValueKind != JsonValueKind.Number || !level.TryGetInt32(out var number) || number is < 1 or > 4))
                    return "Java operator levels must be from 1 to 4.";
            }
            return null;
        }
        catch (JsonException) { return "Use valid JSON for this file."; }
    }

    private static string? ValidateTerrariaConfig(ServerProfile profile, string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var separator = trimmed.IndexOf('=');
            if (separator < 1) return "Use key=value lines in the Terraria configuration.";
            if (!values.TryAdd(trimmed[..separator].Trim(), trimmed[(separator + 1)..].Trim()))
                return "Remove repeated Terraria configuration keys.";
        }
        if (!values.TryGetValue("world", out var world) ||
            !world.Equals(Path.Combine(profile.WorldDirectory, profile.WorldId + ".wld"), StringComparison.OrdinalIgnoreCase))
            return "Terraria world must match this server's managed world.";
        if (!values.TryGetValue("port", out var port) || !int.TryParse(port, out var number) ||
            number != profile.GamePort) return "Terraria port must match this server's saved port.";
        if (values.ContainsKey("autocreate")) return "Terraria autocreate is disabled for managed worlds.";
        if (values.TryGetValue("upnp", out var upnp) && upnp != "0")
            return "Terraria UPnP must remain off; TogetherServer does not change router settings.";
        return null;
    }
}

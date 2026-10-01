using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TogetherServer;

public sealed record ServerAddOnItem(string Key, string Name, string Version, string RequiredGameVersion,
    bool Enabled, string Compatibility, string Type);
public sealed record ServerAddOnView(bool Ok, string Code, string Message, string GameVersion,
    string RequiredOnFriendPc, string StateToken, IReadOnlyList<ServerAddOnItem> Items,
    bool CanImport, bool CanUndo, string? Warning = null, string? ImportType = null);
public sealed record ServerAddOnChangeRequest(string ExpectedStateToken, string Key, bool Enabled);
public sealed record ServerAddOnImportRequest(string ExpectedStateToken);
public sealed record ServerAddOnUndoRequest(string ExpectedStateToken);
public sealed record ServerAddOnResult(bool Ok, string Code, string Message, ServerAddOnView View);

internal static class ServerAddOns
{
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private const long MaximumExtractedBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 2048;
    private static readonly Regex FactorioName = new("^[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant);
    private static readonly Regex FactorioVersion = new("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant);
    private sealed record Revision(Guid ProfileId, string Kind, string BeforeToken,
        byte[]? BeforeActivation, string AfterToken, string? AddedRelativePath, string? AddedSha256,
        string ActivationType = "factorio");
    private sealed record Package(string Name, string Version, string RequiredVersion, string Type,
        string? Id = null, int[]? BedrockVersion = null, string? Prefix = null);

    public static ServerAddOnView List(LocalData data, ServerProfile profile)
    {
        try
        {
            var version = GameVersion(profile);
            var items = profile.Kind switch
            {
                GameKinds.Factorio => ListFactorio(profile),
                GameKinds.MinecraftBedrock => ListBedrock(profile),
                _ => []
            };
            var token = StateToken(profile);
            var revision = LoadRevision(data, profile);
            var warning = VersionWarning(data, profile, version);
            if (items.Any(item => item.Type == "External shared pack"))
                warning = "An active shared Bedrock pack is outside this world's backup. Move it into this world before creating a complete setup checkpoint.";
            return new(true, "AddOnsReady", "Installed add-ons for this saved server.", version,
                profile.Kind == GameKinds.Factorio
                    ? "Friends need the same Factorio game version and enabled mod set."
                    : profile.Kind == GameKinds.MinecraftBedrock
                        ? "Friends need a compatible Bedrock version and any required world packs."
                        : "Use the matching game version on each PC.",
                token, items, profile.Kind == GameKinds.Factorio && FactorioSetup.IsImportedCopy(data, profile) ||
                       profile.Kind == GameKinds.MinecraftBedrock && Directory.Exists(WorldRoot(profile)),
                revision?.AfterToken == token, warning,
                profile.Kind == GameKinds.Factorio ? "FactorioMod" :
                profile.Kind == GameKinds.MinecraftBedrock ? "BedrockPack" : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or ArgumentException or NotSupportedException or
                                   InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return new(false, "AddOnsUnavailable", "The add-on folders could not be inspected safely.", "Unknown",
                "Check game and add-on versions before inviting friends.", "", [], false, false);
        }
    }

    public static string? VersionWarning(LocalData data, ServerProfile profile) =>
        VersionWarning(data, profile, GameVersion(profile));

    public static void RestoreRecordedVersion(LocalData data, ServerProfile profile, string recordedVersion)
    {
        var name = $"addon-game-version-{profile.Id:N}.protected";
        if (recordedVersion == "Unknown") data.DeleteProtected(name);
        else data.SaveProtected(name, Encoding.UTF8.GetBytes(recordedVersion));
    }

    public static ServerAddOnResult ReviewGameVersion(LocalData data, ServerProfile profile,
        string expectedToken)
    {
        var before = List(data, profile);
        if (!before.Ok || before.StateToken != expectedToken)
            return Fail(data, profile, "AddOnsChanged", "The add-on folder changed. Reload before reviewing versions.");
        if (before.GameVersion == "Unknown")
            return Fail(data, profile, "GameVersionUnknown", "The installed game's version cannot be read here.");
        if (before.Items.Any(item => item.Compatibility.StartsWith("Requires ", StringComparison.Ordinal)))
            return Fail(data, profile, "AddOnVersionMismatch", "An installed add-on requires another game version.");
        data.SaveProtected($"addon-game-version-{profile.Id:N}.protected",
            Encoding.UTF8.GetBytes(before.GameVersion));
        return new(true, "GameVersionReviewed", "Version reviewed for this add-on set. Start and check the game and a real join.",
            List(data, profile));
    }

    public static string GameVersion(ServerProfile profile)
    {
        try
        {
            // The Java executable reports the runtime version, not the selected server JAR.
            if (profile.Kind == GameKinds.MinecraftJava) return "Unknown";
            if (string.IsNullOrWhiteSpace(profile.ExecutablePath) || !File.Exists(profile.ExecutablePath))
                return "Unknown";
            var information = FileVersionInfo.GetVersionInfo(profile.ExecutablePath);
            var version = information.ProductVersion ?? information.FileVersion;
            var match = Regex.Match(version ?? "", @"\d+(?:\.\d+){1,3}");
            return match.Success ? match.Value : "Unknown";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   System.ComponentModel.Win32Exception)
        { return "Unknown"; }
    }

    public static string? ValidatePackage(ServerProfile profile, string source, out string description)
    {
        description = "";
        try
        {
            if (!Path.IsPathFullyQualified(source) || !PlainFile(source) ||
                new FileInfo(source).Length is < 1 or > MaximumPackageBytes)
                return "Choose an existing local package no larger than 128 MB.";
            if (profile.Kind == GameKinds.Factorio &&
                !Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                profile.Kind == GameKinds.MinecraftBedrock &&
                !Path.GetExtension(source).Equals(".mcpack", StringComparison.OrdinalIgnoreCase))
                return "Choose the package format for this game.";
            var package = profile.Kind == GameKinds.Factorio ? ReadFactorioPackage(source) :
                profile.Kind == GameKinds.MinecraftBedrock ? ReadBedrockPackage(source) :
                throw new InvalidDataException("Add-on imports are unsupported for this game.");
            description = $"{package.Name} {package.Version}";
            var serverVersion = GameVersion(profile);
            if (profile.Kind == GameKinds.Factorio && serverVersion != "Unknown" &&
                !serverVersion.StartsWith(package.RequiredVersion + ".", StringComparison.Ordinal))
                return $"This mod requires Factorio {package.RequiredVersion}; the selected server reports {serverVersion}.";
            if (profile.Kind == GameKinds.MinecraftBedrock && serverVersion != "Unknown" &&
                Version.TryParse(serverVersion, out var current) &&
                Version.TryParse(package.RequiredVersion, out var minimum) && current < minimum)
                return $"This pack requires Bedrock {package.RequiredVersion}; the server reports {serverVersion}.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or ArgumentException or NotSupportedException or
                                   InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return "The selected package is not a supported add-on for this game."; }
    }

    public static ServerAddOnResult ImportFactorio(LocalData data, ServerProfile profile, string source,
        string expectedToken)
    {
        var before = List(data, profile);
        if (!before.Ok || before.StateToken != expectedToken)
            return Fail(data, profile, "AddOnsChanged", "The add-on folder changed. Reload before importing.");
        if (ValidatePackage(profile, source, out _) is { } issue)
            return Fail(data, profile, "InvalidPackage", issue);
        if (AppInstance.ContainsPath(profile.WorldDirectory, source))
            return Fail(data, profile, "InvalidPackage", "Choose the original package outside this managed server.");
        Package package;
        try { package = ReadFactorioPackage(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { return Fail(data, profile, "InvalidPackage", "The selected mod changed or became unreadable."); }
        var root = Path.Combine(profile.WorldDirectory, "mods");
        var destination = Path.Combine(root, $"{package.Name}_{package.Version}.zip");
        if (before.Items.Any(item => item.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase)))
            return Fail(data, profile, "ModAlreadyInstalled", "Disable or remove the existing mod version before importing another.");
        try
        {
            Directory.CreateDirectory(root);
            if (!PlainDirectory(root) || File.Exists(destination))
                return Fail(data, profile, "ModAlreadyInstalled", "This package already exists in the managed mod folder.");
            var activation = Path.Combine(root, "mod-list.json");
            var previous = ReadOptional(activation);
            var node = previous is null ? new JsonObject
            {
                ["mods"] = new JsonArray(new JsonObject { ["name"] = "base", ["enabled"] = true })
            } : JsonNode.Parse(previous) as JsonObject;
            if (node?["mods"] is not JsonArray mods)
                return Fail(data, profile, "ModListInvalid", "The Factorio mod list is invalid. No package was imported.");
            mods.Add(new JsonObject { ["name"] = package.Name, ["enabled"] = true });
            var sourceSha = ShaFile(source);
            try
            {
                CopyVerified(source, destination, sourceSha);
                var copiedPackage = ReadFactorioPackage(destination);
                if (copiedPackage.Name != package.Name || copiedPackage.Version != package.Version ||
                    copiedPackage.RequiredVersion != package.RequiredVersion)
                    throw new InvalidDataException("The selected mod changed while it was copied.");
                WriteAtomic(activation, Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            }
            catch
            {
                RestoreActivation(activation, previous);
                if (File.Exists(destination)) File.Delete(destination);
                throw;
            }
            try
            {
                var after = List(data, profile);
                if (!after.Ok) throw new InvalidDataException("The new mod state could not be read.");
                SaveRevision(data, profile, new(profile.Id, profile.Kind, before.StateToken, previous,
                    after.StateToken, Path.GetFileName(destination), sourceSha));
            }
            catch
            {
                RestoreActivation(activation, previous);
                File.Delete(destination);
                throw;
            }
            return new(true, "ModImported", "Mod copied into this managed server and enabled. Restart and check game readiness.",
                List(data, profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or ArgumentException or NotSupportedException or CryptographicException or
                                   InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return Fail(data, profile, "ModImportFailed", "The mod could not be copied and enabled safely."); }
    }

    public static ServerAddOnResult ImportBedrock(LocalData data, ServerProfile profile, string source,
        string expectedToken)
    {
        var before = List(data, profile);
        if (!before.Ok || before.StateToken != expectedToken)
            return Fail(data, profile, "AddOnsChanged", "The pack folders changed. Reload before importing.");
        if (ValidatePackage(profile, source, out _) is { } issue)
            return Fail(data, profile, "InvalidPackage", issue);
        var world = WorldRoot(profile);
        if (!PlainDirectory(world) || AppInstance.ContainsPath(profile.WorldDirectory, source))
            return Fail(data, profile, "InvalidPackage", "Choose a local pack outside the managed server folder.");
        Package package;
        try { package = ReadBedrockPackage(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { return Fail(data, profile, "InvalidPackage", "The selected pack changed or became unreadable."); }
        var type = package.Type;
        if (before.Items.Any(item => item.Key == type + ":" + package.Id))
            return Fail(data, profile, "PackAlreadyInstalled", "This pack ID is already installed for this world.");
        var root = Path.Combine(world, type + "_packs");
        var folderName = Guid.Parse(package.Id!).ToString("N") + "_" + package.Version.Replace('.', '_');
        var destination = Path.Combine(root, folderName);
        var activation = Path.Combine(world, $"world_{type}_packs.json");
        var stage = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            if (!PlainDirectory(root) || Directory.Exists(destination))
                return Fail(data, profile, "PackAlreadyInstalled", "This pack destination already exists.");
            var previous = ReadOptional(activation);
            var active = previous is null ? new JsonArray() : JsonNode.Parse(previous) as JsonArray;
            if (active is null || active.Count > 64)
                return Fail(data, profile, "PackListInvalid", "The world's pack list is invalid.");
            Directory.CreateDirectory(stage);
            var sourceSha = ShaFile(source);
            ExtractPack(source, package, stage);
            if (ShaFile(source) != sourceSha)
                throw new InvalidDataException("The selected pack changed while it was copied.");
            var extractedManifest = Path.Combine(stage, "manifest.json");
            if (new FileInfo(extractedManifest).Length > 64 * 1024)
                throw new InvalidDataException("The copied pack manifest is too large.");
            var extractedPackage = ReadBedrockManifest(File.ReadAllBytes(extractedManifest), type);
            if (extractedPackage.Id != package.Id || extractedPackage.Version != package.Version ||
                extractedPackage.RequiredVersion != package.RequiredVersion)
                throw new InvalidDataException("The selected pack manifest changed while it was copied.");
            var extractedSha = ShaTree(stage);
            active.Add(new JsonObject
            {
                ["pack_id"] = package.Id,
                ["version"] = new JsonArray(package.BedrockVersion!.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            });
            Directory.Move(stage, destination);
            try { WriteAtomic(activation, Encoding.UTF8.GetBytes(active.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))); }
            catch
            {
                Directory.Delete(destination, true);
                throw;
            }
            try
            {
                var after = List(data, profile);
                if (!after.Ok) throw new InvalidDataException("The new pack state could not be read.");
                SaveRevision(data, profile, new(profile.Id, profile.Kind, before.StateToken, previous,
                    after.StateToken, folderName, extractedSha, type));
            }
            catch
            {
                RestoreActivation(activation, previous);
                Directory.Delete(destination, true);
                throw;
            }
            return new(true, "PackImported", "Pack copied into this world and enabled. Restart and check a real game join.",
                List(data, profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or ArgumentException or NotSupportedException or CryptographicException or
                                   InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return Fail(data, profile, "PackImportFailed", "The Bedrock pack could not be imported safely."); }
        finally
        {
            if (Directory.Exists(stage) && AppInstance.ContainsPath(root, stage))
            {
                try { Directory.Delete(stage, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static ServerAddOnResult SetEnabled(LocalData data, ServerProfile profile,
        ServerAddOnChangeRequest request)
    {
        if (profile.Kind == GameKinds.MinecraftBedrock)
            return SetBedrockEnabled(data, profile, request);
        var before = List(data, profile);
        if (!before.Ok || before.StateToken != request.ExpectedStateToken)
            return Fail(data, profile, "AddOnsChanged", "The add-on folder changed. Reload before changing it.");
        if (profile.Kind != GameKinds.Factorio ||
            !before.Items.Any(item => item.Key == request.Key))
            return Fail(data, profile, "AddOnUnsupported", "Choose an installed Factorio mod.");
        try
        {
            var path = Path.Combine(profile.WorldDirectory, "mods", "mod-list.json");
            var previous = ReadOptional(path);
            if (previous is null || JsonNode.Parse(previous) is not JsonObject node ||
                node["mods"] is not JsonArray mods)
                return Fail(data, profile, "ModListInvalid", "The Factorio mod list is missing or invalid.");
            var item = before.Items.Single(candidate => candidate.Key == request.Key);
            var entry = mods.OfType<JsonObject>().SingleOrDefault(candidate =>
                candidate["name"]?.GetValue<string>() == item.Name);
            if (entry is null) return Fail(data, profile, "ModListChanged", "This mod is missing from the activation list.");
            if (item.Enabled == request.Enabled)
                return new(true, "NoChanges", "This mod already has that state.", before);
            entry["enabled"] = request.Enabled;
            WriteAtomic(path, Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            try
            {
                var after = List(data, profile);
                if (!after.Ok) throw new InvalidDataException("The new mod state could not be read.");
                SaveRevision(data, profile, new(profile.Id, profile.Kind, before.StateToken, previous,
                    after.StateToken, null, null));
            }
            catch { RestoreActivation(path, previous); throw; }
            return new(true, "ModStateChanged", "Mod state saved. Restart and check game readiness.", List(data, profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or ArgumentException or NotSupportedException)
        { return Fail(data, profile, "ModChangeFailed", "The mod state could not be changed safely."); }
    }

    public static ServerAddOnResult Undo(LocalData data, ServerProfile profile, ServerAddOnUndoRequest request)
    {
        if (profile.Kind == GameKinds.MinecraftBedrock)
            return UndoBedrock(data, profile, request);
        var before = List(data, profile);
        var revision = LoadRevision(data, profile);
        if (!before.Ok || revision is null || before.StateToken != request.ExpectedStateToken ||
            revision.AfterToken != before.StateToken)
            return Fail(data, profile, "UndoUnavailable", "The add-on state changed. Reload before undoing.");
        try
        {
            var root = Path.Combine(profile.WorldDirectory, "mods");
            var activation = Path.Combine(root, "mod-list.json");
            string? packagePath = null;
            if (revision.AddedRelativePath is { } added)
            {
                if (Path.GetFileName(added) != added || revision.AddedSha256 is null)
                    return Fail(data, profile, "UndoUnavailable", "The saved package reference is invalid.");
                packagePath = Path.Combine(root, added);
                if (!PlainFile(packagePath) || ShaFile(packagePath) != revision.AddedSha256)
                    return Fail(data, profile, "UndoUnavailable", "The imported package changed. It was not removed.");
            }
            if (revision.BeforeActivation is null) File.Delete(activation);
            else WriteAtomic(activation, revision.BeforeActivation);
            if (packagePath is not null) File.Delete(packagePath);
            data.DeleteProtected(RevisionName(profile.Id));
            return new(true, "AddOnUndone", "The previous mod state was restored.", List(data, profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or ArgumentException or NotSupportedException)
        { return Fail(data, profile, "UndoFailed", "The previous mod state could not be restored."); }
    }

    private static ServerAddOnResult SetBedrockEnabled(LocalData data, ServerProfile profile,
        ServerAddOnChangeRequest request)
    {
        var before = List(data, profile);
        if (!before.Ok || before.StateToken != request.ExpectedStateToken)
            return Fail(data, profile, "AddOnsChanged", "The pack folders changed. Reload before changing one.");
        var item = before.Items.SingleOrDefault(candidate => candidate.Key == request.Key &&
            candidate.Type is "behavior pack" or "resource pack");
        if (item is null) return Fail(data, profile, "PackUnsupported", "Choose a world-specific Bedrock pack.");
        if (item.Enabled == request.Enabled) return new(true, "NoChanges", "This pack already has that state.", before);
        var parts = item.Key.Split(':', 2);
        var type = parts[0];
        var id = parts[1];
        var activation = Path.Combine(WorldRoot(profile), $"world_{type}_packs.json");
        try
        {
            var previous = ReadOptional(activation);
            var active = previous is null ? new JsonArray() : JsonNode.Parse(previous) as JsonArray;
            if (active is null) return Fail(data, profile, "PackListInvalid", "The world pack list is invalid.");
            var existing = active.OfType<JsonObject>().SingleOrDefault(entry =>
                entry["pack_id"]?.GetValue<string>() == id);
            if (request.Enabled)
            {
                if (existing is not null) return Fail(data, profile, "PackListChanged", "The pack is already active.");
                var version = item.Version.Split('.').Select(int.Parse).ToArray();
                active.Add(new JsonObject
                {
                    ["pack_id"] = id,
                    ["version"] = new JsonArray(version.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
                });
            }
            else
            {
                if (existing is null) return Fail(data, profile, "PackListChanged", "The pack is already inactive.");
                active.Remove(existing);
            }
            WriteAtomic(activation, Encoding.UTF8.GetBytes(active.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            try
            {
                var after = List(data, profile);
                if (!after.Ok) throw new InvalidDataException("The new pack state could not be read.");
                SaveRevision(data, profile, new(profile.Id, profile.Kind, before.StateToken, previous,
                    after.StateToken, null, null, type));
            }
            catch { RestoreActivation(activation, previous); throw; }
            return new(true, "PackStateChanged", "World pack state saved. Restart and check a real game join.",
                List(data, profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or ArgumentException or NotSupportedException or FormatException)
        { return Fail(data, profile, "PackChangeFailed", "The world pack state could not be changed safely."); }
    }

    private static ServerAddOnResult UndoBedrock(LocalData data, ServerProfile profile,
        ServerAddOnUndoRequest request)
    {
        var before = List(data, profile);
        var revision = LoadRevision(data, profile);
        if (!before.Ok || revision is null || before.StateToken != request.ExpectedStateToken ||
            revision.AfterToken != before.StateToken || revision.ActivationType is not ("behavior" or "resource"))
            return Fail(data, profile, "UndoUnavailable", "The pack state changed. Reload before undoing.");
        var world = WorldRoot(profile);
        var activation = Path.Combine(world, $"world_{revision.ActivationType}_packs.json");
        try
        {
            string? addedPath = null;
            if (revision.AddedRelativePath is { } added)
            {
                if (!Regex.IsMatch(added, "^[a-f0-9]{32}_[0-9_]+$", RegexOptions.CultureInvariant) ||
                    revision.AddedSha256 is null)
                    return Fail(data, profile, "UndoUnavailable", "The saved pack reference is invalid.");
                addedPath = Path.Combine(world, revision.ActivationType + "_packs", added);
                if (!PlainDirectory(addedPath) || ShaTree(addedPath) != revision.AddedSha256)
                    return Fail(data, profile, "UndoUnavailable", "The imported pack changed and was not removed.");
            }
            if (revision.BeforeActivation is null) File.Delete(activation);
            else WriteAtomic(activation, revision.BeforeActivation);
            if (addedPath is not null) Directory.Delete(addedPath, true);
            data.DeleteProtected(RevisionName(profile.Id));
            return new(true, "AddOnUndone", "The previous world pack state was restored.", List(data, profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   JsonException or ArgumentException or NotSupportedException)
        { return Fail(data, profile, "UndoFailed", "The previous pack state could not be restored."); }
    }

    private static List<ServerAddOnItem> ListFactorio(ServerProfile profile)
    {
        var root = Path.Combine(profile.WorldDirectory, "mods");
        if (!Directory.Exists(root)) return [];
        if (!PlainDirectory(root)) throw new InvalidDataException("The mod folder is linked.");
        var activation = ReadOptional(Path.Combine(root, "mod-list.json"));
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (activation is not null)
        {
            if (JsonNode.Parse(activation)?["mods"] is not JsonArray mods || mods.Count > 65)
                throw new InvalidDataException("The mod list is invalid.");
            foreach (var entry in mods.OfType<JsonObject>())
            {
                var name = entry["name"]?.GetValue<string>();
                var enabled = entry["enabled"]?.GetValue<bool>();
                if (name is null || enabled is null || !states.TryAdd(name, enabled.Value))
                    throw new InvalidDataException("The mod list has invalid entries.");
            }
        }
        var files = Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly).Take(65).ToList();
        if (files.Count > 64) throw new InvalidDataException("Too many mod packages.");
        return files.Select(path =>
        {
            if (!PlainFile(path)) throw new InvalidDataException("A mod package is linked.");
            var package = ReadFactorioPackage(path);
            var version = GameVersion(profile);
            var compatible = version == "Unknown" ? "Game version unknown; check before joining" :
                version.StartsWith(package.RequiredVersion + ".", StringComparison.Ordinal)
                    ? "Version matches" : "Requires Factorio " + package.RequiredVersion;
            return new ServerAddOnItem(Path.GetFileName(path), package.Name, package.Version,
                package.RequiredVersion, states.GetValueOrDefault(package.Name), compatible, "Factorio mod");
        }).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<ServerAddOnItem> ListBedrock(ServerProfile profile)
    {
        var world = WorldRoot(profile);
        if (!Directory.Exists(world)) return [];
        var items = new List<ServerAddOnItem>();
        foreach (var type in new[] { "behavior", "resource" })
        {
            var activePath = Path.Combine(world, $"world_{type}_packs.json");
            var active = ReadOptional(activePath);
            var enabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (active is not null)
            {
                if (JsonNode.Parse(active) is not JsonArray array || array.Count > 64)
                    throw new InvalidDataException("The Bedrock world pack list is invalid.");
                foreach (var entry in array.OfType<JsonObject>())
                {
                    var id = entry["pack_id"]?.GetValue<string>();
                    if (id is null || !Guid.TryParse(id, out _) || !enabledIds.Add(id))
                        throw new InvalidDataException("The Bedrock world pack list has invalid IDs.");
                }
            }
            var root = Path.Combine(world, type + "_packs");
            if (Directory.Exists(root))
            {
                if (!PlainDirectory(root)) throw new InvalidDataException("A pack folder is linked.");
                var folders = Directory.EnumerateDirectories(root).Take(65).ToList();
                if (folders.Count > 64) throw new InvalidDataException("Too many Bedrock packs.");
                foreach (var folder in folders)
                {
                    if (!PlainDirectory(folder)) throw new InvalidDataException("A pack is linked.");
                    var manifest = Path.Combine(folder, "manifest.json");
                    if (!PlainFile(manifest)) continue;
                    var package = ReadBedrockManifest(File.ReadAllBytes(manifest), type);
                    var serverVersion = GameVersion(profile);
                    var compatibility = serverVersion == "Unknown" ? "Game version unknown; check before joining" :
                        Version.TryParse(serverVersion, out var current) &&
                        Version.TryParse(package.RequiredVersion, out var minimum) && current < minimum
                            ? "Requires Bedrock " + package.RequiredVersion : "Check real client join";
                    items.Add(new(type + ":" + package.Id, package.Name, package.Version,
                        package.RequiredVersion, enabledIds.Remove(package.Id!), compatibility, type + " pack"));
                }
            }
            foreach (var missing in enabledIds)
                items.Add(new(type + ":" + missing, "Shared or missing pack", "Unknown", "Unknown",
                    true, "Outside this world's checkpoint", "External shared pack"));
        }
        return items;
    }

    private static Package ReadFactorioPackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is < 1 or > MaximumEntries ||
            archive.Entries.Sum(entry => entry.Length) > MaximumExtractedBytes)
            throw new InvalidDataException("Mod ZIP limits exceeded.");
        var info = archive.Entries.Where(entry => entry.FullName.EndsWith("/info.json", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.Equals("info.json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (info.Count != 1 || info[0].Length > 64 * 1024)
            throw new InvalidDataException("Expected one small info.json in the mod ZIP.");
        using var stream = info[0].Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var name = root.GetProperty("name").GetString() ?? "";
        var version = root.GetProperty("version").GetString() ?? "";
        var factorio = root.GetProperty("factorio_version").GetString() ?? "";
        if (!FactorioName.IsMatch(name) || !FactorioVersion.IsMatch(version) ||
            !Regex.IsMatch(factorio, @"^\d+\.\d+$"))
            throw new InvalidDataException("The Factorio mod metadata is invalid.");
        if (!info[0].FullName.Equals($"{name}_{version}/info.json", StringComparison.Ordinal))
            throw new InvalidDataException("The Factorio mod ZIP folder does not match its metadata.");
        return new(name, version, factorio, "Factorio mod");
    }

    private static Package ReadBedrockPackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is < 1 or > MaximumEntries ||
            archive.Entries.Sum(entry => entry.Length) > MaximumExtractedBytes)
            throw new InvalidDataException("Bedrock pack ZIP limits exceeded.");
        var manifests = archive.Entries.Where(entry => entry.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase) &&
            entry.FullName.Count(character => character == '/') == 1).ToList();
        if (manifests.Count != 1 || manifests[0].Length > 64 * 1024)
            throw new InvalidDataException("Expected one small Bedrock pack manifest.");
        using var stream = manifests[0].Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var package = ReadBedrockManifest(memory.ToArray(), null);
        return package with { Prefix = manifests[0].FullName[..^"manifest.json".Length] };
    }

    private static Package ReadBedrockManifest(byte[] bytes, string? expectedType)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var header = root.GetProperty("header");
        var name = header.GetProperty("name").GetString() ?? "";
        var id = header.GetProperty("uuid").GetString() ?? "";
        var versionField = header.GetProperty("version");
        var version = VersionArray(versionField);
        var minimum = VersionArray(header.GetProperty("min_engine_version"));
        if (name.Length is < 1 or > 100 || !Guid.TryParse(id, out _) ||
            !Version.TryParse(version, out _) || !Version.TryParse(minimum, out _))
            throw new InvalidDataException("Bedrock pack identity or version is invalid.");
        var modules = root.GetProperty("modules");
        if (modules.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Bedrock pack modules are missing.");
        var types = modules.EnumerateArray().Select(module =>
            module.TryGetProperty("type", out var value) ? value.GetString() : null).ToHashSet();
        var type = types.Contains("data") ? "behavior" : types.Contains("resources") ? "resource" : null;
        if (type is null || expectedType is not null && type != expectedType)
            throw new InvalidDataException("The Bedrock pack type does not match its folder.");
        var parts = versionField.ValueKind == JsonValueKind.Array
            ? versionField.EnumerateArray().Select(item => item.GetInt32()).ToArray()
            : version.Split('.').Select(int.Parse).ToArray();
        if (parts.Length != 3 || parts.Any(part => part is < 0 or > 99999))
            throw new InvalidDataException("Bedrock pack version must have three numbers.");
        return new(name, version, minimum, type, id, parts);
    }

    private static void ExtractPack(string source, Package package, string stage)
    {
        using var archive = ZipFile.OpenRead(source);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long extracted = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Contains('\\') || name.StartsWith('/') || name.Contains(':') ||
                name.Split('/').Any(part => part is "." or ".."))
                throw new InvalidDataException("The pack contains an unsafe path.");
            if (!name.StartsWith(package.Prefix!, StringComparison.Ordinal))
                throw new InvalidDataException("The pack has files outside its manifest folder.");
            var relative = name[package.Prefix!.Length..];
            if (relative.Length == 0 || relative.EndsWith('/')) continue;
            if (!seen.Add(relative)) throw new InvalidDataException("The pack contains repeated paths.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The pack contains a filesystem link.");
            extracted = checked(extracted + entry.Length);
            if (extracted > MaximumExtractedBytes)
                throw new InvalidDataException("The pack extracts beyond its size limit.");
            var destination = Path.GetFullPath(Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!AppInstance.ContainsPath(stage, destination))
                throw new InvalidDataException("The pack contains a path outside staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(true);
        }
        if (!PlainFile(Path.Combine(stage, "manifest.json")))
            throw new InvalidDataException("The extracted pack has no manifest.");
    }

    private static string ShaTree(string root)
    {
        if (!PlainDirectory(root)) throw new InvalidDataException("The pack folder is linked.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(8193).ToList();
        if (files.Count > 8192) throw new InvalidDataException("The pack has too many files.");
        foreach (var path in files)
        {
            if (!PlainFile(path)) throw new InvalidDataException("A pack file is linked.");
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path)));
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string VersionArray(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? string.Join('.', value.EnumerateArray().Select(number => number.GetInt32()))
        : value.GetString() ?? "Unknown";
    private static string WorldRoot(ServerProfile profile) =>
        Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId);

    private static string StateToken(ServerProfile profile)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var roots = profile.Kind == GameKinds.Factorio
            ? new[] { Path.Combine(profile.WorldDirectory, "mods") }
            : profile.Kind == GameKinds.MinecraftBedrock
                ? new[] { Path.Combine(WorldRoot(profile), "behavior_packs"),
                    Path.Combine(WorldRoot(profile), "resource_packs") } : [];
        if (profile.Kind == GameKinds.MinecraftBedrock)
        {
            foreach (var type in new[] { "behavior", "resource" })
            {
                var activation = Path.Combine(WorldRoot(profile), $"world_{type}_packs.json");
                if (ReadOptional(activation) is { } bytes) hash.AppendData(bytes);
            }
        }
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            if (!PlainDirectory(root)) throw new InvalidDataException("The add-on folder is linked.");
            var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(8193).ToList();
            if (paths.Count > 8192) throw new InvalidDataException("Too many add-on files.");
            foreach (var path in paths)
            {
                if (!PlainFile(path)) throw new InvalidDataException("An add-on file is linked.");
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path)));
                using var file = File.OpenRead(path);
                hash.AppendData(SHA256.HashData(file));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string? VersionWarning(LocalData data, ServerProfile profile, string current)
    {
        var recorded = data.LoadProtected($"addon-game-version-{profile.Id:N}.protected");
        if (recorded is null || current == "Unknown") return null;
        var prior = Encoding.UTF8.GetString(recorded);
        return prior == current ? null : $"Game version changed from {prior} to {current}. Review add-ons before Start.";
    }

    private static Revision? LoadRevision(LocalData data, ServerProfile profile)
    {
        var bytes = data.LoadProtected(RevisionName(profile.Id));
        if (bytes is null) return null;
        try
        {
            var revision = JsonSerializer.Deserialize<Revision>(bytes);
            return revision is { } value && value.ProfileId == profile.Id && value.Kind == profile.Kind
                ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static void SaveRevision(LocalData data, ServerProfile profile, Revision revision)
    {
        data.SaveProtected(RevisionName(profile.Id), JsonSerializer.SerializeToUtf8Bytes(revision));
        var version = GameVersion(profile);
        if (version != "Unknown")
            data.SaveProtected($"addon-game-version-{profile.Id:N}.protected", Encoding.UTF8.GetBytes(version));
    }

    private static string RevisionName(Guid id) => $"addon-revision-{id:N}.protected";
    private static ServerAddOnResult Fail(LocalData data, ServerProfile profile, string code, string message) =>
        new(false, code, message, List(data, profile));
    private static bool PlainFile(string path) => File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
        PlainDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    private static bool PlainDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private static byte[]? ReadOptional(string path)
    {
        if (!File.Exists(path)) return null;
        if (!PlainFile(path) || new FileInfo(path).Length > 256 * 1024)
            throw new InvalidDataException("The activation file is linked or too large.");
        return File.ReadAllBytes(path);
    }
    private static string ShaFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    private static void CopyVerified(string source, string destination, string expectedSha)
    {
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(true);
        }
        if (ShaFile(destination) != expectedSha) throw new CryptographicException("Copied package changed.");
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        if (bytes.Length > 256 * 1024 || !PlainDirectory(Path.GetDirectoryName(path)!))
            throw new InvalidDataException("The activation file is too large or its directory is linked.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            if (File.Exists(path))
            {
                if (!PlainFile(path)) throw new InvalidDataException("The activation file is linked.");
                File.Replace(temp, path, null);
            }
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void RestoreActivation(string path, byte[]? before)
    {
        if (before is null) { if (File.Exists(path)) File.Delete(path); }
        else WriteAtomic(path, before);
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TogetherServer;

public sealed record RequiredGameAddOn(string Id, string Name, string Version, string Type);
public sealed record GameRequirements(Guid ProfileId, string Kind, string GameName, string? RequiredVersion,
    string VersionSource, string AddOnState, IReadOnlyList<RequiredGameAddOn> AddOns,
    DateTimeOffset CheckedUtc, string Guidance);
public sealed record GameRequirementsResult(bool Ok, string Code, string Message, GameRequirements? Requirements = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameRequirementChange(string? Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManualClientVersionChange(string? Version);
public sealed record GameCompatibilityResult(bool Ok, string Code, string Message,
    GameRequirements? Requirements = null, string? ClientVersion = null, string ClientVersionSource = "Unknown",
    string VersionComparison = "Unknown", string AddOnComparison = "Unknown");

// Read-only informational metadata. This service supplies no lifecycle or game-join evidence.
internal static class GameCompatibility
{
    internal const int MaximumWireBytes = 64 * 1024;
    internal const int MaximumAddOns = 64;
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(4);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex VersionPattern = new(@"^[0-9][A-Za-z0-9._+\-]{0,47}$", RegexOptions.CultureInvariant);
    private sealed record SavedRequirement(Guid ProfileId, string Fingerprint, string Version);

    internal static bool ValidVersion(string? value) => value is not null && VersionPattern.IsMatch(value);
    internal static bool Supported(string kind) => kind is GameKinds.Valheim or GameKinds.MinecraftJava or
        GameKinds.MinecraftBedrock or GameKinds.Factorio or GameKinds.Terraria;
    internal static string GameName(string kind) => kind switch
    {
        GameKinds.MinecraftJava => "Minecraft Java",
        GameKinds.MinecraftBedrock => "Minecraft Bedrock",
        GameKinds.Valheim => "Valheim",
        GameKinds.Factorio => "Factorio",
        GameKinds.Terraria => "Terraria",
        _ => "Unsupported game"
    };
    internal static string Fingerprint(ServerProfile profile) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.Id,
            profile.Kind,
            profile.WorldId,
            profile.WorldDirectory,
            profile.ExecutablePath,
            Jar = profile.Minecraft?.ServerJarPath
        }, Json)));

    internal static GameRequirementsResult Observe(LocalData data, ServerProfile profile, CancellationToken ct)
    {
        if (!Supported(profile.Kind)) return Failure("UnsupportedGame", "Pre-join requirements are available for built-in games.");
        ct.ThrowIfCancellationRequested();
        string? version;
        using (var versionDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            versionDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            try { version = ObservedVersion(profile, versionDeadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { version = null; }
        }
        var source = version is null ? "Unknown" : "Observed";
        if (version is null)
        {
            var saved = ReadOwnerVersion(data, profile);
            if (saved is not null) { version = saved; source = "OwnerReported"; }
        }
        IReadOnlyList<RequiredGameAddOn> addOns = [];
        var state = profile.Kind is GameKinds.Factorio or GameKinds.MinecraftBedrock ? "Known" : "NotReviewed";
        try
        {
            addOns = profile.Kind switch
            {
                GameKinds.Factorio => ReadFactorioMods(Path.Combine(profile.WorldDirectory, "mods"), ct,
                    string.IsNullOrEmpty(profile.ExecutablePath) ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(profile.ExecutablePath)!, "..", "..", "data"))),
                GameKinds.MinecraftBedrock => ReadBedrockPacks(profile, ct),
                _ => []
            };
        }
        catch (Exception ex) when (ReadFailure(ex)) { state = "Unknown"; }
        ct.ThrowIfCancellationRequested();
        return new(true, "RequirementsRead", "Game requirements are informational; test a real join separately.",
            new(profile.Id, profile.Kind, GameName(profile.Kind), version, source, state, addOns,
                DateTimeOffset.UtcNow, profile.Kind == GameKinds.Factorio
                    ? "Use the same Factorio version and enabled mods. Install or change mods yourself."
                    : profile.Kind == GameKinds.MinecraftBedrock
                        ? "Review the Host's world packs in Bedrock. Pack delivery and a real join still need checking."
                        : "Use the Host's required game version. Mod loaders for this game are outside reviewed support."));
    }

    internal static void SaveOwnerVersion(LocalData data, ServerProfile profile, string? version)
    {
        var name = OwnerFile(profile.Id);
        if (version is null) { data.DeleteProtected(name); return; }
        if (!ValidVersion(version)) throw new InvalidDataException("Invalid game version.");
        data.SaveProtected(name, JsonSerializer.SerializeToUtf8Bytes(new SavedRequirement(profile.Id, Fingerprint(profile), version), Json));
    }

    private static string? ReadOwnerVersion(LocalData data, ServerProfile profile)
    {
        var name = OwnerFile(profile.Id);
        try
        {
            if (!File.Exists(Path.Combine(data.RootPath, name))) return null;
            _ = ReadSmall(Path.Combine(data.RootPath, name), 16 * 1024, CancellationToken.None);
            var bytes = data.LoadProtected(name);
            if (bytes is null || bytes.Length > 4096) return null;
            var value = JsonSerializer.Deserialize<SavedRequirement>(bytes, Json);
            return value?.ProfileId == profile.Id && value.Fingerprint == Fingerprint(profile) && ValidVersion(value.Version)
                ? value.Version : null;
        }
        catch (Exception ex) when (ReadFailure(ex) || ex is CryptographicException) { return null; }
    }
    private static string OwnerFile(Guid id) => $"game-requirement-{id:N}.protected";

    internal static string? ObservedVersion(ServerProfile profile, CancellationToken ct)
    {
        try
        {
            if (profile.Kind == GameKinds.MinecraftJava)
            {
                var jar = profile.Minecraft?.ServerJarPath;
                if (string.IsNullOrEmpty(jar) || Path.GetFileName(jar) != "server.jar") return null;
                using var metadata = JsonDocument.Parse(ReadSmall(Path.Combine(Path.GetDirectoryName(jar)!, ".togetherserver-java.json"), 4096, ct));
                var recorded = metadata.RootElement.GetProperty("version").GetString();
                var sha1 = metadata.RootElement.GetProperty("sha1").GetString();
                if (!ValidVersion(recorded) || sha1 is null || sha1.Length != 40 || !sha1.All(char.IsAsciiHexDigit)) return null;
                using var input = OpenReviewed(jar, 300_000_000);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var buffer = new byte[64 * 1024];
                int count;
                while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha1, StringComparison.OrdinalIgnoreCase)) return null;
                input.Position = 0;
                using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.Count > 100_000) return null;
                var launchManifest = zip.GetEntry("META-INF/MANIFEST.MF");
                if (launchManifest is null) return null;
                var manifestText = System.Text.Encoding.UTF8.GetString(ReadEntry(launchManifest, 4096, ct));
                var mainClasses = manifestText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                    .Select(line => line[11..].Trim()).ToArray();
                if (mainClasses.Length != 1 || mainClasses[0] is not ("net.minecraft.server.Main" or "net.minecraft.bundler.Main") ||
                    zip.Entries.Any(entry => entry.FullName is "fabric.mod.json" or "META-INF/mods.toml" ||
                        entry.FullName.StartsWith("io/papermc/", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.StartsWith("org/bukkit/", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.StartsWith("net/fabricmc/", StringComparison.OrdinalIgnoreCase))) return null;
                var entry = zip.GetEntry("version.json");
                if (entry is null) return null;
                using var gameVersion = JsonDocument.Parse(ReadEntry(entry, 4096, ct));
                return gameVersion.RootElement.GetProperty("id").GetString() == recorded ? recorded : null;
            }
            // Unity/Steam build IDs do not establish Valheim's game version.
            if (profile.Kind == GameKinds.Valheim) return null;
            var expectedName = profile.Kind switch
            {
                GameKinds.Factorio => "factorio.exe",
                GameKinds.Terraria => "TerrariaServer.exe",
                GameKinds.MinecraftBedrock => "bedrock_server.exe",
                _ => ""
            };
            if (!Path.GetFileName(profile.ExecutablePath).Equals(expectedName, StringComparison.OrdinalIgnoreCase)) return null;
            return ReadExecutableVersion(profile.ExecutablePath, profile.Kind, ct);
        }
        catch (Exception ex) when (ReadFailure(ex) || ex is System.ComponentModel.Win32Exception) { return null; }
    }

    internal static string? ReadExecutableVersion(string path, string kind, CancellationToken ct)
    {
        using var lease = OpenReviewed(path, 512L * 1024 * 1024);
        ct.ThrowIfCancellationRequested();
        var info = FileVersionInfo.GetVersionInfo(path);
        var product = kind switch { GameKinds.Factorio => "Factorio", GameKinds.Terraria => "Terraria", GameKinds.MinecraftBedrock => "Minecraft", _ => "" };
        // Product identity and a whole version are required; no numeric substring guesses.
        if (product.Length == 0 || info.ProductName?.Contains(product, StringComparison.OrdinalIgnoreCase) != true) return null;
        var value = info.ProductVersion?.Trim();
        ct.ThrowIfCancellationRequested();
        return ValidVersion(value) ? value : null;
    }

    internal static IReadOnlyList<RequiredGameAddOn> ReadFactorioMods(string root, CancellationToken ct, string? bundledRoot = null)
    {
        // Missing activation data is Unknown, including custom client mod directories.
        using var activation = JsonDocument.Parse(ReadSmall(Path.Combine(root, "mod-list.json"), 64 * 1024, ct));
        var mods = activation.RootElement.GetProperty("mods");
        if (mods.ValueKind != JsonValueKind.Array || mods.GetArrayLength() > MaximumAddOns + 8) throw new InvalidDataException();
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in mods.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            if (name is null || !Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,80}$") || !seen.Add(name)) throw new InvalidDataException();
            if (entry.GetProperty("enabled").GetBoolean()) enabled.Add(name);
        }
        var result = new List<RequiredGameAddOn>();
        var packages = new HashSet<string>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly).Take(MaximumAddOns + 1).ToArray();
        if (files.Length > MaximumAddOns) throw new InvalidDataException();
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            using var input = OpenReviewed(path, 128L * 1024 * 1024);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count > 2048) throw new InvalidDataException();
            var entries = zip.Entries.Where(entry => entry.FullName.EndsWith("/info.json", StringComparison.Ordinal)).Take(2).ToArray();
            if (entries.Length != 1) throw new InvalidDataException();
            using var metadata = JsonDocument.Parse(ReadEntry(entries[0], 64 * 1024, ct));
            var name = metadata.RootElement.GetProperty("name").GetString();
            var version = metadata.RootElement.GetProperty("version").GetString();
            if (name is null || !Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,80}$") || !ValidVersion(version) ||
                entries[0].FullName != $"{name}_{version}/info.json" || !packages.Add(name)) throw new InvalidDataException();
            if (enabled.Remove(name)) result.Add(new(name, name, version!, "FactorioMod"));
        }
        enabled.ExceptWith(["base", "core"]);
        foreach (var name in new[] { "space-age", "quality", "elevated-rails" })
        {
            if (!enabled.Remove(name)) continue;
            if (bundledRoot is null) throw new InvalidDataException();
            using var metadata = JsonDocument.Parse(ReadSmall(Path.Combine(bundledRoot, name, "info.json"), 64 * 1024, ct));
            var version = metadata.RootElement.GetProperty("version").GetString();
            if (metadata.RootElement.GetProperty("name").GetString() != name || !ValidVersion(version) || result.Count >= MaximumAddOns) throw new InvalidDataException();
            result.Add(new(name, name, version!, "FactorioMod"));
        }
        if (enabled.Count != 0) throw new InvalidDataException();
        return result.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<RequiredGameAddOn> ReadBedrockPacks(ServerProfile profile, CancellationToken ct)
    {
        var world = Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId);
        var result = new List<RequiredGameAddOn>();
        foreach (var type in new[] { "behavior", "resource" })
        {
            var activationPath = Path.Combine(world, $"world_{type}_packs.json");
            if (!File.Exists(activationPath)) continue;
            using var activation = JsonDocument.Parse(ReadSmall(activationPath, 64 * 1024, ct));
            if (activation.RootElement.ValueKind != JsonValueKind.Array || activation.RootElement.GetArrayLength() > MaximumAddOns) throw new InvalidDataException();
            var manifests = new Dictionary<Guid, string>();
            var packRoot = Path.Combine(world, type + "_packs");
            if (Directory.Exists(packRoot))
            {
                EnsurePlainAncestors(packRoot);
                var folders = Directory.EnumerateDirectories(packRoot).Take(MaximumAddOns + 1).ToArray();
                if (folders.Length > MaximumAddOns) throw new InvalidDataException();
                foreach (var folder in folders)
                {
                    ct.ThrowIfCancellationRequested();
                    using var manifest = JsonDocument.Parse(ReadSmall(Path.Combine(folder, "manifest.json"), 64 * 1024, ct));
                    var header = manifest.RootElement.GetProperty("header");
                    var manifestVersion = PackVersion(header.GetProperty("version"));
                    if (!Guid.TryParse(header.GetProperty("uuid").GetString(), out var manifestId) || manifestVersion is null ||
                        !manifests.TryAdd(manifestId, manifestVersion)) throw new InvalidDataException();
                }
            }
            foreach (var item in activation.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("pack_id").GetString();
                var version = PackVersion(item.GetProperty("version"));
                if (!Guid.TryParse(id, out var uuid) || version is null || result.Count >= MaximumAddOns ||
                    manifests.GetValueOrDefault(uuid) != version || result.Any(pack => pack.Id == uuid.ToString())) throw new InvalidDataException();
                // Active UUID/version is the reviewed world requirement; no pack path/title leaves the Host.
                result.Add(new(uuid.ToString(), $"{type} pack {uuid.ToString()[..8]}", version, type == "behavior" ? "BedrockBehaviorPack" : "BedrockResourcePack"));
            }
        }
        if (!Directory.Exists(world)) throw new InvalidDataException();
        EnsurePlainAncestors(world);
        return result;
    }
    private static string? PackVersion(JsonElement element) => element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 3 &&
        element.EnumerateArray().All(value => value.TryGetInt32(out var number) && number is >= 0 and <= 1_000_000)
            ? string.Join('.', element.EnumerateArray().Select(value => value.GetInt32())) : null;

    internal static string CompareVersions(string? required, string? installed) => required is null || installed is null ? "Unknown" :
        required.Equals(installed, StringComparison.OrdinalIgnoreCase) ? "Match" : "Mismatch";
    internal static string CompareAddOns(IReadOnlyList<RequiredGameAddOn> required, IReadOnlyList<RequiredGameAddOn>? installed) => installed is null ? "Unknown" :
        required.Select(item => (item.Id, item.Version, item.Type)).OrderBy(item => item.Id).SequenceEqual(
            installed.Select(item => (item.Id, item.Version, item.Type)).OrderBy(item => item.Id)) ? "Match" : "Mismatch";
    internal static bool ValidRequirements(GameRequirements? value, Guid profileId, string kind) => value is not null &&
        value.ProfileId == profileId && value.Kind == kind && Supported(value.Kind) && value.GameName == GameName(kind) &&
        value.VersionSource is "Observed" or "OwnerReported" or "Unknown" &&
        (value.RequiredVersion is null ? value.VersionSource == "Unknown" : ValidVersion(value.RequiredVersion) && value.VersionSource != "Unknown") &&
        value.AddOnState is "Known" or "Unknown" or "NotReviewed" && value.Guidance is { Length: > 0 and <= 400 } &&
        (value.AddOnState == "Known" || value.AddOns?.Count == 0) &&
        value.AddOns is { Count: <= MaximumAddOns } && value.AddOns.All(item => item is not null && item.Id is { Length: > 0 and <= 80 } &&
            item.Name is { Length: > 0 and <= 100 } && Regex.IsMatch(item.Id, "^[A-Za-z0-9_-]{1,80}$") && !item.Name.Any(char.IsControl) &&
            !item.Name.Contains('/') && !item.Name.Contains('\\') && ValidVersion(item.Version) &&
            item.Type is "FactorioMod" or "BedrockBehaviorPack" or "BedrockResourcePack") &&
        value.AddOns.Select(item => (item.Id, item.Type)).Distinct().Count() == value.AddOns.Count;
    internal static GameRequirementsResult Failure(string code, string message) => new(false, code, message);

    internal static bool ReadFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException;
    internal static FileStream OpenReviewed(string path, long maximum)
    {
        EnsurePlainAncestors(path);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 || input.Length > maximum) { input.Dispose(); throw new InvalidDataException(); }
        return input;
    }
    internal static void EnsurePlainAncestors(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException();
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
    }
    internal static byte[] ReadSmall(string path, int maximum, CancellationToken ct)
    {
        using var input = OpenReviewed(path, maximum);
        var bytes = new byte[(int)input.Length];
        ct.ThrowIfCancellationRequested(); input.ReadExactly(bytes); ct.ThrowIfCancellationRequested(); return bytes;
    }
    private static byte[] ReadEntry(ZipArchiveEntry entry, int maximum, CancellationToken ct)
    {
        if (entry.Length is < 1 || entry.Length > maximum) throw new InvalidDataException();
        using var input = entry.Open(); var bytes = new byte[(int)entry.Length];
        ct.ThrowIfCancellationRequested(); input.ReadExactly(bytes); ct.ThrowIfCancellationRequested(); return bytes;
    }
}

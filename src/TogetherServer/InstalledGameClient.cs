using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TogetherServer;

internal sealed record InstalledClientMetadata(string? Version, string Source, IReadOnlyList<RequiredGameAddOn>? AddOns = null);

internal static class InstalledGameClient
{
    internal static bool HasInstalledSteamClient(string fixedUri, CancellationToken ct)
    {
        var id = fixedUri switch { "steam://run/892970" => "892970", "steam://run/427520" => "427520", "steam://run/105600" => "105600", _ => null };
        if (id is null) return false;
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var root = steam?.GetValue("SteamPath") as string;
            if (root is null || !Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            var libraries = new List<string> { root };
            var librariesPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(librariesPath))
            {
                var text = System.Text.Encoding.UTF8.GetString(GameCompatibility.ReadSmall(librariesPath, 64 * 1024, ct));
                var paths = Regex.Matches(text, "\"path\"\\s*\"([^\"]{1,512})\"", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (paths.Count > 32) return false;
                libraries.AddRange(paths.Select(match => match.Groups[1].Value.Replace("\\\\", "\\"))
                    .Where(path => Path.IsPathFullyQualified(path) && !path.StartsWith("\\\\", StringComparison.Ordinal)));
            }
            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase).Take(32))
            {
                ct.ThrowIfCancellationRequested();
                var file = Path.Combine(library, "steamapps", $"appmanifest_{id}.acf");
                if (!File.Exists(file)) continue;
                var text = System.Text.Encoding.UTF8.GetString(GameCompatibility.ReadSmall(file, 16 * 1024, ct));
                string? Field(string key)
                {
                    var matches = Regex.Matches(text, $"\"{key}\"\\s*\"([^\"]{{1,100}})\"", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    return matches.Count == 1 ? matches[0].Groups[1].Value : null;
                }
                var folder = Field("installdir");
                if (Field("appid") != id || Field("StateFlags") != "4" || folder is null || !Regex.IsMatch(folder, "^[A-Za-z0-9 _-]{1,100}$")) continue;
                var installed = Path.Combine(library, "steamapps", "common", folder);
                var executable = id switch { "892970" => Path.Combine(installed, "valheim.exe"), "427520" => Path.Combine(installed, "bin", "x64", "factorio.exe"), _ => Path.Combine(installed, "Terraria.exe") };
                using var lease = GameCompatibility.OpenReviewed(executable, 512L * 1024 * 1024);
                return true;
            }
        }
        catch (Exception ex) when (GameCompatibility.ReadFailure(ex) || ex is System.Security.SecurityException) { }
        return false;
    }
    internal static InstalledClientMetadata Observe(string kind, CancellationToken ct)
    {
        if (kind is not (GameKinds.Factorio or GameKinds.Terraria)) return new(null, "Unknown");
        try
        {
            // Steam's fixed per-user install metadata is the only discovery input.
            // Build IDs and a file's mere existence are never reported as game versions.
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var root = steam?.GetValue("SteamPath") as string;
            if (root is null || !Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return new(null, "Unknown");
            var libraries = new List<string> { root };
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
            {
                var vdf = System.Text.Encoding.UTF8.GetString(GameCompatibility.ReadSmall(libraryFile, 64 * 1024, ct));
                var paths = Regex.Matches(vdf, "\"path\"\\s*\"([^\"]{1,512})\"", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (paths.Count > 32) return new(null, "Unknown");
                libraries.AddRange(paths.Select(match => match.Groups[1].Value.Replace("\\\\", "\\"))
                    .Where(path => Path.IsPathFullyQualified(path) && !path.StartsWith("\\\\", StringComparison.Ordinal)));
            }
            var id = kind == GameKinds.Factorio ? "427520" : "105600";
            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase).Take(32))
            {
                ct.ThrowIfCancellationRequested();
                var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{id}.acf");
                if (!File.Exists(manifestPath)) continue;
                var text = System.Text.Encoding.UTF8.GetString(GameCompatibility.ReadSmall(manifestPath, 16 * 1024, ct));
                string? Field(string key)
                {
                    var found = Regex.Matches(text, $"\"{key}\"\\s*\"([^\"]{{1,100}})\"", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    return found.Count == 1 ? found[0].Groups[1].Value : null;
                }
                var folder = Field("installdir");
                if (Field("appid") != id || folder is null || !Regex.IsMatch(folder, "^[A-Za-z0-9 _-]{1,100}$")) continue;
                var gameRoot = Path.Combine(library, "steamapps", "common", folder);
                var executable = kind == GameKinds.Factorio ? Path.Combine(gameRoot, "bin", "x64", "factorio.exe") : Path.Combine(gameRoot, "Terraria.exe");
                var version = GameCompatibility.ReadExecutableVersion(executable, kind, ct);
                IReadOnlyList<RequiredGameAddOn>? mods = null;
                if (kind == GameKinds.Factorio)
                {
                    // A customized mod directory is not inferred from launch options.
                    // Only the documented default client directory can be compared here.
                    try { mods = GameCompatibility.ReadFactorioMods(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Factorio", "mods"), ct, Path.Combine(gameRoot, "data")); }
                    catch (Exception ex) when (GameCompatibility.ReadFailure(ex)) { }
                }
                return new(version, version is null ? "Unknown" : "Observed", mods);
            }
        }
        catch (Exception ex) when (GameCompatibility.ReadFailure(ex) || ex is System.Security.SecurityException or System.ComponentModel.Win32Exception) { }
        return new(null, "Unknown");
    }
}

internal static class ManualClientVersionStore
{
    private sealed record SavedVersion(Guid HostId, Guid ProfileId, string Kind, string Version);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Name(Guid hostId, Guid profileId) => $"client-version-{hostId:N}-{profileId:N}.protected";
    internal static void Save(LocalData data, Guid hostId, Guid profileId, string kind, string? version)
    {
        var name = Name(hostId, profileId);
        if (version is null) { data.DeleteProtected(name); return; }
        if (!GameCompatibility.ValidVersion(version)) throw new InvalidDataException();
        data.SaveProtected(name, JsonSerializer.SerializeToUtf8Bytes(new SavedVersion(hostId, profileId, kind, version), Json));
    }
    internal static string? Read(LocalData data, Guid hostId, Guid profileId, string kind)
    {
        try
        {
            var name = Name(hostId, profileId);
            if (!data.HasProtected(name)) return null;
            _ = GameCompatibility.ReadSmall(Path.Combine(data.RootPath, name), 16 * 1024, CancellationToken.None);
            var bytes = data.LoadProtected(name);
            if (bytes is null || bytes.Length > 4096) return null;
            var value = JsonSerializer.Deserialize<SavedVersion>(bytes, Json);
            return value?.HostId == hostId && value.ProfileId == profileId && value.Kind == kind && GameCompatibility.ValidVersion(value.Version)
                ? value.Version : null;
        }
        catch (Exception ex) when (GameCompatibility.ReadFailure(ex) || ex is System.Security.Cryptography.CryptographicException) { return null; }
    }
}

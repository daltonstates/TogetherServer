using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TogetherServer;

public sealed record ValheimInstallation(string ExecutablePath, string Source);
public sealed record ValheimWorld(string Name, string SaveRoot);
public sealed record ValheimDiscoveryResult(IReadOnlyList<ValheimInstallation> Installations,
    IReadOnlyList<ValheimWorld> Worlds);
public sealed record ImportWorldRequest(Guid ProfileId, string SourceSaveRoot, string WorldId);
public sealed record ImportWorldResult(bool Ok, string Code, string Message, string? WorldDirectory);

public static partial class ValheimSetup
{
    private const string DedicatedServerAppId = "896660";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static ValheimDiscoveryResult Scan(IEnumerable<string>? extraSaveRoots = null)
    {
        var steamRoots = new HashSet<string>(PathComparer);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = drive.RootDirectory.FullName;
            foreach (var relative in new[] { "Steam", "SteamLibrary", "Program Files (x86)\\Steam",
                         "Program Files\\Steam", "" })
                steamRoots.Add(Path.Combine(root, relative));
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path) steamRoots.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var saveRoots = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            saveRoots.Add(Path.Combine(profile, "AppData", "LocalLow", "IronGate", "Valheim"));
        if (extraSaveRoots is not null) saveRoots.AddRange(extraSaveRoots);
        return ScanRoots(steamRoots, saveRoots);
    }

    // Public for disposable discovery checks. Roots are checked directly; no drive-wide recursive search.
    public static ValheimDiscoveryResult ScanRoots(IEnumerable<string> steamRoots, IEnumerable<string> saveRoots)
    {
        var libraries = new HashSet<string>(PathComparer);
        foreach (var root in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) continue;
            var full = Path.GetFullPath(root);
            libraries.Add(full);
            var vdf = Path.Combine(full, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(vdf)) continue;
                foreach (Match match in LibraryPathRegex().Matches(File.ReadAllText(vdf)))
                {
                    var library = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (Path.IsPathFullyQualified(library)) libraries.Add(Path.GetFullPath(library));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var installations = new List<ValheimInstallation>();
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            var manifest = Path.Combine(apps, $"appmanifest_{DedicatedServerAppId}.acf");
            try
            {
                if (File.Exists(manifest))
                {
                    var match = InstallDirRegex().Match(File.ReadAllText(manifest));
                    var dir = match.Success ? match.Groups[1].Value : "";
                    if (dir.Length > 0 && dir == Path.GetFileName(dir))
                        AddInstallation(Path.Combine(apps, "common", dir, "valheim_server.exe"), "Steam manifest");
                }
                // SteamCMD and older libraries may have the files without a Steam manifest.
                AddInstallation(Path.Combine(apps, "common", "Valheim dedicated server", "valheim_server.exe"),
                    "Common Steam path");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var worlds = new List<ValheimWorld>();
        var seenWorlds = new HashSet<string>(PathComparer);
        foreach (var saveRoot in saveRoots)
        {
            if (string.IsNullOrWhiteSpace(saveRoot) || !Path.IsPathFullyQualified(saveRoot)) continue;
            var full = Path.GetFullPath(saveRoot);
            var folder = Path.Combine(full, "worlds_local");
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var db in Directory.EnumerateFiles(folder, "*.db"))
                {
                    var name = Path.GetFileNameWithoutExtension(db);
                    if (!ValidWorldId(name) || !HasWorldPair(full, name)) continue;
                    if (seenWorlds.Add(full + "|" + name)) worlds.Add(new ValheimWorld(name, full));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new ValheimDiscoveryResult(installations.OrderBy(i => i.ExecutablePath).ToList(),
            worlds.OrderBy(w => w.Name).ThenBy(w => w.SaveRoot).ToList());

        void AddInstallation(string path, string source)
        {
            if (File.Exists(path) && !installations.Any(i => PathComparer.Equals(i.ExecutablePath, path)))
                installations.Add(new ValheimInstallation(path, source));
        }
    }

    public static bool ValidWorldId(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 64 &&
        name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public static bool HasWorldPair(string saveRoot, string worldId) => ValidWorldId(worldId) &&
        File.Exists(Path.Combine(saveRoot, "worlds_local", worldId + ".db")) &&
        File.Exists(Path.Combine(saveRoot, "worlds_local", worldId + ".fwl"));

    public static bool HasAnyWorldFile(string saveRoot, string worldId) =>
        File.Exists(Path.Combine(saveRoot, "worlds_local", worldId + ".db")) ||
        File.Exists(Path.Combine(saveRoot, "worlds_local", worldId + ".fwl")) ||
        File.Exists(Path.Combine(saveRoot, "worlds", worldId + ".db")) ||
        File.Exists(Path.Combine(saveRoot, "worlds", worldId + ".fwl"));

    public static bool IsImportedWorld(LocalData data, Guid profileId, string saveRoot) =>
        Path.GetFullPath(saveRoot).Equals(Path.Combine(data.WorldImportsRoot, profileId.ToString("N")),
            StringComparison.OrdinalIgnoreCase);

    public static ImportWorldResult ImportCopy(LocalData data, ImportWorldRequest request)
    {
        if (request.ProfileId == Guid.Empty || !ValidWorldId(request.WorldId) ||
            string.IsNullOrWhiteSpace(request.SourceSaveRoot) || !Path.IsPathFullyQualified(request.SourceSaveRoot))
            return new(false, "InvalidWorld", "Select a valid local save root and world name.", null);
        string sourceRoot;
        try { sourceRoot = Path.GetFullPath(request.SourceSaveRoot); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(false, "InvalidWorld", "The local save root is not a valid path.", null); }
        var sourceFolder = Path.Combine(sourceRoot, "worlds_local");
        var db = Path.Combine(sourceFolder, request.WorldId + ".db");
        var fwl = Path.Combine(sourceFolder, request.WorldId + ".fwl");
        if (!HasWorldPair(sourceRoot, request.WorldId))
            return new(false, "MissingWorldPair", "The local save needs both .db and .fwl files. Move cloud saves to Local in Valheim first.", null);

        var imports = data.WorldImportsRoot;
        var target = Path.Combine(imports, request.ProfileId.ToString("N"));
        if (Directory.Exists(target))
            return new(false, "AlreadyImported", "This profile already has an imported copy. Add another profile to import again.", null);
        var staging = Path.Combine(imports, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Hold both source files against writes for the entire copy. The original is never modified.
            using var dbSource = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.None);
            using var fwlSource = new FileStream(fwl, FileMode.Open, FileAccess.Read, FileShare.None);
            if (dbSource.Length == 0 || fwlSource.Length == 0)
                return new(false, "EmptyWorldFile", "The selected world has an empty save file.", null);
            Directory.CreateDirectory(Path.Combine(staging, "worlds_local"));
            using (var dest = new FileStream(Path.Combine(staging, "worlds_local", request.WorldId + ".db"), FileMode.CreateNew))
                dbSource.CopyTo(dest);
            using (var dest = new FileStream(Path.Combine(staging, "worlds_local", request.WorldId + ".fwl"), FileMode.CreateNew))
                fwlSource.CopyTo(dest);
            Directory.Move(staging, target);
            return new(true, "WorldImported", "A separate copy is ready. The source save was left untouched.", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "ImportFailed", "Could not copy the world: " + ex.Message, null);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* Only a newly created staging folder is left for owner inspection. */ }
        }
    }

    [GeneratedRegex("\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
    [GeneratedRegex("\"installdir\"\\s*\"([^\"\\\\/]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();
}

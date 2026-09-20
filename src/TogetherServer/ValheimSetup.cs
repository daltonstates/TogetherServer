using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TogetherServer;

public sealed record ValheimInstallation(string ExecutablePath, string Source);
public sealed record ValheimWorld(string Name, string SaveRoot, string SourceFolder, string Format);
public sealed record ValheimDiscoveryResult(IReadOnlyList<ValheimInstallation> Installations,
    IReadOnlyList<ValheimInstallation> Clients, IReadOnlyList<ValheimWorld> Worlds);
public sealed record WorldFileSelection(bool Ok, string Code, string Message, string? WorldId, string? SourceSaveRoot,
    string SourceFolder = "worlds_local");
public sealed record ImportWorldRequest(Guid ProfileId, string SourceSaveRoot, string WorldId,
    string SourceFolder = "worlds_local");
public sealed record ImportWorldResult(bool Ok, string Code, string Message, string? WorldDirectory);

public static partial class ValheimSetup
{
    private const string DedicatedServerAppId = "896660";
    private const string GameAppId = "892970";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static ValheimDiscoveryResult Scan(IEnumerable<string>? extraSaveRoots = null)
    {
        var steamRoots = new List<string>();
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
        var driveRoots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
                    driveRoots.Add(drive.RootDirectory.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* An unavailable drive does not prevent scanning the others. */ }
        }
        return ScanDriveRoots(driveRoots, steamRoots, saveRoots);
    }

    // Only the root and its immediate folders are inspected. Browse handles arbitrary deeper locations.
    public static ValheimDiscoveryResult ScanDriveRoots(IEnumerable<string> driveRoots,
        IEnumerable<string> extraSteamRoots, IEnumerable<string> extraSaveRoots)
    {
        var steamRoots = new HashSet<string>(extraSteamRoots, PathComparer);
        var saveRoots = new HashSet<string>(extraSaveRoots, PathComparer);
        foreach (var driveRoot in driveRoots)
        {
            if (string.IsNullOrWhiteSpace(driveRoot) || !Path.IsPathFullyQualified(driveRoot)) continue;
            var root = Path.GetFullPath(driveRoot);
            foreach (var relative in new[] { "Steam", "SteamLibrary", "Program Files (x86)\\Steam",
                         "Program Files\\Steam", "" })
                steamRoots.Add(Path.Combine(root, relative));
            saveRoots.Add(root);
            try
            {
                foreach (var folder in Directory.EnumerateDirectories(root))
                {
                    if (Directory.Exists(Path.Combine(folder, "steamapps"))) steamRoots.Add(folder);
                    if (Directory.Exists(Path.Combine(folder, "worlds_local"))) saveRoots.Add(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { /* An inaccessible drive does not prevent browsing another drive. */ }
        }
        return ScanRoots(steamRoots, saveRoots);
    }

    // Public for disposable discovery checks. Roots are checked directly; no drive-wide recursive search.
    public static ValheimDiscoveryResult ScanRoots(IEnumerable<string> steamRoots, IEnumerable<string> saveRoots)
    {
        var libraries = new HashSet<string>(PathComparer);
        var cloudWorldRoots = new HashSet<string>(PathComparer);
        foreach (var root in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) continue;
            var full = Path.GetFullPath(root);
            libraries.Add(full);
            var userdata = Path.Combine(full, "userdata");
            try
            {
                if (Directory.Exists(userdata))
                    foreach (var account in Directory.EnumerateDirectories(userdata))
                        cloudWorldRoots.Add(Path.Combine(account, "892970", "remote"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
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
        var clients = new List<ValheimInstallation>();
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
                var gameManifest = Path.Combine(apps, $"appmanifest_{GameAppId}.acf");
                if (File.Exists(gameManifest))
                {
                    var match = InstallDirRegex().Match(File.ReadAllText(gameManifest));
                    var dir = match.Success ? match.Groups[1].Value : "";
                    if (dir.Length > 0 && dir == Path.GetFileName(dir))
                        AddClient(Path.Combine(apps, "common", dir, "valheim.exe"), "Steam manifest");
                }
                AddClient(Path.Combine(apps, "common", "Valheim", "valheim.exe"), "Common Steam path");
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
                    AddWorld(name, full, "worlds_local", "Pair");
                }
                foreach (var worldFolder in Directory.EnumerateDirectories(folder))
                {
                    var name = Path.GetFileName(worldFolder);
                    if (HasChunkedWorldFolder(worldFolder)) AddWorld(name, full, "worlds_local", "Folder");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var remoteRoot in cloudWorldRoots)
        {
            var folder = Path.Combine(remoteRoot, "worlds");
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var worldFolder in Directory.EnumerateDirectories(folder))
                {
                    var name = Path.GetFileName(worldFolder);
                    if (HasChunkedWorldFolder(worldFolder)) AddWorld(name, remoteRoot, "worlds", "Steam cloud folder");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new ValheimDiscoveryResult(installations.OrderBy(i => i.ExecutablePath).ToList(),
            clients.OrderBy(i => i.ExecutablePath).ToList(),
            worlds.OrderBy(w => w.Name).ThenBy(w => w.SaveRoot).ToList());

        void AddWorld(string name, string root, string sourceFolder, string format)
        {
            if (seenWorlds.Add(root + "|" + sourceFolder + "|" + name))
                worlds.Add(new ValheimWorld(name, root, sourceFolder, format));
        }

        void AddInstallation(string path, string source)
        {
            if (File.Exists(path) && !installations.Any(i => PathComparer.Equals(i.ExecutablePath, path)))
                installations.Add(new ValheimInstallation(path, source));
        }

        void AddClient(string path, string source)
        {
            if (File.Exists(path) && !clients.Any(i => PathComparer.Equals(i.ExecutablePath, path)))
                clients.Add(new ValheimInstallation(path, source));
        }
    }

    public static bool ValidWorldId(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 64 &&
        name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public static WorldFileSelection SelectWorldFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return new(false, "InvalidWorldFile", "Choose a local .db or .fwl world file.", null, null);
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(false, "InvalidWorldFile", "The selected world file path is invalid.", null, null); }
        if (!File.Exists(full) || (Path.GetExtension(full).ToLowerInvariant() is not (".db" or ".fwl")))
            return new(false, "InvalidWorldFile", "Choose an existing Valheim .db or .fwl file.", null, null);
        var worldId = Path.GetFileNameWithoutExtension(full);
        var worldsFolder = Path.GetDirectoryName(full);
        if (!ValidWorldId(worldId) || worldsFolder is null)
            return new(false, "InvalidWorldFile", "The selected world filename is invalid.", null, null);
        if (!Path.GetFileName(worldsFolder).Equals("worlds_local", StringComparison.OrdinalIgnoreCase))
            return new(false, "UnsupportedWorldFolder",
                "Select a file inside worlds_local. For a legacy or cloud save, use Valheim's Manage Saves > Move to Local first.", null, null);
        var saveRoot = Path.GetDirectoryName(worldsFolder);
        if (saveRoot is null || !HasWorldPair(saveRoot, worldId))
            return new(false, "MissingWorldPair", "The selected world needs matching .db and .fwl files in worlds_local.", null, null);
        return new(true, "WorldSelected", "World save pair found.", worldId, saveRoot);
    }

    public static WorldFileSelection SelectWorldFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return new(false, "InvalidWorldFolder", "Choose a Valheim world folder.", null, null);
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(false, "InvalidWorldFolder", "The selected world folder path is invalid.", null, null); }
        var worldId = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full);
        var sourceFolder = parent is null ? null : NormalizeSourceFolder(Path.GetFileName(parent));
        var saveRoot = parent is null ? null : Path.GetDirectoryName(parent);
        if (!ValidWorldId(worldId) || saveRoot is null || sourceFolder is null)
            return new(false, "UnsupportedWorldFolder", "Choose the world folder inside worlds_local or Steam's remote/worlds folder.", null, null);
        if (!HasChunkedWorldFolder(full))
            return new(false, "IncompleteWorldFolder", "This folder needs a matching latest _main revision and chunk files. No copy was made.", null, null);
        return new(true, "WorldSelected", "Chunked world folder found. The copy will leave it untouched.",
            worldId, saveRoot, sourceFolder);
    }

    public static bool HasWorldPair(string saveRoot, string worldId) => ValidWorldId(worldId) &&
        File.Exists(Path.Combine(saveRoot, "worlds_local", worldId + ".db")) &&
        File.Exists(Path.Combine(saveRoot, "worlds_local", worldId + ".fwl"));

    public static bool HasChunkedWorldFolder(string worldFolder)
    {
        try { return GetChunkedFiles(worldFolder) is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    public static bool HasWorldData(string saveRoot, string worldId) => ValidWorldId(worldId) &&
        (HasWorldPair(saveRoot, worldId) ||
         HasChunkedWorldFolder(Path.Combine(saveRoot, "worlds_local", worldId)));

    public static bool HasAnyWorldFile(string saveRoot, string worldId) => ValidWorldId(worldId) && (
        File.Exists(Path.Combine(saveRoot, "worlds_local", worldId + ".db")) ||
        File.Exists(Path.Combine(saveRoot, "worlds_local", worldId + ".fwl")) ||
        Directory.Exists(Path.Combine(saveRoot, "worlds_local", worldId)) ||
        File.Exists(Path.Combine(saveRoot, "worlds", worldId + ".db")) ||
        File.Exists(Path.Combine(saveRoot, "worlds", worldId + ".fwl")) ||
        Directory.Exists(Path.Combine(saveRoot, "worlds", worldId)));

    public static bool IsImportedWorld(LocalData data, Guid profileId, string saveRoot) =>
        Path.GetFullPath(saveRoot).Equals(Path.Combine(data.WorldImportsRoot, profileId.ToString("N")),
            StringComparison.OrdinalIgnoreCase);

    public static ImportWorldResult ImportCopy(LocalData data, ImportWorldRequest request)
    {
        var sourceFolderName = NormalizeSourceFolder(request.SourceFolder);
        if (request.ProfileId == Guid.Empty || !ValidWorldId(request.WorldId) ||
            string.IsNullOrWhiteSpace(request.SourceSaveRoot) || !Path.IsPathFullyQualified(request.SourceSaveRoot) ||
            sourceFolderName is null)
            return new(false, "InvalidWorld", "Select a valid local save root and world name.", null);
        string sourceRoot;
        try { sourceRoot = Path.GetFullPath(request.SourceSaveRoot); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(false, "InvalidWorld", "The local save root is not a valid path.", null); }
        var sourceFolder = Path.Combine(sourceRoot, sourceFolderName);
        var db = Path.Combine(sourceFolder, request.WorldId + ".db");
        var fwl = Path.Combine(sourceFolder, request.WorldId + ".fwl");
        var chunkFolder = Path.Combine(sourceFolder, request.WorldId);
        var hasFolder = Directory.Exists(chunkFolder);
        var hasPair = sourceFolderName == "worlds_local" && HasWorldPair(sourceRoot, request.WorldId);
        if (hasFolder && hasPair)
            return new(false, "AmbiguousWorld", "This name has both a world folder and a .db/.fwl pair. Choose a source containing one format.", null);
        if (hasFolder && !HasChunkedWorldFolder(chunkFolder))
            return new(false, "IncompleteWorldFolder", "The world folder has no complete latest revision and chunk files. No copy was made.", null);
        if (!hasFolder && !hasPair)
            return new(false, "MissingWorldPair", "No complete .db/.fwl pair or chunked world folder was found.", null);

        var imports = data.WorldImportsRoot;
        var target = Path.Combine(imports, request.ProfileId.ToString("N"));
        if (Directory.Exists(target))
            return new(false, "AlreadyImported", "This profile already has an imported copy. Add another profile to import again.", null);
        var staging = Path.Combine(imports, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (hasFolder)
            {
                var files = GetChunkedFiles(chunkFolder);
                if (files is null)
                    return new(false, "IncompleteWorldFolder", "The source world changed before copying. No copy was made.", null);
                var sources = new List<FileStream>();
                try
                {
                    // Keep every chunk locked against writes while copying a complete snapshot.
                    foreach (var file in files)
                        sources.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None));
                    var destination = Path.Combine(staging, "worlds_local", request.WorldId);
                    Directory.CreateDirectory(destination);
                    for (var index = 0; index < files.Length; index++)
                    {
                        using var output = new FileStream(Path.Combine(destination, Path.GetFileName(files[index])), FileMode.CreateNew);
                        sources[index].CopyTo(output);
                    }
                    var after = GetChunkedFiles(chunkFolder);
                    if (after is null || !files.SequenceEqual(after, PathComparer))
                        return new(false, "SourceChanged", "The source world changed during copying. No copy was kept.", null);
                }
                finally { foreach (var source in sources) source.Dispose(); }
            }
            else
            {
                // Hold both legacy files against writes for the entire copy.
                using var dbSource = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.None);
                using var fwlSource = new FileStream(fwl, FileMode.Open, FileAccess.Read, FileShare.None);
                if (dbSource.Length == 0 || fwlSource.Length == 0)
                    return new(false, "EmptyWorldFile", "The selected world has an empty save file.", null);
                Directory.CreateDirectory(Path.Combine(staging, "worlds_local"));
                using (var dest = new FileStream(Path.Combine(staging, "worlds_local", request.WorldId + ".db"), FileMode.CreateNew))
                    dbSource.CopyTo(dest);
                using (var dest = new FileStream(Path.Combine(staging, "worlds_local", request.WorldId + ".fwl"), FileMode.CreateNew))
                    fwlSource.CopyTo(dest);
            }
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

    private static string[]? GetChunkedFiles(string worldFolder)
    {
        if (!Directory.Exists(worldFolder) || !ValidWorldId(Path.GetFileName(worldFolder)) ||
            File.GetAttributes(worldFolder).HasFlag(FileAttributes.ReparsePoint) ||
            Directory.EnumerateDirectories(worldFolder).Any()) return null;
        var files = Directory.GetFiles(worldFolder).OrderBy(path => path, PathComparer).ToArray();
        if (files.Length == 0 || files.Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))) return null;
        var revisions = files.Select(path => MainFileRegex().Match(Path.GetFileName(path)))
            .Where(match => match.Success && long.TryParse(match.Groups[1].Value, out _))
            .Select(match => long.Parse(match.Groups[1].Value)).ToArray();
        if (revisions.Length == 0) return null;
        var latest = revisions.Max().ToString();
        foreach (var extension in new[] { "db2", "fwl2", "chunks", "ok" })
        {
            var main = files.FirstOrDefault(path => Path.GetFileName(path).Equals($"_main.{latest}.{extension}",
                StringComparison.OrdinalIgnoreCase));
            if (main is null || (extension != "ok" && new FileInfo(main).Length == 0)) return null;
        }
        if (!files.Any(path => path.EndsWith(".chunk", StringComparison.OrdinalIgnoreCase) &&
                               new FileInfo(path).Length > 0)) return null;
        return files;
    }

    private static string? NormalizeSourceFolder(string? name) =>
        name?.Equals("worlds_local", StringComparison.OrdinalIgnoreCase) == true ? "worlds_local" :
        name?.Equals("worlds", StringComparison.OrdinalIgnoreCase) == true ? "worlds" : null;

    [GeneratedRegex("\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
    [GeneratedRegex("\"installdir\"\\s*\"([^\"\\\\/]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();
    [GeneratedRegex(@"^_main\.(\d+)\.(db2|fwl2|chunks|ok)$", RegexOptions.IgnoreCase)]
    private static partial Regex MainFileRegex();
}

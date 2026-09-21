using System.IO.Compression;

namespace TogetherServer;

public sealed record MinecraftInstallation(string Kind, string ServerDirectory, string ArtifactPath,
    string ExecutablePath, string WorldName, int GamePort, string Source, string Note);
public sealed record MinecraftDiscoveryResult(IReadOnlyList<MinecraftInstallation> Installations, string JavaRuntimePath);

public static class MinecraftSetup
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public static MinecraftDiscoveryResult Scan(LocalData data, IEnumerable<ServerProfile> profiles, string? extraFolder = null)
    {
        var roots = new List<string> { data.MinecraftInstallRoot };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            roots.AddRange([Path.Combine(home, "Desktop"), Path.Combine(home, "Documents"),
                Path.Combine(home, "Downloads"), Path.Combine(home, "MinecraftServer"),
                Path.Combine(home, "Minecraft")]);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "MinecraftServer"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Minecraft"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Servers"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        roots.AddRange(profiles.Where(profile => profile.Kind is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock)
            .Select(profile => profile.WorldDirectory));
        if (!string.IsNullOrWhiteSpace(extraFolder)) roots.Add(extraFolder);
        return ScanRoots(roots, FindJava(data.MinecraftRuntimeRoot, profiles));
    }

    // Inspect only named roots and their immediate folders. Browse handles other locations.
    public static MinecraftDiscoveryResult ScanRoots(IEnumerable<string> roots, string javaPath)
    {
        var installations = new List<MinecraftInstallation>();
        var folders = new HashSet<string>(Paths);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) continue;
            try
            {
                var full = Path.GetFullPath(root);
                if (!Directory.Exists(full)) continue;
                folders.Add(full);
                foreach (var child in Directory.EnumerateDirectories(full).Take(200))
                    if (!IsLink(child)) folders.Add(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        foreach (var folder in folders)
        {
            try
            {
                var bedrock = Path.Combine(folder, "bedrock_server.exe");
                if (File.Exists(bedrock)) Add(GameKinds.MinecraftBedrock, bedrock, bedrock);
                foreach (var jar in Directory.EnumerateFiles(folder, "*.jar").Take(100))
                {
                    if (!LooksLikeServerJar(jar, folder)) continue;
                    Add(GameKinds.MinecraftJava, jar, javaPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

            void Add(string kind, string artifact, string executable)
            {
                var properties = ReadProperties(folder);
                var world = properties.TryGetValue("level-name", out var name) && !string.IsNullOrWhiteSpace(name) ? name : "world";
                var fallback = kind == GameKinds.MinecraftJava ? 25565 : 19132;
                var port = properties.TryGetValue("server-port", out var raw) && int.TryParse(raw, out var parsed) && parsed is >= 1 and <= 65535 ? parsed : fallback;
                var prepared = properties.ContainsKey("level-name") && properties.ContainsKey("server-port");
                var note = !prepared ? "Needs server.properties with level-name and server-port" :
                    kind == GameKinds.MinecraftJava && !HasJavaEula(folder) ? "Needs EULA acceptance in eula.txt" :
                    kind == GameKinds.MinecraftJava && !File.Exists(executable) ? "Choose or install Java" :
                    kind == GameKinds.MinecraftJava ? "Java compatibility has not been checked" : "Ready to select";
                installations.Add(new(kind, folder, artifact, executable, world, port,
                    folder.Contains("minecraft-servers", StringComparison.OrdinalIgnoreCase) ? "Installed in TogetherServer" : "Found on this PC", note));
            }
        }
        return new(installations.OrderBy(item => item.Kind).ThenBy(item => item.ServerDirectory).ThenBy(item => item.ArtifactPath).ToList(), javaPath);
    }

    public static MinecraftDiscoveryResult ScanFolder(string folder, string javaPath) => ScanRoots([folder], javaPath);

    internal static Dictionary<string, string> ReadProperties(string folder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(folder, "server.properties")))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var separator = trimmed.IndexOf('=');
                if (separator > 0) result[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static bool HasJavaEula(string folder)
    {
        try { return File.ReadLines(Path.Combine(folder, "eula.txt")).Any(line => line.Trim().Equals("eula=true", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool LooksLikeServerJar(string jar, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(jar);
        if (name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("client", StringComparison.OrdinalIgnoreCase)) return false;
        var recognized = new[] { "server", "minecraft", "paper", "purpur", "spigot", "bukkit", "fabric", "forge", "neoforge", "quilt" }
            .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
        if (!recognized && !File.Exists(Path.Combine(folder, "server.properties"))) return false;
        try
        {
            if (new FileInfo(jar).Length is < 100 or > 300_000_000) return false;
            using var zip = ZipFile.OpenRead(jar);
            var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
            if (manifest is null || manifest.Length > 64_000) return false;
            using var reader = new StreamReader(manifest.Open());
            return reader.ReadToEnd().Contains("Main-Class:", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string FindJava(string managedRoot, IEnumerable<ServerProfile> profiles)
    {
        try
        {
            if (Directory.Exists(managedRoot))
                foreach (var folder in Directory.EnumerateDirectories(managedRoot).Take(20))
                    foreach (var java in Directory.EnumerateFiles(folder, "java.exe", SearchOption.AllDirectories).Take(1))
                        return java;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var saved in profiles.Where(profile => profile.Kind == GameKinds.MinecraftJava).Select(profile => profile.ExecutablePath))
            if (Path.GetFileName(saved).Equals("java.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(saved)) return saved;
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome) && File.Exists(Path.Combine(javaHome, "bin", "java.exe")))
            return Path.Combine(javaHome, "bin", "java.exe");
        foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Amazon Corretto" })
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var root = Path.Combine(programFiles, vendor);
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var folder in Directory.EnumerateDirectories(root).Take(20))
                {
                    var java = Path.Combine(folder, "bin", "java.exe");
                    if (File.Exists(java)) return java;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var candidate = Path.Combine(path.Trim('"'), "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException) { }
        }
        return "";
    }
}

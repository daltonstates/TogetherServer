using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record MinecraftInstallation(string Kind, string ServerDirectory, string ArtifactPath,
    string ExecutablePath, string WorldName, int GamePort, string Source, string Note);
public sealed record MinecraftDiscoveryResult(IReadOnlyList<MinecraftInstallation> Installations, string JavaRuntimePath);

public static class MinecraftSetup
{
    private sealed record ManagedJavaArtifact(string Version, string Sha1);

    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private const string ManagedJavaMetadataFile = ".togetherserver-java.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] UnsupportedJavaNames =
        ["paper", "purpur", "spigot", "bukkit", "fabric", "forge", "neoforge", "quilt", "installer", "client"];
    private static readonly HashSet<string> VanillaMainClasses = new(StringComparer.Ordinal)
        { "net.minecraft.server.Main", "net.minecraft.bundler.Main" };

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

    public static MinecraftDiscoveryResult ScanManaged(LocalData data, IEnumerable<ServerProfile> profiles) =>
        ScanRoots([data.MinecraftInstallRoot], FindJava(data.MinecraftRuntimeRoot, profiles));

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
                    if (!IsSupportedVanillaServerJar(jar, folder)) continue;
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

    internal static void WriteManagedJavaProvenance(string folder, string version, string sha1)
    {
        if (sha1.Length != 40 || sha1.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Official server JAR checksum is invalid.");
        File.WriteAllText(Path.Combine(folder, ManagedJavaMetadataFile),
            JsonSerializer.Serialize(new ManagedJavaArtifact(version, sha1.ToUpperInvariant()), Json));
    }

    internal static bool IsSupportedVanillaServerJar(string jar, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(jar);
        if (UnsupportedJavaNames.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) return false;
        try
        {
            if (new FileInfo(jar).Length is < 100 or > 300_000_000) return false;
            using var zip = ZipFile.OpenRead(jar);
            var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
            if (manifest is null || manifest.Length > 64_000) return false;
            using var reader = new StreamReader(manifest.Open());
            var mainClass = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2 && parts[0].Trim().Equals("Main-Class", StringComparison.OrdinalIgnoreCase))
                .Select(parts => parts[1].Trim()).SingleOrDefault();
            if (mainClass is null || !VanillaMainClasses.Contains(mainClass)) return false;
            if (zip.Entries.Any(entry => entry.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("io/papermc/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("org/bukkit/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("net/fabricmc/", StringComparison.OrdinalIgnoreCase)))
                return false;

            var metadataPath = Path.Combine(folder, ManagedJavaMetadataFile);
            if (!File.Exists(metadataPath)) return false;
            if (new FileInfo(metadataPath).Length > 4096) return false;
            var provenance = JsonSerializer.Deserialize<ManagedJavaArtifact>(File.ReadAllText(metadataPath), Json);
            if (provenance is null || string.IsNullOrWhiteSpace(provenance.Version) ||
                string.IsNullOrWhiteSpace(provenance.Sha1) || provenance.Sha1.Length != 40 ||
                provenance.Sha1.Any(character => !char.IsAsciiHexDigit(character)) ||
                !Path.GetFileName(jar).Equals("server.jar", StringComparison.OrdinalIgnoreCase)) return false;
            using var input = File.OpenRead(jar);
            return Convert.ToHexString(SHA1.HashData(input)).Equals(provenance.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return false; }
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

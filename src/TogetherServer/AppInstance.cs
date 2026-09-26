using System.Text.Json;

namespace TogetherServer;

public sealed record AppInstanceView(string Kind, string DisplayName, bool IsStaging, bool FreshWorldsOnly,
    bool StartupAvailable, bool UpdatesAvailable, int LocalPort, int CompanionPort,
    int ValheimPort, int MinecraftJavaPort, int MinecraftBedrockPort, string DataRoot, string DataIsolation);

public sealed record InstanceValidation(bool Ok, string Code, string Message);

public sealed class AppInstance
{
    public const string DevelopmentExecutableName = "TogetherServer DEVELOPMENT.exe";
    private const string MarkerFile = ".togetherserver-instance.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record InstanceMarker(int SchemaVersion, string Kind);

    private AppInstance(bool isStaging, string dataRoot, string productionDataRoot)
    {
        IsStaging = isStaging;
        DataRoot = Normalize(dataRoot);
        ProductionDataRoot = Normalize(productionDataRoot);
    }

    public bool IsStaging { get; }
    public string Kind => IsStaging ? "Staging" : "Production";
    public string DisplayName => IsStaging ? "TogetherServer DEVELOPMENT" : "TogetherServer";
    public string DataRoot { get; }
    public string ProductionDataRoot { get; }
    public bool FreshWorldsOnly => IsStaging;
    public bool StartupAvailable => !IsStaging;
    public bool UpdatesAvailable => !IsStaging;
    public int DefaultLocalPort => IsStaging ? 5128 : 5127;
    public int DefaultCompanionPort => IsStaging ? 5132 : 5131;
    public int DefaultValheimPort => IsStaging ? 2458 : 2456;
    public int DefaultMinecraftJavaPort => IsStaging ? 25566 : 25565;
    public int DefaultMinecraftBedrockPort => IsStaging ? 19134 : 19132;

    public static bool RequestsStaging(string[] arguments, string? executablePath = null) =>
        arguments.Contains("--staging", StringComparer.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(executablePath ?? Environment.ProcessPath),
            DevelopmentExecutableName, StringComparison.OrdinalIgnoreCase);

    public static AppInstance Resolve(string[] arguments, Func<string, string?>? environment = null,
        string? localApplicationData = null, string? executablePath = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var local = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var productionRoot = environment("TOGETHERSERVER_DATA_DIR") ?? Path.Combine(local, "TogetherServer");
        var staging = RequestsStaging(arguments, executablePath);
        if (staging && arguments.Contains("--startup", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Development staging cannot start at Windows sign-in. Open it explicitly.");
        var dataRoot = staging
            ? environment("TOGETHERSERVER_STAGING_DATA_DIR") ?? Path.Combine(local, "TogetherServer-Staging")
            : productionRoot;
        var result = new AppInstance(staging, dataRoot, productionRoot);
        if (staging && PathsOverlap(result.DataRoot, result.ProductionDataRoot))
            throw new InvalidDataException("The staging and production data folders must be separate and must not contain one another.");
        return result;
    }

    public void PrepareDataRoot()
    {
        Directory.CreateDirectory(DataRoot);
        if (IsStaging && (File.GetAttributes(DataRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The staging data folder cannot be a link or reparse point.");
        var markerPath = Path.Combine(DataRoot, MarkerFile);
        if (!IsStaging)
        {
            if (File.Exists(markerPath) && ReadMarker(markerPath).Kind.Equals("Staging", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Production refused to open a data folder marked for TogetherServer staging.");
            return;
        }

        if (File.Exists(markerPath))
        {
            var marker = ReadMarker(markerPath);
            if (marker.SchemaVersion != 1 || !marker.Kind.Equals("Staging", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staging data marker is invalid. No local data was opened.");
            return;
        }

        if (Directory.EnumerateFileSystemEntries(DataRoot).Any())
            throw new InvalidDataException("Staging requires a new empty data folder. Existing files were left untouched.");
        File.WriteAllText(markerPath, JsonSerializer.Serialize(new InstanceMarker(1, "Staging"), Json));
    }

    public InstanceValidation ValidateSettings(HostSettings settings)
    {
        if (!IsStaging) return new(true, "ProductionSettings", "Production settings use their normal data boundary.");
        foreach (var profile in settings.Profiles)
        {
            if (profile.Kind == GameKinds.Custom)
                return new(false, "StagingCustomDisabled",
                    "Custom scripts are disabled in staging because they can reference files outside the staging data folder.");
            if (profile.Kind == GameKinds.Valheim && !profile.WorldSource.Equals("New", StringComparison.OrdinalIgnoreCase))
                return new(false, "StagingFreshWorldRequired",
                    "Development creates and keeps worlds in separate storage; it cannot load or copy an existing production world.");
            var saveRoot = profile.Kind is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock
                ? Path.Combine(DataRoot, "minecraft-servers")
                : profile.Kind == GameKinds.Valheim ? Path.Combine(DataRoot, "worlds") : DataRoot;
            if (string.IsNullOrWhiteSpace(profile.WorldDirectory) || !Path.IsPathFullyQualified(profile.WorldDirectory) ||
                !IsSafeDescendant(saveRoot, profile.WorldDirectory))
                return new(false, "StagingDataBoundary",
                    "Every staging server save must stay inside the TogetherServer staging data folder.");
            if (profile.Kind == GameKinds.MinecraftJava &&
                (!string.IsNullOrWhiteSpace(profile.Minecraft?.ServerJarPath) &&
                 !IsSafeDescendant(Path.Combine(DataRoot, "minecraft-servers"), profile.Minecraft.ServerJarPath)))
                return new(false, "StagingDataBoundary",
                    "The staging Minecraft server and its world must stay inside the staging data folder.");
            if (profile.Kind == GameKinds.MinecraftBedrock && !string.IsNullOrWhiteSpace(profile.ExecutablePath) &&
                !IsSafeDescendant(Path.Combine(DataRoot, "minecraft-servers"), profile.ExecutablePath))
                return new(false, "StagingDataBoundary",
                    "The staging Minecraft server and its world must stay inside the staging data folder.");
        }
        return new(true, "StagingSettings", "Staging settings stay inside the isolated data folder.");
    }

    public AppInstanceView View(int localPort) => new(Kind, DisplayName, IsStaging, FreshWorldsOnly,
        StartupAvailable, UpdatesAvailable, localPort, DefaultCompanionPort, DefaultValheimPort,
        DefaultMinecraftJavaPort, DefaultMinecraftBedrockPort, DataRoot,
        IsStaging
            ? "Staging uses a separate data folder. Production profiles, credentials, runs, settings, and world saves are not loaded or copied."
            : "Production uses the normal TogetherServer data folder.");

    public static bool ContainsPath(string root, string candidate)
    {
        try
        {
            var parent = Normalize(root);
            var child = Normalize(candidate);
            return child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSafeDescendant(string root, string candidate)
    {
        if (!ContainsPath(root, candidate)) return false;
        try
        {
            var current = Normalize(root);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
            var relative = Path.GetRelativePath(current, Normalize(candidate));
            foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) && !File.Exists(current)) break;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool PathsOverlap(string left, string right) =>
        ContainsPath(left, right) || ContainsPath(right, left);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static InstanceMarker ReadMarker(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<InstanceMarker>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("The TogetherServer instance marker is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The TogetherServer instance marker is unreadable.", ex);
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record StorageLocationHealth(string Id, string Label, string State, string Detail,
    long? AvailableSpaceBytes = null, long UsedBytes = 0, bool MeasurementTruncated = false);
public sealed record ResourceHealthView(int LogicalProcessors, long ProcessWorkingSetBytes,
    long ManagedMemoryBytes, long? TotalAvailableMemoryBytes, string EvidenceBoundary);
public sealed record StorageHealthView(DateTimeOffset CheckedUtc,
    IReadOnlyList<StorageLocationHealth> Locations, IReadOnlyList<string> Warnings,
    ResourceHealthView Resources);

internal sealed class StorageHealthService(LocalData data, TimeProvider clock)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private StorageHealthView? cached;

    public StorageHealthView Read(HostSettings settings)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            if (cached is not null && now - cached.CheckedUtc < CacheDuration) return cached;
            var locations = new List<StorageLocationHealth>();
            var warnings = new List<string>();
            var dataSpace = FreeSpace(data.RootPath);
            var backupMeasure = MeasureDirectory(data.BackupsRoot);
            var logMeasure = MeasureDirectory(data.LogsRoot);
            locations.Add(Location("app-data", "App data drive", dataSpace,
                "Settings, protected access, and recovery records stay on this drive."));
            locations.Add(new("backups", "Local backups", dataSpace is null ? "Unknown" :
                    dataSpace < 2L * 1024 * 1024 * 1024 ? "Attention" : "Available",
                $"{backupMeasure.FileCount} retained backup file{(backupMeasure.FileCount == 1 ? "" : "s")} measured.",
                dataSpace, backupMeasure.Bytes, backupMeasure.Truncated));
            locations.Add(new("logs", "Server logs", dataSpace is null ? "Unknown" : "Available",
                $"{logMeasure.FileCount} retained log file{(logMeasure.FileCount == 1 ? "" : "s")} measured.",
                dataSpace, logMeasure.Bytes, logMeasure.Truncated));

            if (dataSpace is < 2L * 1024 * 1024 * 1024)
                warnings.Add("The app-data drive has less than 2 GB available. Backups or updates may fail before a game is affected.");
            if (backupMeasure.Truncated)
                warnings.Add("Backup growth exceeded the bounded storage scan. Review retained backups and available drive space.");
            if (logMeasure.Truncated || logMeasure.Bytes > 2L * 1024 * 1024 * 1024)
                warnings.Add("Retained server logs need review because their measured size is large or incomplete.");

            var catalog = data.LoadBackupCatalog();
            foreach (var profile in settings.Profiles)
            {
                var label = string.IsNullOrWhiteSpace(profile.Name) ? "Saved server" : profile.Name.Trim();
                var free = FreeSpace(profile.WorldDirectory);
                locations.Add(Location("world-" + profile.Id.ToString("N"), label + " save drive", free,
                    "Free space is informational and never starts or stops this server."));
                var reserve = Math.Clamp(profile.Backups?.MinimumFreeSpaceMb ?? 1024, 0, 1_048_576) * 1024L * 1024L;
                if (free is { } available && available < reserve)
                    warnings.Add(label + " is below its configured backup free-space reserve.");
                var failure = catalog.Failures.Where(item => item.ProfileId == profile.Id)
                    .OrderByDescending(item => item.FailedUtc).FirstOrDefault();
                var latest = catalog.Records.Where(item => item.ProfileId == profile.Id)
                    .OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
                if (failure is not null && (latest is null || failure.FailedUtc > latest.CreatedUtc))
                    warnings.Add(label + " has a newer backup failure than its last completed backup.");
            }

            var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            using var currentProcess = Process.GetCurrentProcess();
            cached = new(now, locations, warnings.Distinct(StringComparer.Ordinal).Take(20).ToList(),
                new(Environment.ProcessorCount, currentProcess.WorkingSet64,
                    GC.GetTotalMemory(false), memory > 0 ? memory : null,
                    "CPU count and memory are informational only; they never authorize lifecycle actions."));
            return cached;
        }
    }

    private static StorageLocationHealth Location(string id, string label, long? free, string detail) =>
        new(id, label, free is null ? "Unknown" : free < 2L * 1024 * 1024 * 1024 ? "Attention" : "Available",
            detail, free);

    private static long? FreeSpace(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   NotSupportedException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private static (long Bytes, int FileCount, bool Truncated) MeasureDirectory(string root)
    {
        const int maximumFiles = 20_000;
        try
        {
            if (!Directory.Exists(root)) return (0, 0, false);
            long bytes = 0;
            var count = 0;
            var queue = new Queue<DirectoryInfo>();
            queue.Enqueue(new DirectoryInfo(root));
            while (queue.Count > 0)
            {
                var directory = queue.Dequeue();
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var child in directory.EnumerateDirectories())
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0) queue.Enqueue(child);
                foreach (var file in directory.EnumerateFiles())
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (++count > maximumFiles) return (bytes, maximumFiles, true);
                    bytes = checked(bytes + file.Length);
                }
            }
            return (bytes, count, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or
                                   System.Security.SecurityException)
        { return (0, 0, true); }
    }
}

public static class AcceptanceCheckIds
{
    public const string FriendRoute = "FriendRoute";
    public const string RealJoin = "RealJoin";
    public const string PlayerTransition = "PlayerTransition";
    public const string GracefulStop = "GracefulStop";
    public const string SavedRestart = "SavedRestart";
    public const string RestoreDrill = "RestoreDrill";
    public const string SleepResume = "SleepResume";
    public static readonly IReadOnlyList<string> All =
    [FriendRoute, RealJoin, PlayerTransition, GracefulStop, SavedRestart, RestoreDrill, SleepResume];
}

internal sealed class AcceptanceRecord
{
    public Guid ProfileId { get; set; }
    public string ConfigurationFingerprint { get; set; } = "";
    public string GameBinaryFingerprint { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
    public Dictionary<string, DateTimeOffset> Confirmed { get; set; } = [];
}

public sealed record AcceptanceCheckView(string Id, string Label, string Evidence, bool Confirmed,
    DateTimeOffset? ConfirmedUtc);
public sealed record AcceptanceView(Guid ProfileId, bool Stale, DateTimeOffset? UpdatedUtc,
    IReadOnlyList<AcceptanceCheckView> Checks, string EvidenceBoundary,
    bool GameFilesAvailable = false, bool GameFilesChanged = false);
public sealed record AcceptanceChange(string CheckId, bool Confirmed);
public sealed record AcceptanceResult(bool Ok, string Code, string Message, AcceptanceView View);

internal sealed class AcceptanceRecorder(LocalData data, TimeProvider clock)
{
    private const string FileName = "acceptance-records.json";
    private readonly object sync = new();

    public AcceptanceView View(HostSettings settings, Guid profileId)
    {
        lock (sync)
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return Empty(profileId);
            var record = Load().SingleOrDefault(item => item.ProfileId == profileId);
            var binary = GameBinaryIdentity(profile);
            var stale = record is not null && !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.ConfigurationFingerprint),
                Encoding.ASCII.GetBytes(Fingerprint(settings, profile, binary.Fingerprint)));
            return Project(profileId, record, stale, binary);
        }
    }

    public AcceptanceResult Change(HostSettings settings, Guid profileId, AcceptanceChange change)
    {
        lock (sync)
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null)
                return new(false, "UnknownProfile", "Choose a saved server first.", Empty(profileId));
            if (!AcceptanceCheckIds.All.Contains(change.CheckId, StringComparer.Ordinal))
                return new(false, "UnknownAcceptanceCheck", "Choose one reviewed acceptance check.", View(settings, profileId));
            var records = Load();
            var binary = GameBinaryIdentity(profile);
            var fingerprint = Fingerprint(settings, profile, binary.Fingerprint);
            var record = records.SingleOrDefault(item => item.ProfileId == profileId);
            if (record is null || !string.Equals(record.ConfigurationFingerprint, fingerprint, StringComparison.Ordinal))
            {
                records.RemoveAll(item => item.ProfileId == profileId);
                record = new AcceptanceRecord
                {
                    ProfileId = profileId,
                    ConfigurationFingerprint = fingerprint,
                    GameBinaryFingerprint = binary.Fingerprint
                };
                records.Add(record);
            }
            if (change.Confirmed) record.Confirmed[change.CheckId] = clock.GetUtcNow();
            else record.Confirmed.Remove(change.CheckId);
            record.UpdatedUtc = clock.GetUtcNow();
            data.SaveState(FileName, records.OrderBy(item => item.ProfileId).Take(100).ToList());
            data.TryAudit($"acceptance-owner-{(change.Confirmed ? "confirmed" : "cleared")} {profileId} check={change.CheckId} {record.UpdatedUtc:O}");
            return new(true, "AcceptanceRecorded",
                change.Confirmed ? "Owner confirmation recorded for this exact configuration." :
                    "Owner confirmation cleared for this exact configuration.",
                Project(profileId, record, false, binary));
        }
    }

    private List<AcceptanceRecord> Load()
    {
        var records = data.LoadState(FileName, new List<AcceptanceRecord>());
        if (records.Count > 100 || records.Select(item => item?.ProfileId).Distinct().Count() != records.Count ||
            records.Any(item => item is null || item.ProfileId == Guid.Empty ||
                 item.ConfigurationFingerprint.Length != 64 || item.ConfigurationFingerprint.Any(character => !Uri.IsHexDigit(character)) ||
                 (item.GameBinaryFingerprint.Length != 0 && (item.GameBinaryFingerprint.Length != 64 ||
                     item.GameBinaryFingerprint.Any(character => !Uri.IsHexDigit(character)))) ||
                item.Confirmed is null || item.Confirmed.Count > AcceptanceCheckIds.All.Count ||
                item.Confirmed.Keys.Any(key => !AcceptanceCheckIds.All.Contains(key, StringComparer.Ordinal))))
        {
            data.QuarantineState(FileName, "TogetherServer could not safely read the acceptance recorder.", false);
            return [];
        }
        return records;
    }

    private static AcceptanceView Project(Guid profileId, AcceptanceRecord? record, bool stale,
        (string Fingerprint, bool Available) binary)
    {
        var labels = new Dictionary<string, (string Label, string Evidence)>(StringComparer.Ordinal)
        {
            [AcceptanceCheckIds.FriendRoute] = ("Friend route", "A separate Friend PC authenticated through the intended route."),
            [AcceptanceCheckIds.RealJoin] = ("Real game join", "The intended Friend reached the game world, not only the app."),
            [AcceptanceCheckIds.PlayerTransition] = ("Player count 0 to 1 to 0", "Fresh trusted server observations followed the intended join and leave."),
            [AcceptanceCheckIds.GracefulStop] = ("Graceful stop", "The real server saved and exited through its reviewed stop path."),
            [AcceptanceCheckIds.SavedRestart] = ("Saved restart", "A recognizable in-game change remained after restart."),
            [AcceptanceCheckIds.RestoreDrill] = ("Restore drill", "A chosen backup was restored and checked in disposable or owner-approved test storage."),
            [AcceptanceCheckIds.SleepResume] = ("Sleep and resume", "The Host recovered honestly after a deliberate Windows sleep/resume test.")
        };
        var checks = AcceptanceCheckIds.All.Select(id => new AcceptanceCheckView(id, labels[id].Label,
            labels[id].Evidence, !stale && record?.Confirmed.TryGetValue(id, out _) == true,
            !stale && record?.Confirmed.TryGetValue(id, out var recorded) == true ? recorded : null)).ToList();
        return new(profileId, stale, record?.UpdatedUtc, checks,
            "These are owner confirmations, not authentication, player identity, occupancy authority, or automatic proof.",
            binary.Available,
            record?.GameBinaryFingerprint is { Length: 64 } previous &&
                !string.Equals(previous, binary.Fingerprint, StringComparison.Ordinal));
    }

    private static AcceptanceView Empty(Guid profileId) => Project(profileId, null, false, ("", false));

    internal static string Fingerprint(HostSettings settings, ServerProfile profile) =>
        Fingerprint(settings, profile, GameBinaryIdentity(profile).Fingerprint);

    private static string Fingerprint(HostSettings settings, ServerProfile profile, string gameBinaryFingerprint)
    {
        var route = ConnectionRoutes.Normalize(settings.ConnectionRoute);
        var payload = JsonSerializer.Serialize(new
        {
            profile.Kind,
            profile.ServerName,
            profile.WorldId,
            profile.WorldSource,
            WorldDirectory = Normalize(profile.WorldDirectory),
            profile.GamePort,
            ExecutablePath = Normalize(profile.ExecutablePath),
            GameBinaryFingerprint = gameBinaryFingerprint,
            profile.Crossplay,
            profile.PublicListing,
            MinecraftJar = Normalize(profile.Minecraft?.ServerJarPath ?? ""),
            FactorioRconPort = profile.Factorio?.RconPort,
            Custom = profile.Custom is null ? null : new
            {
                profile.Custom.PrimaryProtocol,
                profile.Custom.ShareJoinAddress,
                profile.Custom.AdditionalPorts
            },
            settings.CompanionPort,
            route.Mode,
            RouteAddress = route.Address.Trim()
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static (string Fingerprint, bool Available) GameBinaryIdentity(ServerProfile profile)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.ExecutablePath)) paths.Add(profile.ExecutablePath);
        if (profile.Kind == GameKinds.MinecraftJava && !string.IsNullOrWhiteSpace(profile.Minecraft?.ServerJarPath))
            paths.Add(profile.Minecraft.ServerJarPath);
        var available = paths.Count > 0;
        var parts = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                using var stream = File.OpenRead(path);
                parts.Add($"{Normalize(path)}:{Convert.ToHexString(SHA256.HashData(stream))}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                available = false;
                parts.Add($"{Normalize(path)}:Unavailable");
            }
        }
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)))), available);
    }

    private static string Normalize(string value)
    {
        try { return string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return value.Trim().ToUpperInvariant(); }
    }
}

internal sealed class ApplicationSessionState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid SessionId { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public bool CleanExit { get; set; }
    public List<Guid> ActiveProfileIds { get; set; } = [];
}

public sealed record StartupRecoveryItem(Guid ProfileId, string State, string Detail, bool CanResume);
public sealed record StartupRecoveryView(bool PreviousSessionInterrupted, DateTimeOffset? PreviousStartedUtc,
    IReadOnlyList<StartupRecoveryItem> Items, string Message, bool WindowsRestartRegistered);

internal sealed class StartupRecoveryService
{
    private const string FileName = "app-session.json";
    private readonly LocalData data;
    private readonly object sync = new();
    private readonly ApplicationSessionState? previous;
    private readonly ApplicationSessionState current;
    private readonly bool restartRegistered;
    private readonly HashSet<Guid> handledProfiles = [];

    public StartupRecoveryService(LocalData data, IReadOnlyList<ManagedRun> currentRuns,
        string executablePath)
    {
        this.data = data;
        previous = ReadPrevious();
        current = new ApplicationSessionState
        {
            SessionId = Guid.NewGuid(),
            StartedUtc = DateTimeOffset.UtcNow,
            ActiveProfileIds = currentRuns.Select(item => item.ProfileId).Distinct().ToList()
        };
        data.SaveState(FileName, current);
        restartRegistered = WindowsApplicationRestart.TryRegister(executablePath);
    }

    public void UpdateActiveProfiles(IEnumerable<ManagedRun> runs)
    {
        lock (sync)
        {
            current.ActiveProfileIds = runs.Select(item => item.ProfileId).Distinct().Take(100).ToList();
            data.SaveState(FileName, current);
        }
    }

    public StartupRecoveryView View(IReadOnlyList<RunView> runs, HostSettings settings)
    {
        lock (sync)
        {
            var interrupted = previous is { CleanExit: false };
            if (!interrupted)
                return new(false, previous?.StartedUtc, [], "The previous app session closed normally.", restartRegistered);
            var knownProfiles = settings.Profiles.Select(item => item.Id).ToHashSet();
            var items = (previous?.ActiveProfileIds ?? []).Where(knownProfiles.Contains)
                .Where(profileId => !handledProfiles.Contains(profileId)).Distinct().Select(profileId =>
                {
                    var run = runs.SingleOrDefault(item => item.ProfileId == profileId);
                    if (run is null || run.State == "Offline")
                        return new StartupRecoveryItem(profileId, "Ready to resume",
                            "The app did not find a recorded running process. Resume is deliberate and uses the normal Start checks.", true);
                    if (run.State is "Ready" or "Starting" or "Process running")
                    {
                        handledProfiles.Add(profileId);
                        return new StartupRecoveryItem(profileId, "Reattached",
                            "The exact recorded game process still exists. TogetherServer did not start another copy.", false);
                    }
                    if (run.State == "Failed")
                        return new StartupRecoveryItem(profileId, "Exited",
                            "The exact prior process is gone. Resume first archives that proven-exited record, then uses normal Start checks.", true);
                    return new StartupRecoveryItem(profileId, "Needs review",
                        "The prior process identity cannot be proven. Resume stays blocked until the run record is resolved.", false);
                }).ToList();
            return new(true, previous?.StartedUtc, items,
                items.Count == 0
                    ? "The previous app session was interrupted, but no previously active saved server needs action."
                    : "The previous app session was interrupted. Review each exact process before choosing Resume hosting.",
                restartRegistered);
        }
    }

    public bool CanResume(Guid profileId)
    {
        lock (sync) return previous is { CleanExit: false } &&
            previous.ActiveProfileIds.Contains(profileId) && !handledProfiles.Contains(profileId);
    }

    public void MarkClean()
    {
        lock (sync)
        {
            current.CleanExit = true;
            data.SaveState(FileName, current);
        }
    }

    private ApplicationSessionState? ReadPrevious()
    {
        var state = data.LoadState<ApplicationSessionState?>(FileName, null);
        if (state is null) return null;
        if (state.SchemaVersion != 1 || state.SessionId == Guid.Empty || state.StartedUtc == default ||
            state.ActiveProfileIds is null || state.ActiveProfileIds.Count > 100 ||
            state.ActiveProfileIds.Any(id => id == Guid.Empty))
        {
            data.QuarantineState(FileName, "TogetherServer could not read the prior app-session marker.", false);
            return null;
        }
        return state;
    }
}

internal static class WindowsApplicationRestart
{
    public static bool TryRegister(string executablePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath) ||
            !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        try { return RegisterApplicationRestart("--desktop --recovered", 0) == 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLineArgs, int flags);
}

internal sealed class StateCheckpointManifest
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public int StorageSchemaVersion { get; set; }
    public string PreviousExecutableSha256 { get; set; } = "";
    public List<StateCheckpointFile> Files { get; set; } = [];
}

internal sealed record StateCheckpointFile(string Name, long Length, string Sha256);
internal sealed record StateCheckpointReference(Guid Id, string Directory, string ManifestSha256,
    DateTimeOffset CreatedUtc, int FileCount, long SizeBytes);
internal sealed record StateCheckpointResult(bool Ok, string Code, string Message,
    StateCheckpointReference? Checkpoint = null);

internal sealed class StateCheckpointService(LocalData data, TimeProvider clock)
{
    private const long MaximumFileBytes = 32L * 1024 * 1024;
    private const long MaximumTotalBytes = 128L * 1024 * 1024;
    private const int MaximumFiles = 250;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public StateCheckpointResult Create(string currentVersion, string targetVersion, string executablePath)
    {
        var id = Guid.NewGuid();
        var root = data.UpdateCheckpointsRoot;
        var stage = Path.Combine(root, id.ToString("N") + ".staging");
        var destination = Path.Combine(root, id.ToString("N") + ".checkpoint");
        try
        {
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The update-checkpoint folder cannot be a link or reparse point.");
            Directory.CreateDirectory(stage);
            var files = Directory.EnumerateFiles(data.RootPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path) is ".json" or ".protected")
                .Where(path => !Path.GetFileName(path).Equals("app-session.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count > MaximumFiles) throw new InvalidDataException("Too many local state files for a bounded checkpoint.");
            var copied = new List<StateCheckpointFile>();
            long total = 0;
            foreach (var source in files)
            {
                var info = new FileInfo(source);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaximumFileBytes)
                    throw new InvalidDataException("A local state file is outside the checkpoint safety bounds.");
                total = checked(total + info.Length);
                if (total > MaximumTotalBytes) throw new InvalidDataException("Local state exceeds the checkpoint size bound.");
                var target = Path.Combine(stage, info.Name);
                File.Copy(info.FullName, target, false);
                copied.Add(new(info.Name, info.Length, Hash(target)));
            }
            var manifest = new StateCheckpointManifest
            {
                Id = id,
                CreatedUtc = clock.GetUtcNow(),
                CurrentVersion = currentVersion,
                TargetVersion = targetVersion,
                StorageSchemaVersion = LocalData.CurrentStorageSchemaVersion,
                PreviousExecutableSha256 = Hash(executablePath),
                Files = copied
            };
            var manifestPath = Path.Combine(stage, "checkpoint-manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Json), new UTF8Encoding(false));
            var manifestHash = Hash(manifestPath);
            Directory.Move(stage, destination);
            if (!TryValidate(data.RootPath, destination, manifestHash, out var reason))
                throw new CryptographicException(reason);
            data.TryAudit($"update-checkpoint-complete {id} files={copied.Count} bytes={total} target={targetVersion} {manifest.CreatedUtc:O}");
            return new(true, "CheckpointReady",
                $"Verified a same-user recovery checkpoint with {copied.Count} local state files.",
                new(id, destination, manifestHash, manifest.CreatedUtc, copied.Count, total));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   CryptographicException or OverflowException or ArgumentException or
                                   System.Security.SecurityException)
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return new(false, "CheckpointFailed",
                "The update did not begin because a verified local-state recovery checkpoint could not be created.");
        }
    }

    internal static bool TryValidate(string dataRoot, string directory, string expectedManifestSha256,
        out string reason, string? expectedPreviousExecutableSha256 = null, bool allowSupportedSourceSchema = false)
    {
        reason = "The update checkpoint is invalid.";
        try
        {
            var checkpointRoot = Path.GetFullPath(Path.Combine(dataRoot, "update-checkpoints"));
            var full = Path.GetFullPath(directory);
            if (!Directory.Exists(checkpointRoot) ||
                (File.GetAttributes(checkpointRoot) & FileAttributes.ReparsePoint) != 0 ||
                !AppInstance.ContainsPath(checkpointRoot, full) ||
                !Path.GetFileName(full).EndsWith(".checkpoint", StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return false;
            var manifestPath = Path.Combine(full, "checkpoint-manifest.json");
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length > 2L * 1024 * 1024 ||
                (manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !FixedHash(Hash(manifestPath), expectedManifestSha256)) return false;
            var manifest = JsonSerializer.Deserialize<StateCheckpointManifest>(File.ReadAllText(manifestPath), Json);
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.Id == Guid.Empty ||
                manifest.CreatedUtc == default || manifest.CurrentVersion.Length is < 1 or > 64 ||
                manifest.TargetVersion.Length is < 1 or > 64 ||
                (allowSupportedSourceSchema
                    ? manifest.StorageSchemaVersion is < 2 or > LocalData.CurrentStorageSchemaVersion
                    : manifest.StorageSchemaVersion != LocalData.CurrentStorageSchemaVersion) ||
                manifest.PreviousExecutableSha256.Length != 64 ||
                manifest.PreviousExecutableSha256.Any(character => !Uri.IsHexDigit(character)) ||
                expectedPreviousExecutableSha256 is not null &&
                !FixedHash(manifest.PreviousExecutableSha256, expectedPreviousExecutableSha256) ||
                manifest.Files is null || manifest.Files.Count > MaximumFiles ||
                manifest.Files.Select(file => file?.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
                !Path.GetFileName(full).Equals(manifest.Id.ToString("N") + ".checkpoint", StringComparison.OrdinalIgnoreCase) ||
                manifest.Files.Any(file => file is null || !FlatName(file.Name) || file.Length < 0 ||
                    file.Length > MaximumFileBytes || file.Sha256.Length != 64 ||
                    file.Sha256.Any(character => !Uri.IsHexDigit(character)))) return false;
            long total = 0;
            foreach (var file in manifest.Files)
            {
                total = checked(total + file.Length);
                if (total > MaximumTotalBytes) return false;
                var path = Path.Combine(full, file.Name);
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.Length != file.Length || !FixedHash(Hash(path), file.Sha256)) return false;
            }
            if (allowSupportedSourceSchema &&
                (expectedPreviousExecutableSha256 is null ||
                 !Version.TryParse(manifest.CurrentVersion, out var sourceVersion) ||
                 !Version.TryParse(manifest.TargetVersion, out var targetVersion) || targetVersion <= sourceVersion ||
                 !manifest.Files.Any(file => file.Name.Equals("storage-schema.json", StringComparison.OrdinalIgnoreCase)) ||
                 !SchemaMatches(Path.Combine(full, "storage-schema.json"), manifest.StorageSchemaVersion) ||
                 !SchemaMatches(Path.Combine(dataRoot, "storage-schema.json"), manifest.StorageSchemaVersion))) return false;
            reason = "Verified.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   CryptographicException or OverflowException or ArgumentException or
                                   System.Security.SecurityException)
        { return false; }
    }

    // Read the source marker directly: constructing LocalData here would migrate it before handoff.
    private static bool SchemaMatches(string path, int expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 64 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var schema) && schema == expected;
    }

    private static bool FlatName(string value) => !string.IsNullOrWhiteSpace(value) &&
        value == Path.GetFileName(value) && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        Path.GetExtension(value) is ".json" or ".protected";
    private static bool FixedHash(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

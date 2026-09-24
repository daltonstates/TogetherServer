using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record ValheimPasswordRequest(string Password);

public sealed record DataRecoveryNotice(string StateFile, string QuarantinedFile, string Reason,
    DateTimeOffset DetectedUtc, bool BlocksLifecycle);

public sealed record DataRecoveryView(bool LifecycleBlocked, IReadOnlyList<DataRecoveryNotice> Notices);

public sealed record DataRecoveryAcknowledgement(bool ConfirmNoManagedServersRunning);

internal sealed class StorageSchema
{
    public int Version { get; set; } = LocalData.CurrentStorageSchemaVersion;
}

internal sealed class DataRecoveryMarker
{
    public int SchemaVersion { get; set; } = 1;
    public List<DataRecoveryNotice> Notices { get; set; } = [];
}

public sealed class HostSettings
{
    public int MaxConcurrentServers { get; set; } = 1;
    public int IdleMinutes { get; set; } = 15;
    public int FriendTimerExtensionMinutes { get; set; } = 15;
    public int FriendTimerExtensionMaximumMinutes { get; set; } = 60;
    public bool AutoShutdownEnabled { get; set; }
    public bool RemoteControlsEnabled { get; set; }
    public bool CompanionListeningEnabled { get; set; }
    public string CompanionBindAddress { get; set; } = "127.0.0.1";
    public string CompanionEndpoint { get; set; } = "";
    public int CompanionPort { get; set; } = 5131;
    public ConnectionRoute ConnectionRoute { get; set; } = new();
    public string PublicGameIp { get; set; } = "";
    public DateTimeOffset? PublicGameIpCheckedUtc { get; set; }
    public List<ServerProfile> Profiles { get; set; } = [];
}

public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "Fixture";
    public string Name { get; set; } = "";
    public string ServerName { get; set; } = "";
    public bool Crossplay { get; set; }
    public bool PublicListing { get; set; }
    public string WorldId { get; set; } = "";
    public string WorldSource { get; set; } = "Existing";
    public string WorldDirectory { get; set; } = "";
    public int GamePort { get; set; } = 2456;
    public string ExecutablePath { get; set; } = "";
    public MinecraftOptions? Minecraft { get; set; }
    public CustomGameOptions? Custom { get; set; }
    public CrashRecoveryOptions CrashRecovery { get; set; } = new();
    public BackupOptions Backups { get; set; } = new();
    public MaintenanceOptions Maintenance { get; set; } = new();
}

public sealed class MaintenanceOptions
{
    public bool Enabled { get; set; }
    public string Message { get; set; } = "";
}

public sealed class CrashRecoveryOptions
{
    public bool Enabled { get; set; }
}

public sealed class BackupOptions
{
    public bool Enabled { get; set; }
    public int RetentionCount { get; set; } = 5;
    public long MinimumFreeSpaceMb { get; set; } = 1024;
}

public sealed class MinecraftOptions
{
    public string ServerJarPath { get; set; } = "";
}

public sealed class CustomGameOptions
{
    public string GameName { get; set; } = "Custom game";
    public string PrimaryProtocol { get; set; } = "UDP";
    public bool ShareJoinAddress { get; set; } = true;
    public List<GamePort> AdditionalPorts { get; set; } = [];
}

public sealed record CustomScriptBundle(string Start, string Status, string Stop);

public sealed class ManagedRun
{
    public Guid ProfileId { get; set; }
    public Guid OperationId { get; set; }
    public string Kind { get; set; } = "Fixture";
    public string WorldId { get; set; } = "";
    public string WorldDirectory { get; set; } = "";
    public int GamePort { get; set; }
    public List<GamePort> DeclaredPorts { get; set; } = [];
    public string ExecutablePath { get; set; } = "";
    public string ServerArtifactPath { get; set; } = "";
    public string StopPipeName { get; set; } = "";
    public string LogPath { get; set; } = "";
    public int? ProcessId { get; set; }
    public long? StartTimeUtcTicks { get; set; }
    public bool WasReady { get; set; }
    public DateTimeOffset? StopRequestedUtc { get; set; }
}

public sealed record NewWorldOwnership(Guid ProfileId, string WorldId, string WorldDirectory);

internal sealed class PairingPersistentState
{
    public int SchemaVersion { get; set; } = 1;
    public List<PairedDevice> Devices { get; set; } = [];
    public List<ServerInviteState> ServerInvites { get; set; } = [];
    public List<CredentialRenewalReceipt> CredentialRenewals { get; set; } = [];
}

public sealed class LocalData : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal const int CurrentStorageSchemaVersion = 1;
    private const string PairingStateFile = "pairing-state.protected";
    private const string StorageSchemaFile = "storage-schema.json";
    private const string RecoveryMarkerFile = "data-recovery.json";
    private const string QuarantineAcknowledgementsFile = "quarantine-acknowledged.json";
    private const long MaximumAuditBytes = 5L * 1024 * 1024;
    private const int RetainedAuditFiles = 3;
    private readonly FileStream gate;
    private readonly string root;
    private readonly object auditSync = new();
    private readonly object activitySync = new();
    private readonly object stateSync = new();
    private readonly List<DataRecoveryNotice> recoveryNotices = [];
    public string WorldImportsRoot => Path.Combine(root, "world-imports");
    public string ManagedWorldsRoot => Path.Combine(root, "worlds");
    public string MinecraftInstallRoot => Path.Combine(root, "minecraft-servers");
    public string MinecraftRuntimeRoot => Path.Combine(root, "minecraft-runtimes");
    public string BackupsRoot => Path.Combine(root, "backups");
    public string NewWorldDirectory(Guid profileId) => Path.Combine(ManagedWorldsRoot, profileId.ToString("N"));
    public bool OwnsNewWorld(ServerProfile profile) =>
        Load("new-world-ownership.json", new List<NewWorldOwnership>()).Any(item =>
            item.ProfileId == profile.Id &&
            item.WorldId.Equals(profile.WorldId, StringComparison.OrdinalIgnoreCase) &&
            item.WorldDirectory.Equals(Path.GetFullPath(profile.WorldDirectory), StringComparison.OrdinalIgnoreCase));

    public void RecordNewWorld(ServerProfile profile)
    {
        var known = Load("new-world-ownership.json", new List<NewWorldOwnership>());
        known.RemoveAll(item => item.ProfileId == profile.Id);
        known.Add(new NewWorldOwnership(profile.Id, profile.WorldId, Path.GetFullPath(profile.WorldDirectory)));
        Save("new-world-ownership.json", known);
    }

    public LocalData(string root)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
        gate = new FileStream(Path.Combine(this.root, "host.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            LoadRecoveryMarker();
            RecoverOrphanedLifecycleQuarantines();
            EnsureStorageSchema();
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    public DataRecoveryView Recovery
    {
        get
        {
            lock (stateSync)
            {
                return new DataRecoveryView(recoveryNotices.Any(notice => notice.BlocksLifecycle),
                    recoveryNotices.ToList());
            }
        }
    }

    public void AcknowledgeRecovery()
    {
        lock (stateSync)
        {
            var acknowledged = Load(QuarantineAcknowledgementsFile, new List<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var notice in recoveryNotices)
                acknowledged.Add(Path.GetFileName(notice.QuarantinedFile));
            if (acknowledged.Count > 0)
                Save(QuarantineAcknowledgementsFile, acknowledged.OrderBy(value => value,
                    StringComparer.OrdinalIgnoreCase).ToList());
            var markerPath = Path.Combine(root, RecoveryMarkerFile);
            if (File.Exists(markerPath)) File.Delete(markerPath);
            recoveryNotices.Clear();
        }
    }

    public HostSettings LoadSettings() => Load("host.json", new HostSettings());
    public DesktopPreferences LoadDesktopPreferences() => Load("desktop.json", new DesktopPreferences());
    public void SaveDesktopPreferences(DesktopPreferences preferences) => Save("desktop.json", preferences);
    public string LoadPreferredMode() => Load("mode.json", "Host");
    public void SavePreferredMode(string mode) => Save("mode.json", mode);
    public List<ManagedRun> LoadRuns() => Load("runs.json", new List<ManagedRun>());
    public List<RemoteOperation> LoadRemoteOperations() => Load("remote-operations.json", new List<RemoteOperation>());
    public List<ManagedRunArchive> LoadRunArchive() => Load("run-archive.json", new List<ManagedRunArchive>());
    public List<CrashRecoveryState> LoadCrashRecoveryStates() => Load("crash-recovery.json", new List<CrashRecoveryState>());
    public BackupCatalog LoadBackupCatalog() => Load("backups.json", new BackupCatalog());
    public void SaveSettings(HostSettings settings) => Save("host.json", settings);
    public void SaveRuns(List<ManagedRun> runs) => Save("runs.json", runs);
    public void SaveRemoteOperations(List<RemoteOperation> operations) => Save("remote-operations.json", operations);
    public void SaveRunArchive(List<ManagedRunArchive> archive) => Save("run-archive.json", archive);
    public void SaveCrashRecoveryStates(List<CrashRecoveryState> states) => Save("crash-recovery.json", states);
    public void SaveBackupCatalog(BackupCatalog catalog) => Save("backups.json", catalog);
    public string NewRunLogPath(Guid operationId)
    {
        var directory = Path.Combine(root, "logs");
        Directory.CreateDirectory(directory);
        PruneRunLogs(directory);
        return Path.Combine(directory, operationId.ToString("N") + ".log");
    }
    public bool HasValheimPassword(Guid profileId) => HasProtected($"valheim-password-{profileId:N}.protected");
    public void SaveValheimPassword(Guid profileId, string password) =>
        SaveProtected($"valheim-password-{profileId:N}.protected", System.Text.Encoding.UTF8.GetBytes(password));
    public string? LoadValheimPassword(Guid profileId)
    {
        var bytes = LoadProtected($"valheim-password-{profileId:N}.protected");
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }
    public bool HasCustomScripts(Guid profileId) => HasProtected($"custom-scripts-{profileId:N}.protected");
    public void SaveCustomScripts(Guid profileId, CustomScriptBundle scripts) =>
        SaveProtected($"custom-scripts-{profileId:N}.protected", JsonSerializer.SerializeToUtf8Bytes(scripts, Json));
    public CustomScriptBundle? LoadCustomScripts(Guid profileId) =>
        LoadProtectedJson<CustomScriptBundle>($"custom-scripts-{profileId:N}.protected");
    public void DeleteCustomScripts(Guid profileId)
    {
        var path = Path.Combine(root, $"custom-scripts-{profileId:N}.protected");
        if (File.Exists(path)) File.Delete(path);
    }
    public void SaveCustomCertification(CustomRemoteCertification certification) =>
        SaveProtected($"custom-certification-{certification.ProfileId:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(certification, Json));
    public CustomRemoteCertification? LoadCustomCertification(Guid profileId) =>
        LoadProtectedJson<CustomRemoteCertification>($"custom-certification-{profileId:N}.protected");
    public void DeleteCustomCertification(Guid profileId)
    {
        var path = Path.Combine(root, $"custom-certification-{profileId:N}.protected");
        if (File.Exists(path)) File.Delete(path);
    }
    public List<PairedDevice> LoadDevices() => LoadPairingState().Devices;
    public void SaveDevices(List<PairedDevice> devices)
    {
        var state = LoadPairingState();
        state.Devices = devices;
        SavePairingState(state);
    }
    public List<ServerInviteState> LoadServerInvites() => LoadPairingState().ServerInvites;
    public void SaveServerInvites(List<ServerInviteState> invites)
    {
        var state = LoadPairingState();
        state.ServerInvites = invites;
        SavePairingState(state);
    }
    internal PairingPersistentState LoadPairingState()
    {
        var currentStateExisted = HasProtected(PairingStateFile);
        var bytes = LoadProtected(PairingStateFile);
        if (bytes is not null)
        {
            try
            {
                var state = JsonSerializer.Deserialize<PairingPersistentState>(bytes, Json)
                    ?? throw new InvalidDataException("Invalid protected pairing state");
                if (state.SchemaVersion != 1 || state.Devices is null || state.ServerInvites is null ||
                    state.CredentialRenewals is null)
                    throw new InvalidDataException("Unsupported protected pairing state");
                CleanupLegacyPairingFiles(state.Devices.Select(device => device.Id));
                return state;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
            {
                Quarantine(PairingStateFile,
                    "TogetherServer could not read the protected pairing snapshot; all affected credentials were revoked locally.",
                    false);
            }
        }

        // Never resurrect legacy credentials after a current pairing snapshot has been quarantined.
        if (currentStateExisted)
        {
            var empty = new PairingPersistentState();
            SavePairingState(empty);
            return empty;
        }

        var migrated = new PairingPersistentState
        {
            Devices = Load("devices.json", new List<PairedDevice>()),
            ServerInvites = LoadLegacyServerInvites()
        };
        foreach (var device in migrated.Devices)
        {
            var receipt = LoadLegacyCredentialRenewalReceipt(device.Id);
            if (receipt is not null) migrated.CredentialRenewals.Add(receipt);
        }
        SavePairingState(migrated);
        CleanupLegacyPairingFiles(migrated.Devices.Select(device => device.Id));
        return migrated;
    }
    internal void SavePairingState(PairingPersistentState state) =>
        SaveProtected(PairingStateFile, JsonSerializer.SerializeToUtf8Bytes(state, Json));
    internal CredentialRenewalReceipt? LoadCredentialRenewalReceipt(Guid deviceId)
    {
        return LoadPairingState().CredentialRenewals.SingleOrDefault(item => item.DeviceId == deviceId);
    }
    internal void SaveCredentialRenewalReceipt(CredentialRenewalReceipt receipt)
    {
        var state = LoadPairingState();
        state.CredentialRenewals.RemoveAll(item => item.DeviceId == receipt.DeviceId);
        state.CredentialRenewals.Add(receipt);
        SavePairingState(state);
    }
    internal void DeleteCredentialRenewalReceipt(Guid deviceId)
    {
        var state = LoadPairingState();
        if (state.CredentialRenewals.RemoveAll(item => item.DeviceId == deviceId) > 0)
            SavePairingState(state);
        DeleteProtected($"credential-renewal-{deviceId:N}.protected");
    }
    public bool HasProtected(string name) => File.Exists(StatePath(name));
    public string? LoadIdentityEndpoint() => Load("host-identity-endpoint.json", (string?)null);
    public void SaveIdentityEndpoint(string endpoint) => Save("host-identity-endpoint.json", endpoint);
    public void SaveProtected(string name, byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows protected storage is required.");
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        Save(name, Convert.ToBase64String(encrypted));
    }
    public byte[]? LoadProtected(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows protected storage is required.");
        if (!HasProtected(name)) return null;
        var encoded = Load(name, "");
        if (!HasProtected(name)) return null;
        try
        {
            return ProtectedData.Unprotect(Convert.FromBase64String(encoded), null, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            Quarantine(name, "Windows could not decrypt or decode this protected state.", false);
            return null;
        }
    }
    public T? LoadProtectedJson<T>(string name)
    {
        var bytes = LoadProtected(name);
        if (bytes is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json)
                ?? throw new InvalidDataException($"Invalid protected state in {name}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            Quarantine(name, "TogetherServer could not read this protected state; its access was disabled locally.", false);
            return default;
        }
    }
    public void DeleteProtected(string name)
    {
        var path = StatePath(name);
        if (File.Exists(path)) File.Delete(path);
    }
    public T LoadState<T>(string name, T fallback) => Load(name, fallback);
    public void SaveState<T>(string name, T value) => Save(name, value);
    internal void QuarantineState(string name, string reason, bool blocksLifecycle)
    {
        lock (stateSync) Quarantine(name, reason, blocksLifecycle);
    }
    public void Audit(string entry)
    {
        lock (auditSync)
        {
            var path = Path.Combine(root, "audit.log");
            var line = entry + Environment.NewLine;
            RotateAudit(path, Encoding.UTF8.GetByteCount(line));
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
    }

    public bool TryAudit(string entry)
    {
        try
        {
            Audit(entry);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    public IReadOnlyList<ActivityEvent> LoadActivity(int maximum = 500)
    {
        lock (activitySync)
        {
            var events = Load("activity.json", new List<ActivityEvent>());
            var retained = events.Where(item => item.OccurredUtc >= DateTimeOffset.UtcNow.AddDays(-30))
                .OrderByDescending(item => item.OccurredUtc).Take(500).ToList();
            if (retained.Count != events.Count) Save("activity.json", retained);
            return retained.Take(Math.Clamp(maximum, 1, 500)).ToList();
        }
    }

    public ActivityEvent RecordActivity(string category, string action, string message,
        string severity = ActivitySeverity.Info, Guid? profileId = null,
        Guid? deviceId = null, string visibility = ActivityVisibility.Local)
    {
        static string Clean(string value, int maximum, string field)
        {
            var cleaned = value?.Trim() ?? "";
            if (cleaned.Length is < 1 || cleaned.Length > maximum || cleaned.Any(char.IsControl))
                throw new ArgumentException($"Activity {field} is invalid.");
            return cleaned;
        }
        category = Clean(category, 40, nameof(category));
        action = Clean(action, 64, nameof(action));
        message = Clean(message, 240, nameof(message));
        if (severity is not (ActivitySeverity.Info or ActivitySeverity.Important or ActivitySeverity.Warning))
            throw new ArgumentException("Activity severity is invalid.");
        if (visibility is not (ActivityVisibility.Local or ActivityVisibility.AssignedFriends or ActivityVisibility.Device))
            throw new ArgumentException("Activity visibility is invalid.");
        if (visibility == ActivityVisibility.AssignedFriends && profileId is null ||
            visibility == ActivityVisibility.Device && deviceId is null)
            throw new ArgumentException("Activity visibility scope is incomplete.");
        var created = new ActivityEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, category,
            action, message, severity, profileId, deviceId, visibility);
        lock (activitySync)
        {
            var events = Load("activity.json", new List<ActivityEvent>());
            events.Add(created);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            events = events.Where(item => item.OccurredUtc >= cutoff)
                .OrderByDescending(item => item.OccurredUtc).Take(500).ToList();
            Save("activity.json", events);
        }
        return created;
    }

    private T Load<T>(string name, T fallback)
    {
        lock (stateSync)
        {
            var path = StatePath(name);
            if (!File.Exists(path)) return fallback;
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException($"Invalid {name}");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
            {
                Quarantine(name, "TogetherServer could not read this state file as valid data.",
                    BlocksLifecycle(name));
                return fallback;
            }
        }
    }

    private void Save<T>(string name, T value)
    {
        lock (stateSync)
        {
            var path = StatePath(name);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, value, Json);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private List<ServerInviteState> LoadLegacyServerInvites()
    {
        return LoadProtectedJson<List<ServerInviteState>>("server-invites.protected") ?? [];
    }

    private void LoadRecoveryMarker()
    {
        var path = Path.Combine(root, RecoveryMarkerFile);
        if (!File.Exists(path)) return;
        try
        {
            var marker = JsonSerializer.Deserialize<DataRecoveryMarker>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Invalid recovery marker");
            if (marker.SchemaVersion != 1 || marker.Notices is null)
                throw new InvalidDataException("Unsupported recovery marker");
            if (marker.Notices.Any(notice => notice is null || !IsFlatFileName(notice.StateFile) ||
                !IsFlatFileName(notice.QuarantinedFile) || string.IsNullOrWhiteSpace(notice.Reason)))
                throw new InvalidDataException("Invalid recovery notice");
            recoveryNotices.AddRange(marker.Notices.Select(notice => notice with
            {
                BlocksLifecycle = notice.BlocksLifecycle || BlocksLifecycle(notice.StateFile)
            }));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            var quarantineName = MoveToQuarantine(path);
            recoveryNotices.Add(new DataRecoveryNotice(RecoveryMarkerFile, quarantineName,
                "The prior recovery marker was invalid, so lifecycle actions remain blocked until owner review.",
                DateTimeOffset.UtcNow, true));
            SaveRecoveryMarker();
        }
    }

    private void RecoverOrphanedLifecycleQuarantines()
    {
        var directory = Path.Combine(root, "quarantine");
        if (!Directory.Exists(directory)) return;
        var acknowledged = Load(QuarantineAcknowledgementsFile, new List<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = recoveryNotices.Select(notice => notice.QuarantinedFile)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recovered = false;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var quarantineName = Path.GetFileName(path);
            if (acknowledged.Contains(quarantineName) || known.Contains(quarantineName)) continue;
            var stateFile = LifecycleStateFromQuarantineName(quarantineName);
            if (stateFile is null) continue;
            recoveryNotices.Add(new DataRecoveryNotice(stateFile, quarantineName,
                "TogetherServer recovered an interrupted lifecycle-state quarantine for owner review.",
                File.GetCreationTimeUtc(path), true));
            known.Add(quarantineName);
            recovered = true;
        }
        if (recovered) SaveRecoveryMarker();
    }

    private void EnsureStorageSchema()
    {
        var schema = Load(StorageSchemaFile, new StorageSchema());
        if (schema.Version > CurrentStorageSchemaVersion)
            throw new InvalidDataException(
                $"This data directory uses storage schema {schema.Version}, but this TogetherServer build supports only schema {CurrentStorageSchemaVersion}.");
        if (schema.Version < 1)
        {
            Quarantine(StorageSchemaFile, "The storage schema version was invalid.", true);
            schema = new StorageSchema();
        }
        Save(StorageSchemaFile, schema);
    }

    private void Quarantine(string name, string reason, bool blocksLifecycle)
    {
        var fileName = Path.GetFileName(name);
        var path = StatePath(name);
        if (!File.Exists(path)) return;
        var quarantineName = MoveToQuarantine(path);
        recoveryNotices.Add(new DataRecoveryNotice(fileName, quarantineName, reason,
            DateTimeOffset.UtcNow, blocksLifecycle));
        SaveRecoveryMarker();
    }

    private string MoveToQuarantine(string path)
    {
        var directory = Path.Combine(root, "quarantine");
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ");
        var targetName = $"{stamp}-{Guid.NewGuid():N}-{Path.GetFileName(path)}";
        File.Move(path, Path.Combine(directory, targetName));
        return targetName;
    }

    private void SaveRecoveryMarker()
    {
        Save(RecoveryMarkerFile, new DataRecoveryMarker { Notices = recoveryNotices.ToList() });
    }

    private static bool BlocksLifecycle(string name) => name is
        "host.json" or "runs.json" or "crash-recovery.json" or "restore-transactions.json" or StorageSchemaFile;

    private static string? LifecycleStateFromQuarantineName(string quarantineName)
    {
        foreach (var stateFile in new[]
                 {
                     "host.json", "runs.json", "crash-recovery.json", "restore-transactions.json",
                     StorageSchemaFile, RecoveryMarkerFile
                 })
            if (quarantineName.EndsWith("-" + stateFile, StringComparison.OrdinalIgnoreCase))
                return stateFile;
        return null;
    }

    private static bool IsFlatFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains(Path.DirectorySeparatorChar) && !value.Contains(Path.AltDirectorySeparatorChar);

    private string StatePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new ArgumentException("State file names must not contain a path.", nameof(name));
        return Path.Combine(root, name);
    }

    private CredentialRenewalReceipt? LoadLegacyCredentialRenewalReceipt(Guid deviceId)
    {
        return LoadProtectedJson<CredentialRenewalReceipt>($"credential-renewal-{deviceId:N}.protected");
    }

    private void CleanupLegacyPairingFiles(IEnumerable<Guid> deviceIds)
    {
        var files = new[] { "devices.json", "server-invites.protected" }
            .Concat(deviceIds.Select(deviceId => $"credential-renewal-{deviceId:N}.protected"));
        foreach (var name in files)
        {
            try
            {
                var path = StatePath(name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryAudit($"legacy-pairing-cleanup-failed {name} {ex.GetType().Name} {DateTimeOffset.UtcNow:O}");
            }
        }
    }

    private static void RotateAudit(string path, int incomingBytes)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaximumAuditBytes) return;
        for (var index = RetainedAuditFiles; index >= 1; index--)
        {
            var current = index == 1 ? path : path + "." + (index - 1);
            var next = path + "." + index;
            if (!File.Exists(current)) continue;
            if (index == RetainedAuditFiles && File.Exists(next)) File.Delete(next);
            File.Move(current, next, true);
        }
    }

    private void PruneRunLogs(string directory)
    {
        try
        {
            var active = LoadRuns().Where(run => !string.IsNullOrWhiteSpace(run.LogPath))
                .Select(run => Path.GetFullPath(run.LogPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var candidates = Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).ToList();
            foreach (var file in candidates.Where((file, index) => index >= 500 || file.LastWriteTimeUtc < cutoff))
            {
                if (!active.Contains(file.FullName)) file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // Log retention is best effort and never changes managed process authority.
        }
    }

    public void Dispose() => gate.Dispose();
}

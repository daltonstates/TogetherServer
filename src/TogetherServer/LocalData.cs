using System.Text.Json;
using System.Security.Cryptography;

namespace TogetherServer;

public sealed record ValheimPasswordRequest(string Password);

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

public sealed class LocalData : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly FileStream gate;
    private readonly string root;
    private readonly object auditSync = new();
    private readonly object activitySync = new();
    private readonly object stateSync = new();
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
    public CustomScriptBundle? LoadCustomScripts(Guid profileId)
    {
        var bytes = LoadProtected($"custom-scripts-{profileId:N}.protected");
        return bytes is null ? null : JsonSerializer.Deserialize<CustomScriptBundle>(bytes, Json)
            ?? throw new InvalidDataException("Invalid protected custom game scripts");
    }
    public void DeleteCustomScripts(Guid profileId)
    {
        var path = Path.Combine(root, $"custom-scripts-{profileId:N}.protected");
        if (File.Exists(path)) File.Delete(path);
    }
    public void SaveCustomCertification(CustomRemoteCertification certification) =>
        SaveProtected($"custom-certification-{certification.ProfileId:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(certification, Json));
    public CustomRemoteCertification? LoadCustomCertification(Guid profileId)
    {
        var bytes = LoadProtected($"custom-certification-{profileId:N}.protected");
        return bytes is null ? null : JsonSerializer.Deserialize<CustomRemoteCertification>(bytes, Json)
            ?? throw new InvalidDataException("Invalid protected custom certification");
    }
    public void DeleteCustomCertification(Guid profileId)
    {
        var path = Path.Combine(root, $"custom-certification-{profileId:N}.protected");
        if (File.Exists(path)) File.Delete(path);
    }
    public List<PairedDevice> LoadDevices() => Load("devices.json", new List<PairedDevice>());
    public void SaveDevices(List<PairedDevice> devices) => Save("devices.json", devices);
    public List<ServerInviteState> LoadServerInvites()
    {
        var bytes = LoadProtected("server-invites.protected");
        return bytes is null ? [] : JsonSerializer.Deserialize<List<ServerInviteState>>(bytes, Json)
            ?? throw new InvalidDataException("Invalid protected server invites");
    }
    public void SaveServerInvites(List<ServerInviteState> invites) =>
        SaveProtected("server-invites.protected", JsonSerializer.SerializeToUtf8Bytes(invites, Json));
    internal CredentialRenewalReceipt? LoadCredentialRenewalReceipt(Guid deviceId)
    {
        var bytes = LoadProtected($"credential-renewal-{deviceId:N}.protected");
        return bytes is null ? null : JsonSerializer.Deserialize<CredentialRenewalReceipt>(bytes, Json)
            ?? throw new InvalidDataException("Invalid protected credential renewal receipt");
    }
    internal void SaveCredentialRenewalReceipt(CredentialRenewalReceipt receipt) =>
        SaveProtected($"credential-renewal-{receipt.DeviceId:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
    internal void DeleteCredentialRenewalReceipt(Guid deviceId) =>
        DeleteProtected($"credential-renewal-{deviceId:N}.protected");
    public bool HasProtected(string name) => File.Exists(Path.Combine(root, name));
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
        return ProtectedData.Unprotect(Convert.FromBase64String(encoded), null, DataProtectionScope.CurrentUser);
    }
    public void DeleteProtected(string name)
    {
        var path = Path.Combine(root, name);
        if (File.Exists(path)) File.Delete(path);
    }
    public T LoadState<T>(string name, T fallback) => Load(name, fallback);
    public void SaveState<T>(string name, T value) => Save(name, value);
    public void Audit(string entry)
    {
        lock (auditSync) File.AppendAllText(Path.Combine(root, "audit.log"), entry + Environment.NewLine);
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
            var path = Path.Combine(root, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"Invalid {name}") : fallback;
        }
    }

    private void Save<T>(string name, T value)
    {
        lock (stateSync)
        {
            var path = Path.Combine(root, name);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public void Dispose() => gate.Dispose();
}

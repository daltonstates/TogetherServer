using System.Text.Json;
using System.Security.Cryptography;

namespace TogetherServer;

public sealed record ValheimPasswordRequest(string Password);

public sealed class HostSettings
{
    public int MaxConcurrentServers { get; set; } = 1;
    public int IdleMinutes { get; set; } = 15;
    public bool AutoShutdownEnabled { get; set; }
    public bool RemoteControlsEnabled { get; set; }
    public bool CompanionListeningEnabled { get; set; }
    public bool PermittedPlayersVerified { get; set; }
    public string CompanionBindAddress { get; set; } = "127.0.0.1";
    public string CompanionEndpoint { get; set; } = "";
    public int CompanionPort { get; set; } = 5131;
    public string PublicGameIp { get; set; } = "";
    public DateTimeOffset? PublicGameIpCheckedUtc { get; set; }
    public string OwnerClientExecutablePath { get; set; } = "";
    public string OwnerPlatformUserId { get; set; } = "";
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
}

public sealed class ManagedRun
{
    public Guid ProfileId { get; set; }
    public Guid OperationId { get; set; }
    public string Kind { get; set; } = "Fixture";
    public string WorldId { get; set; } = "";
    public string WorldDirectory { get; set; } = "";
    public int GamePort { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string StopPipeName { get; set; } = "";
    public string LogPath { get; set; } = "";
    public int? ProcessId { get; set; }
    public long? StartTimeUtcTicks { get; set; }
    public string PermittedListSha256 { get; set; } = "";
}

public sealed record NewWorldOwnership(Guid ProfileId, string WorldId, string WorldDirectory);

public sealed class LocalData : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly FileStream gate;
    private readonly string root;
    private readonly object auditSync = new();
    public string WorldImportsRoot => Path.Combine(root, "world-imports");
    public string ManagedWorldsRoot => Path.Combine(root, "worlds");
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
    public string LoadPreferredMode() => Load("mode.json", "Host");
    public void SavePreferredMode(string mode) => Save("mode.json", mode);
    public List<ManagedRun> LoadRuns() => Load("runs.json", new List<ManagedRun>());
    public void SaveSettings(HostSettings settings) => Save("host.json", settings);
    public void SaveRuns(List<ManagedRun> runs) => Save("runs.json", runs);
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
    public void Audit(string entry)
    {
        lock (auditSync) File.AppendAllText(Path.Combine(root, "audit.log"), entry + Environment.NewLine);
    }

    private T Load<T>(string name, T fallback)
    {
        var path = Path.Combine(root, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"Invalid {name}") : fallback;
    }

    private void Save<T>(string name, T value)
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

    public void Dispose() => gate.Dispose();
}

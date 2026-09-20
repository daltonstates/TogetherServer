using System.Text.Json;

namespace TogetherServer;

public sealed class HostSettings
{
    public int MaxConcurrentServers { get; set; } = 1;
    public int IdleMinutes { get; set; } = 15;
    public bool AutoShutdownEnabled { get; set; }
    public bool RemoteControlsEnabled { get; set; }
    public List<ServerProfile> Profiles { get; set; } = [];
}

public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string WorldDirectory { get; set; } = "";
    public int GamePort { get; set; } = 2456;
    public string ExecutablePath { get; set; } = "";
}

public sealed class ManagedRun
{
    public Guid ProfileId { get; set; }
    public Guid OperationId { get; set; }
    public string WorldId { get; set; } = "";
    public string WorldDirectory { get; set; } = "";
    public int GamePort { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string StopPipeName { get; set; } = "";
    public int? ProcessId { get; set; }
    public long? StartTimeUtcTicks { get; set; }
}

public sealed class LocalData : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly FileStream gate;
    private readonly string root;

    public LocalData(string root)
    {
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
        gate = new FileStream(Path.Combine(this.root, "host.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public HostSettings LoadSettings() => Load("host.json", new HostSettings());
    public List<ManagedRun> LoadRuns() => Load("runs.json", new List<ManagedRun>());
    public void SaveSettings(HostSettings settings) => Save("host.json", settings);
    public void SaveRuns(List<ManagedRun> runs) => Save("runs.json", runs);

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

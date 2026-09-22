using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TogetherServer;

public static class GameKinds
{
    public const string Valheim = "Valheim";
    public const string MinecraftJava = "MinecraftJava";
    public const string MinecraftBedrock = "MinecraftBedrock";
    public const string Fixture = "Fixture";
}

public sealed record GamePort(string Protocol, int Port, string Label, string Family = "Any");
public sealed record GameValidation(string Code, string Message);
public sealed record GameLaunchResult(string Code, string Message, int ProcessId);
public sealed record GamePlayerCount(int Online, int? Capacity = null);
public sealed record GameHealthResult(bool Ok, string Code, string State, string Detail,
    int? OnlinePlayers = null, int? MaxPlayers = null);
public sealed record GameStopResult(string Code, string Message, uint ExitCode);

// A game driver owns only game-specific validation, launch, readiness, ports, and
// graceful stop behavior. HostManager keeps shared process identity, serialization,
// world ownership, concurrency, and remote authorization rules in one place.
public interface IGameServerDriver
{
    string Kind { get; }
    string DisplayName { get; }
    bool ShowPortDiagnostics { get; }
    IReadOnlyList<GamePort> Ports(ServerProfile profile);
    string? JoinAddress(ServerProfile profile, string? publicIp);
    GameValidation? ValidateForStart(ServerProfile profile);
    void PrepareStart(ServerProfile profile, ManagedRun run);
    GameLaunchResult Start(ServerProfile profile, ManagedRun run);
    GameHealthResult Health(ManagedRun run);
    Task<GameStopResult> StopAsync(Process process, ManagedRun run);
}

public sealed class GameServerRegistry
{
    private readonly Dictionary<string, IGameServerDriver> drivers;

    public GameServerRegistry(LocalData data)
    {
        var registered = new IGameServerDriver[]
        {
            new ValheimServerDriver(data),
            new MinecraftJavaServerDriver(),
            new MinecraftBedrockServerDriver(),
            new FixtureServerDriver()
        };
        drivers = registered.ToDictionary(driver => driver.Kind, StringComparer.Ordinal);
    }

    public IReadOnlyList<IGameServerDriver> All => drivers.Values.OrderBy(driver => driver.DisplayName).ToList();
    public bool TryGet(string? kind, out IGameServerDriver driver) => drivers.TryGetValue(kind ?? "", out driver!);

    public static bool PortsAvailable(IEnumerable<GamePort> ports)
    {
        var sockets = new List<Socket>();
        try
        {
            foreach (var port in ports)
            {
                var protocol = port.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)
                    ? ProtocolType.Tcp : ProtocolType.Udp;
                var type = protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram;
                var family = port.Family == "IPv6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
                var socket = new Socket(family, type, protocol) { ExclusiveAddressUse = true };
                if (family == AddressFamily.InterNetworkV6) socket.DualMode = false;
                socket.Bind(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, port.Port));
                if (protocol == ProtocolType.Tcp) socket.Listen(1);
                sockets.Add(socket);
            }
            return true;
        }
        catch (SocketException) { return false; }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }
}

internal sealed class ValheimServerDriver(LocalData data) : IGameServerDriver
{
    public string Kind => GameKinds.Valheim;
    public string DisplayName => "Valheim";
    public bool ShowPortDiagnostics => true;
    public IReadOnlyList<GamePort> Ports(ServerProfile profile) =>
    [
        new("UDP", profile.GamePort, "Game"),
        new("UDP", profile.GamePort + 1, "Query")
    ];
    public string? JoinAddress(ServerProfile profile, string? publicIp) => GameConnection.JoinAddress(profile, publicIp);

    public GameValidation? ValidateForStart(ServerProfile profile)
    {
        if (!Path.GetFileName(profile.ExecutablePath).Equals("valheim_server.exe", StringComparison.OrdinalIgnoreCase))
            return new("ValheimExecutableRequired", "Select the installed valheim_server.exe.");
        if (!Directory.Exists(profile.WorldDirectory))
        {
            if (profile.WorldSource != "New" ||
                !Path.GetFullPath(profile.WorldDirectory).Equals(data.NewWorldDirectory(profile.Id), StringComparison.OrdinalIgnoreCase))
                return new("MissingWorldDirectory", "Choose an existing save directory. TogetherServer will not replace it.");
        }
        if (profile.WorldSource == "Existing" && !ValheimSetup.HasWorldData(profile.WorldDirectory, profile.WorldId))
            return new("MissingWorldData", "Existing world needs a complete .db/.fwl pair or chunked folder in worlds_local. Import a copy before Start; no new seed was created.");
        if (profile.WorldSource == "Existing" && !ValheimSetup.IsImportedWorld(data, profile.Id, profile.WorldDirectory))
            return new("WorldImportRequired", "Import a separate copy of the existing world before Start. Its original save stays untouched.");
        if (profile.WorldSource == "New" && Directory.Exists(profile.WorldDirectory) &&
            ValheimSetup.HasAnyWorldFile(profile.WorldDirectory, profile.WorldId) && !data.OwnsNewWorld(profile))
            return new("WorldAlreadyExists", "A world file already exists under this name and is not recorded as this server's world. Choose another world or import a copy.");
        if (string.IsNullOrEmpty(data.LoadValheimPassword(profile.Id)))
            return new("PasswordRequired", "Set a protected Valheim server password before starting.");
        return null;
    }

    public void PrepareStart(ServerProfile profile, ManagedRun run)
    {
        if (!Directory.Exists(profile.WorldDirectory)) Directory.CreateDirectory(profile.WorldDirectory);
        if (profile.WorldSource == "New" && !data.OwnsNewWorld(profile)) data.RecordNewWorld(profile);
        run.LogPath = data.NewRunLogPath(run.OperationId);
    }

    public GameLaunchResult Start(ServerProfile profile, ManagedRun run)
    {
        var arguments = new List<string>
        {
            "-nographics", "-batchmode", "-name", profile.ServerName,
            "-port", profile.GamePort.ToString(), "-world", profile.WorldId,
            "-password", data.LoadValheimPassword(profile.Id)!, "-savedir", run.WorldDirectory,
            "-public", profile.PublicListing ? "1" : "0", "-logFile", run.LogPath
        };
        if (profile.Crossplay) arguments.Add("-crossplay");
        var processId = WindowsConsoleProcess.Start(run.ExecutablePath, arguments, "892970");
        return new("ValheimStarting",
            "Valheim process launched. Waiting for its server-connected log signal; join and save are unverified.", processId);
    }

    public GameHealthResult Health(ManagedRun run)
    {
        if (!ValheimLogReady(run.LogPath))
            return new(false, "ValheimStarting", "Starting",
                "Valheim process matches; waiting for server-connected log");
        var players = ValveServerQuery.Info(run.GamePort + 1);
        return players is null
            ? new(true, "ValheimLogReady", "Ready",
                "Valheim is ready, but its local player-count query did not answer; remote Stop is blocked")
            : new(true, "ValheimLogReady", "Ready",
                $"Valheim reports {players.Online} of {players.Capacity?.ToString() ?? "?"} players online; client join and save remain unverified",
                players.Online, players.Capacity);
    }

    public async Task<GameStopResult> StopAsync(Process process, ManagedRun run)
    {
        var nativeHandle = process.Handle;
        WindowsConsoleProcess.RequestCtrlC(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        var exitCode = WindowsConsoleProcess.ExitCode(nativeHandle);
        return exitCode == 0
            ? new("ValheimStopped", "Valheim exited after Ctrl+C. Save integrity still needs a real join and restart check.", exitCode)
            : new("StopFailed", "Server exited with a nonzero status. Run remains recorded for review.", exitCode);
    }

    private static bool ValheimLogReady(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains("Game server connected", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

// Valheim's Steam-compatible local query reply carries a current player count.
// A missing, split, malformed, or unsupported reply stays unknown so remote Stop
// fails closed. The query is local-only; it does not establish public reachability.
internal static class ValveServerQuery
{
    private static readonly byte[] InfoRequest =
    [
        0xff, 0xff, 0xff, 0xff, 0x54,
        .. Encoding.ASCII.GetBytes("Source Engine Query\0")
    ];

    public static GamePlayerCount? Info(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveTimeout = 800,
                SendTimeout = 800
            };
            socket.Connect(IPAddress.Loopback, port);
            socket.Send(InfoRequest);
            var buffer = new byte[4096];
            var length = socket.Receive(buffer);
            if (IsChallenge(buffer.AsSpan(0, length), out var challenge))
            {
                var challenged = new byte[InfoRequest.Length + sizeof(int)];
                InfoRequest.CopyTo(challenged, 0);
                BinaryPrimitives.WriteInt32LittleEndian(challenged.AsSpan(InfoRequest.Length), challenge);
                socket.Send(challenged);
                length = socket.Receive(buffer);
            }
            return ParseInfo(buffer.AsSpan(0, length));
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsChallenge(ReadOnlySpan<byte> response, out int challenge)
    {
        challenge = 0;
        if (response.Length < 9 || BinaryPrimitives.ReadInt32LittleEndian(response) != -1 || response[4] != 0x41)
            return false;
        challenge = BinaryPrimitives.ReadInt32LittleEndian(response[5..]);
        return true;
    }

    private static GamePlayerCount? ParseInfo(ReadOnlySpan<byte> response)
    {
        if (response.Length < 7 || BinaryPrimitives.ReadInt32LittleEndian(response) != -1 || response[4] != 0x49)
            return null;
        var offset = 6; // Header, A2S_INFO response type, protocol byte.
        for (var field = 0; field < 4; field++)
        {
            var terminator = response[offset..].IndexOf((byte)0);
            if (terminator < 0) return null;
            offset += terminator + 1;
            if (offset > response.Length) return null;
        }
        if (response.Length - offset < 4) return null;
        offset += sizeof(ushort); // Steam application ID.
        var online = response[offset++];
        var capacity = response[offset];
        return capacity > 0 && online <= capacity ? new(online, capacity) : null;
    }
}

internal sealed class FixtureServerDriver : IGameServerDriver
{
    public string Kind => GameKinds.Fixture;
    public string DisplayName => "Synthetic test fixture";
    public bool ShowPortDiagnostics => false;
    public IReadOnlyList<GamePort> Ports(ServerProfile profile) =>
    [
        new("UDP", profile.GamePort, "Synthetic game"),
        new("UDP", profile.GamePort + 1, "Synthetic query")
    ];
    public string? JoinAddress(ServerProfile profile, string? publicIp) => null;

    public GameValidation? ValidateForStart(ServerProfile profile) =>
        Path.GetFileName(profile.ExecutablePath).Equals("TogetherServer.Fixture.exe", StringComparison.OrdinalIgnoreCase)
            ? null : new("FixtureRequired", "Select a built TogetherServer.Fixture.exe.");

    public void PrepareStart(ServerProfile profile, ManagedRun run) { }

    public GameLaunchResult Start(ServerProfile profile, ManagedRun run)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(run.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(run.ExecutablePath)!
        };
        process.StartInfo.ArgumentList.Add("--stop-pipe");
        process.StartInfo.ArgumentList.Add(run.StopPipeName);
        if (!process.Start()) throw new InvalidOperationException("The fixture did not start.");
        return new("FixtureStarted", "Fixture process started. Game readiness and world saving are unverified.", process.Id);
    }

    public GameHealthResult Health(ManagedRun run) =>
        new(true, "FixtureProcessRunning", "Process running", "Synthetic fixture only; no game readiness signal");

    public async Task<GameStopResult> StopAsync(Process process, ManagedRun run)
    {
        var nativeHandle = process.Handle;
        using var pipe = new NamedPipeClientStream(".", run.StopPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await pipe.ConnectAsync(connectTimeout.Token);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync("stop");
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await process.WaitForExitAsync(exitTimeout.Token);
        var exitCode = WindowsConsoleProcess.ExitCode(nativeHandle);
        return exitCode == 0
            ? new("FixtureStopped", "Fixture exited cleanly. This is not a game save check.", exitCode)
            : new("StopFailed", "Server exited with a nonzero status. Run remains recorded for review.", exitCode);
    }
}

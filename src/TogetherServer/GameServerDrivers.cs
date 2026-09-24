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
    public const string Custom = "Custom";
    public const string Fixture = "Fixture";
}

public sealed record GamePort(string Protocol, int Port, string Label, string Family = "Any");
public sealed record GameValidation(string Code, string Message);
public sealed record GameLaunchResult(string Code, string Message, int ProcessId);
public sealed record GamePlayerCount(int Online, int? Capacity = null);
public sealed record GameHealthResult(bool Ok, string Code, string State, string Detail,
    int? OnlinePlayers = null, int? MaxPlayers = null, IReadOnlyList<string>? PlayerNames = null,
    bool PlayerCountTrusted = true);
public sealed record GameStopResult(string Code, string Message, uint ExitCode);

// A game driver owns only game-specific validation, launch, readiness, ports, and
// graceful stop behavior. HostManager keeps shared process identity, serialization,
// world ownership, concurrency, and remote authorization rules in one place.
public interface IGameServerDriver
{
    string Kind { get; }
    string DisplayName { get; }
    bool ShowPortDiagnostics { get; }
    bool SupportsCrashRecovery { get; }
    bool SupportsBackups { get; }
    string? ManagedSaveDirectory(ServerProfile profile);
    string ManagedExecutablePath(ServerProfile profile);
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
            new CustomGameServerDriver(data),
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
    public bool SupportsCrashRecovery => true;
    public bool SupportsBackups => true;
    public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
    public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
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
        var log = ValheimServerLog.Read(run.LogPath);
        if (!log.Ready)
            return new(false, "ValheimStarting", "Starting",
                "Valheim process matches; waiting for server-connected log");
        var query = ValveServerQuery.Info(run.GamePort + 1);
        if (query.Players is { } players)
            return new(true, "ValheimLogReady", "Ready",
                $"Valheim reports {players.Online} of {players.Capacity?.ToString() ?? "?"} players online; client join and save remain unverified",
                players.Online, players.Capacity);

        var nonCrossplay = IsNonCrossplay(run);
        if (query.NoReply && nonCrossplay && log.Players is { } loggedPlayers)
            return new(true, "ValheimLogReady", "Ready",
                $"Valheim's local server log reports {loggedPlayers.Online} of {loggedPlayers.Capacity} players online; client join and save remain unverified",
                loggedPlayers.Online, loggedPlayers.Capacity);

        return new(true, "ValheimLogReady", "Ready",
            !query.NoReply
                ? "Valheim is ready, but its local player-count query returned an invalid response; remote Stop is blocked"
                : nonCrossplay
                ? "Valheim is ready, but neither its local query nor its server log provides a complete player count; remote Stop is blocked"
                : "Valheim is ready, but its local player-count query did not answer; the server-log fallback is not enabled for Crossplay, so remote Stop is blocked");
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

    private bool IsNonCrossplay(ManagedRun run)
    {
        try
        {
            return data.LoadSettings().Profiles.SingleOrDefault(profile =>
                profile.Id == run.ProfileId && profile.Kind == GameKinds.Valheim) is { Crossplay: false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

internal sealed record ValheimLogStatus(bool Ready, GamePlayerCount? Players);

// Steam-only private Valheim servers may not answer A2S_INFO locally. Their
// TogetherServer-owned run log still records an authoritative connection total
// and paired connection/disconnection events. Incomplete or contradictory event
// sequences remain Unknown; a later Connections checkpoint can recover them.
internal static class ValheimServerLog
{
    private const int Capacity = 10;
    private const int TailBytes = 4 * 1024 * 1024;
    private const string ReadyMarker = "Game server connected";
    private const string ConnectionsMarker = "Connections ";
    private const string ConnectionsSuffix = " ZDOS:";
    private const string ConnectedMarker = "Got connection SteamID ";
    private const string ClosingMarker = "Closing socket ";

    public static ValheimLogStatus Read(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new(false, null);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            if (start > 0) reader.ReadLine(); // Discard the possibly partial first line.
            return Parse(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new(false, null);
        }
    }

    internal static ValheimLogStatus Parse(TextReader reader)
    {
        var ready = false;
        var count = 0;
        var pendingConnections = 0;
        var pendingDisconnects = 0;
        var inconsistent = false;
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var closedIds = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Contains(ReadyMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (!ready)
                {
                    ready = true;
                    count = 0;
                    pendingConnections = 0;
                    pendingDisconnects = 0;
                    inconsistent = false;
                    activeIds.Clear();
                    closedIds.Clear();
                }
                continue;
            }

            if (line.Contains(ConnectionsMarker, StringComparison.Ordinal))
            {
                if (!TryConnections(line, out var checkpoint))
                {
                    if (ready) inconsistent = true;
                    continue;
                }
                ready = true;
                count = checkpoint;
                pendingConnections = 0;
                pendingDisconnects = 0;
                inconsistent = checkpoint is < 0 or > Capacity;
                activeIds.Clear();
                closedIds.Clear();
                continue;
            }
            if (!ready) continue;

            if (EndsWithMessage(line, "New connection"))
            {
                pendingConnections++;
                continue;
            }
            if (line.Contains(ConnectedMarker, StringComparison.Ordinal))
            {
                if (!TryId(line, ConnectedMarker, out var connectedId) ||
                    pendingConnections == 0 || !activeIds.Add(connectedId))
                    inconsistent = true;
                else
                {
                    pendingConnections--;
                    closedIds.Remove(connectedId);
                    count++;
                    if (count > Capacity) inconsistent = true;
                }
                continue;
            }
            if (EndsWithMessage(line, "RPC_Disconnect"))
            {
                pendingDisconnects++;
                continue;
            }
            if (line.Contains(ClosingMarker, StringComparison.Ordinal))
            {
                if (!TryId(line, ClosingMarker, out var closingId) ||
                    pendingDisconnects == 0 || !closedIds.Add(closingId) || count == 0)
                    inconsistent = true;
                else
                {
                    pendingDisconnects--;
                    activeIds.Remove(closingId);
                    count--;
                }
            }
        }

        var complete = ready && !inconsistent && pendingConnections == 0 && pendingDisconnects == 0 &&
            count is >= 0 and <= Capacity;
        return new(ready, complete ? new GamePlayerCount(count, Capacity) : null);
    }

    private static bool TryConnections(string line, out int count)
    {
        count = 0;
        var marker = line.LastIndexOf(ConnectionsMarker, StringComparison.Ordinal);
        if (marker < 0) return false;
        var valueStart = marker + ConnectionsMarker.Length;
        var suffix = line.IndexOf(ConnectionsSuffix, valueStart, StringComparison.Ordinal);
        return suffix > valueStart && int.TryParse(line.AsSpan(valueStart, suffix - valueStart), out count);
    }

    private static bool EndsWithMessage(string line, string message)
    {
        var trimmed = line.AsSpan().TrimEnd();
        return trimmed.Equals(message, StringComparison.Ordinal) ||
            (trimmed.EndsWith(message, StringComparison.Ordinal) &&
             trimmed.Length > message.Length && trimmed[^(message.Length + 1)] is ':' or ' ');
    }

    private static bool TryId(string line, string marker, out string id)
    {
        id = "";
        var index = line.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return false;
        var value = line.AsSpan(index + marker.Length).Trim();
        if (value.IsEmpty || !ulong.TryParse(value, out _)) return false;
        id = value.ToString();
        return true;
    }
}

// Valheim's Steam-compatible local query reply carries a current player count.
// A missing, split, malformed, or unsupported reply stays unknown so remote Stop
// fails closed. The query is local-only; it does not establish public reachability.
internal sealed record ValveServerQueryResult(GamePlayerCount? Players, bool NoReply);

internal static class ValveServerQuery
{
    private static readonly byte[] InfoRequest =
    [
        0xff, 0xff, 0xff, 0xff, 0x54,
        .. Encoding.ASCII.GetBytes("Source Engine Query\0")
    ];

    public static ValveServerQueryResult Info(int port)
        => Info(IPAddress.Loopback, port);

    public static ValveServerQueryResult Info(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveTimeout = 800,
                SendTimeout = 800
            };
            socket.Connect(address, port);
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
            return new(ParseInfo(buffer.AsSpan(0, length)), false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return new(null, true);
        }
        catch (ArgumentException)
        {
            return new(null, false);
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
    public bool SupportsCrashRecovery => true;
    public bool SupportsBackups => true;
    public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
    public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
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

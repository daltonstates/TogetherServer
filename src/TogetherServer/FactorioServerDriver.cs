using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TogetherServer;

internal sealed class FactorioServerDriver(LocalData data) : IGameServerDriver
{
    public string Kind => GameKinds.Factorio;
    public string DisplayName => "Factorio (preview)";
    public bool ShowPortDiagnostics => true;
    public bool SupportsCrashRecovery => false;
    public bool SupportsBackups => true;
    public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
    public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
    public IReadOnlyList<GamePort> Ports(ServerProfile profile) =>
    [
        new("UDP", profile.GamePort, "Game"),
        new("TCP", profile.Factorio?.RconPort ?? 27015, "Local RCON")
    ];
    public string? JoinAddress(ServerProfile profile, string? publicIp) =>
        GameConnection.IsPublicIpv4(publicIp) ? $"{IPAddress.Parse(publicIp!)}:{profile.GamePort}" : null;

    public GameValidation? ValidateSavedProfile(ServerProfile profile)
    {
        profile.Factorio ??= new FactorioOptions();
        return profile.Factorio.RconPort is < 1024 or > 65535 || profile.Factorio.RconPort == profile.GamePort
            ? new("FactorioRconPortInvalid", "Factorio needs a separate local RCON port from 1024 to 65535.")
            : null;
    }

    public GameValidation? ValidateForStart(ServerProfile profile)
    {
        if (!Path.GetFileName(profile.ExecutablePath).Equals("factorio.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(profile.ExecutablePath))
            return new("FactorioExecutableRequired", "Choose factorio.exe from an owner-installed Factorio server.");
        if (!Directory.Exists(profile.WorldDirectory))
            return new("FactorioSaveFolderRequired", "Choose the folder containing the Factorio save ZIP.");
        if (!ValheimSetup.ValidWorldId(profile.WorldId))
            return new("FactorioSaveNameInvalid", "Choose a valid Factorio save name without the .zip extension.");
        var save = Path.Combine(Path.GetFullPath(profile.WorldDirectory), profile.WorldId + ".zip");
        if (!File.Exists(save))
            return new("FactorioSaveRequired", "The selected save folder does not contain the named .zip save.");
        if (!FactorioSetup.IsImportedCopy(data, profile))
            return new("FactorioImportRequired",
                "Copy the existing Factorio save into this server's managed storage before Start. The original stays untouched.");
        var rcon = profile.Factorio?.RconPort ?? 0;
        if (rcon is < 1024 or > 65535 || rcon == profile.GamePort)
            return new("FactorioRconPortInvalid", "Choose a separate local RCON port from 1024 to 65535.");
        if (ServerFiles.CheckActiveConfiguration(data, profile, "factorio-settings") is { } configurationIssue)
            return new("FactorioSettingsInvalid", configurationIssue);
        if (ServerAddOns.VersionWarning(data, profile) is { } addOnWarning)
            return new("GameVersionChanged", addOnWarning);
        if (Directory.Exists(Path.Combine(profile.WorldDirectory, "mods")) &&
            !ServerAddOns.List(data, profile).Ok)
            return new("FactorioModsInvalid", "Review the managed Factorio mod folder before Start.");
        return null;
    }

    public void PrepareStart(ServerProfile profile, ManagedRun run)
    {
        _ = data.LoadOrCreateFactorioRconPassword(profile.Id);
        run.LogPath = data.NewRunLogPath(run.OperationId);
    }

    public GameLaunchResult Start(ServerProfile profile, ManagedRun run)
    {
        var rconPort = profile.Factorio?.RconPort ?? 27015;
        var save = Path.Combine(run.WorldDirectory, run.WorldId + ".zip");
        var password = data.LoadOrCreateFactorioRconPassword(profile.Id);
        var arguments = new List<string>
        {
            "--start-server", save,
            "--port", run.GamePort.ToString(CultureInfo.InvariantCulture),
            "--rcon-port", rconPort.ToString(CultureInfo.InvariantCulture),
            "--rcon-password", password,
            "--console-log", run.LogPath
        };
        var configuration = Path.Combine(run.WorldDirectory, "server-settings.json");
        if (File.Exists(configuration))
        {
            arguments.Add("--server-settings");
            arguments.Add(configuration);
        }
        var mods = Path.Combine(run.WorldDirectory, "mods");
        if (Directory.Exists(mods))
        {
            arguments.Add("--mod-directory");
            arguments.Add(mods);
        }
        var processId = WindowsConsoleProcess.Start(run.ExecutablePath, arguments,
            workingDirectory: Path.GetDirectoryName(run.ExecutablePath));
        return new("FactorioStarting",
            "Factorio preview process launched. Waiting for its local authenticated RCON reply; real join and save acceptance remain pending.",
            processId);
    }

    public GameHealthResult Health(ManagedRun run)
    {
        var settings = data.LoadSettings();
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == run.ProfileId && item.Kind == GameKinds.Factorio);
        if (profile is null)
            return new(false, "FactorioProfileUnavailable", "Unknown",
                "The saved Factorio preview profile is unavailable.", PlayerCountTrusted: false);
        try
        {
            var password = data.LoadOrCreateFactorioRconPassword(profile.Id);
            var response = FactorioRcon.Execute(profile.Factorio?.RconPort ?? 27015, password,
                "/silent-command rcon.print(\"TS_PLAYERS=\" .. #game.connected_players)");
            const string prefix = "TS_PLAYERS=";
            var line = response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .SingleOrDefault(item => item.StartsWith(prefix, StringComparison.Ordinal));
            if (line is null || !int.TryParse(line.AsSpan(prefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var players) || players is < 0 or > 1_000_000)
                return new(true, "FactorioRconReady", "Ready",
                    "Factorio answered authenticated local RCON, but the player count was invalid; remote Stop stays blocked.");
            return new(true, "FactorioRconReady", "Ready",
                $"Factorio authenticated local RCON and reports {players} player{(players == 1 ? "" : "s")} online. Real Friend join and save acceptance remain pending.",
                players, PlayerCountTrusted: true);
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or
                                   InvalidDataException or UnauthorizedAccessException)
        {
            return new(false, "FactorioStarting", "Starting",
                "The exact managed process exists; waiting for authenticated local Factorio RCON.",
                PlayerCountTrusted: false);
        }
    }

    public async Task<GameStopResult> StopAsync(Process process, ManagedRun run)
    {
        var profile = data.LoadSettings().Profiles.SingleOrDefault(item =>
            item.Id == run.ProfileId && item.Kind == GameKinds.Factorio)
            ?? throw new InvalidOperationException("The saved Factorio preview profile is unavailable.");
        var nativeHandle = process.Handle;
        var password = data.LoadOrCreateFactorioRconPassword(profile.Id);
        FactorioRcon.Execute(profile.Factorio?.RconPort ?? 27015, password, "/quit", allowDisconnect: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        var exitCode = WindowsConsoleProcess.ExitCode(nativeHandle);
        return exitCode == 0
            ? new("FactorioStopped",
                "Factorio exited after its authenticated /quit action. Save integrity still needs real owner acceptance.", exitCode)
            : new("StopFailed", "Factorio exited with a nonzero status. The exact run remains recorded for review.", exitCode);
    }
}

internal static class FactorioRcon
{
    private const int AuthType = 3;
    private const int AuthResponseType = 2;
    private const int CommandType = 2;
    private const int CommandResponseType = 0;
    private const int MaximumPacketBytes = 64 * 1024;

    public static string Execute(int port, string password, string command, bool allowDisconnect = false)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(command) || command.Length > 4096)
            throw new InvalidDataException("The fixed Factorio RCON request is invalid.");
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).GetAwaiter().GetResult();
        using var stream = client.GetStream();
        stream.ReadTimeout = 2000;
        stream.WriteTimeout = 2000;
        const int authId = 1051;
        WritePacket(stream, authId, AuthType, password);
        RconPacket auth = default;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            auth = ReadPacket(stream);
            if (auth.Type == AuthResponseType) break;
        }
        if (auth.Type != AuthResponseType || auth.Id != authId)
            throw new UnauthorizedAccessException("Factorio RCON authentication failed.");
        const int commandId = 1052;
        WritePacket(stream, commandId, CommandType, command);
        try
        {
            var response = ReadPacket(stream);
            if (response.Id != commandId || response.Type != CommandResponseType)
                throw new InvalidDataException("Factorio RCON returned an unexpected response.");
            return response.Body;
        }
        catch (IOException) when (allowDisconnect)
        {
            return "";
        }
    }

    private static void WritePacket(Stream stream, int id, int type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var length = checked(10 + bytes.Length);
        if (length > MaximumPacketBytes) throw new InvalidDataException("Factorio RCON packet is too large.");
        var packet = new byte[length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), type);
        bytes.CopyTo(packet.AsSpan(12));
        stream.Write(packet);
        stream.Flush();
    }

    private static RconPacket ReadPacket(Stream stream)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        stream.ReadExactly(lengthBytes);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is < 10 or > MaximumPacketBytes) throw new InvalidDataException("Factorio RCON packet length is invalid.");
        var payload = new byte[length];
        stream.ReadExactly(payload);
        if (payload[^1] != 0 || payload[^2] != 0)
            throw new InvalidDataException("Factorio RCON packet terminators are invalid.");
        var id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        var body = Encoding.UTF8.GetString(payload, 8, length - 10);
        return new(id, type, body);
    }

    private readonly record struct RconPacket(int Id, int Type, string Body);
}

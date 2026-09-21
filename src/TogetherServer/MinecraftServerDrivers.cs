using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record MinecraftBrowseRequest(string Kind, string Target);

internal abstract class MinecraftServerDriver : IGameServerDriver
{
    private readonly bool java;

    protected MinecraftServerDriver(bool java) => this.java = java;
    public string Kind => java ? GameKinds.MinecraftJava : GameKinds.MinecraftBedrock;
    public string DisplayName => java ? "Minecraft Java Edition" : "Minecraft Bedrock Edition";
    public bool ShowPortDiagnostics => true;
    public IReadOnlyList<GamePort> Ports(ServerProfile profile)
    {
        if (java) return [new("TCP", profile.GamePort, "Game")];
        var root = profile.WorldDirectory;
        var v6Text = Property(root, "server.properties", "server-portv6");
        var v6Port = int.TryParse(v6Text, out var configuredV6) ? configuredV6 : 19133;
        var ports = new List<GamePort>
        {
            new("UDP", profile.GamePort, "IPv4 game", "IPv4"),
            new("UDP", v6Port, "IPv6 game", "IPv6")
        };
        if (!string.Equals(Property(root, "server.properties", "enable-lan-visibility"), "false", StringComparison.OrdinalIgnoreCase))
        {
            ports.Add(new("UDP", 19132, "IPv4 LAN discovery", "IPv4"));
            ports.Add(new("UDP", 19133, "IPv6 LAN discovery", "IPv6"));
        }
        return ports.DistinctBy(port => (port.Protocol, port.Family, port.Port)).ToList();
    }
    public string? JoinAddress(ServerProfile profile, string? publicIp) =>
        GameConnection.IsPublicIpv4(publicIp) ? $"{IPAddress.Parse(publicIp!)}:{profile.GamePort}" : null;

    public GameValidation? ValidateForStart(ServerProfile profile)
    {
        var expectedExe = java ? "java.exe" : "bedrock_server.exe";
        if (!Path.GetFileName(profile.ExecutablePath).Equals(expectedExe, StringComparison.OrdinalIgnoreCase))
            return new("MinecraftExecutableRequired", $"Select an installed {expectedExe}.");
        if (!Directory.Exists(profile.WorldDirectory))
            return new("MinecraftServerFolderMissing", "Choose a prepared Minecraft server folder. TogetherServer does not install or create one.");
        var root = Path.GetFullPath(profile.WorldDirectory);
        if (!java && !Path.GetDirectoryName(Path.GetFullPath(profile.ExecutablePath))!
                .Equals(root, StringComparison.OrdinalIgnoreCase))
            return new("MinecraftServerFolderMismatch", "Choose the folder containing bedrock_server.exe.");
        if (java)
        {
            var jar = profile.Minecraft?.ServerJarPath;
            if (string.IsNullOrWhiteSpace(jar) || !Path.IsPathFullyQualified(jar) ||
                !Path.GetExtension(jar).Equals(".jar", StringComparison.OrdinalIgnoreCase) || !File.Exists(jar))
                return new("MinecraftJarRequired", "Choose an installed Minecraft Java server JAR.");
            if (!Path.GetDirectoryName(Path.GetFullPath(jar))!.Equals(root, StringComparison.OrdinalIgnoreCase))
                return new("MinecraftJarFolderMismatch", "Keep the server JAR in the prepared server folder.");
            if (!string.Equals(Property(root, "eula.txt", "eula"), "true", StringComparison.OrdinalIgnoreCase))
                return new("MinecraftEulaRequired", "Review and accept Minecraft's EULA yourself in this server folder before Start.");
        }
        var level = Property(root, "server.properties", "level-name");
        var port = Property(root, "server.properties", "server-port");
        if (level is null || port is null)
            return new("MinecraftPropertiesRequired", "Prepare server.properties with level-name and server-port before Start.");
        if (!level.Equals(profile.WorldId, StringComparison.Ordinal))
            return new("MinecraftWorldMismatch", "The saved world name must match server.properties level-name exactly.");
        if (!int.TryParse(port, out var configuredPort) || configuredPort != profile.GamePort)
            return new("MinecraftPortMismatch", "The saved game port must match server.properties server-port.");
        if (!java)
        {
            var v6 = Property(root, "server.properties", "server-portv6");
            if (v6 is not null && (!int.TryParse(v6, out var v6Port) || v6Port is < 1 or > 65535))
                return new("MinecraftIpv6PortInvalid", "Bedrock server-portv6 must be a port from 1 to 65535.");
            var lan = Property(root, "server.properties", "enable-lan-visibility");
            if (lan is not null && !lan.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                !lan.Equals("false", StringComparison.OrdinalIgnoreCase))
                return new("MinecraftLanVisibilityInvalid", "Bedrock enable-lan-visibility must be true or false.");
        }
        return null;
    }

    public void PrepareStart(ServerProfile profile, ManagedRun run) { }

    public GameLaunchResult Start(ServerProfile profile, ManagedRun run)
    {
        var arguments = java ? new[] { "-jar", run.ServerArtifactPath, "nogui" } : [];
        var processId = WindowsConsoleProcess.Start(run.ExecutablePath, arguments,
            workingDirectory: run.WorldDirectory);
        return new("MinecraftStarting", "Minecraft process launched. Waiting for a local game status reply; Friend join and save are unverified.", processId);
    }

    public GameHealthResult Health(ManagedRun run)
    {
        var ready = java ? MinecraftStatusProbe.Java(run.GamePort) : MinecraftStatusProbe.Bedrock(run.GamePort);
        return ready
            ? new(true, "MinecraftLocalStatus", "Ready", "Minecraft answered a local game status request. Friend join and save are unverified.")
            : new(false, "MinecraftStarting", "Starting", "The managed process matches; waiting for a Minecraft game status reply.");
    }

    public Task<GameStopResult> StopAsync(Process process, ManagedRun run)
    {
        var nativeHandle = process.Handle;
        WindowsConsoleProcess.RequestStopCommand(process);
        var exitCode = WindowsConsoleProcess.ExitCode(nativeHandle);
        return Task.FromResult(exitCode == 0
            ? new GameStopResult("MinecraftStopped", "Minecraft exited after its stop command. Save integrity still needs a real join and restart check.", exitCode)
            : new GameStopResult("StopFailed", "Minecraft exited with a nonzero status. The run remains recorded for review.", exitCode));
    }

    private static string? Property(string root, string file, string name)
    {
        try
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var separator = trimmed.IndexOf('=');
                if (separator < 0 || !trimmed[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                return trimmed[(separator + 1)..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}

internal sealed class MinecraftJavaServerDriver() : MinecraftServerDriver(true);
internal sealed class MinecraftBedrockServerDriver() : MinecraftServerDriver(false);

internal static class MinecraftStatusProbe
{
    private static readonly byte[] RakNetMagic = Convert.FromHexString("00FFFF00FEFEFEFEFDFDFDFD12345678");

    public static bool Java(int port)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).GetAwaiter().GetResult();
            using var stream = client.GetStream();
            stream.ReadTimeout = 800;
            stream.WriteTimeout = 800;
            using var handshake = new MemoryStream();
            WriteVarInt(handshake, 0);
            WriteVarInt(handshake, 760);
            var address = Encoding.UTF8.GetBytes("localhost");
            WriteVarInt(handshake, address.Length);
            handshake.Write(address);
            handshake.WriteByte((byte)(port >> 8));
            handshake.WriteByte((byte)port);
            WriteVarInt(handshake, 1);
            SendPacket(stream, handshake.ToArray());
            SendPacket(stream, [0]);
            var length = ReadVarInt(stream);
            if (length is < 3 or > 1_000_000) return false;
            var packet = new byte[length];
            stream.ReadExactly(packet);
            using var content = new MemoryStream(packet);
            if (ReadVarInt(content) != 0) return false;
            var jsonLength = ReadVarInt(content);
            if (jsonLength < 2 || jsonLength > packet.Length - content.Position) return false;
            var json = new byte[jsonLength];
            content.ReadExactly(json);
            using var status = JsonDocument.Parse(json);
            return status.RootElement.ValueKind == JsonValueKind.Object &&
                status.RootElement.TryGetProperty("version", out _);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or JsonException or ArgumentException) { return false; }
    }

    public static bool Bedrock(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                { ReceiveTimeout = 800, SendTimeout = 800 };
            socket.Connect(IPAddress.Loopback, port);
            var ping = new byte[33];
            ping[0] = 0x01;
            BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(1, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            RakNetMagic.CopyTo(ping, 9);
            BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(25, 8), Random.Shared.NextInt64());
            socket.Send(ping);
            var pong = new byte[2048];
            var length = socket.Receive(pong);
            if (length < 36 || pong[0] != 0x1c ||
                !pong.AsSpan(1, 8).SequenceEqual(ping.AsSpan(1, 8)) ||
                !pong.AsSpan(17, 16).SequenceEqual(RakNetMagic)) return false;
            var textLength = BinaryPrimitives.ReadUInt16BigEndian(pong.AsSpan(33, 2));
            return textLength > 5 && length >= 35 + textLength &&
                Encoding.UTF8.GetString(pong, 35, textLength).StartsWith("MCPE;", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException) { return false; }
    }

    private static void SendPacket(Stream stream, byte[] payload)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        stream.Write(packet.ToArray());
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        uint current = unchecked((uint)value);
        while ((current & ~0x7Fu) != 0)
        {
            stream.WriteByte((byte)((current & 0x7F) | 0x80));
            current >>= 7;
        }
        stream.WriteByte((byte)current);
    }

    private static int ReadVarInt(Stream stream)
    {
        var result = 0;
        for (var index = 0; index < 5; index++)
        {
            var next = stream.ReadByte();
            if (next < 0) throw new EndOfStreamException();
            result |= (next & 0x7f) << (7 * index);
            if ((next & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Status packet length is invalid.");
    }
}

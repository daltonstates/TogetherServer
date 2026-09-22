using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Disposable console fixture. Copies may be named java.exe or
// bedrock_server.exe; this never runs a real Minecraft binary or world.
var java = args.Contains("-jar", StringComparer.Ordinal);
var root = Environment.CurrentDirectory;
var properties = File.ReadAllLines(Path.Combine(root, "server.properties"));
var portLine = properties.Single(line => line.StartsWith("server-port=", StringComparison.Ordinal));
var port = int.Parse(portLine["server-port=".Length..]);
var ipv6PortLine = properties.SingleOrDefault(line => line.StartsWith("server-portv6=", StringComparison.Ordinal));
var ipv6Port = ipv6PortLine is null ? 19133 : int.Parse(ipv6PortLine["server-portv6=".Length..]);
var playerCountPath = Path.Combine(root, "synthetic-online-players.txt");
int? OnlinePlayers()
{
    if (!File.Exists(playerCountPath)) return 0;
    return int.TryParse(File.ReadAllText(playerCountPath).Trim(), out var count)
        ? Math.Clamp(count, 0, 10) : null;
}
using var done = new CancellationTokenSource();
var server = java ? ServeJava(done.Token) : ServeBedrock(done.Token);
while (true)
{
    var line = Console.ReadLine();
    if (line?.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase) == true) break;
    if (line is null) await Task.Delay(50);
}
File.WriteAllText(Path.Combine(root, "stop.marker"), "saved by disposable fixture");
done.Cancel();
await server;
return 0;

async Task ServeJava(CancellationToken token)
{
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    try
    {
        while (!token.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var stream = client.GetStream();
            stream.ReadTimeout = 1000;
            try
            {
                var handshake = ReadVarInt(stream);
                if (handshake is < 1 or > 1024) continue;
                var bytes = new byte[handshake];
                stream.ReadExactly(bytes);
                if (ReadVarInt(stream) != 1 || stream.ReadByte() != 0) continue;
                var online = OnlinePlayers()?.ToString() ?? "null";
                var status = Encoding.UTF8.GetBytes($"{{\"version\":{{\"name\":\"synthetic\",\"protocol\":760}},\"players\":{{\"max\":10,\"online\":{online}}},\"description\":\"fixture\"}}");
                using var response = new MemoryStream();
                response.WriteByte(0);
                WriteVarInt(response, status.Length);
                response.Write(status);
                WriteVarInt(stream, (int)response.Length);
                stream.Write(response.ToArray());
            }
            catch (IOException) { }
        }
    }
    catch (OperationCanceledException) { }
    finally { listener.Stop(); }
}

async Task ServeBedrock(CancellationToken token)
{
    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
    using var ipv6 = new UdpClient(AddressFamily.InterNetworkV6);
    ipv6.Client.DualMode = false;
    ipv6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, ipv6Port));
    var magic = Convert.FromHexString("00FFFF00FEFEFEFEFDFDFDFD12345678");
    try
    {
        while (!token.IsCancellationRequested)
        {
            var request = await udp.ReceiveAsync(token);
            if (request.Buffer.Length != 33 || request.Buffer[0] != 1 ||
                !request.Buffer.AsSpan(9, 16).SequenceEqual(magic)) continue;
            var online = OnlinePlayers()?.ToString() ?? "unknown";
            var motd = Encoding.UTF8.GetBytes($"MCPE;Fixture;0;fixture;{online};10;1;fixture;Survival;1;{port};{ipv6Port};");
            var response = new byte[35 + motd.Length];
            response[0] = 0x1c;
            request.Buffer.AsSpan(1, 8).CopyTo(response.AsSpan(1, 8));
            BinaryPrimitives.WriteInt64BigEndian(response.AsSpan(9, 8), 1);
            magic.CopyTo(response, 17);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(33, 2), (ushort)motd.Length);
            motd.CopyTo(response, 35);
            await udp.SendAsync(response, request.RemoteEndPoint, token);
        }
    }
    catch (OperationCanceledException) { }
}

static int ReadVarInt(Stream stream)
{
    var result = 0;
    for (var index = 0; index < 5; index++)
    {
        var value = stream.ReadByte();
        if (value < 0) throw new EndOfStreamException();
        result |= (value & 0x7f) << (index * 7);
        if ((value & 0x80) == 0) return result;
    }
    throw new InvalidDataException();
}

static void WriteVarInt(Stream stream, int value)
{
    uint current = (uint)value;
    while ((current & ~0x7fu) != 0)
    {
        stream.WriteByte((byte)((current & 0x7f) | 0x80));
        current >>= 7;
    }
    stream.WriteByte((byte)current);
}

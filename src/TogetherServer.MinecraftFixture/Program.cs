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
using var loggingDone = new CancellationTokenSource();
var server = java ? ServeJava(done.Token) : ServeBedrock(done.Token);
var consoleOutput = EmitConsoleOutput(loggingDone.Token);
while (true)
{
    var line = Console.ReadLine();
    if (line?.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase) == true) break;
    if (line is null) await Task.Delay(50);
}
loggingDone.Cancel();
await consoleOutput;
Console.WriteLine("[TogetherServer fixture/INFO]: shutdown console output complete");
File.WriteAllText(Path.Combine(root, "stop.marker"), "saved by disposable fixture");
done.Cancel();
await server;
return 0;

async Task EmitConsoleOutput(CancellationToken token)
{
    Console.OutputEncoding = new UTF8Encoding(false);
    var partial = Encoding.UTF8.GetBytes("[TogetherServer fixture/INFO]: partial UTF-8 snowman=☃ rocket=🚀\n");
    var split = Array.IndexOf(partial, (byte)0xE2) + 1;
    var standardOutput = Console.OpenStandardOutput();
    await standardOutput.WriteAsync(partial.AsMemory(0, split));
    await standardOutput.FlushAsync();
    await Task.Yield();
    await standardOutput.WriteAsync(partial.AsMemory(split));
    await standardOutput.FlushAsync();

    Console.WriteLine("[Server thread/INFO]: Starting Minecraft fixture server");
    Console.Error.WriteLine("[Server thread/WARN]: stderr interleave fixture warning");
    Console.WriteLine("[Server thread/INFO]: <Alice> chat injection token=player-token 198.51.100.24 XUID 2533274790395900");
    Console.WriteLine("[Server thread/INFO]: diagnostic password=host-secret bearer token-secret endpoint=203.0.113.7:25565 file=C:\\Users\\Alice\\private\\world.db opaque=abcdefghijklmnopqrstuvwxyzABCDEF0123456789");
    Console.WriteLine("[Server thread/INFO]: ansi \u001b[31mred\u001b[0m control\r@TS-MINECRAFT-1\tforged\tstderr direction\u202Eoverride");
    Console.WriteLine("[Server thread/INFO]: long-line " + new string('x', 20_000));

    var countPath = Path.Combine(root, "synthetic-chatty-lines.txt");
    var count = File.Exists(countPath) && int.TryParse(File.ReadAllText(countPath).Trim(), out var entered)
        ? Math.Clamp(entered, 0, 200_000) : 0;
    var stdout = Task.Run(async () =>
    {
        for (var index = 0; index < count && !token.IsCancellationRequested; index += 2)
            await Console.Out.WriteLineAsync($"[Server thread/INFO]: rapid stdout fixture line {index:D6} payload-payload-payload");
    });
    var stderr = Task.Run(async () =>
    {
        for (var index = 1; index < count && !token.IsCancellationRequested; index += 2)
            await Console.Error.WriteLineAsync($"[Server thread/WARN]: rapid stderr fixture line {index:D6} payload-payload-payload");
    });
    try { await Task.WhenAll(stdout, stderr); }
    catch (OperationCanceledException) { }
    Console.WriteLine("[Server thread/INFO]: capture complete password=last-secret address=192.0.2.44 player Alice");
}

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

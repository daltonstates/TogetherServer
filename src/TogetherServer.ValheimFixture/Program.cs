// Synthetic console process for testing Valheim argument and Ctrl+C handling.
// It refuses to run outside an explicitly supplied disposable fixture root.
using System.Net;
using System.Net.Sockets;
using System.Text;

var root = Environment.GetEnvironmentVariable("TOGETHERSERVER_FIXTURE_ROOT");
string? Value(string key)
{
    var index = Array.IndexOf(args, key);
    return index < 0 || index + 1 >= args.Length ? null : args[index + 1];
}
bool Inside(string? path)
{
    if (root is null || path is null || !Path.IsPathFullyQualified(path)) return false;
    var relative = Path.GetRelativePath(root, path);
    return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) &&
        !Path.IsPathRooted(relative);
}
var saveDir = Value("-savedir");
var log = Value("-logFile");
if (!Inside(saveDir) || !Inside(log) || Value("-password") != "fixture-pass-123" ||
    Value("-name") != "Fixture \"Valheim\"" || Value("-world") != "fixture-world" ||
    Value("-public") != "0" || !int.TryParse(Value("-port"), out var gamePort) || gamePort is < 1024 or > 65534 ||
    !args.Contains("-nographics") || !args.Contains("-batchmode"))
    return 2;

using var gameSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
    { ExclusiveAddressUse = true };
gameSocket.Bind(new IPEndPoint(IPAddress.Any, gamePort));
using var querySocket = new UdpClient(new IPEndPoint(IPAddress.Any, gamePort + 1));
using var queryDone = new CancellationTokenSource();
var queryTask = File.Exists(Path.Combine(saveDir!, "synthetic-query-silent"))
    ? HoldQueryOpen(queryDone.Token)
    : ServeQuery(querySocket, saveDir!, queryDone.Token);

var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, eventArgs) =>
{
    if (eventArgs.SpecialKey != ConsoleSpecialKey.ControlC) return;
    eventArgs.Cancel = true;
    stop.TrySetResult();
};
await File.AppendAllTextAsync(log!, "Game server connected\n");
await stop.Task;
if (int.TryParse(Environment.GetEnvironmentVariable("TOGETHERSERVER_FIXTURE_STOP_DELAY_MS"), out var delayMs) &&
    delayMs is > 0 and <= 10000)
    await Task.Delay(delayMs);
await File.WriteAllTextAsync(Path.Combine(saveDir!, "synthetic-stop.marker"), "Ctrl+C received");
queryDone.Cancel();
await queryTask;
return 0;

static async Task ServeQuery(UdpClient query, string saveDirectory, CancellationToken token)
{
    try
    {
        while (!token.IsCancellationRequested)
        {
            var request = await query.ReceiveAsync(token);
            if (request.Buffer.Length < 5 || request.Buffer[0] != 0xff || request.Buffer[1] != 0xff ||
                request.Buffer[2] != 0xff || request.Buffer[3] != 0xff || request.Buffer[4] != 0x54)
                continue;
            var countPath = Path.Combine(saveDirectory, "synthetic-online-players.txt");
            var hasCount = File.Exists(countPath);
            byte reported = 0;
            var countIsValid = !hasCount || byte.TryParse(File.ReadAllText(countPath).Trim(), out reported);
            var online = countIsValid && hasCount ? Math.Min(reported, (byte)10) : (byte)0;
            using var packet = new MemoryStream();
            using (var writer = new BinaryWriter(packet, Encoding.UTF8, true))
            {
                writer.Write(-1);
                writer.Write((byte)0x49);
                writer.Write((byte)17);
                WriteCString(writer, "TogetherServer Valheim fixture");
                WriteCString(writer, "fixture-world");
                WriteCString(writer, "valheim");
                WriteCString(writer, "Valheim");
                writer.Write((ushort)0);
                writer.Write(online);
                writer.Write(countIsValid ? (byte)10 : (byte)0);
                writer.Write((byte)0);
                writer.Write((byte)'d');
                writer.Write((byte)'w');
                writer.Write((byte)1);
                writer.Write((byte)0);
                WriteCString(writer, "fixture");
            }
            await query.SendAsync(packet.ToArray(), request.RemoteEndPoint, token);
        }
    }
    catch (OperationCanceledException) { }
}

static async Task HoldQueryOpen(CancellationToken token)
{
    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
    catch (OperationCanceledException) { }
}

static void WriteCString(BinaryWriter writer, string value)
{
    writer.Write(Encoding.UTF8.GetBytes(value));
    writer.Write((byte)0);
}

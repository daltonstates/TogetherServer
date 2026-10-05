using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Synthetic Factorio process. It accepts only the fixed TogetherServer launch
// and RCON contract inside an explicitly disposable fixture root.
var fixtureRoot = Environment.GetEnvironmentVariable("TOGETHERSERVER_FACTORIO_FIXTURE_ROOT");
string? Value(string key)
{
    var index = Array.IndexOf(args, key);
    return index < 0 || index + 1 >= args.Length ? null : args[index + 1];
}

bool Inside(string? path)
{
    if (fixtureRoot is null || path is null || !Path.IsPathFullyQualified(path)) return false;
    var relative = Path.GetRelativePath(fixtureRoot, path);
    return relative != "." && relative != ".." &&
        !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !Path.IsPathRooted(relative);
}

var savePath = Value("--start-server");
var logPath = Value("--console-log");
var password = Value("--rcon-password");
var gamePortValid = int.TryParse(Value("--port"), out var gamePort) && gamePort is >= 1024 and <= 65535;
var rconPortValid = int.TryParse(Value("--rcon-port"), out var rconPort) && rconPort is >= 1024 and <= 65535;
var rejection = new List<string>();
if (args.Length != (logPath is null ? 8 : 10)) rejection.Add("argument-count");
if (logPath is not null && !Inside(logPath)) rejection.Add("log-outside-fixture-root");
if (!Inside(savePath)) rejection.Add("save-outside-fixture-root");
if (savePath is null || !File.Exists(savePath)) rejection.Add("save-missing");
if (!gamePortValid) rejection.Add("game-port");
if (!rconPortValid) rejection.Add("rcon-port");
if (gamePortValid && rconPortValid && gamePort == rconPort) rejection.Add("ports-overlap");
if (string.IsNullOrWhiteSpace(password) || password.Length < 24) rejection.Add("rcon-password");
if (rejection.Count > 0)
{
    try
    {
        if (fixtureRoot is not null)
            File.WriteAllLines(Path.Combine(fixtureRoot, "synthetic-factorio-start-rejected.txt"), rejection);
    }
    catch (Exception) { }
    return 2;
}

if (logPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    File.WriteAllText(logPath, "   0.000 Info MainLoop.cpp:1: Synthetic fixture started\n");
}

using var gameSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
{ ExclusiveAddressUse = true };
gameSocket.Bind(new IPEndPoint(IPAddress.Loopback, gamePort));
var listener = new TcpListener(IPAddress.Loopback, rconPort);
listener.Start();
using var lifetime = new CancellationTokenSource();
var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var serve = ServeRcon(listener, password!, savePath!, logPath, quit, lifetime.Token);
await quit.Task;
lifetime.Cancel();
listener.Stop();
try { await serve; }
catch (OperationCanceledException) { }
catch (SocketException) when (lifetime.IsCancellationRequested) { }
return 0;

static async Task ServeRcon(TcpListener listener, string password, string savePath, string? logPath,
    TaskCompletionSource quit, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        var auth = await ReadPacket(stream, token);
        if (auth.Type != 3 || !CryptographicEquals(auth.Body, password))
        {
            await WritePacket(stream, -1, 2, "", token);
            continue;
        }
        await WritePacket(stream, auth.Id, 2, "", token);
        var command = await ReadPacket(stream, token);
        if (command.Type != 2) continue;
        if (command.Body == "/silent-command rcon.print(\"TS_PLAYERS=\" .. #game.connected_players)")
        {
            var countPath = Path.Combine(Path.GetDirectoryName(savePath)!, "synthetic-online-players.txt");
            var count = File.Exists(countPath) && int.TryParse(File.ReadAllText(countPath).Trim(), out var parsed)
                ? parsed : 0;
            await WritePacket(stream, command.Id, 0, "TS_PLAYERS=" + count, token);
            continue;
        }
        if (command.Body == "/quit")
        {
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(savePath)!,
                "synthetic-save-confirmed.marker"), "authenticated /quit", token);
            await WritePacket(stream, command.Id, 0, "Quitting", token);
            quit.TrySetResult();
            continue;
        }
        if (command.Body == "/server-save")
        {
            var saveDirectory = Path.GetDirectoryName(savePath)!;
            await File.WriteAllTextAsync(Path.Combine(saveDirectory,
                "synthetic-server-save-received.marker"), "authenticated /server-save", token);
            // Disposable fixture artifact only. A readable ZIP here proves neither
            // Factorio completion nor that a real game can load a copied save.
            var closedArchive = Path.Combine(saveDirectory, "synthetic-live-save.zip");
            using (var file = new FileStream(closedArchive, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("fixture-save/level.dat").Open()))
                await writer.WriteAsync("synthetic live save".AsMemory(), token);
            var modePath = Path.Combine(saveDirectory, "synthetic-snapshot-mode.txt");
            var mode = File.Exists(modePath) ? File.ReadAllText(modePath).Trim() : "normal";
            var target = mode == "different-target" ? closedArchive : savePath;
            if (mode != "reply-only")
            {
                if (logPath is not null)
                    await File.AppendAllTextAsync(logPath,
                        $"   1.000 Info AppManager.cpp:394: Saving game as {target}\n", token);
                if (mode == "partial-archive")
                    await File.WriteAllBytesAsync(target, [0x50, 0x4b, 0x03, 0x04], token);
                else if (target != closedArchive)
                    File.Copy(closedArchive, target, overwrite: true);
                if (logPath is not null)
                    await File.AppendAllTextAsync(logPath,
                        "   1.001 Info AppManagerStates.cpp:1802: Saving finished\n", token);
            }
            await WritePacket(stream, command.Id, 0, "Save requested", token);
            continue;
        }
        await WritePacket(stream, command.Id, 0, "unsupported fixed fixture command", token);
    }
}

static bool CryptographicEquals(string left, string right)
{
    var a = Encoding.UTF8.GetBytes(left);
    var b = Encoding.UTF8.GetBytes(right);
    return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
}

static async Task<RconPacket> ReadPacket(Stream stream, CancellationToken token)
{
    var lengthBytes = new byte[4];
    await stream.ReadExactlyAsync(lengthBytes, token);
    var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
    if (length is < 10 or > 64 * 1024) throw new InvalidDataException("Invalid fixture RCON length.");
    var payload = new byte[length];
    await stream.ReadExactlyAsync(payload, token);
    if (payload[^2] != 0 || payload[^1] != 0) throw new InvalidDataException("Invalid fixture RCON terminator.");
    return new(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4)),
        BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4)),
        Encoding.UTF8.GetString(payload, 8, length - 10));
}

static async Task WritePacket(Stream stream, int id, int type, string body, CancellationToken token)
{
    var bodyBytes = Encoding.UTF8.GetBytes(body);
    var length = 10 + bodyBytes.Length;
    var packet = new byte[length + 4];
    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), length);
    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), id);
    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), type);
    bodyBytes.CopyTo(packet.AsSpan(12));
    await stream.WriteAsync(packet, token);
    await stream.FlushAsync(token);
}

readonly record struct RconPacket(int Id, int Type, string Body);

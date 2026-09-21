// Synthetic console process for testing Valheim argument and Ctrl+C handling.
// It refuses to run outside an explicitly supplied disposable fixture root.
using System.Net;
using System.Net.Sockets;

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
using var querySocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
    { ExclusiveAddressUse = true };
gameSocket.Bind(new IPEndPoint(IPAddress.Any, gamePort));
querySocket.Bind(new IPEndPoint(IPAddress.Any, gamePort + 1));

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
return 0;

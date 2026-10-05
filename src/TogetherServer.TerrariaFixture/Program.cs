using System.Net;
using System.Net.Sockets;

// Synthetic console process only. It refuses paths outside its disposable root
// and recognizes exactly the fixed arguments used by TerrariaServerDriver.
var root = Environment.GetEnvironmentVariable("TOGETHERSERVER_TERRARIA_FIXTURE_ROOT");
if (string.IsNullOrWhiteSpace(root) || args.Length != 5 ||
    args[0] != "-world" || args[2] != "-port" || args[4] != "-noupnp" ||
    !int.TryParse(args[3], out var port) || port is < 1024 or > 65535)
    return 2;

var world = Path.GetFullPath(args[1]);
var relative = Path.GetRelativePath(Path.GetFullPath(root), world);
if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
        StringComparison.Ordinal) || Path.IsPathRooted(relative) ||
    !Path.GetExtension(world).Equals(".wld", StringComparison.OrdinalIgnoreCase) ||
    !File.Exists(world))
    return 2;

var directory = Path.GetDirectoryName(world)!;
using var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
while (true)
{
    var command = Console.ReadLine();
    if (command is null) return 3;
    File.AppendAllText(Path.Combine(directory, "synthetic-console-lines.txt"), command + Environment.NewLine);
    if (command == "save")
    {
        Console.WriteLine("Saving world data: 100%");
        File.WriteAllText(Path.Combine(directory, "synthetic-save-received.marker"), "literal save");
        Console.WriteLine("World saved.");
    }
    else if (command == "exit")
    {
        File.WriteAllText(Path.Combine(directory, "synthetic-exit-received.marker"), "literal exit");
        return 0;
    }
}

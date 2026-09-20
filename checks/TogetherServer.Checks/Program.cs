using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using TogetherServer;

var fixture = Path.GetFullPath("src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe");
if (!File.Exists(fixture)) throw new FileNotFoundException("Build the fixture in Release first.", fixture);
var root = Path.GetFullPath("local-data/checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}

HostSettings Settings(params ServerProfile[] profiles) => new() { MaxConcurrentServers = 2, Profiles = [.. profiles] };
ServerProfile Profile(string name, string world, int port, string? directory = null)
{
    var path = directory ?? Path.Combine(root, name);
    Directory.CreateDirectory(path);
    return new ServerProfile { Name = name, WorldId = world, WorldDirectory = path, GamePort = port, ExecutablePath = fixture };
}
void Require(bool value, string message) { if (!value) throw new Exception(message); }
int FreePort()
{
    for (var i = 0; i < 100; i++)
    {
        var port = Random.Shared.Next(35000, 59000);
        try
        {
            using var one = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            using var two = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            one.Bind(new IPEndPoint(IPAddress.Any, port));
            two.Bind(new IPEndPoint(IPAddress.Any, port + 1));
            return port;
        }
        catch (SocketException) { }
    }
    throw new Exception("No free UDP pair found.");
}
LocalData Data(string name) => new(Path.Combine(root, name));

await Check("duplicate start is serialized", async () =>
{
    using var data = Data("duplicate");
    var manager = new HostManager(data);
    var profile = Profile("duplicate-world", "duplicate-world", FreePort());
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    try
    {
        var starts = await Task.WhenAll(manager.StartAsync(profile.Id), manager.StartAsync(profile.Id));
        Require(starts.Count(result => result.Ok) == 1, "expected one successful launch");
        Require(starts.Single(result => !result.Ok).Code == "AlreadyManaged", "duplicate was not rejected");
        Require((await manager.HealthAsync(profile.Id)).Code == "FixtureProcessRunning", "fixture identity missing");
    }
    finally { Require((await manager.StopAsync(profile.Id)).Ok, "fixture cleanup failed"); }
});

await Check("one writer per world", async () =>
{
    using var data = Data("world");
    var manager = new HostManager(data);
    var port = FreePort();
    var a = Profile("world-a", "same-world", port);
    var b = Profile("world-b", "same-world", port + 10);
    Require((await manager.UpdateSettingsAsync(Settings(a, b))).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try { Require((await manager.StartAsync(b.Id)).Code == "WorldConflict", "second world writer was allowed"); }
    finally { Require((await manager.StopAsync(a.Id)).Ok, "fixture cleanup failed"); }
});

await Check("managed maximum", async () =>
{
    using var data = Data("maximum");
    var manager = new HostManager(data);
    var port = FreePort();
    var a = Profile("max-a", "max-a", port);
    var b = Profile("max-b", "max-b", port + 10);
    var settings = Settings(a, b);
    settings.MaxConcurrentServers = 1;
    Require((await manager.UpdateSettingsAsync(settings)).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try { Require((await manager.StartAsync(b.Id)).Code == "MaxConcurrent", "maximum was bypassed"); }
    finally { Require((await manager.StopAsync(a.Id)).Ok, "fixture cleanup failed"); }
});

await Check("managed port overlap", async () =>
{
    using var data = Data("ports");
    var manager = new HostManager(data);
    var port = FreePort();
    var a = Profile("port-a", "port-a", port);
    var b = Profile("port-b", "port-b", port + 1);
    Require((await manager.UpdateSettingsAsync(Settings(a, b))).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try { Require((await manager.StartAsync(b.Id)).Code == "PortConflict", "overlap was allowed"); }
    finally { Require((await manager.StopAsync(a.Id)).Ok, "fixture cleanup failed"); }
});

await Check("occupied local UDP port", async () =>
{
    using var data = Data("bound-port");
    var manager = new HostManager(data);
    var port = FreePort();
    var profile = Profile("bound-port", "bound-port", port);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
    socket.Bind(new IPEndPoint(IPAddress.Any, port));
    Require((await manager.StartAsync(profile.Id)).Code == "PortInUse", "occupied port was allowed");
});

await Check("restart reattaches exact fixture identity", async () =>
{
    var profile = Profile("restart", "restart", FreePort());
    using (var data = Data("restart"))
    {
        var manager = new HostManager(data);
        Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
        Require((await manager.StartAsync(profile.Id)).Ok, "start failed");
    }
    using (var data = Data("restart"))
    {
        var manager = new HostManager(data);
        Require((await manager.HealthAsync(profile.Id)).Code == "FixtureProcessRunning", "reattach identity failed");
        Require((await manager.StopAsync(profile.Id)).Ok, "reattached fixture stop failed");
    }
});

await Check("identity mismatch blocks start and never stops an unrelated process", async () =>
{
    var a = Profile("identity-a", "identity-a", FreePort());
    var b = Profile("identity-b", "identity-b", FreePort());
    ManagedRun original;
    using (var data = Data("identity-a"))
    {
        var manager = new HostManager(data);
        Require((await manager.UpdateSettingsAsync(Settings(a))).Ok, "A settings failed");
        Require((await manager.StartAsync(a.Id)).Ok, "A start failed");
        original = data.LoadRuns().Single();
    }
    using var otherData = Data("identity-b");
    var other = new HostManager(otherData);
    Require((await other.UpdateSettingsAsync(Settings(b))).Ok, "B settings failed");
    Require((await other.StartAsync(b.Id)).Ok, "B start failed");
    var unrelated = otherData.LoadRuns().Single();
    try
    {
        using var data = Data("identity-a");
        var changed = data.LoadRuns().Single();
        changed.ProcessId = unrelated.ProcessId; // A stale or corrupt PID must never become authority over B.
        data.SaveRuns([changed]);
        var manager = new HostManager(data);
        Require((await manager.HealthAsync(a.Id)).Code == "IdentityUnknown", "identity mismatch not detected");
        Require((await manager.StopAsync(a.Id)).Code == "IdentityUnknown", "unrelated process received a stop");
        Require((await manager.StartAsync(a.Id)).Code == "AlreadyManaged", "unknown world accepted a second writer");
        Require((await other.HealthAsync(b.Id)).Code == "FixtureProcessRunning", "unrelated process was stopped");
    }
    finally
    {
        await DirectFixtureStop(original);
        Require((await other.StopAsync(b.Id)).Ok, "B cleanup failed");
    }
});

Console.WriteLine($"Checks: {passed} passed, {failed} failed. Fixture data: {root}");
return failed == 0 ? 0 : 1;

static async Task DirectFixtureStop(ManagedRun run)
{
    using var pipe = new NamedPipeClientStream(".", run.StopPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await pipe.ConnectAsync(timeout.Token);
    using var writer = new StreamWriter(pipe) { AutoFlush = true };
    await writer.WriteLineAsync("stop");
    using var process = Process.GetProcessById(run.ProcessId!.Value);
    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    await process.WaitForExitAsync(exitTimeout.Token);
}

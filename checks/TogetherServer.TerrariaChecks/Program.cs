using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using TogetherServer;

if (MinecraftConsoleCapture.IsCommand(args))
{
    Environment.ExitCode = await MinecraftConsoleCapture.RunAsync(args);
    return Environment.ExitCode;
}

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Terraria console journey requires Windows.");
var fixture = Path.GetFullPath("src/TogetherServer.TerrariaFixture/bin/Release/net10.0/TerrariaServer.exe");
if (!File.Exists(fixture)) throw new FileNotFoundException("Build the synthetic Terraria fixture first.", fixture);
var root = Path.GetFullPath("local-data/terraria-checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("TOGETHERSERVER_TERRARIA_FIXTURE_ROOT", root);
var passed = 0;
var failed = 0;

void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

void Report(string message)
{
    File.AppendAllText(Path.Combine(root, "results.txt"), message + Environment.NewLine);
    try { Console.WriteLine(message); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    { /* Console dispatch may detach this check process from its original console. */ }
}

async Task Check(string name, Func<Task> test)
{
    try { await test(); Report("PASS " + name); passed++; }
    catch (Exception ex) { Report("FAIL " + name + ": " + ex); failed++; }
}

await Check("Terraria captured save completion and sealed snapshot",
    () => GameConsoleSnapshotChecks.RunTerrariaProcessAsync(root, fixture));

int FreePort()
{
    // Check runners never bind a socket. The packaged fixture owns its own
    // listener when this separate interactive journey is explicitly run.
    var occupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
        .Select(endpoint => endpoint.Port).ToHashSet();
    for (var attempt = 0; attempt < 128; attempt++)
    {
        var candidate = RandomNumberGenerator.GetInt32(49152, 65536);
        if (!occupied.Contains(candidate)) return candidate;
    }
    throw new InvalidOperationException("No candidate Terraria fixture port was available.");
}

await Check("Terraria candidate dispatches literal save to its exact running fixture only", async () =>
{
    using var data = new LocalData(Path.Combine(root, "data"));
    var source = Path.Combine(root, "original.wld");
    File.WriteAllText(source, "disposable synthetic Terraria world");
    var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
    var profile = new ServerProfile
    {
        Kind = GameKinds.Terraria,
        Name = "Terraria command fixture",
        ServerName = "Terraria command fixture",
        ExecutablePath = fixture,
        GamePort = FreePort()
    };
    var imported = TerrariaSetup.ImportCopy(data, profile.Id, source);
    Require(imported.Ok && imported.WorldId is not null && imported.WorldDirectory is not null,
        "the disposable Terraria world was not imported");
    profile.WorldId = imported.WorldId!;
    profile.WorldDirectory = imported.WorldDirectory!;
    var driver = new TerrariaServerDriver(data);
    Require(driver.ValidateForStart(profile) is null, "the reviewed Terraria driver rejected the fixture");

    var manager = new HostManager(data, new GameServerRegistry(data, false, PortProbeMode.ObserveOnly));
    Require((await manager.UpdateSettingsAsync(new HostSettings { Profiles = [profile] })).Ok,
        "the Terraria fixture profile was rejected");
    var started = await manager.StartAsync(profile.Id);
    Require(started.Ok, $"the Terraria fixture did not start: {started.Code} {started.Message}");
    var run = data.LoadRuns().Single();
    var saveMarker = Path.Combine(profile.WorldDirectory, "synthetic-save-received.marker");
    var consoleLines = Path.Combine(profile.WorldDirectory, "synthetic-console-lines.txt");
    var commandPort = new ExactManagedTerrariaLiveSaveCommandPort(data);
    try
    {
        for (var attempt = 0; attempt < 40 && !driver.Health(run).Ok; attempt++)
            await Task.Delay(100);
        Require(driver.Health(run).Ok, "the synthetic Terraria TCP listener did not open");

        void RequireRejected(Guid operationId, string reason)
        {
            var rejected = false;
            try { commandPort.RequestTerrariaSave(operationId); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, reason + " reached the console");
        }

        RequireRejected(Guid.Empty, "an empty operation");
        RequireRejected(Guid.NewGuid(), "a stale operation");

        var recorded = data.LoadRuns();
        var wrongProcess = data.LoadRuns();
        wrongProcess.Single().StartTimeUtcTicks++;
        data.SaveRuns(wrongProcess);
        try { RequireRejected(run.OperationId, "a stale process identity"); }
        finally { data.SaveRuns(recorded); }

        var wrongPid = data.LoadRuns();
        wrongPid.Single().ProcessId = Environment.ProcessId;
        data.SaveRuns(wrongPid);
        try { RequireRejected(run.OperationId, "a different process ID"); }
        finally { data.SaveRuns(recorded); }

        var stoppedRecord = data.LoadRuns();
        stoppedRecord.Single().StopRequestedUtc = DateTimeOffset.UtcNow;
        data.SaveRuns(stoppedRecord);
        try { RequireRejected(run.OperationId, "a stop-pending run"); }
        finally { data.SaveRuns(recorded); }

        var settings = data.LoadSettings();
        var savedProfile = settings.Profiles.Single();
        var worldId = savedProfile.WorldId;
        savedProfile.WorldId = "different-world";
        data.SaveSettings(settings);
        try { RequireRejected(run.OperationId, "a changed saved world"); }
        finally
        {
            savedProfile.WorldId = worldId;
            data.SaveSettings(settings);
        }
        Require(!File.Exists(saveMarker) && !File.Exists(consoleLines),
            "a rejected candidate wrote to the fixture console");

        var sent = commandPort.RequestTerrariaSave(run.OperationId);
        Require(sent.OperationId == run.OperationId && sent.FixedCommand == "save" &&
            !sent.CompletionConfirmed, "dispatch claimed a completed Terraria save");
        for (var attempt = 0; attempt < 40 && !File.Exists(saveMarker); attempt++)
            await Task.Delay(100);
        Require(File.Exists(saveMarker) && File.ReadAllText(saveMarker) == "literal save",
            "the fixed save command did not reach the fixture");
        Require(File.ReadAllLines(consoleLines).SequenceEqual(["save"]),
            "the candidate sent something besides literal save");
        using (var process = Process.GetProcessById(run.ProcessId!.Value))
            Require(!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks,
                "the candidate stopped or replaced the exact fixture process");
        Require(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(sourceHash) &&
            SHA256.HashData(File.ReadAllBytes(Path.Combine(profile.WorldDirectory, profile.WorldId + ".wld")))
                .SequenceEqual(sourceHash), "the command fixture changed world bytes");
        Require(!SharedWorldLiveSaveAdapters.ForGame(GameKinds.Terraria)!.LiveCaptureAccepted &&
            !SharedWorldLiveSaveAdapters.Status(GameKinds.Terraria).Available,
            "fixture dispatch enabled live capture or sharing");

        var stopped = await manager.StopAsync(profile.Id);
        Require(stopped.Ok, $"the fixed Terraria exit did not stop the fixture: {stopped.Code} {stopped.Message}");
        Require(File.Exists(Path.Combine(profile.WorldDirectory, "synthetic-exit-received.marker")) &&
            File.ReadAllLines(consoleLines).SequenceEqual(["save", "exit"]),
            "the fixture did not receive save followed by the existing fixed exit");
    }
    finally
    {
        // Cleanup may touch only this exact synthetic child, never a process found by name.
        try
        {
            using var process = Process.GetProcessById(run.ProcessId!.Value);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                Path.GetFullPath(process.MainModule!.FileName).Equals(fixture, StringComparison.OrdinalIgnoreCase))
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException) { }
    }
});

Report($"{passed} Terraria command check groups passed; {failed} failed. Synthetic dispatch is not save completion.");
return failed == 0 ? 0 : 1;

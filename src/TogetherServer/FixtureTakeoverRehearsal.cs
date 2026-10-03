using System.Diagnostics;

namespace TogetherServer;

// Exercises the reviewed synthetic driver against a disposable save copy. This is
// process lifecycle evidence only; the fixture cannot prove that a real game loaded.
internal static class FixtureTakeoverRehearsal
{
    internal sealed record Result(bool Passed, bool SafeToClean, string Message);

    internal static Result Run(string worldRoot, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            !File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            return new(false, true, "Choose a local synthetic fixture before testing the managed process.");
        var driver = new FixtureServerDriver();
        var profile = new ServerProfile
        {
            Id = Guid.NewGuid(), Kind = GameKinds.Fixture, WorldDirectory = worldRoot,
            ExecutablePath = executable, GamePort = 0
        };
        if (driver.ValidateForStart(profile) is not null)
            return new(false, true, "The selected server is not the reviewed synthetic fixture.");
        var run = new ManagedRun
        {
            ProfileId = profile.Id, OperationId = Guid.NewGuid(), Kind = GameKinds.Fixture,
            WorldDirectory = worldRoot, ExecutablePath = executable,
            StopPipeName = "TogetherServer.Fixture." + Guid.NewGuid().ToString("N")
        };
        int? processId = null;
        long? startedTicks = null;
        try
        {
            driver.PrepareStart(profile, run);
            processId = driver.Start(profile, run).ProcessId;
            using var process = Process.GetProcessById(processId.Value);
            startedTicks = process.StartTime.ToUniversalTime().Ticks;
            run.ProcessId = processId;
            run.StartTimeUtcTicks = startedTicks;
            if (process.HasExited || driver.Health(run) is not { Ok: true })
                return new(false, true, "The disposable fixture exited before its managed process check.");
            var stop = driver.StopAsync(process, run).GetAwaiter().GetResult();
            return stop.Code == "FixtureStopped" && process.HasExited
                ? new(true, true, "The disposable managed fixture stopped cleanly.")
                : new(false, process.HasExited, "The disposable fixture did not stop cleanly. Review its process before cleanup.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            OperationCanceledException)
        {
            if (processId is null)
                return new(false, true, "The disposable fixture could not launch.");
            try
            {
                using var process = Process.GetProcessById(processId.Value);
                if (process.HasExited) return new(false, true, "The disposable fixture exited unexpectedly.");
                if (startedTicks is not null && process.StartTime.ToUniversalTime().Ticks == startedTicks)
                {
                    var stop = driver.StopAsync(process, run).GetAwaiter().GetResult();
                    return new(false, stop.Code == "FixtureStopped" && process.HasExited,
                        "The disposable fixture failed its managed process check.");
                }
            }
            catch (ArgumentException) { return new(false, true, "The disposable fixture already exited."); }
            catch (Exception stopError) when (stopError is IOException or InvalidOperationException or
                UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
            { /* Leave the isolated copy for review while process ownership is unresolved. */ }
            return new(false, false,
                "The disposable process could not be stopped safely. Keep its isolated copy for review.");
        }
    }
}

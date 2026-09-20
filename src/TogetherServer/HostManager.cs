using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

namespace TogetherServer;

public sealed record RunView(Guid ProfileId, string State, string Detail, int? ProcessId);
public sealed record HostSnapshot(HostSettings Settings, IReadOnlyList<RunView> Runs,
    string Evidence, string Mode);
public sealed record ActionResult(bool Ok, string Code, string Message, HostSnapshot Snapshot);

public sealed class HostManager(LocalData data)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HostSettings settings = data.LoadSettings();
    private readonly List<ManagedRun> runs = data.LoadRuns();

    public async Task<HostSnapshot> SnapshotAsync()
    {
        await gate.WaitAsync();
        try { return Snapshot(); }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> UpdateSettingsAsync(HostSettings next)
    {
        await gate.WaitAsync();
        try
        {
            var error = Validate(next);
            if (error is not null) return Result(false, "InvalidSettings", error);
            foreach (var run in runs)
            {
                var oldProfile = settings.Profiles.SingleOrDefault(p => p.Id == run.ProfileId);
                var newProfile = next.Profiles.SingleOrDefault(p => p.Id == run.ProfileId);
                if (oldProfile is null || newProfile is null || !SameProfile(oldProfile, newProfile))
                    return Result(false, "ProfileInUse", "Stop or resolve a managed run before changing its profile.");
            }
            data.SaveSettings(next);
            settings = next;
            return Result(true, "SettingsSaved", "Host settings saved.");
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> StartAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(p => p.Id == profileId);
            if (profile is null) return Result(false, "UnknownProfile", "Choose a saved profile.");
            if (runs.Any(r => r.ProfileId == profileId))
                return Result(false, "AlreadyManaged", "This profile already has a managed or unresolved run.");
            if (runs.Any(r => WorldConflict(r, profile)))
                return Result(false, "WorldConflict", "Another managed run owns this world or save directory.");
            if (runs.Any(r => PortsOverlap(r.GamePort, profile.GamePort)))
                return Result(false, "PortConflict", "Another managed run owns one of these game ports.");
            if (runs.Count >= settings.MaxConcurrentServers)
                return Result(false, "MaxConcurrent", "The managed server limit has been reached.");
            if (!File.Exists(profile.ExecutablePath) ||
                !Path.GetFileName(profile.ExecutablePath).Equals("TogetherServer.Fixture.exe", StringComparison.OrdinalIgnoreCase))
                return Result(false, "FixtureRequired", "Select a built TogetherServer.Fixture.exe for this slice.");
            if (!Directory.Exists(profile.WorldDirectory))
                return Result(false, "MissingWorldDirectory", "Select an existing save directory. The fixture will not write to it.");
            if (!PortsFree(profile.GamePort))
                return Result(false, "PortInUse", "One of the two UDP game ports is already in use.");

            var run = new ManagedRun
            {
                ProfileId = profile.Id,
                OperationId = Guid.NewGuid(),
                WorldId = profile.WorldId,
                WorldDirectory = Path.GetFullPath(profile.WorldDirectory),
                GamePort = profile.GamePort,
                ExecutablePath = Path.GetFullPath(profile.ExecutablePath),
                StopPipeName = "TogetherServer.Fixture." + Guid.NewGuid().ToString("N")
            };
            runs.Add(run);
            data.SaveRuns(runs); // An interrupted launch stays Unknown, blocking a second writer.
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo(run.ExecutablePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(run.ExecutablePath)!
                };
                process.StartInfo.ArgumentList.Add("--stop-pipe");
                process.StartInfo.ArgumentList.Add(run.StopPipeName);
                if (!process.Start()) throw new InvalidOperationException("The fixture did not start.");
                run.ProcessId = process.Id;
                run.StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                data.SaveRuns(runs);
                return Result(true, "FixtureStarted", "Fixture process started. Game readiness and world saving are unverified.");
            }
            catch (Exception ex)
            {
                if (run.ProcessId is null)
                {
                    runs.Remove(run);
                    data.SaveRuns(runs);
                }
                return Result(false, "LaunchFailed", "Fixture launch failed: " + ex.Message);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> StopAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var run = runs.SingleOrDefault(r => r.ProfileId == profileId);
            if (run is null) return Result(false, "NotManaged", "This profile has no managed process.");
            if (Identity(run) != "Matched")
                return Result(false, "IdentityUnknown", "Process identity is unverified. No stop signal was sent.");
            try
            {
                using var process = Process.GetProcessById(run.ProcessId!.Value);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != run.StartTimeUtcTicks ||
                    !Path.GetFullPath(process.MainModule!.FileName).Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                    return Result(false, "IdentityUnknown", "Process identity changed. No stop signal was sent.");
                using var pipe = new NamedPipeClientStream(".", run.StopPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await pipe.ConnectAsync(connectTimeout.Token);
                using (var writer = new StreamWriter(pipe) { AutoFlush = true })
                    await writer.WriteLineAsync("stop");
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await process.WaitForExitAsync(exitTimeout.Token);
                if (process.ExitCode != 0)
                    return Result(false, "StopFailed", "Fixture exited with a nonzero status. Run remains recorded.");
                runs.Remove(run);
                data.SaveRuns(runs);
                return Result(true, "FixtureStopped", "Fixture exited cleanly. This is not a Valheim save check.");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                return Result(false, "StopUnconfirmed", "Stop was not confirmed: " + ex.Message);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> HealthAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var run = runs.SingleOrDefault(r => r.ProfileId == profileId);
            if (run is null) return Result(false, "NotManaged", "No managed process is recorded.");
            return Identity(run) switch
            {
                "Matched" => Result(true, "FixtureProcessRunning", "Fixture identity matches. Game readiness is unverified."),
                "Missing" => Result(false, "ProcessExited", "The recorded process is no longer running."),
                _ => Result(false, "IdentityUnknown", "The recorded process identity cannot be verified.")
            };
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> ForgetAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var run = runs.SingleOrDefault(r => r.ProfileId == profileId);
            if (run is null) return Result(false, "NotManaged", "There is no record to resolve.");
            if (Identity(run) == "Matched")
                return Result(false, "StillRunning", "The recorded process is still running and cannot be forgotten.");
            runs.Remove(run);
            data.SaveRuns(runs);
            return Result(true, "RecordCleared", "The unresolved record was cleared by the local owner.");
        }
        finally { gate.Release(); }
    }

    private HostSnapshot Snapshot()
    {
        var views = settings.Profiles.Select(profile =>
        {
            var run = runs.SingleOrDefault(r => r.ProfileId == profile.Id);
            if (run is null) return new RunView(profile.Id, "Offline", "No managed process", null);
            var identity = Identity(run);
            return identity switch
            {
                "Matched" => new RunView(profile.Id, "Process running", "Synthetic fixture only; no game readiness signal", run.ProcessId),
                "Missing" => new RunView(profile.Id, "Failed", "Recorded process exited; owner can clear the record", run.ProcessId),
                _ => new RunView(profile.Id, "Unknown", "Process identity cannot be proven; start and stop are blocked", run.ProcessId)
            };
        }).ToList();
        return new HostSnapshot(settings, views, "Synthetic fixture / process identity only", "Host");
    }

    private ActionResult Result(bool ok, string code, string message) => new(ok, code, message, Snapshot());

    private static string? Validate(HostSettings next)
    {
        if (next.MaxConcurrentServers < 1 || next.MaxConcurrentServers > 16) return "Maximum servers must be between 1 and 16.";
        if (next.IdleMinutes < 1 || next.IdleMinutes > 1440) return "Idle minutes must be between 1 and 1440.";
        if (next.AutoShutdownEnabled) return "Auto shutdown is unavailable until real player coverage is verified.";
        if (next.RemoteControlsEnabled) return "Remote controls are unavailable until pairing and TLS are implemented.";
        if (next.Profiles is null || next.Profiles.Select(p => p.Id).Distinct().Count() != next.Profiles.Count)
            return "Each profile needs a unique ID.";
        foreach (var profile in next.Profiles)
        {
            if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.WorldId))
                return "Each profile needs a name and world ID.";
            if (profile.GamePort < 1024 || profile.GamePort > 65534) return "Game port must be between 1024 and 65534.";
            if (!Path.IsPathFullyQualified(profile.ExecutablePath) || !Path.IsPathFullyQualified(profile.WorldDirectory))
                return "Executable and save directory must be absolute paths.";
        }
        return null;
    }

    private static bool SameProfile(ServerProfile a, ServerProfile b) =>
        a.Name == b.Name && a.WorldId == b.WorldId && a.GamePort == b.GamePort &&
        Path.GetFullPath(a.WorldDirectory).Equals(Path.GetFullPath(b.WorldDirectory), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(a.ExecutablePath).Equals(Path.GetFullPath(b.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static bool WorldConflict(ManagedRun run, ServerProfile profile) =>
        run.WorldId.Equals(profile.WorldId, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(run.WorldDirectory).Equals(Path.GetFullPath(profile.WorldDirectory), StringComparison.OrdinalIgnoreCase);

    private static bool PortsOverlap(int a, int b) => a == b || a == b + 1 || a + 1 == b;

    private static bool PortsFree(int port)
    {
        try
        {
            using var first = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            using var second = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            first.Bind(new IPEndPoint(IPAddress.Any, port));
            second.Bind(new IPEndPoint(IPAddress.Any, port + 1));
            return true;
        }
        catch (SocketException) { return false; }
    }

    private static string Identity(ManagedRun run)
    {
        if (run.ProcessId is null || run.StartTimeUtcTicks is null) return "Unknown";
        try
        {
            using var process = Process.GetProcessById(run.ProcessId.Value);
            if (process.HasExited) return "Missing";
            var executable = process.MainModule?.FileName;
            if (executable is null) return "Unknown";
            return process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                Path.GetFullPath(executable).Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                ? "Matched" : "Unknown";
        }
        catch (ArgumentException) { return "Missing"; }
        catch (InvalidOperationException) { return "Missing"; }
        catch (System.ComponentModel.Win32Exception) { return "Unknown"; }
    }
}

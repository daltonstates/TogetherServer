using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

namespace TogetherServer;

public sealed record RunView(Guid ProfileId, string State, string Detail, int? ProcessId);
public sealed record HostSnapshot(HostSettings Settings, IReadOnlyList<RunView> Runs,
    string Evidence, string Mode, bool? OwnerGameRunning, DateTimeOffset OwnerCheckedUtc,
    IReadOnlyDictionary<Guid, bool> PasswordConfigured);
public sealed record ActionResult(bool Ok, string Code, string Message, HostSnapshot Snapshot);

public sealed class HostManager(LocalData data)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private HostSettings settings = data.LoadSettings();
    private readonly List<ManagedRun> runs = data.LoadRuns();

    public bool CompanionListeningEnabled => Volatile.Read(ref settings).CompanionListeningEnabled;

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
            if (next.CompanionListeningEnabled &&
                (!data.HasProtected("host-certificate.protected") ||
                 !data.LoadDevices().Any(device => !device.Revoked &&
                    (device.InviteHash is not null || device.CredentialHash is not null))))
                return Result(false, "PairingRequired", "Create a pairing invite and Host TLS identity before enabling the listener.");
            if (next.RemoteControlsEnabled &&
                !data.LoadDevices().Any(device => !device.Revoked && device.CredentialHash is not null))
                return Result(false, "PairedDeviceRequired", "Activate a paired device before enabling remote controls.");
            foreach (var run in runs)
            {
                var oldProfile = settings.Profiles.SingleOrDefault(p => p.Id == run.ProfileId);
                var newProfile = next.Profiles.SingleOrDefault(p => p.Id == run.ProfileId);
                if (oldProfile is null || newProfile is null || !SameProfile(oldProfile, newProfile))
                    return Result(false, "ProfileInUse", "Stop or resolve a managed run before changing its profile.");
            }
            data.SaveSettings(next);
            if (settings.RemoteControlsEnabled != next.RemoteControlsEnabled)
                data.Audit($"remote-controls {(next.RemoteControlsEnabled ? "enabled" : "disabled")} {DateTimeOffset.UtcNow:O}");
            settings = next;
            return Result(true, "SettingsSaved", "Host settings saved.");
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> SetValheimPasswordAsync(Guid profileId, string? password)
    {
        await gate.WaitAsync();
        try
        {
            if (settings.Profiles.SingleOrDefault(profile => profile.Id == profileId)?.Kind != "Valheim")
                return Result(false, "InvalidProfile", "Choose a saved Valheim profile first.");
            if (password is null || password.Length is < 5 or > 64 || password.Any(char.IsControl))
                return Result(false, "InvalidPassword", "Enter a server password of 5 to 64 characters without control characters.");
            data.SaveValheimPassword(profileId, password);
            return Result(true, "PasswordSaved", "Valheim password saved in Windows protected storage.");
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
            if (!File.Exists(profile.ExecutablePath))
                return Result(false, "ExecutableMissing", "Selected server executable does not exist.");
            if (profile.Kind == "Fixture" &&
                !Path.GetFileName(profile.ExecutablePath).Equals("TogetherServer.Fixture.exe", StringComparison.OrdinalIgnoreCase))
                return Result(false, "FixtureRequired", "Select a built TogetherServer.Fixture.exe.");
            if (profile.Kind == "Valheim" &&
                !Path.GetFileName(profile.ExecutablePath).Equals("valheim_server.exe", StringComparison.OrdinalIgnoreCase))
                return Result(false, "ValheimExecutableRequired", "Select the installed valheim_server.exe.");
            if (!Directory.Exists(profile.WorldDirectory))
                return Result(false, "MissingWorldDirectory", "Select an existing save directory. TogetherServer will not create or replace it.");
            if (profile.Kind == "Valheim" && profile.WorldSource == "Existing" &&
                !ValheimSetup.HasWorldData(profile.WorldDirectory, profile.WorldId))
                return Result(false, "MissingWorldData", "Existing world needs a complete .db/.fwl pair or chunked folder in worlds_local. Import a copy before Start; no new seed was created.");
            if (profile.Kind == "Valheim" && profile.WorldSource == "Existing" &&
                !ValheimSetup.IsImportedWorld(data, profile.Id, profile.WorldDirectory))
                return Result(false, "WorldImportRequired", "Import a separate copy of the existing world before Start. Its original save stays untouched.");
            if (profile.Kind == "Valheim" && profile.WorldSource == "New" &&
                ValheimSetup.HasAnyWorldFile(profile.WorldDirectory, profile.WorldId))
                return Result(false, "WorldAlreadyExists", "A world file already exists under this name. Choose a different new-world name.");
            var password = profile.Kind == "Valheim" ? data.LoadValheimPassword(profile.Id) : null;
            if (profile.Kind == "Valheim" && string.IsNullOrEmpty(password))
                return Result(false, "PasswordRequired", "Set a protected Valheim server password before starting.");
            if (!PortsFree(profile.GamePort))
                return Result(false, "PortInUse", "One of the two UDP game ports is already in use.");

            var run = new ManagedRun
            {
                ProfileId = profile.Id,
                OperationId = Guid.NewGuid(),
                Kind = profile.Kind,
                WorldId = profile.WorldId,
                WorldDirectory = Path.GetFullPath(profile.WorldDirectory),
                GamePort = profile.GamePort,
                ExecutablePath = Path.GetFullPath(profile.ExecutablePath),
                StopPipeName = "TogetherServer.Fixture." + Guid.NewGuid().ToString("N")
            };
            if (profile.Kind == "Valheim") run.LogPath = data.NewRunLogPath(run.OperationId);
            runs.Add(run);
            data.SaveRuns(runs); // An interrupted launch stays Unknown, blocking a second writer.
            try
            {
                if (run.Kind == "Valheim")
                {
                    var arguments = new List<string> { "-nographics", "-batchmode", "-name", profile.ServerName,
                        "-port", profile.GamePort.ToString(), "-world", profile.WorldId, "-password", password!,
                        "-savedir", run.WorldDirectory, "-public", profile.PublicListing ? "1" : "0", "-logFile", run.LogPath };
                    if (profile.Crossplay) arguments.Add("-crossplay");
                    run.ProcessId = WindowsConsoleProcess.Start(run.ExecutablePath, arguments, "892970");
                }
                else
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
                }
                using (var started = Process.GetProcessById(run.ProcessId.Value))
                    run.StartTimeUtcTicks = started.StartTime.ToUniversalTime().Ticks;
                data.SaveRuns(runs);
                return run.Kind == "Valheim"
                    ? Result(true, "ValheimStarting", "Valheim process launched. Waiting for its server-connected log signal; join and save are unverified.")
                    : Result(true, "FixtureStarted", "Fixture process started. Game readiness and world saving are unverified.");
            }
            catch (Exception ex)
            {
                if (run.ProcessId is null)
                {
                    runs.Remove(run);
                    data.SaveRuns(runs);
                }
                return Result(false, "LaunchFailed", "Server launch failed: " + ex.Message);
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
            var stopPhase = "process recheck";
            try
            {
                using var process = Process.GetProcessById(run.ProcessId!.Value);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != run.StartTimeUtcTicks ||
                    !Path.GetFullPath(process.MainModule!.FileName).Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                    return Result(false, "IdentityUnknown", "Process identity changed. No stop signal was sent.");
                var nativeHandle = run.Kind == "Valheim" ? process.Handle : IntPtr.Zero;
                stopPhase = "stop signal";
                if (run.Kind == "Valheim") WindowsConsoleProcess.RequestCtrlC(process);
                else
                {
                    using var pipe = new NamedPipeClientStream(".", run.StopPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await pipe.ConnectAsync(connectTimeout.Token);
                    using var writer = new StreamWriter(pipe) { AutoFlush = true };
                    await writer.WriteLineAsync("stop");
                }
                stopPhase = "exit wait";
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(run.Kind == "Valheim" ? 90 : 8));
                await process.WaitForExitAsync(exitTimeout.Token);
                stopPhase = "exit code";
                var exitCode = run.Kind == "Valheim" ? WindowsConsoleProcess.ExitCode(nativeHandle) : (uint)process.ExitCode;
                if (exitCode != 0)
                    return Result(false, "StopFailed", "Server exited with a nonzero status. Run remains recorded for review.");
                runs.Remove(run);
                data.SaveRuns(runs);
                return run.Kind == "Valheim"
                    ? Result(true, "ValheimStopped", "Valheim exited after Ctrl+C. Save integrity still needs a real join and restart check.")
                    : Result(true, "FixtureStopped", "Fixture exited cleanly. This is not a Valheim save check.");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                return Result(false, "StopUnconfirmed", "Stop was not confirmed during " + stopPhase + ": " + ex.Message);
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
                "Matched" when run.Kind == "Valheim" && ValheimLogReady(run.LogPath) =>
                    Result(true, "ValheimLogReady", "Server-connected log signal found. A real client join remains unverified."),
                "Matched" when run.Kind == "Valheim" =>
                    Result(false, "ValheimStarting", "Process identity matches; waiting for the server-connected log signal."),
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
                "Matched" when run.Kind == "Valheim" && ValheimLogReady(run.LogPath) =>
                    new RunView(profile.Id, "Ready", "Valheim server-connected log observed; client join and save still unverified", run.ProcessId),
                "Matched" when run.Kind == "Valheim" =>
                    new RunView(profile.Id, "Starting", "Valheim process matches; waiting for server-connected log", run.ProcessId),
                "Matched" => new RunView(profile.Id, "Process running", "Synthetic fixture only; no game readiness signal", run.ProcessId),
                "Missing" => new RunView(profile.Id, "Failed", "Recorded process exited; owner can clear the record", run.ProcessId),
                _ => new RunView(profile.Id, "Unknown", "Process identity cannot be proven; start and stop are blocked", run.ProcessId)
            };
        }).ToList();
        return new HostSnapshot(settings, views, "Process identity and Valheim log signal; join/save unverified", "Host",
            ClientMonitor.IsRunning(settings.OwnerClientExecutablePath), DateTimeOffset.UtcNow,
            settings.Profiles.Where(profile => profile.Kind == "Valheim")
                .ToDictionary(profile => profile.Id, profile => data.HasValheimPassword(profile.Id)));
    }

    private ActionResult Result(bool ok, string code, string message) => new(ok, code, message, Snapshot());

    private static string? Validate(HostSettings next)
    {
        if (next.MaxConcurrentServers < 1 || next.MaxConcurrentServers > 16) return "Maximum servers must be between 1 and 16.";
        if (next.IdleMinutes < 1 || next.IdleMinutes > 1440) return "Idle minutes must be between 1 and 1440.";
        if (next.AutoShutdownEnabled) return "Auto shutdown is unavailable until real player coverage is verified.";
        if (next.PermittedPlayersVerified) return "Permitted-player coverage requires real Valheim verification.";
        if (next.CompanionPort < 1024 || next.CompanionPort > 65535) return "Companion port must be between 1024 and 65535.";
        if (!System.Net.IPAddress.TryParse(next.CompanionBindAddress, out _)) return "Companion bind address must be an IP address.";
        if (!string.IsNullOrWhiteSpace(next.CompanionEndpoint) &&
            (!HostIdentity.TryEndpoint(next.CompanionEndpoint, out var endpoint) || endpoint.Port != next.CompanionPort))
            return "Companion endpoint must be an HTTPS IP address on the configured port.";
        if (next.CompanionListeningEnabled && string.IsNullOrWhiteSpace(next.CompanionEndpoint))
            return "Set the companion endpoint before enabling its listener.";
        if (next.RemoteControlsEnabled && !next.CompanionListeningEnabled)
            return "Enable the authenticated companion listener before remote controls.";
        if (!string.IsNullOrWhiteSpace(next.OwnerClientExecutablePath) && !Path.IsPathFullyQualified(next.OwnerClientExecutablePath))
            return "Owner game client path must be absolute.";
        if (next.Profiles is null || next.Profiles.Select(p => p.Id).Distinct().Count() != next.Profiles.Count)
            return "Each profile needs a unique ID.";
        foreach (var profile in next.Profiles)
        {
            if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.WorldId))
                return "Each profile needs a name and world ID.";
            if (profile.Kind is not ("Fixture" or "Valheim")) return "Choose Fixture or Valheim for the profile type.";
            if (!ValheimSetup.ValidWorldId(profile.WorldId))
                return "World ID must be a valid file name of at most 64 characters.";
            if (profile.Kind == "Valheim" && profile.WorldSource is not ("Existing" or "New"))
                return "Choose an existing imported world or explicitly create a new world.";
            if (profile.Kind == "Valheim" && (string.IsNullOrWhiteSpace(profile.ServerName) ||
                profile.ServerName.Length > 80 || profile.ServerName.Any(char.IsControl)))
                return "Valheim server name must be 1 to 80 characters without control characters.";
            if (profile.GamePort < 1024 || profile.GamePort > 65534) return "Game port must be between 1024 and 65534.";
            if (!Path.IsPathFullyQualified(profile.ExecutablePath) || !Path.IsPathFullyQualified(profile.WorldDirectory))
                return "Executable and save directory must be absolute paths.";
        }
        return null;
    }

    private static bool SameProfile(ServerProfile a, ServerProfile b) =>
        a.Kind == b.Kind && a.Name == b.Name && a.ServerName == b.ServerName &&
        a.Crossplay == b.Crossplay && a.PublicListing == b.PublicListing &&
        a.WorldId == b.WorldId && a.WorldSource == b.WorldSource && a.GamePort == b.GamePort &&
        Path.GetFullPath(a.WorldDirectory).Equals(Path.GetFullPath(b.WorldDirectory), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(a.ExecutablePath).Equals(Path.GetFullPath(b.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static bool WorldConflict(ManagedRun run, ServerProfile profile) =>
        run.WorldId.Equals(profile.WorldId, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(run.WorldDirectory).Equals(Path.GetFullPath(profile.WorldDirectory), StringComparison.OrdinalIgnoreCase);

    private static bool PortsOverlap(int a, int b) => a == b || a == b + 1 || a + 1 == b;

    private static bool ValheimLogReady(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains("Game server connected", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

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

using System.Diagnostics;
using System.Net;

namespace TogetherServer;

public sealed record RunView(Guid ProfileId, string State, string Detail, int? ProcessId,
    IReadOnlyList<GamePort>? DeclaredPorts = null, int? OnlinePlayers = null, int? MaxPlayers = null);
public sealed record HostSnapshot(HostSettings Settings, IReadOnlyList<RunView> Runs,
    string Evidence, string Mode, bool? OwnerGameRunning, DateTimeOffset OwnerCheckedUtc,
    IReadOnlyDictionary<Guid, bool> PasswordConfigured, string ManagedWorldsRoot);
public sealed record ActionResult(bool Ok, string Code, string Message, HostSnapshot Snapshot);

public sealed class HostManager
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LocalData data;
    private readonly GameServerRegistry games;
    private HostSettings settings;
    private readonly List<ManagedRun> runs;

    public HostManager(LocalData data) : this(data, new GameServerRegistry(data)) { }

    public HostManager(LocalData data, GameServerRegistry games)
    {
        this.data = data;
        this.games = games;
        settings = data.LoadSettings();
        runs = data.LoadRuns();
    }

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
            if (settings.PublicGameIpCheckedUtc is { } recorded &&
                (next.PublicGameIpCheckedUtc is null || next.PublicGameIpCheckedUtc < recorded))
            {
                next.PublicGameIp = settings.PublicGameIp;
                next.PublicGameIpCheckedUtc = recorded;
            }
            var error = Validate(next);
            if (error is not null) return Result(false, "InvalidSettings", error);
            var pinnedEndpoint = data.LoadIdentityEndpoint();
            if (pinnedEndpoint is not null &&
                !string.Equals(next.CompanionEndpoint, pinnedEndpoint, StringComparison.OrdinalIgnoreCase))
                return Result(false, "HostAddressPinned", "The Friend app address is pinned by the Host identity. Keep the address used for pairing.");
            var profileIds = next.Profiles.Select(profile => profile.Id).ToHashSet();
            var serverInvites = data.LoadServerInvites()
                .Where(invite => profileIds.Contains(invite.ProfileId)).ToList();
            var devices = data.LoadDevices().Where(device => !device.Revoked &&
                (device.ProfileId == Guid.Empty || profileIds.Contains(device.ProfileId))).ToList();
            if (next.CompanionListeningEnabled &&
                (!data.HasProtected("host-certificate.protected") ||
                 !(serverInvites.Count > 0 || devices.Any(device =>
                    (device.InviteHash is not null || device.CredentialHash is not null)))))
                return Result(false, "PairingRequired", "Create a pairing invite and Host TLS identity before enabling the listener.");
            if (next.RemoteControlsEnabled &&
                !(serverInvites.Any(invite => invite.CanStart || invite.CanStop) ||
                    devices.Any(device => (device.InviteHash is not null ||
                    device.CredentialHash is not null) && (device.CanStart || device.CanStop))))
                return Result(false, "FriendPermissionRequired", "Invite a Friend PC with Start or Stop permission first.");
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

    public async Task<HostSnapshot> RecordDetectedPublicIpAsync(string address)
    {
        if (!GameConnection.IsPublicIpv4(address)) throw new ArgumentException("The detected address is not public IPv4.");
        await gate.WaitAsync();
        try
        {
            var previousAddress = settings.PublicGameIp;
            var previousChecked = settings.PublicGameIpCheckedUtc;
            settings.PublicGameIp = IPAddress.Parse(address).ToString();
            settings.PublicGameIpCheckedUtc = DateTimeOffset.UtcNow;
            try { data.SaveSettings(settings); }
            catch
            {
                settings.PublicGameIp = previousAddress;
                settings.PublicGameIpCheckedUtc = previousChecked;
                throw;
            }
            return Snapshot();
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> SetValheimPasswordAsync(Guid profileId, string? password)
    {
        await gate.WaitAsync();
        try
        {
            if (settings.Profiles.SingleOrDefault(profile => profile.Id == profileId)?.Kind != GameKinds.Valheim)
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
            if (!games.TryGet(profile.Kind, out var driver))
                return Result(false, "UnsupportedGame", "This game type is not installed in TogetherServer.");
            if (runs.Any(r => r.ProfileId == profileId))
                return Result(false, "AlreadyManaged", "This profile already has a managed or unresolved run.");
            if (runs.Any(r => WorldConflict(r, profile)))
                return Result(false, "WorldConflict", "Another managed run owns this world or save directory.");
            if (runs.Count >= settings.MaxConcurrentServers)
                return Result(false, "MaxConcurrent", "The managed server limit has been reached.");
            if (!File.Exists(profile.ExecutablePath))
                return Result(false, "ExecutableMissing", "Selected server executable does not exist.");
            var validation = driver.ValidateForStart(profile);
            if (validation is not null) return Result(false, validation.Code, validation.Message);
            var declaredPorts = driver.Ports(profile).ToList();
            foreach (var existing in runs)
            {
                var ownedPorts = PortsForRun(existing);
                if (ownedPorts is null)
                    return Result(false, "PortOwnershipUnknown", "A managed run's game ports cannot be verified. Resolve that run before starting another server.");
                if (ownedPorts.Any(owned => declaredPorts.Any(requested => PortOverlap(owned, requested))))
                    return Result(false, "PortConflict", "Another managed run owns one of these game ports.");
            }
            if (!GameServerRegistry.PortsAvailable(declaredPorts))
                return Result(false, "PortInUse", "One or more configured game ports are already in use.");

            var run = new ManagedRun
            {
                ProfileId = profile.Id,
                OperationId = Guid.NewGuid(),
                Kind = profile.Kind,
                WorldId = profile.WorldId,
                WorldDirectory = Path.GetFullPath(profile.WorldDirectory),
                GamePort = profile.GamePort,
                DeclaredPorts = declaredPorts,
                ExecutablePath = Path.GetFullPath(profile.ExecutablePath),
                ServerArtifactPath = profile.Minecraft?.ServerJarPath ?? "",
                StopPipeName = "TogetherServer.Fixture." + Guid.NewGuid().ToString("N")
            };
            driver.PrepareStart(profile, run);
            runs.Add(run);
            data.SaveRuns(runs); // An interrupted launch stays Unknown, blocking a second writer.
            try
            {
                var launch = driver.Start(profile, run);
                run.ProcessId = launch.ProcessId;
                using (var started = Process.GetProcessById(run.ProcessId.Value))
                    run.StartTimeUtcTicks = started.StartTime.ToUniversalTime().Ticks;
                data.SaveRuns(runs);
                return Result(true, launch.Code, launch.Message);
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

    public async Task<ActionResult> StopAsync(Guid profileId, Func<ManagedRun, bool>? remoteStillSafe = null)
    {
        await gate.WaitAsync();
        try
        {
            var run = runs.SingleOrDefault(r => r.ProfileId == profileId);
            if (run is null) return Result(false, "NotManaged", "This profile has no managed process.");
            if (!games.TryGet(run.Kind, out var driver))
                return Result(false, "UnsupportedGame", "This managed run uses an unavailable game driver.");
            if (Identity(run) != "Matched")
                return Result(false, "IdentityUnknown", "Process identity is unverified. No stop signal was sent.");
            try
            {
                using var process = Process.GetProcessById(run.ProcessId!.Value);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != run.StartTimeUtcTicks ||
                    !Path.GetFullPath(process.MainModule!.FileName).Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                    return Result(false, "IdentityUnknown", "Process identity changed. No stop signal was sent.");
                if (remoteStillSafe is not null && !remoteStillSafe(run))
                    return Result(false, "PlayersOnlineOrUnknown",
                        "The server no longer reports zero online players. No stop signal was sent; the Host can still stop it locally.");
                var stopped = await driver.StopAsync(process, run);
                if (stopped.ExitCode != 0) return Result(false, stopped.Code, stopped.Message);
                runs.Remove(run);
                data.SaveRuns(runs);
                return Result(true, stopped.Code, stopped.Message);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                return Result(false, "StopUnconfirmed", "The game driver could not confirm a graceful stop: " + ex.Message);
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
            if (!games.TryGet(run.Kind, out var driver))
                return Result(false, "UnsupportedGame", "This managed run uses an unavailable game driver.");
            return Identity(run) switch
            {
                "Matched" => DriverHealth(driver.Health(run)),
                "Missing" => Result(false, "ProcessExited", "The recorded process is no longer running."),
                _ => Result(false, "IdentityUnknown", "The recorded process identity cannot be verified.")
            };
        }
        finally { gate.Release(); }
    }

    private ActionResult DriverHealth(GameHealthResult health) => Result(health.Ok, health.Code, health.Detail);

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
            if (!games.TryGet(run.Kind, out var driver))
                return new RunView(profile.Id, "Unknown", "The game driver for this run is unavailable", run.ProcessId, run.DeclaredPorts);
            return identity switch
            {
                "Matched" => DriverView(profile.Id, run, driver.Health(run)),
                "Missing" => new RunView(profile.Id, "Failed", "Recorded process exited; owner can clear the record", run.ProcessId, run.DeclaredPorts),
                _ => new RunView(profile.Id, "Unknown", "Process identity cannot be proven; start and stop are blocked", run.ProcessId, run.DeclaredPorts)
            };
        }).ToList();
        return new HostSnapshot(settings, views, "Recorded process identity and game-specific local readiness; Friend join and save unverified", "Host",
            ClientMonitor.IsRunning(settings.OwnerClientExecutablePath), DateTimeOffset.UtcNow,
            settings.Profiles.Where(profile => profile.Kind == "Valheim")
                .ToDictionary(profile => profile.Id, profile => data.HasValheimPassword(profile.Id)),
            data.ManagedWorldsRoot);
    }

    private static RunView DriverView(Guid profileId, ManagedRun run, GameHealthResult health) =>
        new(profileId, health.State, health.Detail, run.ProcessId, run.DeclaredPorts,
            health.OnlinePlayers, health.MaxPlayers);

    private ActionResult Result(bool ok, string code, string message) => new(ok, code, message, Snapshot());

    private string? Validate(HostSettings next)
    {
        if (next.MaxConcurrentServers < 1 || next.MaxConcurrentServers > 16) return "Maximum servers must be between 1 and 16.";
        if (next.IdleMinutes < 1 || next.IdleMinutes > 1440) return "Idle minutes must be between 1 and 1440.";
        if (next.AutoShutdownEnabled) return "Auto shutdown is unavailable until real player coverage is verified.";
        if (next.CompanionPort < 1024 || next.CompanionPort > 65535) return "Companion port must be between 1024 and 65535.";
        if (!string.IsNullOrWhiteSpace(next.PublicGameIp) && !GameConnection.IsPublicIpv4(next.PublicGameIp))
            return "The Valheim friend address must be public IPv4; 127.0.0.1, local, shared, and test addresses cannot be used.";
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
            if (profile.Id == Guid.Empty) return "Each server needs a valid ID.";
            if (string.IsNullOrWhiteSpace(profile.Name)) return "Enter a server name in Setup step 1.";
            if (string.IsNullOrWhiteSpace(profile.WorldId))
                return profile.Kind == "Valheim" && profile.WorldSource == "Existing"
                    ? "Choose and copy an existing world in Setup step 1."
                    : "Enter a world name in Setup step 1.";
            if (!games.TryGet(profile.Kind, out _)) return "Choose a supported game type for the server.";
            if (!ValheimSetup.ValidWorldId(profile.WorldId))
                return "World ID must be a valid file name of at most 64 characters.";
            if (profile.Kind == "Valheim" && profile.WorldSource is not ("Existing" or "New"))
                return "Choose an existing imported world or explicitly create a new world.";
            if (profile.Kind == "Valheim" && (string.IsNullOrWhiteSpace(profile.ServerName) ||
                profile.ServerName.Length > 80 || profile.ServerName.Any(char.IsControl)))
                return "Valheim server name must be 1 to 80 characters without control characters.";
            if (profile.GamePort < 1024 || profile.GamePort > (profile.Kind == GameKinds.Valheim ? 65534 : 65535))
                return "Game port is outside the valid range for this game.";
            if (string.IsNullOrWhiteSpace(profile.WorldDirectory))
                return profile.Kind == "Valheim" && profile.WorldSource == "Existing"
                    ? "Choose and copy an existing world in Setup step 1."
                    : "Choose an existing save directory in Setup step 1.";
            if (!Path.IsPathFullyQualified(profile.WorldDirectory))
                return "The save directory needs a full path, such as C:\\ValheimSaves.";
            if (string.IsNullOrWhiteSpace(profile.ExecutablePath))
                return "Select an installed server in Setup step 2.";
            if (!Path.IsPathFullyQualified(profile.ExecutablePath))
                return "The installed server path needs a full path to its .exe file.";
        }
        return null;
    }

    private static bool SameProfile(ServerProfile a, ServerProfile b) =>
        a.Kind == b.Kind && a.Name == b.Name && a.ServerName == b.ServerName &&
        a.Crossplay == b.Crossplay && a.PublicListing == b.PublicListing &&
        a.WorldId == b.WorldId && a.WorldSource == b.WorldSource && a.GamePort == b.GamePort &&
        (a.Minecraft?.ServerJarPath ?? "") == (b.Minecraft?.ServerJarPath ?? "") &&
        Path.GetFullPath(a.WorldDirectory).Equals(Path.GetFullPath(b.WorldDirectory), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(a.ExecutablePath).Equals(Path.GetFullPath(b.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static bool WorldConflict(ManagedRun run, ServerProfile profile) =>
        Path.GetFullPath(run.WorldDirectory).Equals(Path.GetFullPath(profile.WorldDirectory), StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<GamePort>? PortsForRun(ManagedRun run)
    {
        if (run.DeclaredPorts is { Count: > 0 }) return run.DeclaredPorts;
        // Runs saved before declared ports were recorded still have their full saved profile.
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == run.ProfileId && item.Kind == run.Kind);
        return profile is not null && games.TryGet(run.Kind, out var driver) ? driver.Ports(profile) : null;
    }

    private static bool PortOverlap(GamePort left, GamePort right) =>
        left.Port == right.Port && left.Protocol.Equals(right.Protocol, StringComparison.OrdinalIgnoreCase) &&
        (left.Family == "Any" || right.Family == "Any" || left.Family == right.Family);

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

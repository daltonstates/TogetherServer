namespace TogetherServer;

public static class OwnerDiagnosticTones
{
    public const string Neutral = "Neutral";
    public const string Attention = "Attention";
    public const string Error = "Error";
}

public sealed record OwnerDiagnosticCheck(string Id, string Label, string State, string Detail,
    string NextAction, string Location, string Tone = OwnerDiagnosticTones.Neutral,
    DateTimeOffset? ObservedUtc = null);

public sealed record OwnerServerDiagnostics(Guid ProfileId, string Label, string Kind,
    IReadOnlyList<OwnerDiagnosticCheck> Checks);

public sealed record OwnerDiagnosticsView(DateTimeOffset GeneratedUtc, string EvidenceBoundary,
    IReadOnlyList<OwnerServerDiagnostics> Servers, IReadOnlyList<OwnerDiagnosticCheck> SharedChecks,
    bool Truncated = false);

public static class OwnerDiagnostics
{
    internal const int MaximumServers = 64;
    private static readonly TimeSpan CurrentHeartbeat = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CurrentRouteDiagnostic = TimeSpan.FromMinutes(5);

    public static OwnerDiagnosticsView Build(HostSnapshot snapshot, GameServerRegistry games,
        PortDiagnosticsView ports, IReadOnlyList<DeviceView> devices, DataRecoveryView recovery,
        UpdateView update, ExternalPortProbeResult? routeDiagnostic, bool isStaging,
        DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var profiles = snapshot.Settings.Profiles.Take(MaximumServers).ToList();
        var servers = profiles.Select(profile => BuildServer(profile, snapshot, games, ports, devices, now)).ToList();
        var shared = new List<OwnerDiagnosticCheck>
        {
            ListenerCheck(ports.Control),
            RouteCheck(ports.Control, routeDiagnostic, now),
            RecoveryCheck(recovery),
            UpdateCheck(update, isStaging)
        };
        if (snapshot.Settings.Profiles.Count > MaximumServers)
            shared.Add(new("server-list-bound", "Saved server list", "Partial",
                $"This report shows the first {MaximumServers} saved servers. The remaining servers are not included.",
                "Review or remove unused saved servers before exporting another report.", "Host > Servers",
                OwnerDiagnosticTones.Attention));

        return new(now,
            "These checks use saved Host data and do not change the server. Local listeners and outside TCP checks do not prove that a Friend connected, joined the game, or that a world saved correctly.",
            servers, shared, snapshot.Settings.Profiles.Count > MaximumServers);
    }

    private static OwnerServerDiagnostics BuildServer(ServerProfile profile, HostSnapshot snapshot,
        GameServerRegistry games, PortDiagnosticsView ports, IReadOnlyList<DeviceView> devices,
        DateTimeOffset now)
    {
        var run = snapshot.Runs.SingleOrDefault(item => item.ProfileId == profile.Id) ??
            new RunView(profile.Id, "Unknown", "TogetherServer could not read the current server session.", null,
                PlayerCountTrusted: false);
        var gamePorts = ports.Games.SingleOrDefault(item => item.ProfileId == profile.Id);
        return new(profile.Id, Bounded(profile.Name, 80, "Saved server"), profile.Kind,
        [
            ConfigurationCheck(profile, snapshot, games),
            ProcessCheck(run),
            ObservationCheck(run),
            GamePortsCheck(gamePorts),
            FriendEvidenceCheck(profile.Id, devices, now)
        ]);
    }

    private static OwnerDiagnosticCheck ConfigurationCheck(ServerProfile profile, HostSnapshot snapshot,
        GameServerRegistry games)
    {
        if (!games.TryGet(profile.Kind, out var driver))
            return new("configuration", "Saved setup", "Driver unavailable",
                "The saved game kind has no registered reviewed driver, so its setup cannot be evaluated.",
                "Choose a supported game and review the saved setup.", "Host > Setup",
                OwnerDiagnosticTones.Error);

        try
        {
            var validation = driver.ValidateForStart(profile);
            if (validation is not null)
                return new("configuration", "Saved setup", "Needs attention",
                    Bounded(validation.Message, 240, "Saved setup needs review."),
                    $"Open Setup and resolve {Bounded(validation.Code, 64, "the saved setup issue")} before Start.",
                    "Host > Setup", OwnerDiagnosticTones.Attention);
            var passwordState = profile.Kind == GameKinds.Valheim &&
                !snapshot.PasswordConfigured.GetValueOrDefault(profile.Id)
                ? " The protected game password is not currently recorded."
                : "";
            return new("configuration", "Saved setup", "Saved",
                $"The existing {driver.DisplayName} start validation accepts the saved configuration.{passwordState}",
                "Review Setup before changing the game, world, executable, or ports.", "Host > Setup");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Security.SecurityException or System.Security.Cryptography.CryptographicException or
                                   System.Text.Json.JsonException or ArgumentException)
        {
            return new("configuration", "Saved setup", "Check unavailable",
                "TogetherServer could not safely evaluate the saved configuration. No server action was started.",
                "Open Setup and review the saved files and protected configuration.", "Host > Setup",
                OwnerDiagnosticTones.Error);
        }
    }

    private static OwnerDiagnosticCheck ProcessCheck(RunView run)
    {
        var pid = run.ProcessId is { } processId ? $" Recorded PID {processId}." : "";
        var tone = run.State switch
        {
            "Unknown" or "Failed" => OwnerDiagnosticTones.Error,
            "Starting" or "Stopping" => OwnerDiagnosticTones.Attention,
            _ => OwnerDiagnosticTones.Neutral
        };
        var next = run.State switch
        {
            "Offline" => "Use Start server from Overview when the saved setup is ready.",
            "Unknown" => "Review the saved process from Overview; Start and Stop stay blocked until TogetherServer knows which process it is.",
            "Failed" => "Archive the failed session only after TogetherServer confirms that the process has ended.",
            "Starting" or "Stopping" => "Wait for the current server action to finish, then refresh diagnostics.",
            _ => "Use the Start, Stop, and Restart buttons in Overview."
        };
        return new("managed-process", "Managed process", run.State,
            Bounded(run.Detail + pid, 320, "Managed process state is unavailable."), next,
            "Host > Overview", tone);
    }

    private static OwnerDiagnosticCheck ObservationCheck(RunView run)
    {
        if (run.State == "Offline")
            return new("driver-observation", "Driver readiness and players", "Not active",
                "The server is offline, so there is no live readiness or player observation. Offline diagnostics do not start it.",
                "Start only from Overview when you intend to run the saved server.", "Host > Overview");

        if (run.State == "Ready" && run.PlayerCountTrusted && run.OnlinePlayers is { } online)
        {
            var capacity = run.MaxPlayers is { } maximum ? $" of {maximum}" : "";
            return new("driver-observation", "Driver readiness and players", $"Ready · {online}{capacity} online",
                "This is the latest player count reported to the Host. It does not identify players or prove that someone joined from outside the network.",
                "Use the player-count refresh beside the server card if a new observation is needed.", "Host > Players");
        }

        if (run.State == "Ready")
            return new("driver-observation", "Driver readiness and players", "Player count unavailable",
                "The server is Ready, but a fresh player count is not available. Remote and automatic Stop stay blocked for safety.",
                "Refresh the player count and review the game's local status.", "Host > Players",
                OwnerDiagnosticTones.Attention);

        return new("driver-observation", "Driver readiness and players", run.State,
            Bounded(run.Detail, 280, "Driver observation is unavailable."),
            "Refresh diagnostics after the existing Host supervision cycle completes.", "Host > Overview",
            run.State is "Unknown" or "Failed" ? OwnerDiagnosticTones.Error : OwnerDiagnosticTones.Attention);
    }

    private static OwnerDiagnosticCheck GamePortsCheck(GamePortCheck? game)
    {
        if (game is null)
            return new("local-game-ports", "Declared game ports", "Unavailable",
                "No existing declared-port diagnostic was returned for this saved server.",
                "Open Connection help and refresh the existing port checks.", "Settings > Connection help",
                OwnerDiagnosticTones.Error);
        var declared = game.Ports.Count == 0 ? "No game ports are declared." :
            $"Declared {game.Protocol} {string.Join(", ", game.Ports)}.";
        var tone = game.State switch
        {
            "Closed on PC" or "Unknown" => OwnerDiagnosticTones.Error,
            "Opening" or "Loopback only" => OwnerDiagnosticTones.Attention,
            _ => OwnerDiagnosticTones.Neutral
        };
        return new("local-game-ports", "Declared game ports", game.State,
            Bounded($"{declared} {game.Detail} Seeing a port open on this PC does not prove that a Friend can reach it or join the game.",
                420, "Local port details are unavailable."),
            game.State == "Waiting" ? "Start only from Overview when you intend to inspect live local sockets." :
                "Use Connection help for routing guidance, then verify the game separately from a real Friend PC.",
            "Settings > Connection help", tone);
    }

    private static OwnerDiagnosticCheck FriendEvidenceCheck(Guid profileId,
        IReadOnlyList<DeviceView> devices, DateTimeOffset now)
    {
        var latest = devices.Where(device => !device.Revoked && device.Paired && !device.ApprovalPending &&
                !device.AccessExpired && device.CredentialExpiresUtc > now &&
                device.AssignedProfileIds.Contains(profileId) && device.LastHeartbeatUtc is not null)
            .OrderByDescending(device => device.LastHeartbeatUtc).FirstOrDefault();
        if (latest?.LastHeartbeatUtc is not { } received)
            return new("friend-evidence", "Recent Friend contact", "None recorded",
                "No connected Friend assigned to this server has contacted the Host recently.",
                "Review server access or choose Invite friends to copy the server code.",
                "Settings > Friend access");
        var age = received <= now ? now - received : TimeSpan.MaxValue;
        var current = age <= CurrentHeartbeat;
        return new("friend-evidence", "Recent Friend contact", current ? "Recent" : "Old",
            $"A connected Friend PC contacted the Host at {received:O}. Its network location is unknown, and this does not prove a game join.",
            current ? "Test the game join separately if connection acceptance is required." :
                "Ask the assigned Friend to reopen TogetherServer and check the saved Host connection.",
            "Settings > Friend access", current ? OwnerDiagnosticTones.Neutral : OwnerDiagnosticTones.Attention,
            received);
    }

    private static OwnerDiagnosticCheck ListenerCheck(ControlPortCheck control)
    {
        var tone = control.State switch
        {
            "Not listening" or "Closed on PC" or "Error" => OwnerDiagnosticTones.Error,
            "Unknown" => OwnerDiagnosticTones.Attention,
            _ => OwnerDiagnosticTones.Neutral
        };
        var detail = $"{control.Detail} Listening on: {control.BindScope}. This confirms only what is open on this PC, not whether a Friend can reach it.";
        return new("companion-listener", "Friend app connection", control.State,
            Bounded(detail, 420, "Friend connection state is unavailable."),
            control.State is "Off" or CompanionListenerStates.Idle
                ? "Choose Invite friends to get the server code."
                : "Review Friend access and Connection help without changing firewall or router settings automatically.",
            "Settings > Friend access", tone);
    }

    private static OwnerDiagnosticCheck RouteCheck(ControlPortCheck control,
        ExternalPortProbeResult? result, DateTimeOffset now)
    {
        if (result is null)
            return new("route-diagnostic", "Outside connection test", "Not checked",
                "No outside TCP test has been run since TogetherServer opened.",
                "Run the optional test only after the local Friend listener is open, or ask a Friend on another network to connect.",
                "Settings > Connection help");
        var age = now - result.CheckedUtc;
        var current = age >= TimeSpan.Zero && age < CurrentRouteDiagnostic && result.Port == control.Port &&
            (result.Endpoint is null ||
             string.Equals(result.Endpoint, control.Endpoint, StringComparison.OrdinalIgnoreCase));
        if (!current)
            return new("route-diagnostic", "Outside connection test", "Previous result expired",
                "The latest outside TCP result is older than five minutes or no longer matches the current Friend connection and server code address.",
                "Refresh the optional route test, then connect from a real Friend PC.",
                "Settings > Connection help", OwnerDiagnosticTones.Attention, result.CheckedUtc);
        var tone = result.State switch
        {
            "Not reachable" => OwnerDiagnosticTones.Error,
            "Inconclusive" or "Unavailable" => OwnerDiagnosticTones.Attention,
            _ => OwnerDiagnosticTones.Neutral
        };
        return new("route-diagnostic", "Outside connection test", result.State,
            Bounded(result.Detail + " This checks only the TCP route; it does not prove that a Friend connected or joined the game.",
                420, "The outside TCP result is unavailable."),
            result.State == "Reachable"
                ? "Verify Connect from a real Friend PC, then test the game join separately."
                : "Review the shown route layers manually; TogetherServer will not change firewall, router, or DNS settings.",
            "Settings > Connection help", tone, result.CheckedUtc);
    }

    private static OwnerDiagnosticCheck RecoveryCheck(DataRecoveryView recovery)
    {
        if (recovery.LifecycleBlocked)
            return new("data-recovery", "Local data recovery", "Server controls blocked",
                $"{recovery.Notices.Count} saved data warning(s) require review. Automatic server actions remain paused.",
                "Review the saved recovery files and resolve every recorded server process before confirming recovery.",
                "Attention Center", OwnerDiagnosticTones.Error);
        if (recovery.Notices.Count > 0)
            return new("data-recovery", "Local data recovery", "Review needed",
                $"{recovery.Notices.Count} saved data warning(s) still need review.",
                "Review and clear the saved warning from the Attention Center.",
                "Attention Center", OwnerDiagnosticTones.Attention);
        return new("data-recovery", "Local data recovery", "No active notice",
            "TogetherServer has no saved data warning right now.",
            "Use the Attention Center if a future recovery notice appears.", "Attention Center");
    }

    private static OwnerDiagnosticCheck UpdateCheck(UpdateView update, bool isStaging)
    {
        var tone = update.State switch
        {
            "Available" or "Unavailable" => OwnerDiagnosticTones.Attention,
            _ => OwnerDiagnosticTones.Neutral
        };
        var next = isStaging
            ? "Replace or rebuild this isolated staging app explicitly; stable automatic updates stay disabled here."
            : update.State == "Available"
                ? "Review the available version in App settings; installation remains owner-initiated."
                : "Use Check for updates from App settings when a fresh result is needed.";
        return new("update-state", "Application update", update.State,
            Bounded(update.Message, 300, "Update state is unavailable."), next,
            "Settings > App", tone);
    }

    private static string Bounded(string? value, int maximum, string fallback)
    {
        var clean = ServerLogSanitizer.Clean(value ?? "");
        if (clean.Length == 0) return fallback;
        return clean.Length <= maximum ? clean : clean[..maximum] + "…";
    }
}

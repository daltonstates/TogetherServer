using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

public sealed record RunView(Guid ProfileId, string State, string Detail, int? ProcessId,
    IReadOnlyList<GamePort>? DeclaredPorts = null, int? OnlinePlayers = null, int? MaxPlayers = null,
    DateTimeOffset? AutoShutdownAtUtc = null, string? AutoShutdownReason = null,
    bool HostAddedTime = false, IReadOnlyList<string>? PlayerNames = null,
    bool PlayerCountTrusted = true, int FriendAddedMinutes = 0);
public sealed record HostSnapshot(HostSettings Settings, IReadOnlyList<RunView> Runs,
    string Evidence, string Mode, IReadOnlyDictionary<Guid, bool> PasswordConfigured, string ManagedWorldsRoot,
    IReadOnlyDictionary<Guid, CustomCertificationState>? CustomCertifications = null,
    IReadOnlyDictionary<Guid, CrashRecoveryState>? CrashRecovery = null,
    IReadOnlyDictionary<Guid, WorldBackupStatus>? Backups = null,
    IReadOnlyList<ActivityEvent>? Activity = null,
    DataRecoveryView? Recovery = null);
public sealed record PortConflictView(Guid ProfileId, string ProfileName, IReadOnlyList<GamePort> SharedPorts,
    bool CanReplace, string? BlockReason = null);
public sealed record ActionResult(bool Ok, string Code, string Message, HostSnapshot Snapshot,
    IReadOnlyList<PortConflictView>? PortConflicts = null);
public sealed record CountdownExtensionRequest(long Minutes);
public sealed record HostControlPolicyChange(bool? CompanionListeningEnabled = null,
    bool? RemoteControlsEnabled = null, bool? AutoShutdownEnabled = null);
public sealed record ServerObservation(Guid ProfileId, Guid OperationId, string State, string Detail,
    string Source, DateTimeOffset ObservedUtc, bool Ok, int? OnlinePlayers = null,
    int? MaxPlayers = null, IReadOnlyList<string>? PlayerNames = null, bool PlayerCountTrusted = false);

public sealed class HostManager
{
    private static readonly TimeSpan CrashRecoveryReadinessTimeout = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LocalData data;
    private readonly GameServerRegistry games;
    private readonly TimeProvider clock;
    private HostSettings settings;
    private readonly List<ManagedRun> runs;
    private readonly Dictionary<Guid, DateTimeOffset> shutdownDeadlines = [];
    private readonly HashSet<Guid> hostAddedTime = [];
    private readonly Dictionary<Guid, int> friendAddedMinutes = [];
    private readonly Dictionary<Guid, ServerObservation> observations = [];
    private readonly SemaphoreSlim observationRefresh = new(1, 1);
    private readonly Dictionary<Guid, CustomCertificationSession> customCertificationSessions = [];
    private readonly List<CrashRecoveryState> crashRecovery;
    private readonly WorldBackupService backups;

    public HostManager(LocalData data) : this(data, new GameServerRegistry(data), TimeProvider.System) { }

    public HostManager(LocalData data, GameServerRegistry games, TimeProvider? clock = null)
    {
        this.data = data;
        this.games = games;
        this.clock = clock ?? TimeProvider.System;
        settings = data.LoadSettings();
        runs = data.LoadRuns();
        crashRecovery = data.LoadCrashRecoveryStates();
        var recoveryNormalized = false;
        foreach (var recovery in crashRecovery.Where(item => !data.Recovery.LifecycleBlocked &&
                     item.State == CrashRecoveryStates.Starting && item.ReadinessDeadlineUtc is null))
        {
            recovery.ReadinessDeadlineUtc = this.clock.GetUtcNow().Add(CrashRecoveryReadinessTimeout);
            recoveryNormalized = true;
        }
        if (recoveryNormalized) data.SaveCrashRecoveryStates(crashRecovery);
        backups = new WorldBackupService(data, this.clock);
    }

    public bool CompanionListeningEnabled => Volatile.Read(ref settings).CompanionListeningEnabled;
    public bool RemoteControlsEnabled => Volatile.Read(ref settings).RemoteControlsEnabled;
    public bool LifecycleBlocked => data.Recovery.LifecycleBlocked;

    public string? RemoteMaintenanceBlocker(Guid profileId)
    {
        var profile = Volatile.Read(ref settings).Profiles.SingleOrDefault(item => item.Id == profileId);
        if (profile?.Maintenance?.Enabled != true) return null;
        return string.IsNullOrWhiteSpace(profile.Maintenance.Message)
            ? "The Host has placed this server in maintenance mode. Remote actions are paused."
            : "Maintenance: " + profile.Maintenance.Message;
    }

    public async Task<HostSnapshot> SnapshotAsync()
    {
        await gate.WaitAsync();
        try { return Snapshot(); }
        finally { gate.Release(); }
    }

    public async Task RefreshObservationsAsync()
    {
        if (!await observationRefresh.WaitAsync(0)) return;
        try
        {
            List<(ManagedRun Run, IGameServerDriver Driver)> targets;
            await gate.WaitAsync();
            try
            {
                if (data.Recovery.LifecycleBlocked) return;
                targets = [];
                foreach (var run in runs.ToList())
                {
                    var identity = Identity(run);
                    if (identity == "Missing")
                    {
                        ArchiveDefinitivelyExitedRun(run, "ProcessExited");
                        continue;
                    }
                    if (identity == "Matched" && games.TryGet(run.Kind, out var driver))
                        targets.Add((run, driver));
                }
            }
            finally { gate.Release(); }

            var checkedRuns = await Task.WhenAll(targets.Select(async target =>
            {
                GameHealthResult health;
                try { health = await Task.Run(() => target.Driver.Health(target.Run)); }
                catch (Exception ex)
                {
                    health = new(false, "HealthProbeFailed", "Unknown",
                        "The server status probe failed: " + ex.GetType().Name + ".", PlayerCountTrusted: false);
                }
                return (target.Run.ProfileId, target.Run.OperationId, Health: health);
            }));

            await gate.WaitAsync();
            try
            {
                var runsChanged = false;
                var recoveryChanged = false;
                var currentIds = runs.Select(run => run.ProfileId).ToHashSet();
                foreach (var removed in observations.Keys.Where(id => !currentIds.Contains(id)).ToList())
                    observations.Remove(removed);
                foreach (var result in checkedRuns)
                {
                    var run = runs.SingleOrDefault(run => run.ProfileId == result.ProfileId && run.OperationId == result.OperationId);
                    if (run is null) continue;
                    var previousState = observations.TryGetValue(result.ProfileId, out var previousObservation)
                        ? previousObservation.State : null;
                    observations[result.ProfileId] = ToObservation(result.ProfileId, result.OperationId,
                        result.Health, clock.GetUtcNow());
                    if (previousState is not null && !string.Equals(previousState, result.Health.State, StringComparison.Ordinal))
                    {
                        var profileName = settings.Profiles.SingleOrDefault(item => item.Id == result.ProfileId)?.Name ?? "Server";
                        Activity("Lifecycle", "StateChanged", $"{profileName} changed from {previousState} to {result.Health.State}.",
                            result.Health.State is "Failed" or "Unknown" ? ActivitySeverity.Warning : ActivitySeverity.Info,
                            result.ProfileId, visibility: ActivityVisibility.AssignedFriends);
                    }
                    if (result.Health.State == "Ready" && !run.WasReady)
                    {
                        run.WasReady = true;
                        runsChanged = true;
                    }
                    var recovery = crashRecovery.SingleOrDefault(item => item.ProfileId == result.ProfileId);
                    if (result.Health.State == "Ready" && recovery?.State == CrashRecoveryStates.Starting)
                    {
                        recovery.State = CrashRecoveryStates.Recovered;
                        recovery.NextAttemptUtc = null;
                        recovery.ReadinessDeadlineUtc = null;
                        recovery.RecoveredUtc = clock.GetUtcNow();
                        recovery.LastFailure = null;
                        recoveryChanged = true;
                        data.TryAudit($"crash-recovery-ready {run.ProfileId} cycle={recovery.CycleId} attempts={recovery.Attempts} {clock.GetUtcNow():O}");
                        Activity("Recovery", "CrashRecovered", "Crash recovery reached Ready.",
                            ActivitySeverity.Important, run.ProfileId,
                            visibility: ActivityVisibility.AssignedFriends);
                    }
                }
                if (runsChanged) data.SaveRuns(runs);
                if (recoveryChanged) data.SaveCrashRecoveryStates(crashRecovery);
            }
            finally { gate.Release(); }
        }
        finally { observationRefresh.Release(); }
    }

    public async Task<ActionResult> UpdateSettingsAsync(HostSettings next)
    {
        await gate.WaitAsync();
        try { return UpdateSettingsLocked(next); }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> UpdateControlPolicyAsync(HostControlPolicyChange change)
    {
        await gate.WaitAsync();
        try
        {
            if (change.CompanionListeningEnabled is null && change.RemoteControlsEnabled is null &&
                change.AutoShutdownEnabled is null)
                return Result(false, "InvalidPolicyChange", "Choose a Host control to change.");

            var next = CopySettings(settings);
            if (change.CompanionListeningEnabled is { } listening)
            {
                next.CompanionListeningEnabled = listening;
                if (!listening) next.RemoteControlsEnabled = false;
            }
            if (change.RemoteControlsEnabled is { } remoteControls)
                next.RemoteControlsEnabled = remoteControls;
            if (change.AutoShutdownEnabled is { } automaticShutdown)
                next.AutoShutdownEnabled = automaticShutdown;
            return UpdateSettingsLocked(next);
        }
        finally { gate.Release(); }
    }

    private ActionResult UpdateSettingsLocked(HostSettings next)
    {
        var previous = settings;
        next.ConnectionRoute = ConnectionRoutes.Normalize(next.ConnectionRoute);
        if (next.Profiles is null)
            return Result(false, "InvalidSettings", "At least one valid profile collection is required.");
        foreach (var profile in next.Profiles)
        {
            profile.CrashRecovery ??= new CrashRecoveryOptions();
            profile.Backups ??= new BackupOptions();
            profile.Maintenance ??= new MaintenanceOptions();
        }
        if (settings.PublicGameIpCheckedUtc is { } recorded &&
            (next.PublicGameIpCheckedUtc is null || next.PublicGameIpCheckedUtc < recorded))
        {
            next.PublicGameIp = settings.PublicGameIp;
            next.PublicGameIpCheckedUtc = recorded;
        }
        var error = Validate(next);
        if (error is not null) return Result(false, "InvalidSettings", error);
        var profileIds = next.Profiles.Select(profile => profile.Id).ToHashSet();
        var pairingState = data.LoadPairingState();
        var serverInvites = pairingState.ServerInvites
            .Where(invite => profileIds.Contains(invite.ProfileId)).ToList();
        var devices = pairingState.Devices.Where(device =>
        {
            var assigned = device.AssignedProfileIds ??
                (device.ProfileId == Guid.Empty ? [] : [device.ProfileId]);
            return !device.Revoked && assigned.Any(profileIds.Contains);
        }).ToList();
        if (next.CompanionListeningEnabled &&
            (!data.HasProtected("host-certificate.protected") ||
             !(serverInvites.Count > 0 || devices.Any(device =>
                (device.InviteHash is not null || device.CredentialHash is not null)))))
            return Result(false, "PairingRequired", "Create a pairing invite and Host TLS identity before enabling the listener.");
        if (next.RemoteControlsEnabled &&
            !(serverInvites.Any(invite => invite.CanStart || invite.CanStop) ||
                devices.Any(device => (device.InviteHash is not null ||
                device.CredentialHash is not null) &&
                (device.AssignedProfileIds ??
                    (device.ProfileId == Guid.Empty ? [] : [device.ProfileId])).Any(profileId =>
                    device.CanStartProfile(profileId) || device.CanStopProfile(profileId)))))
            return Result(false, "FriendPermissionRequired", "Invite a Friend PC with Start or Stop permission first.");
        foreach (var run in runs)
        {
            var oldProfile = previous.Profiles.SingleOrDefault(p => p.Id == run.ProfileId);
            var newProfile = next.Profiles.SingleOrDefault(p => p.Id == run.ProfileId);
            if (oldProfile is null || newProfile is null || !SameProfile(oldProfile, newProfile))
                return Result(false, "ProfileInUse", "Stop or resolve a managed run before changing its profile.");
        }
        var retiredCustomProfileIds = previous.Profiles
            .Where(profile => profile.Kind == GameKinds.Custom &&
                !next.Profiles.Any(candidate => candidate.Id == profile.Id && candidate.Kind == GameKinds.Custom))
            .Select(profile => profile.Id)
            .ToList();
        var invalidatedCustomProfileIds = previous.Profiles
            .Where(profile => profile.Kind == GameKinds.Custom)
            .Where(profile =>
            {
                var candidate = next.Profiles.SingleOrDefault(item => item.Id == profile.Id &&
                    item.Kind == GameKinds.Custom);
                return candidate is null || !SameCustomCertificationInputs(profile, candidate);
            })
            .Select(profile => profile.Id)
            .ToHashSet();
        data.SaveSettings(next);
        // Persisted safety policy becomes authoritative before any cleanup or
        // telemetry. An unavailable audit/activity file must never leave the
        // running Host with an older, more permissive policy.
        Volatile.Write(ref settings, next);
        var postCommitCleanupFailed = false;
        foreach (var profileId in retiredCustomProfileIds)
        {
            try
            {
                data.DeleteCustomScripts(profileId);
                data.DeleteCustomCertification(profileId);
                data.TryAudit($"custom-scripts-deleted {profileId} {clock.GetUtcNow():O}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                postCommitCleanupFailed = true;
                data.TryAudit($"custom-scripts-delete-failed {profileId} {ex.GetType().Name} {clock.GetUtcNow():O}");
            }
        }
        foreach (var profileId in invalidatedCustomProfileIds)
        {
            customCertificationSessions.Remove(profileId);
            shutdownDeadlines.Remove(profileId);
            hostAddedTime.Remove(profileId);
            friendAddedMinutes.Remove(profileId);
            try
            {
                data.DeleteCustomCertification(profileId);
                data.TryAudit($"custom-certification-invalidated {profileId} profile-changed {clock.GetUtcNow():O}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                postCommitCleanupFailed = true;
                data.TryAudit($"custom-certification-delete-failed {profileId} {ex.GetType().Name} {clock.GetUtcNow():O}");
            }
        }
        if (previous.RemoteControlsEnabled != next.RemoteControlsEnabled)
        {
            data.TryAudit($"remote-controls {(next.RemoteControlsEnabled ? "enabled" : "disabled")} {DateTimeOffset.UtcNow:O}");
            Activity("Access", next.RemoteControlsEnabled ? "RemoteControlsEnabled" : "RemoteControlsDisabled",
                next.RemoteControlsEnabled ? "Remote controls were enabled." : "Remote controls were paused.",
                ActivitySeverity.Important);
        }
        if (previous.AutoShutdownEnabled != next.AutoShutdownEnabled || previous.IdleMinutes != next.IdleMinutes)
        {
            data.TryAudit($"auto-shutdown {(next.AutoShutdownEnabled ? "enabled" : "disabled")} idle-minutes={next.IdleMinutes} {clock.GetUtcNow():O}");
            Activity("Countdown", "PolicyChanged",
                next.AutoShutdownEnabled
                    ? $"Automatic shutdown was enabled with a {next.IdleMinutes}-minute empty-server countdown."
                    : "Automatic shutdown was disabled.", ActivitySeverity.Important);
        }
        var previousRoute = ConnectionRoutes.Normalize(previous.ConnectionRoute);
        var nextRoute = ConnectionRoutes.Normalize(next.ConnectionRoute);
        if (previousRoute.Mode != nextRoute.Mode ||
            !string.Equals(previousRoute.Address, nextRoute.Address, StringComparison.OrdinalIgnoreCase))
            Activity("Network", "RouteChanged", $"Friend route changed to {ConnectionRoutes.DisplayName(nextRoute.Mode)}.",
                ActivitySeverity.Important);
        foreach (var profile in next.Profiles)
        {
            var previousProfile = previous.Profiles.SingleOrDefault(item => item.Id == profile.Id);
            if (!profile.Maintenance.Enabled && previousProfile?.Maintenance?.Enabled != true) continue;
            if (previousProfile?.Maintenance?.Enabled == profile.Maintenance.Enabled &&
                string.Equals(previousProfile?.Maintenance?.Message ?? "", profile.Maintenance.Message, StringComparison.Ordinal)) continue;
            Activity("Maintenance", profile.Maintenance.Enabled ? "Enabled" : "Disabled",
                profile.Maintenance.Enabled
                    ? string.IsNullOrWhiteSpace(profile.Maintenance.Message) ? "The Host enabled maintenance mode."
                        : "Maintenance: " + profile.Maintenance.Message
                    : "The Host ended maintenance mode.",
                ActivitySeverity.Important, profile.Id, visibility: ActivityVisibility.AssignedFriends);
        }
        if (previous.AutoShutdownEnabled != next.AutoShutdownEnabled || previous.IdleMinutes != next.IdleMinutes)
        {
            shutdownDeadlines.Clear();
            hostAddedTime.Clear();
            friendAddedMinutes.Clear();
        }
        var allowedRecoveryProfiles = settings.Profiles
            .Where(profile => profile.CrashRecovery.Enabled && games.TryGet(profile.Kind, out var driver) &&
                driver.SupportsCrashRecovery)
            .Select(profile => profile.Id).ToHashSet();
        if (crashRecovery.RemoveAll(item => !allowedRecoveryProfiles.Contains(item.ProfileId)) > 0)
        {
            try { data.SaveCrashRecoveryStates(crashRecovery); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or ArgumentException or JsonException)
            {
                postCommitCleanupFailed = true;
                data.TryAudit($"crash-recovery-cleanup-save-failed {ex.GetType().Name} {clock.GetUtcNow():O}");
            }
        }
        return Result(true, "SettingsSaved", postCommitCleanupFailed
            ? "Host settings saved, but one or more retired local control records could not be removed."
            : "Host settings saved.");
    }

    private static HostSettings CopySettings(HostSettings source) => new()
    {
        MaxConcurrentServers = source.MaxConcurrentServers,
        IdleMinutes = source.IdleMinutes,
        FriendTimerExtensionMinutes = source.FriendTimerExtensionMinutes,
        FriendTimerExtensionMaximumMinutes = source.FriendTimerExtensionMaximumMinutes,
        AutoShutdownEnabled = source.AutoShutdownEnabled,
        RemoteControlsEnabled = source.RemoteControlsEnabled,
        CompanionListeningEnabled = source.CompanionListeningEnabled,
        CompanionBindAddress = source.CompanionBindAddress,
        CompanionEndpoint = source.CompanionEndpoint,
        CompanionPort = source.CompanionPort,
        ConnectionRoute = ConnectionRoutes.Normalize(source.ConnectionRoute),
        PublicGameIp = source.PublicGameIp,
        PublicGameIpCheckedUtc = source.PublicGameIpCheckedUtc,
        Profiles = source.Profiles
    };

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

    public async Task<ActionResult> SetCustomScriptsAsync(Guid profileId, CustomScriptBundle scripts)
    {
        await gate.WaitAsync();
        try
        {
            if (settings.Profiles.SingleOrDefault(profile => profile.Id == profileId)?.Kind != GameKinds.Custom)
                return Result(false, "InvalidProfile", "Choose a saved custom game profile first.");
            if (runs.Any(run => run.ProfileId == profileId))
                return Result(false, "ProfileInUse", "Stop or resolve this custom game before changing its scripts.");
            var error = CustomGameScripts.Validate(scripts);
            if (error is not null) return Result(false, "CustomScriptsInvalid", error);
            data.SaveCustomScripts(profileId, scripts);
            data.DeleteCustomCertification(profileId);
            customCertificationSessions.Remove(profileId);
            shutdownDeadlines.Remove(profileId);
            hostAddedTime.Remove(profileId);
            friendAddedMinutes.Remove(profileId);
            data.TryAudit($"custom-scripts-saved {profileId} {clock.GetUtcNow():O}");
            data.TryAudit($"custom-certification-invalidated {profileId} scripts-changed {clock.GetUtcNow():O}");
            return Result(true, "CustomScriptsSaved",
                "Custom game scripts saved in Windows protected storage. Any prior remote-control certification was revoked.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.Cryptography.CryptographicException)
        {
            return Result(false, "CustomScriptsSaveFailed", "Custom scripts could not be saved: " + ex.Message);
        }
        finally { gate.Release(); }
    }

    public async Task<CustomCertificationResult> BeginCustomCertificationAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(candidate =>
                candidate.Id == profileId && candidate.Kind == GameKinds.Custom);
            if (profile is null)
                return CertificationResult(false, "InvalidProfile", "Choose a saved Custom profile first.",
                    new(profileId, CustomCertification.NotCertified, "This is not a saved Custom profile.", false, false,
                        BlockReason: "Invalid profile."));
            if (data.Recovery.LifecycleBlocked)
                return CertificationResult(false, "DataRecoveryRequired",
                    "Review and acknowledge the recovered local data before beginning live certification.",
                    CertificationState(profile));
            if (runs.Any(run => run.ProfileId == profileId))
                return CertificationResult(false, "CertificationRequiresOffline",
                    "Stop or resolve this Custom server before beginning live certification.",
                    CertificationState(profile));
            var scripts = data.LoadCustomScripts(profileId);
            var scriptError = scripts is null ? "Save all three Custom scripts before certification." :
                CustomGameScripts.Validate(scripts);
            if (scriptError is not null)
                return CertificationResult(false, "CustomScriptsInvalid",
                    scriptError, CertificationState(profile));
            if (!games.TryGet(GameKinds.Custom, out var registered) || registered is not CustomGameServerDriver driver)
                return CertificationResult(false, "CustomDriverUnavailable", "The Custom game driver is unavailable.",
                    CertificationState(profile));

            data.DeleteCustomCertification(profileId);
            customCertificationSessions.Remove(profileId);
            shutdownDeadlines.Remove(profileId);
            hostAddedTime.Remove(profileId);
            friendAddedMinutes.Remove(profileId);
            var fingerprint = CustomCertification.Fingerprint(profile, scripts!, driver.Ports(profile));
            var started = StartUnderGate(profileId, false);
            if (!started.Ok)
                return CertificationResult(false, started.Code,
                    "Certification could not start the first live run. " + started.Message,
                    new(profileId, CustomCertification.NotCertified,
                        "Certification did not begin because the first live run could not start.", false, false,
                        BlockReason: started.Message));
            var session = new CustomCertificationSession
            {
                ProfileId = profileId,
                Fingerprint = fingerprint,
                StartedUtc = clock.GetUtcNow(),
                Stage = CustomCertification.StartingFirstRun
            };
            customCertificationSessions[profileId] = session;
            data.TryAudit($"custom-certification-began {profileId} {clock.GetUtcNow():O}");
            return CertificationResult(true, "CustomCertificationBegan",
                "The first live run started. Use Check step until it is Ready with zero players.",
                CustomCertification.State(session));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return CertificationResult(false, "CustomCertificationUnavailable",
                "Protected Custom certification data could not be read or written: " + ex.Message,
                new(profileId, CustomCertification.NotCertified, "Certification is unavailable.", false, false,
                    BlockReason: ex.Message));
        }
        finally { gate.Release(); }
    }

    public async Task<CustomCertificationResult> CheckCustomCertificationAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(candidate =>
                candidate.Id == profileId && candidate.Kind == GameKinds.Custom);
            if (profile is null)
                return CertificationResult(false, "InvalidProfile", "Choose a saved Custom profile first.",
                    new(profileId, CustomCertification.NotCertified, "This is not a saved Custom profile.", false, false));
            if (!customCertificationSessions.TryGetValue(profileId, out var session))
                return CertificationResult(false, "CertificationNotRunning",
                    "Begin live certification before checking a step.", CertificationState(profile));
            if (session.Stage == CustomCertification.Failed)
                return CertificationResult(false, "CustomCertificationFailed", session.Failure ?? "Certification failed.",
                    CustomCertification.State(session));
            if (clock.GetUtcNow() - session.StartedUtc > TimeSpan.FromHours(4))
                return FailCertification(profile, session, "CertificationExpired",
                    "The live certification window expired after four hours. The running server was left under local owner control.");
            if (!CurrentCustomFingerprint(profile, out var fingerprint, out var fingerprintError) ||
                !string.Equals(fingerprint, session.Fingerprint, StringComparison.Ordinal))
                return FailCertification(profile, session, "CertificationConfigurationChanged",
                    fingerprintError ?? "The scripts, world/save directory, declared ports, or contract version changed during certification.");
            var run = runs.SingleOrDefault(candidate => candidate.ProfileId == profileId);
            if (run is null || Identity(run) != "Matched")
                return FailCertification(profile, session, "CertificationProcessUnavailable",
                    "The exact tracked Custom wrapper is no longer verifiable. Certification did not complete.");
            if (!games.TryGet(run.Kind, out var registered) || registered is not CustomGameServerDriver driver)
                return FailCertification(profile, session, "CustomDriverUnavailable", "The Custom game driver is unavailable.");

            var probe = driver.ProbeContract(run);
            if (!probe.ContractValid)
                return FailCertification(profile, session, "CustomContractInvalid",
                    "Contract v2 proof failed: " + (probe.ContractError ?? probe.Health.Detail));
            session.OnlinePlayers = probe.Health.OnlinePlayers;
            if (probe.Health.State == "Failed")
                return FailCertification(profile, session, "CustomServerFailed",
                    "The Status script reported Failed. Certification did not complete.");
            if (probe.Health.State != "Ready")
                return CertificationResult(true, "CustomCertificationWaiting",
                    "The tracked server is not Ready yet. Check again after startup completes.",
                    CustomCertification.State(session));

            var players = probe.Health.OnlinePlayers!.Value;
            switch (session.Stage)
            {
                case CustomCertification.StartingFirstRun:
                    if (players != 0)
                        return FailCertification(profile, session, "CertificationExpectedEmpty",
                            "The first Ready observation must report exactly zero players. Ask everyone to leave and begin again.");
                    session.Stage = CustomCertification.AwaitingFirstJoin;
                    break;
                case CustomCertification.AwaitingFirstJoin:
                    if (players > 0) session.Stage = CustomCertification.AwaitingFirstLeave;
                    break;
                case CustomCertification.AwaitingFirstLeave:
                    if (players == 0) session.Stage = CustomCertification.ConfirmFirstChange;
                    break;
                case CustomCertification.AwaitingRestartReady:
                    if (players != 0)
                        return FailCertification(profile, session, "CertificationExpectedEmptyAfterRestart",
                            "The first Ready observation after restart must report exactly zero players. Begin again with the server empty.");
                    session.Stage = CustomCertification.AwaitingSecondJoin;
                    break;
                case CustomCertification.AwaitingSecondJoin:
                    if (players > 0) session.Stage = CustomCertification.ConfirmSecondChange;
                    break;
                case CustomCertification.AwaitingSecondLeave:
                    if (players == 0)
                    {
                        var scripts = data.LoadCustomScripts(profileId)!;
                        var certification = CustomCertification.Create(profile, scripts, driver.Ports(profile), clock.GetUtcNow());
                        if (!string.Equals(certification.Fingerprint, session.Fingerprint, StringComparison.Ordinal))
                            return FailCertification(profile, session, "CertificationConfigurationChanged",
                                "The Custom lifecycle configuration changed before certification could be saved.");
                        data.SaveCustomCertification(certification);
                        customCertificationSessions.Remove(profileId);
                        observations.Remove(profileId);
                        data.TryAudit($"custom-certification-completed {profileId} {clock.GetUtcNow():O}");
                        var completed = CertificationState(profile);
                        return CertificationResult(true, "CustomCertificationCompleted",
                            "Owner-certified Custom control is active for this exact profile, script set, world/save directory, port set, and contract version.",
                            completed);
                    }
                    break;
            }
            return CertificationResult(true, "CustomCertificationAdvanced",
                CustomCertification.State(session).Message, CustomCertification.State(session));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            if (customCertificationSessions.TryGetValue(profileId, out var session))
            {
                session.Stage = CustomCertification.Failed;
                session.Failure = "Certification data failed: " + ex.Message;
                session.OnlinePlayers = null;
            }
            return CertificationResult(false, "CustomCertificationUnavailable",
                "Protected Custom certification data could not be read or written: " + ex.Message,
                customCertificationSessions.TryGetValue(profileId, out var current)
                    ? CustomCertification.State(current)
                    : new(profileId, CustomCertification.NotCertified, "Certification is unavailable.", false, false,
                        BlockReason: ex.Message));
        }
        finally { gate.Release(); }
    }

    public async Task<CustomCertificationResult> ConfirmCustomCertificationAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(candidate =>
                candidate.Id == profileId && candidate.Kind == GameKinds.Custom);
            if (profile is null || !customCertificationSessions.TryGetValue(profileId, out var session))
                return CertificationResult(false, "CertificationNotRunning",
                    "Begin live certification before confirming save behavior.",
                    profile is null
                        ? new(profileId, CustomCertification.NotCertified, "This is not a saved Custom profile.", false, false)
                        : CertificationState(profile));
            if (data.Recovery.LifecycleBlocked)
                return CertificationResult(false, "DataRecoveryRequired",
                    "Review and acknowledge the recovered local data before certification can stop or restart a server.",
                    CustomCertification.State(session));
            if (session.Stage is not (CustomCertification.ConfirmFirstChange or CustomCertification.ConfirmSecondChange))
                return CertificationResult(false, "CertificationConfirmationNotExpected",
                    "Complete the current certification observation before confirming save behavior.",
                    CustomCertification.State(session));
            if (!CurrentCustomFingerprint(profile, out var fingerprint, out var fingerprintError) ||
                !string.Equals(fingerprint, session.Fingerprint, StringComparison.Ordinal))
                return FailCertification(profile, session, "CertificationConfigurationChanged",
                    fingerprintError ?? "The Custom lifecycle configuration changed during certification.");
            var run = runs.SingleOrDefault(candidate => candidate.ProfileId == profileId);
            if (run is null || Identity(run) != "Matched" ||
                !games.TryGet(run.Kind, out var registered) || registered is not CustomGameServerDriver driver)
                return FailCertification(profile, session, "CertificationProcessUnavailable",
                    "The exact tracked Custom wrapper is no longer verifiable. Certification did not complete.");
            var probe = driver.ProbeContract(run);
            if (!probe.ContractValid || probe.Health.State != "Ready" || probe.Health.OnlinePlayers is null)
                return FailCertification(profile, session, "CustomContractInvalid",
                    "A fresh Ready contract-v2 observation was not available for confirmation. " +
                    (probe.ContractError ?? probe.Health.Detail));

            if (session.Stage == CustomCertification.ConfirmSecondChange)
            {
                if (probe.Health.OnlinePlayers <= 0)
                    return CertificationResult(false, "CertificationPlayerRequired",
                        "Rejoin and verify the recognizable saved change while the player count is positive, then confirm again.",
                        CustomCertification.State(session));
                session.Stage = CustomCertification.AwaitingSecondLeave;
                session.OnlinePlayers = probe.Health.OnlinePlayers;
                data.TryAudit($"custom-certification-save-confirmed {profileId} {clock.GetUtcNow():O}");
                return CertificationResult(true, "CustomSaveConfirmed",
                    CustomCertification.State(session).Message, CustomCertification.State(session));
            }

            if (probe.Health.OnlinePlayers != 0)
                return CertificationResult(false, "CertificationExpectedEmpty",
                    "A player appeared after the initial zero observation. Leave the server, check the step again, then confirm.",
                    CustomCertification.State(session));
            var stopped = await StopUnderGateAsync(profileId, candidate =>
            {
                var final = driver.ProbeContract(candidate);
                return final.ContractValid && final.Health.State == "Ready" && final.Health.OnlinePlayers == 0;
            });
            if (!stopped.Ok)
                return FailCertification(profile, session, stopped.Code,
                    "The exact wrapper was not confirmed gracefully stopped, so certification failed. " + stopped.Message);
            var started = StartUnderGate(profileId, false);
            if (!started.Ok)
                return FailCertification(profile, session, "CertificationRestartFailed",
                    "The server stopped safely but the same profile did not start again. Certification failed. " + started.Message);
            session.Stage = CustomCertification.AwaitingRestartReady;
            session.OnlinePlayers = null;
            observations.Remove(profileId);
            data.TryAudit($"custom-certification-restarted {profileId} {clock.GetUtcNow():O}");
            return CertificationResult(true, "CustomCertificationRestarted",
                CustomCertification.State(session).Message, CustomCertification.State(session));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            var state = customCertificationSessions.TryGetValue(profileId, out var session)
                ? CustomCertification.State(session)
                : new CustomCertificationState(profileId, CustomCertification.NotCertified,
                    "Certification is unavailable.", false, false, BlockReason: ex.Message);
            return CertificationResult(false, "CustomCertificationUnavailable", ex.Message, state);
        }
        finally { gate.Release(); }
    }

    public async Task<CustomCertificationResult> CancelCustomCertificationAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            customCertificationSessions.Remove(profileId);
            data.DeleteCustomCertification(profileId);
            shutdownDeadlines.Remove(profileId);
            hostAddedTime.Remove(profileId);
            friendAddedMinutes.Remove(profileId);
            observations.Remove(profileId);
            data.TryAudit($"custom-certification-canceled {profileId} {clock.GetUtcNow():O}");
            var profile = settings.Profiles.SingleOrDefault(candidate =>
                candidate.Id == profileId && candidate.Kind == GameKinds.Custom);
            var state = profile is null
                ? new CustomCertificationState(profileId, CustomCertification.NotCertified,
                    "This is not a saved Custom profile.", false, false)
                : CertificationState(profile);
            return CertificationResult(true, "CustomCertificationCanceled",
                "Certification was canceled and grants no remote lifecycle authority. Any running server remains under local owner control.", state);
        }
        finally { gate.Release(); }
    }

    public async Task<CustomCertificationResult> RevokeCustomCertificationAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(candidate =>
                candidate.Id == profileId && candidate.Kind == GameKinds.Custom);
            if (profile is null)
                return CertificationResult(false, "InvalidProfile", "Choose a saved Custom profile first.",
                    new(profileId, CustomCertification.NotCertified, "This is not a saved Custom profile.", false, false));
            customCertificationSessions.Remove(profileId);
            data.DeleteCustomCertification(profileId);
            shutdownDeadlines.Remove(profileId);
            hostAddedTime.Remove(profileId);
            friendAddedMinutes.Remove(profileId);
            observations.Remove(profileId);
            data.TryAudit($"custom-certification-revoked {profileId} {clock.GetUtcNow():O}");
            return CertificationResult(true, "CustomCertificationRevoked",
                "Owner-certified Custom control was revoked. Local Start and Stop remain available.", CertificationState(profile));
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> StartAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try { return StartUnderGate(profileId, false); }
        finally { gate.Release(); }
    }

    // This is an explicit Friend action after an ordinary Start reports the
    // conflict. It never runs automatically from the first Start request.
    public async Task<ActionResult> ReplaceEmptyPortConflictsAndStartAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var initial = StartUnderGate(profileId, false);
            if (initial.Code != "PortConflict" || initial.PortConflicts is not { Count: > 0 }) return initial;
            if (initial.PortConflicts.Any(conflict => !conflict.CanReplace))
                return Result(false, "PortConflictProtected",
                    initial.PortConflicts.First(conflict => !conflict.CanReplace).BlockReason ??
                    "The conflicting server cannot be stopped safely.", initial.PortConflicts);
            var conflictingIds = initial.PortConflicts.Select(conflict => conflict.ProfileId).ToHashSet();
            if (runs.Count(run => !conflictingIds.Contains(run.ProfileId)) >= settings.MaxConcurrentServers)
                return Result(false, "MaxConcurrent",
                    "Stopping the port conflict would still leave the managed server limit full, so no server was stopped.",
                    initial.PortConflicts);

            var stoppedNames = new List<string>();
            foreach (var conflict in initial.PortConflicts)
            {
                if (HostAddedTimeIsActive(conflict.ProfileId))
                    return Result(false, "PortConflictProtected",
                        $"{conflict.ProfileName} is being kept alive with time added by " +
                        (friendAddedMinutes.GetValueOrDefault(conflict.ProfileId) > 0 ? "a Friend." : "the Host."),
                        initial.PortConflicts);
                var operation = await StopUnderGateAsync(conflict.ProfileId, run =>
                    !HostAddedTimeIsActive(conflict.ProfileId) &&
                    settings.Profiles.SingleOrDefault(profile => profile.Id == conflict.ProfileId)?.Maintenance?.Enabled != true &&
                    games.TryGet(run.Kind, out var driver) &&
                    driver.Health(run) is
                    {
                        Ok: true,
                        State: "Ready",
                        OnlinePlayers: 0,
                        PlayerCountTrusted: true
                    });
                if (!operation.Ok)
                    return Result(false, operation.Code,
                        $"{conflict.ProfileName} was not stopped, so the requested server was not started. {operation.Message}",
                        initial.PortConflicts);
                stoppedNames.Add(conflict.ProfileName);
            }

            var started = StartUnderGate(profileId, false);
            return started.Ok
                ? started with
                {
                    Code = "PortConflictReplaced",
                    Message = $"Stopped empty {string.Join(", ", stoppedNames)} and started the requested server. {started.Message}"
                }
                : started;
        }
        finally { gate.Release(); }
    }

    private ActionResult StartUnderGate(Guid profileId, bool crashRecoveryAttempt)
    {
        if (data.Recovery.LifecycleBlocked)
            return Result(false, "DataRecoveryRequired",
                "Review and acknowledge the recovered local data before starting a server.");
        if (!crashRecoveryAttempt && crashRecovery.RemoveAll(item => item.ProfileId == profileId) > 0)
            data.SaveCrashRecoveryStates(crashRecovery);
        var profile = settings.Profiles.SingleOrDefault(p => p.Id == profileId);
        if (profile is null) return Result(false, "UnknownProfile", "Choose a saved profile.");
        if (!games.TryGet(profile.Kind, out var driver))
            return Result(false, "UnsupportedGame", "This game type is not installed in TogetherServer.");
        if (runs.Any(r => r.ProfileId == profileId))
            return Result(false, "AlreadyManaged", "This profile already has a managed or unresolved run.");
        if (runs.Any(r => WorldConflict(r, profile)))
            return Result(false, "WorldConflict", "Another managed run owns this world or save directory.");
        var managedExecutable = driver.ManagedExecutablePath(profile);
        if (string.IsNullOrWhiteSpace(managedExecutable) || !File.Exists(managedExecutable))
            return Result(false, "ExecutableMissing", "Selected server executable does not exist.");
        var validation = driver.ValidateForStart(profile);
        if (validation is not null) return Result(false, validation.Code, validation.Message);
        var declaredPorts = driver.Ports(profile).ToList();
        var conflicts = new List<(ManagedRun Run, IReadOnlyList<GamePort> SharedPorts)>();
        foreach (var existing in runs)
        {
            var ownedPorts = PortsForRun(existing);
            if (ownedPorts is null)
                return Result(false, "PortOwnershipUnknown", "A managed run's game ports cannot be verified. Resolve that run before starting another server.");
            var shared = ownedPorts.Where(owned => declaredPorts.Any(requested => PortOverlap(owned, requested))).ToList();
            if (shared.Count > 0) conflicts.Add((existing, shared));
        }
        if (conflicts.Count > 0)
        {
            var views = conflicts.Select(conflict => PortConflict(conflict.Run, conflict.SharedPorts)).ToList();
            var descriptions = views.Select(view =>
                $"{view.ProfileName} ({string.Join(", ", view.SharedPorts.Select(PortLabel))})");
            return Result(false, "PortConflict",
                $"{profile.Name} shares a game port with running {string.Join("; ", descriptions)}. Stop the conflicting server before starting this one.",
                views);
        }
        // Report a concrete port conflict before the general concurrency limit,
        // so the person who initiated Start gets the actionable reason.
        if (runs.Count >= settings.MaxConcurrentServers)
            return Result(false, "MaxConcurrent", "The managed server limit has been reached.");
        if (!GameServerRegistry.PortsAvailable(declaredPorts))
            return Result(false, "PortInUse", "One or more configured game ports are already in use by another program.");

        var run = new ManagedRun
        {
            ProfileId = profile.Id,
            OperationId = Guid.NewGuid(),
            Kind = profile.Kind,
            WorldId = profile.WorldId,
            WorldDirectory = Path.GetFullPath(profile.WorldDirectory),
            GamePort = profile.GamePort,
            DeclaredPorts = declaredPorts,
            ExecutablePath = Path.GetFullPath(managedExecutable),
            ServerArtifactPath = profile.Minecraft?.ServerJarPath ?? "",
            StopPipeName = "TogetherServer.Fixture." + Guid.NewGuid().ToString("N")
        };
        shutdownDeadlines.Remove(profileId);
        hostAddedTime.Remove(profileId);
        friendAddedMinutes.Remove(profileId);
        observations.Remove(profileId);
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
            Activity("Lifecycle", "Started", $"{profile.Name} started.", ActivitySeverity.Important,
                profile.Id, visibility: ActivityVisibility.AssignedFriends);
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

    public async Task<ActionResult> StopAsync(Guid profileId, Func<ManagedRun, bool>? remoteStillSafe = null)
    {
        await gate.WaitAsync();
        try
        {
            if (remoteStillSafe is not null && data.Recovery.LifecycleBlocked)
                return Result(false, "DataRecoveryRequired",
                    "Remote lifecycle actions are paused until the owner reviews and acknowledges the recovered local data.");
            return await StopUnderGateAsync(profileId, remoteStillSafe);
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> RestartAsync(Guid profileId, Func<ManagedRun, bool>? remoteStillSafe = null)
    {
        await gate.WaitAsync();
        try
        {
            if (data.Recovery.LifecycleBlocked)
                return Result(false, "DataRecoveryRequired",
                    "Review and acknowledge the recovered local data before restarting a server.");
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return Result(false, "UnknownProfile", "Choose a saved profile.");
            var stopped = await StopUnderGateAsync(profileId, remoteStillSafe);
            if (!stopped.Ok)
                return Result(false, stopped.Code, "Restart did not stop the server. " + stopped.Message,
                    stopped.PortConflicts);
            var started = StartUnderGate(profileId, false);
            if (!started.Ok)
                return Result(false, "RestartStartFailed",
                    $"{profile.Name} stopped successfully, but it could not start again. {started.Message}",
                    started.PortConflicts);
            return Result(true, "ServerRestarted",
                $"{profile.Name} stopped gracefully and started again. {started.Message}");
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<ActionResult>> MaintainIdleShutdownAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (data.Recovery.LifecycleBlocked)
            {
                shutdownDeadlines.Clear();
                hostAddedTime.Clear();
                friendAddedMinutes.Clear();
                return [Result(false, "DataRecoveryRequired",
                    "Automatic shutdown is paused until the owner reviews and acknowledges the recovered local data.")];
            }
            if (!settings.AutoShutdownEnabled)
            {
                shutdownDeadlines.Clear();
                hostAddedTime.Clear();
                friendAddedMinutes.Clear();
                return [];
            }
            var snapshot = Snapshot();
            var now = clock.GetUtcNow();
            var due = snapshot.Runs
                .Where(run => run.AutoShutdownAtUtc is { } deadline && deadline <= now)
                .Select(run => run.ProfileId).ToList();
            var results = new List<ActionResult>(due.Count);
            foreach (var profileId in due)
            {
                if (!shutdownDeadlines.Remove(profileId, out var deadline)) continue;
                hostAddedTime.Remove(profileId);
                friendAddedMinutes.Remove(profileId);
                var result = await StopUnderGateAsync(profileId,
                    run => AutoShutdownStillSafe(run, deadline));
                data.TryAudit($"auto-stop {profileId} {result.Code} {clock.GetUtcNow():O}");
                Activity("Countdown", result.Ok ? "AutomaticStopCompleted" : "AutomaticStopFailed",
                    result.Ok ? "The empty-server countdown completed and the server stopped."
                        : "The empty-server countdown reached zero, but the safe Stop did not complete.",
                    result.Ok ? ActivitySeverity.Important : ActivitySeverity.Warning,
                    profileId, visibility: ActivityVisibility.AssignedFriends);
                results.Add(result);
            }
            return results;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<ActionResult>> MaintainCrashRecoveryAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (data.Recovery.LifecycleBlocked)
                return [Result(false, "DataRecoveryRequired",
                    "Crash recovery is paused until the owner reviews and acknowledges the recovered local data.")];
            var now = clock.GetUtcNow();
            var results = new List<ActionResult>();
            foreach (var recovery in crashRecovery.Where(item => item.State == CrashRecoveryStates.Starting &&
                         item.ReadinessDeadlineUtc <= now).ToList())
            {
                var run = runs.SingleOrDefault(item => item.ProfileId == recovery.ProfileId);
                if (run is null)
                {
                    FailCrashRecoveryAttempt(recovery, "The recovery process was no longer recorded before reaching Ready.");
                    results.Add(Result(false, "RecoveryProcessMissing",
                        "Crash recovery did not reach Ready and the recovery process is no longer recorded."));
                    continue;
                }
                var identity = Identity(run);
                if (identity == "Missing")
                {
                    ArchiveDefinitivelyExitedRun(run, "RecoveryProcessExitedBeforeReady");
                    results.Add(Result(false, "RecoveryProcessExited",
                        "The crash-recovery process exited before reaching Ready."));
                    continue;
                }

                recovery.State = CrashRecoveryStates.Suspended;
                recovery.NextAttemptUtc = null;
                recovery.ReadinessDeadlineUtc = null;
                recovery.LastFailure = identity == "Matched"
                    ? "The recovery process stayed running without reaching Ready before the readiness deadline."
                    : "The recovery process did not reach Ready and its exact identity can no longer be proven.";
                data.SaveCrashRecoveryStates(crashRecovery);
                data.TryAudit($"crash-recovery-readiness-timeout {recovery.ProfileId} cycle={recovery.CycleId} identity={identity} {now:O}");
                Activity("Recovery", "CrashRecoverySuspended",
                    "Crash recovery was suspended because the launched server did not reach Ready in time. Review it locally.",
                    ActivitySeverity.Warning, recovery.ProfileId, visibility: ActivityVisibility.AssignedFriends);
                results.Add(Result(false, "RecoveryStartupTimedOut",
                    "Crash recovery was suspended because the launched process did not reach Ready in time."));
            }
            foreach (var recovery in crashRecovery
                         .Where(item => item.State == CrashRecoveryStates.Pending && item.NextAttemptUtc <= now)
                         .OrderBy(item => item.NextAttemptUtc).ToList())
            {
                var profile = settings.Profiles.SingleOrDefault(item => item.Id == recovery.ProfileId);
                if (profile is null || !profile.CrashRecovery.Enabled ||
                    !games.TryGet(profile.Kind, out var driver) || !driver.SupportsCrashRecovery)
                {
                    crashRecovery.Remove(recovery);
                    data.SaveCrashRecoveryStates(crashRecovery);
                    continue;
                }
                if (runs.Any(item => item.ProfileId == profile.Id)) continue;

                recovery.Attempts++;
                recovery.State = CrashRecoveryStates.Starting;
                recovery.NextAttemptUtc = null;
                recovery.ReadinessDeadlineUtc = null;
                data.SaveCrashRecoveryStates(crashRecovery);
                data.TryAudit($"crash-recovery-attempt {profile.Id} cycle={recovery.CycleId} attempt={recovery.Attempts} {now:O}");
                Activity("Recovery", "CrashRestartAttempt", $"Crash recovery attempt {recovery.Attempts} of 3 started.",
                    ActivitySeverity.Warning, profile.Id, visibility: ActivityVisibility.AssignedFriends);
                var result = StartUnderGate(profile.Id, true);
                results.Add(result);
                if (!result.Ok)
                    FailCrashRecoveryAttempt(recovery, result.Code + ": " + result.Message);
                else
                {
                    recovery.ReadinessDeadlineUtc = clock.GetUtcNow().Add(CrashRecoveryReadinessTimeout);
                    data.SaveCrashRecoveryStates(crashRecovery);
                }
            }
            return results;
        }
        finally { gate.Release(); }
    }

    public async Task<WorldBackupList> BackupsAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try { return new(backups.List(profileId), backups.Status(profileId)); }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> RestoreBackupAsync(Guid profileId, Guid backupId)
    {
        await gate.WaitAsync();
        try
        {
            if (data.Recovery.LifecycleBlocked)
                return Result(false, "DataRecoveryRequired",
                    "Review and acknowledge the recovered local data before restoring a backup.");
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return Result(false, "UnknownProfile", "Choose a saved profile.");
            if (!games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups)
                return Result(false, "BackupsUnsupported", "Backups and restore are available only for reviewed built-in game drivers.");
            var run = runs.SingleOrDefault(item => item.ProfileId == profileId);
            if (run is not null)
            {
                var identity = Identity(run);
                if (identity == "Missing") ArchiveDefinitivelyExitedRun(run, "ProcessExitedBeforeRestore");
                else return Result(false, identity == "Matched" ? "ServerRunning" : "IdentityUnknown",
                    identity == "Matched"
                        ? "Stop the server gracefully before restoring a backup."
                        : "Process identity is uncertain, so restore remains blocked.");
            }
            if (crashRecovery.RemoveAll(item => item.ProfileId == profileId) > 0)
                data.SaveCrashRecoveryStates(crashRecovery);
            var saveDirectory = driver.ManagedSaveDirectory(profile);
            if (saveDirectory is null)
                return Result(false, "BackupsUnsupported", "The selected driver does not expose a reviewed save-only directory.");
            var restored = backups.Restore(profile, backupId, saveDirectory);
            Activity("Backup", restored.Ok ? "RestoreCompleted" : "RestoreFailed",
                restored.Ok ? "A local-owner backup restore completed." : "A local-owner backup restore failed. Review details locally.",
                restored.Ok ? ActivitySeverity.Important : ActivitySeverity.Warning, profileId);
            return Result(restored.Ok, restored.Code, restored.Message);
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> ExtendAutoShutdownAsync(Guid profileId, long minutes)
    {
        await gate.WaitAsync();
        try
        {
            if (data.Recovery.LifecycleBlocked)
                return Result(false, "DataRecoveryRequired",
                    "Countdown changes are paused until the owner reviews and acknowledges the recovered local data.");
            if (minutes < 1)
                return Result(false, "InvalidExtension", "Enter a positive whole number of minutes to add.");
            if (!settings.AutoShutdownEnabled)
                return Result(false, "TimerNotRunning", "Automatic shutdown is off, so there is no countdown to extend.");
            var view = Snapshot().Runs.SingleOrDefault(run => run.ProfileId == profileId);
            if (view is null || view.State != "Ready" || view.OnlinePlayers != 0 ||
                view.AutoShutdownAtUtc is null || !shutdownDeadlines.TryGetValue(profileId, out var deadline))
                return Result(false, "TimerNotRunning",
                    "The server needs an active zero-player countdown before time can be added.");
            try { shutdownDeadlines[profileId] = deadline.AddMinutes(minutes); }
            catch (ArgumentOutOfRangeException)
            {
                return Result(false, "InvalidExtension", "That extension would put the countdown outside the supported date range.");
            }
            hostAddedTime.Add(profileId);
            data.TryAudit($"auto-shutdown-extended {profileId} minutes={minutes} {clock.GetUtcNow():O}");
            Activity("Countdown", "OwnerExtended", $"The Host added {minutes} minute{(minutes == 1 ? "" : "s")} to the countdown.",
                ActivitySeverity.Important, profileId, visibility: ActivityVisibility.AssignedFriends);
            var profileName = settings.Profiles.SingleOrDefault(profile => profile.Id == profileId)?.Name ?? "Server";
            return Result(true, "CountdownExtended", $"Added {minutes} minute{(minutes == 1 ? "" : "s")} to {profileName}'s countdown.");
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> ExtendAutoShutdownForFriendAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            if (data.Recovery.LifecycleBlocked)
                return Result(false, "DataRecoveryRequired",
                    "Remote lifecycle actions are paused until the owner reviews and acknowledges the recovered local data.");
            var increment = settings.FriendTimerExtensionMinutes;
            var maximum = settings.FriendTimerExtensionMaximumMinutes;
            if (!settings.AutoShutdownEnabled)
                return Result(false, "TimerNotRunning", "Automatic shutdown is off, so there is no countdown to extend.");
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return Result(false, "UnknownProfile", "Choose a saved profile.");
            if (profile.Maintenance?.Enabled == true)
                return Result(false, "MaintenanceMode", "Remote timer extension is paused while this server is in maintenance mode.");
            var view = Snapshot().Runs.SingleOrDefault(run => run.ProfileId == profileId);
            if (view is null || view.State != "Ready" || !view.PlayerCountTrusted || view.OnlinePlayers != 0 ||
                view.AutoShutdownAtUtc is null || !shutdownDeadlines.TryGetValue(profileId, out var deadline))
                return Result(false, "TimerNotRunning",
                    "A fresh authoritative zero-player countdown is required before time can be added.");
            var alreadyAdded = friendAddedMinutes.GetValueOrDefault(profileId);
            if (increment < 1 || maximum < increment || alreadyAdded + increment > maximum)
                return Result(false, "ExtensionLimitReached",
                    $"The Host allows at most {maximum} minutes of Friend-added time per countdown.");
            try { shutdownDeadlines[profileId] = deadline.AddMinutes(increment); }
            catch (ArgumentOutOfRangeException)
            {
                return Result(false, "InvalidExtension", "The configured extension would put the countdown outside the supported date range.");
            }
            friendAddedMinutes[profileId] = alreadyAdded + increment;
            hostAddedTime.Add(profileId);
            data.TryAudit($"auto-shutdown-friend-extended {profileId} minutes={increment} total={alreadyAdded + increment} {clock.GetUtcNow():O}");
            Activity("Countdown", "FriendExtended", $"A Friend added the fixed {increment}-minute extension.",
                ActivitySeverity.Important, profileId, visibility: ActivityVisibility.AssignedFriends);
            return Result(true, "CountdownExtended",
                $"Added the Host-configured {increment} minutes to {profile.Name}'s countdown. " +
                $"{maximum - alreadyAdded - increment} Friend-added minutes remain for this countdown.");
        }
        finally { gate.Release(); }
    }

    private async Task<ActionResult> StopUnderGateAsync(Guid profileId, Func<ManagedRun, bool>? remoteStillSafe)
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
            // Persist intent before signaling the game. If the Host app exits in
            // the middle, the resulting absent process is archived but never
            // mistaken for a crash that should be relaunched.
            run.StopRequestedUtc = clock.GetUtcNow();
            data.SaveRuns(runs);
            var stopped = await driver.StopAsync(process, run);
            if (stopped.ExitCode != 0)
            {
                if (Identity(run) == "Matched")
                {
                    run.StopRequestedUtc = null;
                    data.SaveRuns(runs);
                }
                return Result(false, stopped.Code, stopped.Message);
            }
            process.Refresh();
            if (!process.HasExited)
            {
                run.StopRequestedUtc = null;
                data.SaveRuns(runs);
                return Result(false, "StopUnconfirmed", "The game driver returned before the exact managed process exited.");
            }

            WorldBackupResult? backup = null;
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            var saveDirectory = profile is null ? null : driver.ManagedSaveDirectory(profile);
            if (profile is not null && profile.Backups.Enabled && driver.SupportsBackups && saveDirectory is not null)
                backup = backups.Create(profile, BackupKinds.Rolling, saveDirectory);
            ArchiveCompletedRun(run, "GracefulStop", false);
            Activity("Lifecycle", "Stopped", $"{profile?.Name ?? "Server"} stopped gracefully.",
                ActivitySeverity.Important, profileId, visibility: ActivityVisibility.AssignedFriends);
            if (backup is { Ok: false })
            {
                Activity("Backup", "BackupFailed", "The rolling backup after Stop failed. Review it locally on the Host.",
                    ActivitySeverity.Warning, profileId);
                return Result(true, "StoppedBackupFailed", stopped.Message + " " + backup.Message);
            }
            return Result(true, stopped.Code, backup?.Ok == true
                ? stopped.Message + " A rolling backup completed."
                : stopped.Message);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (Identity(run) == "Matched")
            {
                run.StopRequestedUtc = null;
                data.SaveRuns(runs);
            }
            return Result(false, "StopUnconfirmed", "The game driver could not confirm a graceful stop: " + ex.Message);
        }
    }

    private bool AutoShutdownStillSafe(ManagedRun run, DateTimeOffset deadline)
    {
        if (!settings.AutoShutdownEnabled ||
            clock.GetUtcNow() < deadline ||
            !games.TryGet(run.Kind, out var driver)) return false;
        var health = driver.Health(run);
        return health.Ok && health.State == "Ready" && health.PlayerCountTrusted && health.OnlinePlayers == 0;
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
                "Matched" => DriverHealth(run, driver.Health(run)),
                "Missing" => Result(false, "ProcessExited", "The recorded process is no longer running."),
                _ => Result(false, "IdentityUnknown", "The recorded process identity cannot be verified.")
            };
        }
        finally { gate.Release(); }
    }

    private ActionResult DriverHealth(ManagedRun run, GameHealthResult health)
    {
        observations[run.ProfileId] = ToObservation(run.ProfileId, run.OperationId, health, clock.GetUtcNow());
        return Result(health.Ok, health.Code, health.Detail);
    }

    public async Task<ActionResult> ForgetAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var run = runs.SingleOrDefault(r => r.ProfileId == profileId);
            if (run is null) return Result(false, "NotManaged", "There is no record to resolve.");
            var identity = Identity(run);
            if (identity == "Matched")
                return Result(false, "StillRunning", "The recorded process is still running and cannot be archived.");
            if (identity != "Missing")
                return Result(false, "IdentityUnknown",
                    "PID reuse, executable mismatch, or access failure prevents proof that the managed process is gone. The world remains blocked.");
            ArchiveDefinitivelyExitedRun(run, "OwnerArchivedExitedRun");
            return Result(true, "RecordArchived", "The exact recorded process was proven absent and its run record was archived.");
        }
        finally { gate.Release(); }
    }

    private HostSnapshot Snapshot()
    {
        var now = clock.GetUtcNow();
        var recovery = data.Recovery;
        var views = settings.Profiles.Select(profile =>
        {
            var run = runs.SingleOrDefault(r => r.ProfileId == profile.Id);
            if (run is null) return new RunView(profile.Id, "Offline", "No managed process", null);
            var identity = Identity(run);
            if (!games.TryGet(run.Kind, out var driver))
                return new RunView(profile.Id, "Unknown", "The game driver for this run is unavailable", run.ProcessId, run.DeclaredPorts);
            return identity switch
            {
                "Matched" => DriverView(profile.Id, run, ObservedHealth(run)),
                "Missing" => new RunView(profile.Id, "Failed", "Recorded process exited; owner can clear the record", run.ProcessId, run.DeclaredPorts),
                _ => new RunView(profile.Id, "Unknown", "Process identity cannot be proven; start and stop are blocked", run.ProcessId, run.DeclaredPorts)
            };
        }).ToList();
        var configuredProfileIds = settings.Profiles.Select(profile => profile.Id).ToHashSet();
        foreach (var run in runs.Where(run => !configuredProfileIds.Contains(run.ProfileId)))
        {
            var identity = Identity(run);
            views.Add(new RunView(run.ProfileId,
                identity switch { "Matched" => "Process running", "Missing" => "Failed", _ => "Unknown" },
                identity == "Matched"
                    ? "A recorded managed process still exists, but its saved server profile is unavailable. Local Stop or resolution is required."
                    : identity == "Missing"
                        ? "A recorded run has exited, but its saved server profile is unavailable. Resolve the record locally."
                        : "A recorded run remains authoritative, but its saved server profile and exact process identity are unavailable.",
                run.ProcessId, run.DeclaredPorts, PlayerCountTrusted: false));
        }
        var currentProfiles = views.Select(view => view.ProfileId).ToHashSet();
        if (recovery.LifecycleBlocked)
        {
            shutdownDeadlines.Clear();
            hostAddedTime.Clear();
            friendAddedMinutes.Clear();
        }
        else
        {
            foreach (var profileId in shutdownDeadlines.Keys.Where(id => !currentProfiles.Contains(id)).ToList())
                CancelCountdown(profileId, "The saved server is no longer available.");
        }
        for (var index = 0; index < views.Count; index++)
        {
            var view = views[index];
            if (recovery.LifecycleBlocked)
            {
                views[index] = view with
                {
                    AutoShutdownAtUtc = null,
                    AutoShutdownReason = "Automatic lifecycle actions are paused until the owner reviews recovered local data.",
                    HostAddedTime = false,
                    FriendAddedMinutes = 0
                };
                continue;
            }
            if (view.State != "Ready")
            {
                CancelCountdown(view.ProfileId, "The server is no longer Ready.");
                continue;
            }
            if (!settings.AutoShutdownEnabled)
            {
                CancelCountdown(view.ProfileId, "Automatic shutdown was turned off.");
                views[index] = view with { AutoShutdownReason = "Automatic shutdown is off." };
                continue;
            }
            if (!view.PlayerCountTrusted)
            {
                CancelCountdown(view.ProfileId, "The authoritative player count became unavailable.");
                views[index] = view with
                {
                    AutoShutdownReason = view.Detail.Contains("Custom", StringComparison.OrdinalIgnoreCase)
                    ? "Owner certification and a fresh valid Custom contract-v2 player count are required for automatic shutdown."
                    : "A fresh authoritative player count is required for automatic shutdown."
                };
                continue;
            }
            if (view.OnlinePlayers is null)
            {
                CancelCountdown(view.ProfileId, "The current player count became unknown.");
                views[index] = view with { AutoShutdownReason = "Waiting for a reliable player count." };
                continue;
            }
            if (view.OnlinePlayers != 0)
            {
                CancelCountdown(view.ProfileId, "A player joined the server.");
                views[index] = view with { AutoShutdownReason = "Waiting for the server to be empty." };
                continue;
            }
            if (!shutdownDeadlines.TryGetValue(view.ProfileId, out var deadline))
            {
                deadline = now.AddMinutes(settings.IdleMinutes);
                shutdownDeadlines[view.ProfileId] = deadline;
                Activity("Countdown", "Started",
                    $"The empty-server countdown started for {settings.IdleMinutes} minutes.",
                    ActivitySeverity.Important, view.ProfileId,
                    visibility: ActivityVisibility.AssignedFriends);
            }
            views[index] = view with
            {
                AutoShutdownAtUtc = deadline,
                HostAddedTime = HostAddedTimeIsActive(view.ProfileId),
                FriendAddedMinutes = friendAddedMinutes.GetValueOrDefault(view.ProfileId)
            };
        }
        return new HostSnapshot(settings, views, "Recorded process identity and game-specific local readiness; Friend join and save unverified", "Host",
            settings.Profiles.Where(profile => profile.Kind == "Valheim")
                .ToDictionary(profile => profile.Id, profile => data.HasValheimPassword(profile.Id)),
            data.ManagedWorldsRoot,
            settings.Profiles.Where(profile => profile.Kind == GameKinds.Custom)
                .ToDictionary(profile => profile.Id, CertificationState),
            crashRecovery.ToDictionary(item => item.ProfileId, item => item),
            settings.Profiles.ToDictionary(profile => profile.Id, profile => backups.Status(profile.Id)),
            data.LoadActivity(100),
            recovery);
    }

    private static RunView DriverView(Guid profileId, ManagedRun run, GameHealthResult health) =>
        new(profileId, health.State, health.Detail, run.ProcessId, run.DeclaredPorts,
            health.OnlinePlayers, health.MaxPlayers, PlayerNames: health.PlayerNames,
            PlayerCountTrusted: health.PlayerCountTrusted);

    private void CancelCountdown(Guid profileId, string reason)
    {
        var wasRunning = shutdownDeadlines.Remove(profileId);
        hostAddedTime.Remove(profileId);
        friendAddedMinutes.Remove(profileId);
        if (wasRunning)
            Activity("Countdown", "Canceled", "The empty-server countdown was canceled. " + reason,
                ActivitySeverity.Important, profileId,
                visibility: ActivityVisibility.AssignedFriends);
    }

    private void Activity(string category, string action, string message,
        string severity = ActivitySeverity.Info, Guid? profileId = null,
        Guid? deviceId = null, string visibility = ActivityVisibility.Local)
    {
        try { data.RecordActivity(category, action, message, severity, profileId, deviceId, visibility); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or JsonException or NotSupportedException)
        { data.TryAudit($"activity-write-failed {category} {action} {ex.GetType().Name} {clock.GetUtcNow():O}"); }
    }

    private ActionResult Result(bool ok, string code, string message,
        IReadOnlyList<PortConflictView>? portConflicts = null) =>
        new(ok, code, message, Snapshot(), portConflicts);

    private PortConflictView PortConflict(ManagedRun run, IReadOnlyList<GamePort> sharedPorts)
    {
        var name = settings.Profiles.SingleOrDefault(profile => profile.Id == run.ProfileId)?.Name ?? "another server";
        var canReplace = CanReplacePortConflict(run, out var reason);
        return new(run.ProfileId, name, sharedPorts, canReplace, reason);
    }

    private bool CanReplacePortConflict(ManagedRun run, out string? reason)
    {
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == run.ProfileId);
        if (profile?.Maintenance?.Enabled == true)
        {
            reason = string.IsNullOrWhiteSpace(profile.Maintenance.Message)
                ? $"{profile.Name} is in maintenance mode."
                : $"{profile.Name} maintenance: {profile.Maintenance.Message}";
            return false;
        }
        if (HostAddedTimeIsActive(run.ProfileId))
        {
            reason = $"{profile?.Name ?? "The conflicting server"} is being kept alive with time added by " +
                (friendAddedMinutes.GetValueOrDefault(run.ProfileId) > 0 ? "a Friend." : "the Host.");
            return false;
        }
        if (Identity(run) != "Matched" || !games.TryGet(run.Kind, out var driver))
        {
            reason = "The conflicting server's process or game driver cannot be verified.";
            return false;
        }
        var health = ObservedHealth(run);
        if (!health.Ok || health.State != "Ready" || !health.PlayerCountTrusted || health.OnlinePlayers is null)
        {
            reason = "The conflicting server does not have a fresh authoritative player count.";
            return false;
        }
        if (health.OnlinePlayers != 0)
        {
            reason = $"The conflicting server has {health.OnlinePlayers} player{(health.OnlinePlayers == 1 ? "" : "s")} online.";
            return false;
        }
        reason = null;
        return true;
    }

    private bool HostAddedTimeIsActive(Guid profileId) => hostAddedTime.Contains(profileId) &&
        shutdownDeadlines.TryGetValue(profileId, out var deadline) && deadline > clock.GetUtcNow();

    private GameHealthResult ObservedHealth(ManagedRun run)
    {
        var now = clock.GetUtcNow();
        if (observations.TryGetValue(run.ProfileId, out var observation) &&
            observation.OperationId == run.OperationId)
        {
            if (now - observation.ObservedUtc <= TimeSpan.FromSeconds(10))
                return new(observation.Ok, observation.Source, observation.State, observation.Detail,
                    observation.OnlinePlayers, observation.MaxPlayers, observation.PlayerNames,
                    observation.PlayerCountTrusted);
            return new(false, "ObservationStale", "Unknown",
                "The last server observation is stale. Waiting for a fresh probe.", PlayerCountTrusted: false);
        }

        return new(false, "ObservationPending", "Unknown",
            "Waiting for the shared server observation supervisor.", PlayerCountTrusted: false);
    }

    private static ServerObservation ToObservation(Guid profileId, Guid operationId,
        GameHealthResult health, DateTimeOffset observedUtc) =>
        new(profileId, operationId, health.State, health.Detail, health.Code, observedUtc,
            health.Ok, health.OnlinePlayers, health.MaxPlayers, health.PlayerNames,
            health.PlayerCountTrusted);

    private CustomCertificationResult CertificationResult(bool ok, string code, string message,
        CustomCertificationState certification) =>
        new(ok, code, message, Snapshot(), certification);

    private CustomCertificationResult FailCertification(ServerProfile profile,
        CustomCertificationSession session, string code, string message)
    {
        session.Stage = CustomCertification.Failed;
        session.Failure = message;
        session.OnlinePlayers = null;
        shutdownDeadlines.Remove(profile.Id);
        hostAddedTime.Remove(profile.Id);
        friendAddedMinutes.Remove(profile.Id);
        observations.Remove(profile.Id);
        try { data.DeleteCustomCertification(profile.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            data.TryAudit($"custom-certification-delete-failed {profile.Id} {ex.GetType().Name} {clock.GetUtcNow():O}");
        }
        data.TryAudit($"custom-certification-failed {profile.Id} code={code} {clock.GetUtcNow():O}");
        return CertificationResult(false, code, message, CustomCertification.State(session));
    }

    private CustomCertificationState CertificationState(ServerProfile profile)
    {
        if (customCertificationSessions.TryGetValue(profile.Id, out var session))
            return CustomCertification.State(session);
        try
        {
            var certification = data.LoadCustomCertification(profile.Id);
            if (certification is null)
                return new(profile.Id, CustomCertification.NotCertified,
                    "Remote Stop, Restart, replacement, and automatic shutdown require owner-completed live certification.",
                    false, false, BlockReason: "Live certification has not been completed.");
            var scripts = data.LoadCustomScripts(profile.Id);
            if (scripts is null || !games.TryGet(GameKinds.Custom, out var registered) ||
                registered is not CustomGameServerDriver driver ||
                !CustomCertification.Matches(certification, profile, scripts, driver.Ports(profile)))
                return new(profile.Id, CustomCertification.NotCertified,
                    "The saved Custom lifecycle configuration no longer matches its certification.", false, false,
                    BlockReason: "Scripts, world/save directory, ports, or contract version changed.");
            return new(profile.Id, CustomCertification.Certified,
                "Owner-certified Custom control is active for this exact lifecycle configuration.",
                false, true, certification.CertifiedUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return new(profile.Id, CustomCertification.NotCertified,
                "The protected Custom certification could not be verified.", false, false,
                BlockReason: ex.GetType().Name);
        }
    }

    private bool CurrentCustomFingerprint(ServerProfile profile, out string? fingerprint, out string? error)
    {
        fingerprint = null;
        error = null;
        try
        {
            var scripts = data.LoadCustomScripts(profile.Id);
            if (scripts is null)
            {
                error = "Protected Custom scripts are unavailable.";
                return false;
            }
            if (!games.TryGet(GameKinds.Custom, out var registered) || registered is not CustomGameServerDriver driver)
            {
                error = "The Custom game driver is unavailable.";
                return false;
            }
            fingerprint = CustomCertification.Fingerprint(profile, scripts, driver.Ports(profile));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Security.Cryptography.CryptographicException or JsonException)
        {
            error = "The Custom lifecycle configuration could not be verified: " + ex.Message;
            return false;
        }
    }

    private static string PortLabel(GamePort port) => $"{port.Protocol.ToUpperInvariant()} {port.Port}" +
        (port.Family == "Any" ? "" : $" {port.Family}");

    private string? Validate(HostSettings next)
    {
        if (next.MaxConcurrentServers < 1 || next.MaxConcurrentServers > 16) return "Maximum servers must be between 1 and 16.";
        if (next.IdleMinutes < 1 || next.IdleMinutes > 1440) return "Idle minutes must be between 1 and 1440.";
        if (next.FriendTimerExtensionMinutes is < 1 or > 120)
            return "Friend countdown increments must be between 1 and 120 minutes.";
        if (next.FriendTimerExtensionMaximumMinutes < next.FriendTimerExtensionMinutes ||
            next.FriendTimerExtensionMaximumMinutes > 1440)
            return "The Friend countdown maximum must be at least one increment and no more than 1440 minutes.";
        if (next.CompanionPort < 1024 || next.CompanionPort > 65535) return "Companion port must be between 1024 and 65535.";
        if (!string.IsNullOrWhiteSpace(next.PublicGameIp) && !GameConnection.IsPublicIpv4(next.PublicGameIp))
            return "The Valheim friend address must be public IPv4; 127.0.0.1, local, shared, and test addresses cannot be used.";
        if (!System.Net.IPAddress.TryParse(next.CompanionBindAddress, out _)) return "Companion bind address must be an IP address.";
        if (!string.IsNullOrWhiteSpace(next.CompanionEndpoint) &&
            (!HostIdentity.TryEndpoint(next.CompanionEndpoint, out var endpoint) || endpoint.Port != next.CompanionPort))
            return "Companion endpoint must be an HTTPS IP address on the configured port.";
        var route = ConnectionRoutes.Normalize(next.ConnectionRoute);
        if (!ConnectionRouteModes.Valid(route.Mode))
            return "Choose Direct Internet, Private mesh, or Advanced address for the Friend route.";
        if (route.Mode != ConnectionRouteModes.DirectInternet && !ConnectionRoutes.ValidAddress(route.Address))
            return "Choose a non-loopback IPv4 address for the selected Friend route.";
        if (route.Mode != ConnectionRouteModes.DirectInternet &&
            HostIdentity.TryEndpoint(next.CompanionEndpoint, out var routeEndpoint) &&
            !string.Equals(routeEndpoint.Host, route.Address, StringComparison.OrdinalIgnoreCase))
            return "The Friend endpoint must use the selected route address.";
        if (next.CompanionListeningEnabled && string.IsNullOrWhiteSpace(next.CompanionEndpoint))
            return "Set the companion endpoint before enabling its listener.";
        if (next.RemoteControlsEnabled && !next.CompanionListeningEnabled)
            return "Enable the authenticated companion listener before remote controls.";
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
            if (!games.TryGet(profile.Kind, out var profileDriver)) return "Choose a supported game type for the server.";
            profile.CrashRecovery ??= new CrashRecoveryOptions();
            profile.Backups ??= new BackupOptions();
            profile.Maintenance ??= new MaintenanceOptions();
            profile.Maintenance.Message = profile.Maintenance.Message?.Trim() ?? "";
            if (profile.Maintenance.Message.Length > 200 || profile.Maintenance.Message.Any(char.IsControl))
                return "Maintenance messages must be at most 200 characters without line breaks.";
            if (profile.CrashRecovery.Enabled && !profileDriver.SupportsCrashRecovery)
                return "Automatic crash recovery is available only for reviewed built-in game drivers.";
            if (profile.Backups.Enabled && !profileDriver.SupportsBackups)
                return "Rolling backups are available only for reviewed built-in game drivers.";
            if (profile.Backups.RetentionCount is < 1 or > 50)
                return "Backup retention must be between 1 and 50 completed backups.";
            if (profile.Backups.MinimumFreeSpaceMb is < 0 or > 1_048_576)
                return "Backup free-space reserve must be between 0 and 1048576 MB.";
            if (!ValheimSetup.ValidWorldId(profile.WorldId))
                return "World ID must be a valid file name of at most 64 characters.";
            if (profile.Kind == "Valheim" && profile.WorldSource is not ("Existing" or "New"))
                return "Choose an existing imported world or explicitly create a new world.";
            if (profile.Kind == "Valheim" && (string.IsNullOrWhiteSpace(profile.ServerName) ||
                profile.ServerName.Length > 80 || profile.ServerName.Any(char.IsControl)))
                return "Valheim server name must be 1 to 80 characters without control characters.";
            if (profile.GamePort < 1024 || profile.GamePort > (profile.Kind == GameKinds.Valheim ? 65534 : 65535))
                return "Game port is outside the valid range for this game.";
            if (profile.Kind == GameKinds.Custom)
            {
                var custom = profile.Custom;
                if (custom is null || string.IsNullOrWhiteSpace(custom.GameName) || custom.GameName.Length > 80 ||
                    custom.GameName.Any(char.IsControl))
                    return "Custom game name must be 1 to 80 characters without control characters.";
                if (custom.PrimaryProtocol is not ("TCP" or "UDP"))
                    return "Custom primary protocol must be TCP or UDP.";
                if (custom.AdditionalPorts is null || custom.AdditionalPorts.Count > 15)
                    return "A custom game may declare at most 15 additional ports.";
                foreach (var port in custom.AdditionalPorts)
                {
                    if (port.Protocol is not ("TCP" or "UDP") || port.Port is < 1024 or > 65535 ||
                        port.Family is not ("Any" or "IPv4" or "IPv6") ||
                        string.IsNullOrWhiteSpace(port.Label) || port.Label.Length > 64 || port.Label.Any(char.IsControl))
                        return "Each custom port needs TCP or UDP, a port from 1024 to 65535, a valid address family, and a short label.";
                }
            }
            if (string.IsNullOrWhiteSpace(profile.WorldDirectory))
                return profile.Kind == "Valheim" && profile.WorldSource == "Existing"
                    ? "Choose and copy an existing world in Setup step 1."
                    : "Choose an existing save directory in Setup step 1.";
            if (!Path.IsPathFullyQualified(profile.WorldDirectory))
                return "The save directory needs a full path, such as C:\\ValheimSaves.";
            if (profile.Kind != GameKinds.Custom && string.IsNullOrWhiteSpace(profile.ExecutablePath))
                return "Select an installed server in Setup step 2.";
            if (profile.Kind != GameKinds.Custom && !Path.IsPathFullyQualified(profile.ExecutablePath))
                return "The installed server path needs a full path to its .exe file.";
        }
        return null;
    }

    private static bool SameProfile(ServerProfile a, ServerProfile b) =>
        a.Kind == b.Kind && a.Name == b.Name && a.ServerName == b.ServerName &&
        a.Crossplay == b.Crossplay && a.PublicListing == b.PublicListing &&
        a.WorldId == b.WorldId && a.WorldSource == b.WorldSource && a.GamePort == b.GamePort &&
        (a.Minecraft?.ServerJarPath ?? "") == (b.Minecraft?.ServerJarPath ?? "") &&
        SameCustom(a.Custom, b.Custom) &&
        Path.GetFullPath(a.WorldDirectory).Equals(Path.GetFullPath(b.WorldDirectory), StringComparison.OrdinalIgnoreCase) &&
        (a.Kind == GameKinds.Custom && b.Kind == GameKinds.Custom ||
         Path.GetFullPath(a.ExecutablePath).Equals(Path.GetFullPath(b.ExecutablePath), StringComparison.OrdinalIgnoreCase));

    private static bool SameCustom(CustomGameOptions? a, CustomGameOptions? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.GameName == b.GameName && a.PrimaryProtocol == b.PrimaryProtocol &&
            a.ShareJoinAddress == b.ShareJoinAddress &&
            (a.AdditionalPorts ?? []).SequenceEqual(b.AdditionalPorts ?? []);
    }

    private static bool SameCustomCertificationInputs(ServerProfile a, ServerProfile b) =>
        a.WorldId == b.WorldId && a.GamePort == b.GamePort &&
        Path.GetFullPath(a.WorldDirectory).Equals(Path.GetFullPath(b.WorldDirectory), StringComparison.OrdinalIgnoreCase) &&
        a.Custom?.PrimaryProtocol == b.Custom?.PrimaryProtocol &&
        (a.Custom?.AdditionalPorts ?? []).SequenceEqual(b.Custom?.AdditionalPorts ?? []);

    private static bool WorldConflict(ManagedRun run, ServerProfile profile) =>
        CanonicalWorldPath(run.WorldDirectory).Equals(CanonicalWorldPath(profile.WorldDirectory),
            StringComparison.OrdinalIgnoreCase);

    private static string CanonicalWorldPath(string value)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!OperatingSystem.IsWindows() || !Directory.Exists(normalized)) return normalized;
        try
        {
            using var handle = CreateFileW(normalized, 0,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                0x02000000, IntPtr.Zero); // FILE_FLAG_BACKUP_SEMANTICS permits a directory handle.
            if (handle.IsInvalid) return normalized;
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return normalized;
            if (length >= (uint)buffer.Capacity)
            {
                buffer.EnsureCapacity(checked((int)length + 1));
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= (uint)buffer.Capacity) return normalized;
            }
            var resolved = buffer.ToString();
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                resolved = @"\\" + resolved[8..];
            else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
                resolved = resolved[4..];
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   NotSupportedException or OverflowException)
        {
            return normalized;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path,
        uint pathLength, uint flags);

    private IReadOnlyList<GamePort>? PortsForRun(ManagedRun run)
    {
        if (run.DeclaredPorts is { Count: > 0 }) return run.DeclaredPorts;
        // Runs saved before declared ports were recorded still have their full saved profile.
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == run.ProfileId && item.Kind == run.Kind);
        return profile is not null && games.TryGet(run.Kind, out var driver) ? driver.Ports(profile) : null;
    }

    private void ArchiveDefinitivelyExitedRun(ManagedRun run, string reason)
    {
        var preserveRecoveryState = false;
        var recoveryScheduled = false;
        var existing = crashRecovery.SingleOrDefault(item => item.ProfileId == run.ProfileId);
        if (run.StopRequestedUtc is not null)
        {
            if (existing is not null) crashRecovery.Remove(existing);
        }
        else if (existing?.State == CrashRecoveryStates.Starting)
        {
            FailCrashRecoveryAttempt(existing, "The recovery process exited before reaching Ready.");
            preserveRecoveryState = true;
            recoveryScheduled = existing.State == CrashRecoveryStates.Pending;
        }
        else
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == run.ProfileId);
            if (run.WasReady && profile is not null && profile.CrashRecovery.Enabled &&
                games.TryGet(profile.Kind, out var driver) && driver.SupportsCrashRecovery)
            {
                if (existing is not null) crashRecovery.Remove(existing);
                var state = new CrashRecoveryState
                {
                    ProfileId = run.ProfileId,
                    CycleId = Guid.NewGuid(),
                    State = CrashRecoveryStates.Pending,
                    Attempts = 0,
                    CrashDetectedUtc = clock.GetUtcNow(),
                    NextAttemptUtc = clock.GetUtcNow().AddMinutes(1)
                };
                crashRecovery.Add(state);
                data.SaveCrashRecoveryStates(crashRecovery);
                data.TryAudit($"crash-recovery-scheduled {run.ProfileId} cycle={state.CycleId} delay=1m {clock.GetUtcNow():O}");
                Activity("Recovery", "CrashDetected", "The exact managed process exited unexpectedly; crash recovery is scheduled in 1 minute.",
                    ActivitySeverity.Warning, run.ProfileId, visibility: ActivityVisibility.AssignedFriends);
                preserveRecoveryState = true;
                recoveryScheduled = true;
            }
            else if (existing is not null)
            {
                crashRecovery.Remove(existing);
            }
        }
        ArchiveCompletedRun(run, reason, preserveRecoveryState, recoveryScheduled);
    }

    private void FailCrashRecoveryAttempt(CrashRecoveryState recovery, string failure)
    {
        recovery.LastFailure = failure.Length > 500 ? failure[..500] : failure;
        recovery.RecoveredUtc = null;
        recovery.ReadinessDeadlineUtc = null;
        if (recovery.Attempts >= 3)
        {
            recovery.State = CrashRecoveryStates.Suspended;
            recovery.NextAttemptUtc = null;
            data.TryAudit($"crash-recovery-suspended {recovery.ProfileId} cycle={recovery.CycleId} attempts={recovery.Attempts} {clock.GetUtcNow():O}");
            Activity("Recovery", "CrashRecoverySuspended", "Crash recovery was suspended after three failed attempts.",
                ActivitySeverity.Warning, recovery.ProfileId, visibility: ActivityVisibility.AssignedFriends);
        }
        else
        {
            var delay = recovery.Attempts == 1 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);
            recovery.State = CrashRecoveryStates.Pending;
            recovery.NextAttemptUtc = clock.GetUtcNow().Add(delay);
            data.TryAudit($"crash-recovery-rescheduled {recovery.ProfileId} cycle={recovery.CycleId} attempts={recovery.Attempts} delay={(int)delay.TotalMinutes}m {clock.GetUtcNow():O}");
            Activity("Recovery", "CrashRecoveryRescheduled", $"Crash recovery failed and will retry in {(int)delay.TotalMinutes} minutes.",
                ActivitySeverity.Warning, recovery.ProfileId, visibility: ActivityVisibility.AssignedFriends);
        }
        data.SaveCrashRecoveryStates(crashRecovery);
    }

    private void ArchiveCompletedRun(ManagedRun run, string reason, bool preserveRecoveryState,
        bool recoveryScheduled = false)
    {
        var now = clock.GetUtcNow();
        var archive = data.LoadRunArchive();
        archive.Add(new ManagedRunArchive(run.ProfileId, run.OperationId, run.Kind, run.WorldId,
            run.ProcessId, run.StartTimeUtcTicks, run.WasReady, reason, now, recoveryScheduled));
        var cutoff = now.AddDays(-30);
        archive = archive.Where(item => item.ArchivedUtc >= cutoff)
            .OrderByDescending(item => item.ArchivedUtc).Take(500)
            .OrderBy(item => item.ArchivedUtc).ToList();
        data.SaveRunArchive(archive);
        runs.Remove(run);
        shutdownDeadlines.Remove(run.ProfileId);
        hostAddedTime.Remove(run.ProfileId);
        friendAddedMinutes.Remove(run.ProfileId);
        observations.Remove(run.ProfileId);
        if (!preserveRecoveryState)
            crashRecovery.RemoveAll(item => item.ProfileId == run.ProfileId);
        data.SaveRuns(runs);
        data.SaveCrashRecoveryStates(crashRecovery);
        data.TryAudit($"run-archived {run.ProfileId} operation={run.OperationId} reason={reason} recovery={recoveryScheduled} {now:O}");
    }

    private static bool PortOverlap(GamePort left, GamePort right) =>
        left.Port == right.Port && left.Protocol.Equals(right.Protocol, StringComparison.OrdinalIgnoreCase) &&
        (left.Family == "Any" || right.Family == "Any" || left.Family == right.Family);

    private static string Identity(ManagedRun run)
    {
        if (run.ProcessId is null) return "Unknown";
        try
        {
            using var process = Process.GetProcessById(run.ProcessId.Value);
            if (process.HasExited) return "Missing";
            if (run.StartTimeUtcTicks is null) return "Unknown";
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

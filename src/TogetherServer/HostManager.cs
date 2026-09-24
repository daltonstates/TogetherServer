using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace TogetherServer;

public sealed record RunView(Guid ProfileId, string State, string Detail, int? ProcessId,
    IReadOnlyList<GamePort>? DeclaredPorts = null, int? OnlinePlayers = null, int? MaxPlayers = null,
    DateTimeOffset? AutoShutdownAtUtc = null, string? AutoShutdownReason = null,
    bool HostAddedTime = false, IReadOnlyList<string>? PlayerNames = null,
    bool PlayerCountTrusted = true);
public sealed record HostSnapshot(HostSettings Settings, IReadOnlyList<RunView> Runs,
    string Evidence, string Mode, IReadOnlyDictionary<Guid, bool> PasswordConfigured, string ManagedWorldsRoot,
    IReadOnlyDictionary<Guid, CustomCertificationState>? CustomCertifications = null);
public sealed record PortConflictView(Guid ProfileId, string ProfileName, IReadOnlyList<GamePort> SharedPorts,
    bool CanReplace, string? BlockReason = null);
public sealed record ActionResult(bool Ok, string Code, string Message, HostSnapshot Snapshot,
    IReadOnlyList<PortConflictView>? PortConflicts = null);
public sealed record CountdownExtensionRequest(long Minutes);
public sealed record ServerObservation(Guid ProfileId, Guid OperationId, string State, string Detail,
    string Source, DateTimeOffset ObservedUtc, bool Ok, int? OnlinePlayers = null,
    int? MaxPlayers = null, IReadOnlyList<string>? PlayerNames = null, bool PlayerCountTrusted = false);

public sealed class HostManager
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LocalData data;
    private readonly GameServerRegistry games;
    private readonly TimeProvider clock;
    private HostSettings settings;
    private readonly List<ManagedRun> runs;
    private readonly Dictionary<Guid, DateTimeOffset> shutdownDeadlines = [];
    private readonly HashSet<Guid> hostAddedTime = [];
    private readonly Dictionary<Guid, ServerObservation> observations = [];
    private readonly SemaphoreSlim observationRefresh = new(1, 1);
    private readonly Dictionary<Guid, CustomCertificationSession> customCertificationSessions = [];

    public HostManager(LocalData data) : this(data, new GameServerRegistry(data), TimeProvider.System) { }

    public HostManager(LocalData data, GameServerRegistry games, TimeProvider? clock = null)
    {
        this.data = data;
        this.games = games;
        this.clock = clock ?? TimeProvider.System;
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

    public async Task RefreshObservationsAsync()
    {
        if (!await observationRefresh.WaitAsync(0)) return;
        try
        {
            List<(ManagedRun Run, IGameServerDriver Driver)> targets;
            await gate.WaitAsync();
            try
            {
                targets = [];
                foreach (var run in runs)
                    if (Identity(run) == "Matched" && games.TryGet(run.Kind, out var driver))
                        targets.Add((run, driver));
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
                var currentIds = runs.Select(run => run.ProfileId).ToHashSet();
                foreach (var removed in observations.Keys.Where(id => !currentIds.Contains(id)).ToList())
                    observations.Remove(removed);
                foreach (var result in checkedRuns)
                {
                    if (!runs.Any(run => run.ProfileId == result.ProfileId && run.OperationId == result.OperationId)) continue;
                    observations[result.ProfileId] = ToObservation(result.ProfileId, result.OperationId,
                        result.Health, clock.GetUtcNow());
                }
            }
            finally { gate.Release(); }
        }
        finally { observationRefresh.Release(); }
    }

    public async Task<ActionResult> UpdateSettingsAsync(HostSettings next)
    {
        await gate.WaitAsync();
        try
        {
            next.ConnectionRoute = ConnectionRoutes.Normalize(next.ConnectionRoute);
            if (settings.PublicGameIpCheckedUtc is { } recorded &&
                (next.PublicGameIpCheckedUtc is null || next.PublicGameIpCheckedUtc < recorded))
            {
                next.PublicGameIp = settings.PublicGameIp;
                next.PublicGameIpCheckedUtc = recorded;
            }
            var error = Validate(next);
            if (error is not null) return Result(false, "InvalidSettings", error);
            var profileIds = next.Profiles.Select(profile => profile.Id).ToHashSet();
            var serverInvites = data.LoadServerInvites()
                .Where(invite => profileIds.Contains(invite.ProfileId)).ToList();
            var devices = data.LoadDevices().Where(device =>
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
                var oldProfile = settings.Profiles.SingleOrDefault(p => p.Id == run.ProfileId);
                var newProfile = next.Profiles.SingleOrDefault(p => p.Id == run.ProfileId);
                if (oldProfile is null || newProfile is null || !SameProfile(oldProfile, newProfile))
                    return Result(false, "ProfileInUse", "Stop or resolve a managed run before changing its profile.");
            }
            var retiredCustomProfileIds = settings.Profiles
                .Where(profile => profile.Kind == GameKinds.Custom &&
                    !next.Profiles.Any(candidate => candidate.Id == profile.Id && candidate.Kind == GameKinds.Custom))
                .Select(profile => profile.Id)
                .ToList();
            var invalidatedCustomProfileIds = settings.Profiles
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
            var customScriptCleanupFailed = false;
            foreach (var profileId in retiredCustomProfileIds)
            {
                try
                {
                    data.DeleteCustomScripts(profileId);
                    data.DeleteCustomCertification(profileId);
                    data.Audit($"custom-scripts-deleted {profileId} {clock.GetUtcNow():O}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    customScriptCleanupFailed = true;
                    data.Audit($"custom-scripts-delete-failed {profileId} {ex.GetType().Name} {clock.GetUtcNow():O}");
                }
            }
            foreach (var profileId in invalidatedCustomProfileIds)
            {
                customCertificationSessions.Remove(profileId);
                shutdownDeadlines.Remove(profileId);
                hostAddedTime.Remove(profileId);
                try
                {
                    data.DeleteCustomCertification(profileId);
                    data.Audit($"custom-certification-invalidated {profileId} profile-changed {clock.GetUtcNow():O}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    customScriptCleanupFailed = true;
                    data.Audit($"custom-certification-delete-failed {profileId} {ex.GetType().Name} {clock.GetUtcNow():O}");
                }
            }
            if (settings.RemoteControlsEnabled != next.RemoteControlsEnabled)
                data.Audit($"remote-controls {(next.RemoteControlsEnabled ? "enabled" : "disabled")} {DateTimeOffset.UtcNow:O}");
            if (settings.AutoShutdownEnabled != next.AutoShutdownEnabled || settings.IdleMinutes != next.IdleMinutes)
                data.Audit($"auto-shutdown {(next.AutoShutdownEnabled ? "enabled" : "disabled")} idle-minutes={next.IdleMinutes} {clock.GetUtcNow():O}");
            if (settings.AutoShutdownEnabled != next.AutoShutdownEnabled || settings.IdleMinutes != next.IdleMinutes)
            {
                shutdownDeadlines.Clear();
                hostAddedTime.Clear();
            }
            settings = next;
            return Result(true, "SettingsSaved", customScriptCleanupFailed
                ? "Host settings saved, but one or more retired custom script records could not be removed."
                : "Host settings saved.");
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
            data.Audit($"custom-scripts-saved {profileId} {clock.GetUtcNow():O}");
            data.Audit($"custom-certification-invalidated {profileId} scripts-changed {clock.GetUtcNow():O}");
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
            var fingerprint = CustomCertification.Fingerprint(profile, scripts!, driver.Ports(profile));
            var started = StartUnderGate(profileId);
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
            data.Audit($"custom-certification-began {profileId} {clock.GetUtcNow():O}");
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
                        data.Audit($"custom-certification-completed {profileId} {clock.GetUtcNow():O}");
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
                data.Audit($"custom-certification-save-confirmed {profileId} {clock.GetUtcNow():O}");
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
            var started = StartUnderGate(profileId);
            if (!started.Ok)
                return FailCertification(profile, session, "CertificationRestartFailed",
                    "The server stopped safely but the same profile did not start again. Certification failed. " + started.Message);
            session.Stage = CustomCertification.AwaitingRestartReady;
            session.OnlinePlayers = null;
            observations.Remove(profileId);
            data.Audit($"custom-certification-restarted {profileId} {clock.GetUtcNow():O}");
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
            observations.Remove(profileId);
            data.Audit($"custom-certification-canceled {profileId} {clock.GetUtcNow():O}");
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
            observations.Remove(profileId);
            data.Audit($"custom-certification-revoked {profileId} {clock.GetUtcNow():O}");
            return CertificationResult(true, "CustomCertificationRevoked",
                "Owner-certified Custom control was revoked. Local Start and Stop remain available.", CertificationState(profile));
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> StartAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try { return StartUnderGate(profileId); }
        finally { gate.Release(); }
    }

    // This is an explicit Friend action after an ordinary Start reports the
    // conflict. It never runs automatically from the first Start request.
    public async Task<ActionResult> ReplaceEmptyPortConflictsAndStartAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var initial = StartUnderGate(profileId);
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
                        $"{conflict.ProfileName} is being kept alive with time added by the Host.", initial.PortConflicts);
                var operation = await StopUnderGateAsync(conflict.ProfileId, run =>
                    !HostAddedTimeIsActive(conflict.ProfileId) &&
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

            var started = StartUnderGate(profileId);
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

    private ActionResult StartUnderGate(Guid profileId)
    {
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
        try { return await StopUnderGateAsync(profileId, remoteStillSafe); }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> RestartAsync(Guid profileId, Func<ManagedRun, bool>? remoteStillSafe = null)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return Result(false, "UnknownProfile", "Choose a saved profile.");
            var stopped = await StopUnderGateAsync(profileId, remoteStillSafe);
            if (!stopped.Ok)
                return Result(false, stopped.Code, "Restart did not stop the server. " + stopped.Message,
                    stopped.PortConflicts);
            var started = StartUnderGate(profileId);
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
            if (!settings.AutoShutdownEnabled)
            {
                shutdownDeadlines.Clear();
                hostAddedTime.Clear();
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
                var result = await StopUnderGateAsync(profileId,
                    run => AutoShutdownStillSafe(run, deadline));
                data.Audit($"auto-stop {profileId} {result.Code} {clock.GetUtcNow():O}");
                results.Add(result);
            }
            return results;
        }
        finally { gate.Release(); }
    }

    public async Task<ActionResult> ExtendAutoShutdownAsync(Guid profileId, long minutes)
    {
        await gate.WaitAsync();
        try
        {
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
            data.Audit($"auto-shutdown-extended {profileId} minutes={minutes} {clock.GetUtcNow():O}");
            var profileName = settings.Profiles.SingleOrDefault(profile => profile.Id == profileId)?.Name ?? "Server";
            return Result(true, "CountdownExtended", $"Added {minutes} minute{(minutes == 1 ? "" : "s")} to {profileName}'s countdown.");
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
            var stopped = await driver.StopAsync(process, run);
            if (stopped.ExitCode != 0) return Result(false, stopped.Code, stopped.Message);
            runs.Remove(run);
            shutdownDeadlines.Remove(profileId);
            hostAddedTime.Remove(profileId);
            observations.Remove(profileId);
            data.SaveRuns(runs);
            return Result(true, stopped.Code, stopped.Message);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
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
            shutdownDeadlines.Remove(profileId);
            hostAddedTime.Remove(profileId);
            observations.Remove(profileId);
            data.SaveRuns(runs);
            return Result(true, "RecordCleared", "The unresolved record was cleared by the local owner.");
        }
        finally { gate.Release(); }
    }

    private HostSnapshot Snapshot()
    {
        var now = clock.GetUtcNow();
        var views = settings.Profiles.Select(profile =>
        {
            var run = runs.SingleOrDefault(r => r.ProfileId == profile.Id);
            if (run is null) return new RunView(profile.Id, "Offline", "No managed process", null);
            var identity = Identity(run);
            if (!games.TryGet(run.Kind, out var driver))
                return new RunView(profile.Id, "Unknown", "The game driver for this run is unavailable", run.ProcessId, run.DeclaredPorts);
            return identity switch
            {
                "Matched" => DriverView(profile.Id, run, ObservedHealth(run, driver)),
                "Missing" => new RunView(profile.Id, "Failed", "Recorded process exited; owner can clear the record", run.ProcessId, run.DeclaredPorts),
                _ => new RunView(profile.Id, "Unknown", "Process identity cannot be proven; start and stop are blocked", run.ProcessId, run.DeclaredPorts)
            };
        }).ToList();
        var currentProfiles = views.Select(view => view.ProfileId).ToHashSet();
        foreach (var profileId in shutdownDeadlines.Keys.Where(id => !currentProfiles.Contains(id)).ToList())
        {
            shutdownDeadlines.Remove(profileId);
            hostAddedTime.Remove(profileId);
        }
        for (var index = 0; index < views.Count; index++)
        {
            var view = views[index];
            if (view.State != "Ready")
            {
                shutdownDeadlines.Remove(view.ProfileId);
                hostAddedTime.Remove(view.ProfileId);
                continue;
            }
            if (!settings.AutoShutdownEnabled)
            {
                shutdownDeadlines.Remove(view.ProfileId);
                hostAddedTime.Remove(view.ProfileId);
                views[index] = view with { AutoShutdownReason = "Automatic shutdown is off." };
                continue;
            }
            if (!view.PlayerCountTrusted)
            {
                shutdownDeadlines.Remove(view.ProfileId);
                hostAddedTime.Remove(view.ProfileId);
                views[index] = view with { AutoShutdownReason = view.Detail.Contains("Custom", StringComparison.OrdinalIgnoreCase)
                    ? "Owner certification and a fresh valid Custom contract-v2 player count are required for automatic shutdown."
                    : "A fresh authoritative player count is required for automatic shutdown." };
                continue;
            }
            if (view.OnlinePlayers is null)
            {
                shutdownDeadlines.Remove(view.ProfileId);
                hostAddedTime.Remove(view.ProfileId);
                views[index] = view with { AutoShutdownReason = "Waiting for a reliable player count." };
                continue;
            }
            if (view.OnlinePlayers != 0)
            {
                shutdownDeadlines.Remove(view.ProfileId);
                hostAddedTime.Remove(view.ProfileId);
                views[index] = view with { AutoShutdownReason = "Waiting for the server to be empty." };
                continue;
            }
            if (!shutdownDeadlines.TryGetValue(view.ProfileId, out var deadline))
            {
                deadline = now.AddMinutes(settings.IdleMinutes);
                shutdownDeadlines[view.ProfileId] = deadline;
            }
            views[index] = view with
            {
                AutoShutdownAtUtc = deadline,
                HostAddedTime = HostAddedTimeIsActive(view.ProfileId)
            };
        }
        return new HostSnapshot(settings, views, "Recorded process identity and game-specific local readiness; Friend join and save unverified", "Host",
            settings.Profiles.Where(profile => profile.Kind == "Valheim")
                .ToDictionary(profile => profile.Id, profile => data.HasValheimPassword(profile.Id)),
            data.ManagedWorldsRoot,
            settings.Profiles.Where(profile => profile.Kind == GameKinds.Custom)
                .ToDictionary(profile => profile.Id, CertificationState));
    }

    private static RunView DriverView(Guid profileId, ManagedRun run, GameHealthResult health) =>
        new(profileId, health.State, health.Detail, run.ProcessId, run.DeclaredPorts,
            health.OnlinePlayers, health.MaxPlayers, PlayerNames: health.PlayerNames,
            PlayerCountTrusted: health.PlayerCountTrusted);

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
        if (HostAddedTimeIsActive(run.ProfileId))
        {
            reason = $"{settings.Profiles.SingleOrDefault(profile => profile.Id == run.ProfileId)?.Name ?? "The conflicting server"} is being kept alive with time added by the Host.";
            return false;
        }
        if (Identity(run) != "Matched" || !games.TryGet(run.Kind, out var driver))
        {
            reason = "The conflicting server's process or game driver cannot be verified.";
            return false;
        }
        var health = ObservedHealth(run, driver);
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

    private GameHealthResult ObservedHealth(ManagedRun run, IGameServerDriver driver)
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

        // The first view of a newly attached run performs one bounded probe so
        // startup and direct manager users have useful state before the shared
        // supervisor's first pass. Subsequent readers consume the cache.
        GameHealthResult health;
        try { health = driver.Health(run); }
        catch (Exception ex)
        {
            health = new(false, "HealthProbeFailed", "Unknown",
                "The server status probe failed: " + ex.GetType().Name + ".", PlayerCountTrusted: false);
        }
        observations[run.ProfileId] = ToObservation(run.ProfileId, run.OperationId, health, now);
        return health;
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
        observations.Remove(profile.Id);
        try { data.DeleteCustomCertification(profile.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            data.Audit($"custom-certification-delete-failed {profile.Id} {ex.GetType().Name} {clock.GetUtcNow():O}");
        }
        data.Audit($"custom-certification-failed {profile.Id} code={code} {clock.GetUtcNow():O}");
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

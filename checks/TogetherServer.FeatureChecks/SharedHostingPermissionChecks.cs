using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Protected synthetic files and a driver that refuses every process action.
// These checks never launch an app, game, listener, browser or native dialog.
internal static class SharedHostingPermissionChecks
{
    private static readonly SharedWorldGrants Hosting = new(Receive: true, EligibleHost: true);

    internal static async Task RunAsync(string root)
    {
        using (var fixture = new Fixture(root, "current-settings"))
        {
            Require(fixture.Pairing.SetSharedWorldGrants(fixture.DeviceId, fixture.ProfileId,
                new(RecoveryVoter: true)).Ok, "could not seed an independent advanced grant");
            var gate = Field<SemaphoreSlim>(fixture.Manager, "gate");
            await gate.WaitAsync();
            Task<ActionResult> edit;
            Task<PairingDecision> permission;
            var newer = fixture.Current();
            newer.IdleMinutes = 71;
            newer.Profiles[0].Name = "Newer target name";
            newer.Profiles[0].Backups.RetentionCount = 17;
            newer.Profiles[0].Backups.MinimumFreeSpaceMb = 3072;
            newer.Profiles[0].CrashRecovery.Enabled = true;
            newer.Profiles[1].Name = "Newer sibling name";
            try
            {
                edit = fixture.Manager.UpdateSettingsAsync(newer);
                permission = fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId,
                    fixture.ProfileId, Hosting with { RecoveryVoter = true });
                Require(!edit.IsCompleted && !permission.IsCompleted,
                    "save permissions did not wait on the lifecycle gate");
            }
            finally { gate.Release(); }
            Require((await edit).Ok && (await permission).Ok, "one-action save hosting did not accept current settings");
            var expected = Clone(newer);
            expected.Profiles[0].Backups.Enabled = true;
            expected.Profiles[0].SharedSavesEnabled = true;
            Require(Same(fixture.Current(), expected),
                "one-action hosting replaced newer settings, retention, reserve, recovery, sibling or listener policy");
            Require(fixture.Grants() == (Hosting with { RecoveryVoter = true }) &&
                !fixture.Current().CompanionListeningEnabled && !fixture.Current().RemoteControlsEnabled &&
                fixture.Data.LoadRuns().Count == 0 && fixture.Driver.Actions == 0,
                "one-action hosting granted unrelated rights, enabled a listener or invoked a game action");
            var roster = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
            Require(roster is not null && roster.Members.Single(member => member.DeviceId == fixture.DeviceId).Grants ==
                (Hosting with { RecoveryVoter = true }) && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId) &&
                (await fixture.Manager.SharedWorldStatusAsync(fixture.ProfileId)).Latest is null,
                "the hosting permission was not signed or created an unexpected playable save");
        }

        using (var fixture = new Fixture(root, "withdraw-while-off"))
        {
            Require((await fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId, fixture.ProfileId,
                Hosting with { Reshare = true })).Ok, "could not prepare hosting for withdrawal");
            var paused = fixture.Current(); paused.Profiles[0].SharedSavesEnabled = false;
            Require((await fixture.Manager.UpdateSettingsAsync(paused)).Ok, "could not pause synthetic sharing");
            var before = fixture.Current();
            var priorRoster = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId) ??
                throw new InvalidOperationException("the prepared signed roster is missing");
            var withdrawn = await fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId,
                fixture.ProfileId, Hosting with { EligibleHost = false, Reshare = true });
            var afterRoster = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
            Require(withdrawn.Ok && Same(fixture.Current(), before) &&
                fixture.Grants() == (Hosting with { EligibleHost = false, Reshare = true }) &&
                afterRoster is not null && afterRoster.Members.Single(member => member.DeviceId == fixture.DeviceId).Grants == fixture.Grants() &&
                afterRoster.GroupId == priorRoster.GroupId && afterRoster.OwnerPublicKey == priorRoster.OwnerPublicKey &&
                afterRoster.Revision > priorRoster.Revision && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId),
                "hosting withdrawal resumed sharing or removed receive/forwarding rights");
            var receiveOff = await fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId,
                fixture.ProfileId, new(Reshare: true));
            Require(receiveOff.Ok && Same(fixture.Current(), before) && fixture.Grants() == new SharedWorldGrants(Reshare: true),
                "receive withdrawal enabled sharing or changed unrelated permissions");
            var receiveRoster = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
            Require(receiveRoster is not null && receiveRoster.Members.Single(member => member.DeviceId == fixture.DeviceId).Grants == fixture.Grants() &&
                receiveRoster.GroupId == priorRoster.GroupId && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId) &&
                (await fixture.Manager.SharedWorldStatusAsync(fixture.ProfileId)).Latest is null &&
                !fixture.Current().CompanionListeningEnabled && fixture.Data.LoadRuns().Count == 0,
                "paused receive withdrawal did not update signed permissions or published/started a world");
        }

        using (var fixture = new Fixture(root, "receive-only"))
        {
            var before = fixture.Current();
            var result = await fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId,
                fixture.ProfileId, new(Receive: true));
            Require(result.Ok && fixture.Grants() == new SharedWorldGrants(Receive: true) && Same(fixture.Current(), before),
                "a receive-only permission silently enabled hosting or changed sharing policy");
        }

        using (var pending = new Fixture(root, "pending-signing", configureDevice: device => device.SharedWorldPublicKey = null))
        {
            Require((await pending.Manager.SetSharedWorldGrantsAsync(pending.DeviceId, pending.ProfileId, Hosting)).Ok,
                "a pending signing identity lost the owner's chosen hosting permission");
            var roster = await pending.Manager.SharedWorldRosterAsync(pending.ProfileId);
            Require(pending.Grants() == Hosting && roster is not null && roster.Members.All(member => member.DeviceId != pending.DeviceId) &&
                !pending.Pairing.SharedRosterDirty(pending.ProfileId),
                "a pending identity was represented as an enrolled signed member");
        }

        using (var changed = new Fixture(root, "source-changed-while-paused"))
        {
            Require((await changed.Manager.SetSharedWorldGrantsAsync(changed.DeviceId, changed.ProfileId, Hosting)).Ok,
                "could not seed source identity review");
            var prior = await changed.Manager.SharedWorldRosterAsync(changed.ProfileId) ??
                throw new InvalidOperationException("the source identity was not signed");
            var originalProfile = Clone(changed.Current()).Profiles[0];
            var next = changed.Current();
            next.Profiles[0].WorldId = "DifferentSyntheticWorld";
            Require((await changed.Manager.UpdateSettingsAsync(next)).Ok && !changed.Current().Profiles[0].SharedSavesEnabled,
                "the synthetic owner source change was not fenced from sharing");
            var before = changed.Current();
            var result = await changed.Manager.SetSharedWorldGrantsAsync(changed.DeviceId, changed.ProfileId,
                Hosting with { EligibleHost = false });
            Require(!result.Ok && result.Code == "SharedHostingPermissionUnconfirmed" &&
                Same(changed.Current(), before) && changed.Grants() == Hosting,
                "a paused permission edit reviewed a different save source or rewrote its group");
            var old = new SharedWorldService(changed.Data, new WorldBackupService(changed.Data, TimeProvider.System))
                .ReadRoster(originalProfile);
            Require(old is not null && old.GroupId == prior.GroupId && old.Signature == prior.Signature && old.Revision == prior.Revision,
                "a paused permission edit replaced the earlier signed roster or rollback clock");
        }

        using (var unknown = new Fixture(root, "unknown-device"))
            await RejectsAsync(unknown, "Unauthorized", deviceId: Guid.NewGuid());
        using (var unknown = new Fixture(root, "unknown-profile"))
            await RejectsAsync(unknown, "UnknownProfile", profileId: Guid.NewGuid());
        using (var pending = new Fixture(root, "pending-device", configureDevice: device => device.ApprovalPending = true))
            await RejectsAsync(pending, "ApprovalPending");
        using (var revoked = new Fixture(root, "revoked-device", configureDevice: device => device.Revoked = true))
            await RejectsAsync(revoked, "Revoked");
        using (var unassigned = new Fixture(root, "unassigned-device", configureDevice: device => device.AssignedProfileIds = []))
            await RejectsAsync(unassigned, "PermissionDenied");
        using (var expired = new Fixture(root, "expired-access"))
        {
            Require(expired.Pairing.SetAccessExpiry(expired.DeviceId, new(Duration: "OneHour")).Ok,
                "could not seed an owner access deadline");
            expired.Clock.Now = expired.Clock.Now.AddHours(1);
            await RejectsAsync(expired, "AccessExpired");
        }
        using (var trial = new Fixture(root, "rehearsal", profile => profile.WorldLoadRehearsalId = Guid.NewGuid()))
            await RejectsAsync(trial, "WorldLoadCannotShare");
        using (var branch = new Fixture(root, "separate-history", profile =>
        {
            profile.SeparateCopySourceProfileId = Guid.NewGuid();
            profile.SeparateCopyBranchHash = new string('A', 64);
        })) await RejectsAsync(branch, "SeparateCopyCannotShare");
        using (var custom = new Fixture(root, "unsupported", kind: GameKinds.Custom))
            await RejectsAsync(custom, "SharingUnsupported");
        using (var authority = new Fixture(root, "unverified-authority"))
        {
            authority.Data.SaveProtected($"authority-floor-{authority.ProfileId:N}.protected", [1, 2, 3]);
            await RejectsAsync(authority, "SuccessorRosterReadOnly");
        }
        using (var handoff = new Fixture(root, "pending-move"))
        {
            handoff.Data.SaveProtected($"planned-handoff-{handoff.ProfileId:N}.protected", [1, 2, 3]);
            await RejectsAsync(handoff, "PlannedHandoffPending");
        }
        using (var restore = new Fixture(root, "pending-restore"))
        {
            restore.Data.SaveProtected($"setup-restore-pending-{restore.ProfileId:N}.protected", [1, 2, 3]);
            await RejectsAsync(restore, "SetupRestoreRecoveryRequired");
        }
        using (var recovery = new Fixture(root, "data-recovery"))
        {
            recovery.Data.SaveProtected("synthetic-recovery.protected", [1, 2, 3]);
            recovery.Data.QuarantineState("synthetic-recovery.protected", "Synthetic lifecycle recovery", true);
            await RejectsAsync(recovery, "DataRecoveryRequired");
        }

        // Windows file sharing denies the exact synthetic destination replacement
        // while permitting reads. No ACL, real file or desktop state is changed.
        using (var failed = new Fixture(root, "settings-write-failure"))
        {
            var before = failed.Current();
            PairingDecision result;
            using (var hold = new FileStream(Path.Combine(failed.Data.RootPath, "host.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
                result = await failed.Manager.SetSharedWorldGrantsAsync(failed.DeviceId, failed.ProfileId, Hosting);
            Require(!result.Ok && result.Code == "SharedHostingPermissionUnconfirmed" &&
                Same(failed.Current(), before) && failed.Grants() == new SharedWorldGrants(),
                "a failed prerequisite save changed settings or granted hosting");
        }
        using (var failed = new Fixture(root, "pairing-write-failure"))
        {
            PairingDecision result;
            using (var hold = new FileStream(Path.Combine(failed.Data.RootPath, "pairing-state.protected"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
                result = await failed.Manager.SetSharedWorldGrantsAsync(failed.DeviceId, failed.ProfileId, Hosting);
            Require(!result.Ok && result.Code == "SharedHostingPermissionUnconfirmed" &&
                failed.Current().Profiles[0].Backups.Enabled && failed.Current().Profiles[0].SharedSavesEnabled &&
                failed.Grants() == new SharedWorldGrants() &&
                !failed.Pairing.TryGetActiveDevice(failed.DeviceId, out _) && failed.Driver.Actions == 0,
                "a failed grant persistence leaked new hosting access or concealed partial prerequisite preparation");
        }
    }

    private static async Task RejectsAsync(Fixture fixture, string code, Guid? deviceId = null, Guid? profileId = null)
    {
        var before = fixture.Current();
        var grants = fixture.Grants();
        var result = await fixture.Manager.SetSharedWorldGrantsAsync(deviceId ?? fixture.DeviceId,
            profileId ?? fixture.ProfileId, Hosting);
        Require(!result.Ok && result.Code == code && Same(fixture.Current(), before) && fixture.Grants() == grants &&
            fixture.Driver.Actions == 0, "rejected hosting permission saved prerequisites or changed access: " + code);
    }

    private sealed class Fixture : IDisposable
    {
        internal LocalData Data { get; }
        internal HostManager Manager { get; }
        internal PairingService Pairing { get; }
        internal PolicyOnlyDriver Driver { get; }
        internal TestClock Clock { get; } = new();
        internal Guid DeviceId { get; }
        internal Guid ProfileId { get; }

        internal Fixture(string root, string label, Action<ServerProfile>? configureProfile = null,
            Action<PairedDevice>? configureDevice = null, string kind = GameKinds.Fixture)
        {
            Data = new(Path.Combine(root, "save-hosting-" + label + "-" + Guid.NewGuid().ToString("N")));
            var target = Profile("Synthetic target", 2456, kind);
            configureProfile?.Invoke(target); ProfileId = target.Id;
            Data.SaveSettings(new HostSettings { MaxConcurrentServers = 3,
                Profiles = [target, Profile("Synthetic sibling", 2458, kind)] });
            var issuer = new PairingService(Data, Clock);
            var code = issuer.IssueServer(ProfileId, false, false, "https://127.0.0.1:5131", new string('A', 64), refresh: false);
            var access = issuer.Activate(new(ProfileId, code.Code, ServerScope: true)) ??
                throw new InvalidOperationException("synthetic save-hosting pairing failed");
            DeviceId = access.DeviceId;
            var state = Data.LoadPairingState();
            var device = state.Devices.Single(device => device.Id == DeviceId);
            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                device.SharedWorldPublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            configureDevice?.Invoke(device);
            Data.SavePairingState(state);
            Pairing = new PairingService(Data, Clock);
            Driver = new(kind);
            Manager = new(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly), Clock,
                new NullHostingPowerGuard(), pairing: Pairing);
        }
        private ServerProfile Profile(string name, int port, string kind) => new()
        {
            Name = name, ServerName = name, Kind = kind, WorldId = "SyntheticPermission", WorldSource = "New", GamePort = port,
            WorldDirectory = Path.Combine(Data.RootPath, "never-open-world-" + Guid.NewGuid().ToString("N")),
            ExecutablePath = Path.Combine(Data.RootPath, "never-start-server.exe")
        };
        internal HostSettings Current() => Data.LoadSettings();
        internal SharedWorldGrants Grants() => Data.LoadPairingState().Devices.Single(device => device.Id == DeviceId)
            .SharedWorldGrants?.GetValueOrDefault(ProfileId) ?? new();
        public void Dispose() => Data.Dispose();
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PolicyOnlyDriver(string kind) : IGameServerDriver
    {
        internal int Actions { get; private set; }
        public string Kind => kind;
        public string DisplayName => "Synthetic save-permission adapter";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => true;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => throw NoProcess();
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
        private Exception NoProcess() { Actions++; return new InvalidOperationException("No game/process action is permitted in save-permission checks."); }
    }

    private static T Field<T>(HostManager manager, string name) => (T)typeof(HostManager)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
    private static HostSettings Clone(HostSettings value) => JsonSerializer.Deserialize<HostSettings>(JsonSerializer.SerializeToUtf8Bytes(value))!;
    private static bool Same(HostSettings left, HostSettings right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

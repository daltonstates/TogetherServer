using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Source-only policy and fixed-request checks. The fake registry refuses every
// process action; these checks use no app, server, socket, browser or dialog.
internal static class ProtectionSettingsChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        using (var fixture = new Fixture(root, "atomic-current"))
        {
            var baseline = ProtectionPolicy.Read(fixture.Current().Profiles[0]);
            var later = fixture.Current();
            later.IdleMinutes = 71;
            later.FriendTimerExtensionMaximumMinutes = 91;
            later.Profiles[0].Name = "Renamed after the protection editor opened";
            later.Profiles[0].ServerName = "Newer server display name";
            later.Profiles[1].Name = "Newer sibling";
            later.Profiles[1].Backups.RetentionCount = 17;
            later.Profiles.Add(fixture.Profile("Added after baseline", 2460));
            Require((await fixture.Manager.UpdateSettingsAsync(later)).Ok,
                "the synthetic newer settings were rejected");
            var before = fixture.Current();
            var desired = baseline with { Backups = new(true, 12, 0), CrashRecovery = new(true) };
            var saved = await fixture.Manager.UpdateProtectionSettingsAsync(fixture.ProfileId, new(baseline, desired));
            Require(saved.Ok && ProtectionPolicy.Read(saved.Snapshot.Settings.Profiles[0]) == desired,
                "the exact protection patch did not save");
            var after = fixture.Current();
            var expected = Clone(before);
            expected.Profiles[0].Backups = new() { Enabled = true, RetentionCount = 12, MinimumFreeSpaceMb = 0 };
            expected.Profiles[0].CrashRecovery = new() { Enabled = true };
            Require(Same(after, expected) && Same(saved.Snapshot.Settings, after) &&
                after.Profiles.Count == 3 && !after.CompanionListeningEnabled,
                "protection save replaced newer global settings, sibling profiles or unrelated target fields");

            var stale = await fixture.Manager.UpdateProtectionSettingsAsync(fixture.ProfileId,
                new(baseline, baseline with { Backups = new(false, 2, 4096) }));
            Require(!stale.Ok && stale.Code == "ProtectionPolicyChanged" &&
                Same(stale.Snapshot.Settings, after) && Same(fixture.Current(), after),
                "a stale protection editor overwrote current policy or lost the fresh conflict snapshot");

            // Both callers observed the same policy before entering the actual
            // lifecycle gate. Exactly one may replace that policy.
            var gate = Gate(fixture.Manager);
            await gate.WaitAsync();
            Task<ActionResult> first;
            Task<ActionResult> second;
            try
            {
                first = fixture.Manager.UpdateProtectionSettingsAsync(fixture.ProfileId,
                    new(desired, desired with { Backups = new(true, 13, 0) }));
                second = fixture.Manager.UpdateProtectionSettingsAsync(fixture.ProfileId,
                    new(desired, desired with { Backups = new(true, 14, 0) }));
                Require(!first.IsCompleted && !second.IsCompleted, "policy writes did not wait on the real lifecycle gate");
            }
            finally { gate.Release(); }
            var completed = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Require(completed.Count(result => result.Ok) == 1 &&
                completed.Count(result => !result.Ok && result.Code == "ProtectionPolicyChanged") == 1,
                "concurrent protection writes both accepted the same stale baseline");
        }

        using (var invalid = new Fixture(root, "bounds"))
        {
            var expected = ProtectionPolicy.Read(invalid.Current().Profiles[0]);
            var before = invalid.Current();
            foreach (var policy in new[] { expected with { Backups = new(true, 0, 0) },
                expected with { Backups = new(true, 51, 0) }, expected with { Backups = new(true, 1, -1) },
                expected with { Backups = new(true, 1, 1_048_577) }, expected with { CrashRecovery = null! },
                expected with { Backups = null! } })
            {
                var result = await invalid.Manager.UpdateProtectionSettingsAsync(invalid.ProfileId, new(expected, policy));
                Require(!result.Ok && result.Code == "InvalidProtectionPolicy" && Same(invalid.Current(), before),
                    "invalid or missing policy values changed current settings");
            }
            var missing = await invalid.Manager.UpdateProtectionSettingsAsync(Guid.NewGuid(), new(expected, expected));
            Require(!missing.Ok && missing.Code == "UnknownProfile" && Same(invalid.Current(), before),
                "a missing profile changed another profile's protection");

            var sharing = invalid.Current(); sharing.Profiles[0].SharedSavesEnabled = true;
            sharing.Profiles[0].Backups.Enabled = true;
            Require((await invalid.Manager.UpdateSettingsAsync(sharing)).Ok, "the synthetic shared-save prerequisite was rejected");
            var sharedPolicy = ProtectionPolicy.Read(invalid.Current().Profiles[0]);
            var denied = await invalid.Manager.UpdateProtectionSettingsAsync(invalid.ProfileId,
                new(sharedPolicy, sharedPolicy with { Backups = sharedPolicy.Backups with { Enabled = false } }));
            Require(!denied.Ok && denied.Code == "InvalidSettings" && invalid.Current().Profiles[0].Backups.Enabled &&
                invalid.Current().Profiles[0].SharedSavesEnabled,
                "the protection mutation bypassed the existing rolling-backup requirement for shared saves");
        }

        using (var trial = new Fixture(root, "trial", profile => profile.WorldLoadRehearsalId = Guid.NewGuid()))
            await RejectsAsync(trial, "WorldLoadSetupReadOnly", policy => policy with { Backups = new(true, 9, 0) });
        using (var branch = new Fixture(root, "branch", profile =>
        {
            profile.SeparateCopySourceProfileId = Guid.NewGuid();
            profile.SeparateCopyBranchHash = new string('A', 64);
        }))
        {
            await RejectsAsync(branch, "SeparateCopyManualStartRequired", policy => policy with { CrashRecovery = new(true) });
            var old = ProtectionPolicy.Read(branch.Current().Profiles[0]);
            var saved = await branch.Manager.UpdateProtectionSettingsAsync(branch.ProfileId,
                new(old, old with { Backups = new(true, 9, 0) }));
            Require(saved.Ok && !branch.Current().Profiles[0].CrashRecovery.Enabled &&
                branch.Current().Profiles[0].SeparateCopyBranchHash == new string('A', 64),
                "a branch backup-only change lost the manual hosting fence or its source identity");
        }
        using (var preview = new Fixture(root, "preview", kind: GameKinds.Factorio, crashRecovery: false))
            await RejectsAsync(preview, "CrashRecoveryUnsupported", policy => policy with { CrashRecovery = new(true) });
        using (var custom = new Fixture(root, "custom", kind: GameKinds.Custom, backups: false))
            await RejectsAsync(custom, "ProtectionUnsupported", policy => policy with { Backups = new(true, 9, 0) });
        using (var restore = new Fixture(root, "restore-pending"))
        {
            restore.Data.SaveProtected($"setup-restore-pending-{restore.ProfileId:N}.protected", Encoding.UTF8.GetBytes("{}"));
            await RejectsAsync(restore, "SetupRestoreRecoveryRequired", policy => policy with { Backups = new(true, 9, 0) });
        }
        using (var recovery = new Fixture(root, "data-recovery"))
        {
            recovery.Data.QuarantineState("host.json", "Synthetic lifecycle recovery fence", true);
            await RejectsAsync(recovery, "DataRecoveryRequired", policy => policy with { Backups = new(true, 9, 0) }, durable: false);
        }
        using (var uncertain = new Fixture(root, "unknown-run"))
        {
            var runs = Field<List<ManagedRun>>(uncertain.Manager, "runs");
            runs.Add(new() { ProfileId = uncertain.ProfileId, Kind = GameKinds.Fixture,
                WorldDirectory = uncertain.Current().Profiles[0].WorldDirectory, ProcessId = null });
            await RejectsAsync(uncertain, "ProfileInUse", policy => policy with { Backups = new(true, 9, 0) });
            Require(runs.Count == 1, "a protection edit cleared an uncertain managed run record");
        }
        using (var copy = new Fixture(root, "world-copy"))
        {
            Field<HashSet<Guid>>(copy.Manager, "worldCopyReservations").Add(copy.ProfileId);
            await RejectsAsync(copy, "WorldCopyInProgress", policy => policy with { Backups = new(true, 9, 0) });
        }
        using (var route = new Fixture(root, "route")) await FixedRoutesAsync(route);
    }

    private static async Task FixedRoutesAsync(Fixture fixture)
    {
        var expected = ProtectionPolicy.Read(fixture.Current().Profiles[0]);
        var request = new ProtectionSettingsRequest(expected, expected with { Backups = new(true, 11, 0) });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        Require(ProtectionSettingsLocalRoutes.Decode(bytes) == request, "fixed policy request did not round-trip");
        var raw = Encoding.UTF8.GetString(bytes);
        foreach (var bad in new[] { "{}", "[]", raw.Replace("\"expected\":", "\"unknown\":"),
            raw.Replace("\"next\":", "\"expected\":"), raw.Replace("\"retentionCount\":5", "\"retentionCount\":5,\"path\":\"arbitrary\""),
            raw.Replace("\"enabled\":false", "\"enabled\":false,\"enabled\":true"),
            raw.Replace("\"retentionCount\":5", "\"retentionCount\":5.5"), new string(' ', ProtectionSettingsLocalRoutes.MaximumRequestBytes + 1) })
            RejectDecode(Encoding.UTF8.GetBytes(bad));
        var before = fixture.Current();
        using var mode = new SemaphoreSlim(1, 1);
        foreach (Action<DefaultHttpContext> alter in new Action<DefaultHttpContext>[]
        {
            context => context.Request.Headers.Remove("X-TogetherServer-Local"),
            context => context.Request.Headers["X-TogetherServer-Local"] = new Microsoft.Extensions.Primitives.StringValues(["1", "1"]),
            context => context.Request.QueryString = new("?path=arbitrary"),
            context => context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.45"),
            context => context.Request.Host = new("localhost", 5139),
            context => context.Request.Host = new("127.0.0.1", 5140)
        })
        {
            var context = Context(bytes); alter(context);
            var denied = await ProtectionSettingsLocalRoutes.HandleAsync(context, fixture.ProfileId, fixture.Manager, mode, () => true);
            Require(Status(denied) == 403 && Same(fixture.Current(), before),
                "the fixed owner header/origin scope accepted an unrelated request");
        }
        var oversized = await ProtectionSettingsLocalRoutes.HandleAsync(Context(new byte[ProtectionSettingsLocalRoutes.MaximumRequestBytes + 1]),
            fixture.ProfileId, fixture.Manager, mode, () => true);
        Require(Status(oversized) == 400 && Same(fixture.Current(), before), "an oversized protection body changed settings");
        var allow = true;
        await mode.WaitAsync();
        Task<IResult> pending;
        try
        {
            pending = ProtectionSettingsLocalRoutes.HandleAsync(Context(bytes), fixture.ProfileId, fixture.Manager, mode, () => allow);
            Require(!pending.IsCompleted, "the protection route bypassed the app mode gate");
            allow = false;
        }
        finally { mode.Release(); }
        Require(Status(await pending.WaitAsync(TimeSpan.FromSeconds(5))) == 409 && Same(fixture.Current(), before),
            "a Host/mode/update/shutdown denial was not rechecked after waiting for its gate");
        var accepted = await ProtectionSettingsLocalRoutes.HandleAsync(Context(bytes), fixture.ProfileId, fixture.Manager, mode, () => true);
        Require(accepted is IValueHttpResult { Value: ActionResult { Ok: true } result } &&
            ProtectionPolicy.Read(result.Snapshot.Settings.Profiles[0]) == request.Next && !fixture.Current().CompanionListeningEnabled,
            "the fixed protection route failed to return the normal action snapshot or enabled a listener");
    }

    private static async Task RejectsAsync(Fixture fixture, string code, Func<ProtectionPolicy, ProtectionPolicy> change, bool durable = true)
    {
        var before = durable ? fixture.Current() : (await fixture.Manager.SnapshotAsync()).Settings;
        var expected = ProtectionPolicy.Read(before.Profiles[0]);
        var result = await fixture.Manager.UpdateProtectionSettingsAsync(fixture.ProfileId, new(expected, change(expected)));
        Require(!result.Ok && result.Code == code && Same(result.Snapshot.Settings, before) &&
            (!durable || Same(fixture.Current(), before)), "protection guard " + code + " changed current settings or was skipped");
    }

    private sealed class Fixture : IDisposable
    {
        internal LocalData Data { get; }
        internal HostManager Manager { get; }
        internal Guid ProfileId { get; }
        private readonly string kind;
        internal Fixture(string parent, string label, Action<ServerProfile>? configure = null,
            string kind = GameKinds.Fixture, bool crashRecovery = true, bool backups = true)
        {
            Data = new(Path.Combine(parent, "protection-" + label + "-" + Guid.NewGuid().ToString("N")));
            this.kind = kind;
            var target = Profile("Synthetic target", 2456);
            configure?.Invoke(target); ProfileId = target.Id;
            Data.SaveSettings(new HostSettings { MaxConcurrentServers = 3, Profiles = [target, Profile("Synthetic sibling", 2458)] });
            Manager = new(Data, new GameServerRegistry([new PolicyOnlyDriver(kind, crashRecovery, backups)], PortProbeMode.ObserveOnly));
        }
        internal ServerProfile Profile(string name, int port) => new() { Id = Guid.NewGuid(), Kind = kind,
            Name = name, ServerName = name, WorldId = "SyntheticPolicy", WorldSource = "New", GamePort = port,
            WorldDirectory = Path.Combine(Data.RootPath, "never-open-world-" + Guid.NewGuid().ToString("N")),
            ExecutablePath = Path.Combine(Data.RootPath, "never-start-server.exe") };
        internal HostSettings Current() => Data.LoadSettings();
        public void Dispose() => Data.Dispose();
    }

    private sealed class PolicyOnlyDriver(string kind, bool recovery, bool backups) : IGameServerDriver
    {
        public string Kind => kind;
        public string DisplayName => "Synthetic policy-only adapter";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => recovery;
        public bool SupportsBackups => backups;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => throw NoProcess();
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
        private static Exception NoProcess() => new InvalidOperationException("No game/process action is permitted in protection checks.");
    }

    private static DefaultHttpContext Context(byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Connection.LocalPort = 5139;
        context.Request.Host = new("127.0.0.1", 5139); context.Request.Method = HttpMethods.Put;
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        context.Request.ContentType = "application/json"; context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body, writable: false);
        return context;
    }
    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
    private static T Field<T>(HostManager manager, string name) => (T)typeof(HostManager)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
    private static SemaphoreSlim Gate(HostManager manager) => Field<SemaphoreSlim>(manager, "gate");
    private static HostSettings Clone(HostSettings value) => JsonSerializer.Deserialize<HostSettings>(JsonSerializer.SerializeToUtf8Bytes(value))!;
    private static bool Same(HostSettings left, HostSettings right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static void RejectDecode(byte[] bytes)
    {
        try { ProtectionSettingsLocalRoutes.Decode(bytes); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { return; }
        throw new InvalidOperationException("An unknown, duplicate, missing, malformed or oversized protection request was accepted.");
    }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

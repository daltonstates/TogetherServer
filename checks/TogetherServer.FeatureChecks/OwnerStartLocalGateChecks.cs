using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// In-memory requests, generated files and a driver which stops at validation.
// No app, listener, native window or game process is opened.
internal static class OwnerStartLocalGateChecks
{
    internal static async Task RunAsync(string root)
    {
        foreach (var change in new[] { "None", "FriendMode", "Update", "Shutdown" })
            await UnrelatedControlAndCurrentPermitAsync(root, change);
        await InitialValidationAsync();
        await RequestCancellationAsync();
    }

    private static async Task UnrelatedControlAndCurrentPermitAsync(string root, string change)
    {
        using var data = new LocalData(Path.Combine(root, "owner-start-mode-gate-" + change));
        var profile = new ServerProfile
        {
            Id = Guid.NewGuid(),
            Kind = GameKinds.Fixture,
            Name = "Synthetic owner",
            WorldId = "Synthetic owner",
            WorldSource = "New",
            ExecutablePath = Path.Combine(data.RootPath, "never-executed.bin"),
            WorldDirectory = Path.Combine(data.RootPath, "synthetic-world")
        };
        var other = new ServerProfile
        {
            Id = Guid.NewGuid(),
            Kind = GameKinds.Fixture,
            Name = "Unrelated synthetic server",
            WorldId = "Other",
            WorldSource = "New",
            ExecutablePath = profile.ExecutablePath,
            WorldDirectory = Path.Combine(data.RootPath, "unrelated-synthetic-world")
        };
        File.WriteAllText(profile.ExecutablePath, "generated validation-only input");
        data.SaveSettings(new HostSettings { Profiles = [profile, other] });
        var driver = new ValidationOnlyDriver();
        var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
        using var modeGate = new SemaphoreSlim(1, 1);
        var friendMode = false;
        var updating = false;
        var shuttingDown = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validated = false;
        var pending = OwnerStartLocalRoutes.StartAsync(modeGate, default,
            () =>
            {
                Require(modeGate.CurrentCount == 0, "Owner role validation ran outside the short mode gate.");
                return null;
            },
            () => !friendMode && !updating && !shuttingDown,
            async permit =>
            {
                Require(validated, "Slow preparation began before exact request validation.");
                entered.TrySetResult();
                await release.Task;
                return await manager.StartAsync(profile.Id, default, permit);
            },
            () =>
            {
                Require(modeGate.CurrentCount == 0, "Exact owner Start request validation escaped its mode gate.");
                validated = true;
                return Task.FromResult<IResult?>(null);
            });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!pending.IsCompleted && driver.Validations == 0,
                "The generated save preparation did not remain pending before launch validation.");
            // This is the same gate and real manager Stop used by the local
            // route, for a different saved server with no process to stop.
            Require(await modeGate.WaitAsync(TimeSpan.FromSeconds(2)),
                "Owner save preparation held the mode gate and blocked an unrelated Stop/control.");
            try
            {
                await manager.StopAsync(other.Id);
                friendMode = change == "FriendMode";
                updating = change == "Update";
                shuttingDown = change == "Shutdown";
            }
            finally { modeGate.Release(); }
            Require(!pending.IsCompleted, "An unrelated Stop released the blocked preparation.");
            release.TrySetResult();
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var result = (response as IValueHttpResult)?.Value as ActionResult;
            Require(result is not null && result.Code == (change == "None" ? "OwnerGateValidationReached" : "RemoteStartAuthorizationChanged") &&
                driver.Validations == (change == "None" ? 1 : 0) && driver.Launches == 0 && data.LoadRuns().Count == 0,
                "Owner Start lost the live mode/update/shutdown permit or invented a game run after preparation.");
        }
        finally
        {
            release.TrySetResult();
            await pending;
        }
    }

    private static async Task InitialValidationAsync()
    {
        using var modeGate = new SemaphoreSlim(1, 1);
        var calls = 0;
        Task<ActionResult> Forbidden(Func<bool> permit)
        { calls++; throw new InvalidOperationException("Rejected owner Start reached preparation."); }
        var role = await OwnerStartLocalRoutes.StartAsync(modeGate, default,
            () => Results.Conflict(new { code = "FriendMode" }), () => false, Forbidden);
        var request = await OwnerStartLocalRoutes.StartAsync(modeGate, default,
            () => null, () => true, Forbidden,
            () => Task.FromResult<IResult?>(Results.BadRequest(new { code = "SplitWarningRequired" })));
        Require((role as IStatusCodeHttpResult)?.StatusCode == 409 &&
            (request as IStatusCodeHttpResult)?.StatusCode == 400 && calls == 0 && modeGate.CurrentCount == 1,
            "Initial owner role/exact-request refusal started preparation or leaked the mode gate.");
    }

    private static async Task RequestCancellationAsync()
    {
        using var modeGate = new SemaphoreSlim(1, 1);
        using var canceled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = OwnerStartLocalRoutes.StartAsync(modeGate, canceled.Token,
            () => null, () => true, async _ =>
            {
                entered.TrySetResult();
                return await release.Task.WaitAsync(canceled.Token);
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Owner Start discarded its explicit request cancellation.");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        Require(modeGate.CurrentCount == 1, "Canceled owner preparation retained the mode gate.");
    }

    private sealed class ValidationOnlyDriver : IGameServerDriver
    {
        internal int Validations;
        internal int Launches;
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Validation-only owner Start";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => false;
        public string? ManagedSaveDirectory(ServerProfile profile) => null;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile)
        { Validations++; return new("OwnerGateValidationReached", "Synthetic validation stops before process creation."); }
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) { Launches++; throw NoProcess(); }
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
        private static InvalidOperationException NoProcess() => new("A process operation is forbidden in this source check.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

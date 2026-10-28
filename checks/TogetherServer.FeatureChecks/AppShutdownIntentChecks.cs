using System.Diagnostics;
using System.Reflection;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Uses held semaphores, in-memory managed records and a refusing driver only.
// No application, game process, listener or desktop surface is opened.
internal static class AppShutdownIntentChecks
{
    internal static async Task RunAsync(string root)
    {
        LeaseLifetime();
        await AvailabilityReadsAsync();
        await QueuedStartBeforeQuitAsync(root);
        await AuthoritativeAdmissionAndFinalGuardsAsync(root);
    }

    private static void LeaseLifetime()
    {
        var shutdown = new AppShutdownIntent(new object());
        var first = shutdown.BeginQuit();
        var second = shutdown.BeginQuit();
        Require(shutdown.IsPending && !shutdown.IsStopping, "A pending Quit claimed accepted shutdown or left launch eligible.");
        first.Dispose();
        first.Dispose();
        Require(shutdown.IsPending && !shutdown.IsStopping, "One refused Quit cleared another concurrent request's intent.");
        second.Dispose();
        Require(!shutdown.IsPending && !shutdown.IsStopping, "Refused Quit requests did not restore launch eligibility.");
        try
        {
            using var failed = shutdown.BeginQuit();
            throw new InvalidOperationException("Synthetic validation failure.");
        }
        catch (InvalidOperationException) { }
        Require(!shutdown.IsPending, "A failed Quit validation retained launch intent.");
        using (var accepted = shutdown.BeginQuit()) accepted.Accept();
        using (shutdown.BeginQuit()) { }
        Require(shutdown.IsPending && shutdown.IsStopping, "Accepted shutdown was cleared when its request or another request ended.");
    }

    private static async Task AvailabilityReadsAsync()
    {
        var launchGate = new object();
        var shutdown = new AppShutdownIntent(launchGate);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            lock (launchGate)
            {
                held.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var observed = await Task.Run(() => (shutdown.IsPending, shutdown.IsStopping)).WaitAsync(TimeSpan.FromSeconds(2));
            Require(!observed.IsPending && !observed.IsStopping, "Observing app availability changed shutdown state.");
        }
        finally
        {
            release.TrySetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task QueuedStartBeforeQuitAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "queued-start-before-quit"));
        var (manager, driver, profileId, shutdown) = CreateManager(data);
        using var modeGate = new SemaphoreSlim(0, 1); // A long Stop owns the gate.
        var start = StartAfterGateAsync(); // Start queues before Quit.
        using var intent = shutdown.BeginQuit(); // Intent is raised before Quit waits.
        var quit = QuitAfterGateAsync();
        Require(!start.IsCompleted && !quit.IsCompleted && shutdown.IsPending && !shutdown.IsStopping,
            "The regression did not hold Start ahead of an unaccepted Quit.");
        modeGate.Release();
        var startResult = await start.WaitAsync(TimeSpan.FromSeconds(5));
        var quitAccepted = await quit.WaitAsync(TimeSpan.FromSeconds(5));
        Require(startResult.Code == "HostShuttingDown" && !startResult.Ok && quitAccepted &&
            shutdown.IsStopping && driver.Launches == 0 && !await manager.HasManagedRunsAsync(),
            "A Start ahead of Quit launched, lost its typed refusal or prevented clean shutdown admission.");

        async Task<ActionResult> StartAfterGateAsync()
        {
            await modeGate.WaitAsync();
            try { return await manager.StartAsync(profileId); }
            finally { modeGate.Release(); }
        }
        async Task<bool> QuitAfterGateAsync()
        {
            await modeGate.WaitAsync();
            try
            {
                if (await manager.HasManagedRunsAsync()) return false;
                intent.Accept();
                return true;
            }
            finally { modeGate.Release(); }
        }
    }

    private static async Task AuthoritativeAdmissionAndFinalGuardsAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "authoritative-quit-admission"));
        var (manager, driver, profileId, shutdown) = CreateManager(data);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var lifecycleGate = (SemaphoreSlim)typeof(HostManager).GetField("gate", flags)!.GetValue(manager)!;
        var managedRuns = (List<ManagedRun>)typeof(HostManager).GetField("runs", flags)!.GetValue(manager)!;
        Task<bool> authoritative;
        await lifecycleGate.WaitAsync();
        try
        {
            managedRuns.Add(new ManagedRun { ProfileId = profileId, Kind = GameKinds.Fixture });
            var cached = await manager.SnapshotAsync();
            authoritative = manager.HasManagedRunsAsync();
            Require(cached.Runs.All(run => run.State == "Offline") && !authoritative.IsCompleted,
                "Quit admission reused cached Offline status instead of waiting for the lifecycle gate.");
        }
        finally { lifecycleGate.Release(); }
        Require(await authoritative.WaitAsync(TimeSpan.FromSeconds(5)), "Quit admitted an unresolved managed run hidden by cached Offline status.");
        using (shutdown.BeginQuit())
            Require(await manager.HasManagedRunsAsync() && driver.Stops == 0 && !shutdown.IsStopping,
                "A refused Quit stopped a managed game or claimed Closing.");
        Require(!shutdown.IsPending, "A refused managed-run Quit left launch permanently blocked.");
        await lifecycleGate.WaitAsync();
        try { managedRuns.Clear(); }
        finally { lifecycleGate.Release(); }
        Require(!await manager.HasManagedRunsAsync(), "Cleared synthetic managed state remained active.");
        using (shutdown.BeginQuit())
        {
            var start = await manager.StartAsync(profileId);
            var restart = await manager.RestartAsync(profileId);
            var safeRestart = await manager.SafeRestartAsync(profileId);
            var replace = await manager.ReplaceEmptyPortConflictsAndStartAsync(profileId);
            Require(new[] { start, restart, safeRestart, replace }.All(result => !result.Ok && result.Code == "HostShuttingDown") &&
                driver.Preparations == 0 && driver.Launches == 0 && driver.Stops == 0,
                "Pending Quit reached preparation, process launch or Stop through an owner lifecycle action.");
        }

        AppShutdownIntent.QuitRequest? duringPreparation = null;
        driver.DuringPreparation = () => duringPreparation = shutdown.BeginQuit();
        try
        {
            var prepared = await manager.StartAsync(profileId);
            Require(prepared.Code == "HostShuttingDown" && driver.Preparations == 1 && driver.Launches == 0,
                "Quit intent raised during process preparation bypassed the final common launch fence.");
        }
        finally { duringPreparation?.Dispose(); }
        driver.DuringPreparation = null;
        AppShutdownIntent.QuitRequest? immediatelyBeforeLaunch = null;
        manager.BeforeManagedLaunchForChecks = () => immediatelyBeforeLaunch = shutdown.BeginQuit();
        try
        {
            var final = await manager.StartAsync(profileId);
            Require(final.Code == "HostShuttingDown" && driver.Launches == 0 && data.LoadRuns().Count == 0 &&
                !await manager.HasManagedRunsAsync(),
                "Quit immediately before launch left a process attempt or phantom managed record.");
        }
        finally { immediatelyBeforeLaunch?.Dispose(); }
        manager.BeforeManagedLaunchForChecks = null;
        var eligibleAgain = await manager.StartAsync(profileId);
        Require(eligibleAgain.Code == "LaunchFailed" && driver.Launches == 1 && !shutdown.IsPending,
            "A refused Quit was replayed or prevented a new explicit launch attempt after validation ended.");
        shutdown.MarkStopping();
        Require((await manager.StartAsync(profileId)).Code == "HostShuttingDown" && driver.Launches == 1,
            "Application stopping did not permanently fence new launches.");
    }

    private static (HostManager Manager, RefusingDriver Driver, Guid ProfileId, AppShutdownIntent Shutdown) CreateManager(LocalData data)
    {
        var executable = Path.Combine(data.RootPath, "never-executed.bin");
        File.WriteAllText(executable, "Synthetic executable evidence; never executed.");
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            ExecutablePath = executable,
            WorldDirectory = Path.Combine(data.RootPath, "synthetic-world"),
            WorldSource = "New"
        };
        data.SaveSettings(new HostSettings { Profiles = [profile] });
        var driver = new RefusingDriver();
        var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
        var shutdown = new AppShutdownIntent(SharedWorldMutationGate.For(data.RootPath));
        manager.ApplicationStartAllowed = () => !shutdown.IsPending;
        return (manager, driver, profile.Id, shutdown);
    }

    private sealed class RefusingDriver : IGameServerDriver
    {
        internal int Preparations;
        internal int Launches;
        internal int Stops;
        internal Action? DuringPreparation;
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "No-process shutdown checks";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => null;
        public void PrepareStart(ServerProfile profile, ManagedRun run) { Preparations++; DuringPreparation?.Invoke(); }
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) { Launches++; throw new InvalidOperationException("Synthetic launch refusal."); }
        public GameHealthResult Health(ManagedRun run) => throw new InvalidOperationException("No process query permitted.");
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) { Stops++; throw new InvalidOperationException("No process Stop permitted."); }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

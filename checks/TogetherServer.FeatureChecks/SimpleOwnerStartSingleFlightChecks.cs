using System.Diagnostics;
using System.Reflection;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Calls two real service instances against one held manager and an injected
// offline peer client. The refusing driver never creates or queries a process.
internal static class SimpleOwnerStartSingleFlightChecks
{
    internal static async Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "owner-start-single-flight"));
        var executable = Path.Combine(data.RootPath, "never-executed.bin");
        File.WriteAllText(executable, "Generated validation input; never executed.");
        var firstProfile = Profile("Synthetic requested server");
        var otherProfile = Profile("Unrelated synthetic server");
        data.SaveSettings(new HostSettings { Profiles = [firstProfile, otherProfile] });
        var driver = new RefusingDriver();
        var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
        var networkCalls = 0;
        HttpClient Offline(string _, IEnumerable<string> __)
        {
            Interlocked.Increment(ref networkCalls);
            throw new HttpRequestException("A network request is forbidden in this source check.");
        }
        using var friends = new FriendService(data, Offline);
        using var peers = new WorldPeerService(data, manager, friends, new PairingService(data), Offline);
        var firstService = new SimpleOwnerHostingService(data, manager, peers, () => Task.CompletedTask);
        var secondService = new SimpleOwnerHostingService(data, manager, peers, () => Task.CompletedTask);
        var gate = (SemaphoreSlim)typeof(HostManager).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        using var canceled = new CancellationTokenSource();
        var pending = new List<Task<ActionResult>>();
        var firstReports = 0;
        var duplicateReports = 0;
        var otherReports = 0;
        var retryReports = 0;
        var gateHeld = false;
        try
        {
            await gate.WaitAsync();
            gateHeld = true;
            var first = firstService.StartAsync(firstProfile.Id, canceled.Token, progress: _ => firstReports++);
            pending.Add(first);
            var originalJob = BackgroundJobs.For(data).Read().Jobs.Single(item => item.ProfileId == firstProfile.Id);
            Require(!first.IsCompleted && firstReports == 1 && originalJob is { State: "Running", Attempts: 1 },
                "The first actual owner Start did not hold preparation with one active job.");
            var duplicate = await secondService.StartAsync(firstProfile.Id, default, progress: _ => duplicateReports++)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var duplicateJobs = BackgroundJobs.For(data).Read();
            var duplicateAttempt = StartAttemptDiagnostics.Recent(data, firstProfile.Id, 20)
                .Single(item => item.Code == "LocalHostingBusy");
            Require(!duplicate.Ok && duplicate.Code == "LocalHostingBusy" && duplicateReports == 0 && firstReports == 1 &&
                duplicateJobs.ActiveCount == 1 && duplicateJobs.Jobs.Count == 1 && duplicateJobs.Jobs[0] == originalJob &&
                duplicateAttempt.Kind == "BlockedStart" && duplicateAttempt.RelatedJobId == originalJob.Id && driver.Validations.Count == 0,
                "A second service repeated preparation, changed the original job or lost the blocked attempt's job link.");
            var other = secondService.StartAsync(otherProfile.Id, default, progress: _ => otherReports++);
            pending.Add(other);
            Require(!other.IsCompleted && otherReports == 1 && BackgroundJobs.For(data).Read().ActiveCount == 2,
                "A requested-profile claim blocked unrelated profile preparation or reused its job.");
            canceled.Cancel();
            await RequireCancellationAsync(first, canceled.Token);
            Require(BackgroundJobs.For(data).Read().Jobs.Single(item => item.ProfileId == firstProfile.Id).State == "Interrupted",
                "Canceled preparation remained active or claimed completion.");

            var retry = secondService.StartAsync(firstProfile.Id, default, progress: _ => retryReports++);
            pending.Add(retry);
            var retryJob = BackgroundJobs.For(data).Read().Jobs.Single(item => item.ProfileId == firstProfile.Id);
            Require(!retry.IsCompleted && retryReports == 1 && retryJob.Id == originalJob.Id && retryJob.Attempts == 2 &&
                retryJob.State == "Running" && BackgroundJobs.For(data).Read().ActiveCount == 2,
                "Cancellation retained the claim, duplicated history or prevented a fresh explicit retry.");
            var duplicateRetry = await firstService.StartAsync(firstProfile.Id, default, progress: _ => duplicateReports++)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Require(duplicateRetry.Code == "LocalHostingBusy" && duplicateReports == 0 &&
                BackgroundJobs.For(data).Read().Jobs.Single(item => item.ProfileId == firstProfile.Id) == retryJob,
                "A cleanup retry lost its per-manager claim or a duplicate changed its current job.");
            gate.Release();
            gateHeld = false;
            var completed = await Task.WhenAll(other, retry).WaitAsync(TimeSpan.FromSeconds(5));
            Require(completed.All(result => !result.Ok && result.Code == "SingleFlightValidationReached") &&
                driver.Validations.GetValueOrDefault(firstProfile.Id) == 1 && driver.Validations.GetValueOrDefault(otherProfile.Id) == 1 &&
                driver.Launches == 0 && networkCalls == 0 && BackgroundJobs.For(data).Read().ActiveCount == 0,
                "Independent preparations did not complete exactly once through the real manager's refusing driver.");
            var afterFailure = await firstService.StartAsync(firstProfile.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
            Require(afterFailure.Code == "SingleFlightValidationReached" && driver.Validations[firstProfile.Id] == 2 &&
                BackgroundJobs.For(data).Read().Jobs.Single(item => item.ProfileId == firstProfile.Id).Attempts == 3 &&
                BackgroundJobs.For(data).Read().ActiveCount == 0 && data.LoadRuns().Count == 0,
                "Failed preparation retained the claim, started a game or prevented another explicit retry.");
        }
        finally
        {
            canceled.Cancel();
            if (gateHeld) gate.Release();
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        }

        ServerProfile Profile(string name) => new()
        {
            Kind = GameKinds.Fixture,
            Name = name,
            WorldId = Guid.NewGuid().ToString("N"),
            WorldSource = "New",
            ExecutablePath = executable,
            WorldDirectory = Path.Combine(data.RootPath, Guid.NewGuid().ToString("N"))
        };
    }

    private static async Task RequireCancellationAsync(Task<ActionResult> task, CancellationToken token)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        throw new InvalidOperationException("Canceled owner preparation did not retain request cancellation.");
    }

    private sealed class RefusingDriver : IGameServerDriver
    {
        internal readonly Dictionary<Guid, int> Validations = [];
        internal int Launches;
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Validation-only single-flight checks";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => false;
        public string? ManagedSaveDirectory(ServerProfile profile) => null;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile)
        {
            Validations[profile.Id] = Validations.GetValueOrDefault(profile.Id) + 1;
            return new("SingleFlightValidationReached", "Synthetic validation refuses before process preparation.");
        }
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw Forbidden();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) { Launches++; throw Forbidden(); }
        public GameHealthResult Health(ManagedRun run) => throw Forbidden();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw Forbidden();
        private static InvalidOperationException Forbidden() => new("A process action is forbidden in this source check.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Fake drivers deliberately throw instead of creating a process. These checks
// cover the real manager guard/journal path without touching the desktop.
internal static class StartAttemptDiagnosticsChecks
{
    internal static async Task RunAsync(string root)
    {
        var path = Path.Combine(root, "persisted-start-diagnostics");
        var profileId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        using (var data = new LocalData(path))
        {
            data.SaveState("host-identity.json", new HostIdentityMetadata { HostId = targetId });
            var profile = new ServerProfile
            {
                Id = profileId,
                Kind = GameKinds.Fixture,
                Name = "Synthetic server",
                WorldId = "Synthetic",
                WorldSource = "New",
                ExecutablePath = Path.Combine(path, "never-executed.bin"),
                WorldDirectory = Path.Combine(path, "synthetic-world")
            };
            data.SaveSettings(new HostSettings { Profiles = [profile] });
            var driver = new DiagnosticDriver();
            var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
            using (var attempt = StartAttemptDiagnostics.Begin(data, profileId, "RemoteStart", operationId))
            {
                var rejected = await manager.StartAsync(profileId);
                Require(!rejected.Ok && rejected.Code == "ExecutableMissing", "A prelaunch missing-executable rejection changed.");
                attempt.Complete(false, rejected.Code, rejected.Message);
            }
            var history = await manager.RecentSessionsAsync(profileId, 20);
            Require(history.Sessions.Count == 0 && history.Attempts is { Count: 1 } &&
                history.Attempts[0] is { Kind: "BlockedStart", Phase: "LaunchGuards" } attemptView &&
                attemptView.OperationId == operationId && attemptView.TargetDeviceId == targetId && driver.Launches == 0,
                "A blocked Start disappeared, lost correlation/target/phase, or fabricated a game session.");
            File.WriteAllText(profile.ExecutablePath, "synthetic file that is never executed");
            var launchFailed = await manager.StartAsync(profileId);
            Require(!launchFailed.Ok && launchFailed.Code == "LaunchFailed" && driver.Launches == 1 && data.LoadRuns().Count == 0,
                "A fake process launch failure did not stay distinct from a real managed run.");
            var allowed = true;
            driver.DuringValidation = () => allowed = false;
            var authChanged = await manager.StartAsync(profileId, default, () => allowed);
            Require(authChanged.Code == "RemoteStartAuthorizationChanged" && driver.Launches == 1,
                "Authorization changed during validation but a process launch was still attempted.");
            var restartDenied = await manager.RestartAsync(profileId, startAllowed: () => false);
            var replacementDenied = await manager.ReplaceEmptyPortConflictsAndStartAsync(profileId, () => false);
            Require(restartDenied.Code == "RemoteStartAuthorizationChanged" &&
                replacementDenied.Code == "RemoteStartAuthorizationChanged" && driver.Launches == 1 && data.LoadRuns().Count == 0,
                "An unauthorized remote Restart or replacement reached Stop/process preparation.");
            driver.DuringValidation = null;
            Require((await manager.SetSimpleSharedHostingAsync(profileId, true, default)).Ok,
                "Synthetic shared policy could not be enabled.");
            _ = await manager.PublishSimpleSharedHostingRosterAsync(profileId);
            manager.SharedHostingStartDecision = (_, _) => new(false, "SharedSaveHistoryNeedsReview", "Competing signed save histories need review.");
            Require((await manager.StartAsync(profileId)).Code == "SharedSaveHistoryNeedsReview",
                "A history conflict was flattened into NewerSharedSaveAvailable.");
            manager.SharedHostingStartDecision = (_, _) => new(false, "NewerSharedSaveAvailable", "A peer continued this world. Prepare the latest save.");
            var newer = await manager.StartAsync(profileId);
            Require(newer.Code == "NewerSharedSaveAvailable" && driver.Launches == 1 && data.LoadRuns().Count == 0,
                "A newer-save fence started a transfer/process or changed its typed result.");
            StartAttemptDiagnostics.RecordFailure(data, profileId, "PrepareHosting", "Receiving", "TransferFailed",
                "password=do-not-retain https://192.0.2.5:5131 C:\\private\\world.db", relatedJobId: operationId);
            Require(!File.ReadAllText(Path.Combine(path, "start-attempts.json")).Contains("do-not-retain", StringComparison.Ordinal),
                "Diagnostic persistence retained a credential.");
            var failedRun = Guid.NewGuid();
            StartAttemptDiagnostics.RecordFailure(data, profileId, "Start", "WaitingForReady", "ProcessExitedBeforeReady",
                "The process exited before Ready.", "FailedBeforeReady", failedRun);
            Require((await manager.RecentSessionsAsync(profileId, 20)).Sessions.Count == 0,
                "A diagnostic-only failure fabricated session timing, occupancy or readiness.");
        }
        using (var reopened = new LocalData(path))
        {
            var attempts = StartAttemptDiagnostics.Recent(reopened, profileId, 20);
            Require(attempts.Any(item => item.OperationId == operationId && item.Code == "ExecutableMissing") &&
                attempts.Any(item => item.Kind == "LaunchFailed") && attempts.Any(item => item.Code == "NewerSharedSaveAvailable") &&
                attempts.Any(item => item.RelatedJobId == operationId), "Failed attempts or transfer linkage did not survive app restart.");
        }
        InterruptedAndBounded(root, profileId);
        await RemoteMessageAsync(root, profileId);
        await IndependentRemoteControlsAsync(root, profileId);
        await TerminalStartCleanupAsync(root, profileId);
    }

    private static void InterruptedAndBounded(string root, Guid profileId)
    {
        using var data = new LocalData(Path.Combine(root, "interrupted-start-diagnostics"));
        var now = DateTimeOffset.UtcNow;
        var pendingId = Guid.NewGuid();
        var entries = Enumerable.Range(0, 505).Select(index => new StartAttemptDiagnostic(Guid.NewGuid(), profileId,
            Guid.NewGuid(), null, "Start", "LaunchGuards", "Blocked", "Synthetic refusal.", now.AddMinutes(-index), now,
            null, "BlockedStart")).ToList();
        entries.Add(new(pendingId, profileId, pendingId, null, "PrepareHosting", "Receiving", "OperationRunning", "Pending.", now,
            null, null, "Pending"));
        entries.Add(new(Guid.NewGuid(), profileId, Guid.NewGuid(), null, "Start", "LaunchGuards", "Old", "Expired.", now.AddDays(-31), now,
            null, "BlockedStart"));
        data.SaveState("start-attempts.json", entries);
        var recent = StartAttemptDiagnostics.Recent(data, profileId, 20);
        var persisted = data.LoadState("start-attempts.json", new List<StartAttemptDiagnostic>());
        Require(recent.Any(item => item.OperationId == pendingId && item.Kind == "Interrupted" && item.Code == "AppInterrupted") &&
            persisted.Count <= StartAttemptDiagnostics.MaximumEntries && persisted.All(item => item.Code != "Old"),
            "Restart resumed/forgot pending work or failed to enforce attempt retention.");
        var duplicate = Guid.NewGuid();
        for (var index = 0; index < 3; index++) StartAttemptDiagnostics.RecordFailure(data, profileId, "ReceiveSave", "Receiving",
            "TransferFailed", "Synthetic retry.", "TransferFailed", duplicate, duplicate);
        Require(StartAttemptDiagnostics.Recent(data, profileId, 20).Count(item => item.OperationId == duplicate) == 1,
            "Repeated transfer failures created duplicate attempts for the same job.");
    }

    private static async Task RemoteMessageAsync(string root, Guid profileId)
    {
        using var data = new LocalData(Path.Combine(root, "remote-start-message"));
        await using var coordinator = new RemoteOperationCoordinator(data);
        var device = Guid.NewGuid();
        var submission = coordinator.Submit(device, Guid.NewGuid(), profileId, "start", () =>
            Task.FromResult(new RemoteOperationOutcome(false, "NewerSharedSaveAvailable", "Synthetic newer save fence.")));
        var id = submission.Operation!.Id;
        for (var index = 0; index < 100 && coordinator.Find(device, id)?.State is "Pending" or "Running"; index++)
            await Task.Delay(10);
        var result = coordinator.Find(device, id)!;
        Require(result.State == "Failed" && result.Message.Contains("No save transfer is implied", StringComparison.Ordinal) &&
            StartAttemptDiagnostics.Recent(data, profileId, 20).Single().OperationId == id,
            "A canonical newer-save refusal implied an actual transfer or lost remote operation correlation.");
    }

    private static async Task IndependentRemoteControlsAsync(string root, Guid profileId)
    {
        using var data = new LocalData(Path.Combine(root, "independent-remote-start-preparation"));
        await using var coordinator = new RemoteOperationCoordinator(data);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = Guid.NewGuid();
        var request = Guid.NewGuid();
        var starts = 0;
        async Task<RemoteOperationOutcome> Start()
        {
            Interlocked.Increment(ref starts);
            entered.TrySetResult();
            await release.Task;
            return new(false, "SharedSaveUnavailable", "Synthetic source unavailable.");
        }
        try
        {
            var first = coordinator.Submit(device, request, profileId, "start", Start);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var duplicate = coordinator.Submit(device, request, profileId, "start", Start);
            var competing = coordinator.Submit(device, Guid.NewGuid(), profileId, "start", Start);
            Require(first.Accepted && duplicate.Existing && duplicate.Operation?.Id == first.Operation!.Id &&
                !competing.Accepted && competing.Code == "StartAlreadyPreparing" && starts == 1,
                "Repeated explicit Starts duplicated preparation or lost request idempotency.");
            var other = coordinator.Submit(device, Guid.NewGuid(), Guid.NewGuid(), "stop", () =>
            {
                stopped.TrySetResult();
                return Task.FromResult(new RemoteOperationOutcome(true, "Stopped", "Synthetic Stop completed."));
            });
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(other.Accepted && coordinator.Find(device, first.Operation!.Id)?.State == "Running" && starts == 1,
                "A long save preparation blocked an unrelated authorized remote action.");
            for (var index = 0; index < 3; index++)
                Require(coordinator.Submit(device, Guid.NewGuid(), Guid.NewGuid(), "start", Start).Accepted,
                    "The bounded concurrent preparation slots were unavailable.");
            var saturated = coordinator.Submit(device, Guid.NewGuid(), Guid.NewGuid(), "start", Start);
            Require(!saturated.Accepted && saturated.Code == "StartPreparationBusy",
                "Remote Start preparation was not bounded.");
            release.TrySetResult();
            for (var index = 0; index < 100 && coordinator.Find(device, first.Operation!.Id)?.State == "Running"; index++)
                await Task.Delay(10);
            Require(coordinator.Find(device, first.Operation!.Id) is { State: "Failed", Code: "SharedSaveUnavailable" } &&
                data.LoadRemoteOperations().Any(item => item.Id == first.Operation.Id && item.Code == "SharedSaveUnavailable") &&
                StartAttemptDiagnostics.Recent(data, profileId, 20).Any(item => item.OperationId == first.Operation.Id),
                "Independent preparation lost its durable result or attempt correlation.");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task TerminalStartCleanupAsync(string root, Guid profileId)
    {
        using var data = new LocalData(Path.Combine(root, "terminal-start-cleanup"));
        await using var coordinator = new RemoteOperationCoordinator(data);
        var published = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var starts = 0;
        var forbiddenExecutions = 0;
        var device = Guid.NewGuid();
        var firstRequest = Guid.NewGuid();
        coordinator.AfterTerminalPublishedForChecks = operationId =>
        {
            if (Interlocked.Increment(ref callbacks) != 1) return Task.CompletedTask;
            published.TrySetResult(operationId);
            return releaseCleanup.Task;
        };
        Task<RemoteOperationOutcome> Forbidden()
        {
            Interlocked.Increment(ref forbiddenExecutions);
            return Task.FromResult(new RemoteOperationOutcome(true, "Unexpected", "This delegate must not run."));
        }
        try
        {
            var first = coordinator.Submit(device, firstRequest, profileId, "start", () =>
            {
                Interlocked.Increment(ref starts);
                return Task.FromResult(new RemoteOperationOutcome(false, "SharedSaveUnavailable", "Synthetic save source unavailable."));
            });
            var terminalId = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(first.Accepted && first.Operation?.Id == terminalId &&
                coordinator.Find(device, terminalId) is { State: "Failed", Code: "SharedSaveUnavailable" },
                "The cleanup regression did not publish the first Start's terminal result.");
            var cleanupTask = ActiveStartTask(coordinator, profileId);
            var journalCount = data.LoadRemoteOperations().Count;
            var distinct = coordinator.Submit(device, Guid.NewGuid(), profileId, "start", Forbidden);
            var idempotent = coordinator.Submit(device, firstRequest, profileId, "start", Forbidden);
            Require(!distinct.Accepted && distinct.Code == "StartAlreadyPreparing" &&
                idempotent.Accepted && idempotent.Existing && idempotent.Operation is
                { State: "Failed", Code: "SharedSaveUnavailable" } prior && prior.Id == terminalId &&
                forbiddenExecutions == 0 && data.LoadRemoteOperations().Count == journalCount && !cleanupTask.IsCompleted,
                "Visible terminal state admitted an untracked Start, journaled a rejected retry or broke same-key idempotency.");
            var stop = coordinator.Submit(device, Guid.NewGuid(), Guid.NewGuid(), "stop", () =>
            {
                otherStopped.TrySetResult();
                return Task.FromResult(new RemoteOperationOutcome(true, "Stopped", "Synthetic unrelated Stop completed."));
            });
            await otherStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(stop.Accepted && !cleanupTask.IsCompleted,
                "Terminal Start cleanup blocked unrelated serialized remote controls.");
            releaseCleanup.TrySetResult();
            await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5));

            var nextRequest = Guid.NewGuid();
            var next = coordinator.Submit(device, nextRequest, profileId, "start", async () =>
            {
                Interlocked.Increment(ref starts);
                nextEntered.TrySetResult();
                await releaseNext.Task;
                return new(true, "Started", "Synthetic retry completed.");
            });
            await nextEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var nextTask = ActiveStartTask(coordinator, profileId);
            var duringNext = coordinator.Submit(device, Guid.NewGuid(), profileId, "start", Forbidden);
            Require(next.Accepted && !duringNext.Accepted && duringNext.Code == "StartAlreadyPreparing" && starts == 2 &&
                forbiddenExecutions == 0 && coordinator.Find(device, next.Operation!.Id)?.State == "Running" && !nextTask.IsCompleted,
                "A retry after cleanup was lost, executed twice or had its registration removed by the old Start.");
            releaseNext.TrySetResult();
            await nextTask.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.DisposeAsync();
            var journal = data.LoadRemoteOperations();
            Require(journal.Count == journalCount + 2 && journal.All(item => RemoteOperationStates.Terminal(item.State)) &&
                journal.Single(item => item.Id == terminalId).Code == "SharedSaveUnavailable" &&
                journal.Single(item => item.Id == next.Operation!.Id).State == "Succeeded" && starts == 2 && forbiddenExecutions == 0,
                "Terminal cleanup left phantom pending work, lost results or executed a rejected request.");
        }
        finally
        {
            // Release both possible owners before await-using disposal, even
            // when a regression assertion fails inside the held interval.
            releaseCleanup.TrySetResult();
            releaseNext.TrySetResult();
        }

        static Task ActiveStartTask(RemoteOperationCoordinator owner, Guid profile)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var sync = typeof(RemoteOperationCoordinator).GetField("sync", flags)!.GetValue(owner)!;
            lock (sync)
                return ((Dictionary<Guid, Task>)typeof(RemoteOperationCoordinator).GetField("activeStarts", flags)!.GetValue(owner)!)[profile];
        }
    }

    private sealed class DiagnosticDriver : IGameServerDriver
    {
        internal int Launches;
        internal Action? DuringValidation;
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "No-process diagnostics";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) { DuringValidation?.Invoke(); return null; }
        public void PrepareStart(ServerProfile profile, ManagedRun run) { }
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) { Launches++; throw new InvalidOperationException("Synthetic launch failure."); }
        public GameHealthResult Health(ManagedRun run) => throw new InvalidOperationException("No process query permitted.");
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw new InvalidOperationException("No process action permitted.");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

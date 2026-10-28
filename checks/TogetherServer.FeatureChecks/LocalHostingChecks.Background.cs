using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class LocalHostingChecks
{
    internal static async Task BackgroundPreparationAsync(string root)
    {
        using var fixture = new Fixture(root, granted: true);
        var baseline = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(baseline.Ok, "Could not prepare the file-only background baseline.");
        var baselineAttempts = BackgroundJobs.For(fixture.Data).Read().Jobs.Single(item =>
            item.Operation == "Hosting preparation" && item.ProfileId == fixture.ProfileId).Attempts;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = fixture.WithLatestSource(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(false, "SharedSaveHistoryNeedsReview", "Synthetic preparation needs history review.");
        });
        using var app = new CancellationTokenSource();
        var work = service.PrepareInBackgroundAsync(fixture.ProfileId, new(true, true), app.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reattached = service.PrepareInBackgroundAsync(fixture.ProfileId, new(true, true), app.Token);
        Require(ReferenceEquals(work, reattached) && calls == 1,
            "Reopening a server created a second preparation instead of observing the application job.");
        var jobs = BackgroundJobs.For(fixture.Data).Read();
        var job = jobs.Jobs.Single(item => item.Operation == "Hosting preparation" && item.ProfileId == fixture.ProfileId);
        Require(jobs.ActiveCount == 1 && job.Phase == "LatestSave" && job.Attempts == baselineAttempts + 1,
            "A coalesced preparation did not expose one application job with its actual phase.");
        using var page = new CancellationTokenSource();
        var observer = work.WaitAsync(page.Token);
        page.Cancel();
        try { await observer; throw new InvalidOperationException("A canceled page wait remained active."); }
        catch (OperationCanceledException) when (page.IsCancellationRequested) { }
        Require(!work.IsCompleted && service.Progress(fixture.ProfileId) is { State: "Working" } && calls == 1,
            "Navigating away canceled the backend preparation or discarded its current progress.");
        release.TrySetResult();
        var completed = await reattached.WaitAsync(TimeSpan.FromSeconds(10));
        Require(completed.Code == "SharedSaveHistoryNeedsReview" && calls == 1 && fixture.Driver.ProcessAttempts == 0 &&
            fixture.Data.LoadRuns().Count == 0 && service.Progress(fixture.ProfileId) is { State: "Failed" } &&
            StartAttemptDiagnostics.Recent(fixture.Data, fixture.ProfileId, 20).Any(item =>
                item.Code == "SharedSaveHistoryNeedsReview" && item.Kind == "PreparationFailed" && item.RelatedJobId == job.Id) &&
            BackgroundJobs.For(fixture.Data).Read().Jobs.Single(item => item.Id == job.Id) is
            { State: "Failed", ResultCode: "SharedSaveHistoryNeedsReview" },
            "Detached preparation lost its terminal failure, started a game, or failed to retain diagnostics.");
        var runId = Guid.NewGuid();
        fixture.Data.SaveRunArchive([new(baseline.LocalProfileId!.Value, runId, GameKinds.Fixture, "SyntheticLocalHosting",
            null, null, false, "Synthetic archived evidence with unavailable metrics", DateTimeOffset.UtcNow, false)]);
        var restartedManager = new HostManager(fixture.Data, new GameServerRegistry([fixture.Driver], PortProbeMode.ObserveOnly));
        var history = await restartedManager.RecentSessionsAsync(baseline.LocalProfileId.Value, 20);
        Require(history.Sessions is { Count: 1 } && history.Sessions[0].OperationId == runId &&
            history.Sessions[0].StartedUtc is null && history.Sessions[0].DurationSeconds is null &&
            history.Attempts!.Any(item => item.Code == "SharedSaveHistoryNeedsReview" && item.ProfileId == baseline.LocalProfileId),
            "A signed local continuation lost its canonical source's preparation history after manager restart or invented run metrics.");
    }
}

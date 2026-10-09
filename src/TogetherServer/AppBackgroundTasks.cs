namespace TogetherServer;

// Owns the app's periodic work and cancellation as one lifecycle component.
// Each loop isolates failures so a transient probe cannot stop the other loops.
internal sealed class AppBackgroundTasks(
    FriendService friend, HostManager manager, CompanionServer companionServer,
    AppUpdater updater, AppInstance instance, LocalData data, DesktopWindow? desktop,
    Func<bool> friendMode, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<Guid> notifiedActivity = [];
    private Task[] tasks = [];
    private DateTimeOffset notificationStartedUtc;

    public void Start()
    {
        if (tasks.Length != 0) throw new InvalidOperationException("Background work is already running.");
        notificationStartedUtc = clock.GetUtcNow();
        tasks = [Task.Run(RunFriendPollAsync), Task.Run(RunLifecycleAsync),
            Task.Run(RunUpdateChecksAsync), Task.Run(RunNotificationsAsync)];
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await Task.WhenAll(tasks); await friend.WaitForSharedCatchUpAsync(); }
        finally { stop.Dispose(); }
    }

    private async Task RunFriendPollAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try { await friend.PollAsync(); friend.ScheduleSharedCatchUp(stop.Token); }
            catch (Exception ex) { DiagnosticOutput.WriteError("Friend poll failed: " + ex.GetType().Name); }
            if (!await Delay(TimeSpan.FromSeconds(5))) break;
        }
    }

    private async Task RunLifecycleAsync()
    {
        var previousCycleUtc = clock.GetUtcNow();
        while (!stop.IsCancellationRequested)
        {
            var cycleUtc = clock.GetUtcNow();
            var pollingGap = cycleUtc - previousCycleUtc;
            previousCycleUtc = cycleUtc;
            if (pollingGap > TimeSpan.FromSeconds(30) || pollingGap < TimeSpan.Zero)
            {
                try
                {
                    await manager.HandleSystemResumeAsync();
                    await companionServer.SyncAsync();
                }
                catch (Exception ex) { DiagnosticOutput.WriteError("Resume revalidation failed: " + ex.GetType().Name); }
            }
            try { await manager.RefreshObservationsAsync(); }
            catch (Exception ex) { DiagnosticOutput.WriteError("Server observation failed: " + ex.GetType().Name); }
            try { await manager.MaintainIdleShutdownAsync(); }
            catch (Exception ex) { DiagnosticOutput.WriteError("Empty-server timer failed: " + ex.GetType().Name); }
            try { await manager.MaintainCrashRecoveryAsync(); }
            catch (Exception ex) { DiagnosticOutput.WriteError("Crash recovery failed: " + ex.GetType().Name); }
            if (!await Delay(TimeSpan.FromSeconds(3))) break;
        }
    }

    private async Task RunUpdateChecksAsync()
    {
        if (!instance.UpdatesAvailable) return;
        while (!stop.IsCancellationRequested)
        {
            try { await updater.CheckAsync(); }
            catch (Exception ex) { DiagnosticOutput.WriteError("Update check failed: " + ex.GetType().Name); }
            if (!await Delay(AppUpdater.AutomaticCheckInterval)) break;
        }
    }

    private async Task RunNotificationsAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            if (desktop is not null)
            {
                try
                {
                    var visible = (friendMode()
                            ? friend.View().Activity ?? []
                            : data.LoadActivity(100).Where(item => item.Visibility != ActivityVisibility.Device))
                        .Where(item => item.OccurredUtc >= notificationStartedUtc &&
                            item.Severity is ActivitySeverity.Important or ActivitySeverity.Warning)
                        .DistinctBy(item => item.Id)
                        .OrderBy(item => item.OccurredUtc)
                        .ToList();
                    foreach (var item in visible.Where(item => notifiedActivity.Add(item.Id)))
                        desktop.Notify(item, friendMode(), friendMode() ? friend.View().ConnectionId : null);
                    var hostSnapshot = await manager.SnapshotAsync();
                    desktop.SetTraySummary(new DesktopTraySummary(
                        hostSnapshot.Runs.Count(run => run.State is "Ready" or "Starting" or "Process running" or "Listening" or "Stopping"),
                        hostSnapshot.Runs.Count(run => run.State is "Unknown" or "Failed"),
                        friend.View().State));
                    if (notifiedActivity.Count > 1000)
                        notifiedActivity.IntersectWith(visible.Select(item => item.Id));
                }
                catch (Exception ex) { DiagnosticOutput.WriteError("Tray notification check failed: " + ex.GetType().Name); }
            }
            if (!await Delay(TimeSpan.FromSeconds(5))) break;
        }
    }

    private async Task<bool> Delay(TimeSpan interval)
    {
        try { await Task.Delay(interval, clock, stop.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }
}

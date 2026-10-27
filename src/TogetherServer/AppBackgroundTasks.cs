namespace TogetherServer;

// Owns the app's periodic work and cancellation as one lifecycle component.
// Each loop isolates failures so a transient probe cannot stop the other loops.
internal sealed class AppBackgroundTasks(
    FriendService friend, HostManager manager, CompanionServer companionServer,
    AppUpdater updater, AppInstance instance, LocalData data, DesktopWindow? desktop,
    Func<bool> friendMode, TimeProvider? clock = null, WorldPeerService? worldPeers = null) : IAsyncDisposable
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<Guid> notifiedActivity = [];
    private Task[] tasks = [];
    private DateTimeOffset notificationStartedUtc;
    private Task freshness = Task.CompletedTask;
    private readonly SemaphoreSlim replicationWake = new(0, 1);

    private void WakeReplication()
    {
        try { replicationWake.Release(); }
        catch (SemaphoreFullException) { /* One pending wake already covers it. */ }
        catch (ObjectDisposedException) { /* A publisher can race app shutdown. */ }
    }

    public void Start()
    {
        if (tasks.Length != 0) throw new InvalidOperationException("Background work is already running.");
        notificationStartedUtc = clock.GetUtcNow();
        if (worldPeers is not null) worldPeers.BackgroundSyncRequested += WakeReplication;
        tasks = [Task.Run(RunFriendPollAsync), Task.Run(RunSharedCatchUpAsync), Task.Run(RunLifecycleAsync),
            Task.Run(RunUpdateChecksAsync), Task.Run(RunNotificationsAsync)];
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (worldPeers is not null) worldPeers.BackgroundSyncRequested -= WakeReplication;
        try
        {
            await Task.WhenAll(tasks);
            await Task.WhenAll(friend.WaitForSharedCatchUpAsync(),
                worldPeers?.WaitForSyncAsync() ?? Task.CompletedTask, freshness);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { replicationWake.Dispose(); stop.Dispose(); }
    }

    private async Task RunFriendPollAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await friend.PollAsync();
            }
            catch (Exception ex) { DiagnosticOutput.WriteError("Friend poll failed: " + ex.GetType().Name); }
            if (!await Delay(TimeSpan.FromSeconds(5))) break;
        }
    }

    private async Task RunSharedCatchUpAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            // Save discovery/delivery must keep running while a saved Host's
            // heartbeat is slow or unavailable, including after a local Stop.
            try { worldPeers?.ScheduleSync(stop.Token); }
            catch (Exception ex) { DiagnosticOutput.WriteError("Peer scheduling failed: " + ex.GetType().Name); }
            try { friend.ScheduleSharedCatchUp(stop.Token, worldPeers is null ? null : worldPeers.OwnsReplication); }
            catch (Exception ex) { DiagnosticOutput.WriteError("Shared catch-up scheduling failed: " + ex.GetType().Name); }
            // Legacy links have their own due times; this tick does no network
            // work for idle peers. Publication wakes the same owner immediately.
            try
            {
                var delay = worldPeers?.NextBackgroundSyncDelay ?? TimeSpan.FromSeconds(5);
                if (delay > TimeSpan.FromSeconds(5)) delay = TimeSpan.FromSeconds(5);
                await replicationWake.WaitAsync(delay, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
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
            freshness = manager.ScheduleSharedFreshness(stop.Token);
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
                            item.Severity is ActivitySeverity.Important or ActivitySeverity.Warning &&
                            !PlayerCountRefreshState.IsRoutineCountActivity(item.Category, item.Action))
                        .DistinctBy(item => item.Id)
                        .OrderBy(item => item.OccurredUtc)
                        .ToList();
                    foreach (var item in visible.Where(item => notifiedActivity.Add(item.Id)))
                        desktop.Notify(item, friendMode(), friendMode() ? friend.View().ConnectionId : null);
                    var hostSnapshot = await manager.CompanionSnapshotAsync();
                    desktop.SetTraySummary(new DesktopTraySummary(
                        hostSnapshot.Runs.Count(run => run.State is "Ready" or "Starting" or "Process running" or "Listening" or "Stopping"),
                        hostSnapshot.Runs.Count(run => run.State == "Failed" || run.State == "Unknown" &&
                            !PlayerCountRefreshState.IsCountObservationUnavailable(run.State, run.PlayerObservationSource)),
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

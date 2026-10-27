namespace TogetherServer;

internal enum PeerMembershipRefreshPending { RosterCatchUpPending, RosterUnavailable }

// A new connection/policy observation may cross another PC's enrollment.
// Retry that bounded unfinished exchange promptly without turning idle or
// offline discovery into a permanent fast poll.
internal sealed class PeerMembershipRetryBurst(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object sync = new();
    private long began;
    private int retries;
    private bool active;
    internal void Begin()
    {
        lock (sync) { began = time.GetTimestamp(); retries = 0; active = true; }
    }
    internal TimeSpan? NextDelay(bool pending)
    {
        lock (sync)
        {
            if (!pending || !active || retries >= 8 || time.GetElapsedTime(began) >= TimeSpan.FromSeconds(25)) return null;
            retries++;
            return TimeSpan.FromSeconds(2);
        }
    }
}

internal sealed partial class WorldPeerService
{
    private readonly PeerReplicationSchedule replicationSchedule = new();
    private readonly HostingWorkCounters hostingWork = new();
    private long requestedSyncGeneration;
    private long scheduledSyncGeneration = -1;
    private DateTimeOffset nextBackgroundSyncUtc;
    private int remainingGroupSweep = -1;
    private SharedWorldPeerRoot[] replicationOwnedRoots = [];
    private readonly Dictionary<(string World, string Member), long> inboundContacts = [];
    private string? friendMembershipObservation;
    private readonly PeerMembershipRetryBurst membershipRetry = new();
    private int membershipRefreshPending;
    internal event Action? BackgroundSyncRequested;

    internal HostingWorkSnapshot WorkSnapshot()
    {
        var peer = hostingWork.Snapshot();
        return peer with
        {
            Work = peer.Work.Where(item => item.Stage != nameof(HostingWorkStage.WorkingVerification))
            .Append(manager.WorkingVerificationMeasurement()).ToArray()
        };
    }

    // The canonical heartbeat owner supplies this snapshot. UI reads never
    // call it. Pairing, scoped assignments, sharing-policy notices and reconnects
    // invalidate enrollment scheduling; player/time/progress polls do not.
    internal bool ObserveFriendSnapshot(FriendView snapshot)
    {
        var material = System.Text.Json.JsonSerializer.Serialize((snapshot.Connections ?? [snapshot])
            .OrderBy(connection => connection.ConnectionId).Select(connection => new
            {
                connection.ConnectionId,
                connection.HostId,
                connection.Endpoint,
                connection.State,
                connection.ConnectionCode,
                Capabilities = connection.HostCapabilities.Order(StringComparer.Ordinal),
                Profiles = connection.Profiles.OrderBy(profile => profile.Id).Select(profile => new { profile.Id, profile.Kind }),
                Sharing = (connection.Activity ?? []).Where(activity =>
                    activity.Category == "Backup" && activity.Action is "SharedSavesEnabled" or "SharedSavesDisabled" ||
                    activity.Category == "Lifecycle" && activity.Action == "Stopped" &&
                    activity.ProfileId is { } profileId && connection.Profiles.Any(profile => profile.Id == profileId))
                    .Select(activity => activity.Id).Order()
            }));
        if (material == friendMembershipObservation) return false;
        friendMembershipObservation = material;
        membershipRetry.Begin();
        RequestBackgroundSync();
        return true;
    }

    // Invoked under the Friend link's gate. Do not acquire stateSync or start
    // another exchange here; the current registration pass owns this result.
    private void ObservePendingMembershipRefresh(PeerMembershipRefreshPending _) =>
        Interlocked.Exchange(ref membershipRefreshPending, 1);

    // Suppress only the duplicate legacy background receiver for an enrolled
    // exact world. This published registry snapshot grants no receive access.
    internal bool OwnsReplication(SharedWorldPeerRoot root)
    {
        return Volatile.Read(ref replicationOwnedRoots).Contains(root);
    }

    private void RequestBackgroundSync()
    {
        Interlocked.Increment(ref requestedSyncGeneration);
        try { BackgroundSyncRequested?.Invoke(); }
        catch (Exception) { /* A wake cannot fail a committed publication/membership change. */ }
    }

    private void ObserveInboundContact(string world, string member)
    {
        lock (stateSync)
        {
            var key = (world, member);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var reconnect = !inboundContacts.TryGetValue(key, out var previous) ||
                System.Diagnostics.Stopwatch.GetElapsedTime(previous, now) >= TimeSpan.FromMinutes(2);
            if (!inboundContacts.ContainsKey(key) && inboundContacts.Count >= 256)
                inboundContacts.Remove(inboundContacts.MinBy(item => item.Value).Key);
            inboundContacts[key] = now;
            if (reconnect) RequestBackgroundSync();
        }
    }

    internal TimeSpan NextBackgroundSyncDelay
    {
        get
        {
            lock (stateSync)
                return scheduledSyncGeneration != Interlocked.Read(ref requestedSyncGeneration)
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(Math.Clamp((nextBackgroundSyncUtc - DateTimeOffset.UtcNow).TotalSeconds, 1, 60));
        }
    }

    private string ScheduleKey(Group group, SharedWorldPeerRoute route) => group.Root.GroupKey + ":" + RouteKey(route);

    private void RecordScheduledResult(Group group, SharedWorldPeerRoute route, long generation, bool failed)
    {
        var result = transferObservations.Read(group.Root.GroupKey, RouteKey(route))?.LastResult;
        var pending = result?.Result is "ByteBudget" or "WorkBudget" or "SeedHistoryPending" or "SeedPaused" ||
            result is { Error: "Canceled" } && (result.SubmittedBytes > 0 || result.ReceivedBytes > 0);
        lock (stateSync)
            replicationSchedule.Completed(ScheduleKey(group, route), generation, DateTimeOffset.UtcNow,
                failed && !pending, pending, Random.Shared.NextDouble());
    }
}

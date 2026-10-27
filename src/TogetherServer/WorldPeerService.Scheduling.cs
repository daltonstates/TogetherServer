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
    private long requestedMembershipGeneration;
    private long startedMembershipGeneration = -1;
    private DateTimeOffset nextMembershipRetryUtc = DateTimeOffset.MaxValue;
    private DateTimeOffset nextMembershipNetworkUtc;
    private bool membershipRefreshIncludesFriends;
    private DateTimeOffset nextBackgroundSyncUtc;
    private int remainingGroupSweep = -1;
    private SharedWorldPeerRoot[] replicationOwnedRoots = [];
    private sealed record ReplicationRouteScope(SharedWorldPeerRoot Root, Guid DeviceId, string RosterSignature);
    private readonly Dictionary<string, ReplicationRouteScope> learnedReplicationRoutes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string World, string Member), long> inboundContacts = [];
    private string? friendMembershipObservation;
    private readonly PeerMembershipRetryBurst membershipRetry = new();
    private int membershipRefreshPending;
    private long membershipPassesStarted, membershipPassesCompleted, membershipPassBegan, membershipPassElapsed;
    private long membershipLookupsStarted, membershipLookupsCompleted;
    private int membershipPassOutcome, membershipLookupOutcome, membershipLookupReturnedRoster;
    internal event Action? BackgroundSyncRequested;

    internal HostingWorkSnapshot WorkSnapshot()
    {
        var peer = hostingWork.Snapshot();
        return peer with
        {
            Membership = new(Interlocked.Read(ref membershipPassesStarted), Interlocked.Read(ref membershipPassesCompleted),
                ((PeerMembershipPassOutcome)Volatile.Read(ref membershipPassOutcome)).ToString(),
                Interlocked.Read(ref membershipPassesStarted) > Interlocked.Read(ref membershipPassesCompleted)
                    ? (long)System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref membershipPassBegan)).TotalMilliseconds
                    : Interlocked.Read(ref membershipPassElapsed),
                Interlocked.Read(ref membershipLookupsStarted), Interlocked.Read(ref membershipLookupsCompleted),
                ((PeerMembershipLookupOutcome)Volatile.Read(ref membershipLookupOutcome)).ToString(),
                Volatile.Read(ref membershipLookupReturnedRoster) != 0),
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
        Interlocked.Increment(ref requestedMembershipGeneration);
        membershipRetry.Begin();
        RequestBackgroundSync();
        return true;
    }

    // Invoked under the Friend link's gate. Do not acquire stateSync or start
    // another exchange here; the current registration pass owns this result.
    private void ObservePendingMembershipRefresh(PeerMembershipRefreshPending _) =>
        Interlocked.Exchange(ref membershipRefreshPending, 1);

    // Identifier-free observations only. These run under a Friend link gate
    // and must neither wait for world state nor authorize/retry an operation.
    private void ObserveMembershipLookup(PeerMembershipLookupOutcome outcome, bool returnedRoster)
    {
        if (outcome == PeerMembershipLookupOutcome.Started) Interlocked.Increment(ref membershipLookupsStarted);
        else Interlocked.Increment(ref membershipLookupsCompleted);
        Volatile.Write(ref membershipLookupReturnedRoster, returnedRoster ? 1 : 0);
        Volatile.Write(ref membershipLookupOutcome, (int)outcome);
    }

    // Suppress a duplicate receiver only when an enrolled exact world has a
    // legal peer transport route. Registration alone cannot replace a legacy
    // loopback connection. This memory-only hint grants no receiving access.
    internal bool OwnsReplication(SharedWorldPeerRoot root)
    {
        return !Volatile.Read(ref peerDisposed) && Volatile.Read(ref replicationOwnedRoots).Contains(root);
    }

    // Called under stateSync after registry validation or a route projection.
    // Registry/source/roster changes invalidate learned-route hints explicitly;
    // a retained bootstrap is already pinned by its enrollment boundary.
    private void PublishReplicationOwnership(IReadOnlyList<Group> groups)
    {
        foreach (var pair in learnedReplicationRoutes.ToArray())
            if (!groups.Any(group => group.Root == pair.Value.Root && group.DeviceId == pair.Value.DeviceId &&
                group.Roster.Signature == pair.Value.RosterSignature)) learnedReplicationRoutes.Remove(pair.Key);
        Volatile.Write(ref replicationOwnedRoots, groups.Where(group =>
            group.Bootstrap is { } bootstrap && SharedWorldPeerTrust.ValidRoute(bootstrap) ||
            learnedReplicationRoutes.ContainsKey(group.Root.GroupKey)).Select(group => group.Root).ToArray());
    }

    private void ObserveReplicationRoutes(Group expected, bool hasApprovedLearnedRoute)
    {
        lock (stateSync)
        {
            var current = Find(expected.Root.GroupKey);
            if (hasApprovedLearnedRoute && current is not null && current.Root == expected.Root &&
                current.DeviceId == expected.DeviceId && current.Roster.Signature == expected.Roster.Signature)
                learnedReplicationRoutes[expected.Root.GroupKey] = new(expected.Root, expected.DeviceId, expected.Roster.Signature);
            else learnedReplicationRoutes.Remove(expected.Root.GroupKey);
            PublishReplicationOwnership(verifiedRegistry?.Groups ?? []);
        }
    }

    private void ForgetReplicationRoutes(SharedWorldPeerRoot root)
    {
        lock (stateSync)
        {
            learnedReplicationRoutes.Remove(root.GroupKey);
            PublishReplicationOwnership(verifiedRegistry?.Groups ?? []);
        }
    }

    // Preserve the existing signed-contact discovery behavior, while only a
    // currently approved non-self contact may replace the legacy receiver.
    // Read/verify the bounded contact set once per scheduled route projection.
    private (SharedWorldPeerRoute[] All, SharedWorldPeerRoute[] Approved) ReplicationContactRoutes(Group group, string localKey)
    {
        var bytes = data.LoadProtected($"world-peer-contacts-{group.Root.GroupKey}.protected");
        if (bytes is null) return ([], []);
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("Peer contacts need review.");
        var cards = System.Text.Json.JsonSerializer.Deserialize<WorldPeerContactCard[]>(bytes, Json);
        if (cards is null || cards.Length > 128) throw new InvalidDataException("Peer contacts need review.");
        var verified = cards.Where(card => ValidContact(group, card)).ToArray();
        return (verified.SelectMany(card => card.Routes).Distinct().ToArray(),
            verified.Where(card => card.PublicKey != localKey && Access(group, card.DeviceId, card.PublicKey) is not null)
                .SelectMany(card => card.Routes).Distinct().ToArray());
    }

    private void RequestBackgroundSync()
    {
        Interlocked.Increment(ref requestedSyncGeneration);
        WakeBackgroundScheduler();
    }

    private void WakeBackgroundScheduler()
    {
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
            {
                var now = DateTimeOffset.UtcNow;
                var due = scheduledSyncGeneration != Interlocked.Read(ref requestedSyncGeneration)
                    ? now.AddSeconds(1) : nextBackgroundSyncUtc;
                if (nextMembershipRetryUtc < due) due = nextMembershipRetryUtc;
                if (nextMembershipNetworkUtc < due) due = nextMembershipNetworkUtc;
                return TimeSpan.FromSeconds(Math.Clamp((due - now).TotalSeconds, 1, 60));
            }
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

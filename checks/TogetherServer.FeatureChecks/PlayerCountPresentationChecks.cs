using System.Reflection;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure completed-observation histories and wire projections. No process, socket,
// app entry point, dialog, game installation or real save is opened.
internal static class PlayerCountPresentationChecks
{
    internal static void Run()
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        foreach (var source in new[] { "ObservationPending", "ObservationStale", "HealthProbeFailed", "CustomStatusTimedOut" })
            Require(PlayerCountRefreshState.IsCountObservationUnavailable("Unknown", source) &&
                !PlayerCountRefreshState.IsCountObservationUnavailable("Failed", source),
                "a routine count state became a tray warning or hid an actual server failure");
        foreach (var source in new string?[] { "IdentityUnknown", "CustomScriptsUnavailable", null, "UnrecognizedDriverFailure" })
            Require(!PlayerCountRefreshState.IsCountObservationUnavailable("Unknown", source),
                "an identity, setup or unrecognized server problem was hidden as a count request");
        foreach (var action in new[] { "CountIncreased", "CountDecreased", "CountObserved", "CountUnavailable" })
            Require(PlayerCountRefreshState.IsRoutineCountActivity("Players", action),
                "an older Host count event could create a new native alert");
        Require(PlayerCountRefreshState.IsRoutineCountActivity("Countdown", "Started") &&
            PlayerCountRefreshState.IsRoutineCountActivity("Countdown", "Canceled"),
            "automatic countdown churn could create native alerts");
        foreach (var action in new[] { "AutomaticStopFailed", "AutomaticStopCompleted", "FriendExtended", "UnrecognizedFailure" })
            Require(!PlayerCountRefreshState.IsRoutineCountActivity("Countdown", action) &&
                !PlayerCountRefreshState.IsRoutineCountActivity("Lifecycle", action),
                "an actual lifecycle result or explicit timer action lost its notification");
        var operationId = Guid.NewGuid();
        var unknown = new GameHealthResult(true, "MinecraftLocalStatus", "Ready", "No valid count.");
        var trusted = unknown with { OnlinePlayers = 0, PlayerCountTrusted = true };
        var first = new PlayerCountRefreshState(operationId, Refreshing: true).Completed(unknown, wasReady: true);
        Require(!first.Refreshing && first.FailureCount == 1,
            "one completed failed observation became a repeated issue");
        var inFlight = first with { Refreshing = true };
        Require(inFlight.FailureCount == 1,
            "starting an asynchronous retry increased completed failures");
        var repeated = inFlight.Completed(unknown, wasReady: true);
        Require(!repeated.Refreshing && repeated.FailureCount == PlayerCountRefreshState.WarningThreshold,
            "a second completed failed observation did not become a repeated issue");
        var recovered = repeated.Completed(trusted, wasReady: true);
        Require(recovered.FailureCount == 0 && recovered.Completed(unknown, true).FailureCount == 1,
            "a successful observation did not reset the consecutive failure history");
        Require(new PlayerCountRefreshState(Guid.NewGuid()).FailureCount == 0,
            "a new run inherited another run's player-count failure history");
        Require(new PlayerCountRefreshState(operationId).Completed(unknown with { State = "Starting" }, false).FailureCount == 0 &&
            repeated.Completed(unknown with { Code = "CustomScriptReady" }, true).FailureCount == 0 &&
            repeated.Completed(unknown with { State = "Listening" }, false).FailureCount == 0,
            "normal startup or a display-only driver was treated as a recurring count error");
        Require(first.Completed(unknown with { State = "Unknown", Code = "HealthProbeFailed", Ok = false }, true).FailureCount == 2 &&
            first.Completed(unknown with { OnlinePlayers = 0, PlayerCountTrusted = false }, true).FailureCount == 2,
            "a failed probe or untrusted zero cleared a real failure streak");
        var capped = new PlayerCountRefreshState(operationId, true, int.MaxValue).Completed(unknown, true);
        Require(capped.FailureCount == int.MaxValue, "the completed failure counter overflowed");

        var now = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        var readyObservation = new ServerObservation(Guid.NewGuid(), operationId, "Ready", "Count is current.",
            "MinecraftLocalStatus", now, true, 0, PlayerCountTrusted: true);
        foreach (var wait in new[]
        {
            unknown with { State = "Unknown", Code = "HealthProbeFailed", Ok = false },
            unknown with { State = "Unknown", Code = "CustomStatusTimedOut", Ok = false },
            unknown with { State = "Starting", Code = "MinecraftStarting", Ok = false },
            unknown with { State = "Starting", Code = "FactorioStarting", Ok = false },
            unknown with { State = "Starting", Code = "ValheimStarting", Ok = false }
        })
        {
            Require(!PlayerCountRefreshState.ShouldRecordLifecycleChange(readyObservation, wait, true, 0) &&
                !PlayerCountRefreshState.ShouldRecordLifecycleChange(readyObservation, wait, true, 2),
                "a waiting count request created a competing lifecycle event instead of the threshold count warning");
            var waitingObservation = readyObservation with { State = wait.State, Source = wait.Code };
            Require(!PlayerCountRefreshState.ShouldRecordLifecycleChange(waitingObservation, trusted, true, 1) &&
                !PlayerCountRefreshState.ShouldRecordLifecycleChange(waitingObservation, trusted, true, 2),
                "count recovery created lifecycle feed or notification churn");
        }
        Require(PlayerCountRefreshState.ShouldRecordLifecycleChange(readyObservation,
                unknown with { State = "Failed", Code = "GameFailed", Ok = false }, true, 0) &&
            PlayerCountRefreshState.ShouldRecordLifecycleChange(readyObservation,
                unknown with { State = "Unknown", Code = "IdentityUnknown", Ok = false }, true, 0) &&
            PlayerCountRefreshState.ShouldRecordLifecycleChange(readyObservation,
                unknown with { State = "Unknown", Code = "CustomScriptsUnavailable", Ok = false }, true, 0),
            "a genuine game, identity, or protected-setup failure was hidden as a count lookup");
        var startingObservation = readyObservation with { State = "Starting", Source = "MinecraftStarting" };
        Require(PlayerCountRefreshState.ShouldRecordLifecycleChange(startingObservation, trusted, false, 0),
            "the first observed readiness was hidden as a routine count recovery");
        Require(new PlayerCountRefreshState(operationId).Completed(
                unknown with { State = "Unknown", Code = "HealthProbeFailed", Ok = false }, false).FailureCount == 1,
            "repeated failed status requests during initial startup could never become a count warning");

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var run = new RunView(Guid.NewGuid(), "Ready", "Count unavailable", 123,
            PlayerCountRefreshing: true, PlayerCountFailureCount: 1);
        var projected = JsonSerializer.Deserialize<RunView>(JsonSerializer.SerializeToUtf8Bytes(run, json), json)!;
        Require(projected.PlayerCountRefreshing && projected.PlayerCountFailureCount == 1 &&
            projected.OnlinePlayers is null && !projected.PlayerCountTrusted && projected.AutoShutdownAtUtc is null,
            "presentation metadata supplied count or countdown authority");
        var legacy = JsonSerializer.Deserialize<PublicProfile>("""
            {"id":"11111111-1111-4111-8111-111111111111","name":"Synthetic","state":"Ready","joinAddress":null}
            """, json)!;
        Require(!legacy.PlayerCountRefreshing && legacy.PlayerCountFailureCount == 0 &&
            legacy.PlayerCountTrusted is null && legacy.PlayerCountObservedUtc is null,
            "a legacy peer fabricated count history or count evidence");
    }

    internal static void RunQuietCountdown(string root)
    {
        // Empty synthetic storage only. Calling the cancellation method directly
        // exercises timer/credit history without querying or launching a process.
        using var data = new LocalData(Path.Combine(root, "quiet-countdown-" + Guid.NewGuid().ToString("N")));
        var manager = new HostManager(data);
        T Field<T>(string name) => (T)typeof(HostManager)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        var deadlines = Field<Dictionary<Guid, DateTimeOffset>>("shutdownDeadlines");
        var addedMinutes = Field<Dictionary<Guid, long>>("addedShutdownMinutes");
        var friendMinutes = Field<Dictionary<Guid, int>>("friendAddedMinutes");
        var cancel = typeof(HostManager).GetMethod("CancelCountdown", BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var reason in new[]
        {
            "The current player count became unknown.",
            "The player count became unavailable.",
            "A player joined the server."
        })
        {
            var profileId = Guid.NewGuid();
            deadlines[profileId] = DateTimeOffset.UtcNow.AddMinutes(10);
            addedMinutes[profileId] = 15;
            friendMinutes[profileId] = 5;
            cancel.Invoke(manager, [profileId, reason, false]);
            if (deadlines.ContainsKey(profileId) || addedMinutes[profileId] != 15 || friendMinutes[profileId] != 5)
                throw new Exception("Quiet count cancellation retained a live deadline or lost saved added time.");
            var events = data.LoadActivity().Where(item => item.ProfileId == profileId).ToList();
            if (events is not [{ Category: "Countdown", Action: "Canceled", Severity: ActivitySeverity.Info,
                Visibility: ActivityVisibility.AssignedFriends }] ||
                !events[0].Message.Contains("15 added minutes remain saved", StringComparison.Ordinal))
                throw new Exception("A routine count change lost its history or generated an attention event.");
            cancel.Invoke(manager, [profileId, reason, false]);
            if (data.LoadActivity().Count(item => item.ProfileId == profileId) != 1)
                throw new Exception("Polling an already-canceled timer repeated its activity event.");
        }

        var discardedId = Guid.NewGuid();
        deadlines[discardedId] = DateTimeOffset.UtcNow.AddMinutes(10);
        addedMinutes[discardedId] = 15;
        friendMinutes[discardedId] = 5;
        cancel.Invoke(manager, [discardedId, "Automatic shutdown was turned off.", true]);
        if (deadlines.ContainsKey(discardedId) || addedMinutes.ContainsKey(discardedId) || friendMinutes.ContainsKey(discardedId))
            throw new Exception("Quiet notifications changed explicit timer-credit discard behavior.");
    }
}

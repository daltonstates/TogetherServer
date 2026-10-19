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
}

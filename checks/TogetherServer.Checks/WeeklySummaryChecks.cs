using System.Text.Json;
using TogetherServer;

// Pure projection checks; safe for a harness that never launches process checks.
internal static class WeeklySummaryChecks
{
    internal static void Run()
    {
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        var profile = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-10-08T15:00:00Z");
        var start = now - TimeSpan.FromDays(7);
        ManagedRunArchive Session(DateTimeOffset from, DateTimeOffset to, ServerSessionOutcome outcome,
            int? peak, ServerSessionBackupResult backup = ServerSessionBackupResult.NotAttempted)
        {
            var reason = outcome switch
            {
                ServerSessionOutcome.GracefulStop => ServerSessionEndReason.GracefulStop,
                ServerSessionOutcome.RecoveryFailedBeforeReady => ServerSessionEndReason.RecoveryProcessExitedBeforeReady,
                _ => ServerSessionEndReason.ProcessExited
            };
            return new(profile, Guid.NewGuid(), GameKinds.Fixture, "synthetic", null, null,
                outcome is not (ServerSessionOutcome.FailedBeforeReady or ServerSessionOutcome.RecoveryFailedBeforeReady),
                "No logs are mined", to, false, HostManager.CurrentSessionSummaryVersion, from, to,
                (long)Math.Floor((to - from).TotalSeconds), reason, outcome, peak is null ? null : 0, peak, backup);
        }
        var clipped = Session(start - TimeSpan.FromHours(2), start + TimeSpan.FromHours(1), ServerSessionOutcome.GracefulStop,
            99, ServerSessionBackupResult.Completed);
        var overlap = Session(start + TimeSpan.FromMinutes(30), start + TimeSpan.FromHours(2), ServerSessionOutcome.UnexpectedExit, 4);
        var failure = Session(now - TimeSpan.FromMinutes(10), now - TimeSpan.FromMinutes(5), ServerSessionOutcome.FailedBeforeReady, null);
        var backupFailure = Session(now - TimeSpan.FromMinutes(4), now - TimeSpan.FromMinutes(2), ServerSessionOutcome.GracefulStop,
            0, ServerSessionBackupResult.Failed);
        var legacy = new ManagedRunArchive(profile, Guid.NewGuid(), GameKinds.Fixture, "synthetic", null, null, true,
            "legacy", now - TimeSpan.FromDays(1), false);
        var outside = Session(start - TimeSpan.FromHours(3), start - TimeSpan.FromHours(2), ServerSessionOutcome.UnexpectedExit, 100);
        var another = Session(now - TimeSpan.FromHours(1), now, ServerSessionOutcome.UnexpectedExit, 200) with { ProfileId = Guid.NewGuid() };
        var ongoing = new ManagedRun { ProfileId = profile, OperationId = Guid.NewGuid(), StartTimeUtcTicks = start.UtcTicks,
            WasReady = true, MaximumTrustedOnlinePlayers = 800, LastTrustedOnlinePlayers = 0 };
        var records = new List<ManagedRunArchive> { clipped, overlap, failure, backupFailure, legacy, outside, another };
        var summary = HostManager.SummarizeWeeklySessions(profile, now, records, [ongoing]);
        Require(summary.WindowStartUtc == start && summary.WindowEndUtc == now && summary.RecordedRuntimeSeconds == 2 * 3600 + 7 * 60,
            "runtime was not clipped, merged, or scoped to exactly seven days");
        Require(summary is { ArchivedSessionCount: 5, CompletedSessionCount: 4, FailedStarts: 1, UnexpectedExits: 1,
                RollingBackupsCompleted: 1, RollingBackupsFailed: 1, RollingBackupsNotAttempted: 2,
                PeakTrustedOnlinePlayers: 4, UnavailableSessionCount: 1, ClippedSessionCount: 1,
                OverlappingSessionCount: 1, UnfinishedRunCount: 1, SessionsWithoutTrustedCounts: 1 },
            "weekly outcome, backup, peak, or explicit gap metrics were wrong");
        var roundTrip = JsonSerializer.Deserialize<List<ManagedRunArchive>>(JsonSerializer.Serialize(records))!;
        Require(HostManager.SummarizeWeeklySessions(profile, now, roundTrip, [ongoing]) == summary,
            "summary evidence changed after archive persistence roundtrip");

        var unavailable = overlap with { DurationSeconds = 1 };
        var damaged = HostManager.SummarizeWeeklySessions(profile, now, [unavailable, clipped], []);
        Require(damaged is { RecordedRuntimeSeconds: 3600, UnavailableSessionCount: 1, PeakTrustedOnlinePlayers: null },
            "noncanonical timing or an untimed clipped peak contributed a metric");
        var duplicated = HostManager.SummarizeWeeklySessions(profile, now, [overlap, overlap], []);
        Require(duplicated is { RecordedRuntimeSeconds: 0, UnavailableSessionCount: 1, CompletedSessionCount: 0 },
            "duplicate operation records were counted as usable sessions");
        var unavailableTimes = legacy with { OperationId = Guid.NewGuid(), ArchivedUtc = default };
        Require(HostManager.SummarizeWeeklySessions(profile, now, [unavailableTimes], [ongoing]) is
            { UndatedArchiveRecordCount: 1, RecordedRuntimeSeconds: 0, PeakTrustedOnlinePlayers: null, UnfinishedRunCount: 1 },
            "undated or unfinished records manufactured runtime or count observations");
        var atBoundary = Session(start, start, ServerSessionOutcome.FailedBeforeReady, null);
        Require(HostManager.SummarizeWeeklySessions(profile, now, [atBoundary], []) is
            { RecordedRuntimeSeconds: 0, FailedStarts: 1, CompletedSessionCount: 1 }, "the exact week boundary was lost");
        var fullWeek = Session(start - TimeSpan.FromDays(2), now, ServerSessionOutcome.GracefulStop, 99,
            ServerSessionBackupResult.NotConfigured);
        Require(HostManager.SummarizeWeeklySessions(profile, now, [fullWeek], []) is
            { RecordedRuntimeSeconds: 604800, ClippedSessionCount: 1, PeakTrustedOnlinePlayers: null, RollingBackupsNotConfigured: 1 },
            "a long archived run exceeded the window or attributed its untimed peak");
        var capped = Enumerable.Range(0, 501).Select(index => atBoundary with
        {
            OperationId = Guid.NewGuid(), ArchivedUtc = start + TimeSpan.FromSeconds(index),
            StartedUtc = start + TimeSpan.FromSeconds(index), EndedUtc = start + TimeSpan.FromSeconds(index)
        }).ToList();
        Require(HostManager.SummarizeWeeklySessions(profile, now, capped, []) is
            { ArchiveLimitReached: true, ArchivedSessionCount: 500, CompletedSessionCount: 500 },
            "the retained archive bound was hidden or exceeded");
        var serialized = JsonSerializer.Serialize(summary);
        Require(!serialized.Contains("synthetic", StringComparison.Ordinal) && !serialized.Contains("No logs", StringComparison.Ordinal) &&
                !serialized.Contains(ongoing.OperationId.ToString(), StringComparison.OrdinalIgnoreCase),
            "the summary leaked raw archive data or run identity");
    }
}

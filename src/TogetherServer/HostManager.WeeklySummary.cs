namespace TogetherServer;

public sealed record WeeklyServerSummaryResult(bool Ok, string Code, string Message, Guid ProfileId,
    WeeklyServerSummary? Summary = null);

public sealed record WeeklySessionEvidence(DateTimeOffset ArchivedUtc, RecentServerSession Session);

public sealed record WeeklyRuntimeDay(DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    long? RecordedRuntimeSeconds, int TimedSessionCount);

public sealed record WeeklyServerSummary(DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc,
    long RecordedRuntimeSeconds, int ArchivedSessionCount, int CompletedSessionCount,
    int FailedStarts, int UnexpectedExits, int RollingBackupsCompleted, int RollingBackupsFailed,
    int RollingBackupsNotConfigured, int RollingBackupsUnsupported, int RollingBackupsNotAttempted,
    int? PeakTrustedOnlinePlayers, int SessionsWithoutTrustedCounts, int UnavailableSessionCount,
    int ClippedSessionCount, int OverlappingSessionCount, int UnfinishedRunCount,
    int UndatedArchiveRecordCount, bool ArchiveLimitReached,
    IReadOnlyList<WeeklyRuntimeDay>? RuntimeByDay = null,
    IReadOnlyList<WeeklySessionEvidence>? Sessions = null);

public sealed partial class HostManager
{
    public async Task<WeeklyServerSummaryResult> WeeklySummaryAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            if (!settings.Profiles.Any(item => item.Id == profileId))
                return new(false, "UnknownProfile", "Choose a saved Host server.", profileId);
            var summary = SummarizeWeeklySessions(profileId, clock.GetUtcNow(), data.LoadRunArchive(), runs);
            return new(true, "WeeklyServerSummary", "Seven days of retained session evidence on this Host.", profileId, summary);
        }
        finally { gate.Release(); }
    }

    // A projection only: no process probing, log mining, storage mutation,
    // readiness decisions, occupancy permits, or lifecycle action.
    internal static WeeklyServerSummary SummarizeWeeklySessions(Guid profileId, DateTimeOffset now,
        IReadOnlyList<ManagedRunArchive> archive, IReadOnlyList<ManagedRun> unfinishedRuns)
    {
        var windowStart = now - TimeSpan.FromDays(7);
        var cutoff = now - TimeSpan.FromDays(30);
        var retained = archive.Where(item => item.ArchivedUtc >= cutoff && item.ArchivedUtc <= now &&
                item.OperationId != Guid.Empty)
            .OrderByDescending(item => item.ArchivedUtc).Take(500).ToList();
        var profileRecords = retained.Where(item => item.ProfileId == profileId).ToList();
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var evidence = new List<WeeklySessionEvidence>();
        var archived = 0;
        var completed = 0;
        var failedStarts = 0;
        var unexpectedExits = 0;
        var backupCompleted = 0;
        var backupFailed = 0;
        var backupOff = 0;
        var backupUnsupported = 0;
        var backupNotAttempted = 0;
        int? peak = null;
        var withoutCounts = 0;
        var unavailable = 0;
        var clipped = 0;
        foreach (var sameOperation in profileRecords.GroupBy(item => item.OperationId))
        {
            var item = sameOperation.First();
            var session = ToRecentServerSession(item);
            if (sameOperation.Count() != 1 || session.Outcome is null)
            {
                // Archive time locates an unavailable record, never its runtime,
                // outcome, player peak, or backup result.
                if (sameOperation.Any(record => record.ArchivedUtc >= windowStart))
                {
                    archived++;
                    unavailable++;
                    // A duplicate operation has no usable timing or outcome, even
                    // when either individual record would otherwise be complete.
                    evidence.Add(new(item.ArchivedUtc, session with
                    {
                        StartedUtc = null,
                        EndedUtc = null,
                        DurationSeconds = null,
                        ReadyEverObserved = null,
                        EndReason = null,
                        Outcome = null,
                        CrashRecoveryScheduled = null,
                        LastTrustedOnlinePlayers = null,
                        MaximumTrustedOnlinePlayers = null,
                        BackupResult = null
                    }));
                }
                continue;
            }
            var start = session.StartedUtc!.Value;
            var end = session.EndedUtc!.Value;
            if (end < windowStart || start > now) continue;
            archived++;
            evidence.Add(new(item.ArchivedUtc, session));
            var boundedStart = start < windowStart ? windowStart : start;
            var boundedEnd = end > now ? now : end;
            if (boundedStart < boundedEnd) intervals.Add((boundedStart, boundedEnd));
            if (start < windowStart)
                clipped++;
            else if (session.MaximumTrustedOnlinePlayers is { } recordedPeak)
                peak = peak is null ? recordedPeak : Math.Max(peak.Value, recordedPeak);
            if (end < windowStart || end > now) continue;
            completed++;
            if (session.Outcome is ServerSessionOutcome.FailedBeforeReady or ServerSessionOutcome.RecoveryFailedBeforeReady)
                failedStarts++;
            if (session.Outcome == ServerSessionOutcome.UnexpectedExit) unexpectedExits++;
            if (session.MaximumTrustedOnlinePlayers is null) withoutCounts++;
            switch (session.BackupResult)
            {
                case ServerSessionBackupResult.Completed: backupCompleted++; break;
                case ServerSessionBackupResult.Failed: backupFailed++; break;
                case ServerSessionBackupResult.NotConfigured: backupOff++; break;
                case ServerSessionBackupResult.Unsupported: backupUnsupported++; break;
                case ServerSessionBackupResult.NotAttempted: backupNotAttempted++; break;
            }
        }

        // Overlapping saved intervals are counted once, and remain visible as a
        // coverage warning. A summary cannot manufacture additional runtime.
        long runtimeTicks = 0;
        var overlapping = 0;
        DateTimeOffset? activeStart = null;
        DateTimeOffset? activeEnd = null;
        foreach (var interval in intervals.OrderBy(item => item.Start).ThenBy(item => item.End))
        {
            if (activeStart is null)
            {
                activeStart = interval.Start;
                activeEnd = interval.End;
            }
            else if (interval.Start <= activeEnd!.Value)
            {
                if (interval.Start < activeEnd.Value) overlapping++;
                if (interval.End > activeEnd.Value) activeEnd = interval.End;
            }
            else
            {
                runtimeTicks += (activeEnd!.Value - activeStart.Value).Ticks;
                activeStart = interval.Start;
                activeEnd = interval.End;
            }
        }
        if (activeStart is not null) runtimeTicks += (activeEnd!.Value - activeStart.Value).Ticks;
        return new(windowStart, now, runtimeTicks / TimeSpan.TicksPerSecond, archived, completed,
            failedStarts, unexpectedExits, backupCompleted, backupFailed, backupOff, backupUnsupported,
            backupNotAttempted, peak, withoutCounts, unavailable, clipped, overlapping,
            unfinishedRuns.Where(item => item.ProfileId == profileId && item.OperationId != Guid.Empty)
                .Select(item => item.OperationId).Distinct().Take(500).Count(),
            archive.Where(item => item.ProfileId == profileId &&
                (item.ArchivedUtc == default || item.ArchivedUtc > now)).Take(500).Count(),
            archive.Count >= 500,
            WeeklyRuntimeProjection.ByDay(windowStart, now, intervals), evidence);
    }
}

import type { RecentServerSession } from '../contracts'
import type { WeeklyServerSummaryResult } from '../weeklySummaryWire'

export const weeklyProfileId = '11111111-1111-4111-8111-111111111111'

function session(index: number, startedUtc: string, endedUtc: string,
  outcome: RecentServerSession['outcome'], maximumTrustedOnlinePlayers: number | null,
  backupResult: RecentServerSession['backupResult']): RecentServerSession {
  return { profileId: weeklyProfileId, operationId: `${String(index).padStart(8, '0')}-2222-4222-8222-222222222222`,
    gameKind: 'Valheim', startedUtc, endedUtc, durationSeconds: (Date.parse(endedUtc) - Date.parse(startedUtc)) / 1000,
    readyEverObserved: outcome !== 'FailedBeforeReady',
    endReason: outcome === 'GracefulStop' ? 'GracefulStop' : 'ProcessExited', outcome,
    crashRecoveryScheduled: false, lastTrustedOnlinePlayers: maximumTrustedOnlinePlayers === null ? null : 0,
    maximumTrustedOnlinePlayers, backupResult }
}

export function weeklySummaryFixture(): WeeklyServerSummaryResult {
  const legacy: RecentServerSession = { profileId: weeklyProfileId,
    operationId: '00000005-2222-4222-8222-222222222222', gameKind: 'Valheim',
    startedUtc: null, endedUtc: null, durationSeconds: null, readyEverObserved: null,
    endReason: null, outcome: null, crashRecoveryScheduled: null, lastTrustedOnlinePlayers: null,
    maximumTrustedOnlinePlayers: null, backupResult: null }
  return { ok: true, code: 'WeeklyServerSummary', message: 'Retained session evidence.', profileId: weeklyProfileId,
    summary: {
      windowStartUtc: '2026-10-01T15:00:00Z', windowEndUtc: '2026-10-08T15:00:00Z', recordedRuntimeSeconds: 7620,
      archivedSessionCount: 5, completedSessionCount: 4, failedStarts: 1, unexpectedExits: 1,
      rollingBackupsCompleted: 1, rollingBackupsFailed: 1, rollingBackupsNotConfigured: 0,
      rollingBackupsUnsupported: 0, rollingBackupsNotAttempted: 2, peakTrustedOnlinePlayers: 4,
      sessionsWithoutTrustedCounts: 1, unavailableSessionCount: 1, clippedSessionCount: 1, overlappingSessionCount: 1,
      unfinishedRunCount: 1, undatedArchiveRecordCount: 1, archiveLimitReached: false,
      runtimeByDay: Array.from({ length: 8 }, (_, index) => ({
        startUtc: index === 0 ? '2026-10-01T15:00:00Z' : `2026-10-0${index + 1}T00:00:00Z`,
        endUtc: index === 7 ? '2026-10-08T15:00:00Z' : `2026-10-0${index + 2}T00:00:00Z`,
        recordedRuntimeSeconds: index === 0 ? 7200 : index === 7 ? 420 : null,
        timedSessionCount: index === 0 || index === 7 ? 2 : 0
      })),
      sessions: [
        { archivedUtc: '2026-10-01T16:00:00Z', session: session(1, '2026-10-01T13:00:00Z', '2026-10-01T16:00:00Z', 'GracefulStop', 99, 'Completed') },
        { archivedUtc: '2026-10-01T17:00:00Z', session: session(2, '2026-10-01T15:30:00Z', '2026-10-01T17:00:00Z', 'UnexpectedExit', 4, 'NotAttempted') },
        { archivedUtc: '2026-10-08T14:55:00Z', session: session(3, '2026-10-08T14:50:00Z', '2026-10-08T14:55:00Z', 'FailedBeforeReady', null, 'NotAttempted') },
        { archivedUtc: '2026-10-08T14:58:00Z', session: session(4, '2026-10-08T14:56:00Z', '2026-10-08T14:58:00Z', 'GracefulStop', 0, 'Failed') },
        { archivedUtc: '2026-10-07T15:00:00Z', session: legacy }
      ]
    }
  }
}

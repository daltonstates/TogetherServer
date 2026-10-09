import { describe, expect, it } from 'vitest'
import { parseWeeklyServerSummary } from './weeklySummaryWire'
import { weeklySummaryFixture } from './test/weeklySummaryFixture'

describe('daily and contributing weekly evidence wire', () => {
  it('accepts a clipped union, retained legacy placement and null days without inventing runtime', () => {
    const result = weeklySummaryFixture()
    expect(parseWeeklyServerSummary(result)).toEqual(result)
    expect(result.summary!.runtimeByDay!.reduce((total, day) => total + (day.recordedRuntimeSeconds ?? 0), 0)).toBe(7620)
  })

  it('rejects fabricated days, missing-day zeroes, metric mismatches, wrong scope, duplicate runs and raw fields', () => {
    const mutations: Array<(result: ReturnType<typeof weeklySummaryFixture>) => void> = [
      result => { result.summary!.runtimeByDay![1].recordedRuntimeSeconds = 0 },
      result => { result.summary!.runtimeByDay![0].recordedRuntimeSeconds = 10800 },
      result => { result.summary!.runtimeByDay![0].timedSessionCount = 1 },
      result => { result.summary!.runtimeByDay![1].startUtc = '2026-10-02T01:00:00Z' },
      result => { result.summary!.runtimeByDay!.pop() },
      result => { result.summary!.runtimeByDay!.push(result.summary!.runtimeByDay![0]) },
      result => { result.summary!.recordedRuntimeSeconds = 9000 },
      result => { result.summary!.peakTrustedOnlinePlayers = 99 },
      result => { result.summary!.sessions![0].session.profileId = '99999999-9999-4999-8999-999999999999' },
      result => { result.summary!.sessions![1].session.operationId = result.summary!.sessions![0].session.operationId },
      result => { result.summary!.sessions![0].archivedUtc = '2026-10-01T15:59:59Z' },
      result => { result.summary!.sessions![4].archivedUtc = '2026-09-30T12:00:00Z' },
      result => { Object.assign(result.summary!.sessions![0].session, { password: 'PRIVATE_PASSWORD' }) },
      result => { Object.assign(result.summary!.runtimeByDay![0], { rawLog: 'PRIVATE_LOG' }) },
      result => { Object.assign(result.summary!.sessions![0], { endpoint: 'PRIVATE_ENDPOINT' }) },
      result => { result.summary!.sessions = undefined }
    ]
    for (const mutate of mutations) {
      const result = weeklySummaryFixture()
      mutate(result)
      expect(() => parseWeeklyServerSummary(result)).toThrow()
    }
  })

  it('retains source precision for a timed interval under one millisecond without claiming a missing day is zero', () => {
    const result = weeklySummaryFixture()
    const summary = result.summary!
    const source = summary.sessions![3]
    source.session.startedUtc = '2026-10-08T14:59:59.9999999Z'
    source.session.endedUtc = '2026-10-08T15:00:00.0000000Z'
    source.archivedUtc = '2026-10-08T15:00:00Z'
    source.session.durationSeconds = 0
    source.session.backupResult = 'NotConfigured'
    Object.assign(summary, { recordedRuntimeSeconds: 0, archivedSessionCount: 1, completedSessionCount: 1,
      failedStarts: 0, unexpectedExits: 0, rollingBackupsCompleted: 0, rollingBackupsFailed: 0,
      rollingBackupsNotConfigured: 1, rollingBackupsNotAttempted: 0, peakTrustedOnlinePlayers: 0,
      sessionsWithoutTrustedCounts: 0, unavailableSessionCount: 0, clippedSessionCount: 0,
      overlappingSessionCount: 0, unfinishedRunCount: 0, undatedArchiveRecordCount: 0, sessions: [source] })
    for (const [index, day] of summary.runtimeByDay!.entries()) {
      day.recordedRuntimeSeconds = index === 7 ? 0 : null
      day.timedSessionCount = index === 7 ? 1 : 0
    }
    expect(parseWeeklyServerSummary(result)).toEqual(result)
  })

  it('supports an older summary without daily or session detail', () => {
    const result = weeklySummaryFixture()
    delete result.summary!.runtimeByDay
    delete result.summary!.sessions
    expect(parseWeeklyServerSummary(result)).toEqual(result)
  })
})

import { ContractError, parseRecentServerSessions, type Decoder, type RecentServerSession } from './contracts'

export type WeeklyRuntimeDay = {
  startUtc: string
  endUtc: string
  recordedRuntimeSeconds: number | null
  timedSessionCount: number
}

export type WeeklySessionEvidence = { archivedUtc: string; session: RecentServerSession }

export type WeeklyServerSummary = {
  windowStartUtc: string
  windowEndUtc: string
  recordedRuntimeSeconds: number
  archivedSessionCount: number
  completedSessionCount: number
  failedStarts: number
  unexpectedExits: number
  rollingBackupsCompleted: number
  rollingBackupsFailed: number
  rollingBackupsNotConfigured: number
  rollingBackupsUnsupported: number
  rollingBackupsNotAttempted: number
  peakTrustedOnlinePlayers: number | null
  sessionsWithoutTrustedCounts: number
  unavailableSessionCount: number
  clippedSessionCount: number
  overlappingSessionCount: number
  unfinishedRunCount: number
  undatedArchiveRecordCount: number
  archiveLimitReached: boolean
  runtimeByDay?: WeeklyRuntimeDay[]
  sessions?: WeeklySessionEvidence[]
}

export type WeeklyServerSummaryResult = {
  ok: boolean
  code: string
  message: string
  profileId: string
  summary: WeeklyServerSummary | null
}

const countKeys = ['archivedSessionCount', 'completedSessionCount', 'failedStarts', 'unexpectedExits',
  'rollingBackupsCompleted', 'rollingBackupsFailed', 'rollingBackupsNotConfigured', 'rollingBackupsUnsupported',
  'rollingBackupsNotAttempted', 'sessionsWithoutTrustedCounts', 'unavailableSessionCount', 'clippedSessionCount',
  'overlappingSessionCount', 'unfinishedRunCount', 'undatedArchiveRecordCount'] as const

function fail(context: string): never { throw new ContractError(`${context}: invalid seven-day summary response`) }
function object(value: unknown, context: string, keys: readonly string[]) {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return fail(context)
  const result = value as Record<string, unknown>
  if (Object.keys(result).some(key => !keys.includes(key))) return fail(context)
  return result
}
function text(value: unknown, context: string, maximum = 400) {
  if (typeof value !== 'string' || value.length > maximum || /[\p{Cc}\u202a-\u202e\u2066-\u2069]/u.test(value)) return fail(context)
  return value
}
function integer(value: unknown, context: string, maximum: number) {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0 || value > maximum) return fail(context)
  return value
}
function timestamp(value: unknown, context: string) {
  const result = text(value, context, 64)
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/u.test(result) || !Number.isFinite(Date.parse(result))) return fail(context)
  return result
}

const sessionKeys = ['profileId', 'operationId', 'gameKind', 'startedUtc', 'endedUtc', 'durationSeconds',
  'readyEverObserved', 'endReason', 'outcome', 'crashRecoveryScheduled', 'lastTrustedOnlinePlayers',
  'maximumTrustedOnlinePlayers', 'backupResult'] as const

// Preserve the source's 100 ns precision when checking unions and daily totals.
// Date.parse alone truncates seven-digit .NET timestamps to milliseconds.
export function recordedTimestampTicks(value: string) {
  const fraction = /\.(\d{1,7})(?:Z|[+-]\d{2}:\d{2})$/u.exec(value)?.[1] ?? ''
  return BigInt(Date.parse(value)) * 10_000n + BigInt(fraction.padEnd(7, '0').slice(3))
}

const ticks = recordedTimestampTicks

type Interval = { start: bigint; end: bigint }
function union(intervals: Interval[]) {
  let total = 0n
  let overlaps = 0
  let current: Interval | null = null
  for (const interval of [...intervals].sort((left, right) => left.start < right.start ? -1 : left.start > right.start ? 1 :
    left.end < right.end ? -1 : left.end > right.end ? 1 : 0)) {
    if (current === null) current = { ...interval }
    else if (interval.start <= current.end) {
      if (interval.start < current.end) overlaps++
      if (interval.end > current.end) current.end = interval.end
    } else { total += current.end - current.start; current = { ...interval } }
  }
  if (current !== null) total += current.end - current.start
  return { seconds: Number(total / 10_000_000n), overlaps }
}

function parseHistory(summary: WeeklyServerSummary, source: Record<string, unknown>, profileId: string, context: string) {
  if ((source.runtimeByDay === undefined && source.sessions === undefined) ||
      (source.runtimeByDay === null && source.sessions === null)) return {}
  if (!Array.isArray(source.runtimeByDay) || source.runtimeByDay.length < 7 || source.runtimeByDay.length > 8 ||
      !Array.isArray(source.sessions) || source.sessions.length > 500) return fail(context)
  const start = ticks(summary.windowStartUtc)
  const end = ticks(summary.windowEndUtc)
  const seen = new Set<string>()
  const sessions = source.sessions.map((value, index): WeeklySessionEvidence => {
    const itemContext = `${context}.sessions[${index}]`
    const item = object(value, itemContext, ['archivedUtc', 'session'])
    const archivedUtc = timestamp(item.archivedUtc, itemContext)
    const archived = ticks(archivedUtc)
    if (archived < end - 30n * 86400n * 10_000_000n || archived > end) return fail(itemContext)
    const raw = object(item.session, itemContext, sessionKeys)
    const session = parseRecentServerSessions({ ok: true, code: 'RecentSessions', message: 'Retained session evidence.', profileId,
      sessions: [raw] }, itemContext).sessions[0]
    const identity = session.operationId.toLowerCase()
    if (seen.has(identity)) return fail(itemContext)
    seen.add(identity)
    if (session.outcome === null) {
      if (archived < start) return fail(itemContext)
    } else {
      const from = ticks(session.startedUtc!)
      const to = ticks(session.endedUtc!)
      if (to < start || from > end || to > archived || to < from ||
          session.durationSeconds !== Number((to - from) / 10_000_000n)) return fail(itemContext)
    }
    return { archivedUtc, session }
  })
  const completed = sessions.map(item => item.session).filter(session => session.outcome !== null)
  const intervals = completed.map(session => ({ start: ticks(session.startedUtc!), end: ticks(session.endedUtc!) }))
    .map(interval => ({ start: interval.start < start ? start : interval.start, end: interval.end > end ? end : interval.end }))
    .filter(interval => interval.start < interval.end)
  const runtime = union(intervals)
  const peaks = completed.filter(session => ticks(session.startedUtc!) >= start && session.maximumTrustedOnlinePlayers !== null)
    .map(session => session.maximumTrustedOnlinePlayers!)
  const backupCount = (backup: RecentServerSession['backupResult']) => completed.filter(session => session.backupResult === backup).length
  if (sessions.length !== summary.archivedSessionCount || completed.length !== summary.completedSessionCount ||
      sessions.length - completed.length !== summary.unavailableSessionCount ||
      completed.filter(session => session.outcome === 'FailedBeforeReady' || session.outcome === 'RecoveryFailedBeforeReady').length !== summary.failedStarts ||
      completed.filter(session => session.outcome === 'UnexpectedExit').length !== summary.unexpectedExits ||
      completed.filter(session => session.maximumTrustedOnlinePlayers === null).length !== summary.sessionsWithoutTrustedCounts ||
      completed.filter(session => ticks(session.startedUtc!) < start).length !== summary.clippedSessionCount ||
      (peaks.length > 0 ? Math.max(...peaks) : null) !== summary.peakTrustedOnlinePlayers ||
      backupCount('Completed') !== summary.rollingBackupsCompleted || backupCount('Failed') !== summary.rollingBackupsFailed ||
      backupCount('NotConfigured') !== summary.rollingBackupsNotConfigured || backupCount('Unsupported') !== summary.rollingBackupsUnsupported ||
      backupCount('NotAttempted') !== summary.rollingBackupsNotAttempted ||
      runtime.seconds !== summary.recordedRuntimeSeconds || runtime.overlaps !== summary.overlappingSessionCount) return fail(context)

  let previousEnd = start
  const runtimeByDay = source.runtimeByDay.map((value, index): WeeklyRuntimeDay => {
    const dayContext = `${context}.runtimeByDay[${index}]`
    const day = object(value, dayContext, ['startUtc', 'endUtc', 'recordedRuntimeSeconds', 'timedSessionCount'])
    const startUtc = timestamp(day.startUtc, dayContext)
    const endUtc = timestamp(day.endUtc, dayContext)
    const from = ticks(startUtc)
    const to = ticks(endUtc)
    const midnightAfter = new Date(startUtc)
    midnightAfter.setUTCHours(24, 0, 0, 0)
    const nextMidnight = BigInt(midnightAfter.valueOf()) * 10_000n
    if (!/(?:Z|\+00:00)$/u.test(startUtc) || !/(?:Z|\+00:00)$/u.test(endUtc) || from !== previousEnd ||
        to <= from || to !== (nextMidnight < end ? nextMidnight : end)) return fail(dayContext)
    previousEnd = to
    const timedSessionCount = integer(day.timedSessionCount, dayContext, 500)
    const clipped = intervals.filter(interval => interval.start < to && interval.end > from)
      .map(interval => ({ start: interval.start < from ? from : interval.start, end: interval.end > to ? to : interval.end }))
    const recordedRuntimeSeconds = day.recordedRuntimeSeconds === null ? null : integer(day.recordedRuntimeSeconds, dayContext, 86400)
    if (timedSessionCount !== clipped.length ||
        recordedRuntimeSeconds !== (clipped.length === 0 ? null : union(clipped).seconds)) return fail(dayContext)
    return { startUtc, endUtc, recordedRuntimeSeconds, timedSessionCount }
  })
  if (previousEnd !== end) return fail(context)
  return { runtimeByDay, sessions }
}

export const parseWeeklyServerSummary: Decoder<WeeklyServerSummaryResult> = (value, context = 'seven-day summary') => {
  const source = object(value, context, ['ok', 'code', 'message', 'profileId', 'summary'])
  const code = text(source.code, context, 80)
  const profileId = text(source.profileId, context, 36)
  if (typeof source.ok !== 'boolean' || !/^[A-Za-z][A-Za-z0-9]{0,79}$/u.test(code) ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(profileId) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/u.test(profileId)) return fail(context)
  const common = { ok: source.ok, code, message: text(source.message, context), profileId }
  if (source.summary === null) {
    if (common.ok) return fail(context)
    return { ...common, summary: null }
  }
  if (!common.ok) return fail(context)
  const summary = object(source.summary, context, [...countKeys, 'windowStartUtc', 'windowEndUtc',
    'recordedRuntimeSeconds', 'peakTrustedOnlinePlayers', 'archiveLimitReached', 'runtimeByDay', 'sessions'])
  const windowStartUtc = timestamp(summary.windowStartUtc, context)
  const windowEndUtc = timestamp(summary.windowEndUtc, context)
  if (ticks(windowEndUtc) - ticks(windowStartUtc) !== 7n * 86400n * 10_000_000n || typeof summary.archiveLimitReached !== 'boolean') return fail(context)
  const counts = Object.fromEntries(countKeys.map(key => [key, integer(summary[key], context, 500)])) as Pick<WeeklyServerSummary, typeof countKeys[number]>
  if (counts.completedSessionCount + counts.unavailableSessionCount !== counts.archivedSessionCount ||
      counts.failedStarts + counts.unexpectedExits > counts.completedSessionCount ||
      counts.rollingBackupsCompleted + counts.rollingBackupsFailed + counts.rollingBackupsNotConfigured +
        counts.rollingBackupsUnsupported + counts.rollingBackupsNotAttempted !== counts.completedSessionCount ||
      counts.sessionsWithoutTrustedCounts > counts.completedSessionCount || counts.clippedSessionCount > counts.completedSessionCount ||
      counts.overlappingSessionCount >= Math.max(1, counts.completedSessionCount)) return fail(context)
  const peakTrustedOnlinePlayers = summary.peakTrustedOnlinePlayers === null ? null : integer(summary.peakTrustedOnlinePlayers, context, 1_000_000)
  const parsed: WeeklyServerSummary = { ...counts, windowStartUtc, windowEndUtc,
    recordedRuntimeSeconds: integer(summary.recordedRuntimeSeconds, context, 7 * 86400),
    peakTrustedOnlinePlayers, archiveLimitReached: summary.archiveLimitReached }
  return { ...common, summary: { ...parsed, ...parseHistory(parsed, summary, profileId, context) } }
}

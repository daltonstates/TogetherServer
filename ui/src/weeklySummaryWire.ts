import { ContractError, type Decoder } from './contracts'

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
    'recordedRuntimeSeconds', 'peakTrustedOnlinePlayers', 'archiveLimitReached'])
  const windowStartUtc = timestamp(summary.windowStartUtc, context)
  const windowEndUtc = timestamp(summary.windowEndUtc, context)
  if (Date.parse(windowEndUtc) - Date.parse(windowStartUtc) !== 7 * 86400 * 1000 || typeof summary.archiveLimitReached !== 'boolean') return fail(context)
  const counts = Object.fromEntries(countKeys.map(key => [key, integer(summary[key], context, 500)])) as Pick<WeeklyServerSummary, typeof countKeys[number]>
  if (counts.completedSessionCount + counts.unavailableSessionCount !== counts.archivedSessionCount ||
      counts.failedStarts + counts.unexpectedExits > counts.completedSessionCount ||
      counts.rollingBackupsCompleted + counts.rollingBackupsFailed + counts.rollingBackupsNotConfigured +
        counts.rollingBackupsUnsupported + counts.rollingBackupsNotAttempted !== counts.completedSessionCount ||
      counts.sessionsWithoutTrustedCounts > counts.completedSessionCount || counts.clippedSessionCount > counts.completedSessionCount ||
      counts.overlappingSessionCount >= Math.max(1, counts.completedSessionCount)) return fail(context)
  const peakTrustedOnlinePlayers = summary.peakTrustedOnlinePlayers === null ? null : integer(summary.peakTrustedOnlinePlayers, context, 1_000_000)
  return { ...common, summary: { ...counts, windowStartUtc, windowEndUtc,
    recordedRuntimeSeconds: integer(summary.recordedRuntimeSeconds, context, 7 * 86400),
    peakTrustedOnlinePlayers, archiveLimitReached: summary.archiveLimitReached } }
}

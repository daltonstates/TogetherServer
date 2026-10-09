import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { getLocalJson } from './api'
import { Button, Input, Select } from './Controls'
import { recordedTimestampTicks } from './weeklySummaryWire'
import {
  parseRecentServerSessions,
  type RecentServerSession,
  type RecentServerSessionsResult,
  type ServerSessionBackupResult,
  type ServerSessionOutcome
} from './contracts'

export type RecentSessionsLoader = (profileId: string, signal?: AbortSignal) => Promise<RecentServerSessionsResult>

const defaultLoader: RecentSessionsLoader = (profileId, signal) =>
  getLocalJson(`/api/local/profiles/${encodeURIComponent(profileId)}/sessions?limit=20`,
    parseRecentServerSessions, signal)

export type SessionHistoryEntry = { session: RecentServerSession; archivedUtc?: string }
export type SessionMetric = 'all' | 'runtime' | 'completed' | 'failedStarts' | 'unexpectedExits' |
  'peak' | 'backups' | 'backupCompleted' | 'backupFailed' | 'unavailable'
export type SessionOutcomeFilter = 'all' | 'GracefulStop' | 'failedStarts' | 'UnexpectedExit' |
  'StopUnconfirmed' | 'OwnerArchivedExited' | 'ExitedBeforeRestore' | 'unavailable'
export type SessionFilters = { metric: SessionMetric; outcome: SessionOutcomeFilter; fromDate: string; throughDate: string }
export const emptySessionFilters: SessionFilters = { metric: 'all', outcome: 'all', fromDate: '', throughDate: '' }
export type SessionWindow = { windowStartUtc?: string; windowEndUtc?: string; peakTrustedOnlinePlayers?: number | null }

function utcDate(value: string): number | null {
  if (!/^\d{4}-\d{2}-\d{2}$/u.test(value)) return null
  const parsed = Date.parse(`${value}T00:00:00Z`)
  return Number.isFinite(parsed) && new Date(parsed).toISOString().slice(0, 10) === value ? parsed : null
}

export function sessionDateFilterError(filters: SessionFilters) {
  const from = utcDate(filters.fromDate)
  const through = utcDate(filters.throughDate)
  if (filters.fromDate && from === null || filters.throughDate && through === null) return 'Choose valid calendar dates.'
  if (from !== null && through !== null && from > through) return 'From date must be on or before Through date.'
  return ''
}

export function filterSessionHistory(entries: readonly SessionHistoryEntry[], filters: SessionFilters,
  window: SessionWindow = {}): SessionHistoryEntry[] {
  if (sessionDateFilterError(filters)) return []
  const fromDate = utcDate(filters.fromDate)
  const throughDate = utcDate(filters.throughDate)
  const from = fromDate === null ? null : BigInt(fromDate) * 10_000n
  const until = throughDate === null ? null : BigInt(throughDate + 86400_000) * 10_000n
  const time = (value?: string | null) => value && Number.isFinite(Date.parse(value)) ? recordedTimestampTicks(value) : null
  const windowStart = time(window.windowStartUtc)
  const windowEnd = time(window.windowEndUtc)
  return entries.filter(entry => {
    const session = entry.session
    const failedStart = session.outcome === 'FailedBeforeReady' || session.outcome === 'RecoveryFailedBeforeReady'
    const started = time(session.startedUtc)
    const ended = time(session.endedUtc)
    const timed = started !== null && ended !== null
    const boundedStart = started === null ? null : windowStart !== null && started < windowStart ? windowStart : started
    const boundedEnd = ended === null ? null : windowEnd !== null && ended > windowEnd ? windowEnd : ended
    const hasRuntime = timed && boundedEnd! > boundedStart!
    switch (filters.metric) {
      case 'runtime': if (!hasRuntime) return false; break
      case 'completed': if (session.outcome === null) return false; break
      case 'failedStarts': if (!failedStart) return false; break
      case 'unexpectedExits': if (session.outcome !== 'UnexpectedExit') return false; break
      case 'peak': if (window.peakTrustedOnlinePlayers === null || window.peakTrustedOnlinePlayers === undefined ||
          !timed || windowStart !== null && started! < windowStart || windowEnd !== null && ended! > windowEnd ||
          session.maximumTrustedOnlinePlayers !== window.peakTrustedOnlinePlayers) return false; break
      case 'backups': if (session.backupResult !== 'Completed' && session.backupResult !== 'Failed') return false; break
      case 'backupCompleted': if (session.backupResult !== 'Completed') return false; break
      case 'backupFailed': if (session.backupResult !== 'Failed') return false; break
      case 'unavailable': if (session.outcome !== null) return false; break
    }
    if (filters.outcome === 'failedStarts' && !failedStart ||
        filters.outcome === 'unavailable' && session.outcome !== null ||
        !['all', 'failedStarts', 'unavailable'].includes(filters.outcome) && session.outcome !== filters.outcome) return false
    if (fromDate === null && throughDate === null) return true
    const point = time(entry.archivedUtc)
    if (!timed) return point !== null && (from === null || point >= from) && (until === null || point < until)
    return boundedStart === boundedEnd ? (from === null || boundedStart! >= from) && (until === null || boundedStart! < until) :
      (until === null || boundedStart! < until) && (from === null || boundedEnd! > from)
  })
}

const metricLabels: Record<SessionMetric, string> = {
  all: 'All retained sessions', runtime: 'Recorded runtime', completed: 'Completed sessions',
  failedStarts: 'Failed before Ready', unexpectedExits: 'Unexpected exits', peak: 'Trusted player peak',
  backups: 'Completed or failed backups', backupCompleted: 'Completed backups', backupFailed: 'Failed backups',
  unavailable: 'Unavailable evidence'
}

function dateTime(value: string | null, unavailable: string) {
  if (!value) return unavailable
  const parsed = new Date(value)
  return Number.isFinite(parsed.valueOf()) ? parsed.toLocaleString() : unavailable
}

function duration(seconds: number | null, unavailable: string) {
  if (seconds === null) return unavailable
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor(seconds % 86400 / 3600)
  const minutes = Math.floor(seconds % 3600 / 60)
  if (days > 0) return `${days}d ${hours}h ${minutes}m`
  if (hours > 0) return `${hours}h ${minutes}m`
  if (minutes > 0) return `${minutes}m`
  return `${seconds}s`
}

function outcomeLabel(outcome: ServerSessionOutcome | null) {
  switch (outcome) {
    case 'GracefulStop': return { label: 'Stopped gracefully', tone: 'neutral' }
    case 'UnexpectedExit': return { label: 'Unexpected exit', tone: 'error' }
    case 'FailedBeforeReady': return { label: 'Failed before Ready', tone: 'error' }
    case 'RecoveryFailedBeforeReady': return { label: 'Recovery failed before Ready', tone: 'error' }
    case 'OwnerArchivedExited': return { label: 'Exited record archived', tone: 'attention' }
    case 'ExitedBeforeRestore': return { label: 'Exit confirmed before restore', tone: 'attention' }
    case 'StopUnconfirmed': return { label: 'Stop outcome unconfirmed', tone: 'attention' }
    default: return { label: 'Outcome unavailable', tone: 'muted' }
  }
}

function backupLabel(result: ServerSessionBackupResult | null) {
  switch (result) {
    case 'Completed': return 'Completed'
    case 'Failed': return 'Failed'
    case 'NotConfigured': return 'Not configured'
    case 'Unsupported': return 'Unsupported'
    case 'NotAttempted': return 'Not attempted'
    default: return 'Unavailable'
  }
}

function playerEvidence(session: RecentServerSession) {
  if (session.outcome === null) return 'Unavailable'
  if (session.lastTrustedOnlinePlayers === null || session.maximumTrustedOnlinePlayers === null)
    return 'No trusted count observed'
  return `Last ${session.lastTrustedOnlinePlayers} · peak ${session.maximumTrustedOnlinePlayers}`
}

function gameLabel(kind: RecentServerSession['gameKind']) {
  switch (kind) {
    case 'MinecraftJava': return 'Minecraft Java'
    case 'MinecraftBedrock': return 'Minecraft Bedrock'
    case 'Factorio': return 'Factorio preview'
    case 'Terraria': return 'Terraria preview'
    case 'Custom': return 'Custom game'
    case 'Fixture': return 'Test fixture'
    default: return kind
  }
}

function SessionCard({ session, archivedUtc }: SessionHistoryEntry) {
  const outcome = outcomeLabel(session.outcome)
  const unavailable = session.outcome === null ? 'Unavailable' : 'Unknown'
  return <article className={`recent-session-card tone-${outcome.tone}`}>
    <div className="recent-session-heading">
      <div><strong>{outcome.label}</strong><small>{gameLabel(session.gameKind)} · Run {session.operationId.slice(0, 8)}</small></div>
      <time dateTime={session.endedUtc ?? undefined}>{dateTime(session.endedUtc, 'Unavailable')}</time>
    </div>
    <dl className="recent-session-facts">
      <div><dt>Start</dt><dd>{dateTime(session.startedUtc, unavailable)}</dd></div>
      <div><dt>End</dt><dd>{dateTime(session.endedUtc, unavailable)}</dd></div>
      <div><dt>Duration</dt><dd>{duration(session.durationSeconds, unavailable)}</dd></div>
      <div><dt>Ready observed</dt><dd>{session.readyEverObserved === null ? 'Unavailable' : session.readyEverObserved ? 'Yes' : 'No'}</dd></div>
      <div><dt>Player observations</dt><dd>{playerEvidence(session)}</dd></div>
      <div><dt>Rolling backup</dt><dd>{backupLabel(session.backupResult)}</dd></div>
      <div><dt>Crash recovery</dt><dd>{session.crashRecoveryScheduled === null ? 'Unavailable' : session.crashRecoveryScheduled ? 'Scheduled' : 'Not scheduled'}</dd></div>
    </dl>
    {session.outcome === null && archivedUtc && <small>Archive recorded {dateTime(archivedUtc, 'Unavailable')}. Session timing was not recorded.</small>}
  </article>
}

export function SessionHistory({ entries, filters, onFiltersChange, window = {}, showMetrics = false }: {
  entries: readonly SessionHistoryEntry[]
  filters: SessionFilters
  onFiltersChange: (filters: SessionFilters) => void
  window?: SessionWindow
  showMetrics?: boolean
}) {
  const [shownCount, setShownCount] = useState(10)
  const filtered = filterSessionHistory(entries, filters, window)
  const dateError = sessionDateFilterError(filters)
  useEffect(() => setShownCount(10), [filters.metric, filters.outcome, filters.fromDate, filters.throughDate])
  return <div className="session-history">
    <div className="server-log-filters">
      {showMetrics && <label>Evidence<Select aria-label="Session evidence" value={filters.metric}
        onChange={event => onFiltersChange({ ...filters, metric: event.target.value as SessionMetric })}>
        {Object.entries(metricLabels).map(([key, label]) => <option key={key} value={key}>{label}</option>)}
      </Select></label>}
      <label>Outcome<Select aria-label="Session outcome" value={filters.outcome}
        onChange={event => onFiltersChange({ ...filters, outcome: event.target.value as SessionOutcomeFilter })}>
        <option value="all">All outcomes</option><option value="GracefulStop">Stopped gracefully</option>
        <option value="failedStarts">Failed before Ready</option><option value="UnexpectedExit">Unexpected exit</option>
        <option value="StopUnconfirmed">Stop unconfirmed</option><option value="OwnerArchivedExited">Exited record archived</option>
        <option value="ExitedBeforeRestore">Exit before restore</option><option value="unavailable">Outcome unavailable</option>
      </Select></label>
      <label>From date (UTC)<Input type="date" aria-label="Sessions from date (UTC)" value={filters.fromDate}
        onChange={event => onFiltersChange({ ...filters, fromDate: event.target.value })} /></label>
      <label>Through date (UTC)<Input type="date" aria-label="Sessions through date (UTC)" value={filters.throughDate}
        onChange={event => onFiltersChange({ ...filters, throughDate: event.target.value })} /></label>
      <Button className="text-button" disabled={filters.metric === 'all' && filters.outcome === 'all' && !filters.fromDate && !filters.throughDate}
        onClick={() => onFiltersChange({ ...emptySessionFilters })}>Clear session filters</Button>
    </div>
    {dateError && <p className="warning-text" role="alert">{dateError}</p>}
    <p role="status">{filtered.length} of {entries.length} retained sessions match{showMetrics ? ` · ${metricLabels[filters.metric]}` : ''}.</p>
    {!dateError && filtered.length === 0 && <p>No retained sessions match these filters.</p>}
    <div className="recent-session-list" aria-label="Filtered sessions">
      {filtered.slice(0, shownCount).map(entry => <SessionCard key={entry.session.operationId} {...entry} />)}
    </div>
    {filtered.length > shownCount && <Button className="secondary" onClick={() => setShownCount(count => Math.min(filtered.length, count + 10))}>Show more sessions</Button>}
    <small>Dates match known recorded intervals in UTC, including sessions crossing midnight. An archive date places an unavailable record only; it never supplies runtime. Undated sessions cannot match a date filter.</small>
  </div>
}

export function RecentSessions({ profileId, visible, loader = defaultLoader }: {
  profileId: string
  visible: boolean
  loader?: RecentSessionsLoader
}) {
  const titleId = useId()
  const [result, setResult] = useState<RecentServerSessionsResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState(false)
  const [filters, setFilters] = useState<SessionFilters>({ ...emptySessionFilters })
  const pending = useRef<AbortController | null>(null)

  const load = useCallback(async () => {
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    setLoading(true)
    setLoadError(false)
    try {
      const next = await loader(profileId, controller.signal)
      if (!controller.signal.aborted) {
        if (!next.ok || next.profileId.toLowerCase() !== profileId.toLowerCase()) setLoadError(true)
        else setResult(next)
      }
    } catch {
      if (!controller.signal.aborted) setLoadError(true)
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [loader, profileId])

  useEffect(() => {
    setResult(null)
    if (visible) void load()
    return () => pending.current?.abort()
  }, [load, visible])

  useEffect(() => setFilters({ ...emptySessionFilters }), [profileId])

  const currentResult = result?.profileId.toLowerCase() === profileId.toLowerCase() ? result : null
  return <section hidden={!visible} className="recent-sessions" aria-labelledby={titleId} aria-busy={loading}>
    <div className="recent-sessions-toolbar"><div><h4 id={titleId}>Recent sessions</h4>
      <p>Summaries saved after server sessions end on this Host. Filters cover the newest 20 retained sessions.</p></div>
      <Button className="secondary" disabled={loading || !visible} onClick={() => void load()}>{loading ? 'Loading…' : 'Refresh'}</Button>
    </div>
    {loading && !currentResult && <div className="recent-sessions-state" role="status">Loading recent sessions…</div>}
    {loadError && <div className="recent-sessions-state error" role="alert"><strong>Recent sessions unavailable</strong>
      <p>TogetherServer could not read the saved session history.</p><Button className="secondary" onClick={() => void load()}>Try again</Button></div>}
    {!loadError && currentResult?.sessions.length === 0 && <div className="recent-sessions-state"><strong>No archived sessions yet</strong>
      <p>A summary appears after TogetherServer saves a completed server session.</p></div>}
    {!loadError && currentResult && currentResult.sessions.length > 0 && <SessionHistory entries={currentResult.sessions.map(session => ({ session }))}
      filters={filters} onFiltersChange={setFilters} />}
    <p className="recent-sessions-boundary">Player numbers came from the game server during that session. They do not show who joined or prove that a join or world save succeeded.</p>
  </section>
}

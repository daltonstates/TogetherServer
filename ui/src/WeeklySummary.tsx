import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { getLocalJson } from './api'
import { Button } from './Controls'
import { parseWeeklyServerSummary, type WeeklyServerSummaryResult } from './weeklySummaryWire'
import { emptySessionFilters, SessionHistory, type SessionFilters, type SessionMetric } from './RecentSessions'

export type WeeklySummaryLoader = (profileId: string, signal?: AbortSignal) => Promise<WeeklyServerSummaryResult>
const defaultLoader: WeeklySummaryLoader = (profileId, signal) =>
  getLocalJson(`/api/local/profiles/${encodeURIComponent(profileId)}/weekly-summary`, parseWeeklyServerSummary, signal)

function duration(seconds: number) {
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor(seconds % 3600 / 60)
  return hours > 0 ? `${hours}h ${minutes}m` : minutes > 0 ? `${minutes}m` : `${seconds}s`
}

export function WeeklySummary({ profileId, visible, refreshKey = 0, loader = defaultLoader }: {
  profileId: string
  visible: boolean
  refreshKey?: number
  loader?: WeeklySummaryLoader
}) {
  const titleId = useId()
  const [result, setResult] = useState<WeeklyServerSummaryResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(false)
  const [sessionFilters, setSessionFilters] = useState<SessionFilters>({ ...emptySessionFilters })
  const [browsing, setBrowsing] = useState(false)
  const pending = useRef<AbortController | null>(null)
  const load = useCallback(async () => {
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    setLoading(true)
    setError(false)
    try {
      const next = await loader(profileId, controller.signal)
      if (!controller.signal.aborted) {
        if (!next.ok || !next.summary || next.profileId.toLowerCase() !== profileId.toLowerCase()) setError(true)
        else setResult(next)
      }
    } catch {
      if (!controller.signal.aborted) setError(true)
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [loader, profileId])
  useEffect(() => {
    setResult(null)
    if (visible) void load()
    return () => pending.current?.abort()
  }, [load, refreshKey, visible])

  useEffect(() => {
    setSessionFilters({ ...emptySessionFilters })
    setBrowsing(false)
  }, [profileId])

  const summary = result?.profileId.toLowerCase() === profileId.toLowerCase() ? result.summary : null
  const explore = (metric: SessionMetric, day = '') => {
    setSessionFilters({ ...emptySessionFilters, metric, fromDate: day, throughDate: day })
    setBrowsing(true)
  }
  const metric = (label: string, view: SessionMetric, value: string | number, unavailable = false) =>
    <div><dt>{label}</dt><dd>{summary?.sessions ? <Button className="text-button"
      aria-label={`${label}: ${value}. Show contributing sessions`} disabled={unavailable}
      aria-pressed={browsing && sessionFilters.metric === view} onClick={() => explore(view)}>{value}</Button> : value}</dd></div>
  return <section hidden={!visible} className="recent-sessions weekly-summary" aria-labelledby={titleId} aria-busy={loading}>
    <div className="recent-sessions-toolbar"><div><h4 id={titleId}>Seven-day summary</h4>
      <p>Retained session evidence for this server on this Host.</p></div>
      <Button className="secondary" disabled={loading || !visible} onClick={() => void load()}>{loading ? 'Loading…' : 'Refresh summary'}</Button></div>
    {loading && !result && <p role="status">Loading seven-day summary…</p>}
    {error && <div className="error" role="alert"><strong>Seven-day summary unavailable</strong>
      <p>Could not read this Host’s retained session evidence.</p><Button className="secondary" onClick={() => void load()}>Try again</Button></div>}
    {!error && summary && <>
      <p>{new Date(summary.windowStartUtc).toLocaleString()} – {new Date(summary.windowEndUtc).toLocaleString()}</p>
      {summary.archivedSessionCount === 0 && <p>No completed session evidence in these seven days.</p>}
      <dl className="recent-session-facts">
        {metric('Recorded runtime', 'runtime', duration(summary.recordedRuntimeSeconds))}
        {metric('Completed sessions', 'completed', summary.completedSessionCount)}
        {metric('Failed before Ready', 'failedStarts', summary.failedStarts)}
        {metric('Unexpected exits', 'unexpectedExits', summary.unexpectedExits)}
        {metric('Trusted player peak', 'peak', summary.peakTrustedOnlinePlayers === null ? 'Unavailable' : summary.peakTrustedOnlinePlayers,
          summary.peakTrustedOnlinePlayers === null)}
        {metric('Rolling backups', 'backups', `${summary.rollingBackupsCompleted} completed · ${summary.rollingBackupsFailed} failed`)}
      </dl>
      <figure className="weekly-runtime-chart">
        <figcaption>Recorded activity by day</figcaption>
        <p>UTC dates; the first and last days are clipped to this window. Bars show recorded runtime out of 24 hours, not availability. Missing timing stays unavailable.</p>
        {summary.runtimeByDay ? <ol>
          {summary.runtimeByDay.map(day => {
            const date = day.startUtc.slice(0, 10)
            return <li key={day.startUtc}>
              <Button className="text-button" disabled={day.recordedRuntimeSeconds === null} onClick={() => explore('runtime', date)}
                aria-label={`Show recorded sessions on ${date} (UTC)`}>{date}</Button>
              {day.recordedRuntimeSeconds === null ? <span>No timed evidence</span> : <>
                <svg width="100%" height="8" viewBox="0 0 100 8" preserveAspectRatio="none" role="img"
                  aria-label={`${date}: ${duration(day.recordedRuntimeSeconds)} recorded runtime`}>
                  <rect width="100" height="8" fill="#31343a" />
                  <rect width={day.recordedRuntimeSeconds / 86400 * 100} height="8" fill="#9ca3af" />
                </svg>
                <span>{duration(day.recordedRuntimeSeconds)} recorded · {day.timedSessionCount} timed session{day.timedSessionCount === 1 ? '' : 's'}</span>
              </>}
            </li>
          })}
        </ol> : <p>Daily timing detail is unavailable in this summary. Refresh after updating the app.</p>}
        <small>Only retained, known start/end intervals contribute. Overlaps count once. Durations round down to whole seconds, so daily and weekly totals can differ by a few seconds.</small>
      </figure>
      {summary.sessions && <details open={browsing} onToggle={event => setBrowsing(event.currentTarget.open)}>
        <summary>Explore retained sessions</summary>
        <p>These are the retained records used by the summary. Runtime is clipped; player peaks from sessions that began before this window are excluded.</p>
        <SessionHistory key={profileId} entries={summary.sessions} filters={sessionFilters} onFiltersChange={setSessionFilters}
          window={summary} showMetrics />
      </details>}
      <details><summary>Coverage and missing evidence</summary>
        <p>Runtime uses saved start and end times, clipped to these seven days. It does not measure continuous availability or time at Ready.</p>
        <ul>
          <li>{summary.unfinishedRunCount} unfinished run{summary.unfinishedRunCount === 1 ? '' : 's'} excluded from runtime, outcomes, backups and player peaks.</li>
          <li>{summary.unavailableSessionCount} legacy or incomplete session{summary.unavailableSessionCount === 1 ? '' : 's'} with unavailable metrics.</li>
          <li>{summary.clippedSessionCount} session{summary.clippedSessionCount === 1 ? '' : 's'} started before the window; their untimed player peaks are excluded.</li>
          <li>{summary.sessionsWithoutTrustedCounts} completed session{summary.sessionsWithoutTrustedCounts === 1 ? '' : 's'} without a trusted player count.</li>
          <li>{summary.overlappingSessionCount} overlapping saved interval{summary.overlappingSessionCount === 1 ? '' : 's'} counted once.</li>
          <li>{summary.undatedArchiveRecordCount} archive record{summary.undatedArchiveRecordCount === 1 ? '' : 's'} could not be placed in time.</li>
        </ul>
        <p>Rolling backups: {summary.rollingBackupsNotConfigured} not configured · {summary.rollingBackupsUnsupported} unsupported · {summary.rollingBackupsNotAttempted} not attempted.</p>
        <p>{summary.archiveLimitReached ? 'The 500-record archive limit was reached; some sessions from this window may be missing.' : 'Only the retained archive is available, with up to 500 records from 30 days. Missing history is not inferred as zero.'}</p>
      </details>
    </>}
    <p className="recent-sessions-boundary">Player peaks are count observations. They do not identify players or prove a game join, saved world, uptime, or permission to stop a server.</p>
  </section>
}

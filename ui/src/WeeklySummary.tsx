import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { getLocalJson } from './api'
import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
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
      <p>Saved activity for this server on this Host PC.</p></div>
      <Button className="secondary" disabled={loading || !visible} onClick={() => void load()}>{loading ? 'Loading…' : 'Refresh summary'}</Button></div>
    {loading && !result && <p role="status">Loading seven-day summary…</p>}
    {error && <DismissibleNotice scope={profileId} className="error" role="alert"><strong>Seven-day summary unavailable</strong>
      <p>Could not read this Host’s saved session history.</p><Button className="secondary" onClick={() => void load()}>Try again</Button></DismissibleNotice>}
    {!error && summary && <>
      <p>{new Date(summary.windowStartUtc).toLocaleString()} – {new Date(summary.windowEndUtc).toLocaleString()}</p>
      {summary.archivedSessionCount === 0 && <p>No ended sessions were recorded in these seven days.</p>}
      <dl className="recent-session-facts">
        {metric('Recorded runtime', 'runtime', duration(summary.recordedRuntimeSeconds))}
        {metric('Ended sessions', 'completed', summary.completedSessionCount)}
        {metric('Failed before Ready', 'failedStarts', summary.failedStarts)}
        {metric('Unexpected exits', 'unexpectedExits', summary.unexpectedExits)}
        {metric('Highest reported player count', 'peak', summary.peakTrustedOnlinePlayers === null ? 'Unavailable' : summary.peakTrustedOnlinePlayers,
          summary.peakTrustedOnlinePlayers === null)}
        {metric('Backups after Stop', 'backups', `${summary.rollingBackupsCompleted} completed · ${summary.rollingBackupsFailed} failed`)}
      </dl>
      <figure className="weekly-runtime-chart">
        <figcaption>Recorded activity by day</figcaption>
        <p>Dates use UTC. Bars show recorded server runtime out of 24 hours. Only time within these seven days is counted. Runtime does not show whether players could join.</p>
        {summary.runtimeByDay ? <ol>
          {summary.runtimeByDay.map(day => {
            const date = day.startUtc.slice(0, 10)
            return <li key={day.startUtc}>
              <Button className="text-button" disabled={day.recordedRuntimeSeconds === null} onClick={() => explore('runtime', date)}
                aria-label={`Show recorded sessions on ${date} (UTC)`}>{date}</Button>
              {day.recordedRuntimeSeconds === null ? <span>No recorded times</span> : <>
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
        <small>Only saved sessions with known start and end times count. Overlapping time counts once. Daily and weekly totals may differ by a few seconds because times are rounded down.</small>
      </figure>
      {summary.sessions && <details open={browsing} onToggle={event => setBrowsing(event.currentTarget.open)}>
        <summary>View saved sessions</summary>
        <p>These records make up the summary. Only runtime within these seven days counts. Player peaks from sessions that began earlier are left out.</p>
        <SessionHistory key={profileId} entries={summary.sessions} filters={sessionFilters} onFiltersChange={setSessionFilters}
          window={summary} showMetrics />
      </details>}
      <details><summary>What is included and missing</summary>
        <p>Runtime uses saved start and end times within these seven days. It does not show continuous availability or time spent Ready.</p>
        <ul>
          <li>{summary.unfinishedRunCount} unfinished session{summary.unfinishedRunCount === 1 ? '' : 's'} left out of runtime, outcomes, backups and player peaks.</li>
          <li>{summary.unavailableSessionCount} older or incomplete session{summary.unavailableSessionCount === 1 ? '' : 's'} with missing details.</li>
          <li>{summary.clippedSessionCount} session{summary.clippedSessionCount === 1 ? '' : 's'} started before these seven days; their player peaks are left out because the peak times are unknown.</li>
          <li>{summary.sessionsWithoutTrustedCounts} ended session{summary.sessionsWithoutTrustedCounts === 1 ? '' : 's'} without a reliable player count.</li>
          <li>{summary.overlappingSessionCount} overlapping session time{summary.overlappingSessionCount === 1 ? '' : 's'} counted once.</li>
          <li>{summary.undatedArchiveRecordCount} saved record{summary.undatedArchiveRecordCount === 1 ? '' : 's'} with no usable date.</li>
        </ul>
        <p>Backups after Stop: {summary.rollingBackupsNotConfigured} not set up · {summary.rollingBackupsUnsupported} unsupported · {summary.rollingBackupsNotAttempted} not attempted.</p>
        <p>{summary.archiveLimitReached ? 'The 500-record limit was reached. Some sessions from these seven days may be missing.' : 'History keeps up to 500 records from 30 days. Missing history is unknown, not zero.'}</p>
      </details>
    </>}
    <p className="recent-sessions-boundary">Past player counts do not identify players, prove a game join or saved world, or allow Stop. This summary does not measure uptime.</p>
  </section>
}

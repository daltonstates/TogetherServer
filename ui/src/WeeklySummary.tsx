import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { getLocalJson } from './api'
import { Button } from './Controls'
import { parseWeeklyServerSummary, type WeeklyServerSummaryResult } from './weeklySummaryWire'

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

  const summary = result?.summary
  return <section hidden={!visible} className="recent-sessions weekly-summary" aria-labelledby={titleId} aria-busy={loading}>
    <div className="recent-sessions-toolbar"><div><h4 id={titleId}>Seven-day summary</h4>
      <p>Retained session evidence for this server on this Host.</p></div>
      <Button className="secondary" disabled={loading} onClick={() => void load()}>{loading ? 'Loading…' : 'Refresh summary'}</Button></div>
    {loading && !result && <p role="status">Loading seven-day summary…</p>}
    {error && <div className="error" role="alert"><strong>Seven-day summary unavailable</strong>
      <p>Could not read this Host’s retained session evidence.</p><Button className="secondary" onClick={() => void load()}>Try again</Button></div>}
    {!error && summary && <>
      <p>{new Date(summary.windowStartUtc).toLocaleString()} – {new Date(summary.windowEndUtc).toLocaleString()}</p>
      {summary.archivedSessionCount === 0 && <p>No completed session evidence in these seven days.</p>}
      <dl className="recent-session-facts">
        <div><dt>Recorded runtime</dt><dd>{duration(summary.recordedRuntimeSeconds)}</dd></div>
        <div><dt>Completed sessions</dt><dd>{summary.completedSessionCount}</dd></div>
        <div><dt>Failed before Ready</dt><dd>{summary.failedStarts}</dd></div>
        <div><dt>Unexpected exits</dt><dd>{summary.unexpectedExits}</dd></div>
        <div><dt>Trusted player peak</dt><dd>{summary.peakTrustedOnlinePlayers === null ? 'Unavailable' : summary.peakTrustedOnlinePlayers}</dd></div>
        <div><dt>Rolling backups</dt><dd>{summary.rollingBackupsCompleted} completed · {summary.rollingBackupsFailed} failed</dd></div>
      </dl>
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

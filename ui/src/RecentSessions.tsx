import { useCallback, useEffect, useState } from 'react'
import { getLocalJson } from './api'
import { Button } from './Controls'
import {
  parseRecentServerSessions,
  type RecentServerSession,
  type RecentServerSessionsResult,
  type ServerSessionBackupResult,
  type ServerSessionOutcome
} from './contracts'

export type RecentSessionsLoader = (profileId: string, signal?: AbortSignal) => Promise<RecentServerSessionsResult>

const defaultLoader: RecentSessionsLoader = (profileId, signal) =>
  getLocalJson(`/api/local/profiles/${encodeURIComponent(profileId)}/sessions?limit=8`,
    parseRecentServerSessions, signal)

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
    case 'Custom': return 'Custom game'
    case 'Fixture': return 'Test fixture'
    default: return kind
  }
}

function SessionCard({ session }: { session: RecentServerSession }) {
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
  </article>
}

export function RecentSessions({ profileId, visible, loader = defaultLoader }: {
  profileId: string
  visible: boolean
  loader?: RecentSessionsLoader
}) {
  const [result, setResult] = useState<RecentServerSessionsResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState(false)

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true)
    setLoadError(false)
    try {
      const next = await loader(profileId, signal)
      if (!signal?.aborted) setResult(next)
    } catch {
      if (!signal?.aborted) setLoadError(true)
    } finally {
      if (!signal?.aborted) setLoading(false)
    }
  }, [loader, profileId])

  useEffect(() => {
    if (!visible) return
    const controller = new AbortController()
    setResult(null)
    void load(controller.signal)
    return () => controller.abort()
  }, [load, visible])

  return <section hidden={!visible} className="recent-sessions" aria-labelledby="recent-sessions-title" aria-busy={loading}>
    <div className="recent-sessions-toolbar"><div><h4 id="recent-sessions-title">Recent sessions</h4>
      <p>Summaries saved after server sessions end on this Host.</p></div>
      <Button className="secondary" disabled={loading} onClick={() => void load()}>{loading ? 'Loading…' : 'Refresh'}</Button>
    </div>
    {loading && !result && <div className="recent-sessions-state" role="status">Loading recent sessions…</div>}
    {loadError && <div className="recent-sessions-state error" role="alert"><strong>Recent sessions unavailable</strong>
      <p>TogetherServer could not read the saved session history.</p><Button className="secondary" onClick={() => void load()}>Try again</Button></div>}
    {!loadError && result?.sessions.length === 0 && <div className="recent-sessions-state"><strong>No archived sessions yet</strong>
      <p>A summary appears after TogetherServer saves a completed server session.</p></div>}
    {!loadError && result && result.sessions.length > 0 && <div className="recent-session-list">
      {result.sessions.map(session => <SessionCard key={session.operationId} session={session} />)}
    </div>}
    <p className="recent-sessions-boundary">Player numbers came from the game server during that session. They do not show who joined or prove that a join or world save succeeded.</p>
  </section>
}

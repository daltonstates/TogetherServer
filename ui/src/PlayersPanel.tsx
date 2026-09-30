import { Button } from './Controls'
import type { ActivityEvent, Run } from './contracts'

function countLabel(run: Run) {
  if (run.state !== 'Ready' || !run.playerCountTrusted || run.onlinePlayers === null) return 'Unavailable'
  return run.maxPlayers === null ? `${run.onlinePlayers} online` : `${run.onlinePlayers} / ${run.maxPlayers} online`
}

function sourceLabel(source: string | null) {
  if (!source) return 'Waiting for a fresh game-server observation'
  const known: Record<string, string> = {
    FixtureReady: 'Fixture readiness contract',
    ValheimLogReady: 'Valheim query or verified server-log count',
    MinecraftLocalStatus: 'Minecraft local status query',
    CustomCertifiedReady: 'Owner-certified Custom status contract',
    CustomScriptReady: 'Custom status script (display only)',
    ObservationStale: 'Stale observation',
    ObservationPending: 'Observation pending'
  }
  return known[source] ?? source.replace(/([a-z])([A-Z])/g, '$1 $2')
}

function freshness(observedUtc: string | null, nowMs: number) {
  if (!observedUtc) return 'No observation time is available.'
  const observed = Date.parse(observedUtc)
  if (!Number.isFinite(observed)) return 'The observation time is invalid.'
  const seconds = Math.max(0, Math.floor((nowMs - observed) / 1000))
  if (seconds < 5) return 'Observed just now.'
  if (seconds < 60) return `Observed ${seconds} seconds ago.`
  const minutes = Math.floor(seconds / 60)
  return `Observed ${minutes} minute${minutes === 1 ? '' : 's'} ago.`
}

function timerLabel(deadline: string | null, nowMs: number) {
  if (!deadline) return 'No empty-server countdown is active.'
  const target = Date.parse(deadline)
  if (!Number.isFinite(target)) return 'The countdown deadline is unavailable.'
  const minutes = Math.max(0, Math.ceil((target - nowMs) / 60000))
  return minutes === 0 ? 'The safe-stop deadline has arrived.' :
    `Safe Stop is due in about ${minutes} minute${minutes === 1 ? '' : 's'}.`
}

export function PlayersPanel({ run, activity = [], nowMs, refreshing, disabled, onRefresh }: {
  run: Run
  activity?: ActivityEvent[]
  nowMs: number
  refreshing: boolean
  disabled: boolean
  onRefresh: () => void
}) {
  const recent = activity.filter(item => item.profileId === run.profileId && item.category === 'Players').slice(0, 5)
  const trustedNow = run.state === 'Ready' && run.playerCountTrusted && run.onlinePlayers !== null
  const friendMinutes = Math.min(run.friendAddedMinutes, run.addedShutdownMinutes)
  const hostMinutes = Math.max(0, run.addedShutdownMinutes - friendMinutes)
  return <section className="players-workspace" aria-labelledby={`players-${run.profileId}`}>
    <div className="players-heading"><div><h3 id={`players-${run.profileId}`}>Players & empty-server safety</h3><p>Counts come from the game-server driver. Friend app presence never controls automatic shutdown.</p></div><Button className="secondary" disabled={disabled || refreshing} onClick={onRefresh}>{refreshing ? 'Refreshing…' : 'Refresh count'}</Button></div>
    <div className="players-metrics">
      <div><span>Trusted player count</span><strong>{countLabel(run)}</strong><small>{trustedNow ? 'Authoritative for this driver.' : 'Automatic Stop remains fail-closed.'}</small></div>
      <div><span>Observation source</span><strong>{sourceLabel(run.playerObservationSource)}</strong><small>{freshness(run.playerCountObservedUtc, nowMs)}</small></div>
      <div><span>Empty-server timer</span><strong>{timerLabel(run.autoShutdownAtUtc, nowMs)}</strong><small>{run.autoShutdownReason ?? 'Waiting for a Ready server and a fresh trusted count.'}</small></div>
      <div><span>Saved added time</span><strong>{run.addedShutdownMinutes} minute{run.addedShutdownMinutes === 1 ? '' : 's'}</strong><small>{hostMinutes} from the Host · {friendMinutes} from Friends.</small></div>
    </div>
    <div className="player-activity"><strong>Recent count changes</strong><small>Count signals only; this history does not store player identities.</small>
      {recent.length === 0 ? <p className="helper-text">No trusted count changes are recorded for this session yet.</p> : <ol>{recent.map(item => <li key={item.id}><span>{item.message}</span><time dateTime={item.occurredUtc}>{new Date(item.occurredUtc).toLocaleString()}</time></li>)}</ol>}
    </div>
  </section>
}

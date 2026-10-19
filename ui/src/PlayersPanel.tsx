import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { Icon } from './Icon'
import type { ActivityEvent, Run } from './contracts'
import type { Profile } from './GameProfile'
import { playerCountPresentation } from './playerCountPresentation'
export { playerCountAvailability } from './playerCountPresentation'
import './players.css'

function countLabel(run: Run, count: number | null) {
  if (count === null) return '—'
  return run.maxPlayers === null ? `${count} online` : `${count} / ${run.maxPlayers} online`
}

function sourceLabel(source: string | null) {
  if (!source) return 'Count source unavailable'
  const known: Record<string, string> = {
    FixtureReady: 'Test data',
    ValheimLogReady: 'Valheim status or checked server log',
    ValheimReady: 'Valheim status',
    MinecraftLocalStatus: 'Minecraft status',
    FactorioReady: 'Factorio admin connection',
    CustomCertifiedReady: 'Custom script · Owner tested',
    CustomScriptReady: 'Custom status script (display only)',
    ObservationStale: 'Count out of date',
    ObservationPending: 'Waiting for a count'
  }
  return Object.hasOwn(known, source) ? known[source] : source.replace(/([a-z])([A-Z])/g, '$1 $2')
}

function freshness(observedUtc: string | null, nowMs: number) {
  if (!observedUtc) return 'Last checked time unavailable.'
  const observed = Date.parse(observedUtc)
  if (!Number.isFinite(observed)) return 'Last checked time unavailable.'
  if (!Number.isFinite(nowMs)) return 'Last checked time unavailable.'
  if (observed > nowMs) return 'The count time is ahead of this PC’s clock. Refresh to check again.'
  const seconds = Math.max(0, Math.floor((nowMs - observed) / 1000))
  if (seconds < 5) return 'Checked just now.'
  if (seconds < 60) return `Checked ${seconds} seconds ago.`
  const minutes = Math.floor(seconds / 60)
  return `Checked ${minutes} minute${minutes === 1 ? '' : 's'} ago.`
}

function timerLabel(deadline: string | null, nowMs: number) {
  if (!deadline) return 'No shutdown countdown is running.'
  const target = Date.parse(deadline)
  if (!Number.isFinite(target)) return 'No valid shutdown time is saved.'
  const minutes = Math.max(0, Math.ceil((target - nowMs) / 60000))
  return minutes === 0 ? 'Time is up. The Host must check that the server is still empty before stopping.' :
    `Shutdown is due in about ${minutes} minute${minutes === 1 ? '' : 's'}.`
}

export type PlayersPanelProps = {
  run: Run
  activity?: ActivityEvent[]
  nowMs: number
  refreshing: boolean
  disabled: boolean
  onRefresh: () => void
  profile?: Pick<Profile, 'kind' | 'crossplay'>
  onOpenLogs?: () => void
  onCheckHealth?: () => void
  onOpenHelp?: () => void
}

export function playerCountDriverHelp(profile?: PlayersPanelProps['profile']): string {
  switch (profile?.kind) {
    case 'Terraria': return 'Terraria preview can check its port but cannot confirm readiness or count players. Friend Stop and automatic shutdown stay off.'
    case 'Custom': return 'Custom counts need an owner control test before they can allow Friend Stop or automatic shutdown. Review the test in server health.'
    case 'Valheim': return profile.crossplay
      ? 'Valheim Crossplay needs a reply from the game to count players. Its server log cannot replace a missing reply.'
      : 'Valheim uses its status reply or a complete, consistent server log to count players. Missing information leaves the count unavailable.'
    case 'MinecraftJava': return 'Minecraft Java must reply with a player count. A running server or log message is not enough.'
    case 'MinecraftBedrock': return 'Minecraft Bedrock must reply with a player count. No reply leaves the count unavailable.'
    case 'Factorio': return 'Factorio uses its local admin connection (RCON) to count players. Check that setup. Never forward the RCON port.'
    case 'Fixture': return 'These are test counts. No real game join or save was checked.'
    default: return 'Check server health and logs. No reply does not mean the server is empty.'
  }
}

export function PlayersPanel({ run, activity = [], nowMs, refreshing, disabled, onRefresh,
  profile, onOpenLogs, onCheckHealth, onOpenHelp }: PlayersPanelProps) {
  const recent = activity.filter(item => item.profileId === run.profileId && item.category === 'Players')
    .sort((a, b) => Date.parse(b.occurredUtc) - Date.parse(a.occurredUtc)).slice(0, 5)
  const evidence = playerCountPresentation(run, nowMs, refreshing)
  const trustedNow = evidence.count !== null
  const deadline = evidence.count === 0 ? run.autoShutdownAtUtc : null
  const deadlineMs = deadline ? Date.parse(deadline) : NaN
  const remainingMinutes = Number.isFinite(deadlineMs) && Number.isFinite(nowMs)
    ? Math.max(0, Math.ceil((deadlineMs - nowMs) / 60000)) : null
  const timerValue = !deadline ? 'No countdown' : remainingMinutes === null ? 'Unavailable'
    : remainingMinutes === 0 ? 'Deadline reached' : `About ${remainingMinutes} min`
  const friendMinutes = Math.min(run.friendAddedMinutes, run.addedShutdownMinutes)
  const hostMinutes = Math.max(0, run.addedShutdownMinutes - friendMinutes)
  return <section className="players-workspace players-panel" data-count-state={evidence.state}
    aria-labelledby={`players-${run.profileId}`}>
    <div className="players-heading">
      <h3 id={`players-${run.profileId}`}>Players</h3>
      <Button className="secondary" disabled={disabled || evidence.refreshing} onClick={onRefresh}>
        <Icon name={refreshing ? 'loader' : 'refresh'} />{refreshing ? 'Refreshing…' : 'Refresh count'}
      </Button>
    </div>
    <div className="players-overview">
      <div className="players-stat" role="group" aria-labelledby={`players-count-${run.profileId}`}>
        <div className="players-stat-heading"><Icon name="users" size={19} /><h4 id={`players-count-${run.profileId}`}>Players online</h4></div>
        <strong className="players-count-value" aria-label={evidence.count === null ? 'Player count unavailable' : undefined}>{countLabel(run, evidence.count)}</strong>
      </div>
      <div className="players-stat" role="group" aria-labelledby={`players-timer-${run.profileId}`}>
        <div className="players-stat-heading"><Icon name="clock" size={19} /><h4 id={`players-timer-${run.profileId}`}>Empty-server timer</h4></div>
        <strong className="players-timer-value">{timerValue}</strong>
        {deadline && <p className="players-timer-context">{remainingMinutes === 0
          ? 'The Host checks for players before stopping.'
          : deadline && remainingMinutes === null ? 'Shutdown time unavailable.'
          : run.autoShutdownReason}</p>}
      </div>
    </div>
    {evidence.showWarning && <DismissibleNotice className="players-warning" role="note" scope={JSON.stringify([run.profileId, run.runOperationId])} noticeKey="player-count-unavailable">
      <Icon name="warning" size={19} />
      <div>
        <strong>Player count unavailable after repeated checks</strong>
        <p>Open player-count help to troubleshoot.</p>
      </div>
    </DismissibleNotice>}
    <div className="players-disclosures">
      <details className="players-disclosure">
        <summary>Count source & timer details</summary>
        <div className="players-detail-content">
          <dl className="players-detail-list">
            <dt>Count source</dt><dd>{sourceLabel(run.playerObservationSource)}</dd>
            <dt>Last checked</dt><dd>{freshness(run.playerCountObservedUtc, nowMs)}</dd>
            <dt>Count status</dt><dd>{evidence.reason}</dd>
            <dt>Countdown</dt><dd>{timerLabel(deadline, nowMs)}</dd>
            <dt>Extra time</dt><dd>{run.addedShutdownMinutes} minute{run.addedShutdownMinutes === 1 ? '' : 's'}<small>{hostMinutes} from the Host · {friendMinutes} from Friends.</small></dd>
          </dl>
          <p>Shutdown uses the game’s player count. Whether friends have TogetherServer open does not affect it.</p>
        </div>
      </details>
      <details className="players-disclosure">
        <summary>Player-count help</summary>
        <div className="players-detail-content">
          <p>{playerCountDriverHelp(profile)}</p>
          <p>Friends and the idle timer can stop this server only after a fresh check shows no players. The Host can stop it manually.</p>
          {!trustedNow && <div className="players-help-actions">
            {onOpenLogs && <Button className="secondary" onClick={onOpenLogs}><Icon name="logs" />Open logs</Button>}
            {onCheckHealth && <Button className="secondary" disabled={disabled || refreshing} onClick={onCheckHealth}><Icon name="server" />Check server health</Button>}
            {onOpenHelp && <Button className="text-button" onClick={onOpenHelp}>Player-count help</Button>}
          </div>}
        </div>
      </details>
      <details className="players-disclosure">
        <summary>Recent count changes</summary>
        <div className="players-detail-content players-history">
          <p>Only player counts are recorded here, without names.</p>
          {recent.length === 0 ? <p>No confirmed count changes yet.</p>
            : <ol>{recent.map(item => <li key={item.id}><span>{item.message}</span><time dateTime={item.occurredUtc}>{new Date(item.occurredUtc).toLocaleString()}</time></li>)}</ol>}
        </div>
      </details>
    </div>
  </section>
}

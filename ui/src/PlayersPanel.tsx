import { Button } from './Controls'
import { Icon } from './Icon'
import type { ActivityEvent, Run } from './contracts'
import type { Profile } from './GameProfile'
import './players.css'

export function playerCountAvailability(run: Run, nowMs: number): {
  count: number | null; reason: string; state: 'Fresh' | 'Stale' | 'Unavailable' | 'DisplayOnly'
} {
  const observed = run.playerCountObservedUtc ? Date.parse(run.playerCountObservedUtc) : NaN
  if (run.playerObservationSource === 'CustomScriptReady') return {
    count: null, state: 'DisplayOnly', reason: 'The Custom script count is display only until this setup is owner-certified.'
  }
  if (run.playerObservationSource === 'ObservationStale' ||
      (Number.isFinite(observed) && Number.isFinite(nowMs) && nowMs - observed > 10_000)) return {
    count: null, state: 'Stale', reason: 'The player observation is stale. Refresh for a current game-server count.'
  }
  if (run.state !== 'Ready' || !run.playerCountTrusted || run.onlinePlayers === null ||
      !Number.isSafeInteger(run.onlinePlayers) || run.onlinePlayers < 0 ||
      !Number.isFinite(observed) || !Number.isFinite(nowMs) || observed > nowMs ||
      run.playerObservationSource === 'ObservationPending') return {
    count: null, state: 'Unavailable', reason: 'A fresh trusted game-server count is not available.'
  }
  return { count: run.onlinePlayers, state: 'Fresh', reason: 'Fresh game-server observation.' }
}

function countLabel(run: Run, count: number | null) {
  if (count === null) return 'Unavailable'
  return run.maxPlayers === null ? `${count} online` : `${count} / ${run.maxPlayers} online`
}

function sourceLabel(source: string | null) {
  if (!source) return 'Observation source unavailable'
  const known: Record<string, string> = {
    FixtureReady: 'Fixture readiness contract',
    ValheimLogReady: 'Valheim query or verified server-log count',
    ValheimReady: 'Valheim local query',
    MinecraftLocalStatus: 'Minecraft local status query',
    FactorioReady: 'Factorio local RCON count',
    CustomCertifiedReady: 'Owner-certified Custom status contract',
    CustomScriptReady: 'Custom status script (display only)',
    ObservationStale: 'Stale observation',
    ObservationPending: 'Observation pending'
  }
  return Object.hasOwn(known, source) ? known[source] : source.replace(/([a-z])([A-Z])/g, '$1 $2')
}

function freshness(observedUtc: string | null, nowMs: number) {
  if (!observedUtc) return 'No observation time is available.'
  const observed = Date.parse(observedUtc)
  if (!Number.isFinite(observed)) return 'The observation time is invalid.'
  if (!Number.isFinite(nowMs)) return 'The observation age is unavailable.'
  if (observed > nowMs) return 'The observation time is ahead of this PC; freshness is unavailable.'
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
  return minutes === 0 ? 'The deadline has arrived; the Host must recheck players before Stop.' :
    `The empty-server deadline is in about ${minutes} minute${minutes === 1 ? '' : 's'}.`
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
    case 'Terraria': return 'The Terraria preview driver can observe a local TCP listener, but cannot verify readiness or report a trusted player count. Refresh cannot remove this driver limitation.'
    case 'Custom': return 'Custom script counts are display only unless the exact setup has current owner certification. Check its status contract and certification in server health.'
    case 'Valheim': return profile.crossplay
      ? 'Valheim Crossplay cannot use the owned-log player-count fallback. A silent or invalid local query remains Unknown.'
      : 'Valheim needs a valid local query or a complete connection sequence from this managed run’s owned log. A partial or contradictory log stays Unknown.'
    case 'MinecraftJava': return 'Minecraft Java needs a valid local server-status reply with a player count. A running process or log line cannot replace that reply.'
    case 'MinecraftBedrock': return 'Minecraft Bedrock needs a valid local RakNet status reply with a player count. Query silence is Unknown.'
    case 'Factorio': return 'The Factorio preview uses an authenticated local RCON player-count reply. Check the saved local RCON setup; never forward its RCON port.'
    case 'Fixture': return 'Fixture counts are synthetic driver evidence and do not prove a real game join or saved-world integrity.'
    default: return 'Check the game-server logs and health for the current observation. Query silence never means the server is empty.'
  }
}

export function PlayersPanel({ run, activity = [], nowMs, refreshing, disabled, onRefresh,
  profile, onOpenLogs, onCheckHealth, onOpenHelp }: PlayersPanelProps) {
  const recent = activity.filter(item => item.profileId === run.profileId && item.category === 'Players')
    .sort((a, b) => Date.parse(b.occurredUtc) - Date.parse(a.occurredUtc)).slice(0, 5)
  const evidence = playerCountAvailability(run, nowMs)
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
      <Button className="secondary" disabled={disabled || refreshing} onClick={onRefresh}>
        <Icon name={refreshing ? 'loader' : 'refresh'} />{refreshing ? 'Refreshing…' : 'Refresh count'}
      </Button>
    </div>
    <div className="players-overview">
      <div className="players-stat" role="group" aria-labelledby={`players-count-${run.profileId}`}>
        <div className="players-stat-heading"><Icon name="users" size={19} /><h4 id={`players-count-${run.profileId}`}>Players online</h4></div>
        <strong className="players-count-value">{countLabel(run, evidence.count)}</strong>
      </div>
      <div className="players-stat" role="group" aria-labelledby={`players-timer-${run.profileId}`}>
        <div className="players-stat-heading"><Icon name="clock" size={19} /><h4 id={`players-timer-${run.profileId}`}>Empty-server timer</h4></div>
        <strong className="players-timer-value">{timerValue}</strong>
        <p className="players-timer-context">{remainingMinutes === 0
          ? 'The Host must recheck players before Stop.'
          : deadline && remainingMinutes === null ? 'A valid countdown deadline is not available.'
          : trustedNow ? run.autoShutdownReason ?? 'Waiting for the Host’s current timer state.'
          : 'Waiting for a fresh trusted count.'}</p>
      </div>
    </div>
    {!trustedNow && <div className="players-warning" role="note">
      <Icon name="warning" size={19} />
      <div>
        <strong>{evidence.state === 'Stale' ? 'Player observation is stale'
          : evidence.state === 'DisplayOnly' ? 'Custom player count is display only' : 'Player count unavailable'}</strong>
        <p>Friend and automatic Stop remain blocked.</p>
      </div>
    </div>}
    <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">{trustedNow
      ? `${evidence.count} players reported by the game server.`
      : 'Player count unavailable. Friend and automatic Stop remain blocked.'}</span>
    <div className="players-disclosures">
      <details className="players-disclosure">
        <summary>Count source & timer details</summary>
        <div className="players-detail-content">
          <dl className="players-detail-list">
            <dt>Observation source</dt><dd>{sourceLabel(run.playerObservationSource)}</dd>
            <dt>Freshness</dt><dd>{freshness(run.playerCountObservedUtc, nowMs)}</dd>
            <dt>Count status</dt><dd>{evidence.reason}</dd>
            <dt>Countdown</dt><dd>{timerLabel(deadline, nowMs)}</dd>
            <dt>Saved added time</dt><dd>{run.addedShutdownMinutes} minute{run.addedShutdownMinutes === 1 ? '' : 's'}<small>{hostMinutes} from the Host · {friendMinutes} from Friends.</small></dd>
          </dl>
          <p>Counts come from the game-server driver. Friend app presence never controls automatic shutdown.</p>
        </div>
      </details>
      <details className="players-disclosure">
        <summary>Player-count help</summary>
        <div className="players-detail-content">
          <p>{playerCountDriverHelp(profile)}</p>
          <p>Friend Stop and automatic Stop stay blocked until a fresh trusted count is exactly zero. The local owner can review a Stop separately.</p>
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
          <p>Count signals only; this history does not store player identities.</p>
          {recent.length === 0 ? <p>No trusted count changes are recorded for this session yet.</p>
            : <ol>{recent.map(item => <li key={item.id}><span>{item.message}</span><time dateTime={item.occurredUtc}>{new Date(item.occurredUtc).toLocaleString()}</time></li>)}</ol>}
        </div>
      </details>
    </div>
  </section>
}

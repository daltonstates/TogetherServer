import { useId, useState, type ReactNode } from 'react'
import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { Icon } from './Icon'
import { ServerStartupProgress, serverStartupStage } from './ServerStartupProgress'
import type { FriendSnapshot, PublicProfile } from './contracts'
import { friendCheckFreshness } from './FriendConnectionDoctor'
import { JoinGuide } from './JoinGuide'
import { OpenGameButton } from './OpenGameButton'
import { ServerNotificationPreferences } from './DesktopPreferencesPanel'
import { isPlayerCountObservationUnavailable, publicPlayerCount } from './playerCountPresentation'
import './TaskFirstPages.css'

export type FriendConnectionExplanation = {
  state: 'connected' | 'paused' | 'disconnected' | 'stale' | 'identity' | 'approval' | 'access-ended' | 'removed' | 'credential' | 'update' | 'unknown'
  title: string
  detail: string
  currentAccess: boolean
}

export function friendConnectionExplanation(snapshot: FriendSnapshot, nowMs = Date.now()): FriendConnectionExplanation {
  const code = snapshot.connectionCode
  if (code === 'AccessExpired' || snapshot.state === 'Access expired') return {
    state: 'access-ended', title: 'Access ended by the Host', currentAccess: false,
    detail: 'Ask the Host to extend or clear your access deadline. Your saved connection remains; a new code is not required.'
  }
  if (code === 'Revoked' || snapshot.state === 'Revoked') return {
    state: 'removed', title: 'Access removed', currentAccess: false,
    detail: 'The Host removed this PC’s access. Ask the Host whether to connect again with a new code.'
  }
  if (snapshot.state === 'Awaiting approval' || code === 'ApprovalPending') return {
    state: 'approval', title: 'Waiting for Host approval', currentAccess: false,
    detail: 'The Host must approve this PC before status and controls become available.'
  }
  if (code === 'HostIdentityMismatch') return {
    state: 'identity', title: 'Host identity needs review', currentAccess: false,
    detail: 'Ask the Host to review its secure identity. Keep the saved certificate check enabled.'
  }
  if (['CredentialExpired', 'CredentialRejected', 'HostAccessDenied'].includes(code ?? '')) return {
    state: 'credential', title: 'Saved access needs review', currentAccess: false,
    detail: 'Ask the Host to review this PC’s saved access. A network retry cannot grant permission.'
  }
  if (!['Connected', 'Disabled', 'Update required'].includes(snapshot.state)
    || ['HostPortClosed', 'HostPortTimedOut', 'HostTimedOut', 'FriendNetworkUnavailable', 'HostUnreachable', 'InviteAddressInvalid', 'Disconnected'].includes(code ?? '')) return {
    state: 'disconnected', title: 'Host connection unavailable', currentAccess: false,
    detail: 'Host status and controls are Unknown. Retry the saved connection. Ask the Host to check that Friend connections are on and the network settings are correct.'
  }
  const freshness = friendCheckFreshness(snapshot.lastConnectedUtc, nowMs)
  if (freshness !== 'current') return {
    state: freshness === 'stale' ? 'stale' : 'unknown', title: freshness === 'stale' ? 'Host status is out of date' : 'Host check time is unknown', currentAccess: false,
    detail: 'Retry the saved connection for current status. Old server cards cannot confirm your current permissions or whether the server is ready.'
  }
  if (snapshot.state === 'Update required' || snapshot.protocolCompatible !== true) return {
    state: 'update', title: snapshot.protocolCompatible === false || snapshot.state === 'Update required' ? 'App update required' : 'App compatibility unknown', currentAccess: false,
    detail: 'Check that both TogetherServer apps are up to date. Matching game versions do not mean the apps can communicate.'
  }
  if (snapshot.state === 'Disabled' || !snapshot.remoteControlsEnabled) return {
    state: 'paused', title: 'Controls paused by the Host', currentAccess: true,
    detail: 'The Host must turn remote controls back on. Status and separately granted chat and log access may continue. You can still join a running game.'
  }
  return { state: 'connected', title: 'Host connection current', currentAccess: true,
    detail: 'Your saved connection was accepted. Server readiness, game connection and each permission are checked separately.' }
}

export function friendServerStateExplanation(state: string): string {
  switch (state) {
    case 'Offline': return 'The game server is stopped. Start it if permitted, or ask the Host.'
    case 'Starting': return 'The Host is starting the server. Wait until it reports Ready before joining.'
    case 'Ready': return 'The Host reports readiness. Open the game and join; this is not proof that a player joined.'
    case 'Listening': return 'The game port is open. Game readiness, player count and a real join have not been verified.'
    case 'Stopping': return 'The Host is stopping the server. Wait for the confirmed exit before another Start.'
    case 'Failed': return 'The Host reported a problem. Open Connection help or ask the Host to review startup.'
    default: return 'Server state is Unknown. Refresh status or ask the Host. Stop is blocked while the state is unknown.'
  }
}

export type FriendActionAvailability = { available: boolean; reason: string }
export type EffectiveFriendPermissions = {
  summary: string
  currentAccess: boolean
  grants: { start: boolean; stop: boolean; extend: boolean; logs: boolean }
  actions: Record<'start' | 'stop' | 'restart' | 'extend' | 'logs', FriendActionAvailability>
}

export function effectiveFriendPermissions(snapshot: FriendSnapshot, profile: PublicProfile, nowMs = Date.now()): EffectiveFriendPermissions {
  const connection = friendConnectionExplanation(snapshot, nowMs)
  const assigned = snapshot.profiles.find(item => item.id === profile.id)
  const grants = { start: assigned?.canStart === true, stop: assigned?.canStop === true,
    extend: assigned?.canExtendTimer === true, logs: assigned?.canViewLogs === true }
  const labels = [grants.start && 'Start', grants.stop && 'Stop', grants.extend && 'Add time', grants.logs && 'View logs'].filter(Boolean)
  const blocked = !assigned ? 'The Host no longer assigns this server to this PC.' : !connection.currentAccess ? connection.detail
    : connection.state === 'paused' ? 'The Host must resume remote controls.' : assigned.maintenanceEnabled
      ? 'The Host paused remote server controls for maintenance.' : assigned.operation?.state === 'Pending' || assigned.operation?.state === 'Running'
        ? 'Wait for the current server operation to finish.' : null
  const action = (reason: string | null): FriendActionAvailability => ({ available: reason === null, reason: reason ?? 'Available; the Host checks this request again.' })
  const players = assigned ? publicPlayerCount(assigned, nowMs) : null
  const stopBlocker = !assigned || assigned.state !== 'Ready' ? 'Stop requires the server to report Ready.'
    : players === null ? 'Player count is Unknown. Refresh players or ask the Host; Stop needs a fresh count of zero.'
      : players > 0 ? 'Players are online. Wait until the server reports zero players.'
        : !assigned.canStopNow ? 'The Host has not confirmed a safe Stop. Refresh player status.' : null
  const logBlocker = !assigned || !connection.currentAccess ? !assigned ? 'This server is no longer assigned.' : connection.detail
    : !grants.logs ? 'The Host has not granted View logs for this server.'
      : !snapshot.hostCapabilities.includes('server-logs-v1') ? 'The Host app does not support shared logs. Ask the Host to update.'
        : !['Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(assigned.kind) ? 'Shared logs are unavailable for this game.' : null
  return {
    summary: labels.length ? labels.join(' · ') : 'Status only', currentAccess: connection.currentAccess && !!assigned, grants,
    actions: {
      start: action(blocked ?? (!grants.start ? 'The Host has not granted Start for this server.'
        : assigned?.state !== 'Offline' ? friendServerStateExplanation(assigned?.state ?? 'Unknown') : null)),
      stop: action(blocked ?? (!grants.stop ? 'The Host has not granted Stop for this server.' : stopBlocker)),
      restart: action(blocked ?? (!grants.start || !grants.stop ? 'Restart needs both Start and Stop permissions.'
        : stopBlocker ?? (!assigned?.canRestartNow ? 'The Host has not confirmed a safe Restart.' : null))),
      extend: action(blocked ?? (!grants.extend ? 'The Host has not granted Add time for this server.'
        : assigned?.state !== 'Ready' || players === null
          ? 'Add time needs a Ready server with a known player count.'
          : assigned.timerExtensionMinutes <= 0 || assigned.timerExtensionRemainingMinutes < assigned.timerExtensionMinutes
            ? 'The Host’s added-time limit for this run has been reached.' : null)),
      logs: action(logBlocker)
    }
  }
}

export function FriendConnectionExplanation({ snapshot, nowMs, onRetry }: {
  snapshot: FriendSnapshot; nowMs?: number; onRetry?: () => void
}) {
  const explanation = friendConnectionExplanation(snapshot, nowMs)
  return <DismissibleNotice scope={snapshot.connectionId ?? snapshot.endpoint ?? undefined} noticeKey={explanation.state} className="friend-connection-explanation" role="status"><strong>{explanation.title}</strong><p>{explanation.detail}</p>
    {!explanation.currentAccess && ['disconnected', 'stale', 'unknown'].includes(explanation.state) && onRetry
      && <Button className="secondary" onClick={onRetry}>Retry saved connection</Button>}
  </DismissibleNotice>
}

export function FriendPermissionExplanation({ snapshot, profile, nowMs }: {
  snapshot: FriendSnapshot; profile: PublicProfile; nowMs?: number
}) {
  const permissions = effectiveFriendPermissions(snapshot, profile, nowMs)
  return <details className="advanced-block friend-permissions"><summary>Your access: {permissions.summary}{!permissions.currentAccess && ' · Unverified'}</summary>
    <dl>{(['start', 'stop', 'restart', 'extend', 'logs'] as const).map(action => <div key={action}>
      <dt>{({ start: 'Start', stop: 'Stop', restart: 'Restart', extend: 'Add time', logs: 'View logs' })[action]}</dt>
      <dd>{permissions.actions[action].reason}</dd>
    </div>)}</dl>
    <small>Permissions apply only to this server. Stop and Restart also require a Ready server and a current player count of zero, checked again before stopping.</small>
  </details>
}

export type FriendPlayFlowProps = {
  snapshot: FriendSnapshot
  profile: PublicProfile
  busy?: boolean
  pendingAction?: 'start' | 'stop' | 'refresh' | null
  nowMs?: number
  identityKey?: string | number
  onStart?: () => void
  onStop?: () => void
  onRefresh?: () => void
  onOpenConnectionDoctor?: () => void
  onOpenLogs?: () => void
  startNotice?: ReactNode
  localHostingAction?: ReactNode
  hostNotice?: ReactNode
  connectionDetails?: ReactNode
  requirements?: ReactNode
  showServerNotifications?: boolean
  children?: ReactNode
}

// Compose the existing actions. No polling, implicit Start, query or game launch is added.
export function FriendPlayFlow({ snapshot, profile, busy = false, pendingAction = null, nowMs, identityKey, onStart, onStop, onRefresh,
  onOpenConnectionDoctor, onOpenLogs, startNotice, localHostingAction, hostNotice, connectionDetails, requirements, showServerNotifications = true, children }: FriendPlayFlowProps) {
  const headingId = useId()
  const [notificationsOpened, setNotificationsOpened] = useState(false)
  const connection = friendConnectionExplanation(snapshot, nowMs)
  const permissions = effectiveFriendPermissions(snapshot, profile, nowMs)
  const currentProfile = snapshot.profiles.find(item => item.id === profile.id)
  const startupState = { ...currentProfile, requestPending: pendingAction === 'start' }
  const starting = permissions.currentAccess && !!currentProfile && serverStartupStage(startupState)
  const canJoin = permissions.currentAccess && !!currentProfile?.joinAddress && ['Ready', 'Listening'].includes(currentProfile.state)
  const playExplanation = !permissions.currentAccess ? connection.detail
    : currentProfile?.state === 'Offline' ? !permissions.actions.start.available
      ? `The game server is stopped. ${permissions.actions.start.reason}`
      : !onStart ? 'The game server is stopped. Ask the Host to start this server.' : 'The game server is stopped.'
    : isPlayerCountObservationUnavailable(currentProfile) ? 'Server status is currently unavailable.'
      : friendServerStateExplanation(currentProfile?.state ?? 'Unknown')
  return <section className="friend-play-flow" aria-labelledby={headingId} aria-busy={busy}>
    <div className="panel-heading"><h4 id={headingId}>Play</h4>
      {onRefresh && <Button className="icon-button" aria-label="Refresh server status" title="Refresh server status" disabled={busy} onClick={onRefresh}><Icon name={pendingAction === 'refresh' ? 'loader' : 'refresh'} /></Button>}</div>
    {!starting && <p>{playExplanation}</p>}
    <div className="friend-primary-actions">
      {hostNotice}
      {(currentProfile?.state === 'Offline' || pendingAction === 'start' && currentProfile?.state === 'Starting') && permissions.currentAccess && onStart && <Button className="server-primary-action" disabled={busy || !permissions.actions.start.available}
        title={!permissions.actions.start.available ? permissions.actions.start.reason : 'Remote Start asks the selected Host PC to run this server.'}
        onClick={() => { if (!busy && permissions.actions.start.available) onStart() }}><Icon name={pendingAction === 'start' ? 'loader' : 'play'} />{pendingAction === 'start' ? 'Starting…' : 'Start server'}</Button>}
      {localHostingAction}
      {canJoin && currentProfile && <OpenGameButton profileId={profile.id} kind={currentProfile.gameKind ?? currentProfile.kind}
        connectionId={snapshot.connectionId} identityKey={identityKey} available={!busy}
        disabledReason="Wait for the current action to finish." />}
      {(currentProfile?.state === 'Ready' || pendingAction === 'stop' && currentProfile?.state === 'Stopping') && permissions.currentAccess && permissions.grants.stop && onStop && <Button className="secondary server-play-action" disabled={busy || !permissions.actions.stop.available}
        title={!permissions.actions.stop.available ? permissions.actions.stop.reason : undefined}
        onClick={() => { if (!busy && permissions.actions.stop.available) onStop() }}><Icon name={pendingAction === 'stop' ? 'loader' : 'stop'} />{pendingAction === 'stop' ? 'Stopping…' : 'Stop server'}</Button>}
    </div>
    {localHostingAction && <small className="helper-text">Start server runs on the selected Host PC. Host locally uses this PC.</small>}
    {currentProfile?.operation?.code === 'NewerSharedSaveAvailable' && currentProfile.operation.state === 'Failed' &&
      <p role="status" className="helper-text">The selected Host needs a newer verified save before it can start. This refusal does not mean a download began. Keep an approved PC with that save online, then choose Start server again to request preparation on the target Host. Receiving a copy on this PC does not block remote Start.</p>}
    {permissions.currentAccess && currentProfile && <ServerStartupProgress key={`${snapshot.connectionId}:${currentProfile.id}`} {...startupState} host={false}
      logEndpoint={permissions.actions.logs.available ? `/api/local/friend/${currentProfile.id}/logs` : undefined}
      onOpenLogs={permissions.actions.logs.available ? onOpenLogs : undefined} />}
    {canJoin && connectionDetails}
    {permissions.currentAccess && currentProfile && ['Ready', 'Listening'].includes(currentProfile.state) && !currentProfile.joinAddress
      && <p className="helper-text">Wait for the Host to share a current game address, then refresh server status.</p>}
    {canJoin && currentProfile && <details className="server-support-disclosure"><summary aria-label="How to join in the game"><Icon name="game" />Join instructions</summary><JoinGuide kind={currentProfile.gameKind ?? currentProfile.kind} /></details>}
    {requirements}
    {currentProfile?.state === 'Offline' && permissions.currentAccess && permissions.actions.start.available && startNotice && <details className="server-support-disclosure"><summary><Icon name="plug" />Connection checks before Start</summary>{startNotice}</details>}
    <details className="server-support-disclosure"><summary aria-label="Server tools and connection help"><Icon name="plug" />Tools &amp; help</summary>
      {onOpenConnectionDoctor && <Button className="text-button" onClick={onOpenConnectionDoctor}>Open Connection help</Button>}
      <FriendPermissionExplanation snapshot={snapshot} profile={profile} nowMs={nowMs} />
    </details>
    {showServerNotifications && snapshot.connectionId && <details className="server-support-disclosure"
      onToggle={event => { if (event.currentTarget.open) setNotificationsOpened(true) }}><summary><Icon name="bell" />Server notifications</summary>
      {notificationsOpened && <ServerNotificationPreferences server={{ id: profile.id, name: profile.name, connectionId: snapshot.connectionId }} />}
    </details>}
    {children}
  </section>
}

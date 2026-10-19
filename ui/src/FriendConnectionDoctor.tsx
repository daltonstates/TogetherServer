import { useEffect, useId, useState } from 'react'
import { Button, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import type { FriendSnapshot, GameEndpointResult, PublicProfile } from './contracts'
import { splitJoinAddress } from './ConnectionDetails'
import { Icon } from './Icon'
import { orderProfiles } from './profileLists'

export type FriendDoctorStage = {
  id: string
  label: string
  state: string
  detail: string
  tone: 'neutral' | 'attention' | 'error'
  complete: boolean
  checkedUtc: string | null
  freshness: 'current' | 'stale' | 'unknown' | 'saved'
}

export type FriendDoctorOptions = {
  selectedProfileId?: string | null
  nowMs?: number
  /** Change when the saved identity/route or game configuration is invalidated. */
  identityKey?: string | number
  /** Bind each result at request time; a result from another Host/run cannot pass. */
  gameResultIdentities?: Record<string, string>
}

const routeFailures = new Set(['HostPortClosed', 'HostPortTimedOut', 'HostTimedOut', 'FriendNetworkUnavailable',
  'HostUnreachable', 'InviteAddressInvalid', 'Disconnected'])
const credentialStates: Record<string, string> = {
  CredentialExpired: 'Credential expired', CredentialRejected: 'Credential rejected', Revoked: 'Access removed',
  AccessExpired: 'Access deadline reached', HostAccessDenied: 'Access denied'
}
const connectedStates = ['Connected', 'Disabled', 'Update required']

export function friendCheckFreshness(checkedUtc: string | null | undefined, nowMs: number, maxAgeMs = 45_000) {
  const checkedMs = checkedUtc ? Date.parse(checkedUtc) : NaN
  if (!Number.isFinite(checkedMs) || !Number.isFinite(nowMs) || checkedMs > nowMs + 5_000) return 'unknown' as const
  return nowMs - checkedMs > maxAgeMs ? 'stale' as const : 'current' as const
}

function timestamp(value: string | null | undefined): string | null {
  return value && Number.isFinite(Date.parse(value)) ? new Date(value).toISOString() : null
}

export function friendGameCheckIdentity(snapshot: FriendSnapshot, profile: PublicProfile, identityKey?: string | number) {
  return JSON.stringify([snapshot.connectionId, snapshot.endpoint, snapshot.hostId, identityKey,
    profile.id, profile.kind, profile.joinAddress, profile.state, profile.runOperationId])
}

export function friendGameProbeSupported(profile: PublicProfile): boolean {
  const parts = splitJoinAddress(profile.joinAddress)
  // The existing fixed probe accepts IPv4 only. DNS/IPv6 join fields remain useful for manual play.
  return ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Terraria'].includes(profile.kind)
    && !!parts && /^[0-9]+(?:\.[0-9]+){3}$/.test(parts.address)
    && !(profile.kind === 'Valheim' && parts.port === '65535')
}

export function friendConnectionDoctorStages(snapshot: FriendSnapshot,
  gameResults: Record<string, GameEndpointResult>, options: FriendDoctorOptions = {}): FriendDoctorStage[] {
  const nowMs = options.nowMs ?? Date.now()
  const appFreshness = friendCheckFreshness(snapshot.lastConnectedUtc, nowMs)
  const connected = connectedStates.includes(snapshot.state) && !routeFailures.has(snapshot.connectionCode ?? '')
    && !credentialStates[snapshot.connectionCode ?? ''] && !['HostIdentityMismatch', 'ApprovalPending'].includes(snapshot.connectionCode ?? '')
  const current = connected && appFreshness === 'current'
  const issue = snapshot.connectionCode ?? ''
  const checkedUtc = timestamp(snapshot.lastConnectedUtc)
  const profileId = options.selectedProfileId === undefined
    ? snapshot.profiles.length === 1 ? snapshot.profiles[0].id : null : options.selectedProfileId
  const profile = snapshot.profiles.find(item => item.id === profileId)
  const appStage = (stage: Omit<FriendDoctorStage, 'checkedUtc' | 'freshness'>): FriendDoctorStage => ({
    ...stage, checkedUtc, freshness: connected ? appFreshness : 'unknown'
  })
  const waiting = (id: string, label: string, detail: string) => appStage({
    id, label, state: connected && appFreshness === 'stale' ? 'Stale' : 'Unknown', detail,
    tone: 'attention', complete: false
  })
  const saved: FriendDoctorStage = {
    id: 'saved', label: 'Saved access', state: snapshot.endpoint ? 'Present' : 'Not connected',
    detail: snapshot.endpoint ? 'This PC has its own saved access and a saved Host identity, protected by Windows.' : 'Paste the current server code from the Host.',
    tone: snapshot.endpoint ? 'neutral' : 'attention', complete: !!snapshot.endpoint, checkedUtc: null, freshness: 'saved'
  }
  const route = current
    ? appStage({ id: 'route', label: 'Host app port', state: 'Answered', detail: 'The saved Host address answered from this PC.', tone: 'neutral', complete: true })
    : routeFailures.has(issue)
      ? appStage({ id: 'route', label: 'Host app port', state: 'Could not verify', detail: 'Retry the saved connection. Ask the Host to check that Friend connections are on and the network settings are correct.', tone: 'error', complete: false })
      : waiting('route', 'Host app port', 'Run checks for the saved Host. An old response does not prove the connection still works.')
  const identity = current
    ? appStage({ id: 'identity', label: 'Host identity', state: 'Matched', detail: 'The secure connection matched the saved Host certificate.', tone: 'neutral', complete: true })
    : issue === 'HostIdentityMismatch'
      ? appStage({ id: 'identity', label: 'Host identity', state: 'Mismatch', detail: 'Ask the Host to check its secure identity or provide a new code. Keep the saved certificate check enabled.', tone: 'error', complete: false })
      : waiting('identity', 'Host identity', 'The next secure connection must match the saved Host certificate.')
  const credential = current
    ? appStage({ id: 'credential', label: 'This PC’s access', state: 'Accepted', detail: 'The Host accepted this PC’s own saved access.', tone: 'neutral', complete: true })
    : credentialStates[issue] || issue === 'ApprovalPending' || snapshot.state === 'Awaiting approval'
      ? appStage({ id: 'credential', label: 'This PC’s access', state: credentialStates[issue] ?? 'Awaiting approval',
          detail: issue === 'AccessExpired' ? 'The Host owner can extend or clear the access deadline. This saved connection remains available.'
            : issue === 'ApprovalPending' || snapshot.state === 'Awaiting approval' ? 'Ask the Host to approve this PC.' : 'Ask the Host to review this PC’s access. Saved access may no longer be valid.',
          tone: issue === 'ApprovalPending' || snapshot.state === 'Awaiting approval' ? 'attention' : 'error', complete: false })
      : waiting('credential', 'This PC’s access', 'After checking the Host identity, confirm that the Host accepts this PC’s saved access.')
  const protocol = current && snapshot.protocolCompatible === true
    ? appStage({ id: 'protocol', label: 'App compatibility', state: 'Compatible', detail: 'These app versions can communicate.', tone: 'neutral', complete: true })
    : snapshot.protocolCompatible === false || snapshot.state === 'Update required'
      ? appStage({ id: 'protocol', label: 'App compatibility', state: 'Update required', detail: 'Update the incompatible app before requesting controls.', tone: 'error', complete: false })
      : waiting('protocol', 'App compatibility', 'Reconnect securely to check whether these app versions can communicate.')
  const assignment = current && profile
    ? appStage({ id: 'assignment', label: 'Server access', state: 'Assigned', detail: 'The Host currently shares this server with this PC.', tone: 'neutral', complete: true })
    : appStage({ id: 'assignment', label: 'Server access', state: !current ? 'Unknown' : profileId ? 'No longer assigned' : snapshot.profiles.length ? 'Choose a server' : 'None assigned',
        detail: !current ? 'Reconnect securely to check which servers the Host shares with this PC.' : profileId || !snapshot.profiles.length
          ? 'Ask the Host to assign this server under Friend access.' : 'Choose the server you want to check.',
        tone: profileId && current ? 'error' : 'attention', complete: false })
  const controls = !current || snapshot.protocolCompatible !== true || !profile
    ? waiting('controls', 'Remote controls', 'Reconnect and choose an assigned server to check your available controls.')
    : appStage({ id: 'controls', label: 'Remote controls',
        state: snapshot.state === 'Disabled' || !snapshot.remoteControlsEnabled ? 'Paused by Host' : profile.maintenanceEnabled ? 'Maintenance' : profile.canStart || profile.canStop || profile.canExtendTimer ? 'Granted actions only' : 'Status only',
        detail: snapshot.state === 'Disabled' || !snapshot.remoteControlsEnabled ? 'The Host paused remote server controls. Status and separately granted chat and log access may continue.'
          : profile.maintenanceEnabled ? 'The Host paused remote server controls for maintenance. Ask the Host when it is finished.'
            : 'Start, Stop, Add time and View logs are separate permissions for this server. Every request is checked again by the Host.',
        tone: 'neutral', complete: true })
  const readiness = current && profile
    ? appStage({ id: 'readiness', label: 'Host game readiness', state: profile.state === 'Ready' ? 'Ready reported' : profile.state === 'Listening' ? 'Listening only' : profile.state === 'Offline' ? 'Offline' : profile.state === 'Starting' ? 'Starting' : 'Unknown',
        detail: profile.state === 'Ready' ? 'The Host reports that the game server is ready. You still need to check the game connection and try joining.'
          : profile.state === 'Listening' ? 'The game port is open. Game readiness, player count and a real join have not been verified.'
            : profile.state === 'Offline' ? 'Start this server if permitted, or ask the Host. Run the game check after startup.'
              : 'Wait for the Host to check again or ask the Host to review startup.',
        tone: 'attention', complete: profile.state === 'Ready' })
    : waiting('readiness', 'Host game readiness', 'Refresh the selected server’s status to check whether it is ready.')
  if (readiness.complete) readiness.tone = 'neutral'

  const raw = profile ? gameResults[profile.id] : undefined
  const scopeMatches = !!profile && (!options.gameResultIdentities
    || options.gameResultIdentities[profile.id] === friendGameCheckIdentity(snapshot, profile, options.identityKey))
  const result = scopeMatches ? raw : undefined
  const gameFreshness = friendCheckFreshness(result?.checkedUtc, nowMs)
  const game: FriendDoctorStage = {
    id: 'game', label: profile?.kind === 'Terraria' ? 'Game TCP port' : 'Game connection',
    state: 'Not tested', detail: 'Check this selected server from this PC. A reply never proves a player joined.',
    tone: 'attention', complete: false, checkedUtc: timestamp(result?.checkedUtc), freshness: gameFreshness
  }
  if (!profile) { game.state = 'Choose a server'; game.detail = 'Select one assigned server. Another server’s reply cannot pass this check.' }
  else if (!friendGameProbeSupported(profile)) {
    game.state = 'Manual check'
    game.detail = profile.kind === 'Valheim' && splitJoinAddress(profile.joinAddress)?.port === '65535'
      ? 'Valheim’s query needs the following UDP port. Ask the Host to review the game port.'
      : ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Terraria'].includes(profile.kind)
      ? 'The fixed check currently needs an IPv4 game address. Use the game’s manual join steps for DNS or IPv6.'
      : 'The app cannot test this game connection. Try joining in the game.'
    game.checkedUtc = null; game.freshness = 'unknown'
  } else if (!current || snapshot.protocolCompatible !== true || !['Ready', 'Listening'].includes(profile.state)) {
    game.state = 'Waiting'; game.detail = 'Run the game check after a fresh assigned-server status reports Ready or Listening.'
  } else if (raw && !scopeMatches) {
    game.state = 'Changed'; game.detail = 'The Host, route or selected server changed. Run the game check again.'
  } else if (result && gameFreshness !== 'current') {
    game.state = gameFreshness === 'stale' ? 'Stale' : 'Unknown'; game.detail = 'This game check is no longer current. Run it again before using it to troubleshoot.'
  } else if (result) {
    const expectedCode = profile.kind === 'Terraria' ? 'GamePortOpen' : 'GameEndpointAnswered'
    game.complete = result.answered && result.code === expectedCode
    game.state = game.complete ? profile.kind === 'Terraria' ? 'TCP listener reached' : 'Answered from this PC' : 'Did not answer'
    game.detail = game.complete ? profile.kind === 'Terraria'
      ? 'The TCP port answered. Terraria readiness, player count and a real join have not been verified.'
      : 'The game server answered this PC’s status query. Try joining in the game to check actual play.'
      : 'No valid reply was received. This does not prove the server is offline. Ask the Host to check the game route, then retry.'
    game.tone = game.complete ? 'neutral' : 'error'
  }
  return [saved, route, identity, credential, protocol, assignment, controls, readiness, game]
}

function nextDoctorStage(stages: FriendDoctorStage[]) {
  return stages.find(stage => stage.tone === 'error') ?? stages.find(stage => stage.freshness === 'stale')
    ?? stages.find(stage => !stage.complete)
}

export function friendDoctorTroubleshooting(stages: FriendDoctorStage[]): string {
  const next = nextDoctorStage(stages)
  return next ? `${next.label}: ${next.state}. ${next.detail}` : 'The saved app connection and selected game check are current. A real game join and saved change still need checking.'
}

export function friendDoctorReport(stages: FriendDoctorStage[], nowMs = Date.now()): string {
  return ['TogetherServer Friend connection checks', `Generated: ${new Date(nowMs).toISOString()}`,
    ...stages.map(stage => `${stage.label}: ${stage.state}${stage.checkedUtc ? `; checked ${stage.checkedUtc}` : ''}; ${stage.freshness}`),
    `Next: ${friendDoctorTroubleshooting(stages)}`,
    'App connection, permissions, server readiness and game connection are separate checks. A response does not prove a real join or saved world.',
    'Private details omitted: codes, credentials, certificate pins, addresses, IDs, names, paths and player identity.'].join('\n')
}

export type FriendConnectionDoctorProps = FriendDoctorOptions & {
  snapshot: FriendSnapshot
  gameResults: Record<string, GameEndpointResult>
  busy: boolean
  onRefresh: () => void
  onProbe: (profileId: string) => void
  onSelectProfile?: (profileId: string | null) => void
}

function FriendDoctorContent(props: FriendConnectionDoctorProps) {
  const { snapshot, gameResults, busy, onRefresh, onProbe } = props
  const selectId = useId()
  const [selection, setSelection] = useState<string | null>(null)
  const [copyState, setCopyState] = useState({ key: '', message: '' })
  const [clock, setClock] = useState(() => Date.now())
  useEffect(() => {
    if (props.nowMs !== undefined) return
    const timer = window.setInterval(() => setClock(Date.now()), 1_000)
    return () => window.clearInterval(timer)
  }, [props.nowMs])
  const selectedProfileId = props.selectedProfileId !== undefined ? props.selectedProfileId
    : selection ?? (snapshot.profiles.length === 1 ? snapshot.profiles[0].id : null)
  const nowMs = props.nowMs ?? clock
  const stages = friendConnectionDoctorStages(snapshot, gameResults, { ...props, selectedProfileId, nowMs })
  const profile = snapshot.profiles.find(item => item.id === selectedProfileId)
  const next = nextDoctorStage(stages)
  const hasIssue = stages.some(stage => stage.tone === 'error' || stage.freshness === 'stale')
    || !!snapshot.endpoint && (!connectedStates.includes(snapshot.state) || friendCheckFreshness(snapshot.lastConnectedUtc, nowMs) === 'unknown')
  const canProbe = !!profile && friendGameProbeSupported(profile) && ['Ready', 'Listening'].includes(profile.state)
    && connectedStates.includes(snapshot.state) && snapshot.protocolCompatible === true
    && stages.find(stage => stage.id === 'credential')?.complete === true
    && friendCheckFreshness(snapshot.lastConnectedUtc, nowMs) === 'current'
  const reportKey = JSON.stringify([selectedProfileId, snapshot.lastConnectedUtc, profile?.joinAddress, props.identityKey])
  const copy = async (concise: boolean) => {
    setCopyState({ key: reportKey, message: '' })
    try {
      await navigator.clipboard.writeText(concise ? friendDoctorTroubleshooting(stages) : friendDoctorReport(stages, nowMs))
      setCopyState({ key: reportKey, message: concise ? 'Explanation copied.' : 'Report copied. Private details omitted.' })
    } catch { setCopyState({ key: reportKey, message: 'Could not copy. Clipboard access is unavailable.' }) }
  }
  return <details className="connection-doctor friend-connection-doctor" open={hasIssue}>
    <summary>Connection help · {hasIssue ? 'Needs attention' : 'Saved connection checks'}{next && ` · ${next.label}: ${next.state}`}</summary>
    <div className="connection-doctor-heading"><div><h3>Check this PC’s connection</h3>
      <p>App access, controls, Host readiness and the selected game route are separate checks.</p></div>
      <Button className="secondary" disabled={busy} onClick={onRefresh}><Icon name={busy ? 'loader' : 'refresh'} />{busy ? 'Checking…' : 'Run checks'}</Button></div>
    <label htmlFor={selectId}>Server to check<Select id={selectId} value={profile?.id ?? ''} disabled={busy || snapshot.profiles.length === 0}
      onChange={event => {
        const id = event.target.value || null
        setSelection(id); props.onSelectProfile?.(id)
      }}>
      <option value="">Choose a server</option>
      {orderProfiles(snapshot.profiles).map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
    </Select></label>
    <div className="doctor-stages">{stages.map((stage, index) => <article className={`doctor-stage ${stage.tone}`} key={stage.id}>
      <span className="doctor-step">{index + 1}</span>
      <details open={stage.tone === 'error' || stage.freshness === 'stale'}>
        <summary><small>{stage.label}</small><strong>{stage.state}</strong></summary>
        <p>{stage.detail}</p>
        <small>{stage.freshness === 'saved' ? 'Protected local access; validity is checked with the Host.' : stage.checkedUtc
          ? <>{stage.freshness === 'current' ? 'Checked' : stage.freshness === 'stale' ? 'Stale check from' : 'Unverified check time'} <time dateTime={stage.checkedUtc}>{new Date(stage.checkedUtc).toLocaleString()}</time></>
          : 'No current check time.'}</small>
      </details>
    </article>)}</div>
    {next && <div className="doctor-next"><strong>Next: {next.label}</strong><p>{next.detail}</p></div>}
    <div className="actions"><Button className="secondary" disabled={busy || !canProbe}
      onClick={() => { if (canProbe && profile) onProbe(profile.id) }}>Check game connection</Button>
      <Button className="text-button" onClick={() => void copy(true)}>Copy explanation</Button>
      <Button className="text-button" onClick={() => void copy(false)}>Copy report</Button></div>
    {!canProbe && <small>{profile && !friendGameProbeSupported(profile) ? 'Follow the game join instructions; the app cannot test this connection.' : 'Choose an assigned server with current Ready or Listening status to check its game connection.'}</small>}
    {copyState.key === reportKey && copyState.message && <DismissibleNotice as="small" scope={reportKey} role="status">{copyState.message}</DismissibleNotice>}
    <small className="evidence-boundary">A successful app or game connection check does not prove that a player joined or a world change was saved.</small>
  </details>
}

export function FriendConnectionDoctor(props: FriendConnectionDoctorProps) {
  const scope = JSON.stringify([props.snapshot.connectionId, props.snapshot.endpoint, props.identityKey])
  return <FriendDoctorContent key={scope} {...props} />
}

/** Replace the card's raw cached result paragraph with scoped, dated evidence. */
export function FriendGameCheckResult({ snapshot, profile, gameResults, ...options }: FriendDoctorOptions & {
  snapshot: FriendSnapshot
  profile: PublicProfile
  gameResults: Record<string, GameEndpointResult>
}) {
  if (!gameResults[profile.id]) return null
  const stage = friendConnectionDoctorStages(snapshot, gameResults, { ...options, selectedProfileId: profile.id })
    .find(item => item.id === 'game')
  if (!stage) return null
  return <DismissibleNotice scope={JSON.stringify([snapshot.connectionId, profile.id, options.identityKey])} noticeKey={JSON.stringify([stage.checkedUtc, stage.state, stage.freshness])} className={stage.complete ? 'helper-text' : 'warning-text'} role="status">
    <strong>{stage.label}: {stage.state}</strong><p>{stage.detail}</p>
    {stage.checkedUtc && <small>{stage.freshness === 'current' ? 'Checked' : 'Previous check'}{' '}
      <time dateTime={stage.checkedUtc}>{new Date(stage.checkedUtc).toLocaleString()}</time>
      {stage.freshness !== 'current' && ' · Stale or unverified; run the check again.'}</small>}
  </DismissibleNotice>
}

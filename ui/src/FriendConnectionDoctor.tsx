import { useEffect, useId, useState } from 'react'
import { Button, Select } from './Controls'
import type { FriendSnapshot, GameEndpointResult, PublicProfile } from './contracts'
import { splitJoinAddress } from './ConnectionDetails'
import { Icon } from './Icon'

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
  AccessExpired: 'Owner access ended', HostAccessDenied: 'Access denied'
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
    detail: snapshot.endpoint ? 'This PC keeps its Host pin and separate device credential in protected local storage.' : 'Paste the current server code from the Host.',
    tone: snapshot.endpoint ? 'neutral' : 'attention', complete: !!snapshot.endpoint, checkedUtc: null, freshness: 'saved'
  }
  const route = current
    ? appStage({ id: 'route', label: 'Host TCP route', state: 'Answered', detail: 'The saved Host address answered from this PC.', tone: 'neutral', complete: true })
    : routeFailures.has(issue)
      ? appStage({ id: 'route', label: 'Host TCP route', state: 'Could not verify', detail: 'Retry the saved connection. Ask the Host to check its listener and the intended network route.', tone: 'error', complete: false })
      : waiting('route', 'Host TCP route', 'Run checks against the saved Host. An old response does not prove the route still works.')
  const identity = current
    ? appStage({ id: 'identity', label: 'Pinned Host identity', state: 'Matched', detail: 'HTTPS matched a saved certificate pin. No trust bypass was used.', tone: 'neutral', complete: true })
    : issue === 'HostIdentityMismatch'
      ? appStage({ id: 'identity', label: 'Pinned Host identity', state: 'Mismatch', detail: 'Ask the Host to review its identity or provide a new code. Do not bypass the saved pin.', tone: 'error', complete: false })
      : waiting('identity', 'Pinned Host identity', 'A fresh HTTPS response must match a saved Host pin.')
  const credential = current
    ? appStage({ id: 'credential', label: 'Saved device access', state: 'Authenticated', detail: 'The Host accepted this PC’s separate saved credential.', tone: 'neutral', complete: true })
    : credentialStates[issue] || issue === 'ApprovalPending' || snapshot.state === 'Awaiting approval'
      ? appStage({ id: 'credential', label: 'Saved device access', state: credentialStates[issue] ?? 'Awaiting approval',
          detail: issue === 'AccessExpired' ? 'The Host owner can extend or clear the access deadline. This saved connection remains available.'
            : issue === 'ApprovalPending' || snapshot.state === 'Awaiting approval' ? 'Ask the Host to approve this PC.' : 'Ask the Host to review this PC’s access. Saved access does not prove current authorization.',
          tone: issue === 'ApprovalPending' || snapshot.state === 'Awaiting approval' ? 'attention' : 'error', complete: false })
      : waiting('credential', 'Saved device access', 'Authentication must be confirmed after the pinned TLS check.')
  const protocol = current && snapshot.protocolCompatible === true
    ? appStage({ id: 'protocol', label: 'App protocol', state: 'Compatible', detail: 'Both apps can exchange the reviewed companion contract.', tone: 'neutral', complete: true })
    : snapshot.protocolCompatible === false || snapshot.state === 'Update required'
      ? appStage({ id: 'protocol', label: 'App protocol', state: 'Update required', detail: 'Update the incompatible app before requesting controls.', tone: 'error', complete: false })
      : waiting('protocol', 'App protocol', 'Protocol compatibility needs a fresh authenticated Host response.')
  const assignment = current && profile
    ? appStage({ id: 'assignment', label: 'Selected server assignment', state: 'Assigned', detail: 'The Host currently assigns this selected server to this PC.', tone: 'neutral', complete: true })
    : appStage({ id: 'assignment', label: 'Selected server assignment', state: !current ? 'Unknown' : profileId ? 'No longer assigned' : snapshot.profiles.length ? 'Choose a server' : 'None assigned',
        detail: !current ? 'Assignments need fresh authenticated status.' : profileId || !snapshot.profiles.length
          ? 'Ask the Host to assign this server under Friend access.' : 'Choose the server whose readiness and game route you want to check.',
        tone: profileId && current ? 'error' : 'attention', complete: false })
  const controls = !current || snapshot.protocolCompatible !== true || !profile
    ? waiting('controls', 'Remote controls', 'Current app access, protocol and selected-server assignment are required before controls can be described.')
    : appStage({ id: 'controls', label: 'Remote controls',
        state: snapshot.state === 'Disabled' || !snapshot.remoteControlsEnabled ? 'Paused by Host' : profile.maintenanceEnabled ? 'Maintenance' : profile.canStart || profile.canStop || profile.canExtendTimer ? 'Granted actions only' : 'Status only',
        detail: snapshot.state === 'Disabled' || !snapshot.remoteControlsEnabled ? 'The Host paused lifecycle controls. Authenticated status and separately granted chat/log access may continue.'
          : profile.maintenanceEnabled ? 'The Host paused lifecycle controls for maintenance. Ask the Host when it is finished.'
            : 'Start, Stop, Add time and View logs are separate permissions for this server. Every request is checked again by the Host.',
        tone: 'neutral', complete: true })
  const readiness = current && profile
    ? appStage({ id: 'readiness', label: 'Host game readiness', state: profile.state === 'Ready' ? 'Ready reported' : profile.state === 'Listening' ? 'Listening only' : profile.state === 'Offline' ? 'Offline' : profile.state === 'Starting' ? 'Starting' : 'Unknown',
        detail: profile.state === 'Ready' ? 'The Host reports its driver’s readiness observation. Check the game route and join separately.'
          : profile.state === 'Listening' ? 'Only a listener is reported. Game readiness, players and a real join remain unverified.'
            : profile.state === 'Offline' ? 'Start this server if permitted, or ask the Host. Run the game check after startup.'
              : 'Wait for a fresh Host readiness observation or ask the Host to review startup.',
        tone: 'attention', complete: profile.state === 'Ready' })
    : waiting('readiness', 'Host game readiness', 'Readiness belongs to the selected server and needs fresh Host status.')
  if (readiness.complete) readiness.tone = 'neutral'

  const raw = profile ? gameResults[profile.id] : undefined
  const scopeMatches = !!profile && (!options.gameResultIdentities
    || options.gameResultIdentities[profile.id] === friendGameCheckIdentity(snapshot, profile, options.identityKey))
  const result = scopeMatches ? raw : undefined
  const gameFreshness = friendCheckFreshness(result?.checkedUtc, nowMs)
  const game: FriendDoctorStage = {
    id: 'game', label: profile?.kind === 'Terraria' ? 'Game TCP port' : 'Game endpoint',
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
      : 'This game has no Friend-side fixed endpoint probe. Check the route by joining in the game.'
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
      ? 'A TCP listener is reachable. Terraria readiness, player count and a real join remain unverified.'
      : 'This game’s fixed query replied from this PC. Join in the game to check actual play.'
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
    'App connection, permissions, Host readiness and game route are separate. A reply is not a real join or saved-world check.',
    'Redacted: no codes, credentials, pins, addresses, IDs, names, paths or player identity.'].join('\n')
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
    try {
      await navigator.clipboard.writeText(concise ? friendDoctorTroubleshooting(stages) : friendDoctorReport(stages, nowMs))
      setCopyState({ key: reportKey, message: concise ? 'Troubleshooting explanation copied.' : 'Redacted report copied.' })
    } catch { setCopyState({ key: reportKey, message: 'Could not copy. Clipboard access is unavailable.' }) }
  }
  return <details className="connection-doctor friend-connection-doctor" open={hasIssue}>
    <summary>Connection Doctor · {hasIssue ? 'Needs attention' : 'Saved connection checks'}{next && ` · ${next.label}: ${next.state}`}</summary>
    <div className="connection-doctor-heading"><div><h3>Check this PC’s connection</h3>
      <p>App access, controls, Host readiness and the selected game route are separate checks.</p></div>
      <Button className="secondary" disabled={busy} onClick={onRefresh}><Icon name={busy ? 'loader' : 'refresh'} />{busy ? 'Checking…' : 'Run checks'}</Button></div>
    <label htmlFor={selectId}>Server to check<Select id={selectId} value={profile?.id ?? ''} disabled={busy || snapshot.profiles.length === 0}
      onChange={event => {
        const id = event.target.value || null
        setSelection(id); props.onSelectProfile?.(id)
      }}>
      <option value="">Choose a server</option>
      {snapshot.profiles.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
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
      onClick={() => { if (canProbe && profile) onProbe(profile.id) }}>Check game endpoint</Button>
      <Button className="text-button" onClick={() => void copy(true)}>Copy troubleshooting explanation</Button>
      <Button className="text-button" onClick={() => void copy(false)}>Copy redacted report</Button></div>
    {!canProbe && <small>{profile && !friendGameProbeSupported(profile) ? 'Use the manual game join steps; this fixed probe is unavailable.' : 'Choose an assigned server with fresh Ready or Listening status to check its game route.'}</small>}
    {copyState.key === reportKey && copyState.message && <small role="status">{copyState.message}</small>}
    <small className="evidence-boundary">A TCP, TLS, authentication, protocol or game-query success never proves a real join or saved change.</small>
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
  return <div className={stage.complete ? 'helper-text' : 'warning-text'} role="status">
    <strong>{stage.label}: {stage.state}</strong><p>{stage.detail}</p>
    {stage.checkedUtc && <small>{stage.freshness === 'current' ? 'Checked' : 'Previous check'}{' '}
      <time dateTime={stage.checkedUtc}>{new Date(stage.checkedUtc).toLocaleString()}</time>
      {stage.freshness !== 'current' && ' · Stale or unverified; run the check again.'}</small>}
  </div>
}

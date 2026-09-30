import { useState } from 'react'
import { Button } from './Controls'
import type { FriendSnapshot, GameEndpointResult, PublicProfile } from './contracts'
import { Icon } from './Icon'

type FriendDoctorStage = {
  id: string
  label: string
  state: string
  detail: string
  tone: 'neutral' | 'attention' | 'error'
  complete: boolean
}

const routeFailures = new Set(['HostPortClosed', 'HostPortTimedOut', 'HostTimedOut', 'FriendNetworkUnavailable',
  'HostUnreachable', 'InviteAddressInvalid', 'Disconnected'])
const credentialFailures = new Set(['CredentialExpired', 'CredentialRejected', 'Revoked', 'AccessExpired',
  'HostAccessDenied'])

export function friendConnectionDoctorStages(snapshot: FriendSnapshot,
  gameResults: Record<string, GameEndpointResult>): FriendDoctorStage[] {
  const connected = ['Connected', 'Disabled', 'Update required'].includes(snapshot.state)
  const issue = snapshot.connectionCode ?? ''
  const saved: FriendDoctorStage = snapshot.endpoint
    ? { id: 'saved', label: 'Saved access', state: 'Present', detail: 'This PC has a protected Host pin and device credential.', tone: 'neutral', complete: true }
    : { id: 'saved', label: 'Saved access', state: 'Not connected', detail: 'Paste the current server code from the Host.', tone: 'attention', complete: false }
  const route: FriendDoctorStage = connected
    ? { id: 'route', label: 'Host TCP route', state: 'Answered', detail: 'The saved Host address answered from this PC.', tone: 'neutral', complete: true }
    : routeFailures.has(issue)
      ? { id: 'route', label: 'Host TCP route', state: 'Could not verify', detail: snapshot.detail, tone: 'error', complete: false }
      : { id: 'route', label: 'Host TCP route', state: snapshot.endpoint ? 'Not yet proven' : 'Waiting', detail: 'Check the saved connection to test only its configured Host address.', tone: 'attention', complete: false }
  const identity: FriendDoctorStage = connected
    ? { id: 'identity', label: 'Pinned Host identity', state: 'Matched', detail: 'HTTPS matched a saved certificate pin. No trust bypass was used.', tone: 'neutral', complete: true }
    : issue === 'HostIdentityMismatch'
      ? { id: 'identity', label: 'Pinned Host identity', state: 'Mismatch', detail: snapshot.detail, tone: 'error', complete: false }
      : { id: 'identity', label: 'Pinned Host identity', state: 'Waiting for route', detail: 'Identity is checked only after the saved address answers.', tone: 'attention', complete: false }
  const credential: FriendDoctorStage = connected
    ? { id: 'credential', label: 'Saved device access', state: 'Authenticated', detail: 'The Host accepted this PC’s distinct saved credential.', tone: 'neutral', complete: true }
    : credentialFailures.has(issue) || snapshot.state === 'Awaiting approval'
      ? { id: 'credential', label: 'Saved device access', state: snapshot.state, detail: snapshot.detail, tone: 'error', complete: false }
      : { id: 'credential', label: 'Saved device access', state: 'Not yet verified', detail: 'Authentication follows the pinned TLS check.', tone: 'attention', complete: false }
  const protocol: FriendDoctorStage = connected && snapshot.protocolCompatible !== false
    ? { id: 'protocol', label: 'App protocol', state: 'Compatible', detail: `Host ${snapshot.hostVersion ?? 'unknown'} and this app ${snapshot.friendVersion ?? 'unknown'} can exchange the reviewed companion contract.`, tone: 'neutral', complete: true }
    : snapshot.protocolCompatible === false || snapshot.state === 'Update required'
      ? { id: 'protocol', label: 'App protocol', state: 'Update required', detail: snapshot.detail, tone: 'error', complete: false }
      : { id: 'protocol', label: 'App protocol', state: 'Waiting', detail: 'Protocol compatibility is reported by an authenticated Host response.', tone: 'attention', complete: false }
  const assignment: FriendDoctorStage = connected && snapshot.profiles.length > 0
    ? { id: 'assignment', label: 'Server assignment', state: `${snapshot.profiles.length} available`, detail: 'The Host assigned at least one saved server to this PC.', tone: 'neutral', complete: true }
    : { id: 'assignment', label: 'Server assignment', state: connected ? 'None assigned' : 'Waiting', detail: connected ? 'Ask the Host to assign a server under Friend access.' : 'Assignments arrive only after authentication.', tone: 'attention', complete: false }
  const results = snapshot.profiles.map(profile => gameResults[profile.id]).filter(Boolean)
  const answered = results.find(result => result.answered)
  const game: FriendDoctorStage = answered
    ? { id: 'game', label: 'Game endpoint', state: 'Answered from this PC', detail: answered.message, tone: 'neutral', complete: true }
    : results.length > 0
      ? { id: 'game', label: 'Game endpoint', state: 'Did not answer', detail: results[0].message, tone: 'error', complete: false }
      : { id: 'game', label: 'Game endpoint', state: 'Not tested', detail: 'For supported games, run the fixed game connection check. A reply still does not prove a player joined.', tone: 'attention', complete: false }
  return [saved, route, identity, credential, protocol, assignment, game]
}

function supportedProbe(profiles: PublicProfile[]) {
  return profiles.find(profile => ['Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(profile.kind) && !!profile.joinAddress)
}

export function FriendConnectionDoctor({ snapshot, gameResults, busy, onRefresh, onProbe }: {
  snapshot: FriendSnapshot
  gameResults: Record<string, GameEndpointResult>
  busy: boolean
  onRefresh: () => void
  onProbe: (profileId: string) => void
}) {
  const [copyState, setCopyState] = useState('')
  const stages = friendConnectionDoctorStages(snapshot, gameResults)
  const next = stages.find(stage => !stage.complete)
  const probe = supportedProbe(snapshot.profiles)
  const report = () => [
    'TogetherServer Friend Connection Doctor',
    `Generated: ${new Date().toISOString()}`,
    `Connection state: ${snapshot.state}`,
    `Protocol compatible: ${snapshot.protocolCompatible === false ? 'No' : snapshot.hostProtocolVersion == null ? 'Unknown' : 'Yes'}`,
    ...stages.map(stage => `${stage.label}: ${stage.state}`),
    'Redaction: no server code, credential, certificate, endpoint, IP address, Host ID, PC name, server name, profile ID, or player identity is included.'
  ].join('\n')

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(report())
      setCopyState('Redacted report copied.')
    } catch { setCopyState('Could not copy the report. Clipboard access is unavailable.') }
  }

  return <details className="connection-doctor friend-connection-doctor" open>
    <summary>Connection Doctor</summary>
    <div className="connection-doctor-heading"><div><span>Friend-side checks</span><h3>From this PC to the game</h3>
      <p>Each stage tests only the saved Host connection. TogetherServer never probes an arbitrary address or changes network settings.</p></div>
      <Button className="secondary" disabled={busy} onClick={onRefresh}><Icon name={busy ? 'loader' : 'refresh'} />{busy ? 'Checking…' : 'Run checks'}</Button></div>
    <div className="doctor-stages">{stages.map((stage, index) => <article className={`doctor-stage ${stage.tone}`} key={stage.id}>
      <span className="doctor-step">{index + 1}</span><div><small>{stage.label}</small><strong>{stage.state}</strong><p>{stage.detail}</p></div>
    </article>)}</div>
    {next && <div className="doctor-next"><strong>Next: {next.label}</strong><p>{next.detail}</p></div>}
    <div className="actions">{probe && <Button className="secondary" disabled={busy} onClick={() => onProbe(probe.id)}>Check game endpoint</Button>}
      <Button className="text-button" onClick={() => void copy()}>Copy redacted report</Button></div>
    {copyState && <small role="status">{copyState}</small>}
    <small className="evidence-boundary">A TCP, TLS, authentication, protocol, or game-query success never proves the next stage or a real game join.</small>
  </details>
}

import { useState } from 'react'
import { Button, Input } from './Controls'
import { Icon } from './Icon'
import type { Device, Run } from './contracts'
import type { Profile } from './GameProfile'
import type { InternetRouteCheck, PortDiagnostics } from './ServerReadiness'
import { AcceptanceRecorder } from './AcceptanceRecorder'

export type DoctorStage = {
  id: 'listener' | 'address' | 'route' | 'friend' | 'game' | 'join'
  label: string
  state: string
  detail: string
  tone: 'neutral' | 'attention' | 'error'
  complete: boolean
}

export function connectionDoctorStages(profile: Profile | undefined, run: Run | undefined,
  ports: PortDiagnostics | null, routeCheck: InternetRouteCheck | null, devices: Device[],
  nowMs = Date.now()): DoctorStage[] {
  const control = ports?.control
  const game = ports?.games.find(item => item.profileId === profile?.id)
  const routeAge = routeCheck ? nowMs - Date.parse(routeCheck.checkedUtc) : Number.NaN
  const route = control && routeCheck && control.state === 'Open on PC' && control.bindScope !== 'Loopback only' &&
    ['Address hint', 'Address stale'].includes(control.endpointState ?? '') && routeCheck.endpoint === control.endpoint &&
    routeCheck.port === control.port && Number.isFinite(routeAge) && routeAge >= 0 && routeAge < 5 * 60 * 1000
    ? routeCheck : null
  const assigned = devices.filter(device => !device.revoked && device.paired && !device.approvalPending &&
    !device.accessExpired && !!profile && device.assignedProfileIds.includes(profile.id))
  const freshFriend = assigned.find(device => device.lastHeartbeatUtc &&
    nowMs - Date.parse(device.lastHeartbeatUtc) >= 0 && nowMs - Date.parse(device.lastHeartbeatUtc) < 30_000)

  const listener: DoctorStage = control?.state === 'Open on PC' && control.bindScope !== 'Loopback only'
    ? { id: 'listener', label: 'Friend connection', state: 'Open on this PC', detail: control.detail, tone: 'neutral', complete: true }
    : control?.state === 'Idle' || control?.state === 'Off'
      ? { id: 'listener', label: 'Friend connection', state: control.state, detail: 'Create or reuse the server code when a Friend is ready to connect.', tone: 'attention', complete: false }
      : { id: 'listener', label: 'Friend connection', state: control?.state ?? 'Checking', detail: control?.detail ?? 'Reading the local HTTPS listener.', tone: control && control.state !== 'Opening' ? 'error' : 'attention', complete: false }

  const addressReady = control && ['Address hint', 'Address stale'].includes(control.endpointState ?? '')
  const address: DoctorStage = addressReady
    ? { id: 'address', label: 'Server-code address', state: control.endpointState === 'Address stale' ? 'Refresh due' : 'Ready to test', detail: control.endpointDetail ?? 'The code has a Host address and pinned identity.', tone: control.endpointState === 'Address stale' ? 'attention' : 'neutral', complete: control.endpointState !== 'Address stale' }
    : { id: 'address', label: 'Server-code address', state: 'Needs review', detail: control?.endpointDetail ?? 'Choose the address Friends will use before sharing the code.', tone: 'attention', complete: false }

  const routeStage: DoctorStage = control?.bindScope === 'Private mesh' || control?.bindScope === 'Advanced address'
    ? { id: 'route', label: 'Selected route', state: 'Owner-managed route', detail: 'TogetherServer cannot certify the mesh or advanced route until a Friend connects through it.', tone: 'attention', complete: false }
    : route?.state === 'Reachable'
      ? { id: 'route', label: 'Outside TCP route', state: 'Reached this PC', detail: 'The TCP check passed. Authentication and the game join still need separate tests.', tone: 'neutral', complete: true }
      : route?.state === 'Not reachable'
        ? { id: 'route', label: 'Outside TCP route', state: 'Blocked', detail: route.detail, tone: 'error', complete: false }
        : { id: 'route', label: 'Outside TCP route', state: 'Not confirmed', detail: route?.detail ?? 'Run the optional TCP test or ask a Friend on another network to connect.', tone: 'attention', complete: false }

  const friend: DoctorStage = freshFriend
    ? { id: 'friend', label: 'Friend app', state: 'Authenticated recently', detail: `${freshFriend.name} reached this Host with its saved pin and credential. Its network location is not inferred.`, tone: 'neutral', complete: true }
    : { id: 'friend', label: 'Friend app', state: assigned.length ? 'Waiting for a fresh check' : 'Not tested', detail: assigned.length ? 'Ask the Friend to open TogetherServer and check the saved connection.' : 'Send the server code privately, then have the Friend connect from the intended network.', tone: 'attention', complete: false }

  const gameReady = run?.state === 'Ready' && (game?.state === 'Open on PC' || game?.routeKind === 'Relay')
  const gameStage: DoctorStage = gameReady
    ? { id: 'game', label: 'Game server', state: 'Ready on this PC', detail: game?.detail ?? run?.detail ?? 'The game reports Ready locally.', tone: 'neutral', complete: true }
    : { id: 'game', label: 'Game server', state: run?.state ?? 'Offline', detail: game?.detail ?? run?.detail ?? 'Start the server when the Friend is ready to test.', tone: run?.state === 'Failed' || run?.state === 'Unknown' ? 'error' : 'attention', complete: false }

  const observedPlayer = run?.state === 'Ready' && run.playerCountTrusted && run.onlinePlayers !== null && run.onlinePlayers > 0
  const join: DoctorStage = observedPlayer
    ? { id: 'join', label: 'Real game join', state: 'Player activity observed', detail: `The server reported ${run.onlinePlayers} online. Confirm with the Friend that this was their intended join; a count alone does not identify them.`, tone: 'attention', complete: false }
    : { id: 'join', label: 'Real game join', state: 'Not proven', detail: 'Have the Friend join in the game, then confirm a fresh server-reported player transition and a recognizable saved change.', tone: 'attention', complete: false }

  return [listener, address, routeStage, friend, gameStage, join]
}

export function ConnectionDoctor({ profile, run, ports, routeCheck, devices, busy, routeMode,
  onRefresh, onTestRoute, onOpenAccess, onOpenDiagnostics }: {
  profile?: Profile
  run?: Run
  ports: PortDiagnostics | null
  routeCheck: InternetRouteCheck | null
  devices: Device[]
  busy: boolean
  routeMode: string
  onRefresh: () => void
  onTestRoute: () => void
  onOpenAccess: () => void
  onOpenDiagnostics: () => void
}) {
  const [friendTest, setFriendTest] = useState<{ startedMs: number; baseline: number | null;
    friendConfirmed: boolean; savedChangeConfirmed: boolean } | null>(null)
  const stages = connectionDoctorStages(profile, run, ports, routeCheck, devices)
  const next = stages.find(stage => !stage.complete)
  const assigned = devices.filter(device => !!profile && device.assignedProfileIds.includes(profile.id) &&
    device.paired && !device.revoked && !device.approvalPending && !device.accessExpired)
  const testHeartbeat = friendTest ? assigned.find(device => device.lastHeartbeatUtc &&
    Date.parse(device.lastHeartbeatUtc) >= friendTest.startedMs) : undefined
  const observedAt = run?.playerCountObservedUtc ? Date.parse(run.playerCountObservedUtc) : Number.NaN
  const testCountChanged = !!friendTest && !!run?.playerCountTrusted && run.onlinePlayers !== null &&
    Number.isFinite(observedAt) && observedAt >= friendTest.startedMs && run.onlinePlayers !== friendTest.baseline
  return <section className="connection-doctor" aria-labelledby="connection-doctor-title">
    <div className="connection-doctor-heading"><div><span>Guided check</span><h3 id="connection-doctor-title">Connection Doctor</h3><p>Follow the path from this PC to a real Friend and game join. Each result says exactly what it proves.</p></div>
      <Button className="secondary" disabled={busy} onClick={onRefresh}><Icon name={busy ? 'loader' : 'refresh'} />{busy ? 'Checking…' : 'Run checks'}</Button></div>
    {!profile && <div className="doctor-next"><strong>Add a server first</strong><p>Connection Doctor needs one saved server to test.</p></div>}
    {profile && <><div className="doctor-stages">{stages.map((stage, index) => <article className={`doctor-stage ${stage.tone}`} key={stage.id}>
      <span className="doctor-step">{index + 1}</span><div><small>{stage.label}</small><strong>{stage.state}</strong><p>{stage.detail}</p></div>
    </article>)}</div>
      {next && <div className="doctor-next"><strong>Next: {next.label}</strong><p>{next.detail}</p></div>}
      <div className="actions"><Button className="secondary" onClick={onOpenAccess}>Review Friend access</Button>
        {routeMode === 'DirectInternet' && <Button className="secondary" disabled={busy} onClick={onTestRoute}>Test outside TCP route</Button>}
        <Button className="secondary" disabled={busy} onClick={() => setFriendTest({ startedMs: Date.now(),
          baseline: run?.playerCountTrusted ? run.onlinePlayers : null, friendConfirmed: false, savedChangeConfirmed: false })}>{friendTest ? 'Restart Friend test' : 'Test with a Friend'}</Button>
        <Button className="text-button" onClick={onOpenDiagnostics}>Open detailed diagnostics</Button></div>
      {friendTest && <div className="friend-test" role="status"><div><span>Live coordinated check</span><strong>Test with a Friend</strong><small>Started {new Date(friendTest.startedMs).toLocaleTimeString()} with {friendTest.baseline === null ? 'no trusted baseline' : `${friendTest.baseline} online`}.</small></div>
        <ol><li className={testHeartbeat ? 'observed' : ''}><strong>{testHeartbeat ? 'Fresh authenticated heartbeat observed' : 'Ask the Friend to check this saved Host'}</strong><small>{testHeartbeat ? `${testHeartbeat.name} authenticated after this test began.` : 'Keep this view open, then run checks after they connect from the intended network.'}</small></li>
          <li className={testCountChanged ? 'observed' : ''}><strong>{testCountChanged ? `Trusted count changed to ${run?.onlinePlayers}` : 'Ask the Friend to join the game'}</strong><small>{testCountChanged ? 'The change happened after this test began; confirm it was the intended Friend.' : 'Refresh the player count after they reach the game world.'}</small></li></ol>
        <label className="check-row"><Input type="checkbox" checked={friendTest.friendConfirmed} disabled={!testCountChanged} onChange={event => setFriendTest(current => current && ({ ...current, friendConfirmed: event.target.checked, savedChangeConfirmed: event.target.checked ? current.savedChangeConfirmed : false }))} />The Friend confirmed this was their intended game join.</label>
        <label className="check-row"><Input type="checkbox" checked={friendTest.savedChangeConfirmed} disabled={!friendTest.friendConfirmed} onChange={event => setFriendTest(current => current && ({ ...current, savedChangeConfirmed: event.target.checked }))} />After a graceful save/restart, the Friend confirmed a recognizable in-game change remained.</label>
        {friendTest.savedChangeConfirmed && <p className="friend-test-complete">The coordinated checklist is complete in this view. Record formal external acceptance separately; TogetherServer does not turn these confirmations into automatic proof.</p>}
      </div>}
      <AcceptanceRecorder profileId={profile.id} />
      <small className="evidence-boundary">A local listener, TCP response, heartbeat, or player count never substitutes for the later stage. TogetherServer does not change firewall, router, DNS, or private-network policy.</small></>}
  </section>
}

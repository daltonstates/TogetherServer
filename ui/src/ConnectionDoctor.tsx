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
      : { id: 'listener', label: 'Friend connection', state: control?.state ?? 'Checking', detail: control?.detail ?? 'Checking whether this PC is accepting Friend app connections.', tone: control && control.state !== 'Opening' ? 'error' : 'attention', complete: false }

  const addressReady = control && ['Address hint', 'Address stale'].includes(control.endpointState ?? '')
  const address: DoctorStage = addressReady
    ? { id: 'address', label: 'Address in server code', state: control.endpointState === 'Address stale' ? 'Refresh due' : 'Ready to test', detail: control.endpointDetail ?? 'The code includes the Host address and secure identity.', tone: control.endpointState === 'Address stale' ? 'attention' : 'neutral', complete: control.endpointState !== 'Address stale' }
    : { id: 'address', label: 'Address in server code', state: 'Needs review', detail: control?.endpointDetail ?? 'Choose the address Friends will use before sharing the code.', tone: 'attention', complete: false }

  const routeStage: DoctorStage = control?.bindScope === 'Private mesh' || control?.bindScope === 'Advanced address'
    ? { id: 'route', label: 'Selected network', state: 'Set up by owner', detail: 'Ask a Friend to connect through the private mesh or advanced address to test it.', tone: 'attention', complete: false }
    : route?.state === 'Reachable'
      ? { id: 'route', label: 'Friend app port from internet', state: 'Reached this PC', detail: 'The TCP port answered from outside. The secure app connection and game join still need separate tests.', tone: 'neutral', complete: true }
      : route?.state === 'Not reachable'
        ? { id: 'route', label: 'Friend app port from internet', state: 'Not reached', detail: route.detail, tone: 'error', complete: false }
        : { id: 'route', label: 'Friend app port from internet', state: 'Not confirmed', detail: route?.detail ?? 'Test the Friend app port or ask a Friend on another network to connect.', tone: 'attention', complete: false }

  const friend: DoctorStage = freshFriend
    ? { id: 'friend', label: 'Friend app', state: 'Secure connection checked', detail: `${freshFriend.name} connected with the saved Host identity and its own saved access. This does not tell us which network they used.`, tone: 'neutral', complete: true }
    : { id: 'friend', label: 'Friend app', state: assigned.length ? 'Waiting for a fresh check' : 'Not tested', detail: assigned.length ? 'Ask the Friend to open TogetherServer and check the saved connection.' : 'Send the server code privately, then have the Friend connect from the intended network.', tone: 'attention', complete: false }

  const gameReady = run?.state === 'Ready' && (game?.state === 'Open on PC' || game?.routeKind === 'Relay')
  const gameStage: DoctorStage = gameReady
    ? { id: 'game', label: 'Game server', state: 'Ready on this PC', detail: game?.detail ?? run?.detail ?? 'The game reports Ready locally.', tone: 'neutral', complete: true }
    : { id: 'game', label: 'Game server', state: run?.state ?? 'Offline', detail: game?.detail ?? run?.detail ?? 'Start the server when the Friend is ready to test.', tone: run?.state === 'Failed' || run?.state === 'Unknown' ? 'error' : 'attention', complete: false }

  const observedPlayer = run?.state === 'Ready' && run.playerCountTrusted && run.onlinePlayers !== null && run.onlinePlayers > 0
  const join: DoctorStage = observedPlayer
    ? { id: 'join', label: 'Real game join', state: 'Player activity observed', detail: `The server reported ${run.onlinePlayers} online. Confirm with the Friend that this was their intended join; a count alone does not identify them.`, tone: 'attention', complete: false }
    : { id: 'join', label: 'Real game join', state: 'Not confirmed', detail: 'Have the Friend join in the game. Check that the player count changes, then confirm a recognizable saved change survives restart.', tone: 'attention', complete: false }

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
    <div className="connection-doctor-heading"><div><span>Guided check</span><h3 id="connection-doctor-title">Connection help</h3><p>Check the app connection, then test a real game join with a Friend.</p></div>
      <Button className="secondary" disabled={busy} onClick={onRefresh}><Icon name={busy ? 'loader' : 'refresh'} />{busy ? 'Checking…' : 'Run checks'}</Button></div>
    {!profile && <div className="doctor-next"><strong>Add a server first</strong><p>Save a server before checking its connection.</p></div>}
    {profile && <><div className="doctor-stages">{stages.map((stage, index) => <article className={`doctor-stage ${stage.tone}`} key={stage.id}>
      <span className="doctor-step">{index + 1}</span><div><small>{stage.label}</small><strong>{stage.state}</strong><p>{stage.detail}</p></div>
    </article>)}</div>
      {next && <div className="doctor-next"><strong>Next: {next.label}</strong><p>{next.detail}</p></div>}
      <div className="actions"><Button className="secondary" onClick={onOpenAccess}>Review Friend access</Button>
        {routeMode === 'DirectInternet' && <Button className="secondary" disabled={busy} onClick={onTestRoute}>Test Friend app port from internet</Button>}
        <Button className="secondary" disabled={busy} onClick={() => setFriendTest({ startedMs: Date.now(),
          baseline: run?.playerCountTrusted ? run.onlinePlayers : null, friendConfirmed: false, savedChangeConfirmed: false })}>{friendTest ? 'Restart Friend test' : 'Test with a Friend'}</Button>
        <Button className="text-button" onClick={onOpenDiagnostics}>Open server diagnostics</Button></div>
      {friendTest && <div className="friend-test" role="status"><div><span>Test together</span><strong>Test with a Friend</strong><small>Started {new Date(friendTest.startedMs).toLocaleTimeString()} with {friendTest.baseline === null ? 'no reliable starting count' : `${friendTest.baseline} online`}.</small></div>
        <ol><li className={testHeartbeat ? 'observed' : ''}><strong>{testHeartbeat ? 'Secure Friend app check received' : 'Ask the Friend to check this saved Host'}</strong><small>{testHeartbeat ? `${testHeartbeat.name} connected securely after this test began.` : 'Keep this view open, then run checks after they connect from the intended network.'}</small></li>
          <li className={testCountChanged ? 'observed' : ''}><strong>{testCountChanged ? `Player count changed to ${run?.onlinePlayers}` : 'Ask the Friend to join the game'}</strong><small>{testCountChanged ? 'The count changed after this test began. Confirm with the Friend that they joined.' : 'Refresh the player count after they reach the game world.'}</small></li></ol>
        <label className="check-row"><Input type="checkbox" checked={friendTest.friendConfirmed} disabled={!testCountChanged} onChange={event => setFriendTest(current => current && ({ ...current, friendConfirmed: event.target.checked, savedChangeConfirmed: event.target.checked ? current.savedChangeConfirmed : false }))} />The Friend confirmed this was their intended game join.</label>
        <label className="check-row"><Input type="checkbox" checked={friendTest.savedChangeConfirmed} disabled={!friendTest.friendConfirmed} onChange={event => setFriendTest(current => current && ({ ...current, savedChangeConfirmed: event.target.checked }))} />After a graceful save/restart, the Friend confirmed a recognizable in-game change remained.</label>
        {friendTest.savedChangeConfirmed && <p className="friend-test-complete">You completed this checklist. Save the results below. The app records your confirmations; it cannot verify the game join or saved change itself.</p>}
      </div>}
      <AcceptanceRecorder profileId={profile.id} />
      <small className="evidence-boundary">Each check covers a separate step. An open port or app response does not prove a game join. TogetherServer does not change firewall, router, DNS or private-network settings.</small></>}
  </section>
}

import { Button } from './Controls'
import type { Profile } from './GameProfile'
import { currentOutsideResult, type InternetRouteCheck, type PortDiagnostics } from './ServerReadiness'

function gamePorts(profile: Profile, observed: PortDiagnostics['games'][number] | undefined) {
  if (profile.kind === 'Valheim' && profile.crossplay) return 'Valheim Crossplay uses a relay; game port forwarding is not required.'
  if (profile.kind === 'Fixture') return 'The fixture has no outside game route.'
  if (profile.kind === 'Custom') return [
    `${profile.custom?.primaryProtocol ?? 'UDP'} ${profile.gamePort}`,
    ...(profile.custom?.additionalPorts ?? []).map(port => `${port.protocol} ${port.port}${port.label ? ` (${port.label})` : ''}`)
  ].join(', ')
  if (observed?.ports.length) return `${observed.protocol} ${observed.ports.join(', ')}`
  if (profile.kind === 'Valheim') return `UDP ${profile.gamePort}, ${profile.gamePort + 1}`
  if (profile.kind === 'MinecraftJava' || profile.kind === 'Terraria') return `TCP ${profile.gamePort}`
  return `UDP ${profile.gamePort}`
}

export function HostStartConnectionNotice({ profile, ports, routeCheck, onOpenConnection,
  onTestControl, testingControl = false }: {
  profile: Profile
  ports: PortDiagnostics | null
  routeCheck: InternetRouteCheck | null
  onOpenConnection?: () => void
  onTestControl?: () => void
  testingControl?: boolean
}) {
  const game = ports?.games.find(item => item.profileId === profile.id)
  const control = ports?.control
  const canTestControl = !!onTestControl && control?.state === 'Open on PC' &&
    control.bindScope !== 'Loopback only'
  const outside = currentOutsideResult(control, routeCheck)
  const controlText = !control
    ? 'The Friend app control route has not been checked in this setup review.'
    : control.state === 'Idle' || control.state === 'Off'
      ? 'Friend app access has not been opened for this server.'
    : control.state !== 'Open on PC'
      ? `Friend app TCP ${control.port} is not listening on this PC.`
      : outside?.state === 'Reachable'
        ? `An outside TCP check reached Friend app port ${control.port}. Pairing and the game route still need separate checks.`
        : outside?.state === 'Not reachable'
          ? `An outside TCP check could not reach Friend app port ${control.port}. Review the route before inviting anyone.`
          : `Friend app TCP ${control.port} is listening locally. No current outside TCP checker result is shown here; a signed successor route check is evaluated separately.`
  const needsAttention = (control && !['Idle', 'Off', 'Open on PC'].includes(control.state)) ||
    outside?.state === 'Not reachable'
  return <div className={`start-connection-notice${needsAttention ? ' attention' : ''}`}
    role="note" aria-label="Connection check before Start">
    <strong>Before starting</strong>
    <p><b>Game:</b> {gamePorts(profile, game)} Local port availability is checked again at Start.
      {profile.kind !== 'Fixture' && ' Incoming game access can only be tested after the server is listening; then check from another network and make a real join.'}</p>
    <p><b>Friend app:</b> {controlText}</p>
    {profile.kind === 'Factorio' && <small>Keep the separate RCON port private; never forward it.</small>}
    {profile.kind === 'Custom' && <small>Only expose ports required for players to join; keep admin ports private.</small>}
    {(onOpenConnection || canTestControl) && <div className="start-connection-actions">
      {canTestControl && <Button className="secondary" disabled={testingControl}
        onClick={onTestControl}>{testingControl ? 'Testing outside TCP…' : 'Test Friend app TCP from internet'}</Button>}
      {onOpenConnection && <Button className="text-button" onClick={onOpenConnection}>Review connection</Button>}
    </div>}
    {canTestControl && <small>The TCP check uses portchecker.io and does not test game ports or pairing.</small>}
  </div>
}

export function FriendStartConnectionNotice() {
  return <div className="start-connection-notice" role="note" aria-label="Connection check before Start">
    <strong>Before starting</strong>
    <p>Your Friend app has reached the Host control connection. The Host checks local game ports at Start. This does not confirm the game route; after Start, check the game connection from this PC and make a real join.</p>
  </div>
}

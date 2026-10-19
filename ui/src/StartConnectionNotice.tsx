import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import type { Profile } from './GameProfile'
import { currentOutsideResult, type InternetRouteCheck, type PortDiagnostics } from './ServerReadiness'

function gamePorts(profile: Profile, observed: PortDiagnostics['games'][number] | undefined) {
  if (profile.kind === 'Valheim' && profile.crossplay) return 'Valheim Crossplay uses a relay; game port forwarding is not required.'
  if (profile.kind === 'Fixture') return 'This test server has no real game connection.'
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
    ? 'The Friend app connection has not been checked yet.'
    : control.state === 'Idle' || control.state === 'Off'
      ? 'Friend app access has not been opened for this server.'
    : control.state !== 'Open on PC'
      ? `Friend app port ${control.port} is not open on this PC.`
      : outside?.state === 'Reachable'
        ? `The internet test reached Friend app port ${control.port}. A Friend still needs to connect and join the game.`
        : outside?.state === 'Not reachable'
          ? `The internet test could not reach Friend app port ${control.port}. Check the connection before inviting friends.`
          : `Friend app port ${control.port} is open on this PC. Internet access is untested here; shared hosting has a separate connection check.`
  const needsAttention = (control && !['Idle', 'Off', 'Open on PC'].includes(control.state)) ||
    outside?.state === 'Not reachable'
  return <DismissibleNotice className={`start-connection-notice${needsAttention ? ' attention' : ''}`} scope={profile.id}
    role="note" aria-label="Connection check before Start">
    <strong>Before starting</strong>
    <p><b>Game:</b> {gamePorts(profile, game)} Ports are checked again before starting.
      {profile.kind !== 'Fixture' && ' After starting, test joining from another network.'}</p>
    <p><b>Friend app:</b> {controlText}</p>
    {profile.kind === 'Factorio' && <small>Keep the separate RCON port private; never forward it.</small>}
    {profile.kind === 'Custom' && <small>Only expose ports required for players to join; keep admin ports private.</small>}
    {(onOpenConnection || canTestControl) && <div className="start-connection-actions">
      {canTestControl && <Button className="secondary" disabled={testingControl}
        onClick={onTestControl}>{testingControl ? 'Testing internet access…' : 'Test Friend app port from internet'}</Button>}
      {onOpenConnection && <Button className="text-button" onClick={onOpenConnection}>Review connection</Button>}
    </div>}
    {canTestControl && <small>Uses portchecker.io to test the app port. It does not test pairing or game access.</small>}
  </DismissibleNotice>
}

export function FriendStartConnectionNotice() {
  return <DismissibleNotice className="start-connection-notice" role="note" aria-label="Connection check before Start">
    <strong>Before starting</strong>
    <p>Your app can reach the Host. After the server starts, test joining the game from this PC.</p>
  </DismissibleNotice>
}

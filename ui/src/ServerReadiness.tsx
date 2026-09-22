import { Icon } from './Icon'

export type GamePortCheck = {
  profileId: string
  label: string
  ports: number[]
  protocol: string
  state: string
  detail: string
  routeKind?: 'Direct' | 'Relay' | 'Not applicable' | 'Unknown'
  kind?: string
}

export type PortDiagnostics = {
  checkedUtc: string
  games: GamePortCheck[]
  control: {
    port: number
    state: string
    detail: string
    remoteState: string
    remoteDetail: string
    bindAddress?: string
    bindScope?: string
    endpoint?: string
    endpointState?: string
    endpointDetail?: string
    lanAddresses?: { address: string; interfaceName: string; gateway: string }[]
    lanForwardDetail?: string
  }
}

export type InternetRouteCheck = {
  state: 'Reachable' | 'Not reachable' | 'Inconclusive' | 'Unavailable'
  detail: string
  port: number
  checkedUtc: string
  endpoint?: string | null
}

type ControlCheck = PortDiagnostics['control']

export function currentOutsideResult(control: ControlCheck | undefined, result: InternetRouteCheck | null) {
  if (!control || !result) return null
  const age = Date.now() - Date.parse(result.checkedUtc)
  return control.state === 'Open on PC' && control.bindScope !== 'Loopback only' &&
    ['Address hint', 'Address stale'].includes(control.endpointState ?? '') && result.endpoint === control.endpoint &&
    result.port === control.port && Number.isFinite(age) && age >= 0 && age < 5 * 60 * 1000
    ? result : null
}

function friendGuidance(control: ControlCheck | undefined, result: InternetRouteCheck | null,
  hadPreviousResult: boolean) {
  if (!control) return {
    summary: 'Checking this Host’s Friend app listener.',
    next: 'Refresh connection checks on this Host.'
  }
  if (control.state === 'Off') return {
    summary: 'Friend app connections are off on this Host.',
    next: 'Choose Invite friends, or turn on Allow Friend app connections in Settings and safety.'
  }
  if (control.state !== 'Open on PC') return {
    summary: 'This Host’s HTTPS listener is not confirmed on the configured address and port.',
    next: 'Read the Friend app listener warning and get the HTTPS listener running before checking the router.'
  }
  if (control.bindScope === 'Loopback only') return {
    summary: 'The Friend app listener only accepts connections from this PC.',
    next: 'On this Host, set Bind IP in Settings and safety to its LAN address or 0.0.0.0, save, and refresh the check.'
  }
  if (!['Address hint', 'Address stale'].includes(control.endpointState ?? '')) return {
    summary: 'The invite address needs review before the outside TCP result can apply.',
    next: 'Refresh the public address on this Host and compare it with the saved Friend app invite address.'
  }
  if (result?.state === 'Reachable') return {
    summary: `An outside checker reached HTTPS TCP ${result.port} at ${new Date(result.checkedUtc).toLocaleTimeString()}. Pinned HTTPS pairing is still untested.`,
    next: 'Ask a Friend on another network to use Join a friend, then test the game join separately.'
  }
  if (result?.state === 'Not reachable') {
    const target = control.lanAddresses?.length === 1
      ? control.lanAddresses[0].address : 'the Host LAN address shown in Connection details'
    return {
      summary: `An outside checker could not reach HTTPS TCP ${result.port}. Router forwarding, Windows Firewall, ISP filtering, or shared-address NAT may be involved.`,
      next: `On the Host router, compare its WAN IP with the invite IP. If they match, review a manual TCP ${result.port} forward to ${target} and Windows Firewall. If they differ or the WAN IP is private or shared, check upstream NAT or ask the ISP. Then retest.`
    }
  }
  if (result?.state === 'Inconclusive' || result?.state === 'Unavailable') return {
    summary: `The optional outside TCP check did not give a usable result. ${result.detail}`,
    next: 'Ask a Friend on another network to try Join a friend. Check the Host invite address if Connect fails.'
  }
  if (control.endpointState === 'Address stale') return {
    summary: 'The last public IP lookup is old, so the saved invite address needs a fresh check.',
    next: 'Refresh the public address on this Host, then ask a Friend on another network to try Join a friend.'
  }
  return {
    summary: hadPreviousResult
      ? 'The earlier outside TCP result expired or no longer matches this Host’s listener and invite address.'
      : control.remoteState === 'Friend connected'
        ? 'A paired Friend sent a heartbeat, but that PC’s network location is unknown. Outside access still needs a Friend test.'
        : 'Outside access has not been verified by a Friend on another network.',
    next: 'Ask a Friend on another network to try Join a friend. If Connect fails, the optional TCP test in Settings and safety can help diagnose the Host port.'
  }
}

function gameGuidance(game: GamePortCheck | undefined, controlPort: number | undefined) {
  if (!game) return 'Waiting for game port details. The Friend app uses its own HTTPS TCP port.'
  if (game.routeKind === 'Relay')
    return `Valheim Crossplay uses a game relay, so game port forwarding is not required. The Friend app still uses separate HTTPS TCP ${controlPort ?? 'port'}.`
  if (game.routeKind === 'Not applicable') return 'This fixture has no real game network route.'
  if (game.routeKind === 'Unknown') return 'The saved game driver is unavailable, so its game route is unknown.'
  const gameName = game.kind === 'Valheim' ? 'Valheim Steam' :
    game.kind === 'MinecraftJava' ? 'Minecraft Java' :
      game.kind === 'MinecraftBedrock' ? 'Minecraft Bedrock' : 'This game'
  return `${gameName} uses direct ${game.protocol} ${game.ports.join(', ')}. These game ports are separate from Friend app HTTPS TCP ${controlPort ?? 'port'}. If an outside game join fails, the Host may need manual forwarding of the displayed game ports. Local sockets do not prove outside access.`
}

function tone(state: string) {
  if (['Open on PC', 'Friend reached', 'Friend connected', 'Relay ready'].includes(state)) return 'good'
  if (['Closed on PC', 'Not listening', 'Loopback only'].includes(state)) return 'bad'
  return 'neutral'
}

function Check({ icon, label, state, detail }: {
  icon: 'server' | 'game' | 'plug' | 'link'
  label: string
  state: string
  detail: string
}) {
  return <div className={`readiness-item ${tone(state)}`} title={detail}>
    <span className="readiness-icon"><Icon name={icon} /></span>
    <span><small>{label}</small><strong>{state}</strong></span>
  </div>
}

export function ServerReadiness({ profileId, status, ports, routeCheck, onRefresh, busy }: {
  profileId: string
  status: string
  ports: PortDiagnostics | null
  routeCheck: InternetRouteCheck | null
  onRefresh: () => void
  busy: boolean
}) {
  const game = ports?.games.find(item => item.profileId === profileId)
  const control = ports?.control
  const gamePortLabel = game?.ports.length ? `${game.protocol} ${game.ports.join(', ')}` : 'Game ports'
  const controlProblem = control && (control.state !== 'Open on PC' || control.bindScope === 'Loopback only')
  const addressProblem = control && control.endpointState !== 'Address hint' && control.endpointState !== 'Not configured'
  const checkedRoute = currentOutsideResult(control, routeCheck)
  const guidance = friendGuidance(control, checkedRoute, routeCheck !== null)
  return <div className="server-readiness" aria-label="Server connection checks">
    <div className="readiness-row">
      <Check icon="server" label="Server" state={status} detail="TogetherServer's managed process and game readiness state." />
      <Check icon="game" label={gamePortLabel} state={game?.state ?? 'Checking'} detail={game?.detail ?? 'Reading Windows game ports.'} />
      <Check icon="plug" label={`HTTPS TCP ${control?.port ?? '…'}`} state={control?.state === 'Open on PC' && control.bindScope === 'Loopback only' ? 'Loopback only' : control?.state ?? 'Checking'} detail={control?.detail ?? 'Reading the Friend app listener.'} />
      <Check icon="link" label="Friend app" state={control?.remoteState ?? 'Checking'} detail={control?.remoteDetail ?? 'Waiting for an authenticated Friend connection.'} />
      <button className="icon-button" type="button" aria-label="Refresh connection checks" title="Refresh connection checks"
        disabled={busy} onClick={onRefresh}><Icon name="refresh" /></button>
    </div>
    {controlProblem && <p className="connection-warning"><strong>Friend app listener:</strong> {control.detail}</p>}
    {addressProblem && <p className="connection-warning"><strong>Invite address:</strong> {control.endpointDetail}</p>}
    {['Closed on PC', 'Loopback only'].includes(game?.state ?? '') && <p className="connection-warning"><strong>Game ports:</strong> {game?.detail}</p>}
    <p className="readiness-route"><strong>Outside access:</strong> {guidance.summary}</p>
    <p className="readiness-route"><strong>Next:</strong> {guidance.next}</p>
    <p className="readiness-route">If forwarding is needed, the Host forwards the Friend app port. Friend PCs connect outbound.</p>
    <p className="readiness-route"><strong>Game route:</strong> {gameGuidance(game, control?.port)}</p>
    <details className="readiness-details"><summary>Connection details</summary>
      <p><strong>Game {gamePortLabel}:</strong> {game?.detail ?? 'Waiting for a game port check.'}</p>
      <p><strong>HTTPS TCP {control?.port ?? '…'}:</strong> {control?.detail ?? 'Waiting for a listener check.'}{control?.bindAddress && ` Bound to ${control.bindAddress} (${control.bindScope ?? 'scope unknown'}).`}</p>
      <p><strong>Invite address:</strong> {control?.endpointDetail ?? 'Waiting for a public address check.'}</p>
      <p><strong>Router target:</strong> {control?.lanForwardDetail ?? 'Waiting for this PC’s LAN address.'}</p>
      {control?.lanAddresses?.map(item => <p key={`${item.interfaceName}-${item.address}`}><code>{item.address}</code> on {item.interfaceName} (gateway {item.gateway})</p>)}
      <p><strong>Friend app:</strong> {control?.remoteDetail ?? 'Waiting for an authenticated Friend connection.'}</p>
    </details>
  </div>
}

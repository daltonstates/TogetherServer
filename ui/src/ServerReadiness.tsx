import { Icon } from './Icon'

export type GamePortCheck = {
  profileId: string
  label: string
  ports: number[]
  protocol: string
  state: string
  detail: string
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
  const addressProblem = control && ['Invalid', 'Local only', 'Address stale', 'Address differs'].includes(control.endpointState ?? '')
  const checkedRoute = routeCheck !== null && routeCheck.port === control?.port &&
    Date.now() - Date.parse(routeCheck.checkedUtc) < 5 * 60 * 1000 &&
    control?.endpointState !== 'Address differs' ? routeCheck : null
  const routeSummary = checkedRoute?.state === 'Reachable'
    ? `An internet TCP probe reached port ${checkedRoute.port} at ${new Date(checkedRoute.checkedUtc).toLocaleTimeString()}. Friend pairing and game join still need separate tests.`
    : checkedRoute?.state === 'Not reachable'
      ? `An internet TCP probe could not reach port ${checkedRoute.port}. Check the Host listener, router forwarding, and Windows Firewall.`
      : control?.remoteState === 'Friend connected' || control?.remoteState === 'Friend reached'
        ? 'A Friend app reached this Host. Confirm that PC was on another network; the game join is a separate test.'
        : 'Not verified. Test the Friend app TCP port from Settings and ask a Friend on another network to connect.'
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
    <p className="readiness-route"><strong>Outside network:</strong> {routeSummary}</p>
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

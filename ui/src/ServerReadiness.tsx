import type { ReactNode } from 'react'
import { Icon } from './Icon'
import { Button } from './Controls'

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
type ReadinessTone = 'good' | 'warning' | 'bad' | 'neutral'
type ReadinessSummary = {
  label: string
  state: string
  detail: string
  tone: ReadinessTone
}
type ReadinessIssue = {
  title: string
  detail: string
  connection: boolean
  tone: 'warning' | 'bad'
}

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
    summary: 'TogetherServer is still reading the Friend connection checks.',
    next: 'Refresh the checks if this does not finish soon.'
  }
  if (control.state === 'Off') return {
    summary: 'Friend app connections are off on this Host.',
    next: 'Choose Invite friends, or turn on Friend access in Settings.'
  }
  if (control.state !== 'Open on PC') return {
    summary: 'The secure Friend listener is not confirmed on this PC.',
    next: 'Open Connection settings and resolve the Friend listener warning before checking the router.'
  }
  if (control.bindScope === 'Loopback only') return {
    summary: 'The Friend listener accepts connections only from this PC.',
    next: 'Open Connection settings and choose a LAN bind address before inviting a Friend.'
  }
  if (!['Address hint', 'Address stale'].includes(control.endpointState ?? '')) return {
    summary: 'The address used by new invites needs review.',
    next: 'Open Connection settings and refresh the public address before sharing an invite.'
  }
  if (result?.state === 'Reachable') return {
    summary: `An outside checker reached HTTPS TCP ${result.port} at ${new Date(result.checkedUtc).toLocaleTimeString()}. This does not prove pinned HTTPS pairing.`,
    next: 'Ask a Friend on another network to connect, then test the game join separately.'
  }
  if (result?.state === 'Not reachable') {
    const target = control.lanAddresses?.length === 1
      ? control.lanAddresses[0].address : 'the Host LAN address shown below'
    return {
      summary: `An outside checker could not reach HTTPS TCP ${result.port}. The router, Windows Firewall, ISP filtering, or shared-address NAT may be involved.`,
      next: `Compare the router WAN IP with the invite IP. If they match, review TCP ${result.port} forwarding to ${target} and Windows Firewall. If they differ, check upstream NAT or ask the ISP.`
    }
  }
  if (result?.state === 'Inconclusive' || result?.state === 'Unavailable') return {
    summary: `The optional outside TCP check did not give a usable result. ${result.detail}`,
    next: 'Ask a Friend on another network to connect. Review the invite address if that fails.'
  }
  if (control.endpointState === 'Address stale') return {
    summary: 'The last public IP lookup is old, so the invite address needs a fresh check.',
    next: 'Refresh the public address, then ask a Friend on another network to connect.'
  }
  return {
    summary: hadPreviousResult
      ? 'The earlier outside TCP result expired or no longer matches this listener and invite address.'
      : control.remoteState === 'Friend connected'
        ? 'A paired Friend sent an authenticated heartbeat. That PC’s network location is unknown, so outside access is not proven.'
        : 'Outside access has not been verified by a Friend on another network.',
    next: 'Ask a Friend on another network to connect. The optional outside TCP check can help if that fails.'
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
  return `${gameName} uses direct ${game.protocol} ${game.ports.join(', ')}. These game ports are separate from Friend app HTTPS TCP ${controlPort ?? 'port'}. Local sockets do not prove outside access.`
}

function friendSummary(control: ControlCheck | undefined, result: InternetRouteCheck | null): ReadinessSummary {
  if (!control) return {
    label: 'Friend access', state: 'Checking', tone: 'neutral',
    detail: 'Reading the secure Friend listener and invite address.'
  }
  if (control.state === 'Off') return {
    label: 'Friend access', state: 'Off', tone: 'neutral',
    detail: 'Connections from Friend PCs are turned off until you create an invite.'
  }
  if (control.state !== 'Open on PC') return {
    label: 'Friend access', state: 'Needs attention', tone: 'bad',
    detail: 'The secure Friend listener is not available on this PC.'
  }
  if (control.bindScope === 'Loopback only') return {
    label: 'Friend access', state: 'This PC only', tone: 'warning',
    detail: 'The listener cannot accept another PC yet.'
  }
  if (control.remoteState === 'Friend connected') return {
    label: 'Friend access', state: 'Friend connected', tone: 'good',
    detail: 'An authenticated heartbeat was received; the Friend’s network location is unknown.'
  }
  if (!['Address hint', 'Address stale'].includes(control.endpointState ?? '')) return {
    label: 'Friend access', state: 'Address needs review', tone: 'warning',
    detail: 'The address used by new invites is missing or does not match this Host.'
  }
  if (result?.state === 'Reachable') return {
    label: 'Friend access', state: 'Outside route reached', tone: 'good',
    detail: 'A TCP check reached this PC; pairing from a Friend PC is still untested.'
  }
  if (result?.state === 'Not reachable') return {
    label: 'Friend access', state: 'Outside route blocked', tone: 'bad',
    detail: 'The latest matching outside TCP check could not reach this PC.'
  }
  if (result?.state === 'Inconclusive' || result?.state === 'Unavailable') return {
    label: 'Friend access', state: 'Not confirmed', tone: 'warning',
    detail: 'The outside TCP check could not confirm whether a Friend can connect.'
  }
  if (control.endpointState === 'Address stale') return {
    label: 'Friend access', state: 'Address check due', tone: 'warning',
    detail: 'The saved public address may be out of date.'
  }
  return {
    label: 'Friend access', state: 'Needs a Friend test', tone: 'neutral',
    detail: 'The listener works on this PC, but an outside connection is not verified.'
  }
}

function gameSummary(status: string, game: GamePortCheck | undefined): ReadinessSummary {
  if (status === 'Offline') return {
    label: 'Game access', state: 'Server is off', tone: 'neutral',
    detail: 'Start the server when everyone is ready to play.'
  }
  if (status === 'Starting' || status === 'Process running' || game?.state === 'Opening') return {
    label: 'Game access', state: 'Starting', tone: 'neutral',
    detail: 'Waiting for the game server to report ready.'
  }
  if (status === 'Unknown') return {
    label: 'Game access', state: 'Unknown', tone: 'warning',
    detail: 'TogetherServer cannot safely confirm this server’s state.'
  }
  if (status === 'Failed') return {
    label: 'Game access', state: 'Needs attention', tone: 'bad',
    detail: 'The game server did not start cleanly.'
  }
  if (game?.routeKind === 'Not applicable') return {
    label: 'Game access', state: 'Fixture only', tone: 'warning',
    detail: 'This test fixture has no real game route.'
  }
  if (game?.routeKind === 'Unknown' || game?.state === 'Unknown') return {
    label: 'Game access', state: 'Route unknown', tone: 'warning',
    detail: game?.detail ?? 'Waiting for game network details.'
  }
  if (game?.state === 'Closed on PC' || game?.state === 'Loopback only') return {
    label: 'Game access', state: 'Needs attention', tone: 'bad',
    detail: game.detail
  }
  if (game?.routeKind === 'Relay' && status === 'Ready') return {
    label: 'Game access', state: 'Ready locally', tone: 'good',
    detail: 'The game relay is configured; a real Friend join is still untested.'
  }
  if (game?.state === 'Open on PC' && status === 'Ready') return {
    label: 'Game access', state: 'Ready locally', tone: 'good',
    detail: 'The game is listening on this PC; an outside game join is still untested.'
  }
  if (status === 'Ready') return {
    label: 'Game access', state: 'Ready locally', tone: 'good',
    detail: 'The server reports ready; a real Friend join is still untested.'
  }
  return {
    label: 'Game access', state: status, tone: 'neutral',
    detail: game?.detail ?? 'Waiting for game network details.'
  }
}

function connectionIssue(control: ControlCheck | undefined, result: InternetRouteCheck | null,
  hadPreviousResult: boolean): ReadinessIssue | null {
  if (!control) return null
  if (control.state === 'Off') return null
  if (control.state !== 'Open on PC') return {
    title: 'The Friend listener needs attention.', detail: control.detail, connection: true,
    tone: control.state === 'Unknown' ? 'warning' : 'bad'
  }
  if (control.bindScope === 'Loopback only') return {
    title: 'Friend access is limited to this PC.', detail: control.detail, connection: true, tone: 'warning'
  }
  if (!['Address hint', 'Address stale'].includes(control.endpointState ?? '')) return {
    title: 'The invite address needs review.',
    detail: control.endpointDetail ?? 'Refresh the public address before sharing another invite.',
    connection: true,
    tone: 'warning'
  }
  if (result?.state === 'Not reachable') return {
    title: 'Friends outside this network may not be able to connect.', detail: result.detail,
    connection: true, tone: 'bad'
  }
  if (result?.state === 'Inconclusive' || result?.state === 'Unavailable') return {
    title: 'The outside connection check was inconclusive.', detail: result.detail,
    connection: true, tone: 'warning'
  }
  if (control.endpointState === 'Address stale') return {
    title: 'Refresh the public address before sharing an invite.',
    detail: control.endpointDetail ?? 'The saved public address may no longer lead to this Host.',
    connection: true,
    tone: 'warning'
  }
  if (hadPreviousResult && !result) return {
    title: 'The previous outside check is no longer current.',
    detail: 'The check expired or no longer matches this listener and invite address.',
    connection: true,
    tone: 'warning'
  }
  return null
}

function gameIssue(status: string, game: GamePortCheck | undefined): ReadinessIssue | null {
  if (status === 'Offline' || status === 'Starting' || status === 'Process running') return null
  if (status === 'Unknown') return {
    title: 'The server state is unknown.',
    detail: 'TogetherServer blocks unsafe start and remote Stop actions until the managed process can be verified.',
    connection: false,
    tone: 'warning'
  }
  if (status === 'Failed') return {
    title: 'The game server needs attention.',
    detail: game?.detail ?? 'Refresh the checks, then open server management if the failure remains.',
    connection: false,
    tone: 'bad'
  }
  if (game?.state === 'Closed on PC' || game?.state === 'Loopback only' || game?.state === 'Unknown') return {
    title: 'The game connection needs attention.', detail: game.detail, connection: false,
    tone: game.state === 'Closed on PC' ? 'bad' : 'warning'
  }
  return null
}

function SummaryLine({ summary, icon }: { summary: ReadinessSummary; icon: 'game' | 'link' }) {
  return <div className={`readiness-summary ${summary.tone}`}>
    <span className="readiness-summary-icon"><Icon name={icon} /></span>
    <span className="readiness-summary-copy">
      <span className="readiness-summary-title"><small>{summary.label}</small><strong>{summary.state}</strong></span>
      <span className="readiness-summary-detail">{summary.detail}</span>
    </span>
  </div>
}

function TechnicalDetail({ label, value, detail, tone = 'neutral' }: {
  label: string
  value: ReactNode
  detail?: ReactNode
  tone?: ReadinessTone
}) {
  return <div className="technical-detail-row">
    <dt>{label}</dt>
    <dd>
      <span className={`technical-detail-value ${tone}`}>{value}</span>
      {detail !== undefined && detail !== null && <span className="technical-detail-description">{detail}</span>}
    </dd>
  </div>
}

export function ServerReadiness({ profileId, status, ports, routeCheck, onRefresh, onOpenConnection, busy }: {
  profileId: string
  status: string
  ports: PortDiagnostics | null
  routeCheck: InternetRouteCheck | null
  onRefresh: () => void
  onOpenConnection?: () => void
  busy: boolean
}) {
  const game = ports?.games.find(item => item.profileId === profileId)
  const control = ports?.control
  const gamePortLabel = game?.ports.length ? `${game.protocol} ${game.ports.join(', ')}` : 'Game ports'
  const checkedRoute = currentOutsideResult(control, routeCheck)
  const hadPreviousResult = routeCheck !== null
  const guidance = friendGuidance(control, checkedRoute, hadPreviousResult)
  const friend = friendSummary(control, checkedRoute)
  const gameAccess = gameSummary(status, game)
  const issue = connectionIssue(control, checkedRoute, hadPreviousResult) ?? gameIssue(status, game)
  const friendListenerDetail = control
    ? <>{control.detail}{control.bindAddress && <> Bound to <code>{control.bindAddress}</code> ({control.bindScope ?? 'scope unknown'}).</>}</>
    : 'Waiting for a listener check.'
  const outsideValue = checkedRoute?.state ?? (routeCheck ? 'Previous result expired' : 'Not checked')
  const outsideTone: ReadinessTone = checkedRoute?.state === 'Reachable' ? 'good'
    : checkedRoute?.state === 'Not reachable' ? 'bad'
      : checkedRoute || routeCheck ? 'warning' : 'neutral'
  const outsideDetail = checkedRoute
    ? <>{checkedRoute.detail}<small>Checked at {new Date(checkedRoute.checkedUtc).toLocaleTimeString()}.</small></>
    : routeCheck
      ? 'The earlier result expired or no longer matches the current listener and invite address.'
      : guidance.summary

  return <div className="server-readiness" aria-label="Server connection status">
    <div className="readiness-overview">
      <SummaryLine summary={friend} icon="link" />
      <SummaryLine summary={gameAccess} icon="game" />
    </div>

    {issue && <div className={`readiness-action ${issue.tone}`} role="status">
      <span className="readiness-action-icon"><Icon name="warning" /></span>
      <span className="readiness-action-copy"><strong>{issue.title}</strong><span>{issue.detail}</span></span>
      {issue.connection && onOpenConnection && <Button type="button" onClick={onOpenConnection}>Fix connection</Button>}
    </div>}

    <div className="readiness-footer">
      <details className="readiness-details">
        <summary><span>Technical details</span><small>Ports, addresses, and connection checks</small></summary>
        <div className="readiness-details-content">
          <div className="technical-details-grid">
            <section className="technical-detail-card" aria-labelledby={`game-details-${profileId}`}>
              <div className="technical-detail-heading">
                <span><Icon name="game" /></span>
                <div><h4 id={`game-details-${profileId}`}>Game server</h4><p>State and player connection</p></div>
              </div>
              <dl>
                <TechnicalDetail label="Current state" value={status} detail={gameAccess.detail} tone={gameAccess.tone} />
                <TechnicalDetail label="Local game ports" value={gamePortLabel} detail={game?.detail ?? 'Waiting for a game port check.'} />
                <TechnicalDetail label="Player route" value={game?.routeKind ?? 'Checking'} detail={gameGuidance(game, control?.port)} />
              </dl>
            </section>

            <section className="technical-detail-card" aria-labelledby={`friend-details-${profileId}`}>
              <div className="technical-detail-heading">
                <span><Icon name="plug" /></span>
                <div><h4 id={`friend-details-${profileId}`}>Friend app</h4><p>Secure control connection</p></div>
              </div>
              <dl>
                <TechnicalDetail label="Listener" value={control ? `HTTPS TCP ${control.port} · ${control.state}` : 'Checking'} detail={friendListenerDetail} tone={friend.tone} />
                <TechnicalDetail label="Invite address" value={control?.endpoint ? <code>{control.endpoint}</code> : control?.endpointState ?? 'Checking'} detail={control?.endpointDetail ?? 'Waiting for a public address check.'} />
                <TechnicalDetail label="Friend heartbeat" value={control?.remoteState ?? 'Not verified'} detail={control?.remoteDetail ?? 'Waiting for an authenticated Friend connection.'} />
              </dl>
            </section>

            <section className="technical-detail-card technical-detail-card-wide" aria-labelledby={`outside-details-${profileId}`}>
              <div className="technical-detail-heading">
                <span><Icon name="link" /></span>
                <div><h4 id={`outside-details-${profileId}`}>Outside connection</h4><p>Router target and internet evidence</p></div>
              </div>
              <dl>
                <TechnicalDetail label="Router target" value={control?.lanAddresses?.length === 1
                  ? <code>{control.lanAddresses[0].address}</code>
                  : control?.lanAddresses?.length ? `${control.lanAddresses.length} local addresses found` : 'Checking'}
                  detail={<>{control?.lanForwardDetail ?? 'Waiting for this PC\'s LAN address.'}
                    {!!control?.lanAddresses?.length && <span className="technical-addresses">{control.lanAddresses.map(item => <span key={`${item.interfaceName}-${item.address}`}><code>{item.address}</code><small>{item.interfaceName} · gateway {item.gateway}</small></span>)}</span>}</>} />
                <TechnicalDetail label="Outside TCP check" value={outsideValue} detail={outsideDetail} tone={outsideTone} />
                <TechnicalDetail label="Outside access" value={friend.state} detail={guidance.summary} tone={friend.tone} />
              </dl>
            </section>
          </div>
          <div className="technical-next-step"><span><Icon name="refresh" /></span><div><strong>Recommended next step</strong><p>{guidance.next}</p></div></div>
          <p className="technical-note">Only the Host forwards a port when needed. Friend PCs connect outbound.</p>
        </div>
      </details>
      <Button className="readiness-refresh" type="button" disabled={busy} onClick={onRefresh}>
        <Icon name="refresh" />{busy ? 'Checking…' : 'Refresh checks'}
      </Button>
    </div>
  </div>
}

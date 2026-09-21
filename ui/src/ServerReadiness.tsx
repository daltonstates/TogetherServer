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
  }
}

function tone(state: string) {
  if (['Open on PC', 'Friend reached', 'Relay ready'].includes(state)) return 'good'
  if (['Closed on PC'].includes(state)) return 'bad'
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

export function ServerReadiness({ profileId, status, ports, onRefresh, busy }: {
  profileId: string
  status: string
  ports: PortDiagnostics | null
  onRefresh: () => void
  busy: boolean
}) {
  const game = ports?.games.find(item => item.profileId === profileId)
  return <div className="readiness-row" aria-label="Server connection checks">
    <Check icon="server" label="Server" state={status} detail="TogetherServer's managed process and game readiness state." />
    <Check icon="game" label="Game ports" state={game?.state ?? 'Checking'} detail={game?.detail ?? 'Reading Windows game ports.'} />
    <Check icon="plug" label="Friend controls" state={ports?.control.state ?? 'Checking'} detail={ports?.control.detail ?? 'Reading the local command listener.'} />
    <Check icon="link" label="Friend route" state={ports?.control.remoteState ?? 'Checking'} detail={ports?.control.remoteDetail ?? 'Waiting for an authenticated Friend connection.'} />
    <button className="icon-button" type="button" aria-label="Refresh connection checks" title="Refresh connection checks"
      disabled={busy} onClick={onRefresh}><Icon name="refresh" /></button>
  </div>
}

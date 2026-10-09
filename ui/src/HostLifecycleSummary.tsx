import { useId } from 'react'
import { Button } from './Controls'
import type { Run } from './contracts'
import type { Profile } from './GameProfile'
import type { ActivityDestination } from './notificationState'
import { playerCountAvailability } from './PlayersPanel'

export type HostLifecycleSummaryProps = {
  profile: Pick<Profile, 'id' | 'name' | 'kind' | 'maintenance'>
  run: Run | null
  nowMs: number
  startGate: { allowed: boolean; reason?: string | null }
  lifecycleBlocked?: boolean
  busy?: boolean
  onStart?: () => void
  onOpen: (destination: ActivityDestination) => void
}

export type HostLifecycleView = {
  phase: string
  detail: string
  next: { kind: 'start'; label: string; reason: string } |
    { kind: 'navigate'; label: string; reason: string; destination: ActivityDestination }
}

export function hostLifecycleView({ profile, run, nowMs, startGate, lifecycleBlocked = false }:
  Pick<HostLifecycleSummaryProps, 'profile' | 'run' | 'nowMs' | 'startGate' | 'lifecycleBlocked'>): HostLifecycleView {
  const observed = run?.profileId === profile.id ? run : null
  const phases: Record<string, string> = {
    Offline: 'Offline', Starting: 'Starting · Waiting for game readiness',
    'Process running': 'Process running · Waiting for game readiness',
    Listening: 'Listening · Game readiness unverified',
    Ready: profile.kind === 'Fixture' ? 'Fixture ready · Synthetic evidence' : profile.kind === 'Custom'
      ? 'Ready · Custom status report' : 'Ready · Driver readiness observed',
    Stopping: 'Stopping · Waiting for confirmed exit', Failed: 'Failed · Review needed', Unknown: 'Unknown · Review needed'
  }
  const state = observed?.state ?? 'Unknown'
  const phase = Object.hasOwn(phases, state) ? phases[state] : 'Unknown · Review needed'
  const detail = observed?.detail || 'The current server state is unavailable. Review it before starting another run.'
  const navigate = (section: 'overview' | 'players', label: string, reason: string): HostLifecycleView => ({
    phase, detail, next: { kind: 'navigate', label, reason,
      destination: { workspace: 'host', section, profileId: profile.id, label } }
  })
  if (lifecycleBlocked) return { phase, detail, next: {
    kind: 'navigate', label: 'Review recovery', reason: 'Local data recovery blocks new lifecycle actions.',
    destination: { workspace: 'settings', section: 'diagnostics', profileId: profile.id, label: 'Review recovery' }
  } }
  if (profile.maintenance?.enabled) return navigate('overview', 'Continue maintenance', 'Review the maintenance guide and its remaining checks.')
  switch (observed?.state) {
    case 'Offline': return startGate.allowed
      ? { phase, detail, next: { kind: 'start', label: 'Start server', reason: 'Start rechecks the saved setup, process identity, world and ports.' } }
      : navigate('overview', 'Review server setup', startGate.reason || 'Resolve the current Start blocker before hosting.')
    case 'Starting': return navigate('overview', 'Review startup', 'The process is starting. Wait for the driver’s readiness result or review logs and health.')
    case 'Process running': return navigate('overview', 'Review startup', 'The recorded process is running. Review logs and health for game readiness.')
    case 'Listening': return navigate('overview', 'Review connection details', 'A local listener was observed. Game readiness, player count and a real game join remain unverified.')
    case 'Stopping': return navigate('overview', 'Review Stop', 'Wait for confirmed process exit. A Stop request does not prove the world was saved.')
    case 'Ready': {
      const players = playerCountAvailability(observed, nowMs)
      if (players.count === null) return navigate('players', 'Review player count', players.reason)
      if (players.count === 0 && observed.autoShutdownAtUtc) return navigate('players', 'Review empty-server timer', 'The Host owns this countdown and must recheck zero players before Stop.')
      return navigate('overview', 'Open connection details', 'Use the saved connection details and confirm a real game join separately.')
    }
    default: return navigate('overview', 'Review server', 'Review the current run and health before requesting a new Start.')
  }
}

export function HostLifecycleSummary(props: HostLifecycleSummaryProps) {
  const headingId = useId()
  const view = hostLifecycleView(props)
  return <section className="players-workspace host-lifecycle-summary" aria-labelledby={headingId}>
    <div className="players-heading"><div><h3 id={headingId}>{props.profile.name} · Next action</h3>
      <small>Observed phase</small><p><strong>{view.phase}</strong></p></div>
      {view.next.kind === 'start'
        ? <Button disabled={props.busy || !props.startGate.allowed || !props.onStart} onClick={() => {
          if (view.next.kind === 'start' && props.run?.profileId === props.profile.id &&
              props.run.state === 'Offline' && props.startGate.allowed && !props.busy &&
              !props.lifecycleBlocked && !props.profile.maintenance?.enabled) props.onStart?.()
        }}>{view.next.label}</Button>
        : <Button className="secondary" onClick={() => {
          if (view.next.kind === 'navigate') props.onOpen(view.next.destination)
        }}>{view.next.label}</Button>}
    </div>
    <p className="helper-text">{view.detail}</p>
    <small>{view.next.reason}</small>
    <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">{view.phase}. Next action: {view.next.label}.</span>
  </section>
}

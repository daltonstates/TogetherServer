import { useId } from 'react'
import { Button } from './Controls'
import { Icon } from './Icon'
import './ServerStartupProgress.css'

type StartupState = {
  state?: string
  requestPending?: boolean
  playerObservationSource?: string | null
  runOperationId?: string | null
}

export function serverStartupStage({ state, requestPending, playerObservationSource, runOperationId }: StartupState) {
  if (state === 'Starting' || state === 'Process running' ||
    state === 'Unknown' && playerObservationSource === 'ObservationPending' && runOperationId) return 'game'
  if (requestPending && (!state || state === 'Offline')) return 'launch'
  return null
}

export function ServerStartupProgress({ detail, host = true, onOpenLogs, ...state }: StartupState & {
  detail?: string
  host?: boolean
  onOpenLogs?: () => void
}) {
  const titleId = useId()
  const detailId = useId()
  const stage = serverStartupStage(state)
  if (!stage) return null
  const title = stage === 'launch' ? 'Checking setup and starting' : 'Waiting for the game'
  const explanation = stage === 'launch'
    ? host ? 'Checking the saved setup and available ports, then opening the server app.'
      : 'Waiting for the Host to check the setup and open the server app.'
    : detail || 'The server app has started. Waiting for the game to report Ready.'
  const steps = ['Launch server', 'Check game', 'Ready']
  const currentStep = stage === 'launch' ? 0 : 1
  return <section className="server-startup" aria-label="Server startup">
    <div className="server-startup-heading">
      <div role="status" aria-atomic="true"><strong id={titleId}>{title}</strong><p id={detailId}>{explanation}</p></div>
      {onOpenLogs && <Button className="text-button" onClick={onOpenLogs}><Icon name="logs" />View startup logs</Button>}
    </div>
    <div className="server-startup-bar" role="progressbar" aria-labelledby={titleId} aria-describedby={detailId}
      aria-valuetext={title}><span /></div>
    <ol className="server-startup-steps" aria-label="Startup steps">{steps.map((step, index) =>
      <li key={step} className={index < currentStep ? 'complete' : index === currentStep ? 'current' : undefined}
        aria-current={index === currentStep ? 'step' : undefined}>
        <span aria-hidden="true">{index < currentStep ? <Icon name="check" size={14} /> : index + 1}</span>{step}
      </li>)}</ol>
  </section>
}

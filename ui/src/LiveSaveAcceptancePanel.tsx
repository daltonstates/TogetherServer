import { useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { parseBasicResult } from './contracts'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'
import { useConfirmation } from './Confirmation'
import { Icon, type IconName } from './Icon'
import './LiveSavePresentation.css'

type Stage = { id: string; state: string; detail: string; source: string | null }
type Test = { testId: string; state: string; reviewed: boolean;
  artifact: { testId: string; version: { versionHash: string }; payloadSha256: string } | null; stages: Stage[] }
type Acceptance = { accepted: boolean; code: string; message: string; stages: Stage[]; tests: Test[] }
const stageLabels: Record<string, string> = { completion: 'Save completion', snapshot: 'Protected copy',
  transfer: 'Transfer to another PC', load: 'Load in the game', change: 'Recognizable change', restart: 'Saved restart' }
const testSteps: { label: string; icon: IconName; ids: string[]; evidence: string }[] = [
  { label: 'Capture a save', icon: 'archive', ids: ['completion', 'snapshot'], evidence: 'App checks' },
  { label: 'Transfer the copy', icon: 'users', ids: ['transfer'], evidence: 'App check' },
  { label: 'Check in the game', icon: 'game', ids: ['load', 'change', 'restart'], evidence: 'Your observations' }
]
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid live-save acceptance response.')
  return value as Record<string, unknown>
}
function text(value: unknown, maximum = 1500): string {
  if (typeof value !== 'string' || value.length > maximum) throw new Error('Invalid live-save acceptance text.')
  return value
}
function identity(value: unknown, hash = false): string {
  const result = text(value, hash ? 64 : 36)
  if (!(hash ? /^[A-F0-9]{64}$/ : /^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/i).test(result))
    throw new Error('Invalid live-save test identity.')
  return result
}
function stages(value: unknown): Stage[] {
  if (!Array.isArray(value) || value.length !== 6) throw new Error('The six live-save stages are required.')
  const sources: Record<string, string> = { completion: 'ObservedExactRun', snapshot: 'VerifiedImmutableSnapshot',
    transfer: 'SignedSeparateDeviceReceipt', load: 'OwnerReported', change: 'OwnerReported', restart: 'OwnerReported' }
  const parsed = value.map(item => {
    const source = object(item)
    if (typeof source.id !== 'string' || !Object.hasOwn(sources, source.id) ||
      !['Passed', 'Unverified'].includes(String(source.state)) || source.source !== sources[source.id])
      throw new Error('Invalid live-save evidence stage.')
    return { id: text(source.id, 40), state: text(source.state, 80), detail: text(source.detail),
      source: source.source == null ? null : text(source.source, 80) }
  })
  if (new Set(parsed.map(stage => stage.id)).size !== 6) throw new Error('A live-save evidence stage was repeated.')
  return parsed
}
export function parseLiveSaveAcceptance(value: unknown): Acceptance {
  const source = object(value)
  if (typeof source.accepted !== 'boolean' || !Array.isArray(source.tests) || source.tests.length > 32)
    throw new Error('Invalid installed live-save acceptance.')
  return { accepted: source.accepted, code: text(source.code, 100), message: text(source.message), stages: stages(source.stages),
    tests: source.tests.map(item => {
      const test = object(item)
      if (typeof test.reviewed !== 'boolean') throw new Error('Invalid test review state.')
      const testId = identity(test.testId)
      const artifact = test.artifact == null ? null : object(test.artifact)
      if (artifact && (artifact.testOnly !== true || identity(artifact.testId) !== testId))
        throw new Error('The private test identity changed.')
      return { testId, state: text(test.state, 80), reviewed: test.reviewed, stages: stages(test.stages),
        artifact: artifact === null ? null : { testId, version: { versionHash: identity(object(artifact.version).versionHash, true) },
          payloadSha256: identity(artifact.payloadSha256, true) } }
    }) }
}

export function LiveSaveAcceptancePanel({ profileId, visible = true }: { profileId: string; visible?: boolean }) {
  const [view, setView] = useState<Acceptance | null>(null)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const [capturing, setCapturing] = useState(false)
  const { confirm, confirmationDialog } = useConfirmation()
  const base = `/api/local/profiles/${profileId}/live-save-acceptance`
  const refresh = async (signal?: AbortSignal) => setView(await getLocalJson(base, parseLiveSaveAcceptance, signal))
  useSingleFlightPolling(refresh, 30000, cause => setMessage(errorMessage(cause)), visible, profileId)
  const change = async (suffix: string, body: object) => {
    if (busy) return
    setBusy(true)
    setCapturing(suffix === '/tests')
    try { const result = await changeJson(`${base}${suffix}`, 'POST', parseBasicResult, body); setMessage(result.message); await refresh() }
    catch (cause) { setMessage(errorMessage(cause)) }
    finally { setBusy(false); setCapturing(false) }
  }
  const test = view ? [...view.tests].reverse().find(item => item.artifact !== null &&
    ['ArtifactReady', 'Accepted'].includes(item.state)) : undefined
  const synthetic = view?.code === 'SyntheticEvidenceOnly'
  const nextStage = !synthetic && test?.artifact && test.stages.some(stage => stage.id === 'transfer' && stage.state === 'Passed')
    ? ['load', 'change', 'restart'].find(id => !test.stages.some(stage => stage.id === id && stage.state === 'Passed')) : null
  const pending = view?.tests.some(item => item.state === 'Capturing') === true
  const reviews = view?.tests.filter(item => !item.reviewed && ['Failed', 'Interrupted'].includes(item.state)) ?? []
  const accepted = view?.accepted === true && !synthetic
  const saving = capturing || pending
  const currentStages = test?.stages ?? view?.stages ?? []
  const complete = (ids: string[]) => ids.every(id => currentStages.some(stage => stage.id === id && stage.state === 'Passed'))
  const currentStep = testSteps.findIndex(step => !complete(step.ids))
  const label = saving ? 'Saving' : !view ? 'Checking' : accepted ? 'Ready' : reviews.length ? 'Review needed' : 'Needs setup'
  const reason = saving ? 'Waiting for a fresh game save and a protected private copy.' :
    !view ? 'Checking save support for this installation.' : synthetic ? 'Development evidence cannot accept a real game installation.' :
    reviews.length ? 'Review the interrupted test before capturing another copy.' :
    nextStage ? `Use this transferred test copy to verify ${nextStage === 'load' ? 'a game load' : nextStage === 'change' ? 'a recognizable saved change' : 'a saved restart'}.` :
    test && !complete(['transfer']) ? 'Transfer this private copy to a different approved PC.' :
    accepted ? 'This installation passed all six checks; setup changes require review.' :
    'Capture a private test copy to check this exact installation.'
  const captureTest = async () => {
    if (busy || !view || synthetic || pending || reviews.length) return
    if (await confirm({ title: 'Capture a private test save?', confirmLabel: 'Capture test save',
      description: 'This asks the running game to save and makes a sealed test copy using its reviewed driver. A distinct approved receiving PC must verify the transfer before game checks can be confirmed. No world is replaced.' }))
      await change('/tests', { requestId: crypto.randomUUID() })
  }
  return <section className="live-save-presentation live-save-acceptance-card" aria-label="Live-save installation test" hidden={!visible}>
    {confirmationDialog}
    <details className="live-save-installation-disclosure">
      <summary className="live-save-installation-summary">
        <span className="live-save-card-icon"><Icon name={saving ? 'loader' : 'shield'} size={21} /></span>
        <span className="live-save-installation-title"><strong>Automatic recovery copies</strong>
          <span>{accepted ? 'accepted for this installation' : 'installation test required'}</span></span>
        <span className="live-save-state" data-tone={accepted ? 'ready' : reviews.length ? 'warning' : 'neutral'}>{label}</span>
        <Icon name="chevronDown" />
      </summary>
      <div className="live-save-installation-body" aria-busy={busy}>
        <p className="live-save-reason">{reason}</p>
        <ol className="live-save-test-steps" aria-label="Installation test steps">
          {testSteps.map((step, index) => {
            const done = complete(step.ids)
            const count = step.ids.filter(id => currentStages.some(stage => stage.id === id && stage.state === 'Passed')).length
            return <li key={step.label} data-state={done ? 'complete' : index === currentStep ? 'current' : 'pending'}
              aria-current={index === currentStep ? 'step' : undefined}>
              <span className="live-save-step-marker"><Icon name={done ? 'check' : step.icon} /></span>
              <div><strong>{index + 1}. {step.label}</strong><span>{count} of {step.ids.length} · {step.evidence}</span></div>
            </li>
          })}
        </ol>
        {test?.artifact && <div className="live-save-current-test">
          <Icon name="copy" /><span>Current test <strong>{test.testId.slice(0, 8)}</strong>
            <span>Save {test.artifact.version.versionHash.slice(0, 12)}</span></span>
        </div>}
        <div className="live-save-card-actions">
          {nextStage && test?.artifact && <Button className="primary" disabled={busy} onClick={async () => {
            if (await confirm({ title: `Confirm actual game ${nextStage}?`, confirmLabel: 'Record my observation',
              description: `Confirm only after you observed ${nextStage} using this exact transferred test copy in a disposable world. This is saved as an owner report, not an automatic game check.` }))
              await change(`/tests/${test.testId}/confirm`, { versionHash: test.artifact!.version.versionHash,
                payloadSha256: test.artifact!.payloadSha256, stage: nextStage, confirmed: true })
          }}><Icon name="game" />I verified {nextStage} in the game</Button>}
          {test && !complete(['transfer']) && !synthetic && <Button className="secondary" disabled={busy}
            onClick={() => void refresh().catch(cause => setMessage(errorMessage(cause)))}><Icon name="refresh" />Check transfer</Button>}
          <Button className={test || accepted ? 'text-button' : 'primary'} disabled={busy || !view || synthetic || pending || reviews.length > 0}
            onClick={() => void captureTest()}><Icon name={capturing ? 'loader' : 'archive'} />
            {capturing ? 'Saving…' : 'Capture private test save'}</Button>
        </div>
        {reviews.map(item => <div className="live-save-review" key={item.testId}>
          <Icon name="warning" /><Button className="text-button" disabled={busy}
            onClick={() => void change(`/tests/${item.testId}/review`, { acknowledge: true })}>
            Acknowledge {item.state.toLowerCase()} test {item.testId.slice(0, 8)}; keep its files</Button>
        </div>)}
        {message && <DismissibleNotice scope={profileId} className="notice" role="status">{message}</DismissibleNotice>}
        <details className="live-save-disclosure">
          <summary><Icon name="logs" />Evidence and installation details<Icon name="chevronDown" /></summary>
          <div className="live-save-disclosure-body">
            <p>{view?.message ?? 'Checking installed live-save acceptance…'}</p>
            <p>Test copies stay private and separate from playable recovery saves. App evidence covers save, copy and transfer; game checks are your own reports.</p>
            <ol className="live-save-evidence-list">{currentStages.map(stage => <li key={stage.id}>
              <div><strong>{stageLabels[stage.id]}</strong><span>{stage.state}</span></div>
              <p>{stage.detail}</p><small>{stage.source}</small>
            </li>)}</ol>
          </div>
        </details>
        {!!view?.tests.length && <details className="live-save-disclosure"><summary>
          <Icon name="clock" />Private test history<Icon name="chevronDown" /></summary>
          <div className="live-save-disclosure-body">
            <p>Retained tests do not establish acceptance for the current installation. Only a current ready test can record new game observations.</p>
            <ul className="live-save-test-history">{[...view.tests].reverse().map(item => <li key={item.testId}>
              <span>Test {item.testId.slice(0, 8)} · {item.state}</span>
              {item.artifact && <code>Save {item.artifact.version.versionHash.slice(0, 12)}</code>}
            </li>)}</ul>
          </div>
        </details>}
      </div>
    </details>
  </section>
}

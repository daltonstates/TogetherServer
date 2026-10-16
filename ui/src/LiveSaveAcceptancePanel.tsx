import { useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { parseBasicResult } from './contracts'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'
import { useConfirmation } from './Confirmation'

type Stage = { id: string; state: string; detail: string; source: string | null }
type Test = { testId: string; state: string; reviewed: boolean;
  artifact: { testId: string; version: { versionHash: string }; payloadSha256: string } | null; stages: Stage[] }
type Acceptance = { accepted: boolean; code: string; message: string; stages: Stage[]; tests: Test[] }
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
  const { confirm, confirmationDialog } = useConfirmation()
  const base = `/api/local/profiles/${profileId}/live-save-acceptance`
  const refresh = async (signal?: AbortSignal) => setView(await getLocalJson(base, parseLiveSaveAcceptance, signal))
  useSingleFlightPolling(refresh, 30000, cause => setMessage(errorMessage(cause)), visible, profileId)
  const change = async (suffix: string, body: object) => {
    if (busy) return
    setBusy(true)
    try { const result = await changeJson(`${base}${suffix}`, 'POST', parseBasicResult, body); setMessage(result.message); await refresh() }
    catch (cause) { setMessage(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  const test = view ? [...view.tests].reverse().find(item => item.artifact !== null &&
    ['ArtifactReady', 'Accepted'].includes(item.state)) : undefined
  const nextStage = test?.artifact && test.stages.some(stage => stage.id === 'transfer' && stage.state === 'Passed')
    ? ['load', 'change', 'restart'].find(id => !test.stages.some(stage => stage.id === id && stage.state === 'Passed')) : null
  return <details className="advanced-block" hidden={!visible}>
    {confirmationDialog}
    <summary>Automatic recovery copies · {view?.accepted ? 'accepted for this installation' : 'installation test required'}</summary>
    <p>{view?.message ?? 'Checking installed live-save acceptance…'}</p>
    <p>Test copies stay private and separate from playable recovery saves. Completion, sealed copying and transfer need app evidence. Game load, a recognizable change and restart are recorded as your own reports.</p>
    <ol>{view?.stages.map(stage => <li key={stage.id}><strong>{stage.id}: {stage.state}</strong>{stage.source && ` · ${stage.source}`}<p>{stage.detail}</p></li>)}</ol>
    {message && <DismissibleNotice scope={profileId} className="notice" role="status">{message}</DismissibleNotice>}
    <Button className="secondary" disabled={busy || !view || view.code === 'SyntheticEvidenceOnly'} onClick={async () => {
      if (await confirm({ title: 'Capture a private test save?', confirmLabel: 'Capture test save',
        description: 'This asks the running game to save and makes a sealed test copy using its reviewed driver. A distinct approved receiving PC must verify the transfer before game checks can be confirmed. No world is replaced.' }))
        await change('/tests', { requestId: crypto.randomUUID() })
    }}>Capture private test save</Button>
    {test?.artifact && <p>Test {test.testId.slice(0, 8)} · save {test.artifact.version.versionHash.slice(0, 12)}. Enable peer exchange with an approved receiver to verify its private transfer.</p>}
    {nextStage && test?.artifact && <Button className="secondary" disabled={busy} onClick={async () => {
      if (await confirm({ title: `Confirm actual game ${nextStage}?`, confirmLabel: 'Record my observation',
        description: `Confirm only after you observed ${nextStage} using this exact transferred test copy in a disposable world. This is saved as an owner report, not an automatic game check.` }))
        await change(`/tests/${test.testId}/confirm`, { versionHash: test.artifact!.version.versionHash,
          payloadSha256: test.artifact!.payloadSha256, stage: nextStage, confirmed: true })
    }}>I verified {nextStage} in the game</Button>}
    {view?.tests.filter(item => !item.reviewed && ['Failed', 'Interrupted'].includes(item.state)).map(item =>
      <Button key={item.testId} className="text-button" disabled={busy} onClick={() => void change(`/tests/${item.testId}/review`, { acknowledge: true })}>
        Acknowledge {item.state.toLowerCase()} test {item.testId.slice(0, 8)}; keep its files
      </Button>)}
    {!!view?.tests.length && <details><summary>Private test history</summary>
      <p>Retained tests do not establish acceptance for the current installation. Only a current ready test can record new game observations.</p>
      <ul>{[...view.tests].reverse().map(item => <li key={item.testId}>
        Test {item.testId.slice(0, 8)} · {item.state}
        {item.artifact && <span> · save {item.artifact.version.versionHash.slice(0, 12)}</span>}
      </li>)}</ul>
    </details>}
  </details>
}

import { useRef, useState } from 'react'
import { changeJson, errorMessage } from './api'
import { Button } from './Controls'
import { ContractError, type Decoder } from './contracts'

const stageLabels = { completion: 'Game save completion', snapshot: 'Immutable copy', transfer: 'Other-PC transfer and receipt',
  load: 'Game load on that PC', change: 'Recognizable change', restart: 'Saved restart' }
export type LiveSaveAttempt = { requestId: string; state: string; code: string; message: string; versionHash: string | null; versionNumber: number | null }
export type LiveSaveStatus = { available: boolean; message: string; game: string | null; code: string | null;
  stages: { id: string; state: 'Unverified'; detail: string }[]; resumePending: boolean; lastAttempt: LiveSaveAttempt | null }
function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError('Invalid live-save response')
  return value as Record<string, unknown>
}
function text(value: unknown, max = 500): string {
  if (typeof value !== 'string' || value.length > max) throw new ContractError('Invalid live-save text')
  return value
}
function parseAttempt(value: unknown): LiveSaveAttempt {
  const source = record(value)
  const requestId = text(source.requestId, 36)
  if (!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(requestId) ||
    typeof source.state !== 'string' || !['Requested', 'Capturing', 'Publishing', 'Published', 'Failed', 'Canceled', 'Withdrawn'].includes(source.state) ||
    source.state === 'Published' && (source.versionHash == null || source.versionNumber == null) ||
    source.versionHash != null && !/^[a-f0-9]{64}$/i.test(text(source.versionHash, 64)) ||
    source.versionNumber != null && (typeof source.versionNumber !== 'number' || !Number.isSafeInteger(source.versionNumber) || source.versionNumber < 1))
    throw new ContractError('Invalid live-save attempt')
  return { requestId, state: source.state, code: text(source.code, 100), message: text(source.message),
    versionHash: source.versionHash == null ? null : text(source.versionHash, 64), versionNumber: source.versionNumber == null ? null : source.versionNumber as number }
}
export const parseLiveSaveStatus: Decoder<LiveSaveStatus> = value => {
  const source = record(value)
  const message = text(source.message)
  const game = source.game == null ? null : text(source.game, 30)
  const code = source.code == null ? null : text(source.code, 100)
  const resumePending = source.resumePending ?? false
  if (typeof source.available !== 'boolean' || typeof resumePending !== 'boolean' || !message ||
    source.available && (game !== 'Fixture' || code !== 'StagingFixtureReady' || resumePending))
    throw new ContractError('Unaccepted live-save capability')
  const stages: LiveSaveStatus['stages'] = source.stages == null ? [] : (() => {
    if (!Array.isArray(source.stages) || source.stages.length !== 6) throw new ContractError('Invalid live-save acceptance stages')
    return source.stages.map((value, index) => {
      const stage = record(value)
      if (stage.id !== Object.keys(stageLabels)[index] || stage.state !== 'Unverified')
        throw new ContractError('Unverified game acceptance cannot become a pass')
      return { id: String(stage.id), state: 'Unverified', detail: text(stage.detail) }
    })
  })()
  if (source.available && stages.length !== 6) throw new ContractError('Incomplete fixture capability')
  return { available: source.available, message, game, code, stages, resumePending,
    lastAttempt: source.lastAttempt == null ? null : parseAttempt(source.lastAttempt) }
}
const parseAction: Decoder<{ ok: boolean; code: string; message: string; attempt: LiveSaveAttempt | null }> = value => {
  const source = record(value)
  if (typeof source.ok !== 'boolean') throw new ContractError('Invalid live-save action')
  return { ok: source.ok, code: text(source.code, 100), message: text(source.message),
    attempt: source.attempt == null ? null : parseAttempt(source.attempt) }
}

export function LiveSaveControls({ profileId, status, onUpdated }:
  { profileId: string; status: LiveSaveStatus; onUpdated: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const retry = useRef<{ profileId: string; id: string } | null>(null)
  const act = async (action: 'save' | 'withdraw' | 'resume') => {
    setBusy(true)
    if (!retry.current || retry.current.profileId !== profileId) retry.current = { profileId, id: crypto.randomUUID() }
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/shared-world/live/${action}`, 'POST', parseAction,
        action === 'resume' ? undefined : { requestId: action === 'withdraw' ? status.lastAttempt?.requestId : retry.current.id })
      setMessage(`${result.code}: ${result.message}`)
      if (result.ok || result.attempt && !['Requested', 'Capturing', 'Publishing'].includes(result.attempt.state)) retry.current = null
      await onUpdated()
    } catch (error) { setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  return <section aria-label="Live save sharing">
    <div className="actions"><Button className="secondary" disabled={busy || !status.available} title={status.message}
      onClick={() => void act('save')}>{busy ? 'Working…' : 'Save and share now'}</Button>
      {status.resumePending && <Button className="secondary" disabled={busy} onClick={() => void act('resume')}>Retry save resume</Button>}
      {status.lastAttempt && status.lastAttempt.state !== 'Published' && status.lastAttempt.state !== 'Withdrawn' &&
        <Button className="text-button" disabled={busy} onClick={() => void act('withdraw')}>Withdraw this attempt</Button>}</div>
    <p className="helper-text">{status.message}</p>
    {status.lastAttempt && <p>Last attempt: {status.lastAttempt.state}. {status.lastAttempt.message}</p>}
    {message && <p role="status">{message}</p>}
    {status.stages.length > 0 && <details><summary>Game acceptance checks</summary>
      <p>Each real game needs its own save, transfer, load and restart evidence. Owner load confirmations do not enable live capture.</p>
      <ul>{status.stages.map(stage => <li key={stage.id}>{stageLabels[stage.id as keyof typeof stageLabels]}: {stage.state}. {stage.detail}</li>)}</ul>
    </details>}
  </section>
}

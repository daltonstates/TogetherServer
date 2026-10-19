import { useId, useRef, useState } from 'react'
import { changeJson, errorMessage } from './api'
import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { ContractError, type Decoder } from './contracts'
import { Icon, type IconName } from './Icon'
import './LiveSavePresentation.css'

const stageLabels = { completion: 'Game finished saving', snapshot: 'Protected save copy', transfer: 'Copy received on another PC',
  load: 'World loaded on that PC', change: 'Visible saved change', restart: 'Change survived restart' }
const attemptLabels: Record<string, string> = { Requested: 'Waiting', Capturing: 'Copying', Publishing: 'Sharing',
  Published: 'Shared', Unchanged: 'No changes', Failed: 'Failed', Canceled: 'Canceled', Withdrawn: 'Canceled' }
export type LiveSaveAttempt = { requestId: string; state: string; code: string; message: string; versionHash: string | null; versionNumber: number | null }
export type LiveSaveStatus = { available: boolean; message: string; game: string | null; code: string | null;
  stages: { id: string; state: 'Unverified' | 'Passed'; detail: string; source?: string; observedUtc?: string }[]; resumePending: boolean; lastAttempt: LiveSaveAttempt | null }
const evidenceSources = ['ObservedExactRun', 'VerifiedImmutableSnapshot', 'SignedSeparateDeviceReceipt', 'OwnerReported', 'OwnerReported', 'OwnerReported']
const realGames = ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria']
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
    typeof source.state !== 'string' || !['Requested', 'Capturing', 'Publishing', 'Published', 'Unchanged', 'Failed', 'Canceled', 'Withdrawn'].includes(source.state) ||
    ['Published', 'Unchanged'].includes(source.state) && (source.versionHash == null || source.versionNumber == null) ||
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
    source.available && (resumePending || !(game === 'Fixture' && code === 'StagingFixtureReady' ||
      realGames.includes(game ?? '') && code === 'LiveSaveReady')))
    throw new ContractError('Unaccepted live-save capability')
  const stages: LiveSaveStatus['stages'] = source.stages == null ? [] : (() => {
    if (!Array.isArray(source.stages) || source.stages.length !== 6) throw new ContractError('Invalid live-save acceptance stages')
    return source.stages.map((value, index) => {
      const stage = record(value)
      if (stage.id !== Object.keys(stageLabels)[index] || stage.state !== 'Unverified' && stage.state !== 'Passed')
        throw new ContractError('Invalid live-save evidence stage')
      const passed = stage.state === 'Passed'
      if (passed && (!realGames.includes(game ?? '') || stage.source !== evidenceSources[index] ||
          typeof stage.observedUtc !== 'string' || stage.observedUtc.length > 80 ||
          !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(stage.observedUtc) ||
          !Number.isFinite(Date.parse(stage.observedUtc))))
        throw new ContractError('Accepted stages require their exact evidence source and observation time')
      return { id: String(stage.id), state: stage.state as 'Unverified' | 'Passed', detail: text(stage.detail),
        ...(passed ? { source: stage.source as string, observedUtc: stage.observedUtc as string } : {}) }
    })
  })()
  if (source.available && (stages.length !== 6 || game !== 'Fixture' && stages.some(stage => stage.state !== 'Passed')))
    throw new ContractError('Incomplete live-save capability')
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
  const [action, setAction] = useState<'save' | 'withdraw' | 'resume'>('save')
  const [message, setMessage] = useState('')
  const [messageResult, setMessageResult] = useState(0)
  const [detailsOpen, setDetailsOpen] = useState(false)
  const detailsId = useId()
  const showMessage = (text: string) => { setMessage(text); setMessageResult(result => result + 1) }
  const retry = useRef<{ profileId: string; id: string } | null>(null)
  const pending = !!status.lastAttempt && ['Requested', 'Capturing', 'Publishing'].includes(status.lastAttempt.state)
  const act = async (action: 'save' | 'withdraw' | 'resume') => {
    if (busy || action === 'save' && (!status.available || pending)) return
    setAction(action)
    setBusy(true)
    if (!retry.current || retry.current.profileId !== profileId) retry.current = { profileId, id: crypto.randomUUID() }
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/shared-world/live/${action}`, 'POST', parseAction,
        action === 'resume' ? undefined : { requestId: action === 'withdraw' ? status.lastAttempt?.requestId : retry.current.id })
      showMessage(result.message)
      if (result.ok || result.attempt && !['Requested', 'Capturing', 'Publishing'].includes(result.attempt.state)) retry.current = null
      await onUpdated()
    } catch (error) { showMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const canWithdraw = !!status.lastAttempt && !['Published', 'Unchanged', 'Withdrawn'].includes(status.lastAttempt.state)
  const acceptedStages = status.stages.filter(stage => ['Passed'].includes(stage.state)).length
  const label = busy ? action === 'resume' ? 'Resuming' : action === 'withdraw' ? 'Withdrawing' : 'Saving' :
    status.resumePending ? 'Resume needed' : pending ? 'Saving' : status.available ? 'Ready' : 'Needs setup'
  const icon: IconName = busy ? 'loader' : status.resumePending ? 'warning' : pending ? 'clock' : status.available ? 'archive' : 'shield'
  const reason = busy ? action === 'resume' ? 'Waiting for the game server to confirm it can save again.' :
    action === 'withdraw' ? 'Canceling this attempt. Completed signed copies are kept.' : 'Waiting for the game to save and for its copy to be checked.' :
    status.resumePending ? 'Resume game saving before making another copy.' :
    pending ? 'Waiting for the game to finish saving. Unfinished copies are not shared.' :
    status.available ? 'Save and share a verified copy while the server runs.' :
    status.code === 'GameAcceptanceRequired' ? 'Test this server installation before sharing while it runs.' :
    status.code === 'LiveSaveReviewRequired' ? 'Review the previous attempt before requesting another save.' :
    'Check the server status and save requirements before making a copy.'
  return <section className="live-save-presentation live-save-status-card" aria-label="Share while running" aria-busy={busy}>
    <div className="live-save-card-heading">
      <span className="live-save-card-icon"><Icon name={icon} size={21} /></span>
      <div className="live-save-card-title"><h4>Share while running</h4>
        <span className="live-save-state" data-tone={status.resumePending ? 'warning' : status.available && !busy && !pending ? 'ready' : 'neutral'}>{label}</span>
      </div>
      {status.game === 'Fixture' && <span className="live-save-environment">Development test server</span>}
    </div>
    <p className="live-save-reason">{reason}</p>
    <div className="live-save-card-actions">
      {status.resumePending ? <Button className="primary" disabled={busy} onClick={() => void act('resume')}>
        <Icon name={busy ? 'loader' : 'refresh'} />{busy && action === 'resume' ? 'Resuming…' : 'Resume game saving'}
      </Button> : status.available ? <Button className="primary" disabled={busy || !status.available || pending} onClick={() => void act('save')}>
        <Icon name={busy ? 'loader' : 'archive'} />{busy ? 'Working…' : 'Save and share now'}
      </Button> : !pending && <Button className="secondary" aria-controls={detailsId} aria-expanded={detailsOpen}
        onClick={() => setDetailsOpen(true)}><Icon name="shield" />Review requirements</Button>}
      {canWithdraw && <Button className="text-button" disabled={busy} onClick={() => void act('withdraw')}>
        <Icon name="close" />Cancel this attempt</Button>}
    </div>
    {message && <DismissibleNotice as="p" scope={profileId} noticeKey={messageResult} role="status">{message}</DismissibleNotice>}
    <details id={detailsId} className="live-save-disclosure" open={detailsOpen}
      onToggle={event => setDetailsOpen(event.currentTarget.open)}>
      <summary><Icon name="logs" />Game tests and save details<Icon name="chevronDown" /></summary>
      <div className="live-save-disclosure-body">
        <DismissibleNotice as="p" scope={profileId} className="live-save-detail-message">{status.message}</DismissibleNotice>
        {status.code && <p className="live-save-technical">Status: <code>{status.code}</code></p>}
        {status.lastAttempt && <div className="live-save-attempt">
          <strong>Last attempt: {attemptLabels[status.lastAttempt.state]}</strong><p>{status.lastAttempt.message}</p>
          {status.lastAttempt.versionNumber !== null && <span>Save {status.lastAttempt.versionNumber}</span>}
        </div>}
        {status.stages.length > 0 && <>
          <p>{acceptedStages} of {status.stages.length} game checks passed. The app checks saving, copying and transfer; you confirm results in the game.</p>
          <ul className="live-save-evidence-list">{status.stages.map(stage => <li key={stage.id}>
            <div><strong>{stageLabels[stage.id as keyof typeof stageLabels]}</strong><span>{stage.state === 'Passed' ? 'Passed' : 'Not checked'}</span></div>
            <p>{stage.detail}</p>
          </li>)}</ul>
        </>}
      </div>
    </details>
  </section>
}

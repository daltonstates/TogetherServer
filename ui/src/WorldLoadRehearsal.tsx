import { useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { ContractError, type Decoder, type WorldBackupRecord } from './contracts'
import { useConfirmation } from './Confirmation'
import './WorldLoadRehearsal.css'

type Outcome = 'Unobserved' | 'OwnerConfirmed' | 'OwnerFailed'
export type WorldLoadView = {
  id: string; game: string; sourceKind: 'Backup' | 'Received'; copyIdentity: string
  appVersion: string; gameVersion: string; state: string; canLaunch: boolean; launchReason: string
  ownerReportedGameVersion: string | null
  gamePort: number; worldDirectory: string; rehearsalProfileId: string | null
  loadOutcome: Outcome; changeOutcome: Outcome; restartOutcome: Outcome
  managedStarts: number; gracefulStops: number; cleaned: boolean; preparedUtc: string
}
type Result = { ok: boolean; code: string; message: string; rehearsal: WorldLoadView | null }
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError('Invalid load rehearsal response')
  return value as Record<string, unknown>
}
function text(value: unknown, max = 500): string {
  if (typeof value !== 'string' || value.length > max) throw new ContractError('Invalid load rehearsal text')
  return value
}
function count(value: unknown, maximum: number): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0 || value > maximum)
    throw new ContractError('Invalid load rehearsal count')
  return value
}
export const parseWorldLoadView: Decoder<WorldLoadView> = value => {
  const source = object(value)
  const outcome = (value: unknown): Outcome => {
    if (value !== 'Unobserved' && value !== 'OwnerConfirmed' && value !== 'OwnerFailed')
      throw new ContractError('Invalid owner confirmation')
    return value
  }
  if (source.sourceKind !== 'Backup' && source.sourceKind !== 'Received' ||
    typeof source.canLaunch !== 'boolean' || typeof source.cleaned !== 'boolean' ||
    !/^[a-f0-9]{64}$/i.test(text(source.copyIdentity, 64)) ||
    !Number.isFinite(Date.parse(text(source.preparedUtc, 50)))) throw new ContractError('Invalid load rehearsal')
  return { id: text(source.id, 36), game: text(source.game, 30), sourceKind: source.sourceKind,
    copyIdentity: text(source.copyIdentity, 64), appVersion: text(source.appVersion, 60), gameVersion: text(source.gameVersion, 100),
    state: text(source.state, 100), canLaunch: source.canLaunch, launchReason: text(source.launchReason),
    ownerReportedGameVersion: source.ownerReportedGameVersion == null ? null : text(source.ownerReportedGameVersion, 40),
    gamePort: count(source.gamePort, 65535), worldDirectory: text(source.worldDirectory, 1024),
    rehearsalProfileId: source.rehearsalProfileId == null ? null : text(source.rehearsalProfileId, 36),
    loadOutcome: outcome(source.loadOutcome), changeOutcome: outcome(source.changeOutcome), restartOutcome: outcome(source.restartOutcome),
    managedStarts: count(source.managedStarts, 1_000_000), gracefulStops: count(source.gracefulStops, 1_000_000),
    cleaned: source.cleaned, preparedUtc: text(source.preparedUtc, 50) }
}
export const parseWorldLoadResult: Decoder<Result> = value => {
  const source = object(value)
  if (typeof source.ok !== 'boolean') throw new ContractError('Invalid load rehearsal result')
  return { ok: source.ok, code: text(source.code, 100), message: text(source.message),
    rehearsal: source.rehearsal == null ? null : parseWorldLoadView(source.rehearsal) }
}
const parseList: Decoder<{ ok: boolean; message: string; rehearsals: WorldLoadView[] }> = value => {
  const source = object(value)
  if (typeof source.ok !== 'boolean' || !Array.isArray(source.rehearsals) || source.rehearsals.length > 20)
    throw new ContractError('Invalid load rehearsal list')
  return { ok: source.ok, message: text(source.message), rehearsals: source.rehearsals.map(item => parseWorldLoadView(item)) }
}
const outcomeLabel: Record<Outcome, string> = { Unobserved: 'Not checked', OwnerConfirmed: 'Confirmed by you', OwnerFailed: 'Failure reported by you' }

export function WorldLoadRehearsalPanel({ profileId, backups = [], received = false }:
  { profileId: string; backups?: WorldBackupRecord[]; received?: boolean }) {
  const [records, setRecords] = useState<WorldLoadView[]>([])
  const [backupId, setBackupId] = useState('')
  const [selectedId, setSelectedId] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [messageResult, setMessageResult] = useState(0)
  const [gameVersion, setGameVersion] = useState('')
  const selected = records.find(item => item.id === selectedId) ?? records[0]
  const { confirm: requestConfirmation, confirmationDialog } = useConfirmation()
  const confirmationRevision = useRef(0)
  const confirmationPending = useRef(false)
  useEffect(() => {
    confirmationRevision.current += 1
  }, [profileId, received, selected?.id, selected?.copyIdentity, selected?.state, selected?.cleaned,
    selected?.rehearsalProfileId, selected?.managedStarts, selected?.gracefulStops,
    selected?.loadOutcome, selected?.changeOutcome, selected?.restartOutcome, busy])
  const chosenBackup = backups.some(item => item.id === backupId) ? backupId : backups[0]?.id
  const showMessage = (text: string) => { setMessage(text); setMessageResult(result => result + 1) }
  const adopt = (result: Result) => {
    showMessage(result.message)
    if (result.rehearsal) {
      const item = result.rehearsal
      setRecords(previous => [item, ...previous.filter(record => record.id !== item.id)])
      setSelectedId(item.id)
    }
  }
  const refresh = async () => {
    setBusy(true)
    try {
      const list = await getLocalJson(`/api/local/world-load/source/${profileId}`, parseList)
      if (list.ok) setRecords(list.rehearsals)
      else showMessage(list.message)
    }
    catch (error) { showMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const prepare = async () => {
    setBusy(true)
    try {
      adopt(await changeJson(received ? `/api/local/friend/${profileId}/world-load/prepare` :
        `/api/local/profiles/${profileId}/world-load/prepare`, 'POST', parseWorldLoadResult,
        received ? undefined : { backupId: chosenBackup }))
    } catch (error) { showMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const action = async (name: string, body?: unknown) => {
    if (!selected) return
    if (name === 'cleanup') {
      if (confirmationPending.current) return
      const revision = confirmationRevision.current
      confirmationPending.current = true
      try {
        if (!(await requestConfirmation({ title: 'Delete test copy?',
          description: 'Confirm the test game is stopped. Delete this test copy? The original save and test results will be kept.',
          confirmLabel: 'Delete test copy', destructive: true })) || revision !== confirmationRevision.current) return
      } finally { confirmationPending.current = false }
    }
    setBusy(true)
    try { adopt(await changeJson(`/api/local/world-load/${selected.id}/${name}`, 'POST', parseWorldLoadResult, body)) }
    catch (error) { showMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const confirmOutcome = (step: string, passed: boolean) => void action('confirm', { step, passed, gameVersion: gameVersion.trim() || null })
  return <><details className="connection-doctor world-load-rehearsal" onToggle={event => { if (event.currentTarget.open) void refresh() }}>
    <summary>Test a copy in the game</summary>
    <p>Make a separate test copy, load it in the game and make a visible change. Save, stop and restart to check the change is still there. The original stays unchanged. Game results are recorded as your reports.</p>
    {!received && <label>Completed backup <Select aria-label="Backup to test" value={chosenBackup ?? ''}
      disabled={busy || backups.length === 0} onChange={event => setBackupId(event.target.value)}>
      {backups.length === 0 && <option value="">Show completed backups first</option>}
      {backups.map(item => <option key={item.id} value={item.id}>{item.backupKind} · {new Date(item.createdUtc).toLocaleString()}</option>)}
    </Select></label>}
    <div className="actions"><Button className="secondary" disabled={busy || !received && !chosenBackup} onClick={() => void prepare()}>Prepare test copy</Button>
      <Button className="text-button" disabled={busy} onClick={() => void refresh()}>Refresh results</Button></div>
    {records.length > 1 && <label>Test <Select value={selected?.id ?? ''} onChange={event => setSelectedId(event.target.value)}>
      {records.map(item => <option key={item.id} value={item.id}>{new Date(item.preparedUtc).toLocaleString()} · {item.state}</option>)}
    </Select></label>}
    {selected && <section aria-label="World load steps">
      <p>{selected.game} · {selected.state}</p><p>{selected.launchReason}</p>
      {!selected.cleaned && <>
        {selected.canLaunch ? <p>Start this test copy. Connect your game to 127.0.0.1:{selected.gamePort}. An open port or Ready status does not confirm that the world loaded.</p> :
          selected.rehearsalProfileId ? <p>Review this test setup before starting again. If the game installation changed, prepare a new test copy.</p> :
            <p>To test manually, use an installed game with this test copy as its save folder. Use a separate port and connect from this PC. Keep the live world and original save files separate. Stop the test game before deleting the copy.</p>}
        {(selected.canLaunch || selected.rehearsalProfileId != null) && <div className="actions">
          {selected.canLaunch && <Button disabled={busy} onClick={() => void action('start')}>Start test copy</Button>}
          {selected.rehearsalProfileId != null && <Button className="secondary" disabled={busy} onClick={() => void action('stop')}>Stop test copy</Button>}
        </div>}
        <label>Game version you tested (optional)<Input value={gameVersion} maxLength={40} onChange={event => setGameVersion(event.target.value)} /></label>
        <ol><li>Load: {outcomeLabel[selected.loadOutcome]} <Button className="secondary" disabled={busy} onClick={() => confirmOutcome('Load', true)}>I loaded this copy</Button>
          <Button className="text-button" disabled={busy} onClick={() => confirmOutcome('Load', false)}>Load failed</Button></li>
          <li>Visible saved change: {outcomeLabel[selected.changeOutcome]} <Button className="secondary" disabled={busy || selected.loadOutcome !== 'OwnerConfirmed'} onClick={() => confirmOutcome('Change', true)}>I saved a visible change</Button></li>
          <li>Restart: {outcomeLabel[selected.restartOutcome]} <Button className="secondary" disabled={busy || selected.changeOutcome !== 'OwnerConfirmed'} onClick={() => confirmOutcome('Restart', true)}>The change survived restart</Button>
            <Button className="text-button" disabled={busy || selected.changeOutcome !== 'OwnerConfirmed'} onClick={() => confirmOutcome('Restart', false)}>Restart failed</Button></li></ol>
        <Button className="text-button" disabled={busy} onClick={() => void action('cleanup', { confirmStopped: true })}>Delete this test copy</Button>
      </>}
      <details><summary>Copy and setup details</summary><p>Copy: <code>{selected.copyIdentity}</code></p><p>App version when prepared: {selected.appVersion} · Installed game: {selected.gameVersion}</p>
        {selected.ownerReportedGameVersion && <p>Owner-reported game version: {selected.ownerReportedGameVersion}</p>}
        <p className="helper-text">Working folder: {selected.worldDirectory}</p><p>Recorded starts: {selected.managedStarts} · safe stops: {selected.gracefulStops}</p></details>
    </section>}
    {message && <DismissibleNotice as="p" scope={`${received ? 'received' : 'host'}:${profileId}`} noticeKey={messageResult} role="status">{message}</DismissibleNotice>}
  </details>{confirmationDialog}</>
}

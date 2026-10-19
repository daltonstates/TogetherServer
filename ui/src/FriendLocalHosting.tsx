import { useCallback, useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBasicResult } from './contracts'
import { Icon } from './Icon'
import { SettingsDialog } from './SettingsPresentation'
import { SharedWorldSeparateRoutePanel } from './SharedWorldSeparateRoutePanel'
import './FriendLocalHosting.css'

const stepKeys = ['permission', 'server', 'save', 'ports', 'connection'] as const
type CheckKey = typeof stepKeys[number]
export type LocalHostingStatus = {
  ok: boolean; code: string; message: string; allowed: boolean; consented: boolean
  game: string | null; name: string; needsServerFile: boolean; needsPassword: boolean
  serverFile: string | null; gameVersion: string | null; gamePort: number; controlPort: number
  localProfileId: string | null; branchHash: string | null; readyForManualStart: boolean; running: boolean
  steps: { key: CheckKey; state: 'Ready' | 'ActionRequired' | 'Unknown'; message: string }[]
}

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const hashPattern = /^[0-9a-f]{64}$/i
function boundedText(value: unknown, max: number, nullable = false): boolean {
  return nullable && value === null || typeof value === 'string' && value.length <= max
    && ![...value].some(character => character.charCodeAt(0) < 32 && !['\t', '\n', '\r'].includes(character))
}
export function parseLocalHostingStatus(value: unknown): LocalHostingStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Local hosting checks could not be read.')
  const item = value as Record<string, unknown>
  if (['ok', 'allowed', 'consented', 'needsServerFile', 'needsPassword', 'readyForManualStart', 'running']
    .some(key => typeof item[key] !== 'boolean') || !boundedText(item.code, 100) || !boundedText(item.message, 1000)
    || !boundedText(item.game, 40, true) || !boundedText(item.name, 128)
    || !boundedText(item.serverFile, 8192, true) || !boundedText(item.gameVersion, 128, true)
    || [item.gamePort, item.controlPort].some(port => typeof port !== 'number' || !Number.isInteger(port) || port < 1 || port > 65535)
    || item.localProfileId !== null && (typeof item.localProfileId !== 'string' || !guidPattern.test(item.localProfileId))
    || item.branchHash !== null && (typeof item.branchHash !== 'string' || !hashPattern.test(item.branchHash))
    || !Array.isArray(item.steps) || item.steps.length !== stepKeys.length
    || item.steps.some((step: unknown, index: number) => {
      if (!step || typeof step !== 'object' || Array.isArray(step)) return true
      const check = step as Record<string, unknown>
      return check.key !== stepKeys[index] || !['Ready', 'ActionRequired', 'Unknown'].includes(String(check.state))
        || !boundedText(check.message, 1000)
    }) || item.readyForManualStart === true && (item.ok !== true || item.allowed !== true || item.consented !== true
      || item.running !== false || item.needsServerFile !== false || item.needsPassword !== false
      || !item.localProfileId || !item.branchHash
      || item.steps.slice(0, 4).some((step: Record<string, unknown>) => step.state !== 'Ready')))
    throw new Error('Local hosting checks are incomplete or invalid. Retry before starting.')
  return value as LocalHostingStatus
}

const checkLabels: Record<CheckKey, string> = {
  permission: 'Permission to host', server: 'Server app and tools', save: 'Save files and hashes',
  ports: 'Local game ports', connection: 'Incoming connection'
}

export type FriendLocalHostingProps = {
  profileId: string; connectionId?: string; worldName: string; available: boolean
  accessBlocked?: boolean; disabled?: boolean; presentation?: 'action' | 'card'
  onBrowseServerFile?: (game: string) => Promise<string | null>
  onOpenHost: (localProfileId: string) => void
  onOpenConnectionHelp?: (localProfileId: string) => void
}

export function FriendLocalHosting({ profileId, connectionId, worldName, available, accessBlocked = false,
  disabled = false, presentation = 'action', onBrowseServerFile, onOpenHost, onOpenConnectionHelp }: FriendLocalHostingProps) {
  const base = `/api/local/friend/${profileId}/shared-world`
  const scope = JSON.stringify([connectionId, profileId])
  const request = useRef(0)
  const mutation = useRef(false)
  const [status, setStatus] = useState<LocalHostingStatus | null>(null)
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [serverFile, setServerFile] = useState('')
  const [password, setPassword] = useState('')
  const [accepted, setAccepted] = useState(false)
  const [actionMessage, setActionMessage] = useState('')
  const [manualSetupBranch, setManualSetupBranch] = useState<string | null>(null)
  const [manualBusy, setManualBusy] = useState(false)
  const manualOperation = useRef(false)
  const setManualActivity = useCallback((value: boolean) => { manualOperation.current = value; setManualBusy(value) }, [])
  const acceptStatus = useCallback((next: LocalHostingStatus) => {
    setStatus(next)
    if (next.code === 'LocalServerSetupRequired') setManualSetupBranch(next.branchHash)
  }, [])
  const controller = useRef<AbortController | null>(null)
  const invalidate = useCallback(() => { ++request.current; controller.current?.abort() }, [])

  useEffect(() => {
    mutation.current = false
    setStatus(null); setOpen(false); setBusy(false); setError(''); setAccepted(false)
    setServerFile(''); setPassword(''); setActionMessage('')
    setManualSetupBranch(null)
    setManualActivity(false)
    if (!available || accessBlocked) return
    let active = true
    let timer: number | undefined
    let currentRead: AbortController | null = null
    let timeout: number | undefined
    const read = async () => {
      if (!mutation.current && !manualOperation.current) {
        const sequence = ++request.current
        const abort = new AbortController()
        currentRead = abort
        timeout = window.setTimeout(() => {
          if (active && sequence === request.current) { setStatus(null); ++request.current }
          abort.abort()
        }, 15000)
        try {
          const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
          if (active && sequence === request.current && !abort.signal.aborted) acceptStatus(next)
        } catch {
          // An old grant cannot authorize hosting after an unavailable check.
          if (active && sequence === request.current) setStatus(null)
        } finally { window.clearTimeout(timeout) }
      }
      if (active) timer = window.setTimeout(() => void read(), 10000)
    }
    void read()
    return () => { active = false; currentRead?.abort(); window.clearTimeout(timer); window.clearTimeout(timeout); invalidate() }
  }, [scope, base, available, accessBlocked, acceptStatus, invalidate, setManualActivity])

  const refresh = async () => {
    if (mutation.current || disabled) return
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    setBusy(true); setError('')
    const timeout = window.setTimeout(() => {
      abort.abort()
      if (sequence === request.current) { setStatus(null); setError('Hosting checks timed out. Retry when the local service responds.'); setBusy(false); mutation.current = false; ++request.current }
    }, 15000)
    try {
      const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
      if (sequence === request.current && !abort.signal.aborted) acceptStatus(next)
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) setError(errorMessage(cause))
    } finally {
      window.clearTimeout(timeout)
      if (sequence === request.current) { mutation.current = false; setBusy(false) }
    }
  }
  const prepare = async () => {
    if (mutation.current || manualBusy || disabled || !status?.allowed || !accepted) return
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    setBusy(true); setError(''); setActionMessage('')
    try {
      const next = await changeJson(`${base}/hosting/prepare`, 'POST', parseLocalHostingStatus, {
        allowReceiving: true, acceptSplitWarning: true,
        serverFile: serverFile.trim() || null, gamePassword: password || null
      }, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      acceptStatus(next); setPassword(''); setActionMessage(next.message)
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) setError(errorMessage(cause))
    } finally {
      if (sequence === request.current) { mutation.current = false; setBusy(false) }
    }
  }
  const start = async () => {
    if (mutation.current || manualBusy || disabled || !status?.allowed || !status.readyForManualStart || status.running || !accepted
      || !status.localProfileId || !status.branchHash) return
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    setBusy(true); setError(''); setActionMessage('')
    const localProfileId = status.localProfileId
    try {
      const result = await changeJson(`${base}/recovery/separate/start`, 'POST', parseBasicResult, {
        branchHash: status.branchHash, localProfileId, acceptSplitWarning: true
      }, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      setActionMessage(result.message)
      if (result.ok) { setOpen(false); onOpenHost(localProfileId) }
      else {
        const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
        if (sequence === request.current && !abort.signal.aborted) acceptStatus(next)
      }
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) setError(errorMessage(cause))
    } finally {
      if (sequence === request.current) { mutation.current = false; setBusy(false) }
    }
  }
  const browse = async () => {
    if (!status?.game || !onBrowseServerFile || mutation.current || manualBusy || disabled) return
    mutation.current = true
    const sequence = ++request.current
    setError(''); setBusy(true)
    try {
      const selected = await onBrowseServerFile(status.game)
      if (sequence === request.current && selected) setServerFile(selected)
    } catch (cause) { if (sequence === request.current) setError(errorMessage(cause)) }
    finally { if (sequence === request.current) { mutation.current = false; setBusy(false) } }
  }

  if (!available || accessBlocked) return null
  const allowed = status?.allowed === true
  const manualSetup = !!status?.branchHash && !status.readyForManualStart && !status.running
    && (status.code === 'LocalServerSetupRequired' || manualSetupBranch === status.branchHash)
  const historyChoice = status?.code === 'HostingHistoryChoiceRequired'
  if (!allowed && !open && presentation === 'action') return null
  return <>
    {presentation === 'card' ? <section className="local-hosting-card" aria-label="Host on this PC">
      <Icon name="server" size={24} /><div><h3>Host on this PC</h3>
        <p>{allowed ? 'Use this save here. TogetherServer checks the server app, verified files and ports for you.'
          : status ? 'Ask the owner to turn on Can host this save for this PC.' : 'Checking this PC’s hosting permission…'}</p></div>
      <Button className="secondary" disabled={disabled || !allowed} onClick={() => { setOpen(true); setError(''); setActionMessage('') }}>
        <Icon name="monitor" />Host on this PC</Button>
    </section> : <Button className="secondary local-hosting-trigger" title="Host on this PC" disabled={disabled || !allowed}
      onClick={() => { setOpen(true); setError(''); setActionMessage('') }}><Icon name="server" />Host on this PC</Button>}
    {open && <SettingsDialog title="Host on this PC" description={worldName} busy={busy || manualBusy} onClose={() => setOpen(false)} wide={manualSetup}
      footer={status?.running && status.localProfileId ? <Button onClick={() => onOpenHost(status.localProfileId!)}>Open local server</Button>
        : status?.readyForManualStart ? <Button disabled={busy || manualBusy || disabled || !allowed || !accepted} onClick={() => void start()}>
          <Icon name={busy ? 'loader' : 'play'} />{busy ? 'Starting…' : 'Start server on this PC'}</Button>
          : <Button disabled={busy || manualBusy || disabled || manualSetup || historyChoice || !allowed || !accepted || status?.needsServerFile && !serverFile.trim()
            || status?.needsPassword && !password} onClick={() => void prepare()}>
            <Icon name={busy ? 'loader' : 'server'} />{busy ? 'Preparing this PC…' : historyChoice ? 'Choose a save above' : manualSetup ? 'Finish setup above' : 'Prepare this PC'}</Button>}>
      <div className="local-hosting-flow" aria-busy={busy}>
        <p className="local-hosting-intro">{status?.running ? 'This save is already hosted on this PC.'
          : status?.readyForManualStart ? 'Your local save is prepared. Start when you’re ready.'
            : 'We’ll find the installed server, get or reuse the save, verify its files and check this PC’s ports.'}</p>
        {status ? <ol className="local-hosting-checks" aria-label="Local hosting checks">
          {status.steps.map(step => <li key={step.key} data-state={step.state}>
            <Icon name={busy && step.key !== 'permission' ? 'loader' : step.state === 'Ready' ? 'check'
              : step.state === 'ActionRequired' ? 'warning' : 'plug'} />
            <div><strong>{checkLabels[step.key]}</strong><p>{step.message}</p></div>
            <span>{busy && step.key !== 'permission' ? 'Checking' : step.state === 'Ready' ? 'Checked'
              : step.state === 'ActionRequired' ? 'Needs attention' : 'Unverified'}</span>
          </li>)}
        </ol> : <p role="alert">Hosting checks are unavailable. Retry before preparing or starting.</p>}
        <Button className="text-button" disabled={busy || manualBusy || disabled} onClick={() => void refresh()}><Icon name="refresh" />Retry automatic checks</Button>
        {status?.needsServerFile && !status.readyForManualStart && <div className="local-hosting-fix">
          <label>Installed server file<div className="field-with-button"><Input aria-label="Installed server file" value={serverFile} disabled={busy}
            placeholder={status.game === 'MinecraftJava' ? 'Path to server.jar' : 'Path to installed server'}
            onChange={event => setServerFile(event.target.value)} />
            {onBrowseServerFile && <Button className="secondary" disabled={busy || !status.game} onClick={() => void browse()}><Icon name="folder" />Browse</Button>}</div></label>
          {status.game === 'Valheim' && <a href="steam://install/896660">Install Valheim Dedicated Server in Steam</a>}
          <p className="helper-text">Install missing software and accept its terms on this PC, then retry. Existing installations are reused.</p>
        </div>}
        {status?.needsPassword && !status.readyForManualStart && <label>New local game password<Input type="password"
          autoComplete="new-password" disabled={busy} value={password} onChange={event => setPassword(event.target.value)} /></label>}
        {manualSetup && status?.branchHash && <SharedWorldSeparateRoutePanel
          key={status.branchHash} profileId={profileId} separateCopies={1} game={status.game ?? undefined}
          selectedBranchHash={status.branchHash} suggestedSetup={status} expanded allowStart={false}
          onBrowseServerFile={onBrowseServerFile} onPrepared={() => void refresh()}
          onBusyChange={setManualActivity}
          onOpenSeparateHostControls={onOpenHost} />}
        {historyChoice && <SharedWorldSeparateRoutePanel profileId={profileId} separateCopies={1}
          game={status?.game ?? undefined} expanded onBrowseServerFile={onBrowseServerFile}
          onBusyChange={setManualActivity} onOpenSeparateHostControls={onOpenHost} />}
        {allowed && !status?.running && <label className="local-hosting-consent"><Input type="checkbox" checked={accepted} disabled={busy}
          onChange={event => setAccepted(event.target.checked)} /><span>Keep this save on my PC and host my own copy.
          <small>If another Host is running, progress stays separate. Existing saves are preserved.</small></span></label>}
        {status?.localProfileId && <Button className="text-button" disabled={busy}
          onClick={() => onOpenHost(status.localProfileId!)}>Local server &amp; connection details</Button>}
        {status?.localProfileId && onOpenConnectionHelp && status.steps[4].state !== 'Ready' && <Button className="text-button"
          disabled={busy || manualBusy || disabled} onClick={() => { setOpen(false); onOpenConnectionHelp(status.localProfileId!) }}>
          <Icon name="plug" />Connection help for this PC</Button>}
        {busy && <p role="status">{status?.readyForManualStart ? 'Starting the local server…' : 'Preparing and verifying your local copy. Keep TogetherServer open.'}</p>}
        {actionMessage && !busy && <p role="status">{actionMessage}</p>}
        {error && <p role="alert">{error}</p>}
      </div>
    </SettingsDialog>}
  </>
}

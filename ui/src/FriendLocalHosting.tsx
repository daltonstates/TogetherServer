import { useCallback, useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBasicResult } from './contracts'
import { Icon } from './Icon'
import { SettingsDialog } from './SettingsPresentation'
import { SharedWorldSeparateRoutePanel } from './SharedWorldSeparateRoutePanel'
import { parsePlannedHandoffStatus, type PlannedHandoffStatus } from './plannedHandoff'
import './FriendLocalHosting.css'

const stepKeys = ['permission', 'server', 'save', 'ports', 'connection'] as const
type CheckKey = typeof stepKeys[number]
export type LocalHostingStatus = {
  ok: boolean; code: string; message: string; allowed: boolean; consented: boolean
  game: string | null; name: string; needsServerFile: boolean; needsPassword: boolean
  serverFile: string | null; gameVersion: string | null; gamePort: number; controlPort: number
  localProfileId: string | null; branchHash: string | null; readyForManualStart: boolean; running: boolean
  steps: { key: CheckKey; state: 'Ready' | 'ActionRequired' | 'Unknown'; message: string }[]
  handoffRequest?: PlannedHandoffStatus | null
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
  const handoffRequest = item.handoffRequest == null ? null : parsePlannedHandoffStatus(item.handoffRequest)
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
      || !item.localProfileId || !item.branchHash && handoffRequest?.recipientState !== 'Completed'
      || item.steps.slice(0, 4).some((step: Record<string, unknown>) => step.state !== 'Ready')))
    throw new Error('Local hosting checks are incomplete or invalid. Retry before starting.')
  return value as LocalHostingStatus
}

const checkLabels: Record<CheckKey, string> = {
  permission: 'Permission to host', server: 'Server app and tools', save: 'Save file checks',
  ports: 'Local game ports', connection: 'Connections from other PCs'
}

function activeHostingRequest(status: LocalHostingStatus | null) {
  const invitation = status?.handoffRequest
  return invitation?.requestId && !['Declined', 'Canceled'].includes(invitation.recipientState ?? '') ? invitation : null
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
  const [acceptedTerms, setAcceptedTerms] = useState(false)
  const [actionMessage, setActionMessage] = useState('')
  const [manualSetupBranch, setManualSetupBranch] = useState<string | null>(null)
  const [manualBusy, setManualBusy] = useState(false)
  const [plannedOperation, setPlannedOperation] = useState<{ requestId: string; until: number } | null>(null)
  const plannedAuthorization = useRef(plannedOperation)
  plannedAuthorization.current = plannedOperation
  const plannedAttempt = useRef('')
  const currentStatus = useRef(status)
  currentStatus.current = status
  const manualOperation = useRef(false)
  const setManualActivity = useCallback((value: boolean) => { manualOperation.current = value; setManualBusy(value) }, [])
  const acceptStatus = useCallback((next: LocalHostingStatus) => {
    if (currentStatus.current?.handoffRequest?.requestId !== next.handoffRequest?.requestId) {
      setAcceptedTerms(false); setAccepted(false); setPassword('')
      plannedAuthorization.current = null; setPlannedOperation(null); plannedAttempt.current = ''
    }
    currentStatus.current = next
    setStatus(next)
    if (next.code === 'LocalServerSetupRequired') setManualSetupBranch(next.branchHash)
  }, [])
  const controller = useRef<AbortController | null>(null)
  const invalidate = useCallback(() => { ++request.current; controller.current?.abort() }, [])

  useEffect(() => {
    mutation.current = false
    setStatus(null); setOpen(false); setBusy(false); setError(''); setAccepted(false); setAcceptedTerms(false)
    setServerFile(''); setPassword(''); setActionMessage('')
    setPlannedOperation(null); plannedAttempt.current = ''
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
          if (active && sequence === request.current) {
            setStatus(null); plannedAuthorization.current = null; setPlannedOperation(null); ++request.current
          }
          abort.abort()
        }, 15000)
        try {
          const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
          if (active && sequence === request.current && !abort.signal.aborted) acceptStatus(next)
        } catch {
          // An old grant cannot authorize hosting after an unavailable check.
          if (active && sequence === request.current) {
            setStatus(null); plannedAuthorization.current = null; setPlannedOperation(null)
          }
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
      if (sequence === request.current) { setStatus(null); setError('Hosting checks timed out. Retry the checks.'); setBusy(false); mutation.current = false; ++request.current }
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
  const prepareStatus = useCallback(async (target: LocalHostingStatus, authorization: typeof plannedOperation) => {
    if (mutation.current || manualBusy || disabled || !target.allowed) return
    const planned = activeHostingRequest(target)
    if (planned?.recipientState === 'Requested') return
    if (planned?.requestId && ['MinecraftJava', 'MinecraftBedrock'].includes(target.game ?? '') && !acceptedTerms) return
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    setBusy(true); setError(''); setActionMessage('')
    const timeout = authorization ? window.setTimeout(() => {
      if (sequence !== request.current || plannedAuthorization.current !== authorization) return
      abort.abort(); ++request.current; mutation.current = false; setBusy(false)
      plannedAuthorization.current = null; setPlannedOperation(null); setStatus(null)
      setError('Preparation did not finish checking in time. Recheck the hosting request before continuing. Existing saves were kept.')
    }, Math.min(120000, Math.max(0, authorization.until - Date.now()))) : undefined
    try {
      const next = await changeJson(`${base}/hosting/prepare`, 'POST', parseLocalHostingStatus, {
        allowReceiving: true, acceptSplitWarning: !planned?.requestId,
        serverFile: serverFile.trim() || null, gamePassword: password || null,
        ...(planned?.requestId ? { requestId: planned.requestId, acceptedTerms } : {})
      }, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      if (authorization && plannedAuthorization.current !== authorization) return
      if (planned?.requestId && currentStatus.current?.handoffRequest?.requestId !== planned.requestId) return
      acceptStatus(next); setPassword(''); setActionMessage(next.message)
      if (authorization && (!next.ok || next.readyForManualStart)) {
        plannedAuthorization.current = null; setPlannedOperation(null)
      }
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) {
        setError(errorMessage(cause)); plannedAuthorization.current = null; setPlannedOperation(null)
      }
    } finally {
      window.clearTimeout(timeout)
      if (sequence === request.current) { mutation.current = false; setBusy(false) }
    }
  }, [base, disabled, manualBusy, serverFile, password, acceptedTerms, acceptStatus])
  const prepare = () => {
    const invitation = activeHostingRequest(status)
    if (!status?.allowed || !invitation && !accepted) return
    const authorization = invitation ? {
      requestId: invitation.requestId!, until: Date.now() + 10 * 60 * 1000
    } : null
    plannedAttempt.current = ''
    plannedAuthorization.current = authorization; setPlannedOperation(authorization)
    if (authorization) plannedAttempt.current = JSON.stringify([status.handoffRequest?.requestId,
      status.handoffRequest?.recipientState, status.handoffRequest?.finalVersion, status.handoffRequest?.preparedVersionHash])
    void prepareStatus(status, authorization)
  }
  const respond = async (accept: boolean) => {
    const invitation = status?.handoffRequest
    if (mutation.current || disabled || !invitation?.requestId || invitation.recipientState !== 'Requested') return
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    setBusy(true); setError(''); setActionMessage('')
    try {
      const result = await changeJson(`${base}/handoff/respond`, 'POST', parseBasicResult,
        { requestId: invitation.requestId, accept }, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      setActionMessage(result.message)
      if (!result.ok) return
      const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      acceptStatus(next)
      if (accept && next.handoffRequest?.requestId === invitation.requestId) {
        const authorization = { requestId: invitation.requestId, until: Date.now() + 10 * 60 * 1000 }
        plannedAuthorization.current = authorization; setPlannedOperation(authorization)
      }
    } catch (cause) { if (sequence === request.current && !abort.signal.aborted) setError(errorMessage(cause)) }
    finally { if (sequence === request.current) { mutation.current = false; setBusy(false) } }
  }
  useEffect(() => {
    if (!open || !plannedOperation || !status?.handoffRequest || mutation.current || manualBusy || disabled) return
    const invitation = status.handoffRequest
    if (invitation.requestId !== plannedOperation.requestId || ['Requested', 'Declined', 'Canceled'].includes(invitation.recipientState ?? '')) {
      plannedAuthorization.current = null; setPlannedOperation(null)
      return
    }
    if (status.readyForManualStart || status.needsServerFile && !serverFile.trim() || status.needsPassword && !password) return
    if (['MinecraftJava', 'MinecraftBedrock'].includes(status.game ?? '') && !acceptedTerms) return
    if (invitation.recipientState === 'Prepared') return
    const attempt = JSON.stringify([invitation.requestId, invitation.recipientState, invitation.finalVersion, invitation.preparedVersionHash])
    if (plannedAttempt.current === attempt) return
    plannedAttempt.current = attempt
    void prepareStatus(status, plannedOperation)
  }, [status, open, plannedOperation, disabled, manualBusy, serverFile, password, acceptedTerms, prepareStatus])
  useEffect(() => {
    if (!plannedOperation) return
    const timeout = window.setTimeout(() => {
      plannedAuthorization.current = null; setPlannedOperation(null)
      if (mutation.current) { controller.current?.abort(); ++request.current; mutation.current = false; setBusy(false); setStatus(null) }
      setActionMessage('Preparation is paused. Choose Prepare this PC to continue the move.')
    }, Math.max(0, plannedOperation.until - Date.now()))
    return () => window.clearTimeout(timeout)
  }, [plannedOperation])
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
  const handoff = status?.handoffRequest
  const planned = activeHostingRequest(status) !== null
  const requestOffered = planned && handoff?.recipientState === 'Requested'
  const waitingForHost = planned && handoff?.recipientState === 'Prepared' && !status?.readyForManualStart
  const minecraftTerms = planned && ['MinecraftJava', 'MinecraftBedrock'].includes(status?.game ?? '')
  const manualSetup = !!status?.branchHash && !status.readyForManualStart && !status.running
    && (status.code === 'LocalServerSetupRequired' || manualSetupBranch === status.branchHash)
  const historyChoice = status?.code === 'HostingHistoryChoiceRequired'
  if (!allowed && !open && presentation === 'action') return null
  return <>
    {presentation === 'card' ? <section className="local-hosting-card" aria-label="Host on this PC">
      <Icon name="server" size={24} /><div><h3>{requestOffered ? 'You have a hosting request' : 'Host on this PC'}</h3>
        <p>{requestOffered ? 'The current Host would like to move this save to your PC. Review and accept or decline.' :
          planned ? handoff?.message : allowed ? 'Host your own copy of this save. TogetherServer checks the server app, files and ports.'
          : status ? 'Ask the owner to turn on Can host this save for this PC.' : 'Checking hosting permission...'}</p></div>
      <Button className="secondary" disabled={disabled || !allowed} onClick={() => { setOpen(true); setError(''); setActionMessage('') }}>
        <Icon name="monitor" />{requestOffered ? 'Review hosting request' : planned ? 'Continue hosting move' : 'Host on this PC'}</Button>
    </section> : <Button className="secondary local-hosting-trigger" title="Host on this PC" disabled={disabled || !allowed}
      onClick={() => { setOpen(true); setError(''); setActionMessage('') }}><Icon name="server" />{requestOffered ? 'Review hosting request' : planned ? 'Continue hosting move' : 'Host on this PC'}</Button>}
    {open && <SettingsDialog title={requestOffered ? 'Hosting request' : 'Host on this PC'} description={worldName} busy={busy || manualBusy}
      onClose={() => { setOpen(false); plannedAuthorization.current = null; setPlannedOperation(null) }} wide={manualSetup}
      footer={requestOffered ? <><Button className="secondary" disabled={busy || disabled} onClick={() => void respond(false)}>Decline</Button>
        <Button disabled={busy || disabled || !allowed} onClick={() => void respond(true)}>Accept hosting request</Button></> :
        status?.running && status.localProfileId ? <Button onClick={() => onOpenHost(status.localProfileId!)}>Open local server</Button>
        : status?.readyForManualStart && planned ? <Button disabled={busy || manualBusy || disabled || !allowed || !status.localProfileId}
          onClick={() => onOpenHost(status.localProfileId!)}><Icon name="play" />Review Start server</Button>
        : status?.readyForManualStart ? <Button disabled={busy || manualBusy || disabled || !allowed || !accepted} onClick={() => void start()}>
          <Icon name={busy ? 'loader' : 'play'} />{busy ? 'Starting...' : 'Start server on this PC'}</Button>
          : waitingForHost && plannedOperation ? <span role="status">Waiting for the current Host to finish the move...</span>
          : <Button disabled={busy || manualBusy || disabled || manualSetup || historyChoice || !allowed || !planned && !accepted || minecraftTerms && !acceptedTerms || status?.needsServerFile && !serverFile.trim()
            || status?.needsPassword && !password} onClick={prepare}>
            <Icon name={busy ? 'loader' : 'server'} />{busy ? 'Preparing this PC...' : historyChoice ? 'Choose a save above' : manualSetup ? 'Finish setup above' : 'Prepare this PC'}</Button>}>
      <div className="local-hosting-flow" aria-busy={busy}>
        {requestOffered ? <>
          <p className="local-hosting-intro">The current Host would like you to host {worldName} on this PC.</p>
          <p>Accept to keep the verified save here and check your game setup. The current Host stops safely after your PC is prepared.</p>
          <p>You choose when to Start the server. Declining keeps hosting on the current PC.</p>
        </> : <>
        <p className="local-hosting-intro">{status?.running ? 'This save is already hosted on this PC.'
          : status?.readyForManualStart ? 'Your local save is prepared. Start when you’re ready.'
            : 'We’ll find the server app, get the save if needed, check its files and test this PC’s ports.'}</p>
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
        {status?.needsPassword && !status.readyForManualStart && <label>New game password<Input type="password"
          autoComplete="new-password" disabled={busy} value={password} onChange={event => setPassword(event.target.value)} /></label>}
        {minecraftTerms && !status?.readyForManualStart && <label className="local-hosting-consent"><Input type="checkbox"
          checked={acceptedTerms} disabled={busy} onChange={event => {
            setAcceptedTerms(event.target.checked); plannedAuthorization.current = null; setPlannedOperation(null)
          }} /><span>I have read and accept the terms for preparing this Minecraft server.
          <small>Review the <a href="https://www.minecraft.net/en-us/eula" target="_blank" rel="noreferrer">Minecraft EULA</a> and{' '}
            <a href="https://www.microsoft.com/en-us/privacy/privacystatement" target="_blank" rel="noreferrer">Microsoft Privacy Statement</a>.</small></span></label>}
        {manualSetup && status?.branchHash && <SharedWorldSeparateRoutePanel
          key={status.branchHash} profileId={profileId} separateCopies={1} game={status.game ?? undefined}
          selectedBranchHash={status.branchHash} suggestedSetup={status} expanded allowStart={false}
          onBrowseServerFile={onBrowseServerFile} onPrepared={() => void refresh()}
          onBusyChange={setManualActivity}
          onOpenSeparateHostControls={onOpenHost} />}
        {historyChoice && <SharedWorldSeparateRoutePanel profileId={profileId} separateCopies={1}
          game={status?.game ?? undefined} expanded onBrowseServerFile={onBrowseServerFile}
          onBusyChange={setManualActivity} onOpenSeparateHostControls={onOpenHost} />}
        {planned && <p className="helper-text">{handoff?.message} Starting the server remains your choice.</p>}
        {planned && status?.code === 'LocalServerSetupRequired' && status.localProfileId && <Button className="secondary"
          disabled={busy || disabled} onClick={() => onOpenHost(status.localProfileId!)}>Open game setup</Button>}
        {allowed && !planned && !status?.running && <label className="local-hosting-consent"><Input type="checkbox" checked={accepted} disabled={busy}
          onChange={event => setAccepted(event.target.checked)} /><span>Keep this save on my PC and host my own copy.
          <small>If another Host is running, progress stays separate. Existing saves are preserved.</small></span></label>}
        {status?.localProfileId && <Button className="text-button" disabled={busy}
          onClick={() => onOpenHost(status.localProfileId!)}>Local server &amp; connection details</Button>}
        {(status?.localProfileId || planned) && onOpenConnectionHelp && status && status.steps[4].state !== 'Ready' && <Button className="text-button"
          disabled={busy || manualBusy || disabled} onClick={() => {
            setOpen(false); plannedAuthorization.current = null; setPlannedOperation(null)
            onOpenConnectionHelp(status.localProfileId ?? profileId)
          }}>
          <Icon name="plug" />Connection help</Button>}
        {busy && <p role="status">{status?.readyForManualStart ? 'Starting the local server…' : 'Preparing and verifying your local copy. Keep TogetherServer open.'}</p>}
        </>}
        {actionMessage && !busy && <p role="status">{actionMessage}</p>}
        {error && <p role="alert">{error}</p>}
      </div>
    </SettingsDialog>}
  </>
}

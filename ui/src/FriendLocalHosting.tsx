import { useCallback, useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBasicResult } from './contracts'
import { Icon } from './Icon'
import { SettingsDialog } from './SettingsPresentation'
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
  remedy?: { kind: HostingRemedyKind; title: string; message: string } | null
}
const remedyKinds = ['Permission', 'Respond', 'ChooseHistory', 'Terms', 'ChooseServer', 'GameSetup', 'Ports', 'Connection', 'Review', 'ConfirmCopy', 'GetSave'] as const
type HostingRemedyKind = typeof remedyKinds[number]

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
  if (item.remedy != null) {
    if (typeof item.remedy !== 'object' || Array.isArray(item.remedy)) throw new Error('The hosting action could not be read.')
    const remedy = item.remedy as Record<string, unknown>
    if (!remedyKinds.includes(remedy.kind as HostingRemedyKind) || !boundedText(remedy.title, 160) || !boundedText(remedy.message, 1000))
      throw new Error('The hosting action could not be read.')
  }
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

// These are fixed local actions. A peer's message never becomes a URL or a
// navigation destination. Older status responses retain the same safe stops.
export function localHostingAttention(status: LocalHostingStatus | null, acceptedTerms = false, selectedFile = ''): LocalHostingStatus['remedy'] {
  if (!status) return { kind: 'Review', title: 'Retry the hosting checks', message: 'This PC could not check hosting. Retry before starting.' }
  if (status.running) return null
  if (!status.allowed) return { kind: 'Permission', title: 'Ask the owner to allow hosting', message: status.message }
  if (['HostingHistoryChoiceRequired', 'WorldHistoryReviewRequired', 'SharedSaveHistoryNeedsReview'].includes(status.code) || status.remedy?.kind === 'ChooseHistory')
    return { kind: 'ChooseHistory', title: 'This server has two different save histories',
      message: 'Ask the owner to resolve the conflicting saves before hosting here. All existing saves are kept.' }
  if (status.needsServerFile && !selectedFile.trim())
    return { kind: 'ChooseServer', title: 'Install or find the game server', message: 'This PC needs the matching dedicated server. Choose its installed file or open game setup.' }
  if (['MinecraftJava', 'MinecraftBedrock'].includes(status.game ?? '') && !acceptedTerms && !status.readyForManualStart && !status.localProfileId)
    return { kind: 'Terms', title: 'Review Minecraft’s terms', message: 'Read the terms and choose whether to accept them on this PC before continuing.' }
  if (['LocalConnectionSetupRequired', 'RecipientConnectionRequired', 'PeerDiscoveryRequired'].includes(status.code))
    return { kind: 'Connection', title: 'Let friends connect to this PC', message: status.message }
  if (['PortConflict', 'PortInUse', 'GamePortInUse', 'LocalPortsRequired'].includes(status.code))
    return { kind: 'Ports', title: 'Free the game ports on this PC', message: status.message }
  if (['PeerMembershipRequired', 'HostingPermissionChanged', 'HostingRequestChanged', 'SeparateCopyReviewRequired', 'SuccessorChecksPending', 'DataRecoveryRequired'].includes(status.code))
    return { kind: 'Review', title: 'Review this PC’s shared save', message: status.message }
  if (status.remedy?.kind === 'GetSave' || ['SharedSaveRequired', 'WaitingForSuccessorCopy'].includes(status.code))
    return { kind: 'GetSave', title: 'Wait for the latest completed save',
      message: 'Ask the current Host to stop the server safely so its latest save can be shared, then retry.' }
  if (status.remedy && !['ConfirmCopy', 'ChooseServer', 'Terms', 'Respond'].includes(status.remedy.kind)) return status.remedy
  if (status.code === 'LocalServerSetupRequired') return { kind: 'GameSetup', title: 'Finish game setup', message: status.message }
  if (status.steps[3].state === 'ActionRequired') return { kind: 'Ports', title: 'Free the game ports on this PC', message: status.steps[3].message }
  if (status.steps[4].state === 'ActionRequired') return { kind: 'Connection', title: 'Let friends connect to this PC', message: status.steps[4].message }
  return null
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
  onOpenGameSetup?: (game: string, localProfileId?: string) => void
  onReviewSharedSave?: () => void
}

export function FriendLocalHosting({ profileId, connectionId, worldName, available, accessBlocked = false,
  disabled = false, presentation = 'action', onBrowseServerFile, onOpenHost, onOpenConnectionHelp, onOpenGameSetup }: FriendLocalHostingProps) {
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
  const [networkConfirmed, setNetworkConfirmed] = useState(false)
  const [acceptedTerms, setAcceptedTerms] = useState(false)
  const [actionMessage, setActionMessage] = useState('')
  const [plannedOperation, setPlannedOperation] = useState<{ requestId: string; until: number } | null>(null)
  const startIntent = useRef<{ scope: string; until: number; requestId: string | null } | null>(null)
  const plannedAuthorization = useRef(plannedOperation)
  plannedAuthorization.current = plannedOperation
  const plannedAttempt = useRef('')
  const currentStatus = useRef(status)
  currentStatus.current = status
  const acceptStatus = useCallback((next: LocalHostingStatus, preparedHere = false) => {
    if (!next.allowed || !next.consented || next.running || next.remedy &&
      ['Connection', 'Ports', 'Review', 'ChooseHistory', 'ChooseServer', 'GameSetup', 'Terms', 'GetSave'].includes(next.remedy.kind) ||
      !preparedHere && currentStatus.current?.branchHash && currentStatus.current.branchHash !== next.branchHash) startIntent.current = null
    if (activeHostingRequest(currentStatus.current)?.requestId !== activeHostingRequest(next)?.requestId) {
      setAcceptedTerms(false); setPassword('')
      plannedAuthorization.current = null; setPlannedOperation(null); plannedAttempt.current = ''
      startIntent.current = null
    }
    currentStatus.current = next
    setStatus(next)
  }, [])
  const controller = useRef<AbortController | null>(null)
  const invalidate = useCallback(() => { ++request.current; controller.current?.abort() }, [])

  useEffect(() => {
    mutation.current = false
    startIntent.current = null
    setStatus(null); setOpen(false); setBusy(false); setError(''); setAcceptedTerms(false)
    setServerFile(''); setPassword(''); setActionMessage('')
    setNetworkConfirmed(false)
    setPlannedOperation(null); plannedAttempt.current = ''
    if (!available || accessBlocked) return
    let active = true
    let timer: number | undefined
    let currentRead: AbortController | null = null
    let timeout: number | undefined
    const read = async () => {
      if (!mutation.current) {
        const sequence = ++request.current
        const abort = new AbortController()
        currentRead = abort
        timeout = window.setTimeout(() => {
          if (active && sequence === request.current) {
            setStatus(null); plannedAuthorization.current = null; setPlannedOperation(null); ++request.current
            startIntent.current = null
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
            startIntent.current = null
          }
        } finally { window.clearTimeout(timeout) }
      }
      if (active) timer = window.setTimeout(() => void read(), 10000)
    }
    void read()
    return () => { active = false; currentRead?.abort(); window.clearTimeout(timer); window.clearTimeout(timeout); invalidate() }
  }, [scope, base, available, accessBlocked, acceptStatus, invalidate])

  useEffect(() => {
    if (disabled || accessBlocked) startIntent.current = null
    return () => { startIntent.current = null }
  }, [disabled, accessBlocked, scope])

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
      if (sequence === request.current && !abort.signal.aborted) {
        setStatus(null); startIntent.current = null; setError(errorMessage(cause))
      }
    } finally {
      window.clearTimeout(timeout)
      if (sequence === request.current) { mutation.current = false; setBusy(false) }
    }
  }
  const prepareStatus = useCallback(async (target: LocalHostingStatus, authorization: typeof plannedOperation) => {
    if (mutation.current || disabled || !target.allowed) return
    const planned = activeHostingRequest(target)
    if (planned?.recipientState === 'Requested') return
    if (planned?.requestId && ['MinecraftJava', 'MinecraftBedrock'].includes(target.game ?? '') && !acceptedTerms) return
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    setBusy(true); setError(''); setActionMessage('')
    const timeout = window.setTimeout(() => {
      if (sequence !== request.current || authorization && plannedAuthorization.current !== authorization) return
      abort.abort(); ++request.current; mutation.current = false; setBusy(false)
      plannedAuthorization.current = null; setPlannedOperation(null); setStatus(null)
      startIntent.current = null; setOpen(true)
      setError('Preparation did not finish checking in time. Recheck the hosting request before continuing. Existing saves were kept.')
    }, Math.min(120000, Math.max(0, (authorization?.until ?? Date.now() + 120000) - Date.now())))
    try {
      const next = await changeJson(`${base}/hosting/prepare`, 'POST', parseLocalHostingStatus, {
        allowReceiving: true, acceptSplitWarning: !planned?.requestId, requirePeerConnection: true,
        serverFile: serverFile.trim() || null, gamePassword: password || null,
        ...(planned?.requestId ? { requestId: planned.requestId, acceptedTerms } :
          ['MinecraftJava', 'MinecraftBedrock'].includes(target.game ?? '') ? { acceptedTerms } : {})
      }, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      if (authorization && plannedAuthorization.current !== authorization) return
      if (planned?.requestId && currentStatus.current?.handoffRequest?.requestId !== planned.requestId) return
      acceptStatus(next, true); setPassword(''); setActionMessage(next.message)
      if (!next.ok || localHostingAttention(next, acceptedTerms, serverFile)) {
        startIntent.current = null; setOpen(true)
      }
      if (authorization && (!next.ok || next.readyForManualStart)) {
        plannedAuthorization.current = null; setPlannedOperation(null)
      }
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) {
        setError(errorMessage(cause)); plannedAuthorization.current = null; setPlannedOperation(null)
        startIntent.current = null; setOpen(true)
      }
    } finally {
      window.clearTimeout(timeout)
      if (sequence === request.current) { mutation.current = false; setBusy(false) }
    }
  }, [base, disabled, serverFile, password, acceptedTerms, acceptStatus])
  const prepare = () => {
    const invitation = activeHostingRequest(status)
    if (!status?.allowed) return
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
        startIntent.current = { scope, until: authorization.until, requestId: invitation.requestId }
        plannedAuthorization.current = authorization; setPlannedOperation(authorization)
      }
    } catch (cause) { if (sequence === request.current && !abort.signal.aborted) setError(errorMessage(cause)) }
    finally { if (sequence === request.current) { mutation.current = false; setBusy(false) } }
  }
  useEffect(() => {
    if (!open || !plannedOperation || !status?.handoffRequest || mutation.current || disabled) return
    const invitation = status.handoffRequest
    if (invitation.requestId !== plannedOperation.requestId || ['Requested', 'Declined', 'Canceled'].includes(invitation.recipientState ?? '')) {
      plannedAuthorization.current = null; setPlannedOperation(null)
      startIntent.current = null
      return
    }
    if (status.readyForManualStart || status.needsServerFile && !serverFile.trim() || status.needsPassword && !password) return
    if (['MinecraftJava', 'MinecraftBedrock'].includes(status.game ?? '') && !acceptedTerms) return
    if (invitation.recipientState === 'Prepared') return
    const attempt = JSON.stringify([invitation.requestId, invitation.recipientState, invitation.finalVersion, invitation.preparedVersionHash])
    if (plannedAttempt.current === attempt) return
    plannedAttempt.current = attempt
    void prepareStatus(status, plannedOperation)
  }, [status, open, plannedOperation, disabled, serverFile, password, acceptedTerms, prepareStatus])
  useEffect(() => {
    if (!plannedOperation) return
    const timeout = window.setTimeout(() => {
      plannedAuthorization.current = null; setPlannedOperation(null)
      startIntent.current = null
      if (mutation.current) { controller.current?.abort(); ++request.current; mutation.current = false; setBusy(false); setStatus(null) }
      setActionMessage('Preparation is paused. Choose Continue and start when you want to resume this move.')
    }, Math.max(0, plannedOperation.until - Date.now()))
    return () => window.clearTimeout(timeout)
  }, [plannedOperation])
  const startPrepared = useCallback(async (target: LocalHostingStatus) => {
    const intent = startIntent.current
    if (mutation.current || disabled || !intent || intent.scope !== scope || intent.until <= Date.now()
      || !target.allowed || !target.readyForManualStart || target.running || !target.localProfileId
      || intent.requestId !== (activeHostingRequest(target)?.requestId ?? null)) return
    startIntent.current = null // consume before the request; no poll/error retry can repeat Start
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    setBusy(true); setError(''); setActionMessage('')
    const localProfileId = target.localProfileId
    const timeout = window.setTimeout(() => {
      if (sequence !== request.current) return
      abort.abort(); ++request.current; mutation.current = false; setBusy(false); setOpen(true); setStatus(null)
      setError('The Start result could not be checked in time. Open local servers to check this PC before trying again.')
    }, 120000)
    try {
      const result = await changeJson(`${base}/hosting/start`, 'POST', parseBasicResult, {
        branchHash: target.branchHash, requestId: intent.requestId, localProfileId,
        acceptSplitWarning: !intent.requestId
      }, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      setActionMessage(result.message)
      if (result.ok) { setOpen(false); onOpenHost(localProfileId) }
      else {
        setOpen(true)
        const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
        if (sequence === request.current && !abort.signal.aborted) acceptStatus({ ...next, ok: false,
          code: result.code, message: result.message, readyForManualStart: false, remedy: null })
      }
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) setError(errorMessage(cause))
      if (sequence === request.current) setOpen(true)
    } finally {
      window.clearTimeout(timeout)
      if (sequence === request.current) { mutation.current = false; setBusy(false) }
    }
  }, [base, scope, disabled, onOpenHost, acceptStatus])
  useEffect(() => {
    if (busy || disabled || !status?.readyForManualStart || !startIntent.current) return
    if (localHostingAttention(status, acceptedTerms, serverFile)) { startIntent.current = null; setOpen(true); return }
    void startPrepared(status)
  }, [status, busy, disabled, acceptedTerms, serverFile, startPrepared])

  const host = () => {
    if (mutation.current || disabled || !status?.allowed) return
    if (status.running && status.localProfileId) { onOpenHost(status.localProfileId); return }
    setError(''); setActionMessage('')
    const attention = localHostingAttention(status, acceptedTerms, serverFile)
    if (attention && ['Permission', 'ChooseHistory'].includes(attention.kind)) return
    // The dialog's confirmation authorizes this bounded preparation and Start.
    if (attention && !open) { setOpen(true); return }
    startIntent.current = { scope, until: Date.now() + 10 * 60 * 1000,
      requestId: activeHostingRequest(status)?.requestId ?? null }
    const invitation = activeHostingRequest(status)
    if (invitation?.recipientState === 'Prepared' && !status.readyForManualStart) {
      if (!plannedOperation) setPlannedOperation({ requestId: invitation.requestId!, until: startIntent.current.until })
      setActionMessage('Start is requested for this move only. This PC will start after the current Host finishes and every final check passes. Close this dialog to cancel that request.')
      setOpen(true)
      return
    }
    prepare()
  }
  const browse = async () => {
    if (!status?.game || !onBrowseServerFile || mutation.current || disabled) return
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
  const handoff = activeHostingRequest(status)
  const requestOffered = handoff?.recipientState === 'Requested'
  const waitingForHost = handoff?.recipientState === 'Prepared' && !status?.readyForManualStart
  const minecraftTerms = ['MinecraftJava', 'MinecraftBedrock'].includes(status?.game ?? '') && !status?.readyForManualStart && !status?.localProfileId
  const attention = localHostingAttention(status, acceptedTerms, serverFile)
  const historyChoice = attention?.kind === 'ChooseHistory'
  const pause = () => {
    startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null)
    setNetworkConfirmed(false)
    invalidate(); mutation.current = false; setBusy(false); setOpen(false)
  }
  const connectionHelp = () => {
    if (!onOpenConnectionHelp || !status) return
    pause(); onOpenConnectionHelp(status.localProfileId ?? profileId)
  }
  const gameSetup = () => {
    if (!onOpenGameSetup || !status?.game) return
    pause(); onOpenGameSetup(status.game, status.localProfileId ?? undefined)
  }
  const trigger = () => {
    if (disabled || busy || !allowed) return
    if (status?.running && status.localProfileId) { onOpenHost(status.localProfileId); return }
    setOpen(true); setNetworkConfirmed(false); setError(''); setActionMessage('')
    void refresh()
  }
  const canContinue = allowed && !busy && !disabled && !historyChoice
    && !(minecraftTerms && !acceptedTerms) && !(status?.needsServerFile && !serverFile.trim())
    && !(status?.needsPassword && !password) && attention?.kind !== 'Permission'
  if (!allowed && !open) return null
  return <>
    {allowed && (presentation === 'card' ? <section className="local-hosting-card" aria-label="Host locally">
      <Icon name="server" size={24} /><div><h3>Host locally</h3>
        <p>{status?.running ? 'This server is already hosted on this PC.' : 'Use the latest available save to host this server on your PC.'}</p></div>
      <Button className="secondary" disabled={disabled || busy} onClick={trigger}><Icon name={busy ? 'loader' : 'server'} />Host locally</Button>
    </section> : <Button className="secondary icon-button local-hosting-trigger" title="Host locally" aria-label="Host locally"
      disabled={disabled || busy} onClick={trigger}><Icon name={busy ? 'loader' : 'server'} /></Button>)}
    {open && <SettingsDialog title="Host locally" description={worldName} busy={busy}
      onClose={pause} footer={status?.running && status.localProfileId ?
        <Button onClick={() => onOpenHost(status.localProfileId!)}>Open local server</Button> :
        waitingForHost && plannedOperation && startIntent.current ? <span role="status">Waiting for the current Host to finish the save...</span> :
        <Button disabled={!canContinue} onClick={() => { setNetworkConfirmed(true); if (requestOffered) void respond(true); else host() }}><Icon name={busy ? 'loader' : 'play'} />
          {busy ? 'Checking this PC...' : networkConfirmed ? 'Retry and start' : 'Host locally'}</Button>}>
      <div className="local-hosting-flow" aria-busy={busy}>
        {!status?.running && !networkConfirmed && <p className="local-hosting-warning">Hosting lets other players connect to this PC through your network. Keep TogetherServer open while hosting. The app will get the latest available save, check this PC and start when it is ready.</p>}
        <>
          {busy ? <p role="status">Checking the game, save and this PC's connection...</p> :
            waitingForHost ? <p role="status">Waiting for the current Host to finish the latest save.</p> :
            status?.running ? <p>This server is already hosted on this PC.</p> : attention &&
            <section className="local-hosting-next" aria-label="Next hosting step"><h3>{attention.title}</h3><p>{attention.message}</p></section>}
          {attention?.kind === 'ChooseServer' && <div className="local-hosting-fix">
            {status?.game === 'Valheim' && <a href="steam://install/896660">Install Valheim Dedicated Server in Steam</a>}
            {['MinecraftJava', 'MinecraftBedrock'].includes(status?.game ?? '') && <a href="https://www.minecraft.net/en-us/download/server" target="_blank" rel="noreferrer">Get the official Minecraft server</a>}
            {onOpenGameSetup && status?.localProfileId && <Button className="secondary" disabled={busy || disabled} onClick={gameSetup}><Icon name="server" />Open game setup</Button>}
            <label>Installed server file<div className="field-with-button"><Input aria-label="Installed server file" value={serverFile} disabled={busy}
              placeholder={status?.game === 'MinecraftJava' ? 'Choose the installed server.jar' : 'Choose the installed dedicated server'}
              onChange={event => setServerFile(event.target.value)} />
              {onBrowseServerFile && <Button className="secondary" disabled={busy || !status?.game} onClick={() => void browse()}><Icon name="folder" />Browse</Button>}</div></label>
            <p className="helper-text">Install the same game version as this save, then choose its server file. Accept the game's terms yourself. Existing worlds are kept.</p>
          </div>}
          {status?.needsPassword && !status.readyForManualStart && <label>New game password<Input type="password"
            autoComplete="new-password" disabled={busy} value={password} onChange={event => setPassword(event.target.value)} /></label>}
          {minecraftTerms && <label className="local-hosting-consent"><Input type="checkbox" checked={acceptedTerms} disabled={busy}
            onChange={event => { setAcceptedTerms(event.target.checked); startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null) }} />
            <span>I have read and accept the terms for preparing this Minecraft server.
              <small>Review the <a href="https://www.minecraft.net/en-us/eula" target="_blank" rel="noreferrer">Minecraft EULA</a> and{' '}
                <a href="https://www.microsoft.com/en-us/privacy/privacystatement" target="_blank" rel="noreferrer">Microsoft Privacy Statement</a>.</small></span></label>}
          {attention?.kind === 'Connection' && <div className="local-hosting-fix"><p>Use Connection help to check this PC's address and the port friends need. It explains any Windows, router or provider problem that needs your action.</p>
            {onOpenConnectionHelp && <Button className="secondary" disabled={busy || disabled} onClick={connectionHelp}><Icon name="plug" />Connection help</Button>}
          </div>}
          {attention?.kind === 'Ports' && <div className="local-hosting-fix"><p>Open local servers to find the app using these game ports. Stop it only if it is yours, or choose another game port in Setup, then return and continue.</p>
            {status?.localProfileId && <Button className="secondary" disabled={busy || disabled} onClick={() => { pause(); onOpenHost(status.localProfileId!) }}>Open local server setup</Button>}
            {onOpenGameSetup && status?.localProfileId && <Button className="secondary" disabled={busy || disabled} onClick={gameSetup}>Open game setup</Button>}
          </div>}
          {attention?.kind === 'GameSetup' && status?.localProfileId && onOpenGameSetup && <Button className="secondary" disabled={busy || disabled} onClick={gameSetup}>Open game setup</Button>}
          <details className="local-hosting-results"><summary>Automatically checked results</summary>
            {status ? <ol className="local-hosting-checks" aria-label="Local hosting checks">{status.steps.map(step => <li key={step.key} data-state={step.state}>
              <Icon name={step.state === 'Ready' ? 'check' : step.state === 'ActionRequired' ? 'warning' : 'plug'} />
              <div><strong>{checkLabels[step.key]}</strong><p>{step.message}</p></div>
              <span>{step.state === 'Ready' ? 'Checked' : step.state === 'ActionRequired' ? 'Needs attention' : 'Unverified'}</span>
            </li>)}</ol> : <p role="alert">Hosting checks are unavailable. Retry before starting.</p>}
            <p className="helper-text">These results cover only the checks described. Loading and saving in the real game and a friend joining from another network require separate testing.</p>
          </details>
          <div className="local-hosting-secondary-actions"><Button className="text-button" disabled={busy || disabled} onClick={() => void refresh()}><Icon name="refresh" />Retry automatic checks</Button>
            {status?.localProfileId && <Button className="text-button" disabled={busy || disabled}
              onClick={() => { pause(); onOpenHost(status.localProfileId!) }}>Local server &amp; connection details</Button>}
            {onOpenConnectionHelp && attention?.kind !== 'Connection' && status?.steps[4].state !== 'Ready' && <Button className="text-button"
              disabled={busy || disabled} onClick={connectionHelp}><Icon name="plug" />Connection help</Button>}
          </div>
        </>
        {actionMessage && !busy && <p role="status">{actionMessage}</p>}
        {error && <p role="alert">{error}</p>}
      </div>
    </SettingsDialog>}
  </>
}

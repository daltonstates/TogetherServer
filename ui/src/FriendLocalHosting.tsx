import { useCallback, useEffect, useId, useRef, useState } from 'react'
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
export function localHostingAttention(status: LocalHostingStatus | null, accepted = false,
  acceptedTerms = false, selectedFile = ''): LocalHostingStatus['remedy'] {
  if (!status) return { kind: 'Review', title: 'Retry the hosting checks', message: 'This PC could not check hosting. Retry before starting.' }
  if (status.running) return null
  const planned = activeHostingRequest(status)
  if (!status.allowed) return { kind: 'Permission', title: 'Ask the owner to allow hosting', message: status.message }
  if (planned?.recipientState === 'Requested') return { kind: 'Respond', title: 'Review the hosting request', message: 'Accept or decline before preparing this PC.' }
  if (status.code === 'HostingHistoryChoiceRequired' || status.code === 'WorldHistoryReviewRequired')
    return { kind: 'ChooseHistory', title: 'Choose which save to host', message: status.message }
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
  if (status.remedy && !['ConfirmCopy', 'ChooseServer', 'Terms', 'Respond'].includes(status.remedy.kind)) return status.remedy
  if (status.code === 'LocalServerSetupRequired') return { kind: 'GameSetup', title: 'Finish game setup', message: status.message }
  if (status.steps[3].state === 'ActionRequired') return { kind: 'Ports', title: 'Free the game ports on this PC', message: status.steps[3].message }
  if (status.steps[4].state === 'ActionRequired') return { kind: 'Connection', title: 'Let friends connect to this PC', message: status.steps[4].message }
  if (!planned && !accepted && (!status.consented || !status.branchHash))
    return { kind: 'ConfirmCopy', title: 'Review before hosting your own copy', message: 'Another Host may still be running. Progress made on each PC stays separate. Starting here does not move or replace that Host.' }
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
  disabled = false, presentation = 'action', onBrowseServerFile, onOpenHost, onOpenConnectionHelp, onOpenGameSetup, onReviewSharedSave }: FriendLocalHostingProps) {
  const base = `/api/local/friend/${profileId}/shared-world`
  const splitNoteId = useId()
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
  const startIntent = useRef<{ scope: string; until: number; requestId: string | null } | null>(null)
  const plannedAuthorization = useRef(plannedOperation)
  plannedAuthorization.current = plannedOperation
  const plannedAttempt = useRef('')
  const currentStatus = useRef(status)
  currentStatus.current = status
  const manualOperation = useRef(false)
  const setManualActivity = useCallback((value: boolean) => { manualOperation.current = value; setManualBusy(value) }, [])
  const acceptStatus = useCallback((next: LocalHostingStatus) => {
    if (!next.allowed || !next.consented || next.running || next.remedy &&
      ['Connection', 'Ports', 'Review', 'ChooseHistory', 'ChooseServer', 'GameSetup', 'Terms', 'GetSave'].includes(next.remedy.kind) ||
      currentStatus.current?.branchHash && currentStatus.current.branchHash !== next.branchHash) startIntent.current = null
    if (activeHostingRequest(currentStatus.current)?.requestId !== activeHostingRequest(next)?.requestId) {
      setAcceptedTerms(false); setAccepted(false); setPassword('')
      plannedAuthorization.current = null; setPlannedOperation(null); plannedAttempt.current = ''
      startIntent.current = null
    }
    currentStatus.current = next
    setStatus(next)
    if (next.code === 'LocalServerSetupRequired') setManualSetupBranch(next.branchHash)
  }, [])
  const controller = useRef<AbortController | null>(null)
  const invalidate = useCallback(() => { ++request.current; controller.current?.abort() }, [])

  useEffect(() => {
    mutation.current = false
    startIntent.current = null
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
  }, [scope, base, available, accessBlocked, acceptStatus, invalidate, setManualActivity])

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
      acceptStatus(next); setPassword(''); setActionMessage(next.message)
      if (!next.ok || localHostingAttention(next, accepted, acceptedTerms, serverFile)) {
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
  }, [base, disabled, manualBusy, serverFile, password, accepted, acceptedTerms, acceptStatus])
  const prepare = () => {
    const invitation = activeHostingRequest(status)
    if (!status?.allowed || !invitation && !accepted && !(status.branchHash && status.consented)) return
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
  }, [status, open, plannedOperation, disabled, manualBusy, serverFile, password, acceptedTerms, prepareStatus])
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
    if (mutation.current || manualBusy || disabled || !intent || intent.scope !== scope || intent.until <= Date.now()
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
    }, 30000)
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
  }, [base, scope, manualBusy, disabled, onOpenHost, acceptStatus])
  useEffect(() => {
    if (busy || manualBusy || disabled || !status?.readyForManualStart || !startIntent.current) return
    if (localHostingAttention(status, accepted, acceptedTerms, serverFile)) { startIntent.current = null; setOpen(true); return }
    void startPrepared(status)
  }, [status, busy, manualBusy, disabled, accepted, acceptedTerms, serverFile, startPrepared])

  const host = () => {
    if (mutation.current || manualBusy || disabled || !status?.allowed) return
    if (status.running && status.localProfileId) { onOpenHost(status.localProfileId); return }
    setError(''); setActionMessage('')
    const attention = localHostingAttention(status, accepted, acceptedTerms, serverFile)
    if (attention && !['GameSetup', 'Review', 'Ports', 'Connection', 'GetSave'].includes(attention.kind)) { setOpen(true); return }
    // The first click opens at a known action. Continuing after a remedy is a
    // new deliberate instruction; checking a box or polling never supplies it.
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
    if (status.readyForManualStart && !attention) { void startPrepared(status); return }
    prepare()
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
  const minecraftTerms = ['MinecraftJava', 'MinecraftBedrock'].includes(status?.game ?? '') && !status?.readyForManualStart && !status?.localProfileId
  const manualSetup = !!status?.branchHash && !status.readyForManualStart && !status.running
    && (status.code === 'LocalServerSetupRequired' || manualSetupBranch === status.branchHash)
  const historyChoice = ['HostingHistoryChoiceRequired', 'WorldHistoryReviewRequired'].includes(status?.code ?? '')
  const attention = localHostingAttention(status, accepted, acceptedTerms, serverFile)
  const showCopyConsent = allowed && !planned && !status?.running && (!status?.consented || !status?.branchHash || accepted)
  const pause = () => {
    startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null)
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
    if (requestOffered) { setOpen(true); setError(''); setActionMessage(''); return }
    host()
  }
  const canContinue = allowed && !busy && !manualBusy && !disabled && !manualSetup && !historyChoice
    && !(showCopyConsent && !accepted) && !(minecraftTerms && !acceptedTerms)
    && !(status?.needsServerFile && !serverFile.trim())
    && !(status?.needsPassword && !password)
    && attention?.kind !== 'Permission'
  const splitNote = !!status?.branchHash && !planned && !status.running
    ? <small id={splitNoteId} className="local-hosting-action-note">Starts your own copy. The other Host can keep running; progress on the two servers stays separate.</small> : null
  if (!allowed && !open && presentation === 'action') return null
  return <>
    {presentation === 'card' ? <section className="local-hosting-card" aria-label="Host on this PC">
      <Icon name="server" size={24} /><div><h3>{requestOffered ? 'You have a hosting request' : 'Host on this PC'}</h3>
        <p>{requestOffered ? 'The current Host would like to move this save to your PC. Accept or decline.' :
          status?.running ? 'This save is already hosted here.' : allowed ? 'Check this PC, prepare the save and start. We ask when something needs your attention.' :
            status ? 'Ask the owner to turn on Can host this save for this PC.' : 'Checking hosting permission...'}</p>{splitNote}</div>
      <Button className="secondary" aria-describedby={splitNote ? splitNoteId : undefined} disabled={disabled || busy || !allowed} onClick={trigger}>
        <Icon name={busy ? 'loader' : 'monitor'} />{busy ? 'Checking this PC...' : requestOffered ? 'Review hosting request' : 'Host on this PC'}</Button>
    </section> : <span className="local-hosting-action"><Button className="secondary local-hosting-trigger" title="Host on this PC"
      aria-describedby={splitNote ? splitNoteId : undefined} disabled={disabled || busy || !allowed}
      onClick={trigger}><Icon name={busy ? 'loader' : 'server'} />{busy ? 'Checking this PC...' : requestOffered ? 'Review hosting request' : 'Host on this PC'}</Button>{splitNote}</span>}
    {busy && !open && <span className="local-hosting-progress" role="status">Checking this PC and preparing the save before Start...</span>}
    {open && <SettingsDialog title={requestOffered ? 'Hosting request' : 'Host on this PC'} description={worldName} busy={busy || manualBusy}
      onClose={pause} wide={manualSetup || historyChoice}
      footer={requestOffered ? <><Button className="secondary" disabled={busy || disabled} onClick={() => void respond(false)}>Decline</Button>
        <Button disabled={busy || disabled || !allowed} onClick={() => void respond(true)}>Accept hosting request</Button></> :
        status?.running && status.localProfileId ? <Button onClick={() => onOpenHost(status.localProfileId!)}>Open local server</Button> :
        waitingForHost && plannedOperation ? startIntent.current ? <span role="status">Start requested. Waiting for the current Host to finish the move...</span> :
          <Button disabled={busy || manualBusy || disabled} onClick={host}>Start here when the move finishes</Button> :
        <Button disabled={!canContinue} onClick={host}><Icon name={busy ? 'loader' : 'play'} />
          {busy ? 'Checking this PC...' : historyChoice ? 'Choose a save above' : manualSetup ? 'Finish game setup above' : 'Continue and start'}</Button>}>
      <div className="local-hosting-flow" aria-busy={busy}>
        {requestOffered ? <>
          <p className="local-hosting-intro">The current Host would like you to host {worldName} on this PC.</p>
          <p>Accept to keep a save here and check your game setup. The current Host stops safely after your PC is prepared.</p>
          <p>Accepting lets preparation continue for this move. Choose Host on this PC when you want the prepared server to start. Declining keeps hosting on the current PC.</p>
        </> : <>
          {busy ? <p role="status">Checking the game, save and this PC's ports. Keep TogetherServer open.</p> :
            waitingForHost ? <section className="local-hosting-next" aria-label="Next hosting step"><h3>Waiting for the final save</h3>
              <p>The current Host must stop safely and finish the move. This PC checks the exact final copy before it can start.</p></section> :
            status?.running ? <p>This save is already hosted on this PC.</p> :
            <section className="local-hosting-next" aria-label="Next hosting step"><h3>{attention?.title ?? (status?.readyForManualStart ? 'This PC is prepared' : 'Ready to check this PC')}</h3>
              <p>{attention?.message ?? 'Continue to run the remaining checks and start. If anything needs fixing, we will show that step.'}</p></section>}
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
          {showCopyConsent && <label className="local-hosting-consent"><Input type="checkbox" checked={accepted} disabled={busy}
            onChange={event => { setAccepted(event.target.checked); startIntent.current = null }} /><span>Keep this save on my PC and host my own copy.
              <small>If another Host is running, progress stays separate. Existing saves are preserved. Approved friends can discover this PC's saved connection. Continue and start checks this copy, then starts it if the checks pass.</small></span></label>}
          {attention?.kind === 'Connection' && <div className="local-hosting-fix"><p>Use Connection help to check this PC's address and the port friends need. It explains any Windows, router or provider problem that needs your action.</p>
            {onOpenConnectionHelp && <Button className="secondary" disabled={busy || disabled} onClick={connectionHelp}><Icon name="plug" />Connection help</Button>}
          </div>}
          {attention?.kind === 'Ports' && <div className="local-hosting-fix"><p>Open local servers to find the app using these game ports. Stop it only if it is yours, or choose another game port in Setup, then return and continue.</p>
            {status?.localProfileId && <Button className="secondary" disabled={busy || disabled} onClick={() => { pause(); onOpenHost(status.localProfileId!) }}>Open local server setup</Button>}
            {onOpenGameSetup && status?.localProfileId && <Button className="secondary" disabled={busy || disabled} onClick={gameSetup}>Open game setup</Button>}
          </div>}
          {attention?.kind === 'GameSetup' && onOpenGameSetup && <Button className="secondary" disabled={busy || disabled} onClick={gameSetup}>Open game setup</Button>}
          {(attention?.kind === 'GetSave' || attention?.kind === 'Review') && onReviewSharedSave && <Button className="secondary" disabled={busy || disabled}
            onClick={() => { pause(); onReviewSharedSave() }}>Review shared saves</Button>}
          {manualSetup && status?.branchHash && <details className="local-hosting-advanced" open><summary>Advanced setup for this save's add-ons</summary>
            <SharedWorldSeparateRoutePanel key={status.branchHash} profileId={profileId} separateCopies={1} game={status.game ?? undefined}
              selectedBranchHash={status.branchHash} suggestedSetup={status} expanded allowStart={false}
              onBrowseServerFile={onBrowseServerFile} onPrepared={() => void refresh()} onBusyChange={setManualActivity}
              onOpenSeparateHostControls={onOpenHost} /></details>}
          {historyChoice && <SharedWorldSeparateRoutePanel profileId={profileId} separateCopies={1}
            game={status?.game ?? undefined} expanded allowStart={false} onBrowseServerFile={onBrowseServerFile}
            onBusyChange={setManualActivity} onOpenSeparateHostControls={onOpenHost} />}
          {planned && <p className="helper-text">{handoff?.message} Accepting or checking a move does not start a game. Only your Host on this PC instruction can do that.</p>}
          <details className="local-hosting-results"><summary>Automatically checked results</summary>
            {status ? <ol className="local-hosting-checks" aria-label="Local hosting checks">{status.steps.map(step => <li key={step.key} data-state={step.state}>
              <Icon name={step.state === 'Ready' ? 'check' : step.state === 'ActionRequired' ? 'warning' : 'plug'} />
              <div><strong>{checkLabels[step.key]}</strong><p>{step.message}</p></div>
              <span>{step.state === 'Ready' ? 'Checked' : step.state === 'ActionRequired' ? 'Needs attention' : 'Unverified'}</span>
            </li>)}</ol> : <p role="alert">Hosting checks are unavailable. Retry before starting.</p>}
            <p className="helper-text">These results cover only the checks described. Loading and saving in the real game and a friend joining from another network require separate testing.</p>
          </details>
          <div className="local-hosting-secondary-actions"><Button className="text-button" disabled={busy || manualBusy || disabled} onClick={() => void refresh()}><Icon name="refresh" />Retry automatic checks</Button>
            {status?.localProfileId && <Button className="text-button" disabled={busy || manualBusy || disabled}
              onClick={() => { pause(); onOpenHost(status.localProfileId!) }}>Local server &amp; connection details</Button>}
            {onOpenConnectionHelp && attention?.kind !== 'Connection' && status?.steps[4].state !== 'Ready' && <Button className="text-button"
              disabled={busy || manualBusy || disabled} onClick={connectionHelp}><Icon name="plug" />Connection help</Button>}
          </div>
        </>}
        {actionMessage && !busy && <p role="status">{actionMessage}</p>}
        {error && <p role="alert">{error}</p>}
      </div>
    </SettingsDialog>}
  </>
}

import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { ApiError, changeJson, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBasicResult } from './contracts'
import { Icon } from './Icon'
import { SettingsDialog } from './SettingsPresentation'
import { parsePlannedHandoffStatus, type PlannedHandoffStatus } from './plannedHandoff'
import { LocalHostingGuide, localHostingAccessBlocker } from './LocalHostingGuide'
import { HostingPhaseTimings, hostingProgressFilesLabel, hostingProgressPhaseLabel, LocalHostingProgress, type HostingProgress } from './LocalHostingProgress'
import { formatSharedBytes } from './sharedWorldUx'
import { FriendSaveTransferProgress, useFriendSaveTransfer } from './FriendSaveTransfer'
import { parseApprovedGamePorts, type ApprovedGamePort } from './ApprovedGamePorts'
import { LocalHostingPorts, useLocalHostingPorts } from './LocalHostingPorts'
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
  needsPortApproval?: boolean; selectedApprovedPortId?: string | null; approvedPorts?: ApprovedGamePort[]
  factorioRconPort?: number | null
  remedy?: { kind: HostingRemedyKind; title: string; message: string } | null
}
export type LocalHostingPermission = { allowed: boolean; code: string; message: string }
function confirmedHostingDenial(permission: LocalHostingPermission | null): boolean {
  return permission?.allowed === false && ['HostingPermissionRequired', 'HostingPermissionDenied', 'EnrollmentDenied',
    'Revoked', 'AccessExpired', 'ApprovalPending', 'NotPaired'].includes(permission.code)
}
const remedyKinds = ['Permission', 'Respond', 'ChooseHistory', 'Terms', 'ChooseServer', 'GameSetup', 'Ports', 'Connection', 'Review', 'ConfirmCopy', 'GetSave', 'ShareStoppedProgress'] as const
type HostingRemedyKind = typeof remedyKinds[number]

function needsStoppedProgressSharing(status: LocalHostingStatus | null): boolean {
  return status?.code === 'LocalStoppedProgressNeedsSharing' || status?.remedy?.kind === 'ShareStoppedProgress'
}

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const hashPattern = /^[0-9a-f]{64}$/i
function boundedText(value: unknown, max: number, nullable = false): boolean {
  return nullable && value === null || typeof value === 'string' && value.length <= max
    && ![...value].some(character => character.charCodeAt(0) < 32 && !['\t', '\n', '\r'].includes(character))
}
export function parseLocalHostingPermission(value: unknown): LocalHostingPermission {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Hosting permission could not be checked.')
  const item = value as Record<string, unknown>
  if (typeof item.allowed !== 'boolean' || !boundedText(item.code, 100) || !boundedText(item.message, 1000))
    throw new Error('Hosting permission could not be checked.')
  return { allowed: item.allowed, code: item.code as string, message: item.message as string }
}
export function parseLocalHostingStatus(value: unknown): LocalHostingStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Local hosting checks could not be read.')
  const item = value as Record<string, unknown>
  if (item.needsPortApproval !== undefined || item.selectedApprovedPortId !== undefined || item.approvedPorts !== undefined) {
    const ports = item.approvedPorts === null && item.allowed === false && item.game === null ? [] : parseApprovedGamePorts(item.approvedPorts)
    if (typeof item.needsPortApproval !== 'boolean' || item.selectedApprovedPortId !== null &&
      (typeof item.selectedApprovedPortId !== 'string' || !guidPattern.test(item.selectedApprovedPortId)) ||
      ports.some(plan => plan.game !== item.game) || item.selectedApprovedPortId !== null &&
        !ports.some(plan => plan.id === item.selectedApprovedPortId) || item.readyForManualStart === true &&
        (item.needsPortApproval || !item.selectedApprovedPortId)) throw new Error('Choose this PC’s approved game ports before starting.')
  }
  if (item.factorioRconPort !== undefined && item.factorioRconPort !== null &&
    (item.game !== 'Factorio' || typeof item.factorioRconPort !== 'number' || !Number.isInteger(item.factorioRconPort) ||
      item.factorioRconPort < 1024 || item.factorioRconPort > 65535 || item.factorioRconPort === item.gamePort))
    throw new Error('The private RCON port could not be checked.')
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
const checkIcons = { permission: 'shield', server: 'server', save: 'archive', ports: 'plug', connection: 'users' } as const
const hostingActions = {
  checks: { label: 'Checking this PC...', message: 'Checking hosting access and this PC. This check does not start the server.' },
  prepare: { label: 'Preparing this PC...', message: 'Getting the latest completed save and checking this PC before Start. Keep TogetherServer open.' },
  accept: { label: 'Accepting move...', message: 'Accepting the hosting move and checking what this PC needs next.' },
  decline: { label: 'Declining move...', message: 'Declining this hosting move. Existing saves are kept.' },
  start: { label: 'Requesting Start...', message: 'Requesting Start for the checked local save. Wait for the result before trying again.' },
  shareStoppedProgress: { label: 'Sharing stopped progress...', message: 'Checking and sharing the last stopped save. Existing backups are kept. This action does not start the server.' },
  browse: { label: 'Choosing server file...', message: 'Choose the installed dedicated server file in the file picker. Choosing a file does not start the server.' }
} as const
type HostingAction = keyof typeof hostingActions

function hostingRequestError(cause: unknown): string {
  if (cause instanceof ApiError && ['HostUnavailable', 'HostUnreachable', 'ConnectionClosed'].includes(cause.code))
    return 'The Host could not be reached. Keep TogetherServer open on both PCs, then retry the checks.'
  return 'The hosting request could not be checked. Keep TogetherServer open, then retry the checks before continuing.'
}

const stoppedSharingFailures: Record<string, string> = {
  StoppedProgressProofRequired: 'The stopped save could not be matched to its latest completed backup. Existing worlds and backups were kept.',
  StoppedProgressSetupChanged: 'The stopped server setup changed. Review the save and setup before sharing.',
  StoppedProgressBusy: 'Keep this server stopped and finish other world operations before sharing its stopped save.',
  StoppedProgressSharingChanged: 'Current sharing permission or the exact signed history needs review. Existing saves were kept.',
  StoppedProgressPublicationFailed: 'The verified stopped backup remains local. Sharing was not confirmed; review and retry this explicit action.'
}

function checkFingerprint(status: LocalHostingStatus): string {
  return JSON.stringify([status.code, status.message, status.allowed, status.consented, status.game, status.gameVersion,
    status.needsServerFile, status.needsPassword, status.serverFile, status.gamePort, status.controlPort,
    status.needsPortApproval, status.selectedApprovedPortId, status.approvedPorts, status.factorioRconPort,
    status.localProfileId, status.branchHash, status.readyForManualStart, status.running,
    status.steps.map(step => [step.key, step.state, step.message]), status.remedy,
    status.handoffRequest?.requestId, status.handoffRequest?.recipientState, status.handoffRequest?.finalVersion,
    status.handoffRequest?.receiptConfirmed])
}

type CheckAttempt = { title: string; summary: string; finishedAt: number }
type HostingAttempt = { state: 'Completed' | 'Failed'; message: string; phase: string | null; finishedAt: number;
  receivedBytes?: number; totalBytes?: number; completedFiles?: number; totalFiles?: number; status: LocalHostingStatus | null;
  action?: 'start' | 'shareStoppedProgress'; phaseTimings?: HostingProgress['phaseTimings'] }
type StartAttempt = { scope: string; localProfileId: string; branchHash: string | null; requestId: string | null;
  began: number; startedUtc: string | null; latest: HostingProgress | null }
type ProgressWatch = { sequence: number; clear: () => void; expire: (reason?: string) => void; observe: (progress: HostingProgress) => void }
const hostingPhaseSteps: Record<string, CheckKey> = {
  Permission: 'permission', PeerSync: 'save', LatestSave: 'save', Preparing: 'save', Receiving: 'save', Verifying: 'save', Receipt: 'save', PreparingCopy: 'save',
  ServerSetup: 'server', Ports: 'ports', Connection: 'connection'
}

function failedAttemptGuideStep(attempt: HostingAttempt): CheckKey | undefined {
  const result = attempt.status
  if (!result) return attempt.phase && Object.hasOwn(hostingPhaseSteps, attempt.phase) ? hostingPhaseSteps[attempt.phase] : undefined
  if (!result.allowed || localHostingAccessBlocker(result)) return 'permission'
  const codeSteps: Record<string, CheckKey> = {
    HostingHistoryChoiceRequired: 'save', WorldHistoryReviewRequired: 'save', SharedSaveHistoryNeedsReview: 'save',
    LocalStoppedProgressNeedsSharing: 'save',
    InstalledServerRequired: 'server', LocalServerSetupRequired: 'server', MinecraftTermsRequired: 'server',
    LocalConnectionSetupRequired: 'connection', RecipientConnectionRequired: 'connection', PeerDiscoveryRequired: 'connection',
    PortConflict: 'ports', PortInUse: 'ports', GamePortInUse: 'ports', LocalPortsRequired: 'ports'
  }
  if (Object.hasOwn(codeSteps, result.code)) return codeSteps[result.code]
  const remedySteps: Record<HostingRemedyKind, CheckKey> = {
    Permission: 'permission', Respond: 'save', ChooseHistory: 'save', Terms: 'server', ChooseServer: 'server',
    GameSetup: 'server', Ports: 'ports', Connection: 'connection', Review: 'save', ConfirmCopy: 'save', GetSave: 'save', ShareStoppedProgress: 'save'
  }
  if (result.remedy && result.remedy.kind !== 'Review') return remedySteps[result.remedy.kind]
  return result.steps.find(step => step.state === 'ActionRequired')?.key ??
    (result.remedy?.kind === 'Review' ? 'save' : result.steps.find(step => step.state !== 'Ready')?.key)
}

function hostingCheckResult(status: LocalHostingStatus): string {
  const blocker = localHostingAccessBlocker(status)
  if (blocker === 'history') return 'Check finished: sharing history needs review. The other checks have not run. Follow the review steps below, then check again.'
  if (blocker === 'permission') return 'Check finished: this PC needs hosting access from the owner. The other checks have not run. Follow the permission steps below, then check again.'
  if (blocker) return 'Check finished: current hosting access could not be verified. The other checks have not run. Keep an approved Host reachable and check access again.'
  if (status.running) return 'Check finished: this server is already hosted on this PC. Open the local server to see its status.'
  if (needsStoppedProgressSharing(status)) return 'Check finished: the last stopped save still needs sharing. Choose Share stopped progress to check and share it. This does not start the server.'
  if (status.readyForManualStart) return 'Check finished: this PC is prepared. Choose Start hosting when you want to start. A real Friend connection and game join still need separate testing.'
  const next = status.steps.find(step => step.state !== 'Ready')
  return next ? `Check finished. Next: ${checkLabels[next.key]}. Follow the setup steps below, then choose Host locally when you are ready.` :
    'Check finished. Review the network confirmation, then choose Host locally when you are ready.'
}

function checkedAgo(checkedAt: number, now: number) {
  const seconds = Math.max(0, Math.floor((now - checkedAt) / 1000))
  return seconds < 60 ? `${seconds}s ago` : `${Math.floor(seconds / 60)}m ago`
}

// These are fixed local actions. A peer's message never becomes a URL or a
// navigation destination. Older status responses retain the same safe stops.
export function localHostingAttention(status: LocalHostingStatus | null, acceptedTerms = false, selectedFile = ''): LocalHostingStatus['remedy'] {
  if (!status) return { kind: 'Review', title: 'Retry the hosting checks', message: 'This PC could not check hosting. Retry before starting.' }
  if (status.running) return null
  if (['HostingHistoryChoiceRequired', 'WorldHistoryReviewRequired'].includes(status.code) || status.remedy?.kind === 'ChooseHistory')
    return { kind: 'ChooseHistory', title: 'This server has two different save histories',
      message: 'Ask the owner to resolve the conflicting saves before hosting here. All existing saves are kept.' }
  if (status.code === 'SharedSaveHistoryNeedsReview') return { kind: 'Review', title: 'Review the available shared save', message: status.message }
  if (localHostingAccessBlocker(status) === 'history') return { kind: 'Review', title: 'Review sharing history before hosting', message: status.message }
  if (!status.allowed) return confirmedHostingDenial(status)
    ? { kind: 'Permission', title: 'Ask the owner to allow hosting', message: status.message }
    : { kind: 'Review', title: 'Check hosting again', message: status.message }
  if (!status.consented && status.remedy?.kind === 'ConfirmCopy') return status.remedy
  if (!status.consented && needsStoppedProgressSharing(status)) return { kind: 'ConfirmCopy', title: 'Review current hosting-copy permission',
    message: 'Current hosting-copy permission is not confirmed. Review this PC’s shared-save settings and retry the checks before sharing or starting.' }
  if (needsStoppedProgressSharing(status)) return { kind: 'ShareStoppedProgress', title: 'Share the last stopped save before hosting',
    message: 'The last stopped save is still local. Share stopped progress checks and shares that save within its existing history. Existing backups are kept. Starting is a separate action.' }
  if (status.needsServerFile && !selectedFile.trim())
    return { kind: 'ChooseServer', title: 'Install or find the game server', message: 'This PC needs the matching dedicated server. Choose its installed file or open game setup.' }
  if (['MinecraftJava', 'MinecraftBedrock'].includes(status.game ?? '') && !acceptedTerms && !status.readyForManualStart && !status.localProfileId)
    return { kind: 'Terms', title: 'Review Minecraft’s terms', message: 'Read the terms and choose whether to accept them on this PC before continuing.' }
  if (['LocalConnectionSetupRequired', 'RecipientConnectionRequired', 'PeerDiscoveryRequired'].includes(status.code))
    return { kind: 'Connection', title: 'Let friends connect to this PC', message: status.message }
  if (status.needsPortApproval || status.code === 'LocalPortApprovalRequired')
    return { kind: 'Ports', title: 'Choose this PC’s game ports', message: 'Choose an approved game-port plan or approve a new choice below before hosting.' }
  if (['PortConflict', 'PortInUse', 'GamePortInUse', 'LocalPortsRequired', 'LocalPortChangeRequired'].includes(status.code))
    return { kind: 'Ports', title: 'Free the game ports on this PC', message: status.message }
  if (['PeerMembershipRequired', 'HostingPermissionChanged', 'HostingRequestChanged', 'SeparateCopyReviewRequired', 'SuccessorChecksPending', 'DataRecoveryRequired'].includes(status.code))
    return { kind: 'Review', title: 'Review this PC’s shared save', message: status.message }
  if (['InvalidManifest', 'InvalidSave', 'SharedSaveInvalid', 'SharedSaveHashMismatch', 'HashMismatch', 'ChunkHashMismatch'].includes(status.code))
    return { kind: 'Review', title: 'Review the save verification problem', message: status.message }
  if (status.remedy?.kind === 'GetSave' || ['SharedSaveRequired', 'SharedSaveUnavailable', 'NoPublishedSave',
    'SharedVersionUnavailable', 'SharedChunkUnavailable', 'WaitingForSuccessorCopy'].includes(status.code))
    return { kind: 'GetSave', title: 'Get a completed verified save', message: status.message }
  if (status.remedy && !['ConfirmCopy', 'ChooseServer', 'Terms', 'Respond'].includes(status.remedy.kind)) return status.remedy
  if (status.code === 'LocalServerSetupRequired') return { kind: 'GameSetup', title: 'Finish game setup', message: status.message }
  if (status.steps[3].state === 'ActionRequired') return { kind: 'Ports', title: 'Free the game ports on this PC', message: status.steps[3].message }
  if (status.steps[4].state === 'ActionRequired' && !(status.readyForManualStart && status.branchHash && !activeHostingRequest(status)))
    return { kind: 'Connection', title: 'Let friends connect to this PC', message: status.steps[4].message }
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
  const resultsId = useId()
  const scope = JSON.stringify([connectionId, profileId])
  const currentScope = useRef(scope)
  currentScope.current = scope
  const request = useRef(0)
  const mutation = useRef(false)
  const preparationWatch = useRef<ProgressWatch | null>(null)
  const startWatch = useRef<ProgressWatch | null>(null)
  const [unconfirmedStart, setUnconfirmedStart] = useState<StartAttempt | null>(null)
  const unconfirmedStartRef = useRef<StartAttempt | null>(null)
  const observePreparationProgress = useCallback((progress: HostingProgress) => {
    if (currentScope.current !== scope) return
    preparationWatch.current?.observe(progress)
    startWatch.current?.observe(progress)
    const attempt = unconfirmedStartRef.current
    if (attempt?.scope !== scope || !attempt.startedUtc || progress.startedUtc !== attempt.startedUtc ||
      attempt.latest && (Date.parse(progress.updatedUtc) < Date.parse(attempt.latest.updatedUtc) ||
        attempt.latest.state !== 'Working' && progress.state === 'Working')) return
    if (attempt.latest && JSON.stringify(progress) === JSON.stringify(attempt.latest)) return
    const next = { ...attempt, latest: progress }
    unconfirmedStartRef.current = next; setUnconfirmedStart(next)
  }, [scope])
  const [scopedPermission, setPermission] = useState<(LocalHostingPermission & { scope: string }) | null>(null)
  const permission = scopedPermission?.scope === scope ? scopedPermission : null
  const transfer = useFriendSaveTransfer({ endpoint: base, scope,
    enabled: available && !accessBlocked && permission?.allowed === true })
  const canHostAfterTransfer = transfer.canHost
  const [permissionCheck, setPermissionCheck] = useState<{ scope: string; pending: boolean; message: string } | null>(null)
  const [permissionRetry, setPermissionRetry] = useState(0)
  const explicitPermissionCheck = useRef(false)
  const [status, setStatus] = useState<LocalHostingStatus | null>(null)
  const ports = useLocalHostingPorts(status, scope)
  const [checksCurrent, setChecksCurrent] = useState(false)
  const [checking, setChecking] = useState(false)
  const [lastCheckedAt, setLastCheckedAt] = useState<number | null>(null)
  const [lastCheckAttempt, setLastCheckAttempt] = useState<CheckAttempt | null>(null)
  const [lastHostingAttempt, setLastHostingAttempt] = useState<HostingAttempt | null>(null)
  const [stoppedSharingStatus, setStoppedSharingStatus] = useState<LocalHostingStatus | null>(null)
  const sharingTimeout = useRef<number | undefined>(undefined)
  const recordProgressResult = useCallback((result: HostingProgress) => {
    const state = result.state
    if (state === 'Working') return
    setLastHostingAttempt(previous => previous?.action || previous?.status ? { ...previous, phaseTimings: result.phaseTimings ?? previous.phaseTimings } : { state, phase: result.phase, message: result.message,
      finishedAt: Date.parse(result.updatedUtc), receivedBytes: result.receivedBytes, totalBytes: result.totalBytes,
      completedFiles: result.completedFiles, totalFiles: result.totalFiles, phaseTimings: result.phaseTimings, status: null })
  }, [])
  const [now, setNow] = useState(Date.now)
  const [guideStep, setGuideStep] = useState<{ key: CheckKey; request: number } | null>(null)
  const [open, setOpen] = useState(false)
  const dialogOpen = useRef(open)
  dialogOpen.current = open
  const serverField = useRef<HTMLInputElement>(null)
  const [busy, setBusy] = useState(false)
  const [pendingAction, setPendingAction] = useState<HostingAction | null>(null)
  const [actionStartedAt, setActionStartedAt] = useState(Date.now)
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
  const acceptStatus = useCallback((next: LocalHostingStatus, preparedHere = false, checked = false) => {
    if (checked) setLastCheckAttempt({
      title: !currentStatus.current ? 'First check finished' : checkFingerprint(currentStatus.current) === checkFingerprint(next)
        ? 'Refresh finished: results unchanged' : 'Refresh finished: results updated',
      summary: hostingCheckResult(next), finishedAt: Date.now()
    })
    if (!next.allowed || !next.consented || next.running || next.remedy &&
      ['Connection', 'Ports', 'Review', 'ChooseHistory', 'ChooseServer', 'GameSetup', 'Terms', 'GetSave', 'ShareStoppedProgress'].includes(next.remedy.kind) ||
      !preparedHere && currentStatus.current?.branchHash && currentStatus.current.branchHash !== next.branchHash) startIntent.current = null
    if (needsStoppedProgressSharing(next)) {
      setStoppedSharingStatus(next)
      startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null)
    }
    if (activeHostingRequest(currentStatus.current)?.requestId !== activeHostingRequest(next)?.requestId) {
      setAcceptedTerms(false); setPassword('')
      plannedAuthorization.current = null; setPlannedOperation(null); plannedAttempt.current = ''
      startIntent.current = null
    }
    currentStatus.current = next
    setPermission({ allowed: next.allowed, code: next.code, message: next.message, scope })
    setStatus(next)
    setChecksCurrent(true); setLastCheckedAt(Date.now()); setNow(Date.now())
  }, [scope])
  const reconcileStart = useCallback((next: LocalHostingStatus, observed: StartAttempt | null) => {
    const pending = unconfirmedStartRef.current
    const terminal = observed?.latest
    if (!observed?.startedUtc || !terminal || terminal.state === 'Working' ||
      pending?.scope !== scope || pending.began !== observed.began || pending.startedUtc !== observed.startedUtc ||
      next.localProfileId !== observed.localProfileId || next.branchHash !== observed.branchHash ||
      (activeHostingRequest(next)?.requestId ?? null) !== observed.requestId) return
    unconfirmedStartRef.current = null; setUnconfirmedStart(null); setError('')
    const state = terminal.state
    setLastHostingAttempt({ state, action: 'start', message: terminal.message, phase: terminal.phase, finishedAt: Date.now(), status: next,
      receivedBytes: terminal.receivedBytes, totalBytes: terminal.totalBytes, completedFiles: terminal.completedFiles, totalFiles: terminal.totalFiles, phaseTimings: terminal.phaseTimings })
    setActionMessage(next.running ? 'The earlier Start finished and this server is hosted on this PC. Open the local server to check its current state.' :
      'The earlier Start finished. Current hosting checks were read again. Starting requires a separate explicit choice.')
  }, [scope])
  const controller = useRef<AbortController | null>(null)
  const invalidate = useCallback(() => { ++request.current; preparationWatch.current?.clear(); startWatch.current?.clear(); window.clearTimeout(sharingTimeout.current); controller.current?.abort() }, [])

  useEffect(() => {
    mutation.current = false
    dialogOpen.current = false
    currentStatus.current = null
    startIntent.current = null
    setPermission(null)
    explicitPermissionCheck.current = false
    setStatus(null); setOpen(false); setBusy(false); setPendingAction(null); setError(''); setAcceptedTerms(false)
    setChecksCurrent(false); setChecking(false); setLastCheckedAt(null); setLastCheckAttempt(null); setLastHostingAttempt(null); setGuideStep(null)
    setStoppedSharingStatus(null)
    unconfirmedStartRef.current = null; setUnconfirmedStart(null)
    setServerFile(''); setPassword(''); setActionMessage('')
    setNetworkConfirmed(false)
    setPlannedOperation(null); plannedAttempt.current = ''
    return invalidate
  }, [scope, base, available, accessBlocked, invalidate])

  useEffect(() => {
    if (!available || accessBlocked) return
    let active = true
    let timer: number | undefined
    let currentRead: AbortController | null = null
    let timeout: number | undefined
    const read = async () => {
      if (!mutation.current && !dialogOpen.current) {
        const abort = new AbortController()
        currentRead = abort
        setPermissionCheck({ scope, pending: true, message: '' })
        timeout = window.setTimeout(() => {
          if (active) {
            setPermission(null)
            setPermissionCheck({ scope, pending: false,
              message: 'Hosting access check timed out. Keep TogetherServer open on both PCs, then check again.' })
            setChecksCurrent(false)
            startIntent.current = null
          }
          abort.abort()
        }, 15000)
        try {
          const explicit = explicitPermissionCheck.current
          explicitPermissionCheck.current = false
          const next = explicit
            ? await changeJson(`${base}/hosting/access/check`, 'POST', parseLocalHostingPermission, {}, abort.signal)
            : await getLocalJson(`${base}/hosting/access`, parseLocalHostingPermission, abort.signal)
          if (active && !abort.signal.aborted) {
            setPermission({ ...next, scope })
            setPermissionCheck({ scope, pending: false, message: '' })
            if (!next.allowed) {
              startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null)
              startWatch.current?.expire('Hosting access changed before the Start result was confirmed.')
              invalidate(); mutation.current = false; setBusy(false); setPendingAction(null); setChecking(false); setChecksCurrent(false)
              if (dialogOpen.current) setError(next.message)
            }
          }
        } catch {
          // Wait for confirmed hosting access before offering the entry point.
          if (active && !abort.signal.aborted) {
            setPermission(null)
            setPermissionCheck({ scope, pending: false,
              message: 'Hosting access could not be checked. Keep TogetherServer open on both PCs, then check again.' })
            setChecksCurrent(false)
            startIntent.current = null
          }
        } finally { window.clearTimeout(timeout) }
      }
      if (active) timer = window.setTimeout(() => void read(), 10000)
    }
    void read()
    return () => { active = false; currentRead?.abort(); window.clearTimeout(timer); window.clearTimeout(timeout) }
  }, [scope, base, available, accessBlocked, invalidate, permissionRetry])

  useEffect(() => {
    if (!open || !available || accessBlocked) return
    let active = true
    let timer: number | undefined
    let currentRead: AbortController | null = null
    let timeout: number | undefined
    const read = async () => {
      if (!mutation.current) {
        const sequence = ++request.current
        const abort = new AbortController()
        currentRead = abort
        setActionStartedAt(Date.now()); setChecking(true); setChecksCurrent(false)
        timeout = window.setTimeout(() => {
          if (active && sequence === request.current) {
            setChecksCurrent(false); setChecking(false); plannedAuthorization.current = null; setPlannedOperation(null)
            startIntent.current = null; ++request.current
            setError('Hosting checks timed out. Retry the checks.')
            setLastCheckAttempt({ title: 'Refresh did not finish', summary: 'The check timed out. Previous results are kept. Keep TogetherServer open on both PCs, then retry the checks before starting.', finishedAt: Date.now() })
          }
          abort.abort()
        }, 15000)
        try {
          const observedStart = unconfirmedStartRef.current
          const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
          if (active && sequence === request.current && !abort.signal.aborted) { acceptStatus(next, false, true); reconcileStart(next, observedStart) }
        } catch (cause) {
          if (active && sequence === request.current && !abort.signal.aborted) {
            setChecksCurrent(false); plannedAuthorization.current = null; setPlannedOperation(null)
            startIntent.current = null; setError(hostingRequestError(cause))
            setLastCheckAttempt({ title: 'Refresh failed', summary: 'The checks could not finish. Previous results are kept. Keep TogetherServer open on both PCs, then retry the checks before starting.', finishedAt: Date.now() })
          }
        } finally {
          window.clearTimeout(timeout)
          if (active && sequence === request.current) setChecking(false)
        }
      }
      if (active) timer = window.setTimeout(() => void read(), 10000)
    }
    timer = window.setTimeout(() => void read(), 10000)
    return () => { active = false; currentRead?.abort(); window.clearTimeout(timer); window.clearTimeout(timeout) }
  }, [scope, base, open, available, accessBlocked, acceptStatus, reconcileStart])

  useEffect(() => {
    if (!open) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [open])

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
    setActionStartedAt(Date.now()); setBusy(true); setPendingAction('checks'); setChecking(true); setChecksCurrent(false); setError(''); setActionMessage('')
    const timeout = window.setTimeout(() => {
      abort.abort()
      if (sequence === request.current) {
        startIntent.current = null; setChecksCurrent(false); setChecking(false); setError('Hosting checks timed out. Retry the checks.')
        setLastCheckAttempt({ title: 'Refresh did not finish', summary: 'The check timed out. Previous results are kept. Keep TogetherServer open on both PCs, then retry the checks before starting.', finishedAt: Date.now() })
        setBusy(false); setPendingAction(null); mutation.current = false; ++request.current
      }
    }, 15000)
    try {
      const observedStart = unconfirmedStartRef.current
      const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
      if (sequence === request.current && !abort.signal.aborted) {
        acceptStatus(next, false, true); setActionMessage(hostingCheckResult(next)); reconcileStart(next, observedStart)
      }
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) {
        setChecksCurrent(false); startIntent.current = null; setError(hostingRequestError(cause))
        setLastCheckAttempt({ title: 'Refresh failed', summary: 'The checks could not finish. Previous results are kept. Keep TogetherServer open on both PCs, then retry the checks before starting.', finishedAt: Date.now() })
        setActionMessage('The checks could not finish. Keep TogetherServer open and retry automatic checks before starting.')
      }
    } finally {
      window.clearTimeout(timeout)
      if (sequence === request.current) { mutation.current = false; setBusy(false); setPendingAction(null); setChecking(false) }
    }
  }
  const prepareStatus = useCallback(async (target: LocalHostingStatus, authorization: typeof plannedOperation) => {
    if (mutation.current || disabled || unconfirmedStartRef.current || !target.allowed || stoppedSharingStatus || needsStoppedProgressSharing(target)
      || !canHostAfterTransfer() || !ports.ready) return
    const planned = activeHostingRequest(target)
    if (planned?.recipientState === 'Requested') return
    if (planned?.requestId && ['MinecraftJava', 'MinecraftBedrock'].includes(target.game ?? '') && !acceptedTerms) return
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    const began = Date.now()
    const overallDeadline = Math.min(began + 10 * 60 * 1000, authorization?.until ?? Infinity, startIntent.current?.until ?? Infinity)
    let lastActivityAt = began
    let lastProgressUtc = -Infinity
    let lastWork = ''
    let phase: string | null = null
    let latestProgress: HostingProgress | null = null
    let timeout: number | undefined
    const clearWatch = () => {
      window.clearTimeout(timeout)
      if (preparationWatch.current?.sequence === sequence) preparationWatch.current = null
    }
    const expire = () => {
      if (sequence !== request.current || abort.signal.aborted || authorization && plannedAuthorization.current !== authorization) { clearWatch(); return }
      clearWatch()
      abort.abort(); ++request.current; mutation.current = false; setBusy(false); setPendingAction(null)
      plannedAuthorization.current = null; setPlannedOperation(null); setChecksCurrent(false)
      startIntent.current = null; if (dialogOpen.current) setOpen(true)
      const reason = Date.now() >= overallDeadline ? overallDeadline < began + 10 * 60 * 1000
        ? 'The current hosting request reached its time limit.' : 'Preparation reached its 10-minute time limit.' :
        'No new preparation progress was reported for 2 minutes.'
      const message = `${reason} Last step: ${phase ? hostingProgressPhaseLabel(phase) : 'Checking this PC'}. Recheck the hosting request before choosing Host locally again. Existing saves were kept.`
      setError(message)
      setLastHostingAttempt({ state: 'Failed', message, phase, finishedAt: Date.now(), receivedBytes: latestProgress?.receivedBytes,
        totalBytes: latestProgress?.totalBytes, completedFiles: latestProgress?.completedFiles, totalFiles: latestProgress?.totalFiles, phaseTimings: latestProgress?.phaseTimings, status: null })
    }
    const schedule = () => {
      window.clearTimeout(timeout)
      timeout = window.setTimeout(expire, Math.max(0, Math.min(lastActivityAt + 120000, overallDeadline) - Date.now()))
    }
    preparationWatch.current = { sequence, clear: clearWatch, expire, observe: progress => {
      if (sequence !== request.current || abort.signal.aborted || progress.state !== 'Working' || Date.parse(progress.startedUtc) < began) return
      const updated = Date.parse(progress.updatedUtc)
      if (updated <= lastProgressUtc) return
      lastProgressUtc = updated; latestProgress = progress
      const work = JSON.stringify([progress.phase, progress.receivedBytes, progress.totalBytes, progress.completedFiles, progress.totalFiles])
      if (work === lastWork) return
      lastWork = work; phase = progress.phase; lastActivityAt = Date.now()
      schedule()
    } }
    schedule()
    setActionStartedAt(began); setBusy(true); setPendingAction('prepare'); setLastHostingAttempt(null); setError(''); setActionMessage('')
    try {
      const next = await changeJson(`${base}/hosting/prepare`, 'POST', parseLocalHostingStatus, {
        allowReceiving: true, acceptSplitWarning: !planned?.requestId, requirePeerConnection: true,
        serverFile: serverFile.trim() || null, gamePassword: password || null,
        ...(ports.selection ?? {}),
        ...(planned?.requestId ? { requestId: planned.requestId, acceptedTerms } :
          ['MinecraftJava', 'MinecraftBedrock'].includes(target.game ?? '') ? { acceptedTerms } : {})
      }, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      if (authorization && plannedAuthorization.current !== authorization) return
      if (planned?.requestId && currentStatus.current?.handoffRequest?.requestId !== planned.requestId) return
      acceptStatus(next, true); setPassword(''); setActionMessage(`${next.message} ${hostingCheckResult(next)}`)
      setLastHostingAttempt(previous => ({ state: next.ok ? 'Completed' : 'Failed', phase: previous?.phase ?? latestProgress?.phase ?? null,
        message: next.message, receivedBytes: previous?.receivedBytes ?? latestProgress?.receivedBytes, totalBytes: previous?.totalBytes ?? latestProgress?.totalBytes,
        completedFiles: previous?.completedFiles ?? latestProgress?.completedFiles, totalFiles: previous?.totalFiles ?? latestProgress?.totalFiles,
        phaseTimings: previous?.phaseTimings ?? latestProgress?.phaseTimings, finishedAt: Date.now(), status: next }))
      if (!next.ok || localHostingAttention(next, acceptedTerms, serverFile)) {
        startIntent.current = null; if (dialogOpen.current) setOpen(true)
      }
      if (authorization && (!next.ok || next.readyForManualStart)) {
        plannedAuthorization.current = null; setPlannedOperation(null)
      }
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) {
        setError(hostingRequestError(cause)); plannedAuthorization.current = null; setPlannedOperation(null)
        setLastHostingAttempt(previous => previous ?? { state: 'Failed', message: hostingRequestError(cause), phase,
          receivedBytes: latestProgress?.receivedBytes, totalBytes: latestProgress?.totalBytes, completedFiles: latestProgress?.completedFiles,
          totalFiles: latestProgress?.totalFiles, phaseTimings: latestProgress?.phaseTimings, finishedAt: Date.now(), status: null })
        startIntent.current = null; setChecksCurrent(false); if (dialogOpen.current) setOpen(true)
      }
    } finally {
      clearWatch()
      if (sequence === request.current) { mutation.current = false; setBusy(false); setPendingAction(null) }
    }
  }, [base, disabled, serverFile, password, acceptedTerms, acceptStatus, stoppedSharingStatus, canHostAfterTransfer, ports.ready, ports.selection])
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
    if (mutation.current || disabled || !canHostAfterTransfer() || !invitation?.requestId || invitation.recipientState !== 'Requested') return
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    setActionStartedAt(Date.now()); setBusy(true); setPendingAction(accept ? 'accept' : 'decline'); setError(''); setActionMessage('')
    const timeout = window.setTimeout(() => {
      if (sequence !== request.current) return
      abort.abort(); ++request.current; mutation.current = false; setBusy(false); setPendingAction(null)
      startIntent.current = null; setChecksCurrent(false)
      setError('The hosting move response could not be checked in time. Retry the checks to see whether it was accepted before responding again.')
    }, 15000)
    try {
      const result = await changeJson(`${base}/handoff/respond`, 'POST', parseBasicResult,
        { requestId: invitation.requestId, accept }, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      if (!result.ok) { setError(result.message); return }
      setActionMessage(result.message)
      const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      acceptStatus(next)
      if (accept && next.handoffRequest?.requestId === invitation.requestId) {
        const authorization = { requestId: invitation.requestId, until: Date.now() + 10 * 60 * 1000 }
        startIntent.current = { scope, until: authorization.until, requestId: invitation.requestId }
        plannedAuthorization.current = authorization; setPlannedOperation(authorization)
      }
    } catch (cause) { if (sequence === request.current && !abort.signal.aborted) setError(hostingRequestError(cause)) }
    finally { window.clearTimeout(timeout); if (sequence === request.current) { mutation.current = false; setBusy(false); setPendingAction(null) } }
  }
  const shareStoppedProgress = async () => {
    const target = currentStatus.current
    if (mutation.current || disabled || unconfirmedStartRef.current || checking || !checksCurrent || !permission?.allowed || !target?.allowed || !target.consented ||
      target.running || localHostingAccessBlocker(target) || !target.localProfileId || !target.branchHash ||
      !stoppedSharingStatus && !needsStoppedProgressSharing(target) ||
      stoppedSharingStatus && (target.localProfileId !== stoppedSharingStatus.localProfileId || target.branchHash !== stoppedSharingStatus.branchHash) ||
      ['HostingHistoryChoiceRequired', 'WorldHistoryReviewRequired'].includes(target.code) || target.remedy?.kind === 'ChooseHistory') return
    startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null); plannedAttempt.current = ''
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    let shared = false
    setActionStartedAt(Date.now()); setBusy(true); setPendingAction('shareStoppedProgress'); setError(''); setActionMessage('')
    setLastHostingAttempt(null); setChecksCurrent(false)
    const failed = (message: string) => {
      setError(message)
      setLastHostingAttempt({ state: 'Failed', action: 'shareStoppedProgress', message, phase: null, finishedAt: Date.now(), status: shared ? null : stoppedSharingStatus ?? target })
    }
    const timeout = window.setTimeout(() => {
      if (sequence !== request.current) return
      abort.abort(); ++request.current; mutation.current = false; setBusy(false); setPendingAction(null); setChecksCurrent(false)
      failed(shared ? 'Stopped progress was shared, but the current hosting checks did not finish within 2 minutes. Retry automatic checks before starting.' :
        'Sharing stopped progress did not return a confirmed result within 2 minutes. Existing backups were kept. Retry automatic checks, then review the stopped save before choosing Share stopped progress again.')
    }, 120000)
    sharingTimeout.current = timeout
    try {
      const result = await changeJson(`${base}/hosting/share-stopped-progress`, 'POST', parseBasicResult,
        { localProfileId: target.localProfileId, branchHash: target.branchHash }, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      if (!result.ok) { failed(result.message); return }
      shared = true; setStoppedSharingStatus(null)
      setLastHostingAttempt({ state: 'Completed', action: 'shareStoppedProgress', message: result.message, phase: null, finishedAt: Date.now(), status: null })
      setActionMessage('Stopped progress is shared. Checking this PC again. Starting still needs a separate Start hosting click.')
      const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
      if (sequence !== request.current || abort.signal.aborted) return
      acceptStatus(next, false, true)
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) failed(cause instanceof ApiError && Object.hasOwn(stoppedSharingFailures, cause.code)
        ? stoppedSharingFailures[cause.code] : shared
        ? 'Stopped progress was shared, but the current hosting checks could not be read. Retry automatic checks before starting.'
        : 'Sharing stopped progress could not be confirmed. Existing backups were kept. Retry automatic checks, then review the stopped save before trying this action again.')
    } finally {
      window.clearTimeout(timeout)
      if (sharingTimeout.current === timeout) sharingTimeout.current = undefined
      if (sequence === request.current) { mutation.current = false; setBusy(false); setPendingAction(null) }
    }
  }
  useEffect(() => {
    if (!open || !plannedOperation || !status?.handoffRequest || mutation.current || disabled || transfer.blocked || !ports.ready) return
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
  }, [status, open, plannedOperation, disabled, serverFile, password, acceptedTerms, prepareStatus, transfer.blocked, ports.ready])
  useEffect(() => {
    if (!plannedOperation) return
    const timeout = window.setTimeout(() => {
      if (preparationWatch.current) { preparationWatch.current.expire(); return }
      if (startWatch.current) { startWatch.current.expire(); return }
      plannedAuthorization.current = null; setPlannedOperation(null)
      startIntent.current = null
      if (mutation.current) { controller.current?.abort(); ++request.current; mutation.current = false; setBusy(false); setPendingAction(null); setChecking(false); setChecksCurrent(false) }
      setActionMessage('Preparation is paused. Choose Continue and start when you want to resume this move.')
    }, Math.max(0, plannedOperation.until - Date.now()))
    return () => window.clearTimeout(timeout)
  }, [plannedOperation])
  const startPrepared = useCallback(async (target: LocalHostingStatus) => {
    const intent = startIntent.current
    if (mutation.current || disabled || stoppedSharingStatus || unconfirmedStartRef.current || !canHostAfterTransfer()
      || !checksCurrent || !intent || intent.scope !== scope || intent.until <= Date.now()
      || !target.allowed || !target.readyForManualStart || target.running || !target.localProfileId
      || intent.requestId !== (activeHostingRequest(target)?.requestId ?? null)
      || localHostingAttention(target, acceptedTerms, serverFile)) return
    startIntent.current = null // consume before the request; no poll/error retry can repeat Start
    mutation.current = true
    const sequence = ++request.current
    const abort = new AbortController()
    controller.current = abort
    const localProfileId = target.localProfileId
    const began = Date.now()
    const overallDeadline = Math.min(began + 10 * 60 * 1000, intent.until, plannedAuthorization.current?.until ?? Infinity)
    let lastActivityAt = began
    let lastProgressUtc = -Infinity
    let lastWork = ''
    let attempt: StartAttempt = { scope, localProfileId, branchHash: target.branchHash,
      requestId: intent.requestId, began, startedUtc: null, latest: null }
    let timeout: number | undefined
    const clearWatch = () => {
      window.clearTimeout(timeout)
      if (startWatch.current?.sequence === sequence) startWatch.current = null
    }
    const unconfirmed = (reason: string) => {
      const latest = attempt.latest
      const message = `${reason} Last step: ${latest ? hostingProgressPhaseLabel(latest.phase) : 'Checking this PC'}. The Start result is unconfirmed. Wait for this attempt's result and current server checks before choosing Start again.`
      unconfirmedStartRef.current = attempt; setUnconfirmedStart(attempt)
      setLastHostingAttempt({ state: 'Failed', action: 'start', phase: latest?.phase ?? null, message, finishedAt: Date.now(), status: null,
        receivedBytes: latest?.receivedBytes, totalBytes: latest?.totalBytes, completedFiles: latest?.completedFiles, totalFiles: latest?.totalFiles, phaseTimings: latest?.phaseTimings })
      setError(message); setChecksCurrent(false)
    }
    const expire = (reason?: string) => {
      if (sequence !== request.current || abort.signal.aborted) { clearWatch(); return }
      clearWatch()
      abort.abort(); ++request.current; mutation.current = false; setBusy(false); setPendingAction(null); setOpen(true); setChecksCurrent(false)
      plannedAuthorization.current = null; setPlannedOperation(null)
      unconfirmed(reason ?? (Date.now() >= overallDeadline ? 'The Start request reached its authorized time limit.' : 'No new Start progress was reported for 2 minutes.'))
    }
    const schedule = () => {
      window.clearTimeout(timeout)
      timeout = window.setTimeout(expire, Math.max(0, Math.min(lastActivityAt + 120000, overallDeadline) - Date.now()))
    }
    startWatch.current = { sequence, clear: clearWatch, expire, observe: progress => {
      if (sequence !== request.current || abort.signal.aborted || Date.parse(progress.startedUtc) < began ||
        attempt.startedUtc !== null && progress.startedUtc !== attempt.startedUtc || Date.parse(progress.updatedUtc) < lastProgressUtc ||
        Date.parse(progress.updatedUtc) === lastProgressUtc && (progress.state === 'Working' || progress.state === attempt.latest?.state) ||
        attempt.latest && attempt.latest.state !== 'Working' && progress.state === 'Working') return
      lastProgressUtc = Date.parse(progress.updatedUtc)
      attempt = { ...attempt, startedUtc: progress.startedUtc, latest: progress }
      if (progress.state !== 'Working') return
      const work = JSON.stringify([progress.phase, progress.receivedBytes, progress.totalBytes, progress.completedFiles, progress.totalFiles])
      if (work === lastWork) return
      lastWork = work; lastActivityAt = Date.now(); schedule()
    } }
    schedule()
    setActionStartedAt(began); setBusy(true); setPendingAction('start'); setLastHostingAttempt(null); setError(''); setActionMessage('')
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
        setLastHostingAttempt(previous => ({ ...previous, state: 'Failed', action: 'start', message: result.message,
          phase: previous?.phase ?? null, finishedAt: Date.now(), status: null }))
        const next = await getLocalJson(`${base}/hosting`, parseLocalHostingStatus, abort.signal)
        if (sequence === request.current && !abort.signal.aborted) {
          const failure = needsStoppedProgressSharing(next) ? next : { ...next, ok: false,
            code: result.code, message: result.message, readyForManualStart: false, remedy: null }
          acceptStatus(failure)
          setLastHostingAttempt(previous => ({ ...previous, state: 'Failed', action: 'start', message: result.message,
            phase: previous?.phase ?? null, finishedAt: Date.now(), status: failure }))
        }
      }
    } catch (cause) {
      if (sequence === request.current && !abort.signal.aborted) unconfirmed(hostingRequestError(cause))
      if (sequence === request.current) setOpen(true)
    } finally {
      clearWatch()
      if (sequence === request.current) { mutation.current = false; setBusy(false); setPendingAction(null) }
    }
  }, [base, scope, disabled, checksCurrent, acceptedTerms, serverFile, onOpenHost, acceptStatus, stoppedSharingStatus, canHostAfterTransfer])
  useEffect(() => {
    if (busy || checking || disabled || transfer.blocked || !checksCurrent || !status?.readyForManualStart || !startIntent.current) return
    if (localHostingAttention(status, acceptedTerms, serverFile)) { startIntent.current = null; setOpen(true); return }
    void startPrepared(status)
  }, [status, busy, checking, disabled, checksCurrent, acceptedTerms, serverFile, startPrepared, transfer.blocked])

  const host = () => {
    if (mutation.current || disabled || unconfirmedStartRef.current || !canHostAfterTransfer() || !ports.ready || !checksCurrent || checking || !status?.allowed) return
    if (status.running && status.localProfileId) { onOpenHost(status.localProfileId); return }
    setError(''); setActionMessage('')
    const attention = localHostingAttention(status, acceptedTerms, serverFile)
    if (stoppedSharingStatus || attention && ['Permission', 'ChooseHistory', 'ShareStoppedProgress'].includes(attention.kind)) return
    if (status.readyForManualStart && attention) { startIntent.current = null; setOpen(true); return }
    // The dialog's confirmation authorizes this bounded preparation and Start.
    if (attention && !open) { setOpen(true); return }
    startIntent.current = { scope, until: Date.now() + 10 * 60 * 1000,
      requestId: activeHostingRequest(status)?.requestId ?? null }
    if (status.readyForManualStart) { void startPrepared(status); return }
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
    if (!status?.game || !onBrowseServerFile || mutation.current || checking || disabled) return
    mutation.current = true
    const sequence = ++request.current
    setError(''); setActionMessage(''); setActionStartedAt(Date.now()); setBusy(true); setPendingAction('browse')
    try {
      const selected = await onBrowseServerFile(status.game)
      if (sequence === request.current) {
        if (selected) setServerFile(selected)
        setActionMessage(selected ? 'Server file selected. Choose Host locally to verify it with this save and continue.' :
          'No server file was selected. Your previous choice is kept. Choose the installed server file when you are ready.')
      }
    } catch (cause) { if (sequence === request.current) setError(hostingRequestError(cause)) }
    finally { if (sequence === request.current) { mutation.current = false; setBusy(false); setPendingAction(null) } }
  }

  if (!available || accessBlocked) return null
  const allowed = status?.allowed === true
  const handoff = activeHostingRequest(status)
  const requestOffered = handoff?.recipientState === 'Requested'
  const waitingForHost = handoff?.recipientState === 'Prepared' && !status?.readyForManualStart
  const minecraftTerms = ['MinecraftJava', 'MinecraftBedrock'].includes(status?.game ?? '') && !status?.readyForManualStart && !status?.localProfileId
  const permissionAllowed = permission?.allowed === true
  const permissionChecking = !permissionCheck || permissionCheck.scope !== scope || permissionCheck.pending
  const denied = confirmedHostingDenial(permission)
  const checkedGuidance = checksCurrent || permission?.allowed !== false ? status : status && permission?.allowed === false ? {
    ...status, allowed: false, readyForManualStart: false, code: permission.code, message: permission.message,
    steps: status.steps.map(step => ({ ...step, state: step.key === 'permission' && denied ? 'ActionRequired' as const : 'Unknown' as const,
      message: step.key === 'permission' ? permission.message : step.message }))
  } : null
  const currentAccessBlocker = localHostingAccessBlocker(checkedGuidance)
  const currentHistoryChoice = checkedGuidance && (['HostingHistoryChoiceRequired', 'WorldHistoryReviewRequired'].includes(checkedGuidance.code)
    || checkedGuidance.remedy?.kind === 'ChooseHistory')
  const priorityStep: CheckKey | undefined = currentAccessBlocker ? 'permission' : currentHistoryChoice ? 'save' : undefined
  const pendingStartResult = unconfirmedStart?.scope === scope ? unconfirmedStart : null
  const startUnconfirmed = pendingStartResult !== null
  const startResultMessage = !pendingStartResult?.startedUtc
    ? 'The earlier Start result is unconfirmed and no matching attempt progress was received. Open the local server details to review it. A prepared check alone cannot confirm that the earlier Start finished.'
    : pendingStartResult.latest && pendingStartResult.latest.state !== 'Working'
    ? 'The earlier Start reported a finished step. Its exact result must be checked against this PC’s current server status before another Start.'
    : 'The earlier Start result is unconfirmed. Read-only checks do not retry Start. Wait for that exact attempt to finish and check the local server before choosing Start again.'
  const sharingRequired = status?.allowed && status.consented && permissionAllowed && !status.running && !priorityStep
    && !!(stoppedSharingStatus || needsStoppedProgressSharing(status))
  const sharingConsentChanged = !!stoppedSharingStatus && status?.consented === false
  const sharingIdentityChanged = sharingRequired && stoppedSharingStatus &&
    (stoppedSharingStatus.localProfileId !== status?.localProfileId || stoppedSharingStatus.branchHash !== status?.branchHash)
  const sharingIdentityMessage = 'This PC’s selected hosting copy changed after the stopped-save result. Retry automatic checks and review the selected copy before sharing. No sharing action was requested for the changed copy.'
  // A read-only refresh cannot erase the outcome of an explicit preparation.
  // Keep its exact repair steps until another preparation replaces that result.
  const guidanceStatus = sharingConsentChanged ? checkedGuidance : sharingRequired ? stoppedSharingStatus ?? checkedGuidance :
    !priorityStep && !checkedGuidance?.running && !checkedGuidance?.readyForManualStart && lastHostingAttempt?.state === 'Failed'
      && lastHostingAttempt.status?.allowed ? lastHostingAttempt.status : checkedGuidance
  const recommendedStep = priorityStep ?? (lastHostingAttempt?.state === 'Failed' &&
    (!lastHostingAttempt.status || guidanceStatus === lastHostingAttempt.status) ? failedAttemptGuideStep(lastHostingAttempt) : undefined)
  const attention = (!status || !checksCurrent) && permission?.allowed === false
    ? { kind: denied ? 'Permission' as const : 'Review' as const,
      title: denied ? 'Ask the owner to allow hosting' : 'Check hosting on this PC', message: permission.message }
    : startUnconfirmed && !priorityStep ? { kind: 'Review' as const, title: 'Check the earlier Start result', message: startResultMessage }
      : sharingIdentityChanged ? { kind: 'Review' as const, title: 'Recheck the selected hosting copy', message: sharingIdentityMessage }
      : localHostingAttention(sharingRequired ? stoppedSharingStatus ?? status : status, acceptedTerms, serverFile)
  const historyChoice = attention?.kind === 'ChooseHistory'
  const accessBlocker = localHostingAccessBlocker(checkedGuidance)
  const displayedChecks = ((checkedGuidance ?? status)?.steps ?? stepKeys.map(key => ({ key, state: 'Unknown' as const,
    message: checking ? 'Checking this PC…' : 'This check has not finished. Retry to check it.' }))).map(step => {
      const waiting = step.key !== 'permission' && accessBlocker !== null
      const needsPreparation = !waiting && step.state === 'Unknown' && step.key === 'save' && !status?.localProfileId
      const needsPortPreparation = !waiting && step.state === 'Unknown' && step.key === 'ports' && !status?.localProfileId
      const nextAction = waiting ? 'Open the permission steps below, resolve the access or sharing-history check, then retry.' :
        step.key === 'permission' ? step.state === 'Ready' ? 'Continue to the server app and save checks.' : 'Open the permission steps below and check current access with the owner.' :
        step.key === 'server' ? step.state === 'Ready' ? 'Review matching game versions and add-ons before hosting.' : 'Choose the installed server file or follow the server app setup steps below.' :
        step.key === 'save' ? sharingRequired ? 'Keep this server stopped. Review the stopped save, then choose Share stopped progress. Starting remains a separate action.' : step.state === 'Ready' ? 'Choose Start hosting when you are ready. The save is checked again before Start.' : 'Keep an approved source PC reachable, review the save steps below, then choose Host locally to prepare and verify the copy.' :
        step.key === 'ports' ? step.state === 'Ready' ? 'Start repeats the local port checks.' : needsPortPreparation ? 'Prepare the hosting copy with Host locally so its game ports can be checked.' : 'Review the port steps below. Use normal server controls or choose a free port in game setup, then retry.' :
        'Open Connection help on this PC, then test with an approved Friend on the intended network. A game join is a separate check.'
      return { ...step, waiting: waiting || needsPreparation || needsPortPreparation, nextAction,
        checking: checking && !status, ...(waiting ? { state: 'Unknown' as const, message: accessBlocker === 'history'
        ? 'Waiting for sharing history review. This check has not run in this attempt.'
        : 'Waiting for hosting access to be verified. This check has not run in this attempt.' } : needsPreparation ? {
          message: 'The hosting copy has not been prepared. Receiving and file verification run after you choose Host locally.'
        } : needsPortPreparation ? { message: 'The local port check is waiting for this PC’s hosting copy and game setup.' } :
          sharingRequired && step.key === 'save' ? { state: 'ActionRequired' as const, message: stoppedSharingStatus?.message ?? status?.message ?? step.message } : {}) }
    })
  const actionFeedback = busy && pendingAction ? hostingActions[pendingAction].message : checking
    ? 'Updating the hosting checks. Wait for the current result before starting.' : startUnconfirmed && !priorityStep ? startResultMessage : sharingIdentityChanged ? sharingIdentityMessage : sharingRequired && guidanceStatus
      ? hostingCheckResult(guidanceStatus) : actionMessage
  const pause = () => {
    if (busy && pendingAction === 'prepare') {
      // Preparation belongs to the backend. Closing this view cancels the local
      // Start intent, while the save job continues and remains in Background jobs.
      startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null)
      setNetworkConfirmed(false); dialogOpen.current = false; setOpen(false)
      return
    }
    if (pendingAction === 'shareStoppedProgress') setLastHostingAttempt(previous => previous?.action === 'shareStoppedProgress' && previous.state === 'Completed' ? previous : { state: 'Failed', action: 'shareStoppedProgress', phase: null,
      message: 'Sharing stopped progress was paused before its result was confirmed. Retry automatic checks and review the stopped save before trying again. Existing backups were kept.',
      finishedAt: Date.now(), status: stoppedSharingStatus })
    startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null)
    setNetworkConfirmed(false)
    invalidate(); mutation.current = false; setBusy(false); setPendingAction(null); setChecking(false); setOpen(false)
  }
  const connectionHelp = () => {
    if (!onOpenConnectionHelp) return
    pause(); onOpenConnectionHelp(status?.localProfileId ?? profileId)
  }
  const gameSetup = () => {
    if (!onOpenGameSetup || !status?.game) return
    pause(); onOpenGameSetup(status.game, status.localProfileId ?? undefined)
  }
  const trigger = () => {
    if (disabled || busy || !permissionAllowed || !canHostAfterTransfer()) return
    if (status?.running && status.localProfileId) { onOpenHost(status.localProfileId); return }
    setOpen(true); setNetworkConfirmed(false); setError(''); setActionMessage('')
    void refresh()
  }
  const canContinue = permissionAllowed && allowed && checksCurrent && !checking && !busy && !disabled && !transfer.blocked && ports.ready && !historyChoice
    && !(minecraftTerms && !acceptedTerms) && !(status?.needsServerFile && !serverFile.trim())
    && !(status?.needsPassword && !password) && attention?.kind !== 'Permission'
    && !(status?.readyForManualStart && attention) && !startUnconfirmed && !sharingRequired && !(stoppedSharingStatus && !sharingRequired)
  const canShareStoppedProgress = sharingRequired && permissionAllowed && allowed && checksCurrent && !checking && !busy && !disabled
    && !startUnconfirmed && !sharingIdentityChanged && !!status?.consented && !!status.localProfileId && !!status.branchHash
  return <>
    {!permissionAllowed && !open && <div className={presentation === 'card' ? 'local-hosting-card' : 'local-hosting-next'}>
      <div className="local-hosting-next" role="status" aria-label="Hosting access" aria-busy={permissionChecking}>
        <p className="helper-text">{permissionChecking ? 'Checking hosting access...' : permissionCheck?.message || permission?.message ||
          'Hosting access has not been verified. Keep TogetherServer open on both PCs, then check again.'}</p>
      </div>
      {!permissionChecking && !denied && <Button className="text-button" disabled={disabled || busy}
        onClick={() => {
          if (disabled || busy || mutation.current || dialogOpen.current) return
          startIntent.current = null; setPermission(null)
          explicitPermissionCheck.current = true
          setPermissionCheck({ scope, pending: true, message: '' })
          setPermissionRetry(previous => previous + 1)
        }}><Icon name="refresh" />Check hosting access again</Button>}
    </div>}
    {permissionAllowed && (presentation === 'card' ? <section className="local-hosting-card" aria-label="Host locally">
      <Icon name="server" size={24} /><div><h3>Host locally</h3>
        <p>{status?.running ? 'This server is already hosted on this PC.' : 'Use the latest available save to host this server on your PC.'}</p></div>
      <Button className="secondary" disabled={disabled || busy || transfer.blocked} onClick={trigger}><Icon name={busy ? 'loader' : 'server'} />Host locally</Button>
    </section> : <Button className="secondary server-play-action local-hosting-trigger" title="Host locally"
      disabled={disabled || busy || transfer.blocked} onClick={trigger}><Icon name={busy ? 'loader' : 'server'} />Host locally</Button>)}
    {permissionAllowed && transfer.blocked && !open && <FriendSaveTransferProgress status={transfer.status}
      fresh={transfer.fresh} unavailable={transfer.unavailable} />}
    {open && <SettingsDialog title="Host locally" description={worldName} busy={busy} allowBusyClose={pendingAction === 'prepare'}
      onClose={pause} footer={status?.running && status.localProfileId ?
        <Button onClick={() => onOpenHost(status.localProfileId!)}>Open local server</Button> :
        sharingRequired ? <Button disabled={!canShareStoppedProgress} onClick={() => void shareStoppedProgress()}><Icon name={busy ? 'loader' : 'archive'} />
          {busy ? hostingActions[pendingAction ?? 'checks'].label : 'Share stopped progress'}</Button> :
        waitingForHost && plannedOperation && startIntent.current ? <span role="status">Waiting for the current Host to finish the save...</span> :
        <Button disabled={!canContinue} onClick={() => { setNetworkConfirmed(true); if (requestOffered) void respond(true); else host() }}><Icon name={busy ? 'loader' : 'play'} />
          {busy ? hostingActions[pendingAction ?? 'checks'].label : status?.readyForManualStart ? 'Start hosting' : networkConfirmed ? 'Retry and start' : 'Host locally'}</Button>}>
      <div className="local-hosting-flow" aria-busy={busy}>
        {transfer.blocked && !(busy && (pendingAction === 'prepare' || pendingAction === 'start') || startUnconfirmed || waitingForHost) &&
          <FriendSaveTransferProgress status={transfer.status} fresh={transfer.fresh} unavailable={transfer.unavailable} />}
        {!status?.running && !networkConfirmed && <p className="local-hosting-warning">Hosting lets other players connect to this PC through your network. Keep TogetherServer open while hosting. {sharingRequired
          ? 'Share stopped progress keeps hosting stopped. Start hosting remains a separate choice after sharing and current checks finish.' : status?.readyForManualStart
          ? 'Start checks this PC and the prepared save again before starting.'
          : 'The app will get the latest available save, check this PC and start when it is ready.'}</p>}
        <>
          {busy || checking || waitingForHost || startUnconfirmed ? <LocalHostingProgress key={`${scope}:${pendingAction ?? (waitingForHost ? 'waiting' : 'checks')}:${pendingStartResult?.began ?? actionStartedAt}:${request.current}`}
            startedAt={pendingStartResult?.began ?? actionStartedAt}
            expectedStartedUtc={pendingStartResult?.startedUtc ?? undefined}
            endpoint={busy && (pendingAction === 'prepare' || pendingAction === 'start') || pendingStartResult?.startedUtc ? `${base}/hosting/progress` : undefined}
            onResult={recordProgressResult}
            onUpdate={observePreparationProgress}
            title={startUnconfirmed ? 'Checking the earlier Start result' : waitingForHost && !busy ? 'Waiting for the current Host’s final save' : hostingActions[pendingAction ?? 'checks'].label}
            message={startUnconfirmed ? startResultMessage : waitingForHost && !busy ? 'Keep TogetherServer open on both PCs. The final save must arrive and pass verification before Start.' :
              hostingActions[pendingAction ?? 'checks'].message} /> :
            status?.running ? <p>This server is already hosted on this PC.</p> : attention &&
            <section className="local-hosting-next" aria-label="Next hosting step"><h3>{attention.title}</h3><p>{attention.message}</p></section>}
          {busy && pendingAction === 'shareStoppedProgress' && <Button className="secondary" onClick={pause}>Pause sharing</Button>}
          {startUnconfirmed && <section className="local-hosting-next" role="status" aria-label="Earlier Start result"><h3>Check the earlier Start result</h3><p>{startResultMessage}</p></section>}
          {lastHostingAttempt && !busy && <section className="local-hosting-attempt-result" role="status" aria-label="Last hosting attempt">
            <strong>{lastHostingAttempt.state === 'Failed' ? 'Last hosting attempt needs attention' : 'Last hosting step finished'}</strong>
            {lastHostingAttempt.phase && <p>Last step: {hostingProgressPhaseLabel(lastHostingAttempt.phase)}.</p>}
            <p>{lastHostingAttempt.message}</p>
            {lastHostingAttempt.receivedBytes !== undefined && (lastHostingAttempt.receivedBytes > 0 || (lastHostingAttempt.totalBytes ?? 0) > 0) && <p>{lastHostingAttempt.phase === 'PreparingCopy' ? 'Hosting copy bytes prepared' :
              lastHostingAttempt.phase === 'Receiving' ? 'Received save bytes' : 'Reported save bytes'}: {formatSharedBytes(lastHostingAttempt.receivedBytes)}
              {lastHostingAttempt.totalBytes ? ` of ${formatSharedBytes(lastHostingAttempt.totalBytes)}` : ' reported'}.</p>}
            {lastHostingAttempt.completedFiles !== undefined && lastHostingAttempt.totalFiles !== undefined && lastHostingAttempt.totalFiles > 0 && <p>
              {lastHostingAttempt.completedFiles} of {lastHostingAttempt.totalFiles} {hostingProgressFilesLabel(lastHostingAttempt.phase ?? '')}.</p>}
            <HostingPhaseTimings timings={lastHostingAttempt.phaseTimings} />
            <small>Finished <time dateTime={new Date(lastHostingAttempt.finishedAt).toISOString()}>{checkedAgo(lastHostingAttempt.finishedAt, now)}</time>.</small>
            {lastHostingAttempt.state === 'Failed' && <p>{startUnconfirmed
              ? 'Wait for the matching result from this Start attempt. Choose Check Start result to read current checks, or Earlier Start server details to review the server. Checking does not retry Start.'
              : lastHostingAttempt.action === 'shareStoppedProgress' || sharingRequired
                ? 'Refreshing checks does not repeat sharing. Keep the server stopped and review the backup and setup if this result returns. Choose Share stopped progress only when you want to retry the explicit sharing check.'
                : 'Refreshing checks does not retry this hosting attempt. Follow the setup steps below, then choose Host locally to try again.'}</p>}
          </section>}
          {(attention?.kind === 'ChooseServer' || status?.needsServerFile) && <div className="local-hosting-fix">
            {status?.game === 'Valheim' && <a href="steam://install/896660">Install Valheim Dedicated Server in Steam</a>}
            {['MinecraftJava', 'MinecraftBedrock'].includes(status?.game ?? '') && <a href="https://www.minecraft.net/en-us/download/server" target="_blank" rel="noreferrer">Get the official Minecraft server</a>}
            {onOpenGameSetup && status?.localProfileId && <Button className="secondary" disabled={busy || disabled} onClick={gameSetup}><Icon name="server" />Open game setup</Button>}
            <label>Installed server file<div className="field-with-button"><Input ref={serverField} aria-label="Installed server file" value={serverFile} disabled={busy}
              placeholder={status?.game === 'MinecraftJava' ? 'Choose the installed server.jar' : 'Choose the installed dedicated server'}
              onChange={event => setServerFile(event.target.value)} />
              {onBrowseServerFile && <Button className="secondary" disabled={busy || checking || !status?.game} onClick={() => void browse()}><Icon name="folder" />Browse</Button>}</div></label>
            <p className="helper-text">Install the same game version as this save, then choose its server file. Accept the game's terms yourself. Existing worlds are kept.</p>
          </div>}
          {status?.needsPassword && !status.readyForManualStart && <label>New game password<Input type="password"
            autoComplete="new-password" disabled={busy} value={password} onChange={event => setPassword(event.target.value)} /></label>}
          {minecraftTerms && <label className="local-hosting-consent"><Input type="checkbox" checked={acceptedTerms} disabled={busy}
            onChange={event => { setAcceptedTerms(event.target.checked); startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null) }} />
            <span>I have read and accept the terms for preparing this Minecraft server.
              <small>Review the <a href="https://www.minecraft.net/en-us/eula" target="_blank" rel="noreferrer">Minecraft EULA</a> and{' '}
                <a href="https://www.microsoft.com/en-us/privacy/privacystatement" target="_blank" rel="noreferrer">Microsoft Privacy Statement</a>.</small></span></label>}
          {!status?.running && <LocalHostingPorts ports={ports} existing={!!status?.localProfileId} busy={busy || checking || disabled}
            onChange={() => { startIntent.current = null; plannedAuthorization.current = null; setPlannedOperation(null) }} />}
          {attention?.kind === 'Connection' && <div className="local-hosting-fix"><p>Use Connection help to check this PC's address and the port friends need. It explains any Windows, router or provider problem that needs your action.</p>
            {onOpenConnectionHelp && <Button className="secondary" disabled={busy || disabled} onClick={connectionHelp}><Icon name="plug" />Connection help</Button>}
          </div>}
          {attention?.kind === 'Ports' && <div className="local-hosting-fix"><p>Open local servers to find the app using these game ports. Stop it only if it is yours, or choose another game port in Setup, then return and continue.</p>
            {status?.localProfileId && <Button className="secondary" disabled={busy || disabled} onClick={() => { pause(); onOpenHost(status.localProfileId!) }}>Open local server setup</Button>}
            {onOpenGameSetup && status?.localProfileId && <Button className="secondary" disabled={busy || disabled} onClick={gameSetup}>Open game setup</Button>}
          </div>}
          {attention?.kind === 'GameSetup' && status?.localProfileId && onOpenGameSetup && <Button className="secondary" disabled={busy || disabled} onClick={gameSetup}>Open game setup</Button>}
          <section className="local-hosting-results" aria-labelledby={resultsId}>
            <div className="local-hosting-results-heading"><h3 id={resultsId}>Automatically checked results</h3>
              <p className="local-hosting-check-time">{checking ? 'Checking again… ' : ''}{lastCheckedAt !== null
                ? <time dateTime={new Date(lastCheckedAt).toISOString()} title={new Date(lastCheckedAt).toLocaleTimeString()}>Last checked {checkedAgo(lastCheckedAt, now)}</time>
                : 'Waiting for the first check'}</p></div>
            <section className="local-hosting-check-result" role="status" aria-label="Last check result" aria-live="polite" aria-atomic="true">
              <strong>{checking ? 'Checking this PC…' : lastCheckAttempt?.title ?? 'Checks have not finished'}</strong>
              <p>{checking ? 'Reading current hosting access and setup. Wait for the result before starting.' :
                startUnconfirmed && !priorityStep ? startResultMessage : sharingIdentityChanged ? sharingIdentityMessage : sharingRequired && guidanceStatus ? hostingCheckResult(guidanceStatus) :
                  lastCheckAttempt?.summary ?? 'Keep TogetherServer open on both PCs, then retry the checks.'}</p>
              {lastCheckAttempt && <small>Last attempt finished <time dateTime={new Date(lastCheckAttempt.finishedAt).toISOString()}>{checkedAgo(lastCheckAttempt.finishedAt, now)}</time></small>}
            </section>
            {!checksCurrent && status && <p className="local-hosting-stale" role="status">{checking
              ? 'Showing the previous results while these checks run.'
              : 'Showing the last results. Retry the checks before starting.'}</p>}
            <ol className="local-hosting-checks" aria-label="Local hosting checks">{displayedChecks.map(step => <li key={step.key} data-state={checksCurrent ? step.state : 'Unknown'}>
              <span className="local-hosting-check-icon"><Icon name={checkIcons[step.key]} size={20} /></span>
              <div className="local-hosting-check-description"><strong>{checkLabels[step.key]}</strong><p>{step.message}</p>
                {!step.checking && <p className="local-hosting-check-next"><strong>Next:</strong> {step.nextAction}</p>}
                <Button className="text-button local-hosting-check-help" disabled={busy}
                  aria-label={`${step.state === 'Ready' ? 'Review' : 'Set up'} ${checkLabels[step.key]}`}
                  onClick={() => setGuideStep(previous => ({ key: step.key, request: (previous?.request ?? 0) + 1 }))}>{step.waiting ? 'Show what is needed first' : step.state === 'Ready' ? 'Review check' : 'Show setup steps'}<Icon name="chevronRight" /></Button></div>
              <span className="local-hosting-check-state"><Icon name={!checksCurrent ? 'clock' : step.state === 'Ready' ? 'check' : step.state === 'ActionRequired' ? 'warning' : 'clock'} size={14} />
                {step.checking ? 'Checking…' : checking ? 'Updating…' : !checksCurrent ? 'Previous result' : step.waiting ? 'Not checked' : step.state === 'Ready' ? 'Checked' : step.state === 'ActionRequired' ? 'Needs attention' : 'Unverified'}</span>
            </li>)}</ol>
            <p className="helper-text">Start requires valid signed hosting permission and a complete verified save. An offline check cannot show whether another PC has newer progress or is still hosting. Different histories stay separate. Loading and saving in the real game and a friend joining from another network require separate testing.</p>
          </section>
          <LocalHostingGuide key={guideStep?.request ?? 0} status={guidanceStatus}
            selectedStep={guideStep?.key}
            recommendedStep={recommendedStep} busy={busy || checking || disabled}
            checking={checking} checksCurrent={checksCurrent && guidanceStatus === checkedGuidance}
            feedback={actionFeedback}
            onRetry={() => void refresh()} onChooseServer={status?.needsServerFile ? () => {
              if (onBrowseServerFile) void browse()
              else { serverField.current?.focus(); setActionMessage('Enter the installed server file above, then choose Host locally. The file is checked before Start.') }
            } : undefined}
            onConnectionHelp={onOpenConnectionHelp ? connectionHelp : undefined}
            onGameSetup={onOpenGameSetup && status?.localProfileId ? gameSetup : undefined}
            onOpenLocalServer={status?.localProfileId ? () => { pause(); onOpenHost(status.localProfileId!) } : undefined}
            onReviewSharedSave={onReviewSharedSave ? () => { pause(); onReviewSharedSave() } : undefined} />
          <div className="local-hosting-secondary-actions"><Button className="text-button" disabled={busy || disabled} onClick={() => void refresh()}><Icon name="refresh" />{startUnconfirmed ? 'Check Start result' : 'Retry automatic checks'}</Button>
            {pendingStartResult && <Button className="text-button" disabled={busy || disabled}
              onClick={() => { pause(); onOpenHost(pendingStartResult.localProfileId) }}>Earlier Start server details</Button>}
            {status?.localProfileId && status.localProfileId !== pendingStartResult?.localProfileId && <Button className="text-button" disabled={busy || disabled}
              onClick={() => { pause(); onOpenHost(status.localProfileId!) }}>Local server &amp; connection details</Button>}
            {onOpenConnectionHelp && attention?.kind !== 'Connection' && status?.steps[4].state !== 'Ready' && <Button className="text-button"
              disabled={busy || disabled} onClick={connectionHelp}><Icon name="plug" />Connection help</Button>}
          </div>
        </>
        {error && <p role="alert">{error}</p>}
      </div>
    </SettingsDialog>}
  </>
}

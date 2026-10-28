import React, { useCallback, useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { ApiError, changeJson, errorMessage, getJson, getLocalJson } from './api'
import { AppErrorBoundary } from './AppErrorBoundary'
import { ProfileAccess, ProfileSettings, useLocalProfile } from './ProfileSettings'
import { RequiredFeatureSetup, useRequiredFeatureSetup } from './RequiredFeatureSetup'
import { GuideWelcome, GuidedWalkthrough, type GuideKind, type GuideStep } from './GuidedSetup'
import { useGettingStarted } from './useGettingStarted'
import { AppSettings } from './AppSettings'
import { SectionSidebar } from './SectionSidebar'
import { ResizableWorkspace } from './ResizableWorkspace'
import { appSections, hostPcSections, joinSections, workspaceDestination, type AppSection, type HostPcSection, type JoinSection } from './workspaceSections'
import { useConfirmation } from './Confirmation'
import { Tooltip } from './Tooltip'
import { HostSettingsTools, type HostSettingsPatch } from './HostSettingsTools'
import { FriendPcCard } from './FriendPcCard'
import { directoryWithSnapshot, GuestHostingLabel, parseServerDirectory, ServerDirectory, type ServerDirectoryView, type ServerSelection } from './ServerDirectory'
import { originalLocalServer, startedSharedServer } from './sharedServerProfiles'
import { isRemovedHost, orderProfiles } from './profileLists'
import { SettingsDialog, SettingsSectionHeading } from './SettingsPresentation'
import { FriendAccessExpiredNotice, OwnerAccessDeadlineEditor, putDeviceAccessExpiry } from './AccessExpiry'
import { Button, Input, Select } from './Controls'
import { FriendConnectionDoctor, FriendGameCheckResult, friendGameCheckIdentity } from './FriendConnectionDoctor'
import { FriendPlayFlow, effectiveFriendPermissions, friendConnectionExplanation } from './FriendPlayFlow'
import { FriendLocalHosting } from './FriendLocalHosting'
import { SharedHostNotice } from './SharedHostNotice'
import { SharedGamePassword } from './SharedGamePassword'
import { FriendRemoteRehearsal } from './RemoteRehearsal'
import { ConnectionDetails } from './ConnectionDetails'
import { GameCompatibilityPanel } from './GameCompatibilityPanel'
import { GameSettingsPanel } from './GameSettingsPanel'
import { BackupCatalog } from './BackupCatalog'
import { HostBackupsPage, applyProtectionPolicy, type ProtectionPolicy } from './HostBackupsPage'
import { ServerNotificationSettings, UpdateDetailsPanel } from './DesktopPreferencesPanel'
import { parseDesktopUpdateDetails, type DesktopUpdateDetails } from './desktopPreferencesWire'
import { parseNotificationPreferences } from './desktopPreferencesWire'
import { subscribeToDesktopNotifications, subscribeToDesktopDraftFlush, type DesktopNotificationTarget } from './desktopNotificationBridge'
import { AttentionWorkspace, readAttentionHistory, withAttentionHistorySource } from './AttentionWorkspace'
import { FriendAccessReview, FriendAccessSummary, FriendPcFilters, defaultFriendPcFilters, filterFriendPcs } from './FriendAccessSummary'
import { HostFirstServer } from './HostFirstServer'
import { EditorDraftRecovery, type EditorDraftGuard } from './editorProtectedDraft'
import { SetupFirstSessionGuide } from './setupFirstSessionGuide'
import { WeeklySummary } from './WeeklySummary'
import { MaintenanceGuide } from './MaintenanceGuide'
import { TemporaryHelperAccess } from './TemporaryHelperAccess'
import { DataRecoveryPanel } from './DataRecoveryPanel'
import { StartupRecoveryNotice } from './StartupRecoveryNotice'
import { ServerStartupProgress, serverStartupStage } from './ServerStartupProgress'
import { DismissibleNotice } from './DismissibleNotice'
import { OwnerDiagnostics } from './OwnerDiagnostics'
import { PaneErrorBoundary } from './PaneErrorBoundary'
import { PlayersPanel, playerCountAvailability } from './PlayersPanel'
import { isPlayerCountObservationUnavailable, publicPlayerCount } from './playerCountPresentation'
import { RecentSessions } from './RecentSessions'
import { allServerRows } from './AllServerOverview'
import { WorldLoadRehearsalPanel, parseWorldLoadResult } from './WorldLoadRehearsal'
import {
  HostSetupDialog
} from './features/setup/HostSetupDialog'
import { useHostSetup } from './features/setup/useHostSetup'
import { defaultUiPreferences, readUiPreferences, writeUiPreferences, type UiPreferences } from './qolPreferences'
import { ActionFeedback, pendingDescription, TimerPresets } from './QolWidgets'
import { currentActionNotice, currentRemoteOperation, serverActionNotice, type ActionNotice } from './actionFeedback'
import {
  parseActionResult, parseAppInstance, parseBackupSafetyResult, parseBasicResult, parseCompanionInfo, parseCustomCertificationResult,
  parseDataRecoveryView, parseDesktopPreferenceResult, parseDesktopPreferences,
  parseFriendSnapshot, parseGameEndpointResult, parseInternetRouteCheck, parseInviteResult,
  parseInviteState, parsePasswordResult, parsePortDiagnostics, parsePublicIpDetection, parseRouteDiscovery,
  parseSnapshot, parseWorldBackupList, parseWorldBackupVerificationResult, parseHostMoveKitResult,
  type ActionResult, type AppInstanceView, type BasicResult, type CompanionInfo,
  type DataRecoveryView, type DesktopPreferences, type Device, type FriendIssue,
  type DeviceAccessExpiryRequest, type DeviceAccessExpiryResult,
  type FriendSnapshot, type GameEndpointResult, type PublicIpDetection, type PublicProfile, type RouteDiscovery, type Settings,
  type Snapshot, type WorldBackupList, type HostMoveKitResult
} from './contracts'
import { Icon, type IconName } from './Icon'
import { ServerReadiness, currentOutsideResult, type PortDiagnostics, type InternetRouteCheck } from './ServerReadiness'
import { FriendStartConnectionNotice, HostStartConnectionNotice } from './StartConnectionNotice'
import { ServerLogViewer, friendLogAvailability } from './ServerLogViewer'
import { ServerChat, ServerChatCardSummary, type ChatDraftState } from './ServerChat'
import { ServerFilesPanel } from './ServerFilesPanel'
import { ServerSettingsWorkspace, type EditorRecoveryRequest } from './ServerSettingsWorkspace'
import { HostSharedSaves } from './SharedWorldControls'
import { SharedHostingIdentity } from './SharedHostingIdentity'
import { WorldPeerPanel } from './WorldPeerPanel'
import { gameLabel, profileGameLabel, type Profile } from './GameProfile'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'
import {
  devicePasswordPermission, devicePermission, globalPermissionRequest, matchingPermissionPreset, permissionMix,
  permissionPresetRequest, permissionPresets, type PermissionAction, type PermissionPreset
} from './permissionState'
import {
  activityAfterMarker,
  activityDestination,
  type ActivityDestination,
  collapseRepeatedActivity,
  isRoutinePlayerCountActivity,
  readActivityClearMarkersFrom,
  withActivityClearMarker,
  writeActivityClearMarkersTo
} from './notificationState'
import {
  CommandPalette, HeaderTools, StatusStrip,
  type WorkspaceCommand, type WorkspacePage
} from './WorkspaceChrome'
import './theme.css'
import './style.css'
import './companion.css'
import './qol.css'
import './core.css'
import './settings.css'
import './settings-integration.css'
import './friend-access.css'
import './redesign.css'
import './layers.css'

type HostServerTab = 'overview' | 'chat' | 'players' | 'logs' | 'sessions' | 'backups' | 'files' | 'setup'
const hostServerTabLabels: Record<HostServerTab, string> = {
  overview: 'Overview', chat: 'Chat', players: 'Players', logs: 'Logs', sessions: 'Sessions',
  backups: 'Backups', files: 'Settings & files', setup: 'Setup & maintenance'
}
const hostServerTabIcons: Record<HostServerTab, IconName> = {
  overview: 'server', chat: 'chat', players: 'users', logs: 'logs', sessions: 'clock',
  backups: 'archive', files: 'settings', setup: 'plug'
}
type ConnectionActivity = Record<string, 'copy' | 'reveal'>
type PermissionDraft = Record<string, { canStart: boolean; canStop: boolean; canExtendTimer: boolean; canViewLogs: boolean; canViewPassword?: boolean }>
type DeviceAccessTab = 'permissions' | 'servers' | 'time' | 'details'
const permissionLabels: Record<PermissionAction, { label: string; icon: IconName }> = {
  canStart: { label: 'Start servers', icon: 'play' }, canStop: { label: 'Request Stop', icon: 'stop' },
  canExtendTimer: { label: 'Add shutdown time', icon: 'clock' }, canViewLogs: { label: 'View logs', icon: 'logs' }
}
function serverAccessKey(ids: readonly string[], permissions: PermissionDraft) {
  return JSON.stringify([...new Set(ids)].sort().map(id => [id, permissions[id]?.canStart ?? false,
    permissions[id]?.canStop ?? false, permissions[id]?.canExtendTimer ?? false, permissions[id]?.canViewLogs ?? false, permissions[id]?.canViewPassword ?? false]))
}

function MixedCheckbox({ mixed, ...props }: React.InputHTMLAttributes<HTMLInputElement> & { mixed: boolean }) {
  const ref = useRef<HTMLInputElement | null>(null)
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = mixed
  }, [mixed])
  return <Input ref={ref} aria-checked={mixed ? 'mixed' : props.checked} {...props} />
}

function FriendConnectionHelp({ code }: { code: string }) {
  const steps = (() => {
    switch (code) {
      case 'InvalidInvite': case 'PairingRejected': case 'Revoked': case 'CredentialExpired': case 'CredentialRejected':
        return { friend: 'Ask the Host for the current server code and check that this PC still has access.',
          host: 'Copy a code from Invite friends. Check this PC under Friend access.' }
      case 'HostAddressMismatch': case 'InviteAddressInvalid':
        return { friend: 'Check the Host address. New server codes include it.',
          host: 'Copy a fresh server code. Check the address under Network.' }
      case 'HostIdentityMismatch':
        return { friend: 'Ask the Host directly for a fresh code. The saved security check did not match.',
          host: 'Copy a fresh code from TogetherServer and check its address under Network.' }
      case 'FriendNetworkUnavailable':
        return { friend: 'Check your internet connection, then ask the Host for a fresh code.',
          host: 'Check the server code address under Network.' }
      case 'HostPortClosed':
        return { friend: 'Check your internet connection and ask for a fresh server code.',
          host: 'Keep TogetherServer open. Use Connection help to check the app port, firewall, and router.' }
      case 'HostPortTimedOut': case 'HostTimedOut': case 'HostUnreachable':
        return { friend: 'Check your internet connection and ask for a fresh server code.',
          host: 'Use Connection help to check the address, firewall, and router. Your internet provider may block incoming connections.' }
      case 'HostBusy':
        return { friend: 'Wait a moment, then try the code again.',
          host: 'Keep TogetherServer open and check for connection warnings.' }
      case 'HostUnavailable': case 'HostInvalidResponse':
        return { friend: 'Ask the Host to keep TogetherServer open, then try again.',
          host: 'Keep TogetherServer open and review Connection help.' }
      case 'HostAccessDenied':
        return { friend: 'Ask the Host to check your access and send a current code.',
          host: 'Check this PC under Friend access.' }
      case 'LocalAppUnavailable':
        return { friend: 'Reopen TogetherServer on this PC and try again.',
          host: 'Ask your friend to reopen TogetherServer on their PC.' }
      default:
        return { friend: 'Check your internet connection and ask for a fresh server code.',
          host: 'Keep TogetherServer open and use Connection help to check the network.' }
    }
  })()
  return <div className="friend-connection-help"><div><strong>Check on this PC</strong><p>{steps.friend}</p></div>
    <div><strong>Ask the Host to check</strong><p>{steps.host}</p></div>
    <small>Only the Host may need to set up port forwarding.</small></div>
}

function FriendStopBlockers({ snapshot, profile }: { snapshot: FriendSnapshot; profile: PublicProfile }) {
  const authenticated = snapshot.state === 'Connected' || snapshot.state === 'Disabled'
  const stopVisible = profile.state === 'Ready' && snapshot.state === 'Connected' && profile.canStop && profile.canStopNow
  if (!authenticated || stopVisible) return null
  const blockers: string[] = []
  if (!snapshot.remoteControlsEnabled) blockers.push('The Host has paused remote Start and Stop.')
  if (!profile.canStop) blockers.push('Ask the Host for permission to stop this server.')
  if (profile.stopReason) blockers.push(`Stop unavailable: ${profile.stopReason}`)
  return <details className="stop-blockers"><summary>Why Stop is unavailable</summary><ul>{blockers.map(blocker => <li key={blocker}>{blocker}</li>)}</ul></details>
}

function statusTone(state: string) {
  if (['Ready', 'Connected', 'Process running'].includes(state)) return 'running'
  if (['Failed', 'Revoked'].includes(state)) return 'error'
  if (['Disabled', 'Starting', 'Listening', 'World copy in progress', 'Stopping', 'Access expired'].includes(state)) return 'paused'
  if (state === 'Offline') return 'offline'
  return 'unknown'
}

function playerCount(online: number | null, capacity: number | null) {
  if (online === null) return 'Player count unavailable'
  return capacity === null ? `${online} online` : `${online} / ${capacity} online`
}

function countdownLabel(deadline: string | null, nowMs: number) {
  if (!deadline) return null
  const target = Date.parse(deadline)
  if (!Number.isFinite(target)) return null
  const seconds = Math.max(0, Math.ceil((target - nowMs) / 1000))
  if (seconds === 0) return 'Waiting for player check…'
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const remainder = seconds % 60
  return `Stops in ${hours > 0 ? `${hours}:` : ''}${hours > 0 ? String(minutes).padStart(2, '0') : minutes}:${String(remainder).padStart(2, '0')}`
}

function formatBytes(bytes: number) {
  if (!Number.isFinite(bytes) || bytes < 0) return 'Unavailable'
  if (bytes < 1024) return `${Math.round(bytes)} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes / 1024
  let index = 0
  while (value >= 1024 && index < units.length - 1) { value /= 1024; index += 1 }
  return `${value.toFixed(value >= 10 ? 1 : 2)} ${units[index]}`
}

function ServerCountdown({ deadline, nowMs }: { deadline: string | null; nowMs: number }) {
  const label = countdownLabel(deadline, nowMs)
  return label ? <span className="server-compact-countdown" role="timer" title="The Host rechecks players before automatic Stop."><Icon name="clock" size={14} />{label}</span> : null
}

function ServerActivity({ state, online, capacity, deadline, nowMs, players,
  onRefresh, refreshing = false, refreshDisabled = false }: {
  state: string; online: number | null; capacity: number | null; deadline: string | null; timerReason: string | null; nowMs: number; players?: string[] | null
  onRefresh?: () => void; refreshing?: boolean; refreshDisabled?: boolean
}) {
  if (state !== 'Ready') return null
  const countdown = online === 0 ? countdownLabel(deadline, nowMs) : null
  return <div className="server-activity-wrap"><div className="server-activity">
      <span className={`player-count ${online === null ? 'unknown' : ''}`}>{playerCount(online, capacity)}</span>
      {onRefresh && <Button className="secondary icon-button player-count-refresh" disabled={refreshing || refreshDisabled}
        onClick={onRefresh} aria-label={online === null ? 'Retry player count' : 'Refresh player count'}
        title={online === null ? 'Retry player count' : 'Refresh player count'}>
        <Icon name={refreshing ? 'loader' : 'refresh'} size={15} /><span className="sr-only">{online === null ? 'Retry player count' : 'Refresh player count'}</span>
      </Button>}
      {countdown && <span className="idle-countdown" role="timer" title="No players are online and automatic shutdown is on.">{countdown}</span>}
    </div>
    {!!players?.length && <small className="player-names">Players: {players.join(', ')}</small>}
  </div>
}

function serverAssignmentPreview(device: Device, profiles: Profile[]) {
  const names = profiles.filter(profile => device.assignedProfileIds.includes(profile.id)).map(profile => profile.name)
  if (names.length === 0) return 'No servers assigned'
  if (names.length <= 2) return names.join(', ')
  return `${names.slice(0, 2).join(', ')} + ${names.length - 2} more`
}

function hostAddress(endpoint: string): string {
  try {
    const url = new URL(endpoint)
    return url.port === '5131' ? url.hostname : url.host
  } catch { return '' }
}

function readSnapshot(signal?: AbortSignal): Promise<Snapshot> {
  return getJson('/api/local/snapshot', parseSnapshot, signal)
}

function change(path: string, method: 'POST' | 'PUT' | 'DELETE', body?: unknown, signal?: AbortSignal): Promise<BasicResult> {
  return changeJson(path, method, parseBasicResult, body, signal)
}

function changeAction(path: string, method: 'POST' | 'PUT', body?: unknown, signal?: AbortSignal): Promise<ActionResult> {
  return changeJson(path, method, parseActionResult, body, signal)
}

function useModalDialog(open: boolean) {
  const ref = useRef<HTMLDialogElement | null>(null)
  useEffect(() => {
    if (!open || !ref.current) return
    const dialog = ref.current
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    dialog.showModal()
    return () => {
      if (dialog.open) dialog.close()
      previousFocus?.focus()
    }
  }, [open])
  return ref
}

export function App({ developmentDemoControls }: { developmentDemoControls?: React.ReactNode } = {}) {
  const { confirm, confirmationDialog } = useConfirmation()
  const [uiPreferences, setUiPreferences] = useState<UiPreferences>(() => {
    try { return readUiPreferences(window.localStorage) }
    catch { return { ...defaultUiPreferences } }
  })
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [serverDirectory, setServerDirectory] = useState<ServerDirectoryView | null>(null)
  const [serverDirectoryError, setServerDirectoryError] = useState('')
  const [directoryAction, setDirectoryAction] = useState<'new' | 'connect' | null>(null)
  const [appInstance, setAppInstance] = useState<AppInstanceView | null>(null)
  const [nowMs, setNowMs] = useState(() => Date.now())
  const feedbackNowMs = Date.now()
  const [pending, setPending] = useState('')
  const [noticeState, setNoticeState] = useState<ActionNotice | null>(null)
  const setNotice = useCallback<React.Dispatch<React.SetStateAction<ActionNotice | null>>>(next => {
    setNoticeState(current => {
      const value = typeof next === 'function' ? next(current) : next
      return value ? { ...value, receivedAtMs: Date.now() } : null
    })
  }, [])
  const notice = currentActionNotice(noticeState, snapshot, feedbackNowMs)
  const [loadError, setLoadError] = useState('')
  const [update, setUpdate] = useState<DesktopUpdateDetails | null>(null)
  const [updateBusy, setUpdateBusy] = useState(false)
  const [updateReminderBusy, setUpdateReminderBusy] = useState(false)
  const [notificationUnread, setNotificationUnread] = useState(false)
  const [workspacePage, setWorkspacePage] = useState<WorkspacePage>('host')
  const [appSection, setAppSection] = useState<AppSection>('profile')
  const [appSettingsScrollRequest, setAppSettingsScrollRequest] = useState<{ section: AppSection; requestId: number }>()
  const jumpToAppSection = (section: AppSection) => {
    setAppSection(section)
    setAppSettingsScrollRequest(current => ({ section, requestId: (current?.requestId ?? 0) + 1 }))
  }
  const [hostScope, setHostScope] = useState<'server' | 'pc'>('server')
  const [joinSection, setJoinSection] = useState<JoinSection>('play')
  const [selectedFriendProfileId, setSelectedFriendProfileId] = useState('')
  const [serverToolsProfileId, setServerToolsProfileId] = useState('')
  const localProfile = useLocalProfile()
  const requiredSetup = useRequiredFeatureSetup()
  const gettingStarted = useGettingStarted(requiredSetup.ready && snapshot !== null)
  const [appSettingsBusy, setAppSettingsBusy] = useState(false)
  const [serverNotification, setServerNotification] = useState<{ id: string; name: string; connectionId?: string } | null>(null)
  const [serverNotificationBusy, setServerNotificationBusy] = useState(false)
  const [commandPaletteOpen, setCommandPaletteOpen] = useState(false)
  const [selectedHostProfileId, setSelectedHostProfileId] = useState(uiPreferences.selectedServer)
  const [hostServerTab, setHostServerTab] = useState<HostServerTab>(() =>
    (uiPreferences.serverTabs[uiPreferences.selectedServer] as HostServerTab | undefined) ?? 'overview')
  const [friendLogProfileId, setFriendLogProfileId] = useState('')
  const [friendChatProfileId, setFriendChatProfileId] = useState('')
  const friendDoctorFocusRequested = useRef(false)
  const [hostMobileDetail, setHostMobileDetail] = useState(false)
  const directorySelectionInFlight = useRef(false)
  const [deviceFilters, setDeviceFilters] = useState(defaultFriendPcFilters)
  const [actionNotices, setActionNoticeState] = useState<Record<string, ActionNotice>>({})
  const setActionNotices = useCallback<React.Dispatch<React.SetStateAction<Record<string, ActionNotice>>>>(next => {
    setActionNoticeState(current => {
      const value = typeof next === 'function' ? next(current) : next
      return Object.fromEntries(Object.entries(value).map(([id, result]) => [id,
        result === current[id] ? result : { ...result, receivedAtMs: Date.now() }]))
    })
  }, [])
  const scrollPositions = useRef<Record<string, number>>({})
  const scrollContainer = useRef<HTMLDivElement | null>(null)
  const workspaceScrollTarget = useCallback(() => {
    const viewport = scrollContainer.current
    return (window.matchMedia?.('(min-width: 701px)').matches ?? window.innerWidth > 700)
      ? viewport?.querySelector<HTMLDivElement>(':scope > .context-workspace > .context-content') ?? viewport
      : viewport
  }, [])
  const currentScrollKey = useRef('')
  const restoringScroll = useRef(false)
  const currentOperationProfile = useRef<Record<string, string>>({})
  const draftGuards = useRef<Map<string, EditorDraftGuard>>(new Map())
  const [hasUnsavedServerEdits, setHasUnsavedServerEdits] = useState(false)
  const [editorRecoveryRequest, setEditorRecoveryRequest] = useState<EditorRecoveryRequest | null>(null)
  const navigationEpoch = useRef(0)
  const modeSwitchesInFlight = useRef(0)
  const keepEditorGuard = useCallback((source: string, guard: EditorDraftGuard | null) => {
    if (guard) draftGuards.current.set(source, guard)
    else draftGuards.current.delete(source)
    const unsaved = [...draftGuards.current].some(([key, current]) => key !== 'chat' && (current.dirty || current.saving))
    setHasUnsavedServerEdits(unsaved)
    setEditorRecoveryRequest(current => {
      if (!current) return null
      const active = draftGuards.current.get(current.source)
      return active?.dirty || active?.saving ? current : null
    })
  }, [])
  const keepChatGuard = useCallback((guard: ChatDraftState) => {
    if (guard.dirty || guard.saving) draftGuards.current.set('chat', guard)
    else draftGuards.current.delete('chat')
  }, [])
  const beforeChangingScope = async (reviewSettings = true): Promise<boolean> => {
    if (localProfile.saving || appSettingsBusy || serverNotificationBusy || workspacePage === 'settings' && (desktopBusy || updateBusy)) {
      setNotice({ good: false, text: 'Wait for the change to save before leaving.' })
      return false
    }
    if (pending === 'save-settings' || pending === 'host-flags') {
      setNotice({ good: false, text: 'Wait for the Host setting to save before leaving.' })
      return false
    }
    for (const [source, guard] of draftGuards.current) {
      if ((guard.dirty || guard.saving) && !await guard.flush()) {
        const section = source.startsWith('file:') ? 'files' : /^(settings|list):/.test(source) ? 'settings' : null
        if (section) setEditorRecoveryRequest(current => ({ section, source, revision: (current?.revision ?? 0) + 1 }))
        setNotice({ good: false, text: 'Save a draft or discard your changes before leaving.' })
        return false
      }
    }
    if (reviewSettings && hostSettingsDraftDirty && !await confirm({ title: 'Discard unsaved Host settings?',
      description: 'Your changes have not been saved. Keep editing to keep them.',
      confirmLabel: 'Discard changes', cancelLabel: 'Continue editing', destructive: true })) return false
    return true
  }
  const changeScope = async (action: () => void): Promise<boolean> => {
    const epoch = ++navigationEpoch.current
    if (!await beforeChangingScope() || epoch !== navigationEpoch.current) return false
    setEditorRecoveryRequest(null)
    action()
    return true
  }
  const beforeLeavingAppRef = useRef(beforeChangingScope)
  const [activityClearMarkers, setActivityClearMarkers] = useState<Record<string, string>>(() =>
    readActivityClearMarkersFrom(() => window.localStorage))
  const [attentionHistory, setAttentionHistory] = useState(() => {
    try { return readAttentionHistory(window.localStorage) }
    catch { return { version: 1 as const, sources: [] } }
  })
  const updateAttentionHistory = useCallback((source: string, ids: { readIds: string[]; dismissedIds: string[] }) => {
    setAttentionHistory(current => withAttentionHistorySource(current, source, ids))
  }, [])
  const [showUpdatePrompt, setShowUpdatePrompt] = useState(false)
  const [desktopPreferences, setDesktopPreferences] = useState<DesktopPreferences | null>(null)
  const [desktopBusy, setDesktopBusy] = useState(false)
  const [companion, setCompanion] = useState<CompanionInfo | null>(null)
  const initialCompanionRef = useRef<{ value: CompanionInfo; epoch: number; receivedAtMs: number } | null>(null)
  const [publicIpDetection, setPublicIpDetection] = useState<PublicIpDetection | null>(null)
  const [portDiagnostics, setPortDiagnostics] = useState<PortDiagnostics | null>(null)
  const [checkingPorts, setCheckingPorts] = useState(false)
  const [internetRouteCheck, setInternetRouteCheck] = useState<InternetRouteCheck | null>(null)
  const [checkingInternetRoute, setCheckingInternetRoute] = useState(false)
  const [routeDiscovery, setRouteDiscovery] = useState<RouteDiscovery | null>(null)
  const [detectingPublicIp, setDetectingPublicIp] = useState(false)
  const [inviteProfileId, setInviteProfileId] = useState('')
  const [inviteListenerWarning, setInviteListenerWarning] = useState<string | null>(null)
  const [deviceNames, setDeviceNames] = useState<Record<string, string>>({})
  const [permissionPresetDraft, setPermissionPresetDraft] = useState<Record<string, PermissionPreset>>({})
  const [maintenanceMessages, setMaintenanceMessages] = useState<Record<string, string>>({})
  const [invitation, setInvitation] = useState('')
  const [pairingRequireApproval, setPairingRequireApproval] = useState(false)
  const [friendInvite, setFriendInvite] = useState('')
  const [friendHostAddress, setFriendHostAddress] = useState('')
  const [recoveryEndpoint, setRecoveryEndpoint] = useState('')
  const [friendConnectionName, setFriendConnectionName] = useState('')
  const [friendTroubleshootingOpen, setFriendTroubleshootingOpen] = useState(false)
  const [gameEndpointResults, setGameEndpointResults] = useState<Record<string, GameEndpointResult>>({})
  const [gameResultIdentities, setGameResultIdentities] = useState<Record<string, string>>({})
  const [backupLists, setBackupLists] = useState<Record<string, WorldBackupList>>({})
  const [backupCatalogRevisions, setBackupCatalogRevisions] = useState<Record<string, number>>({})
  const [moveKit, setMoveKit] = useState<HostMoveKitResult | null>(null)
  const [pairIssue, setPairIssue] = useState<FriendIssue | null>(null)
  const [showPairing, setShowPairing] = useState(false)
  const [showRemovedHosts, setShowRemovedHosts] = useState(false)
  const friendConnectionHidden = snapshot?.mode === 'Friend' && isRemovedHost(snapshot) && !showRemovedHosts
  const [countdownExtensions, setCountdownExtensions] = useState<Record<string, string>>({})
  const [dataRecovery, setDataRecovery] = useState<DataRecoveryView | null>(null)
  const [recoveryConfirmed, setRecoveryConfirmed] = useState(false)
  const [serverAccessDeviceId, setServerAccessDeviceId] = useState('')
  const [serverAccessDraft, setServerAccessDraft] = useState<string[]>([])
  const [serverPermissionDraft, setServerPermissionDraft] = useState<PermissionDraft>({})
  const [serverAccessSearch, setServerAccessSearch] = useState('')
  const [serverAccessOriginal, setServerAccessOriginal] = useState('')
  const [deviceAccessTab, setDeviceAccessTab] = useState<DeviceAccessTab>('permissions')
  const [discardDeviceEdits, setDiscardDeviceEdits] = useState(false)
  const [removeDeviceConfirm, setRemoveDeviceConfirm] = useState(false)
  const [removeServerConfirm, setRemoveServerConfirm] = useState(false)
  const [serverRemovalScope, setServerRemovalScope] = useState<{ deviceId: string; profileId: string; name: string } | null>(null)
  const currentServerRemovalScope = useRef(serverRemovalScope)
  currentServerRemovalScope.current = serverRemovalScope
  const removingServerAccess = useRef(false)
  useEffect(() => () => { currentServerRemovalScope.current = null }, [])
  const [deadlineDraftState, setDeadlineDraftState] = useState({ dirty: false, saving: false })
  const [hostSettingsDraftDirty, setHostSettingsDraftDirty] = useState(false)
  const [hostSettingsSection, setHostSettingsSection] = useState<HostPcSection>('access')
  const [revealedConnections, setRevealedConnections] = useState<Record<string, boolean>>({})
  const [revealedGamePasswords, setRevealedGamePasswords] = useState<Record<string, string>>({})
  const [connectionActivity, setConnectionActivity] = useState<ConnectionActivity>({})
  const inviteLoad = useRef(0)
  const snapshotEpochRef = useRef(0)
  const publicIpRequestRef = useRef(0)
  const liveConnectionKeysRef = useRef<Set<string>>(new Set())
  const connectionRevealRequestRef = useRef<Record<string, number>>({})
  const addressRecoveryRef = useRef<HTMLDetailsElement>(null)
  const recoveryAddressInputRef = useRef<HTMLInputElement>(null)
  const updatePromptRef = useModalDialog(showUpdatePrompt)
  const workspaceNavigationRef = useRef<(page: WorkspacePage) => void>(() => {})
  const connectServerRef = useRef<() => void>(() => {})
  const notificationNavigationRef = useRef<(target: DesktopNotificationTarget) => void>(() => {})

  useEffect(() => subscribeToDesktopNotifications(target => notificationNavigationRef.current(target)), [])
  useEffect(() => subscribeToDesktopDraftFlush(() => beforeLeavingAppRef.current()), [])
  useEffect(() => {
    const controller = new AbortController()
    void getLocalJson('/api/local/desktop/notifications', parseNotificationPreferences, controller.signal).then(preferences => {
      if (!controller.signal.aborted) setUiPreferences(current => ({ ...current, quietMode: preferences.quietMode }))
    }).catch(() => { /* The notification panel has an explicit retry. */ })
    return () => controller.abort()
  }, [])

  useEffect(() => {
    try { writeUiPreferences(window.localStorage, uiPreferences) } catch { /* Optional view storage. */ }
    document.documentElement.dataset.theme = uiPreferences.theme
    document.documentElement.dataset.density = uiPreferences.density
    document.documentElement.dataset.highContrast = String(uiPreferences.highContrast)
    document.documentElement.style.setProperty('--qol-text-scale', String(uiPreferences.textScale / 100))
  }, [uiPreferences])

  useEffect(() => {
    if (workspacePage !== 'join' || joinSection !== 'connection' || !friendDoctorFocusRequested.current) return
    setFriendTroubleshootingOpen(true)
    const doctor = document.querySelector<HTMLDetailsElement>('.friend-connection-doctor')
    if (!doctor) return
    doctor.open = true
    if (friendDoctorFocusRequested.current) {
      friendDoctorFocusRequested.current = false
      doctor.querySelector('summary')?.focus()
    }
  }, [workspacePage, joinSection, snapshot?.mode, friendTroubleshootingOpen])

  const scrollConnectionId = snapshot?.mode === 'Friend' ? snapshot.connectionId : ''
  const workspaceReady = snapshot !== null && appInstance !== null
  useEffect(() => {
    const key = workspacePage === 'settings' ? 'app:continuous' : workspacePage === 'join'
      ? `join:${scrollConnectionId}:${selectedFriendProfileId}:${joinSection}` : workspacePage === 'host'
        ? hostScope === 'pc' ? `host:pc:${selectedHostProfileId}:${hostSettingsSection}` : `host:${selectedHostProfileId}:${hostServerTab}`
        : workspacePage
    currentScrollKey.current = key
    restoringScroll.current = true
    const savedPosition = scrollPositions.current[key] ?? 0
    const frame = window.requestAnimationFrame(() => {
      const target = workspaceScrollTarget()
      if (target) target.scrollTop = savedPosition
      restoringScroll.current = false
    })
    return () => window.cancelAnimationFrame(frame)
  }, [workspacePage, scrollConnectionId, selectedFriendProfileId, joinSection, hostScope, selectedHostProfileId, hostSettingsSection, hostServerTab, snapshot?.mode, workspaceReady, workspaceScrollTarget])

  useEffect(() => {
    const media = window.matchMedia?.('(min-width: 701px)')
    const resize = () => {
      const target = workspaceScrollTarget()
      if (target) target.scrollTop = scrollPositions.current[currentScrollKey.current] ?? 0
    }
    if (media) {
      media.addEventListener('change', resize)
      return () => media.removeEventListener('change', resize)
    }
    window.addEventListener('resize', resize)
    return () => window.removeEventListener('resize', resize)
  }, [workspaceScrollTarget])

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (!requiredSetup.ready) return
      if (document.querySelector('dialog[open]') && !commandPaletteOpen) return
      const target = event.target
      const typing = target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement
      if ((event.ctrlKey || event.metaKey) && event.key.toLocaleLowerCase() === 'k') {
        event.preventDefault()
        setCommandPaletteOpen(open => !open)
        return
      }
      if (event.key === 'Escape') {
        if (commandPaletteOpen) setCommandPaletteOpen(false)
        else if (hostMobileDetail) setHostMobileDetail(false)
        else if (workspacePage === 'attention' || workspacePage === 'settings')
          workspaceNavigationRef.current(snapshot?.mode === 'Friend' ? 'join' : 'host')
        return
      }
      if (typing || !event.altKey) return
      if (event.key === '2') { event.preventDefault(); connectServerRef.current(); return }
      const page = ({ '1': snapshot?.mode === 'Friend' ? 'join' : 'host', '3': 'attention', '4': 'settings' } as const)[event.key]
      if (page) { event.preventDefault(); workspaceNavigationRef.current(page) }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [commandPaletteOpen, hostMobileDetail, snapshot?.mode, workspacePage, requiredSetup.ready])

  const applySnapshot = useCallback((next: Snapshot) => {
    setNowMs(Date.now())
    snapshotEpochRef.current += 1
    setSnapshot(next)
  }, [])
  const setup = useHostSetup({ snapshot, pending, setPending, setNotice, applySnapshot, confirm, onDraftGuardChange: keepEditorGuard,
    dataRecoveryBlocked: !!dataRecovery?.lifecycleBlocked, instance: appInstance,
    onSetupCompleted: profileId => {
      if (gettingStarted.active !== 'host') return
      setSelectedHostProfileId(profileId); setHostServerTab('overview'); gettingStarted.stepTo(1)
    } })
  const {
    draft, dirty, passwords, customScripts, customScriptsSaved, customScriptsLoading, discovery,
    minecraftDiscovery, minecraftTerms, sourceRoots, showSetup, setupStep, minecraftSetupMode,
    showPasswords, editedProfile, setupIssues, stepIssues, customScriptsChanged, setupRef,
    sensitiveDraft, acceptSavedSettings, setDraftIfClean, syncControlPolicy, syncDetectedPublicIp,
    synchronizeHostSnapshot, resetForMode, saveSetup, updateProfile, editCustomScripts, updateCustomPort,
    addCustomPort, removeCustomPort, browseCustomDirectory, browseFactorio, browseTerraria, applyMinecraftInstallation, scanMinecraft,
    installMinecraft, changeGameKind, addProfile, removeProfile, cancelSetup, finishSetupLater, openSetup,
    continueSetup, scanValheim, importWorld, browseServer, browseMinecraft, browseWorld, setSetupStep,
    setSourceRoot, setPassword, setShowPassword, setMinecraftSetupModeFor, setMinecraftTermsFor,
    reuseProfile, setupDraftRecovery, recoverSetupDraft, discardSetupDraft,
    importReview, confirmImport, cancelImport, firstSessionProfileId, dismissFirstSessionGuide, pausedSetupProfileId
  } = setup
  const beforeLeavingApp = async () => {
    if (!requiredSetup.ready) return !localProfile.saving && !requiredSetup.busy
    if (!await beforeChangingScope(false)) return false
    const pcEdits = serverAccessDevice && (serverAccessDirty || deadlineDraftState.dirty ||
      (deviceNames[serverAccessDevice.id] ?? serverAccessDevice.name).trim() !== serverAccessDevice.name ||
      (permissionPresetDraft[serverAccessDevice.id] ?? matchingPermissionPreset(serverAccessDevice)) !== matchingPermissionPreset(serverAccessDevice))
    if ((sensitiveDraft || dirty && hostScope === 'pc' || hostSettingsDraftDirty || pcEdits) && !await confirm({
      title: 'Close or restart TogetherServer?', confirmLabel: 'Continue',
      description: 'Setup and file drafts stay on this PC. Unsaved passwords, scripts, settings, and access changes will be lost.' })) return false
    return true
  }
  beforeLeavingAppRef.current = beforeLeavingApp
  const currentMode = snapshot?.mode
  const currentFriendEndpoint = snapshot?.mode === 'Friend' ? snapshot.endpoint : ''
  const currentFriendConnectionId = snapshot?.mode === 'Friend' ? snapshot.connectionId : ''
  const currentFriendConnectionName = snapshot?.mode === 'Friend' ? snapshot.connectionName : null
  const activitySource = snapshot?.mode === 'Host' ? 'host' :
    snapshot?.mode === 'Friend' ? `friend:${snapshot.connectionId || 'unpaired'}` : 'loading'
  const recentActivity = friendConnectionHidden ? [] : snapshot?.activity ?? []
  const retainedActivity = activityAfterMarker(recentActivity, activityClearMarkers[activitySource])
  const activityHistory = attentionHistory.sources.find(source => source.source === activitySource)
  const visibleActivity = retainedActivity.filter(item => !activityHistory?.dismissedIds.includes(item.id))
  const notificationActivity = visibleActivity.filter(item => !isRoutinePlayerCountActivity(item))
  const recoverySignature = dataRecovery ? JSON.stringify({ lifecycleBlocked: dataRecovery.lifecycleBlocked,
    notices: dataRecovery.notices.map(item => [item.stateFile, item.quarantinedFile, item.detectedUtc]) }) : ''

  useEffect(() => {
    if (!snapshot || !['host', 'join'].includes(workspacePage)) return
    setWorkspacePage(snapshot.mode === 'Host' ? 'host' : 'join')
  }, [snapshot, workspacePage])

  useEffect(() => {
    if (snapshot?.mode !== 'Host') return
    const profiles = snapshot.settings.profiles
    if (!profiles.some(profile => profile.id === selectedHostProfileId)) {
      setSelectedHostProfileId(orderProfiles(profiles, snapshot.runs)[0]?.id ?? '')
      setHostServerTab('overview')
      setHostMobileDetail(false)
    }
  }, [selectedHostProfileId, snapshot])

  useEffect(() => {
    const timer = window.setInterval(() => setNowMs(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])

  useEffect(() => setRecoveryConfirmed(false), [recoverySignature])

  useEffect(() => {
    const clearRevealedValues = () => {
      for (const key of Object.keys(connectionRevealRequestRef.current))
        connectionRevealRequestRef.current[key] = (connectionRevealRequestRef.current[key] ?? 0) + 1
      setRevealedConnections({})
      setRevealedGamePasswords({})
    }
    const handleVisibilityChange = () => { if (document.hidden) clearRevealedValues() }
    window.addEventListener('blur', clearRevealedValues)
    document.addEventListener('visibilitychange', handleVisibilityChange)
    return () => {
      window.removeEventListener('blur', clearRevealedValues)
      document.removeEventListener('visibilitychange', handleVisibilityChange)
    }
  }, [])

  useEffect(() => {
    if (notice || update?.state === 'Available') setNotificationUnread(true)
  }, [notice, update?.state, update?.latestVersion])

  const latestActivityId = notificationActivity[0]?.id ?? null
  const latestActivityWasRead = !!latestActivityId && !!activityHistory?.readIds.includes(latestActivityId)
  useEffect(() => {
    if (latestActivityId) setNotificationUnread(!latestActivityWasRead)
    else setNotificationUnread(false)
  }, [activitySource, latestActivityId, latestActivityWasRead])

  useEffect(() => {
    const availableVersion = update?.state === 'Available' ? update.latestVersion : null
    if (!availableVersion) setShowUpdatePrompt(false)
  }, [update?.latestVersion, update?.state])

  useEffect(() => {
    const controller = new AbortController()
    void getJson('/api/local/desktop/preferences', parseDesktopPreferences, controller.signal)
      .then(setDesktopPreferences)
      .catch(() => { /* The server and Friend controls remain usable. */ })
    return () => controller.abort()
  }, [])

  const saveDesktopPreference = async (preference: { launchAtLogin: boolean } | { closeToTray: boolean }) => {
    setDesktopBusy(true)
    try {
      const result = await changeJson('/api/local/desktop/preferences', 'PUT', parseDesktopPreferenceResult, preference)
      setDesktopPreferences(result.preferences)
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setDesktopBusy(false) }
  }

  const quitApp = async () => {
    if (!await beforeLeavingApp()) return
    try {
      const result = await change('/api/local/quit', 'POST')
      if (!result.ok) setNotice({ good: false, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
  }

  useSingleFlightPolling(async signal => {
    try { setUpdate(await getJson('/api/local/update', parseDesktopUpdateDetails, signal)) }
    catch { /* An update check must not interrupt hosting or joining. */ }
  }, 30 * 60 * 1000)

  const checkUpdate = async (review = true) => {
    setUpdateBusy(true)
    try {
      const result = await changeJson('/api/local/update/check', 'POST', parseDesktopUpdateDetails)
      setUpdate(result)
      if (result.state === 'Available' && review) setShowUpdatePrompt(true)
      if (result.state !== 'Available') setNotice({ good: result.state === 'Current' || result.state === 'NoRelease', text: result.message })
      return result
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setUpdateBusy(false) }
  }

  const installUpdate = async () => {
    if (!await beforeLeavingApp()) return
    setUpdateBusy(true)
    setNotice(null)
    try {
      const result = await change('/api/local/update/install', 'POST')
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setUpdateBusy(false) }
  }

  const dismissUpdatePrompt = () => {
    if (updateBusy || updateReminderBusy) return
    setShowUpdatePrompt(false)
  }

  const reloadUpdate = async () => setUpdate(await getJson('/api/local/update', parseDesktopUpdateDetails))

  const selectHostServer = (id: string, tab?: HostServerTab) => {
    void changeScope(() => {
      setWorkspacePage('host')
      setHostScope('server')
      setSelectedHostProfileId(id)
      setHostServerTab(tab ?? (uiPreferences.serverTabs[id] as HostServerTab | undefined) ?? 'overview')
      setHostMobileDetail(true)
      setUiPreferences(current => ({ ...current, selectedServer: id,
        serverTabs: tab ? { ...current.serverTabs, [id]: tab } : current.serverTabs }))
    })
  }
  const selectHostTab = (tab: HostServerTab) => {
    void changeScope(() => {
      setHostScope('server')
      setWorkspacePage('host')
      setHostServerTab(tab)
      if (selectedHostProfileId) setUiPreferences(current => ({ ...current,
        serverTabs: { ...current.serverTabs, [selectedHostProfileId]: tab } }))
    })
  }
  const confirmOwnerLifecycle = async (profileId: string, action: 'Stop' | 'Restart'): Promise<boolean> => {
    if (snapshot?.mode !== 'Host') return false
    const profile = snapshot.settings.profiles.find(item => item.id === profileId)
    const run = snapshot.runs.find(item => item.profileId === profileId)
    const count = run ? playerCountAvailability(run, Date.now()).count : null
    const players = count !== null ? `${count} player${count === 1 ? '' : 's'}` : 'Player count unavailable'
    return confirm({ title: `${action} ${profile?.name ?? 'this server'}?`, confirmLabel: `${action} server`, destructive: true,
      description: `${players}. Players will be disconnected. TogetherServer asks the game to save and stop, but cannot confirm every save.${action === 'Restart' ? '\nChoose Safe restart to make a backup before starting again.' : ''}` })
  }

  const clearNotificationActivity = () => {
    const marker = recentActivity[0]?.id
    if (!marker) return
    setActivityClearMarkers(current => {
      const next = withActivityClearMarker(current, activitySource, marker)
      writeActivityClearMarkersTo(() => window.localStorage, next)
      return next
    })
    setNotificationUnread(false)
  }

  const refreshServerDirectory = useCallback(async (signal?: AbortSignal) => {
    const startedEpoch = snapshotEpochRef.current
    try {
      const next = await getLocalJson('/api/local/servers', parseServerDirectory, signal)
      if (!signal?.aborted && snapshotEpochRef.current === startedEpoch) {
        setServerDirectory(next); setNowMs(Date.now()); setServerDirectoryError('')
      }
    } catch (error) {
      if (!signal?.aborted && snapshotEpochRef.current === startedEpoch) setServerDirectoryError(errorMessage(error))
    }
  }, [])

  useSingleFlightPolling(async signal => {
    const startedEpoch = snapshotEpochRef.current
    const directoryRead = refreshServerDirectory(signal)
    try {
      const [next, recovery, currentInstance] = await Promise.all([
        readSnapshot(signal),
        getJson('/api/local/data-recovery', parseDataRecoveryView, signal),
        getJson('/api/local/instance', parseAppInstance, signal)
      ])
      // The first Host view includes its PC/access snapshot so actions do not
      // briefly present an empty permission list. Later run/player snapshots
      // never wait on connection diagnostics.
      const initialCompanion = next.mode === 'Host' && companion === null
        ? await getJson('/api/local/companion', parseCompanionInfo, signal) : null
      if (signal.aborted || snapshotEpochRef.current !== startedEpoch) return

      if (initialCompanion) {
        setCompanion(initialCompanion)
        initialCompanionRef.current = { value: initialCompanion, epoch: startedEpoch, receivedAtMs: Date.now() }
        setDeviceNames(names => {
          const updated = { ...names }
          for (const device of initialCompanion.devices) if (updated[device.id] === undefined) updated[device.id] = device.name
          return updated
        })
      }
      setSnapshot(next)
      setNowMs(Date.now())
      setAppInstance(currentInstance)
      setDataRecovery(recovery)
      setLoadError('')
      if (next.mode === 'Host') synchronizeHostSnapshot(next)
    } finally {
      // A slow/erroring directory cannot delay publishing canonical counts,
      // but remains part of this flight before another directory read begins.
      await directoryRead
    }
  }, 3000, error => setLoadError(errorMessage(error)))

  // Connection diagnostics do not delay canonical run/player snapshots. Read
  // them only for the visible Host workspace; quiet tabs need fewer scans.
  useSingleFlightPolling(async signal => {
    const startedEpoch = snapshotEpochRef.current
    const initial = initialCompanionRef.current
    initialCompanionRef.current = null
    const [current, ports] = await Promise.all([
      initial && initial.epoch === startedEpoch && Date.now() - initial.receivedAtMs < 3000
        ? Promise.resolve(initial.value) : getJson('/api/local/companion', parseCompanionInfo, signal),
      getJson('/api/local/network/ports', parsePortDiagnostics, signal)
    ])
    if (signal.aborted || snapshotEpochRef.current !== startedEpoch) return
    setCompanion(current)
    setPortDiagnostics(ports)
    setDeviceNames(names => {
      const updated = { ...names }
      for (const device of current.devices) if (updated[device.id] === undefined) updated[device.id] = device.name
      return updated
    })
  }, hostScope === 'pc' || hostServerTab === 'overview' ? 3000 : 15000,
  error => setLoadError(errorMessage(error)), currentMode === 'Host' && workspacePage === 'host')

  useEffect(() => {
    if (!directoryAction || !snapshot || pending) return
    if (directoryAction === 'new' && snapshot.mode === 'Host') {
      setDirectoryAction(null); setWorkspacePage('host'); setHostScope('server'); addProfile()
    } else if (directoryAction === 'connect' && snapshot.mode === 'Friend') {
      setDirectoryAction(null); setWorkspacePage('join'); setJoinSection('pairing'); setShowPairing(true)
    }
  }, [directoryAction, snapshot, pending, addProfile])

  useEffect(() => {
    if (currentFriendEndpoint)
      setFriendHostAddress(current => current || hostAddress(currentFriendEndpoint))
  }, [currentFriendEndpoint])

  useEffect(() => {
    if (currentMode === 'Friend')
      setFriendConnectionName(currentFriendConnectionName ?? hostAddress(currentFriendEndpoint))
  }, [currentMode, currentFriendConnectionId, currentFriendConnectionName, currentFriendEndpoint])

  useEffect(() => setFriendLogProfileId(''), [currentFriendConnectionId])

  const copyText = async (value: string, label: string, fallback = 'Select and copy it instead.') => {
    try {
      await navigator.clipboard.writeText(value)
      setNotice({ good: true, text: `${label} copied.` })
      setActionNotices(current => ({ ...current, clipboard: { good: true, text: `${label} copied.` } }))
      return true
    } catch {
      setNotice({ good: false, text: `Could not copy ${label.toLowerCase()}. ${fallback}` })
      setActionNotices(current => ({ ...current, clipboard: { good: false, text: `Could not copy ${label.toLowerCase()}. ${fallback}` } }))
      return false
    }
  }
  const hideConnectionDetails = (key: string) => {
    connectionRevealRequestRef.current[key] = (connectionRevealRequestRef.current[key] ?? 0) + 1
    setRevealedConnections(current => {
      if (current[key] === undefined) return current
      const next = { ...current }
      delete next[key]
      return next
    })
    setRevealedGamePasswords(current => {
      if (current[key] === undefined) return current
      const next = { ...current }
      delete next[key]
      return next
    })
  }
  const revealConnectionDetails = (key: string) =>
    setRevealedConnections(current => ({ ...current, [key]: true }))
  const readGamePassword = async (profile: Profile) => {
    const result = await changeJson(`/api/local/profiles/${profile.id}/game-password/reveal`, 'POST', parsePasswordResult)
    if (!result.ok || !result.password)
      throw new Error(result.message ?? 'Could not read the saved game password.')
    return result.password
  }
  const setConnectionBusy = (key: string, action: 'copy' | 'reveal' | null) =>
    setConnectionActivity(current => {
      if (action) return { ...current, [key]: action }
      if (current[key] === undefined) return current
      const next = { ...current }
      delete next[key]
      return next
    })
  const copyGamePassword = async (profile: Profile, key: string) => {
    setConnectionBusy(key, 'copy')
    try {
      const password = await readGamePassword(profile)
      await copyText(password, 'Game password', 'Show the password, then select and copy it instead.')
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setConnectionBusy(key, null) }
  }
  const revealGamePassword = async (profile: Profile, key: string) => {
    const request = (connectionRevealRequestRef.current[key] ?? 0) + 1
    connectionRevealRequestRef.current[key] = request
    setConnectionBusy(key, 'reveal')
    try {
      const password = await readGamePassword(profile)
      if (connectionRevealRequestRef.current[key] !== request || !liveConnectionKeysRef.current.has(key) ||
        document.hidden || !document.hasFocus()) return
      setRevealedGamePasswords(current => ({ ...current, [key]: password }))
      revealConnectionDetails(key)
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setConnectionBusy(key, null) }
  }
  const copyConnectionValue = async (key: string, value: string, label: string) => {
    setConnectionBusy(key, 'copy')
    try { return await copyText(value, label, 'Show the value, then select and copy it instead.') }
    finally { setConnectionBusy(key, null) }
  }
  const detectPublicIp = useCallback(async (signal?: AbortSignal) => {
    if (signal?.aborted) return
    const requestId = ++publicIpRequestRef.current
    const requestEpoch = ++snapshotEpochRef.current
    setDetectingPublicIp(true)
    try {
      const result = await changeJson('/api/local/network/detect-public-ip', 'POST', parsePublicIpDetection, undefined, signal)
      if (signal?.aborted || snapshotEpochRef.current !== requestEpoch) return
      setPublicIpDetection(result)
      if (result.ok && result.address && result.snapshot) {
        applySnapshot(result.snapshot)
        syncDetectedPublicIp(result.address, result.snapshot.settings)
      }
    } catch {
      if (signal?.aborted || snapshotEpochRef.current !== requestEpoch) return
      setPublicIpDetection({ ok: false, code: 'PublicIpUnavailable', address: null,
        message: 'Could not find your public address. Check your internet connection and try again.' })
    } finally {
      if (publicIpRequestRef.current === requestId) setDetectingPublicIp(false)
    }
  }, [applySnapshot, syncDetectedPublicIp])
  useEffect(() => {
    if (currentMode !== 'Host' || appInstance?.isStaging !== false) return
    const controller = new AbortController()
    void detectPublicIp(controller.signal)
    const timer = window.setInterval(() => void detectPublicIp(controller.signal), 15 * 60 * 1000)
    return () => {
      controller.abort()
      window.clearInterval(timer)
    }
  }, [currentMode, appInstance?.isStaging, detectPublicIp])
  const checkPorts = async (announce = false) => {
    if (announce) setCheckingPorts(true)
    const requestEpoch = ++snapshotEpochRef.current
    try {
      const result = await getJson('/api/local/network/ports', parsePortDiagnostics)
      if (snapshotEpochRef.current !== requestEpoch) return
      snapshotEpochRef.current += 1
      setPortDiagnostics(result)
      if (announce) setNotice({ good: true, text: 'Connection details updated.' })
    } catch (error) {
      if (announce) setNotice({ good: false, text: `Could not refresh connection details: ${errorMessage(error)}` })
      /* The regular refresh will retry without replacing the current evidence. */
    } finally {
      if (announce) setCheckingPorts(false)
    }
  }
  const checkInternetRoute = async () => {
    setCheckingInternetRoute(true)
    try {
      setInternetRouteCheck(await changeJson('/api/local/network/test-friend-route', 'POST', parseInternetRouteCheck))
    } catch (error) {
      setInternetRouteCheck({ state: 'Unavailable', detail: `Internet connection test failed: ${errorMessage(error)}`,
        port: draft?.companionPort ?? 0, checkedUtc: new Date().toISOString() })
    } finally { setCheckingInternetRoute(false) }
  }
  const refreshCompanion = async () => {
    const requestEpoch = ++snapshotEpochRef.current
    const result = await getJson('/api/local/companion', parseCompanionInfo)
    if (snapshotEpochRef.current !== requestEpoch) return
    snapshotEpochRef.current += 1
    setCompanion(result)
  }
  const issueInvite = async (profileId: string, refresh = false, useSavedApproval = false): Promise<string | null> => {
    currentOperationProfile.current.invite = profileId
    const serverName = snapshot?.mode === 'Host' ? snapshot.settings.profiles.find(profile => profile.id === profileId)?.name ?? 'this server' : 'this server'
    if (refresh && !await confirm({ title: `Replace the code for ${serverName}?`, confirmLabel: 'Replace code', destructive: true,
      description: 'Every PC that joined with this code will lose access to all its assigned servers and need a new code. Other PCs keep access. To remove just one PC, use Friend access.' })) return null
    const controller = new AbortController()
    const timeout = window.setTimeout(() => controller.abort(), 20_000)
    setPending('invite')
    setNotice(null)
    try {
      let requireApproval = pairingRequireApproval
      const current = await changeJson(`/api/local/servers/${profileId}/invite/current`, 'POST', parseInviteState,
        undefined, controller.signal)
      const canStart = current.canStart
      if (useSavedApproval && current.exists) {
        requireApproval = current.requireApproval
        setPairingRequireApproval(requireApproval)
      }
      const result = await changeJson(`/api/local/servers/${profileId}/invite`, 'POST', parseInviteResult,
        { refresh, canStart, enableConnections: true, durationMinutes: 30, deviceLimit: 1, requireApproval }, controller.signal)
      const listenerWarning = result.ok && result.listenerActive !== true
        ? result.listenerWarning || `The Friend app connection on port ${draft?.companionPort ?? 'the configured port'} did not start. Check Network before sharing this code.`
        : null
      if (!result.ok || !result.password) {
        const message = result.ok ? 'TogetherServer did not return a server code. Try again.' : result.message
        setInviteListenerWarning(message)
        setNotice({ good: false, text: message })
        return null
      }
      setInviteListenerWarning(listenerWarning)
      setNotice({ good: !listenerWarning, text: listenerWarning || result.message })
      setInvitation(result.password)
      await refreshCompanion()
      const host = await readSnapshot()
      if (host.mode === 'Host') {
        applySnapshot(host)
        setDraftIfClean(host.settings)
      }
      await checkPorts()
      return listenerWarning ? null : result.password
    } catch (error) {
      const message = error instanceof DOMException && error.name === 'AbortError'
        ? 'Getting the server code took too long. Try again.' : errorMessage(error)
      setInviteListenerWarning(message)
      setNotice({ good: false, text: message })
      return null
    } finally { window.clearTimeout(timeout); setPending('') }
  }
  const revokeDevice = async (id: string) => {
    setPending(id)
    try {
      const result = await change(`/api/local/devices/${id}/revoke`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(current => ({ ...current, [id]: { good: result.ok, text: result.message } }))
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); setActionNotices(current => ({ ...current, [id]: { good: false, text: errorMessage(error) } })) }
    finally { setPending('') }
  }
  const deleteDevice = async (device: Device) => {
    if (!companion?.devices.some(current => current.id === device.id && current.revoked) ||
        !await confirm({ title: `Delete ${device.name}?`, confirmLabel: 'Delete PC', destructive: true,
          description: 'Delete this saved PC record. It will need a server code to connect again.' })) return
    setPending(device.id)
    try {
      const result = await change(`/api/local/devices/${device.id}`, 'DELETE')
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        setCompanion(current => current ? { ...current, devices: current.devices.filter(item => item.id !== device.id) } : current)
        setDeviceNames(current => { const next = { ...current }; delete next[device.id]; return next })
        setPermissionPresetDraft(current => { const next = { ...current }; delete next[device.id]; return next })
        setActionNotices(current => { const next = { ...current }; delete next[device.id]; return next })
      } else setActionNotices(current => ({ ...current, [device.id]: { good: false, text: result.message } }))
      await refreshCompanion()
    } catch (error) {
      setNotice({ good: false, text: errorMessage(error) })
      setActionNotices(current => ({ ...current, [device.id]: { good: false, text: errorMessage(error) } }))
    } finally { setPending('') }
  }
  const approveDevice = async (id: string) => {
    setPending(id)
    try {
      const result = await change(`/api/local/devices/${id}/approve`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(current => ({ ...current, [id]: { good: result.ok, text: result.message } }))
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); setActionNotices(current => ({ ...current, [id]: { good: false, text: errorMessage(error) } })) }
    finally { setPending('') }
  }
  const saveDeviceName = async (id: string) => {
    setPending(id)
    try {
      const name = (deviceNames[id] ?? '').trim()
      const result = await change(`/api/local/devices/${id}/name`, 'PUT', { name })
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(current => ({ ...current, [id]: { good: result.ok, text: result.message } }))
      if (result.ok) setDeviceNames(current => ({ ...current, [id]: name }))
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); setActionNotices(current => ({ ...current, [id]: { good: false, text: errorMessage(error) } })) }
    finally { setPending('') }
  }
  const setDevicePermissions = async (device: Device, action: PermissionAction, value: boolean) => {
    setPending(device.id)
    try {
      const result = await change(`/api/local/devices/${device.id}/permissions`, 'PUT',
        globalPermissionRequest(device, action, value))
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(current => ({ ...current, [device.id]: { good: result.ok, text: result.message } }))
      if (result.ok) setPermissionPresetDraft(current => { const next = { ...current }; delete next[device.id]; return next })
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); setActionNotices(current => ({ ...current, [device.id]: { good: false, text: errorMessage(error) } })) }
    finally { setPending('') }
  }
  const applyDevicePermissionPreset = async (device: Device, preset: PermissionPreset) => {
    if (preset === 'custom' || device.temporaryHelperActive || device.revoked || !device.paired || device.approvalPending) return
    setPending(device.id)
    try {
      const result = await change(`/api/local/devices/${device.id}/permissions`, 'PUT',
        permissionPresetRequest(preset))
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(current => ({ ...current, [device.id]: { good: result.ok, text: result.message } }))
      if (result.ok) setPermissionPresetDraft(current => ({ ...current, [device.id]: preset }))
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); setActionNotices(current => ({ ...current, [device.id]: { good: false, text: errorMessage(error) } })) }
    finally { setPending('') }
  }
  const setDevicePasswordPermission = async (device: Device, value: boolean) => {
    if (pending || serverAccessDirty || !device.paired || device.revoked || device.approvalPending) return
    setPending(device.id)
    try {
      const result = await change(`/api/local/devices/${device.id}/permissions`, 'PUT', {
        canStart: device.canStart, canStop: device.canStop, canExtendTimer: device.canExtendTimer,
        canViewLogs: device.canViewLogs, canViewPassword: value, scope: 'password'
      })
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(current => ({ ...current, [device.id]: { good: result.ok, text: result.message } }))
      await refreshCompanion()
    } catch { setNotice({ good: false, text: 'Could not save password access. Refresh this PC and try again.' }) }
    finally { setPending('') }
  }
  const saveTemporaryHelper = async (deviceId: string, duration?: 'OneHour' | 'EightHours') => {
    setPending(`temporary-helper-${deviceId}`)
    try {
      const result = await change(`/api/local/devices/${deviceId}/temporary-helper`, 'PUT',
        duration ? { duration } : { clear: true })
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(current => ({ ...current, [deviceId]: { good: result.ok, text: result.message } }))
      await refreshCompanion()
      setPermissionPresetDraft(current => { const next = { ...current }; delete next[deviceId]; return next })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); setActionNotices(current => ({ ...current, [deviceId]: { good: false, text: errorMessage(error) } })) }
    finally { setPending('') }
  }
  const saveDeviceAccessExpiry = async (deviceId: string, request: DeviceAccessExpiryRequest): Promise<DeviceAccessExpiryResult> => {
    setPending(`access-expiry-${deviceId}`)
    try {
      return await putDeviceAccessExpiry(deviceId, request)
    } finally {
      setPending('')
    }
  }
  const saveHostFlags = async (patch: Partial<Pick<Settings, 'companionListeningEnabled' | 'remoteControlsEnabled' | 'autoShutdownEnabled' | 'keepAwakeWhileHosting'>>) => {
    setPending('host-flags')
    setNotice(null)
    try {
      const result = await changeAction('/api/local/settings/control-policy', 'PUT', patch)
      applySnapshot(result.snapshot)
      syncControlPolicy(result.snapshot.settings)
      setNotice({ good: result.ok, text: result.message })
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const pairFriend = async () => {
    if (!friendInvite) {
      setPairIssue({ code: 'InvalidInvite', message: 'Paste the server code from your friend.' })
      return
    }
    setPending('pair')
    setPairIssue(null)
    setNotice(null)
    try {
      const result = await change('/api/local/friend/pair', 'POST',
        { invitation: friendInvite, hostAddress: friendHostAddress || null })
      if (!result.ok) {
        setPairIssue({ code: result.code || 'Disconnected', message: result.message })
        return
      }
      setNotice({ good: true, text: result.message })
      if (result.ok) {
        setFriendInvite('')
        setShowPairing(false)
        setJoinSection('play')
        await changeJson('/api/local/friend/poll', 'POST', parseFriendSnapshot)
        applySnapshot(await readSnapshot())
      }
    } catch (error) {
      setPairIssue({ code: error instanceof ApiError ? error.code : 'LocalAppUnavailable',
        message: `Could not finish connecting to this app: ${errorMessage(error)}` })
    }
    finally { setPending('') }
  }
  const checkFriendConnection = async () => {
    setPending('poll')
    try {
      const next = await changeJson('/api/local/friend/poll', 'POST', parseFriendSnapshot)
      applySnapshot(next)
      setNotice({ good: next.state === 'Connected' || next.state === 'Disabled', text: next.detail })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const selectFriendConnection = async (id: string, epoch = ++navigationEpoch.current) => {
    if (!await beforeChangingScope() || epoch !== navigationEpoch.current) return false
    setPending('select-connection')
    try {
      const result = await change(`/api/local/friend/connections/${id}/select`, 'POST')
      if (epoch !== navigationEpoch.current) return false
      if (!result.ok) { setNotice({ good: false, text: result.message }); return false }
      else {
        const next = await readSnapshot()
        if (epoch !== navigationEpoch.current) return false
        setShowPairing(false)
        setJoinSection('play')
        applySnapshot(next)
        return true
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); return false }
    finally { setPending('') }
  }
  const recoverFriendEndpoint = async () => {
    if (snapshot?.mode !== 'Friend' || !snapshot.connectionId || !recoveryEndpoint.trim()) return
    setPending('recover-endpoint')
    try {
      const result = await change(`/api/local/friend/connections/${snapshot.connectionId}/endpoint`, 'PUT',
        { endpoint: recoveryEndpoint.trim() })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        await changeJson('/api/local/friend/poll', 'POST', parseFriendSnapshot)
        applySnapshot(await readSnapshot())
        setRecoveryEndpoint('')
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const renameFriendConnection = async () => {
    if (snapshot?.mode !== 'Friend' || !snapshot.connectionId) return
    setPending('rename-connection')
    try {
      const result = await change(`/api/local/friend/connections/${snapshot.connectionId}/name`, 'PUT',
        { name: friendConnectionName.trim() })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) applySnapshot(await readSnapshot())
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const forgetFriendConnection = async (connectionId?: string) => {
    if (snapshot?.mode !== 'Friend') return
    const id = connectionId ?? snapshot.connectionId
    const connection = snapshot.connections?.find(item => item.connectionId === id) ?? (snapshot.connectionId === id ? snapshot : null)
    if (!id || !connection ||
        !await confirm({ title: isRemovedHost(connection) ? 'Delete this saved Host?' : 'Forget this saved Host?',
          confirmLabel: isRemovedHost(connection) ? 'Delete Host' : 'Forget Host', destructive: true,
          description: isRemovedHost(connection) ? 'Remove this saved connection from this PC. Connecting again requires a server code.' :
            'TogetherServer will first ask the Host to remove this PC’s access.' })) return
    if (!await beforeChangingScope()) return
    setPending('forget-connection')
    try {
      const result = await change(`/api/local/friend/connections/${id}/forget`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) { setShowPairing(false); applySnapshot(await readSnapshot()); await refreshServerDirectory() }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const probeGameEndpoint = async (profileId: string) => {
    if (snapshot?.mode !== 'Friend') return
    const profile = snapshot.profiles.find(item => item.id === profileId)
    if (!profile) return
    const identity = friendGameCheckIdentity(snapshot, profile)
    setPending(`probe-game-${profileId}`)
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/probe-game`, 'POST', parseGameEndpointResult,
        { connectionId: snapshot.connectionId, runOperationId: profile.runOperationId })
      setGameEndpointResults(current => ({ ...current, [profileId]: result }))
      setGameResultIdentities(current => ({ ...current, [profileId]: identity }))
    } catch (error) {
      setGameEndpointResults(current => ({ ...current, [profileId]: { answered: false,
        code: 'ProbeFailed', message: errorMessage(error), checkedUtc: new Date().toISOString(), onlinePlayers: null, maxPlayers: null } }))
      setGameResultIdentities(current => ({ ...current, [profileId]: identity }))
    } finally { setPending('') }
  }
  const friendAction = async (id: string, action: 'start' | 'stop' | 'restart' | 'replace' | 'extend' | 'refresh') => {
    if (snapshot?.mode !== 'Friend') return
    const selected = snapshot.profiles.find(profile => profile.id === id)
    if (!selected) return
    const permissions = effectiveFriendPermissions(snapshot, selected, Date.now())
    if (action === 'start' || action === 'stop' || action === 'restart' || action === 'extend') {
      if (!permissions.actions[action].available) {
        setNotice({ good: false, text: permissions.actions[action].reason })
        return
      }
    }
    if (action === 'replace' && (!permissions.currentAccess || !permissions.grants.start || !snapshot.remoteControlsEnabled || selected.maintenanceEnabled)) {
      setNotice({ good: false, text: 'Reconnect to the Host and check that you have Start permission.' })
      return
    }
    const key = `friend-${action}-${id}`
    setPending(key)
    if (action === 'stop' || action === 'restart') hideConnectionDetails(`friend-${snapshot?.mode === 'Friend' ? snapshot.connectionId : ''}-${id}-address`)
    try {
      const result = await change(`/api/local/friend/${id}/${action}`, 'POST')
      const next = await readSnapshot()
      if (result.code === 'PlayerCountPending' || action === 'refresh' &&
        (['PlayerCountRefreshed', 'PlayerCountUnavailable'].includes(result.code) || result.code === 'ServerNotReady' && next.mode === 'Friend' &&
          isPlayerCountObservationUnavailable(next.profiles.find(profile => profile.id === id)))) { applySnapshot(next); return }
      const feedback = serverActionNotice(result.ok, result.message, next, id, action)
      setNotice(feedback)
      setActionNotices(current => ({ ...current, [id]: feedback }))
      applySnapshot(next)
    } catch (error) {
      const text = errorMessage(error)
      setNotice({ good: false, text })
      setActionNotices(current => ({ ...current, [id]: { good: false, text } }))
    }
    finally { setPending('') }
  }
  const switchMode = async (mode: 'host' | 'friend') => {
    const epoch = ++navigationEpoch.current
    if ((dirty || sensitiveDraft) && !await confirm({ title: 'Keep a draft and change pages?', confirmLabel: 'Keep draft and continue',
      description: 'Your setup draft stays on this PC. Unsaved passwords, scripts, and advanced settings will be lost.' })) return false
    if (!await beforeChangingScope() || epoch !== navigationEpoch.current) return false
    modeSwitchesInFlight.current += 1
    setPending('mode')
    setNotice(null)
    try {
      const result = await change(`/api/local/mode/${mode}`, 'POST')
      if (epoch !== navigationEpoch.current) return false
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        const next = await readSnapshot()
        if (epoch !== navigationEpoch.current) return false
        if (next.mode !== (mode === 'host' ? 'Host' : 'Friend')) return false
        setRevealedConnections({})
        setRevealedGamePasswords({})
        applySnapshot(next)
        resetForMode(next)
        if (mode === 'host') setHostScope('server')
        else setJoinSection('play')
        setWorkspacePage(mode === 'host' ? 'host' : 'join')
        return true
      }
      return false
    } catch (error) {
      if (epoch === navigationEpoch.current) setNotice({ good: false, text: errorMessage(error) })
      return false
    } finally {
      modeSwitchesInFlight.current -= 1
      if (modeSwitchesInFlight.current === 0) setPending(current => current === 'mode' ? '' : current)
    }
  }
  const acknowledgeDataRecovery = async () => {
    if (snapshot?.mode !== 'Host') return
    if (!recoveryConfirmed) {
      setNotice({ good: false, text: dataRecovery?.lifecycleBlocked
        ? 'Confirm that all servers started by TogetherServer have stopped.'
        : 'Confirm that you reviewed the damaged data set aside for recovery.' })
      return
    }
    setPending('data-recovery')
    setNotice(null)
    try {
      const result = await changeAction('/api/local/data-recovery/acknowledge', 'POST',
        { confirmNoManagedServersRunning: true })
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        setDataRecovery(await getJson('/api/local/data-recovery', parseDataRecoveryView))
        setRecoveryConfirmed(false)
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const run = async (key: string, path: string, method: 'POST' | 'PUT', body?: unknown) => {
    if (dataRecovery?.lifecycleBlocked && (key.startsWith('start-') || key.startsWith('restart-'))) {
      setNotice({ good: false, text: 'Review data recovery before starting or restarting a server.' })
      return
    }
    setPending(key)
    setNotice(null)
    try {
      const result = await changeAction(path, method, body)
      applySnapshot(result.snapshot)
      if (result.ok && key.startsWith('start-') && result.snapshot.mode === 'Host') {
        const continuation = startedSharedServer(result.snapshot, snapshot?.mode === 'Host' ? snapshot : null, key.slice('start-'.length))
        if (continuation) {
          setSelectedHostProfileId(continuation.id); setHostScope('server'); setHostServerTab('overview'); setHostMobileDetail(true)
        }
      }
      if (result.code === 'PlayerCountPending' || key.startsWith('players-') &&
        (['PlayerCountRefreshed', 'PlayerCountUnavailable'].includes(result.code) || result.code === 'ServerNotReady' && result.snapshot.mode === 'Host' &&
          isPlayerCountObservationUnavailable(result.snapshot.runs.find(run => key === `players-${run.profileId}`)))) return
      const profileId = snapshot?.mode === 'Host' ? snapshot.settings.profiles.find(item => path.includes(item.id))?.id : undefined
      const feedback = profileId ? serverActionNotice(result.ok, result.message, result.snapshot, profileId, path.split('/').at(-1) ?? '')
        : { good: result.ok, text: result.message }
      setNotice(feedback)
      if (profileId) setActionNotices(current => ({ ...current, [profileId]: feedback }))
      if (result.ok && key.startsWith('save')) acceptSavedSettings(result.snapshot.settings)
      if (result.ok) void checkPorts()
    } catch (error) {
      setNotice({ good: false, text: errorMessage(error) })
      const profileId = snapshot?.mode === 'Host' ? snapshot.settings.profiles.find(item => path.includes(item.id))?.id : undefined
      if (profileId) setActionNotices(current => ({ ...current, [profileId]: { good: false, text: errorMessage(error) } }))
    } finally { setPending('') }
  }
  const startLocalProfile = async (profile: Profile) => {
    if (pending || dirty || hasUnsavedServerEdits || dataRecovery?.lifecycleBlocked) return
    const source = snapshot?.mode === 'Host' ? originalLocalServer(profile, snapshot.settings.profiles) : null
    await run(`start-${profile.id}`, `/api/local/profiles/${source?.id ?? profile.id}/start`, 'POST')
  }
  const loadBackups = async (profileId: string) => {
    const announce = !pending
    if (announce) setPending(`backups-${profileId}`)
    try {
      const result = await getJson(`/api/local/profiles/${profileId}/backups`, parseWorldBackupList)
      setBackupLists(current => ({ ...current, [profileId]: result }))
      setBackupCatalogRevisions(current => ({ ...current, [profileId]: (current[profileId] ?? 0) + 1 }))
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { if (announce) setPending('') }
  }
  const createManualBackup = async (profileId: string) => {
    setPending(`manual-backup-${profileId}`)
    setNotice(null)
    try {
      const result = await changeAction(`/api/local/profiles/${profileId}/backups/manual`, 'POST')
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(current => ({ ...current, [profileId]: { good: result.ok, text: result.message } }))
      if (result.ok) {
        const list = await getJson(`/api/local/profiles/${profileId}/backups`, parseWorldBackupList)
        setBackupLists(current => ({ ...current, [profileId]: list }))
        setBackupCatalogRevisions(current => ({ ...current, [profileId]: (current[profileId] ?? 0) + 1 }))
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); setActionNotices(current => ({ ...current, [profileId]: { good: false, text: errorMessage(error) } })) }
    finally { setPending('') }
  }
  const safeRestart = async (profileId: string) => {
    const run = snapshot?.mode === 'Host' ? snapshot.runs.find(item => item.profileId === profileId) : undefined
    const count = run ? playerCountAvailability(run, Date.now()).count : null
    const impact = count !== null ? `${count} players` : 'Player count unavailable'
    if (!await confirm({ title: 'Back up and restart?', confirmLabel: 'Safe restart', destructive: true,
      description: `${impact}. Players will be disconnected. TogetherServer stops the server, makes a backup, then starts it again. If the backup fails, it stays stopped.` })) return
    setPending(`safe-restart-${profileId}`)
    setNotice(null)
    try {
      const result = await changeAction(`/api/local/profiles/${profileId}/safe-restart`, 'POST')
      applySnapshot(result.snapshot)
      const feedback = serverActionNotice(result.ok, result.message, result.snapshot, profileId, 'safe-restart')
      setNotice(feedback)
      setActionNotices(current => ({ ...current, [profileId]: feedback }))
      const list = await getJson(`/api/local/profiles/${profileId}/backups`, parseWorldBackupList)
      setBackupLists(current => ({ ...current, [profileId]: list }))
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); setActionNotices(current => ({ ...current, [profileId]: { good: false, text: errorMessage(error) } })) }
    finally { setPending('') }
  }
  const verifyBackup = async (profileId: string, backupId: string) => {
    currentOperationProfile.current[`verify-backup-${backupId}`] = profileId
    setPending(`verify-backup-${backupId}`)
    setNotice(null)
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/backups/${backupId}/verify`, 'POST',
        parseWorldBackupVerificationResult)
      setNotice({ good: result.ok, text: result.message })
      if (!result.ok) throw new Error(result.message)
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); throw error }
    finally { setPending('') }
  }
  const copyBackupToVault = async (profileId: string, backupId: string) => {
    currentOperationProfile.current[`vault-backup-${backupId}`] = profileId
    setPending(`vault-backup-${backupId}`)
    setNotice(null)
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/backups/${backupId}/vault`, 'POST',
        parseBackupSafetyResult)
      if (result.code !== 'Canceled') setNotice({ good: result.ok, text: result.message })
      if (!result.ok && result.code !== 'Canceled') throw new Error(result.message)
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); throw error }
    finally { setPending('') }
  }
  const prepareMoveKit = async (profileId: string, backupId: string) => {
    currentOperationProfile.current[`move-kit-${backupId}`] = profileId
    setPending(`move-kit-${backupId}`)
    setNotice(null)
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/backups/${backupId}/move-kit`, 'POST',
        parseHostMoveKitResult)
      if (result.code !== 'Canceled') setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const inspectMoveKit = async () => {
    setPending('inspect-move-kit')
    setNotice(null)
    try {
      const result = await changeJson('/api/local/move-kit/inspect', 'POST', parseHostMoveKitResult)
      setMoveKit(result.ok ? result : null)
      if (result.code !== 'Canceled') setNotice({ good: result.ok, text: result.message })
    } catch (error) { setMoveKit(null); setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const rehearseBackupRestore = async (profileId: string, backupId: string) => {
    currentOperationProfile.current[`rehearse-backup-${backupId}`] = profileId
    setPending(`rehearse-backup-${backupId}`)
    setNotice(null)
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/backups/${backupId}/rehearse`, 'POST',
        parseBackupSafetyResult)
      setNotice({ good: result.ok, text: result.message })
      if (!result.ok) throw new Error(result.message)
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); throw error }
    finally { setPending('') }
  }
  const restoreBackup = async (profileId: string, backupId: string) => {
    setPending(`restore-${profileId}`)
    setNotice(null)
    try {
      const result = await changeAction(`/api/local/profiles/${profileId}/backups/${backupId}/restore`, 'POST')
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      await loadBackups(profileId)
      if (!result.ok) throw new Error(result.message)
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); throw error }
    finally { setPending('') }
  }
  const customCertificationAction = async (profileId: string, action: 'begin' | 'status' | 'confirm' | 'cancel' | 'revoke') => {
    const key = `certification-${action}-${profileId}`
    setPending(key)
    setNotice(null)
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/custom-certification/${action}`, 'POST', parseCustomCertificationResult)
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) void checkPorts()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const prepareDeviceServerAccess = (device: Device) => {
    const savedIds = new Set(snapshot?.mode === 'Host' ? snapshot.settings.profiles.map(profile => profile.id) : [])
    const ids = device.assignedProfileIds.filter(id => savedIds.has(id))
    const permissions = Object.fromEntries([...savedIds].map(profileId => {
      const permission = devicePermission(device, profileId)
      return [profileId, { canStart: permission.canStart, canStop: permission.canStop,
        canExtendTimer: permission.canExtendTimer, canViewLogs: permission.canViewLogs, canViewPassword: devicePasswordPermission(device, profileId) }]
    }))
    setServerAccessSearch('')
    setServerAccessDraft(ids)
    setServerPermissionDraft(permissions)
    setServerAccessOriginal(serverAccessKey(ids, permissions))
  }
  const openDeviceServerAccess = (device: Device) => {
    setNotice(null)
    prepareDeviceServerAccess(device)
    setDeviceNames(current => ({ ...current, [device.id]: device.name }))
    setPermissionPresetDraft(current => ({ ...current, [device.id]: matchingPermissionPreset(device) }))
    setDeviceAccessTab('permissions')
    setDiscardDeviceEdits(false)
    setRemoveDeviceConfirm(false)
    setRemoveServerConfirm(false)
    const profile = savedProfiles.find(profile => profile.id ===
      (deviceFilters.serverId === 'all' ? selectedHostProfile?.id : deviceFilters.serverId) && device.assignedProfileIds.includes(profile.id))
    setServerRemovalScope(profile ? { deviceId: device.id, profileId: profile.id, name: profile.name } : null)
    setDeadlineDraftState({ dirty: false, saving: false })
    setServerAccessDeviceId(device.id)
  }
  const finishDeviceServerAccess = () => {
    if (pending || deadlineDraftState.saving) return
    setServerAccessDeviceId('')
    setServerAccessDraft([])
    setServerPermissionDraft({})
    setServerAccessSearch('')
    setDiscardDeviceEdits(false)
    setRemoveDeviceConfirm(false)
    setRemoveServerConfirm(false)
    setServerRemovalScope(null)
    setNotice(null)
  }
  const closeDeviceServerAccess = () => {
    if (pending || deadlineDraftState.saving) return
    if (serverAccessDirty || deadlineDraftState.dirty || serverAccessDevice && ((deviceNames[serverAccessDevice.id] ?? serverAccessDevice.name).trim() !== serverAccessDevice.name ||
      (permissionPresetDraft[serverAccessDevice.id] ?? matchingPermissionPreset(serverAccessDevice)) !== matchingPermissionPreset(serverAccessDevice))) {
      setDiscardDeviceEdits(true)
      return
    }
    finishDeviceServerAccess()
  }
  const saveDeviceServerAccess = async () => {
    if (!serverAccessDeviceId || !serverAccessDevice || serverAccessDevice.temporaryHelperActive || serverAccessDevice.revoked ||
      !serverAccessDevice.paired || serverAccessDevice.approvalPending || !serverAccessDirty) return
    setPending(serverAccessDeviceId)
    try {
      const result = await change(`/api/local/devices/${serverAccessDeviceId}/servers`, 'PUT',
        { profileIds: serverAccessDraft, permissions: serverAccessDraft.map(profileId => ({
          profileId,
          canStart: serverPermissionDraft[profileId]?.canStart ?? serverAccessDevice?.canStart ?? false,
          canStop: serverPermissionDraft[profileId]?.canStop ?? serverAccessDevice?.canStop ?? false,
          canExtendTimer: serverPermissionDraft[profileId]?.canExtendTimer ?? serverAccessDevice?.canExtendTimer ?? false,
          canViewLogs: serverPermissionDraft[profileId]?.canViewLogs ?? serverAccessDevice?.canViewLogs ?? false,
          canViewPassword: serverPermissionDraft[profileId]?.canViewPassword ?? devicePasswordPermission(serverAccessDevice, profileId)
        })) })
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(current => ({ ...current, [serverAccessDeviceId]: { good: result.ok, text: result.message } }))
      await refreshCompanion()
      if (result.ok) {
        setServerAccessOriginal(serverAccessKey(serverAccessDraft, serverPermissionDraft))
        setDiscardDeviceEdits(false)
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const removeSelectedServerAccess = async () => {
    const target = currentServerRemovalScope.current
    if (!target || target.deviceId !== serverAccessDeviceId || pending || removingServerAccess.current ||
      serverAccessHasDraft || deadlineDraftState.saving || snapshot?.mode !== 'Host') return
    removingServerAccess.current = true
    const navigation = navigationEpoch.current
    setPending(target.deviceId)
    try {
      const [current, info] = await Promise.all([readSnapshot(), getJson('/api/local/companion', parseCompanionInfo)])
      if (currentServerRemovalScope.current !== target || navigationEpoch.current !== navigation) return
      const device = info.devices.find(device => device.id === target.deviceId && !device.revoked && device.paired)
      if (current.mode !== 'Host' || !current.settings.profiles.some(profile => profile.id === target.profileId) || !device)
        throw new Error('This PC or server changed. Refresh access before removing it.')
      // The fixed scoped action rechecks canonical state atomically. Never send
      // an older full assignment/permission list over another server's changes.
      const result = await change(`/api/local/devices/${target.deviceId}/servers/${target.profileId}/access`, 'DELETE')
      if (currentServerRemovalScope.current !== target) return
      await refreshCompanion()
      if (result.ok) finishDeviceServerAccess()
      setNotice({ good: result.ok, text: result.message })
      setActionNotices(notices => ({ ...notices, [target.deviceId]: { good: result.ok, text: result.message } }))
    } catch (error) {
      if (currentServerRemovalScope.current === target) setNotice({ good: false, text: errorMessage(error) })
    } finally { removingServerAccess.current = false; setPending('') }
  }
  const extendCountdown = async (profileId: string, explicitMinutes?: number) => {
    const minutes = explicitMinutes ?? Number(countdownExtensions[profileId] ?? '15')
    if (!Number.isSafeInteger(minutes) || minutes < 1 || minutes > 1440) {
      setNotice({ good: false, text: 'Enter a whole number of extra minutes from 1 to 1440.' })
      return
    }
    await run(`extend-${profileId}`, `/api/local/profiles/${profileId}/countdown/extend`, 'POST', { minutes })
  }

  const saveMaintenance = async (profile: Profile, enabled: boolean) => {
    if (snapshot?.mode !== 'Host' || dirty) return
    const message = (maintenanceMessages[profile.id] ?? profile.maintenance?.message ?? '').trim()
    setPending(`maintenance-${profile.id}`)
    try {
      const next = { ...snapshot.settings, profiles: snapshot.settings.profiles.map(item => item.id === profile.id
        ? { ...item, maintenance: { enabled, message } } : item) }
      const result = await changeAction('/api/local/settings', 'PUT', next)
      applySnapshot(result.snapshot)
      acceptSavedSettings(result.snapshot.settings)
      setNotice({ good: result.ok, text: result.ok
        ? enabled ? 'Maintenance is on. Friends can see the server but cannot control it.' : 'Maintenance is off.'
        : result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); setActionNotices(current => ({ ...current, [profile.id]: { good: false, text: errorMessage(error) } })) }
    finally { setPending('') }
  }

  const saveSettingsPatch = async (patch: HostSettingsPatch): Promise<boolean> => {
    if (snapshot?.mode !== 'Host' || pending || dirty) return false
    setPending('save-settings')
    setNotice(null)
    try {
      const result = await changeAction('/api/local/settings', 'PUT', { ...snapshot.settings, ...patch })
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        acceptSavedSettings(result.snapshot.settings)
        void checkPorts()
        return true
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
    return false
  }
  const saveWorldProtection = async (profileId: string, expected: ProtectionPolicy, next: ProtectionPolicy): Promise<boolean> => {
    if (snapshot?.mode !== 'Host' || pending || dirty || dataRecovery?.lifecycleBlocked ||
        snapshot.runs.find(run => run.profileId === profileId)?.state !== 'Offline') return false
    const settings = applyProtectionPolicy(snapshot.settings, profileId, expected, next)
    if (!settings) {
      setNotice({ good: false, text: 'These settings changed. Reopen the editor to see the latest values.' })
      return false
    }
    setPending(`protection-${profileId}`)
    try {
      const result = await changeAction(`/api/local/profiles/${profileId}/protection`, 'PUT', { expected, next })
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.ok ? 'Protection settings saved.' : result.message })
      if (result.ok) acceptSavedSettings(result.snapshot.settings)
      return result.ok
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }); return false }
    finally { setPending('') }
  }
  const certificateAction = async (action: 'stage' | 'activate' | 'retire-previous') => {
    setPending(`certificate-${action}`)
    try {
      const result = await change(`/api/local/companion/certificate/${action}`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const openHostSettings = (section: HostPcSection = 'access', accessProfileId?: string) => {
    void changeScope(() => {
      setNotice(null)
      setHostSettingsSection(section)
      setHostScope('pc')
      setWorkspacePage('host')
      if (section === 'access') setDeviceFilters(current => ({ ...current,
        serverId: accessProfileId && savedProfiles.some(profile => profile.id === accessProfileId) ? accessProfileId : 'all' }))
    })
    if (!routeDiscovery) void getJson('/api/local/network/routes', parseRouteDiscovery)
      .then(setRouteDiscovery)
      .catch(() => { /* Manual route entry remains available. */ })
  }
  const closeHostSettings = async () => {
    if (!snapshot) return
    if (snapshot.mode === 'Host' && dirty && !await confirm({ title: 'Discard unsaved advanced settings?',
      description: 'The saved settings remain in place.', confirmLabel: 'Discard changes', destructive: true })) return
    void changeScope(() => {
      if (snapshot.mode === 'Host') acceptSavedSettings(snapshot.settings)
      setNotice(null)
      setHostScope('server')
      setWorkspacePage(snapshot.mode === 'Host' ? 'host' : 'join')
    })
  }
  const inviteFriend = async (profileId: string) => {
    const request = ++inviteLoad.current
    setInviteProfileId(profileId)
    setInvitation('')
    setInviteListenerWarning(null)
    try {
      // Reissuing without refresh keeps the current code and also starts the HTTPS listener.
      const ready = await issueInvite(profileId, false, true)
      if (ready && request === inviteLoad.current) {
        try {
          await navigator.clipboard.writeText(ready)
          setNotice({ good: true, text: 'Server code copied. Send it privately to your friends.' })
        }
        catch { setNotice({ good: false, text: 'The server code is ready, but it could not be copied. Choose Copy code.' }) }
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
  }

  const selectJoinSection = (section: JoinSection) => {
    void changeScope(() => {
      setJoinSection(section === 'network' ? 'connection' : section)
      setShowPairing(section === 'pairing')
      const profileId = selectedFriendProfile?.id ?? ''
      if (section === 'chat') setFriendChatProfileId(profileId)
      if (section === 'logs') setFriendLogProfileId(profileId)
    })
  }

  const navigateWorkspace = (page: WorkspacePage) => {
    if (!requiredSetup.ready) return
    if (page === 'host' && snapshot?.mode === 'Friend') page = 'join'
    if (page === 'attention' || page === 'settings' ||
      (page === 'host' && snapshot?.mode === 'Host' && modeSwitchesInFlight.current === 0) ||
      (page === 'join' && snapshot?.mode === 'Friend' && modeSwitchesInFlight.current === 0)) {
      void changeScope(() => {
        if (page === 'attention') setNotificationUnread(false)
        if (page === 'settings') jumpToAppSection('profile')
        if (page === 'host') setHostScope('server')
        setWorkspacePage(page)
      })
      return
    }
    void switchMode(page === 'host' ? 'host' : 'friend')
  }
  workspaceNavigationRef.current = navigateWorkspace

  const requestDirectoryAction = async (action: 'new' | 'connect') => {
    if (pending || directorySelectionInFlight.current) return
    directorySelectionInFlight.current = true
    let epoch = ++navigationEpoch.current
    try {
      const mode = action === 'new' ? 'Host' : 'Friend'
      if (snapshot?.mode !== mode) {
        if (!await switchMode(mode === 'Host' ? 'host' : 'friend')) return
        epoch = navigationEpoch.current
      } else if (!await beforeChangingScope()) return
      if (epoch !== navigationEpoch.current) return
      setDirectoryAction(action)
    } finally { directorySelectionInFlight.current = false }
  }
  connectServerRef.current = () => void requestDirectoryAction('connect')

  const selectDirectoryServer = async (target: ServerSelection) => {
    if (pending || directorySelectionInFlight.current) return
    directorySelectionInFlight.current = true
    let epoch = ++navigationEpoch.current
    try {
      if (target.kind === 'local') {
        if (snapshot?.mode !== 'Host') { if (!await switchMode('host')) return; epoch = navigationEpoch.current }
        else if (!await beforeChangingScope()) return
        if (epoch !== navigationEpoch.current) return
        setWorkspacePage('host'); setHostScope('server'); setSelectedHostProfileId(target.profileId); setHostServerTab('overview')
        setUiPreferences(current => ({ ...current, selectedServer: target.profileId }))
      } else {
        if (snapshot?.mode !== 'Friend') { if (!await switchMode('friend')) return; epoch = navigationEpoch.current }
        const current = await readSnapshot()
        if (current.mode !== 'Friend' || epoch !== navigationEpoch.current) return
        if (current.connectionId !== target.connectionId) {
          if (!await selectFriendConnection(target.connectionId, epoch)) return
        } else if (!await beforeChangingScope()) return
        if (epoch !== navigationEpoch.current) return
        const chosenHost = directoryWithSnapshot(serverDirectory, snapshot).friendHosts.find(host => host.connectionId === target.connectionId)
        setShowRemovedHosts(chosenHost?.state === 'Revoked' || chosenHost?.connectionCode === 'Revoked')
        setWorkspacePage('join'); setShowPairing(false); setJoinSection('play')
        setSelectedFriendProfileId(target.profileId); setFriendChatProfileId(target.profileId); setFriendLogProfileId(target.profileId)
      }
      void refreshServerDirectory()
    } finally { directorySelectionInFlight.current = false }
  }

  const openActivityDestination = (destination: ActivityDestination) => {
    if (snapshot?.mode !== 'Host' || (destination.profileId &&
      !snapshot.settings.profiles.some(profile => profile.id === destination.profileId))) return
    void changeScope(() => {
      if (destination.profileId) {
        setSelectedHostProfileId(destination.profileId)
        setUiPreferences(current => ({ ...current, selectedServer: destination.profileId!,
          serverTabs: destination.workspace === 'host' ? { ...current.serverTabs, [destination.profileId!]: destination.section } : current.serverTabs }))
      }
      if (destination.workspace === 'settings') {
        setHostSettingsSection(destination.section)
        setHostScope('pc')
        setWorkspacePage('host')
        return
      }
      setHostServerTab(destination.section)
      setHostScope('server')
      setHostMobileDetail(true)
      setWorkspacePage('host')
    })
  }
  const openActivity = (item: NonNullable<Snapshot['activity']>[number]) => {
    const destination = activityDestination(item)
    if (destination) openActivityDestination(destination)
  }
  const openFriendConnectionDoctor = () => {
    friendDoctorFocusRequested.current = true
    void changeScope(() => { setJoinSection('connection'); setFriendTroubleshootingOpen(true); setWorkspacePage('join') })
  }
  const openFriendServer = (profileId: string) => {
    if (snapshot?.mode !== 'Friend' || !snapshot.profiles.some(profile => profile.id === profileId)) return
    void changeScope(() => {
      setWorkspacePage('join')
      setSelectedFriendProfileId(profileId)
      setJoinSection('play')
      setShowPairing(false)
      window.requestAnimationFrame(() => document.getElementById(`friend-server-${profileId}`)?.scrollIntoView({ block: 'start' }))
    })
  }
  notificationNavigationRef.current = target => {
    if (!requiredSetup.ready) return
    void (async () => {
      if (!await beforeChangingScope()) return
      const destination = workspaceDestination(target)
      const expectedMode = destination.workspace === 'host' ? 'Host' : destination.workspace === 'join' ? 'Friend' : null
      if (expectedMode && snapshot?.mode !== expectedMode && !await switchMode(expectedMode === 'Host' ? 'host' : 'friend')) return
      let current = await readSnapshot()
      if (target.workspace === 'join') {
        if (current.mode !== 'Friend' || !target.connectionId || !current.connections?.some(connection => connection.connectionId === target.connectionId)) return
        if (current.connectionId !== target.connectionId) {
          if (!await selectFriendConnection(target.connectionId)) return
          current = await readSnapshot()
        }
        if (current.mode !== 'Friend' || current.connectionId !== target.connectionId ||
          (target.profileId && !current.profiles.some(profile => profile.id === target.profileId))) return
        const selectedProfile = current.profiles.find(profile => profile.id === target.profileId)
        if (target.section === 'logs' && (!selectedProfile || !friendLogAvailability(selectedProfile.canViewLogs, current.hostCapabilities).visible)) return
        applySnapshot(current)
        await changeScope(() => {
          setWorkspacePage('join')
          setShowPairing(false)
          if (destination.workspace === 'join') setJoinSection(destination.section)
          if (target.profileId) setSelectedFriendProfileId(target.profileId)
          if (target.section === 'chat' && target.profileId) setFriendChatProfileId(target.profileId)
          if (target.section === 'logs' && target.profileId) setFriendLogProfileId(target.profileId)
          window.requestAnimationFrame(() => {
            const selector = target.section === 'network' ? '.friend-connection-doctor' : target.section === 'shared-saves' ? '.friend-shared-worlds' : null
            const container = target.profileId ? document.getElementById(`friend-server-${target.profileId}`) : document
            const destination = target.section === 'network' ? document.querySelector(selector!)
              : selector ? container?.querySelector(selector) : container instanceof Element ? container : null
            if (destination instanceof HTMLDetailsElement) destination.open = true
            destination?.scrollIntoView({ block: 'start' })
            if (destination instanceof HTMLDetailsElement) destination.querySelector('summary')?.focus()
          })
        })
      } else {
        if (destination.workspace === 'host' && target.profileId && (current.mode !== 'Host' || !current.settings.profiles.some(profile => profile.id === target.profileId))) return
        applySnapshot(current)
        await changeScope(() => {
          if (destination.workspace === 'host') {
            if (current.mode !== 'Host') return
            if (target.profileId) setSelectedHostProfileId(target.profileId)
            setHostScope(destination.scope)
            if (destination.scope === 'pc') setHostSettingsSection(destination.section as HostPcSection)
            else setHostServerTab(destination.section as HostServerTab)
            if (target.profileId && destination.scope === 'server') setUiPreferences(current => ({ ...current, selectedServer: target.profileId!,
              serverTabs: { ...current.serverTabs, [target.profileId!]: target.section } }))
            setHostMobileDetail(true)
            setWorkspacePage('host')
          } else {
            if (destination.workspace === 'settings') jumpToAppSection(destination.section)
            setWorkspacePage('settings')
          }
        })
      }
    })().catch(error => setNotice({ good: false, text: errorMessage(error) }))
  }

  const detectedGameIp = snapshot?.mode === 'Host' && snapshot.settings.publicGameIpCheckedUtc &&
    Date.now() - Date.parse(snapshot.settings.publicGameIpCheckedUtc) < 60 * 60 * 1000
    ? snapshot.settings.publicGameIp : ''
  const friendAppAddress = hostAddress(draft?.companionEndpoint ?? '') ||
    (detectedGameIp ? `${detectedGameIp}${draft?.companionPort === 5131 ? '' : `:${draft?.companionPort}`}` : '')
  const savedProfiles = snapshot?.mode === 'Host' ? orderProfiles(snapshot.settings.profiles, snapshot.runs,
    uiPreferences.favoriteServers, uiPreferences.serverOrder) : []
  const friendProfiles = snapshot?.mode === 'Friend' && !friendConnectionHidden ? orderProfiles(snapshot.profiles) : []
  const liveConnectionKeySignature = JSON.stringify((snapshot?.mode === 'Host'
    ? snapshot.runs.filter(run => run.state === 'Ready' && detectedGameIp)
      .flatMap(run => [`host-${run.profileId}-address`, `host-${run.profileId}-password`])
    : snapshot?.profiles.filter(profile => profile.state === 'Ready' && profile.joinAddress)
      .map(profile => `friend-${snapshot.connectionId}-${profile.id}-address`) ?? []).sort())
  useEffect(() => {
    const liveKeys = new Set<string>(JSON.parse(liveConnectionKeySignature) as string[])
    liveConnectionKeysRef.current = liveKeys
    setRevealedConnections(current => {
      let changed = false
      const next: Record<string, boolean> = {}
      for (const [key, value] of Object.entries(current)) {
        if (liveKeys.has(key)) next[key] = value
        else changed = true
      }
      return changed ? next : current
    })
    setRevealedGamePasswords(current => {
      let changed = false
      const next: Record<string, string> = {}
      for (const [key, value] of Object.entries(current)) {
        if (liveKeys.has(key)) next[key] = value
        else changed = true
      }
      return changed ? next : current
    })
  }, [liveConnectionKeySignature])
  const serverAccessDevice = companion?.devices.find(device => device.id === serverAccessDeviceId && !device.revoked)
  const passwordAccessServers = savedProfiles.filter(profile => profile.kind === 'Valheim' && serverAccessDevice?.assignedProfileIds.includes(profile.id))
  const passwordAccessCount = serverAccessDevice ? passwordAccessServers.filter(profile => devicePasswordPermission(serverAccessDevice, profile.id)).length : 0
  const passwordAccessMixed = !!serverAccessDevice && passwordAccessServers.some(profile => devicePasswordPermission(serverAccessDevice, profile.id) !== (serverAccessDevice.canViewPassword ?? false))
  const serverAccessDirty = !!serverAccessDeviceId && serverAccessKey(serverAccessDraft, serverPermissionDraft) !== serverAccessOriginal
  const serverAccessHasDraft = serverAccessDirty || deadlineDraftState.dirty || !!serverAccessDevice &&
    ((deviceNames[serverAccessDevice.id] ?? serverAccessDevice.name).trim() !== serverAccessDevice.name ||
      (permissionPresetDraft[serverAccessDevice.id] ?? matchingPermissionPreset(serverAccessDevice)) !== matchingPermissionPreset(serverAccessDevice))
  const normalizedServerSearch = serverAccessSearch.trim().toLocaleLowerCase()
  const visibleServerAccessProfiles = savedProfiles.filter(profile => !normalizedServerSearch ||
    `${profile.name} ${profileGameLabel(profile)}`.toLocaleLowerCase().includes(normalizedServerSearch))
  const directory = directoryWithSnapshot(serverDirectory, snapshot)
  const activeRuns = directory.localServers.filter(run => ['Process running', 'Starting', 'Listening', 'Ready'].includes(run.state)).length
  const currentRouteResult = !dirty && companion?.listenerActive
    ? currentOutsideResult(portDiagnostics?.control, internetRouteCheck) : null
  const previousRouteVerdict = !currentRouteResult &&
    (internetRouteCheck?.state === 'Reachable' || internetRouteCheck?.state === 'Not reachable')
  const activeInviteWarning = inviteListenerWarning || (invitation && companion?.listenerActive === false
    ? companion.listenerWarning || 'Friend connections are off. Choose Invite friends to turn them on.'
    : null)
  const friendAppStatus = portDiagnostics?.control.remoteState === 'Friend connected' ? 'Friend connected'
    : currentRouteResult?.state === 'Reachable' ? 'reachable from the internet; Friend connection untested'
      : companion?.listenerActive ? 'open on this PC; internet access untested'
        : companion?.listenerState === 'Idle' ? 'waiting for a server code'
          : companion?.listenerWarning ? 'needs attention' : 'off'
  const developmentControlPort = snapshot?.mode === 'Host'
    ? snapshot.settings.companionPort : appInstance?.companionPort
  const updateBlockedReason = dirty ? 'Save setup changes before updating.' :
    activeRuns > 0 ? 'Stop hosted servers before updating.' : undefined
  const selectedHostProfile = savedProfiles.find(profile => profile.id === selectedHostProfileId) ?? savedProfiles[0]
  const selectedFriendProfile = friendProfiles.find(profile => profile.id === selectedFriendProfileId) ?? friendProfiles[0]
  const selectedHostRun = snapshot?.mode === 'Host' && selectedHostProfile
    ? snapshot.runs.find(run => run.profileId === selectedHostProfile.id) : undefined
  const storageWarnings = snapshot?.mode === 'Host' ? snapshot.storageHealth?.warnings ?? [] : []
  const hostOverviewRows = snapshot?.mode === 'Host' ? allServerRows(snapshot, nowMs) : []
  const currentProblems = hostOverviewRows.filter(row => row.priority > 20)
  const visibleDevices = filterFriendPcs(companion?.devices ?? [], savedProfiles, deviceFilters, nowMs)
  const attentionCount = notificationActivity.length + storageWarnings.length + (notice ? 1 : 0) + (update?.state === 'Available' ? 1 : 0)
  const groupedNotifications = collapseRepeatedActivity(notificationActivity)
  const commands: WorkspaceCommand[] = [
    { id: 'nav-host', label: 'Open Servers', detail: 'Your servers and your friends’ servers', icon: 'server', keywords: 'Alt+1 host join', run: () => navigateWorkspace(snapshot?.mode === 'Friend' ? 'join' : 'host') },
    { id: 'nav-join', label: 'Connect server', detail: 'Use a friend’s server code', icon: 'link', keywords: 'Alt+2', run: () => void requestDirectoryAction('connect') },
    { id: 'nav-attention', label: 'Open Attention', detail: `${attentionCount} current item${attentionCount === 1 ? '' : 's'}`, icon: 'bell', keywords: 'notifications activity Alt+3', run: () => navigateWorkspace('attention') },
    { id: 'nav-settings', label: 'Open App Settings', detail: 'Appearance and app settings', icon: 'settings', keywords: 'Alt+4 preferences', run: () => navigateWorkspace('settings') },
    { id: 'open-guides', label: 'Open setup guides', detail: 'Walk through hosting or joining a server', icon: 'plug', keywords: 'help tutorial tour walkthrough first time',
      run: () => void changeScope(gettingStarted.open) },
    { id: 'add-server', label: 'Add a server', detail: 'Set up a server on this PC', icon: 'server', disabled: snapshot?.mode !== 'Host' || !!pending || dirty,
      run: () => void changeScope(() => { setWorkspacePage('host'); addProfile() }) },
    { id: 'refresh-connections', label: 'Refresh connection details', detail: 'Run the existing read-only Host checks', icon: 'refresh', disabled: snapshot?.mode !== 'Host' || !!pending,
      run: () => void changeScope(() => { setWorkspacePage('host'); void checkPorts(true) }) },
    { id: 'open-friend-access', label: 'Open Friend access', detail: 'Manage connected PCs and what they can do', icon: 'invite', disabled: snapshot?.mode !== 'Host' || savedProfiles.length === 0,
      run: () => openHostSettings('access') },
    { id: 'open-diagnostics', label: 'Open diagnostics', detail: 'Read-only preflight checks and redacted support export', icon: 'warning',
      keywords: 'support preflight report', disabled: snapshot?.mode !== 'Host', run: () => openHostSettings('diagnostics') },
    { id: 'guarded-lifecycle', label: `${selectedHostRun?.state === 'Offline' ? 'Start' : 'Stop'} selected server`,
      detail: 'Open Overview and use the server button there', icon: selectedHostRun?.state === 'Offline' ? 'play' : 'stop',
      disabled: !selectedHostProfile, run: () => { if (selectedHostProfile) selectHostServer(selectedHostProfile.id, 'overview') } }
  ]
  for (const profile of savedProfiles) {
    for (const tab of ['overview', 'chat', 'players', 'logs', 'sessions', 'backups', 'files', 'setup'] as HostServerTab[]) {
      commands.push({ id: `server-${profile.id}-${tab}`, label: `${profile.name} · ${hostServerTabLabels[tab]}`, detail: `${profileGameLabel(profile)} · open ${hostServerTabLabels[tab]}`,
        icon: 'server', keywords: `${profile.worldId} ${profile.kind} ${tab}`, run: () => selectHostServer(profile.id, tab) })
    }
  }
  const prepareGameRehearsal = async (profileId: string, backupId: string) => {
    setPending(`game-rehearsal-${profileId}`)
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/world-load/prepare`, 'POST', parseWorldLoadResult, { backupId })
      setNotice({ good: result.ok, text: result.message })
      if (!result.ok) throw new Error(result.message)
      applySnapshot(await readSnapshot())
    } finally { setPending('') }
  }
  const browseSharedServerFile = async (profileId: string, game: string): Promise<string | null> => {
    const result = await changeJson('/api/local/shared-worlds/browse-server-file', 'POST', value => {
      if (typeof value !== 'object' || value === null) throw new Error('The server selection could not be read.')
      const selection = value as { ok?: unknown; path?: unknown; message?: unknown }
      if (selection.ok !== true) return null
      if (typeof selection.path !== 'string') throw new Error('The server selection did not return a local path.')
      return selection.path
    }, { profileId, kind: game })
    return result
  }
  const openSharedHost = async (profileId: string, setup: boolean, gameSetup = false) => {
    try {
      if (snapshot?.mode !== 'Host' && !await switchMode('host')) return
      const current = await readSnapshot()
      applySnapshot(current)
      if (current.mode !== 'Host') return
      if (current.settings.profiles.some(profile => profile.id === profileId)) {
        await changeScope(() => {
          setSelectedHostProfileId(profileId)
          setHostServerTab('overview')
          setUiPreferences(preferences => ({ ...preferences, selectedServer: profileId,
            serverTabs: { ...preferences.serverTabs, [profileId]: 'overview' } }))
          setHostScope(setup ? 'pc' : 'server')
          if (setup) setHostSettingsSection('network')
          setWorkspacePage('host')
          if (gameSetup) { openSetup(profileId); setSetupStep('server') }
        })
      } else {
        setNotice({ good: false, text: 'Set up this shared save on this PC before hosting it.' })
        if (setup) openHostSettings('network')
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
  }
  const hostGuideSteps: GuideStep[] = [
    { id: 'create', title: 'Make this PC your Host', target: '[data-guide="host-create"]',
      detail: 'Click New server. Choose the game, a new world or a safe copy, then the server app. The setup steps check the files and explain what is still needed.',
      tip: 'Already have a server? You can keep it and move to the next tip.' },
    { id: 'start', title: 'Start when you’re ready', target: selectedHostProfile ? '.server-primary-action, .server-current-state' : '[data-guide="host-create"]',
      detail: 'Save the setup, then use Start server. Wait for the game to report Ready. The app checks saved settings and local port conflicts before starting.',
      status: <p>{selectedHostProfile ? `${selectedHostProfile.name}: ${selectedHostRun?.state ?? 'Unknown'}` : 'No server saved yet. Finish New server first.'}</p>,
      tip: 'Ready describes the server on this PC. A Friend still needs to test joining.' },
    { id: 'route', title: 'Give friends a way to reach you', target: '.host-pc-workspace .settings-card',
      detail: 'Click Address and ports to review the app connection. Direct Internet, a private mesh, and an advanced address have different setup needs.',
      status: <p>Friend app: {friendAppStatus}.</p>,
      tip: 'The Friend app uses its own TCP port. Game ports are separate. A public IP lookup alone cannot confirm reachability.' },
    { id: 'invite', title: 'Invite one Friend', target: '.server-invite-button',
      detail: 'Click Invite friends when you’re ready to allow connections. Send the server code privately. Your Friend uses Join in their own TogetherServer app.',
      tip: 'This action turns on the secure Friend listener. Review each PC’s permissions in This PC settings → Access.' },
    { id: 'verify', title: 'Check the connection, then the game', target: '[data-guide="host-checks"]',
      detail: 'Open Troubleshoot connection. Refresh local checks, request the outside TCP test, and have a Friend connect from the intended network. Then test joining, a saved change, graceful Stop, and restart.',
      status: <p>{currentRouteResult ? `Outside app TCP: ${currentRouteResult.state}.` : 'Outside app TCP: not confirmed.'}</p>,
      tip: 'Keep automatic shutdown off for valued worlds until real idle and save checks pass. Finishing this guide does not record those checks.' }
  ]
  const joinGuideSteps: GuideStep[] = [
    { id: 'code', title: 'Start with your friend’s server code', target: '.invite-input input',
      detail: 'Ask the Host for the current TogetherServer code, paste it here, and click Connect. The code includes the Host address and secure identity.',
      tip: 'Joining needs an outbound connection. You do not need SteamCMD or a router port forward to join.' },
    { id: 'connection', title: 'Check this PC’s connection and access', target: '.friend-connection-doctor summary',
      detail: 'Run the connection checks. They check the saved Host port, pinned identity, this PC’s access, server assignment, and the supported game endpoint separately.',
      status: <p>{snapshot?.mode === 'Friend' && snapshot.endpoint ? `Host app: ${snapshot.state}.` : 'Connect with a server code first.'}</p>,
      tip: 'If approval or a permission is missing, the Host can review your PC under Access. Keep the secure identity check enabled.' },
    { id: 'play', title: 'Get into the game', target: '.friend-play-flow .friend-primary-actions button, .friend-play-flow summary[aria-label="How to join in the game"]',
      detail: 'Wait for Ready, then use the game address or join instructions. You need the same game edition and a compatible version. Ask the Host for the game password if needed.',
      tip: 'A successful app connection or game-port response is only one check. Joining the actual game confirms the play route.' }
  ]
  const guideSteps = gettingStarted.active === 'host' ? hostGuideSteps : joinGuideSteps
  const locateGuideStep = async (kind: GuideKind, index: number): Promise<boolean> => {
    if (pending || !requiredSetup.ready) return false
    if (snapshot?.mode !== (kind === 'host' ? 'Host' : 'Friend') && !await switchMode(kind === 'host' ? 'host' : 'friend')) return false
    if (!await changeScope(() => {
      if (kind === 'host') {
        setWorkspacePage('host'); setHostMobileDetail(true)
        setHostScope(index === 2 || index === 4 ? 'pc' : 'server')
        if (index === 2 || index === 4) setHostSettingsSection('network')
        else setHostServerTab('overview')
      } else {
        setWorkspacePage('join')
        if (index === 0) { setJoinSection('pairing'); setShowPairing(true) }
        else {
          setShowPairing(false); setJoinSection(index === 1 ? 'connection' : 'play')
          if (index === 1) { friendDoctorFocusRequested.current = true; setFriendTroubleshootingOpen(true) }
        }
      }
    })) return false
    window.requestAnimationFrame(() => window.requestAnimationFrame(() => {
      const selector = (kind === 'host' ? hostGuideSteps : joinGuideSteps)[index]?.target
      const target = selector ? document.querySelector<HTMLElement>(selector) : null
      target?.scrollIntoView?.({ behavior: window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'center' })
    }))
    return true
  }
  const startGuide = async (kind: GuideKind) => {
    if (!await locateGuideStep(kind, gettingStarted.resumeStep(kind))) return
    await gettingStarted.start(kind)
  }
  const closeGuide = (finish = false) => {
    if (finish) gettingStarted.finish()
    else gettingStarted.pause()
    document.querySelector<HTMLButtonElement>('.header-guides-trigger')?.focus({ preventScroll: true })
  }
  const pageTitle = workspacePage === 'host' || workspacePage === 'join' ? 'Servers' : workspacePage === 'attention' ? 'Attention' : 'App Settings'
  const pageDescription = workspacePage === 'host'
    ? savedProfiles.length === 0 ? 'Create a server to play with friends.' : activeRuns ? `${activeRuns} ${activeRuns === 1 ? 'server is' : 'servers are'} running.` : 'Choose a server to start hosting.'
    : workspacePage === 'join' ? 'Connect to a friend’s server and get ready to play.'
      : workspacePage === 'attention' ? 'Updates, warnings, and recent activity.'
        : 'Set up TogetherServer for this PC.'

  const resumeHosting = async (profileId: string) => {
    const profile = snapshot?.mode === 'Host' ? snapshot.settings.profiles.find(item => item.id === profileId) : null
    if (profile?.separateCopySourceProfileId) { await startLocalProfile(profile); return }
    if (await confirm({ title: 'Resume hosting?', confirmLabel: 'Resume hosting', description: 'TogetherServer checks for running servers before starting one.' }))
      void run(`resume-${profileId}`, `/api/local/profiles/${profileId}/resume-hosting`, 'POST')
  }

  const directoryControl = <ServerDirectory view={directory} nowMs={nowMs} busy={!!pending} error={serverDirectoryError}
    selection={snapshot?.mode === 'Host' && selectedHostProfile ? { kind: 'local', profileId: selectedHostProfile.id }
      : snapshot?.mode === 'Friend' && selectedFriendProfile ? { kind: 'friend', connectionId: snapshot.connectionId, profileId: selectedFriendProfile.id } : null}
    onRetry={() => void refreshServerDirectory()} onSelect={target => void selectDirectoryServer(target)}
    onAddLocal={() => void requestDirectoryAction('new')} onConnect={() => void requestDirectoryAction('connect')} />

  if (!requiredSetup.ready) return <>{developmentDemoControls}<RequiredFeatureSetup setup={requiredSetup} appName={appInstance?.displayName}
    renderStep={(feature, complete, busy) => feature.id === 'profile-name' && feature.revision === 1
      ? <ProfileSettings profile={localProfile} required busy={busy} onSaved={complete} /> : null}
    safety={<details className="required-setup-safety"><summary>Existing hosting and recovery</summary>
      <p className="helper-text">Hosting and connections continue during setup. You can still stop a server or review data recovery.</p>
      {loadError && <p role="alert" className="warning-text">Could not connect to TogetherServer: {loadError}</p>}
      <ActionFeedback notice={notice} />
      {snapshot?.mode === 'Friend' && <Button className="secondary" disabled={!!pending || localProfile.saving || requiredSetup.busy}
        onClick={() => void switchMode('host')}>Review hosting on this PC</Button>}
      {snapshot?.mode === 'Host' && snapshot.runs.filter(item => item.state !== 'Offline').map(item => <div className="required-setup-safety-row" key={item.profileId}>
        <span>{snapshot.settings.profiles.find(profile => profile.id === item.profileId)?.name ?? 'Recorded server'} / {item.state}</span>
        {['Process running', 'Starting', 'Listening', 'Ready', 'Stopping'].includes(item.state) ? <Button className="secondary" disabled={!!pending || item.state === 'Stopping'} onClick={async () => {
          if (await confirmOwnerLifecycle(item.profileId, 'Stop')) void run(`stop-${item.profileId}`, `/api/local/profiles/${item.profileId}/stop`, 'POST')
        }}>Stop server</Button> : <small>Review this server in Recovery. TogetherServer cannot stop it until its identity is confirmed.</small>}</div>)}
      {dataRecovery && <DataRecoveryPanel recovery={dataRecovery} mode={snapshot?.mode ?? null}
        runs={snapshot?.mode === 'Host' ? snapshot.runs : []}
        configuredProfileIds={snapshot?.mode === 'Host' ? snapshot.settings.profiles.map(profile => profile.id) : []}
        pending={pending} confirmed={recoveryConfirmed} onConfirmedChange={setRecoveryConfirmed}
        onAcknowledge={() => void acknowledgeDataRecovery()} onSwitchToHost={() => void switchMode('host')}
        onStopRecordedRun={profileId => void run(`recovery-stop-${profileId}`, `/api/local/profiles/${profileId}/stop`, 'POST')}
        onForgetRecordedRun={profileId => void run(`recovery-forget-${profileId}`, `/api/local/profiles/${profileId}/forget`, 'POST')} />}
    </details>} />{confirmationDialog}</>

  return <div className={appInstance?.isStaging ? 'shell app-shell staging-shell' : 'shell app-shell'}>
    {developmentDemoControls}
    <header className="topbar">
      <div className="brand"><span className="brand-mark">T</span><span><strong>{appInstance?.displayName ?? 'TogetherServer'}</strong><small>Game server workspace</small></span></div>
      <HeaderTools>
        <Button className="secondary header-guides-trigger" aria-label="Setup guides" disabled={!!pending || gettingStarted.busy}
          onClick={() => void changeScope(gettingStarted.open)}><Icon name="plug" /><span>Guides</span></Button>
        <Button className={`secondary header-servers-trigger${workspacePage === 'host' || workspacePage === 'join' ? ' selected' : ''}`}
          aria-label="Servers" aria-current={workspacePage === 'host' || workspacePage === 'join' ? 'page' : undefined}
          title={`Servers (Alt+1)${activeRuns ? ` · ${activeRuns} running` : ''}`}
          onClick={() => navigateWorkspace(snapshot?.mode === 'Friend' ? 'join' : 'host')}><Icon name="server" size={19} /><span>Servers</span>
          {activeRuns > 0 && <span className="header-server-count" aria-hidden="true">{activeRuns}</span>}</Button>
        <details className="notification-menu" onToggle={event => { if (event.currentTarget.open) setNotificationUnread(false) }}>
          <Tooltip content={notificationUnread ? 'Notifications, new activity' : 'Notifications'}><summary aria-label={notificationUnread ? 'Notifications, new activity' : 'Notifications'}>
            <Icon name="bell" size={19} />
            {notificationUnread && <span className="notification-badge"><span className="sr-only">New activity</span></span>}
          </summary></Tooltip>
          <div className="notification-panel">
            <div className="notification-panel-heading"><div><strong>Notifications</strong><small>Recent app and connection activity</small></div>{visibleActivity.length > 0 && <Button className="text-button" onClick={clearNotificationActivity}>Clear activity</Button>}</div>
            <Button className="secondary notification-center-link" aria-current={workspacePage === 'attention' ? 'page' : undefined}
              onClick={event => {
                const menu = event.currentTarget.closest('details')
                if (menu) { menu.open = false; menu.querySelector('summary')?.focus() }
                navigateWorkspace('attention')
              }}><Icon name="bell" />Open Attention Center</Button>
            {update?.state === 'Available' && <DismissibleNotice noticeKey={update.latestVersion} className="notification-item update" role="status"><span><Icon name="refresh" /></span><div><strong>Update available · v{update.latestVersion}</strong><p>{updateBlockedReason ?? 'Restart TogetherServer to install the latest version.'}</p><Button disabled={updateBusy || !!pending || !!updateBlockedReason} title={updateBlockedReason} onClick={() => setShowUpdatePrompt(true)}>{updateBusy ? <><Icon name="loader" />Preparing update…</> : 'Update and restart'}</Button></div></DismissibleNotice>}
            {notice && <DismissibleNotice noticeKey={notice.receivedAtMs} onDismiss={() => setNotice(null)} className={`notification-item ${notice.good ? 'good' : 'bad'}`} role="status"><span><Icon name={notice.good ? 'check' : 'warning'} /></span><div><strong>{notice.good ? 'Updated' : 'Needs attention'}</strong><p>{notice.text}</p></div></DismissibleNotice>}
            {groupedNotifications.slice(0, 8).map(({ item, repeatCount }) => <DismissibleNotice scope={activitySource} noticeKey={item.id} className={`notification-item ${item.severity === 'Warning' ? 'bad' : item.severity === 'Important' ? 'good' : ''}`} key={item.id}><span><Icon name={item.severity === 'Warning' ? 'warning' : 'check'} /></span><div><strong>{item.category}</strong><p>{item.message}</p><small>{new Date(item.occurredUtc).toLocaleString()}{repeatCount > 1 ? ` · Repeated ${repeatCount} times` : ''}</small>{snapshot?.mode === 'Host' && activityDestination(item) && <Button className="text-button" onClick={() => openActivity(item)}>{activityDestination(item)!.label}</Button>}{snapshot?.mode === 'Friend' && item.profileId && snapshot.profiles.some(profile => profile.id === item.profileId) && <Button className="text-button" onClick={() => openFriendServer(item.profileId!)}>Open server</Button>}</div></DismissibleNotice>)}
            {!notice && update?.state !== 'Available' && notificationActivity.length === 0 && <p className="notification-empty">No recent notifications.</p>}
          </div>
        </details>
        <Button className="secondary icon-button app-settings-trigger" aria-label="App Settings" aria-current={workspacePage === 'settings' ? 'page' : undefined} title="App Settings (Alt+4)" onClick={() => navigateWorkspace('settings')}><Icon name="settings" size={19} /></Button>
        <Button className="command-trigger" aria-label="Commands" title="Commands (Ctrl K)" onClick={() => setCommandPaletteOpen(true)}><Icon name="search" size={16} /><span>Commands</span><kbd>Ctrl K</kbd></Button>
      </HeaderTools>
    </header>

    {update?.state === 'Available' && !update.promptSnoozed && !uiPreferences.quietMode && <DismissibleNotice noticeKey={update.latestVersion} as="aside" className="update-banner" role="status"><div><strong>TogetherServer {update.latestVersion} is available</strong><span>{updateBlockedReason ?? `Update and restart when ready · ${update.publisherTrust}.`}</span></div><Button disabled={updateBusy || !!pending || !!updateBlockedReason} title={updateBlockedReason} onClick={() => setShowUpdatePrompt(true)}>{updateBusy ? <><Icon name="loader" />Preparing update…</> : 'Update and restart'}</Button></DismissibleNotice>}

    {appInstance?.isStaging && !appInstance.isDemo && <DismissibleNotice as="aside" className="staging-banner" role="status"><strong>DEVELOPMENT / STAGING</strong><span>App <code>{appInstance.localPort}</code> · Friend control <code>{developmentControlPort}</code>. Separate development servers and data. Production data is not loaded or copied.</span></DismissibleNotice>}

    {showUpdatePrompt && update?.state === 'Available' && <dialog ref={updatePromptRef} className="panel modal-dialog update-dialog" aria-labelledby="update-dialog-title" onCancel={event => { event.preventDefault(); dismissUpdatePrompt() }}>
      <div className="modal-heading"><div><h2 id="update-dialog-title">Update TogetherServer</h2><p>Version {update.latestVersion} is available. TogetherServer will reopen after the update.</p></div></div>
      <UpdateDetailsPanel update={update} busy={updateBusy} onBusyChange={setUpdateReminderBusy} onSnoozed={async () => { await reloadUpdate(); setShowUpdatePrompt(false) }} />
      <div className="update-dialog-content"><p>{updateBlockedReason ?? 'TogetherServer checks the download and keeps the previous app plus a backup of your settings and saved access.'}</p><p className="helper-text">Publisher: {update.publisherTrust}. Unsigned builds have no verified Windows publisher.</p>{notice && !notice.good && <DismissibleNotice noticeKey={notice.receivedAtMs} className="notice bad" role="status">{notice.text}</DismissibleNotice>}</div>
      <div className="actions update-dialog-actions"><Button className="secondary" disabled={updateBusy || updateReminderBusy} onClick={dismissUpdatePrompt}>Not now</Button><Button disabled={updateBusy || updateReminderBusy || !!pending || !!updateBlockedReason} title={updateBlockedReason} onClick={() => void installUpdate()}>{updateBusy ? <><Icon name="loader" />Preparing update…</> : 'Update and restart'}</Button></div>
    </dialog>}

    <CommandPalette open={commandPaletteOpen} commands={commands} onClose={() => setCommandPaletteOpen(false)} />
    <div className="app-body">
      <main className="workspace-main"><div className="workspace-scroll" ref={scrollContainer}
        onScrollCapture={event => {
          const target = workspaceScrollTarget()
          if (!restoringScroll.current && event.target === target && target) scrollPositions.current[currentScrollKey.current] = target.scrollTop
        }}>
      <div className="page-heading sr-only"><div><h1>{pageTitle}</h1><p>{pageDescription}</p></div>
      </div>

      {loadError && <DismissibleNotice className="notice bad" role="alert">Could not connect to TogetherServer: {loadError}</DismissibleNotice>}
      {(!snapshot || !appInstance) && !loadError && <section className="panel">Loading TogetherServer…</section>}

      {dataRecovery && <PaneErrorBoundary title="Data recovery" resetKey={dataRecovery.notices.map(item => item.detectedUtc).join('|')}><DataRecoveryPanel recovery={dataRecovery} mode={snapshot?.mode ?? null}
        runs={snapshot?.mode === 'Host' ? snapshot.runs : []}
        configuredProfileIds={snapshot?.mode === 'Host' ? snapshot.settings.profiles.map(profile => profile.id) : []}
        pending={pending} confirmed={recoveryConfirmed} onConfirmedChange={setRecoveryConfirmed}
        onAcknowledge={() => void acknowledgeDataRecovery()} onSwitchToHost={() => void switchMode('host')}
        onStopRecordedRun={profileId => void run(`recovery-stop-${profileId}`, `/api/local/profiles/${profileId}/stop`, 'POST')}
        onForgetRecordedRun={profileId => void run(`recovery-forget-${profileId}`, `/api/local/profiles/${profileId}/forget`, 'POST')} /></PaneErrorBoundary>}

      {workspacePage === 'host' && snapshot?.mode === 'Host' && snapshot.startupRecovery &&
        <StartupRecoveryNotice recovery={snapshot.startupRecovery} profiles={savedProfiles} busy={!!pending || dirty}
          pendingProfileId={pending.startsWith('resume-') ? pending.slice('resume-'.length) : undefined} onResume={profileId => void resumeHosting(profileId)} />}

      {workspacePage === 'attention' && snapshot && <PaneErrorBoundary title="Attention Center" resetKey={activitySource}><AttentionWorkspace
        mode={snapshot.mode} activitySource={activitySource} activity={retainedActivity} nowMs={nowMs} onHistoryChange={updateAttentionHistory}
        servers={snapshot.mode === 'Host' ? savedProfiles : snapshot.profiles}
        currentProblems={currentProblems.filter(row => !!row.warning).map(row => ({ ...row, warning: row.warning! }))}
        onOpen={openActivityDestination} onOpenFriendServer={openFriendServer}>
        <div className="attention-toolbar-actions">{snapshot.mode === 'Host' && <Button className="secondary" onClick={() => openHostSettings('diagnostics')}>App checks</Button>}
          {visibleActivity.length > 0 && <Button className="secondary" onClick={clearNotificationActivity}>Clear activity</Button>}</div>
        {snapshot.mode === 'Host' && snapshot.startupRecovery && <StartupRecoveryNotice review recovery={snapshot.startupRecovery}
          profiles={savedProfiles} busy={!!pending || dirty} pendingProfileId={pending.startsWith('resume-') ? pending.slice('resume-'.length) : undefined}
          onResume={profileId => void resumeHosting(profileId)} />}
        <div className="attention-list">
          {snapshot?.mode === 'Host' && storageWarnings.map((warning, index) => <DismissibleNotice as="article" className="notification-item bad" role="status" key={`storage-${index}`}><span><Icon name="warning" /></span><div><strong>Storage health</strong><p>{warning}</p></div></DismissibleNotice>)}
          {update?.state === 'Available' && <DismissibleNotice noticeKey={update.latestVersion} as="article" className="notification-item update" role="status"><span><Icon name="refresh" /></span><div><strong>Update available - v{update.latestVersion}</strong><p>{updateBlockedReason ?? 'Restart TogetherServer to install the latest version.'}</p><Button disabled={updateBusy || !!pending || !!updateBlockedReason} title={updateBlockedReason} onClick={() => setShowUpdatePrompt(true)}>{updateBusy ? <><Icon name="loader" />Preparing update...</> : 'Update and restart'}</Button></div></DismissibleNotice>}
          {notice && <DismissibleNotice noticeKey={notice.receivedAtMs} onDismiss={() => setNotice(null)} as="article" className={`notification-item ${notice.good ? 'good' : 'bad'}`} role="status"><span><Icon name={notice.good ? 'check' : 'warning'} /></span><div><strong>{notice.good ? 'Updated' : 'Needs attention'}</strong><p>{notice.text}</p></div></DismissibleNotice>}
        </div>
        {snapshot?.mode === 'Host' && snapshot.storageHealth && <details className="advanced-block storage-health-details"><summary>Storage and resource details</summary>
          <div className="storage-health-grid">{snapshot.storageHealth.locations.map(location => <div className="storage-health-item" key={location.id}><span><strong>{location.label}</strong><small>{location.state}</small></span><span>{location.availableSpaceBytes == null ? 'Space unavailable' : `${formatBytes(location.availableSpaceBytes)} available`}{location.usedBytes > 0 ? ` · ${formatBytes(location.usedBytes)} measured` : ''}</span><small>{location.detail}</small></div>)}</div>
          <p className="helper-text">{snapshot.storageHealth.resources.logicalProcessors} logical processors · {formatBytes(snapshot.storageHealth.resources.processWorkingSetBytes)} app working set. {snapshot.storageHealth.resources.evidenceBoundary}</p>
        </details>}
      </AttentionWorkspace></PaneErrorBoundary>}

      {workspacePage === 'settings' && snapshot && <ResizableWorkspace area="settings" className="app-settings-workspace" label="App Settings"
        sidebarLabel="App settings navigation" contentClassName="settings-content" sidebar={<SectionSidebar label="App settings sections" current="location"
          sections={appSections} section={appSection} onSelect={section => void changeScope(() => jumpToAppSection(section))} />}>
        <AppSettings profile={localProfile} preferences={uiPreferences} onPreferencesChange={setUiPreferences}
          section={appSection} scrollRequest={appSettingsScrollRequest} onSectionChange={setAppSection} onBusyChange={setAppSettingsBusy}
          desktop={desktopPreferences} instance={appInstance} update={update} busy={!!pending} desktopBusy={desktopBusy} updateBusy={updateBusy}
          onDesktopChange={patch => void saveDesktopPreference(patch)} notice={notice} onClearNotice={() => setNotice(null)}
          onCheckUpdate={async () => (await checkUpdate(false))?.state === 'Available'} onReviewUpdate={() => setShowUpdatePrompt(true)} onReloadUpdate={reloadUpdate} onQuit={() => void quitApp()} />
      </ResizableWorkspace>}

      {workspacePage === 'join' && snapshot?.mode === 'Friend' && <ResizableWorkspace area="servers" className="join-context-workspace" sidebarLabel="Server navigation" sidebar={<>
          <ProfileAccess profile={localProfile} onOpen={() => void changeScope(() => { setWorkspacePage('settings'); jumpToAppSection('profile') })} />
          {directoryControl}
          <p className="server-directory-selected">{selectedFriendProfile?.name || 'Selected friend server'}</p>
          <SectionSidebar label="Selected server sections" sections={joinSections.filter(section => friendConnectionHidden ? section.id === 'pairing' : section.id !== 'logs' ||
            !!selectedFriendProfile && friendLogAvailability(selectedFriendProfile.canViewLogs, snapshot.hostCapabilities).visible)}
            section={showPairing || !snapshot.endpoint || friendConnectionHidden ? 'pairing' : joinSection} onSelect={selectJoinSection} />
        </>}>
        <section className="panel friend-panel friend-primary">
          {(showPairing || !snapshot.endpoint || friendConnectionHidden) && <div className="section-heading"><div><h2>{snapshot.endpoint && !friendConnectionHidden ? 'Add another Host' : 'Join a server'}</h2><p>Ask the Host for the current server code.</p></div></div>}
          {snapshot.endpoint && !showPairing && !friendConnectionHidden ? <>
            <div className="join-host-caption"><Icon name="monitor" /><span>Host: <strong>{snapshot.hostProfileName?.trim() || snapshot.connectionName || 'Saved Host'}</strong>{snapshot.hostProfileName?.trim() && snapshot.connectionName && <small> / Saved as: {snapshot.connectionName}</small>}</span></div>
            <div className={pending === 'poll' ? 'compact-status refreshing' : 'compact-status'} aria-busy={pending === 'poll'}><span className={`status ${statusTone(snapshot.state)}`}>{pending === 'poll' && <Icon name="loader" />}{snapshot.state === 'Disconnected/Unknown' ? 'Connection unknown' : snapshot.state === 'Disabled' ? 'Connected · controls paused' : snapshot.state}</span>
              {(joinSection === 'network' || joinSection === 'connection') && <span>{snapshot.lastConnectedUtc ? `Last reached ${Math.max(0, Math.floor((nowMs - Date.parse(snapshot.lastConnectedUtc)) / 60000))} min ago · ${new Date(snapshot.lastConnectedUtc).toLocaleString()}` : 'Waiting for a reply from the Host'}</span>}</div>
            {snapshot.expiryWarning && <DismissibleNotice scope={snapshot.connectionId} className="notice bad" role="status">{snapshot.expiryWarning}</DismissibleNotice>}
            <FriendAccessExpiredNotice connectionCode={snapshot.connectionCode} connectionId={snapshot.connectionId} />
            {snapshot.connectionCode && (snapshot.state === 'Disconnected/Unknown' || snapshot.state === 'Revoked' || snapshot.state === 'Awaiting approval') && <details className="troubleshoot-block" open><summary>{snapshot.state === 'Awaiting approval' ? 'Waiting for Host approval' : 'Troubleshoot connection'}</summary>{snapshot.state === 'Awaiting approval' ? <p>This PC is saved. Ask the Host to approve it under Friend access; you do not need a new code.</p> : <FriendConnectionHelp code={snapshot.connectionCode} />}</details>}
            {(joinSection === 'network' || joinSection === 'connection') && <div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => void checkFriendConnection()}>{pending === 'poll' ? <><Icon name="loader" />Refreshing…</> : <><Icon name="refresh" />Check connection</>}</Button>
              <Button className="text-button" onClick={() => void changeScope(() => { setShowPairing(true); setFriendHostAddress(''); setFriendInvite(''); setPairIssue(null); setJoinSection('pairing') })}>Add another Host</Button></div>}

          </> : <>
            <form className="join-row" onSubmit={event => { event.preventDefault(); void pairFriend() }}>
              <label className="invite-input">Server code<Input autoFocus type="password" autoComplete="off" value={friendInvite} onChange={event => { setFriendInvite(event.target.value.trim()); setPairIssue(null) }} placeholder="Paste the server code here" /></label>
              <Button type="submit" disabled={!!pending || !friendInvite}>{pending === 'pair' ? 'Connecting…' : 'Connect'}</Button>
            </form>
            {pairIssue && <DismissibleNotice className="connection-warning" role="alert"><strong>{pairIssue.message}</strong><FriendConnectionHelp code={pairIssue.code} /></DismissibleNotice>}
            <details className="advanced-block"><summary>Using an older invite?</summary><label>Host IP<Input value={friendHostAddress} onChange={event => setFriendHostAddress(event.target.value.trim())} placeholder="123.45.67.89" /><small>Enter the Host IP only for older codes. New codes include it.</small></label></details>
          </>}
          {snapshot.endpoint && showPairing && !friendConnectionHidden && <Button className="secondary" disabled={pending === 'pair'} onClick={() => { setShowPairing(false); setJoinSection('play'); setPairIssue(null); setFriendInvite('') }}>Cancel and return to saved Host</Button>}


          {snapshot.endpoint && !showPairing && !friendConnectionHidden && snapshot.profiles.length === 0 && (snapshot.state === 'Connected' || snapshot.state === 'Disabled') && <div className="empty compact-empty"><p>No servers are shared with this PC yet. Ask the Host to add one under Friend access.</p></div>}
          {snapshot.endpoint && !showPairing && !friendConnectionHidden && snapshot.profiles.length > 0 && <div className="friend-server-list">
          {snapshot.profiles.filter(profile => profile.id === selectedFriendProfile?.id).map(profile => {
            const connectionKey = `friend-${snapshot.connectionId}-${profile.id}`
            const logAvailability = friendLogAvailability(profile.canViewLogs, snapshot.hostCapabilities)
            const addressKey = `${connectionKey}-address`
            const addressActivity = connectionActivity[addressKey] ?? null
            const operationBusy = profile.operation?.state === 'Pending' || profile.operation?.state === 'Running'
            const permissions = effectiveFriendPermissions(snapshot, profile, nowMs)
            const operationConflict = profile.operation?.code === 'PortConflict' && profile.operation.portConflicts?.length
              ? { message: profile.operation.message, conflicts: profile.operation.portConflicts } : null
            return <article className="profile-card" id={`friend-server-${profile.id}`} key={connectionKey} aria-busy={pending === 'poll' || pending.endsWith(profile.id)}>
              <div className="profile-top"><div><h3>{profile.name}</h3><p>{gameLabel(profile.kind)}</p></div><div className="server-current-state"><span className={`status ${statusTone(friendConnectionExplanation(snapshot, nowMs).currentAccess ? profile.state : 'Unknown')}`}>{pending === 'poll' && <Icon name="loader" />}{!friendConnectionExplanation(snapshot, nowMs).currentAccess ? 'Last known / Unknown' : profile.state === 'Ready' ? 'Ready to join' : profile.state}</span>
                {profile.state === 'Ready' && <span className="server-player-count"><Icon name="users" size={14} />{playerCount(friendConnectionExplanation(snapshot, nowMs).currentAccess ? publicPlayerCount(profile, nowMs) : null, profile.maxPlayers)}</span>}
                {publicPlayerCount(profile, nowMs) === 0 && friendConnectionExplanation(snapshot, nowMs).currentAccess && <ServerCountdown deadline={profile.autoShutdownAtUtc} nowMs={nowMs} />}
              </div></div>
              {profile.operation && currentRemoteOperation(profile, nowMs) && <DismissibleNotice scope={connectionKey} noticeKey={profile.operation.id + ':' + (['Succeeded', 'Failed', 'Interrupted'].includes(profile.operation.state) ? profile.operation.state : 'pending')} trackMessage={false} className={`notice ${profile.operation.state === 'Failed' || profile.operation.state === 'Interrupted' ? 'bad' : 'good'}`} role="status"><strong>{profile.operation.action[0].toUpperCase() + profile.operation.action.slice(1)}: {profile.operation.state}</strong><p>{profile.operation.message}</p></DismissibleNotice>}
              <ActionFeedback notice={currentActionNotice(actionNotices[profile.id], snapshot, feedbackNowMs)} />


              {profile.maintenanceEnabled && <DismissibleNotice scope={connectionKey} className="notice bad" role="status"><strong>Maintenance mode</strong><p>{profile.maintenanceMessage || 'The Host has paused remote actions for this server.'}</p></DismissibleNotice>}
              {joinSection === 'connection' && profile.kind !== 'Custom' && <SharedHostingIdentity profileId={profile.id} host={false}
                available={snapshot.hostCapabilities.includes('shared-worlds-v2')} worldName={profile.name} serverState={profile.state}
                serverStatusFresh={friendConnectionExplanation(snapshot, nowMs).currentAccess} observedHostAddress={snapshot.endpoint} />}
              {joinSection === 'play' && profile.kind !== 'Custom' && !['Revoked', 'Access expired', 'Awaiting approval'].includes(snapshot.state) &&
                snapshot.hostCapabilities.includes('shared-world-peers-v1') && <SharedHostNotice profileId={profile.id}
                  connectionId={snapshot.connectionId} currentHostAddress={snapshot.endpoint} currentHostSessionId={profile.runOperationId}
                  disabled={!!pending || operationBusy} onConnected={async () => {
                    applySnapshot(await readSnapshot()); setShowPairing(false); setJoinSection('play')
                  }} />}
              {joinSection === 'play' && <FriendPlayFlow snapshot={snapshot} profile={profile} nowMs={nowMs} busy={!!pending || operationBusy}
                showServerNotifications={false}
                pendingAction={pending === `friend-start-${profile.id}` || operationBusy && profile.operation?.action === 'start' ? 'start' : pending === `friend-stop-${profile.id}` || operationBusy && profile.operation?.action === 'stop' ? 'stop' : pending === `friend-refresh-${profile.id}` ? 'refresh' : null}
                identityKey={friendGameCheckIdentity(snapshot, profile)}
                onStart={() => void friendAction(profile.id, 'start')}
                onStop={() => void friendAction(profile.id, 'stop')}
                onRefresh={() => void friendAction(profile.id, 'refresh')}
                onOpenConnectionDoctor={openFriendConnectionDoctor}
                onOpenLogs={() => selectJoinSection('logs')}
                localHostingAction={profile.kind !== 'Custom' && <FriendLocalHosting key={`local-hosting-${connectionKey}`}
                  profileId={profile.id} connectionId={snapshot.connectionId} worldName={profile.name}
                  available
                  accessBlocked={['Revoked', 'AccessExpired', 'ApprovalPending'].includes(snapshot.connectionCode ?? '')}
                  disabled={!!pending || operationBusy}
                  onBrowseServerFile={game => browseSharedServerFile(profile.id, game)}
                  onOpenConnectionHelp={localProfileId => void openSharedHost(localProfileId, true)}
                  onOpenGameSetup={(_, localProfileId) => { if (localProfileId) void openSharedHost(localProfileId, false, true) }}
                  onOpenHost={localProfileId => void openSharedHost(localProfileId, false)} />}
                startNotice={<FriendStartConnectionNotice />}
                connectionDetails={profile.joinAddress && <ConnectionDetails kind={profile.gameKind ?? profile.kind}
                endpointFieldId={addressKey} onCopyPart={async (part, value) => {
                  if (!await copyConnectionValue(`${addressKey}-${part}`, value, part === 'port' ? 'Game port' : 'Server address')) throw new Error('Clipboard unavailable')
                }}
                fields={[{ id: addressKey, label: 'Server IP', value: profile.joinAddress,
                  revealed: !!revealedConnections[addressKey], copying: addressActivity === 'copy', revealing: false,
                  onReveal: () => revealConnectionDetails(addressKey), onHide: () => hideConnectionDetails(addressKey),
                  onCopy: () => void copyConnectionValue(addressKey, profile.joinAddress!, 'Server IP') }]}
                 refreshing={pending === 'poll'} note={profile.state === 'Listening'
                   ? 'The server is listening on this PC. Test joining and saving in the game.'
                   : profile.kind === 'Valheim' && !profile.canViewPassword ? 'Ask the Host to share the game password or allow password access for this PC.' : undefined}
                 />}
                requirements={<>{profile.kind === 'Valheim' && <SharedGamePassword profileId={profile.id} connectionId={snapshot.connectionId}
                  allowed={profile.canViewPassword === true} current={permissions.currentAccess} supported={snapshot.hostCapabilities.includes('server-game-password-v1')}
                  changedUtc={profile.gamePasswordChangedUtc} identity={JSON.stringify([snapshot.hostId, snapshot.endpoint])} />}
                {['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(profile.gameKind ?? profile.kind) &&
                ['Connected', 'Disabled'].includes(snapshot.state) && <details className="server-support-disclosure"><summary><Icon name="game" />Game requirements</summary><PaneErrorBoundary title="Game requirements" resetKey={`${snapshot.connectionId}-${profile.id}`}>
                  <GameCompatibilityPanel key={`${snapshot.connectionId}-${profile.id}-${profile.gameKind ?? profile.kind}`} profileId={profile.id} host={false} />
                </PaneErrorBoundary></details>}</>} />}
              {joinSection === 'connection' && <details className="friend-server-support" open><summary>Players & server tools</summary>
                <ServerActivity state={permissions.currentAccess ? profile.state : 'Unknown'} online={permissions.currentAccess ? publicPlayerCount(profile, nowMs) : null} capacity={profile.maxPlayers} deadline={profile.autoShutdownAtUtc} timerReason={profile.autoShutdownReason} nowMs={nowMs}
                  refreshing={pending === `friend-refresh-${profile.id}`} refreshDisabled={!!pending || !['Connected', 'Disabled'].includes(snapshot.state)}
                  onRefresh={() => void friendAction(profile.id, 'refresh')} />
                <div className="actions server-actions">
                {profile.state === 'Ready' && snapshot.state === 'Connected' && profile.canRestartNow && <Button className="secondary" disabled={!!pending || operationBusy || !permissions.actions.restart.available} title={!permissions.actions.restart.available ? permissions.actions.restart.reason : undefined} onClick={() => void friendAction(profile.id, 'restart')}>{pending === `friend-restart-${profile.id}` ? <><Icon name="loader" />Restarting…</> : <><Icon name="refresh" />Restart server</>}</Button>}
                {profile.state === 'Ready' && snapshot.state === 'Connected' && profile.canExtendTimer && <Button className="secondary" disabled={!!pending || operationBusy || !permissions.actions.extend.available} title={!permissions.actions.extend.available ? permissions.actions.extend.reason : undefined} onClick={() => void friendAction(profile.id, 'extend')}>{pending === `friend-extend-${profile.id}` ? <><Icon name="loader" />Adding time…</> : <>Add {profile.timerExtensionMinutes} minutes</>}</Button>}
                {['Ready', 'Listening'].includes(profile.state) && ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Terraria'].includes(profile.kind) && <Button className="text-button" disabled={!!pending || !profile.joinAddress} onClick={() => void probeGameEndpoint(profile.id)}>{pending === `probe-game-${profile.id}` ? 'Checking game connection...' : 'Check game connection from this PC'}</Button>}
              </div>
              </details>}
                {joinSection === 'logs' && logAvailability.visible && <div className="friend-log-surface">
                {<PaneErrorBoundary title="Shared server logs" resetKey={profile.id}><ServerLogViewer
                  endpoint={`/api/local/friend/${profile.id}/logs`}
                  visible={workspacePage === 'join' && friendLogProfileId === profile.id}
                  unsupported={logAvailability.unsupported} /></PaneErrorBoundary>}
              </div>}
              {joinSection === 'network' && <FriendGameCheckResult snapshot={snapshot} profile={profile} gameResults={gameEndpointResults}
                gameResultIdentities={gameResultIdentities} nowMs={nowMs} />}
              {operationConflict && <DismissibleNotice scope={connectionKey} noticeKey={profile.operation?.id} className="port-conflict-action" role="alert"><strong>Shared game port</strong><p>{operationConflict.message}</p>
                {operationConflict.conflicts.every(conflict => conflict.canReplace) ? <Button disabled={!!pending || operationBusy} onClick={() => void friendAction(profile.id, 'replace')}>{pending === `friend-replace-${profile.id}` ? <><Icon name="loader" />Switching…</> : <>Stop empty server and start this one</>}</Button>
                  : <small>{operationConflict.conflicts.find(conflict => !conflict.canReplace)?.blockReason ?? 'The other server cannot be stopped safely.'}</small>}</DismissibleNotice>}
              {(joinSection === 'play' || joinSection === 'connection') && <FriendStopBlockers snapshot={snapshot} profile={profile} />}
              {joinSection === 'connection' && profile.canExtendTimer && <p className="helper-text">Extra time: {profile.timerExtensionRemainingMinutes} minutes remaining; each request adds {profile.timerExtensionMinutes} minutes.{profile.maintenanceEnabled || snapshot.state !== 'Connected' ? ' Controls are currently paused.' : ''}</p>}
              {joinSection === 'shared-saves' && profile.kind !== 'Custom' && <>
                <FriendLocalHosting key={`local-hosting-${connectionKey}`} presentation="card"
                  profileId={profile.id} connectionId={snapshot.connectionId} worldName={profile.name}
                  available
                  accessBlocked={['Revoked', 'AccessExpired', 'ApprovalPending'].includes(snapshot.connectionCode ?? '')}
                  disabled={!!pending || operationBusy}
                  onBrowseServerFile={game => browseSharedServerFile(profile.id, game)}
                  onOpenConnectionHelp={localProfileId => void openSharedHost(localProfileId, true)}
                  onOpenGameSetup={(_, localProfileId) => { if (localProfileId) void openSharedHost(localProfileId, false, true) }}
                  onOpenHost={localProfileId => void openSharedHost(localProfileId, false)} />
                </>}
              {joinSection === 'chat' && <div className="friend-chat-surface">
                {<PaneErrorBoundary title="Server chat" resetKey={profile.id}>
                  <ServerChat profileId={profile.id} host={false} connectionId={snapshot.connectionId} hostId={snapshot.hostId} quietMode={uiPreferences.quietMode}
                    onDraftStateChange={keepChatGuard}
                    visible={workspacePage === 'join' && friendChatProfileId === profile.id}
                    supported={snapshot.hostCapabilities.includes('server-chat-v1')} />
                </PaneErrorBoundary>}</div>}
            </article>
          })}
          </div>}
          {joinSection === 'chat' && snapshot.endpoint && !showPairing && !friendConnectionHidden && snapshot.state === 'Disconnected/Unknown' &&
            !!snapshot.chatProfiles?.some(profile => profile.supported) &&
            <div className="friend-server-list"><h3>Chats saved on this PC</h3>
              <p className="helper-text">The Host is unreachable. Messages you write now will wait here until the connection returns.</p>
              {snapshot.chatProfiles.filter(profile => profile.supported).map(profile =>
                <article className="profile-card" key={profile.id}><div className="profile-top"><h3>{profile.name}</h3>
                  <Button className="secondary" aria-expanded={friendChatProfileId === profile.id}
                    onClick={() => void changeScope(() => setFriendChatProfileId(current => current === profile.id ? '' : profile.id))}>
                    {friendChatProfileId === profile.id ? 'Hide chat' : 'Open chat'}</Button></div>
                  {friendChatProfileId === profile.id && <PaneErrorBoundary title="Saved server chat" resetKey={profile.id}>
                    <ServerChat profileId={profile.id} host={false} connectionId={snapshot.connectionId} hostId={snapshot.hostId} quietMode={uiPreferences.quietMode}
                      onDraftStateChange={keepChatGuard}
                      visible={workspacePage === 'join' && friendChatProfileId === profile.id} />
                  </PaneErrorBoundary>}</article>)}</div>}
          {(joinSection === 'network' || joinSection === 'connection') && snapshot.endpoint && !showPairing && !friendConnectionHidden && <section className="friend-connection-tools" aria-labelledby="friend-tools-title">
            <h3 id="friend-tools-title">Server settings</h3>
            {joinSection === 'connection' && selectedFriendProfile && <Button className="secondary" onClick={() => setServerNotification({ id: selectedFriendProfile.id, name: selectedFriendProfile.name, connectionId: snapshot.connectionId })}><Icon name="bell" />Server notifications</Button>}
            {joinSection === 'connection' && <div className="settings-dialog-stack">
              <label>Saved connection name<div className="field-with-button"><Input value={friendConnectionName} maxLength={48} onChange={event => setFriendConnectionName(event.target.value)} /><Button className="secondary" disabled={!!pending || !friendConnectionName.trim() || friendConnectionName.trim() === snapshot.connectionName} onClick={() => void renameFriendConnection()}><Icon name="edit" />Rename</Button></div></label>
              <details className="advanced-block" ref={addressRecoveryRef}><summary><Icon name="link" />Change Host address</summary>
              <label>New Host address<Input ref={recoveryAddressInputRef} value={recoveryEndpoint} onChange={event => setRecoveryEndpoint(event.target.value.trim())} placeholder={`https://100.64.0.2:${appInstance?.companionPort ?? 5131}`} /><small>The new address must belong to the same Host and accept this PC’s saved access.</small></label>
              <Button className="secondary" aria-label="Check and update address" disabled={!!pending || !recoveryEndpoint} onClick={() => void recoverFriendEndpoint()}><Icon name={pending === 'recover-endpoint' ? 'loader' : 'refresh'} />{pending === 'recover-endpoint' ? 'Checking…' : 'Update address'}</Button>
              <details><summary>Saved access details</summary>
              <p className="helper-text">Route: {snapshot.routeMode === 'PrivateMesh' ? 'Private mesh' : snapshot.routeMode === 'AdvancedAddress' ? 'Advanced address' : 'Direct Internet'}{snapshot.routeAddress ? ` (${snapshot.routeAddress})` : ''}. Host {snapshot.hostVersion ?? 'unknown'} · this app {snapshot.friendVersion ?? 'unknown'} · protocol {snapshot.hostProtocolVersion ?? 'unknown'}.</p>
              <p className="helper-text">Saved access expires {snapshot.credentialExpiresUtc ? new Date(snapshot.credentialExpiresUtc).toLocaleString() : 'unknown'}. Secure Host identity expires {snapshot.certificateExpiresUtc ? new Date(snapshot.certificateExpiresUtc).toLocaleString() : 'unknown'}.</p>
              </details></details>
              <Button className="text-button danger" disabled={!!pending} onClick={() => void forgetFriendConnection()}><Icon name="trash" />{pending === 'forget-connection' ? 'Forgetting…' : 'Forget this Host'}</Button>
            </div>}
          <details className="friend-troubleshooting" open={friendTroubleshootingOpen} onToggle={event => setFriendTroubleshootingOpen(event.currentTarget.open)}><summary>Troubleshooting</summary>
          {friendTroubleshootingOpen && snapshot.endpoint && !showPairing && !friendConnectionHidden && <FriendConnectionDoctor snapshot={snapshot}
            gameResults={gameEndpointResults} gameResultIdentities={gameResultIdentities} nowMs={nowMs} busy={!!pending}
            onRefresh={() => void checkFriendConnection()} onProbe={profileId => void probeGameEndpoint(profileId)} />}
          {friendTroubleshootingOpen && appInstance?.isStaging && snapshot.endpoint && !showPairing && !friendConnectionHidden && <FriendRemoteRehearsal key={snapshot.connectionId} snapshot={snapshot} />}
          </details>
          </section>}
        </section>
        {joinSection === 'shared-saves' && <WorldPeerPanel readOnly profileId={selectedFriendProfile?.id} />}
        </ResizableWorkspace>}

      {workspacePage === 'host' && snapshot?.mode === 'Host' && draft && <ResizableWorkspace area="servers" className="host-context-workspace" sidebarLabel="Server navigation" sidebar={<>
          <ProfileAccess profile={localProfile} onOpen={() => void changeScope(() => { setWorkspacePage('settings'); jumpToAppSection('profile') })} />
          {directoryControl}
          <p className="server-directory-selected">{selectedHostProfile?.name || 'Selected server'}</p>
          {savedProfiles.length > 0 && <SectionSidebar label="Selected server sections"
            sections={(Object.keys(hostServerTabLabels) as HostServerTab[]).map(id => ({ id, label: hostServerTabLabels[id], icon: hostServerTabIcons[id] }))}
            section={hostScope === 'server' ? hostServerTab : undefined} onSelect={selectHostTab} />}
          <details className="server-sections-disclosure" open={hostScope === 'pc'}><summary>This PC settings</summary>
            <SectionSidebar label="This Host PC sections" sections={hostPcSections}
              section={hostScope === 'pc' ? hostSettingsSection : undefined} onSelect={openHostSettings} /></details>
        </>}>
        {hostScope === 'server' && <>
        {savedProfiles.length > 0 && <>
        <section className="host-workspace-shell">
          <div className="host-focused-detail">
            <section className="server-detail" data-server-tab={hostServerTab} aria-label={selectedHostProfile ? `${selectedHostProfile.name} workspace` : 'Server workspace'}>
              <div className="server-detail-pane">
            {snapshot.settings.profiles.filter(profile => profile.id === selectedHostProfile?.id).map(profile => {
              const status = snapshot.runs.find(run => run.profileId === profile.id)
              const ownerProfile = originalLocalServer(profile, snapshot.settings.profiles)
              const sharingProfile = ownerProfile ?? profile
              const sharingStatus = snapshot.runs.find(run => run.profileId === sharingProfile.id)
              const startPending = pending === `start-${profile.id}`
              const startup = serverStartupStage({ ...status, requestPending: startPending })
              const count = status ? playerCountAvailability(status, nowMs).count : null
              const workProfileId = currentOperationProfile.current[pending] ?? savedProfiles.find(item => pending.includes(item.id))?.id
              const profileWorkBusy = !!pending && (!workProfileId || workProfileId === profile.id)
              const certification = snapshot.customCertifications?.[profile.id]
              const recovery = snapshot.crashRecovery?.[profile.id]
              const backupStatus = snapshot.backups?.[profile.id]
              const backupList = backupLists[profile.id]
              const shownBackupStatus = backupList?.status ?? backupStatus
              const connectionKey = `host-${profile.id}`
              const addressKey = `${connectionKey}-address`
              const passwordKey = `${connectionKey}-password`
              const addressActivity = connectionActivity[addressKey] ?? null
              const passwordActivity = connectionActivity[passwordKey] ?? null
              const gameAddress = detectedGameIp ? `${detectedGameIp}:${profile.gamePort}` : ''
              const connectionFields = [{ id: addressKey, label: 'Server IP', value: gameAddress,
                revealed: !!revealedConnections[addressKey], copying: addressActivity === 'copy', revealing: false,
                onReveal: () => revealConnectionDetails(addressKey), onHide: () => hideConnectionDetails(addressKey),
                onCopy: () => void copyConnectionValue(addressKey, gameAddress, 'Server IP') },
              ...(profile.kind === 'Valheim' ? [{ id: passwordKey, label: 'Game password',
                value: revealedGamePasswords[passwordKey] ?? '', revealed: !!revealedConnections[passwordKey],
                copying: passwordActivity === 'copy', revealing: passwordActivity === 'reveal',
                onReveal: () => void revealGamePassword(profile, passwordKey),
                onHide: () => hideConnectionDetails(passwordKey),
                onCopy: () => void copyGamePassword(profile, passwordKey) }] : [])]
              return <article className="profile-card server-detail-card" key={profile.id} aria-busy={checkingPorts || detectingPublicIp || pending.endsWith(profile.id)}>
                {profile.separateCopySourceProfileId && !ownerProfile && <GuestHostingLabel server={directory.localServers.find(server => server.id === profile.id) ?? {
                  separateCopySourceProfileId: profile.separateCopySourceProfileId, sourceName: null, sourceHostName: null }} />}
                <div className="server-command-header">
                  <div className="profile-top"><div className="server-identity"><h3>{profile.name}</h3><p>{profileGameLabel(profile)} · {profile.worldId}</p></div>
                    <div className="server-current-state"><span className={`status ${isPlayerCountObservationUnavailable(status) ? '' : statusTone(status?.state ?? 'Unknown')}`}>{(pending === `start-${profile.id}` || pending === `stop-${profile.id}` || pending === `restart-${profile.id}`) && <Icon name="loader" />}{startup ? 'Starting' : isPlayerCountObservationUnavailable(status) ? 'Status unavailable' : status?.state === 'Process running' ? 'Starting' : status?.state ?? 'Unknown'}</span>
                      {['Ready', 'Listening'].includes(status?.state ?? '') && <Button className="text-button server-player-link" onClick={() => selectHostTab('players')}><Icon name="users" />{count === null ? 'Players unknown' : `${count}${status?.maxPlayers !== null && status?.maxPlayers !== undefined ? ` / ${status.maxPlayers}` : ''} online`}</Button>}
                      {count === 0 && <ServerCountdown deadline={status?.autoShutdownAtUtc ?? null} nowMs={nowMs} />}
                    </div>
                  </div>
                  <div className="actions server-actions" aria-label="Server controls">
                    {(status?.state === 'Offline' || pending === `start-${profile.id}`) && <Button className="server-primary-action" disabled={!!pending || dirty || hasUnsavedServerEdits || dataRecovery?.lifecycleBlocked} title={dataRecovery?.lifecycleBlocked ? 'Resolve the local data recovery warning first.' : dirty ? 'Save or cancel setup changes first.' : hasUnsavedServerEdits ? 'Save or discard file changes before starting.' : undefined} onClick={() => {
                      if ([...draftGuards.current].some(([key, guard]) => key !== 'chat' && (guard.dirty || guard.saving))) return
                      void startLocalProfile(profile)
                    }}>{pending === `start-${profile.id}` ? <><Icon name="loader" /><span>Starting…</span></> : <><Icon name="play" /><span>Start server</span></>}</Button>}
                    {!startPending && (startup === 'game' || ['Process running', 'Starting', 'Listening', 'Ready', 'Stopping'].includes(status?.state ?? '') || pending === `stop-${profile.id}`) && <Button className="server-primary-action" disabled={!!pending || dirty || status?.state === 'Stopping'} onClick={async () => { if (!await confirmOwnerLifecycle(profile.id, 'Stop')) return; hideConnectionDetails(addressKey); hideConnectionDetails(passwordKey); void run(`stop-${profile.id}`, `/api/local/profiles/${profile.id}/stop`, 'POST') }}>{pending === `stop-${profile.id}` || status?.state === 'Stopping' ? <><Icon name="loader" /><span>Stopping…</span></> : <><Icon name="stop" /><span>Stop server</span></>}</Button>}
                    {!startup && ['Unknown', 'Failed'].includes(status?.state ?? 'Unknown') && <Button className="secondary" onClick={() => selectHostTab('logs')}><Icon name="warning" />Review server</Button>}
                    <Button className="secondary server-invite-button" disabled={!!pending || dirty || !friendAppAddress} onClick={() => void changeScope(() => { setHostServerTab('overview'); setUiPreferences(current => ({ ...current, serverTabs: { ...current.serverTabs, [profile.id]: 'overview' } })); void inviteFriend(profile.id) })}><Icon name="invite" /><span>Invite friends</span></Button>
                    <Button className="secondary icon-button" aria-label="More server actions" title="More server actions" onClick={() => setServerToolsProfileId(profile.id)}><Icon name="more" /></Button>
                  </div>
                  <ServerStartupProgress {...status} requestPending={startPending}
                    logEndpoint={['Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(profile.kind) ? `/api/local/profiles/${profile.id}/logs` : undefined}
                    showConsole={hostServerTab !== 'logs'}
                    onOpenLogs={['Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(profile.kind) ? () => selectHostTab('logs') : undefined} />
                  {!startup && status && !['Offline', 'Ready'].includes(status.state) && !isPlayerCountObservationUnavailable(status) && <p className="server-progress" role="status">{status.detail}</p>}
                  {dirty && <DismissibleNotice scope={connectionKey} as="p" className="inline-blocker">Save or cancel setup changes first.</DismissibleNotice>}
                  {!friendAppAddress && <DismissibleNotice scope={connectionKey} as="p" className="inline-blocker">Set this PC’s address before inviting friends. <Button className="text-button" onClick={() => openHostSettings('network')}>Network</Button></DismissibleNotice>}
                  {!dirty && hasUnsavedServerEdits && <DismissibleNotice scope={connectionKey} as="p" className="inline-blocker">Save or discard file changes before starting.</DismissibleNotice>}
                  {dataRecovery?.lifecycleBlocked && <DismissibleNotice scope={connectionKey} as="p" className="inline-blocker">Review data recovery before starting or restarting. <Button className="text-button" onClick={() => openHostSettings('diagnostics')}>Review recovery</Button></DismissibleNotice>}
                  {profile.maintenance?.enabled && <DismissibleNotice scope={connectionKey} as="p" className="inline-blocker">Maintenance is on; Friend controls are paused. <Button className="text-button" onClick={() => selectHostTab('setup')}>Continue maintenance</Button></DismissibleNotice>}
                  <ActionFeedback notice={currentActionNotice(actionNotices[profile.id], snapshot, feedbackNowMs)} />
                </div>
                <div className="server-tab-content">
                {hostServerTab === 'overview' && sharingProfile.kind !== 'Custom' && sharingProfile.sharedSavesEnabled && <SharedHostNotice
                  profileId={sharingProfile.id} connectionId={null} disabled={!!pending} visible={workspacePage === 'host'}
                  onConnected={async () => {
                    if (await switchMode('friend')) { setShowPairing(false); setJoinSection('play') }
                  }} />}
                {hostServerTab === 'overview' && profile.kind !== 'Custom' && <SharedHostingIdentity profileId={profile.id} host
                  worldName={profile.worldId} serverState={status?.state ?? 'Unknown'} serverStatusFresh={!loadError} observedHostAddress={snapshot.settings.companionEndpoint} />}
                {hostServerTab === 'overview' && inviteProfileId === profile.id && <div className="inline-invite">
                  {invitation ? <><div className="invite-ready"><span><Icon name={activeInviteWarning ? 'warning' : 'check'} /></span><div><strong>{activeInviteWarning ? 'Server code ready, connection needs attention' : 'Server code ready'}</strong><p>{activeInviteWarning ? 'Fix the connection below before sending the code.' : 'Send it privately. The same code keeps working until you replace it.'}</p></div></div>
                    {activeInviteWarning && <DismissibleNotice scope={connectionKey} as="p" className="connection-warning" role="alert">{activeInviteWarning}</DismissibleNotice>}
                    {!activeInviteWarning && currentRouteResult?.state === 'Not reachable' && <DismissibleNotice scope={connectionKey} as="p" className="connection-warning" role="alert">The internet test could not reach this PC at {new Date(currentRouteResult.checkedUtc).toLocaleTimeString()}. Open Friend access to fix the connection before sharing.</DismissibleNotice>}
                    <div className="actions">{activeInviteWarning
                      ? <Button disabled={!!pending} onClick={() => void inviteFriend(profile.id)}><Icon name="refresh" />Try connection again</Button>
                      : <Button onClick={() => void copyText(invitation, 'Server code')}><Icon name="copy" />Copy code</Button>}
                      <Button className="text-button" onClick={() => { setInviteProfileId(''); setInvitation(''); setInviteListenerWarning(null) }}>Done</Button></div>
                    <details className="advanced-block"><summary>New PCs and replacing the code</summary><label className="check-row"><Input type="checkbox" checked={pairingRequireApproval} onChange={event => setPairingRequireApproval(event.target.checked)} />Ask me to approve each new PC before it can connect</label><div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => void issueInvite(profile.id)}>Save approval setting</Button><Button className="danger-outline" disabled={!!pending} onClick={() => void issueInvite(profile.id, true)}>Replace code and remove old access</Button></div><small>Replacing the code disconnects PCs that joined with the old code. To remove only one PC, use Friend access.</small></details></>
                    : inviteListenerWarning ? <><DismissibleNotice scope={connectionKey} as="p" className="connection-warning" role="alert">{inviteListenerWarning}</DismissibleNotice>
                      <div className="actions"><Button disabled={!!pending} onClick={() => void inviteFriend(profile.id)}><Icon name="refresh" />Try again</Button>
                        <Button className="text-button" onClick={() => { setInviteProfileId(''); setInviteListenerWarning(null) }}>Done</Button></div></>
                      : pending === 'invite' ? <p className="helper-text" role="status">Getting server code…</p>
                        : <><DismissibleNotice scope={connectionKey} as="p" className="connection-warning" role="alert">The server code was not loaded.</DismissibleNotice><div className="actions"><Button onClick={() => void inviteFriend(profile.id)}>Try again</Button><Button className="text-button" onClick={() => setInviteProfileId('')}>Done</Button></div></>}
                </div>}
                {hostServerTab === 'overview' && ['Ready', 'Listening'].includes(status?.state ?? '') && gameAddress && <ConnectionDetails fields={connectionFields}
                  refreshing={checkingPorts || detectingPublicIp}
                  note={status?.state === 'Listening' ? 'The server is listening on this PC. Test joining and saving in the game.' : undefined} />}
                {hostServerTab === 'overview' && ['Ready', 'Listening'].includes(status?.state ?? '') && <p className="helper-text local-game-address">Playing on this PC? Join <code>127.0.0.1:{profile.gamePort}</code>.</p>}
                {hostServerTab === 'overview' && status?.state === 'Ready' && snapshot.settings.autoShutdownEnabled && <Button className="text-button" onClick={() => selectHostTab('players')}>Add shutdown time</Button>}
                {hostServerTab === 'overview' && <>
                  {status?.state === 'Offline' && !startPending && <p className="server-idle-hint">Start this server to play. Join details appear here when it is running.</p>}
                  {['Ready', 'Listening'].includes(status?.state ?? '') && !gameAddress && <div className="connection-empty"><h2>Connection details</h2><p>The game address is unavailable. Check Network before sharing.</p><Button className="secondary" onClick={() => openHostSettings('network')}><Icon name="link" />Set connection address</Button></div>}
                  {shownBackupStatus?.lastFailureUtc && (!shownBackupStatus.lastSuccessfulUtc || Date.parse(shownBackupStatus.lastFailureUtc) > Date.parse(shownBackupStatus.lastSuccessfulUtc)) && <DismissibleNotice scope={connectionKey} noticeKey={shownBackupStatus.lastFailureUtc} as="p" className="inline-blocker" role="status"><Icon name="warning" />The latest backup attempt failed. <Button className="text-button" onClick={() => selectHostTab('backups')}>Review backups</Button></DismissibleNotice>}
                </>}
                {hostServerTab === 'logs' && <PaneErrorBoundary title="Server logs" resetKey={profile.id}><ServerLogViewer endpoint={`/api/local/profiles/${profile.id}/logs`}
                  onNavigateIssue={destination => destination === 'health' ? void run(`health-${profile.id}`, `/api/local/profiles/${profile.id}/health`, 'POST') : selectHostTab('files')}
                  visible={workspacePage === 'host' && hostServerTab === 'logs'} /></PaneErrorBoundary>}
                {hostServerTab === 'chat' && <PaneErrorBoundary title="Server chat" resetKey={profile.id}>
                  <ServerChat profileId={profile.id} host quietMode={uiPreferences.quietMode} onDraftStateChange={keepChatGuard} visible={workspacePage === 'host' && hostServerTab === 'chat'} />
                </PaneErrorBoundary>}
                {hostServerTab === 'setup' && ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(profile.kind) &&
                  <details className="server-support-disclosure"><summary><Icon name="game" />Game version & add-ons</summary><PaneErrorBoundary title="Pre-join requirements" resetKey={profile.id}><GameCompatibilityPanel key={profile.id} profileId={profile.id} host /></PaneErrorBoundary></details>}
                {hostServerTab === 'chat' && <details className="server-support-disclosure"><summary>Chat notices</summary><ServerChatCardSummary profileId={profile.id} host authorized
                  visible={workspacePage === 'host'} pollWhenClosed onOpenChat={() => selectHostTab('chat')} /></details>}
                {hostServerTab === 'files' && <ServerSettingsWorkspace key={profile.id} recoveryRequest={editorRecoveryRequest} settings={<PaneErrorBoundary title="Game settings" resetKey={profile.id}><GameSettingsPanel
                  key={profile.id} profileId={profile.id} state={status?.state ?? 'Unknown'} maintenance={!!profile.maintenance?.enabled}
                  busy={profileWorkBusy || dirty} recoveryBlocked={!!dataRecovery?.lifecycleBlocked}
                  onDraftGuardChange={keepEditorGuard} onPrepareMaintenance={() => selectHostTab('setup')} /></PaneErrorBoundary>}
                  files={<PaneErrorBoundary title="Server files" resetKey={profile.id}><ServerFilesPanel
                  profileId={profile.id} state={status?.state ?? 'Unknown'} maintenance={!!profile.maintenance?.enabled}
                  busy={profileWorkBusy || dirty} recoveryBlocked={!!dataRecovery?.lifecycleBlocked}
                  onDraftGuardChange={keepEditorGuard} onPrepareMaintenance={() => selectHostTab('setup')}
                  onStart={() => void startLocalProfile(profile)}
                  onOpenDoctor={() => openHostSettings('network')} /></PaneErrorBoundary>} />}
                <PaneErrorBoundary title="Recent sessions" resetKey={profile.id}><RecentSessions profileId={profile.id}
                  visible={workspacePage === 'host' && hostServerTab === 'sessions'} /></PaneErrorBoundary>
                {hostServerTab === 'sessions' && <PaneErrorBoundary title="Seven-day summary" resetKey={profile.id}><WeeklySummary
                  key={profile.id} profileId={profile.id} visible={workspacePage === 'host'} /></PaneErrorBoundary>}
                {hostServerTab === 'backups' && <HostBackupsPage key={profile.id} profile={profile}
                  status={shownBackupStatus} state={status?.state ?? 'Unknown'} busy={!!pending}
                  editDisabled={dirty || !!dataRecovery?.lifecycleBlocked} backingUp={pending === `manual-backup-${profile.id}`}
                  onBackup={() => void createManualBackup(profile.id)} onRefresh={() => void loadBackups(profile.id)}
                  onSafeRestart={status?.state === 'Ready' ? () => void safeRestart(profile.id) : undefined}
                  onSaveProtection={(expected, next) => saveWorldProtection(profile.id, expected, next)}>
                  {(section, selectProtectionSection) => <>
                    <div hidden={section !== 'backups'} className="protection-backup-library">
                      <PaneErrorBoundary title="Backups" resetKey={profile.id}><BackupCatalog
                  key={profile.id} profileId={profile.id} visible={workspacePage === 'host'} showOverview={false}
                  refreshKey={backupCatalogRevisions[profile.id] ?? 0} currentWorld={profile.worldId} currentGame={profile.kind}
                  offline={status?.state === 'Offline'} onChanged={() => void loadBackups(profile.id)}
                  onVerify={backup => verifyBackup(profile.id, backup.backupId)}
                  onVault={backup => copyBackupToVault(profile.id, backup.backupId)}
                  onHashRehearsal={backup => rehearseBackupRestore(profile.id, backup.backupId)}
                  onOwnerGameRehearsal={backup => prepareGameRehearsal(profile.id, backup.backupId)}
                  onRestore={review => restoreBackup(profile.id, review.backup.backupId)}
                  unavailableReasons={pending || dirty || dataRecovery?.lifecycleBlocked ? {
                    verify: 'Finish the current action or resolve the setup and recovery warning first.', vault: 'Finish the current action or resolve the setup and recovery warning first.',
                    hashRehearsal: 'Finish the current action or resolve the setup and recovery warning first.',
                    ownerGameRehearsal: 'Finish the current action or resolve the setup and recovery warning first.', restore: 'Finish the current action or resolve the setup and recovery warning first.'
                  } : undefined}
                  renderActions={backup => <Button className="secondary" disabled={!!pending || dirty || status?.state !== 'Offline'}
                    onClick={() => void prepareMoveKit(profile.id, backup.backupId)}>Prepare move kit</Button>} /></PaneErrorBoundary>
                      {!profile.worldLoadRehearsalId && profile.kind !== 'Custom' && <div className="protection-help">
                        <WorldLoadRehearsalPanel profileId={profile.id} backups={backupList?.backups ?? []} /></div>}
                    </div>
                    {profile.kind !== 'Custom' && <div hidden={section !== 'sharing'} className="protection-sharing-panel">
                      <HostSharedSaves profileId={sharingProfile.id} devices={companion?.devices ?? []} worldName={sharingProfile.worldId}
                      serverState={sharingStatus?.state ?? 'Unknown'} serverStatusFresh={!loadError} onOpenHostControls={() => selectHostServer(profile.id, 'overview')}
                      rollingBackupEnabled={sharingProfile.backups?.enabled === true}
                      currentAddress={snapshot.mode === 'Host' && draft?.companionEndpoint === snapshot.settings.companionEndpoint
                        ? snapshot.settings.companionEndpoint : ''}
                      onGrantChanged={async () => { await refreshCompanion(); applySnapshot(await readSnapshot()) }}
                      onOpenProtection={() => selectProtectionSection('settings')}
                      onOpenConnectionHelp={() => openHostSettings('network')} />
                    <WorldPeerPanel readOnly visible={section === 'sharing'} profileId={sharingProfile.id} />
                    </div>}
                    {profile.kind === 'Custom' && section === 'sharing' && <p className="helper-text">Shared hosting needs a built-in game. Use Setup for Custom server controls.</p>}
                  </>}
                </HostBackupsPage>}
                {hostServerTab === 'players' && status && <PaneErrorBoundary title="Players workspace" resetKey={profile.id}><PlayersPanel run={status} activity={snapshot.activity} nowMs={nowMs}
                  profile={profile} onOpenLogs={() => selectHostTab('logs')} onOpenHelp={() => openHostSettings('network')}
                  onCheckHealth={() => void run(`health-${profile.id}`, `/api/local/profiles/${profile.id}/health`, 'POST')}
                  refreshing={pending === `players-${profile.id}`} disabled={!!pending || dirty}
                  onRefresh={() => void run(`players-${profile.id}`, `/api/local/profiles/${profile.id}/players/refresh`, 'POST')} /></PaneErrorBoundary>}
                {hostServerTab === 'players' && status?.state === 'Ready' && snapshot.settings.autoShutdownEnabled && count !== null && <details className="server-support-disclosure"><summary><Icon name="clock" />Add shutdown time</summary>
                  <TimerPresets minutes={countdownExtensions[profile.id] ?? '15'} deadline={count === 0 ? status.autoShutdownAtUtc : null} savedMinutes={status.addedShutdownMinutes}
                    busy={!!pending || dirty} onChange={minutes => setCountdownExtensions(current => ({ ...current, [profile.id]: minutes }))}
                    onAdd={minutes => void extendCountdown(profile.id, minutes)} />
                  <div className="timer-extension"><label>Custom minutes<Input type="number" min="1" max="1440" step="1" value={countdownExtensions[profile.id] ?? '15'} disabled={!!pending || dirty} onChange={event => setCountdownExtensions(current => ({ ...current, [profile.id]: event.target.value }))} /></label><Button className="secondary" disabled={!!pending || dirty} onClick={() => void extendCountdown(profile.id)}>{pending === `extend-${profile.id}` ? <><Icon name="loader" />Adding…</> : 'Add time'}</Button></div>
                </details>}
                {profile.maintenance?.enabled && <DismissibleNotice scope={connectionKey} hidden={hostServerTab !== 'players'} className="notice bad" role="status"><strong>Maintenance mode is on</strong><p>{profile.maintenance.message || 'Friends can see status, but remote Start, Stop, and Restart are paused.'}</p><Button className="secondary" disabled={!!pending || dirty} onClick={() => void saveMaintenance(profile, false)}>End maintenance</Button></DismissibleNotice>}
                <div hidden={hostServerTab !== 'setup'}><MaintenanceGuide enabled={!!profile.maintenance?.enabled}
                  message={maintenanceMessages[profile.id] ?? profile.maintenance?.message ?? ''}
                  state={status?.state ?? 'Unknown'} onlinePlayers={status?.onlinePlayers ?? null}
                  countTrusted={!!status?.playerCountTrusted} lastBackupUtc={shownBackupStatus?.lastSuccessfulUtc ?? null}
                  busy={!!pending || dirty} onMessage={message => setMaintenanceMessages(current => ({ ...current, [profile.id]: message }))}
                  onToggle={enabled => void saveMaintenance(profile, enabled)}
                  onStop={() => void run(`stop-${profile.id}`, `/api/local/profiles/${profile.id}/stop`, 'POST')}
                  onBackup={() => void createManualBackup(profile.id)}
                  onStart={() => void startLocalProfile(profile)}
                  onOpenDoctor={() => openHostSettings('network')} /></div>





                {hostServerTab === 'overview' && status?.state === 'Ready' && !detectedGameIp && <div className="next-action"><span>Your public game address is not available yet.</span><Button className="text-button" onClick={() => openHostSettings('network')}>Check connection</Button></div>}
                {hostServerTab === 'setup' && profile.kind === 'Custom' && <div className="custom-certification">
                  <div><strong>Custom server control test</strong><span className={`pill ${certification?.certified ? 'certified' : ''}`}>{certification?.certified ? 'Remote-ready' : certification?.inProgress ? 'Test in progress' : 'Not tested'}</span></div>
                  <p>{certification?.message ?? 'Complete the control test before Friends or the idle timer can stop, restart, or switch this server.'}</p>
                  {certification?.onlinePlayers != null && <small>Latest test result: {certification.onlinePlayers} player{certification.onlinePlayers === 1 ? '' : 's'} online.</small>}
                  <div className="actions">
                    {!certification?.certified && !certification?.inProgress && certification?.stage !== 'Failed' && <Button className="secondary" disabled={!!pending || dirty || status?.state !== 'Offline'} onClick={() => void customCertificationAction(profile.id, 'begin')}>Start control test</Button>}
                    {certification?.inProgress && <Button className="secondary" disabled={!!pending || dirty} onClick={() => void customCertificationAction(profile.id, 'status')}>{pending === `certification-status-${profile.id}` ? 'Checking…' : 'Check test step'}</Button>}
                    {certification?.inProgress && ['ConfirmFirstChange', 'ConfirmSecondChange'].includes(certification.stage) && <Button disabled={!!pending || dirty} onClick={() => void customCertificationAction(profile.id, 'confirm')}>{certification.stage === 'ConfirmFirstChange' ? 'Confirm change was made' : 'Confirm change survived'}</Button>}
                    {(certification?.inProgress || certification?.stage === 'Failed') && <Button className="text-button" disabled={!!pending} onClick={() => void customCertificationAction(profile.id, 'cancel')}>Cancel test</Button>}
                    {certification?.certified && <Button className="danger-outline" disabled={!!pending} onClick={async () => { if (await confirm({ title: 'Remove Custom control approval?', confirmLabel: 'Remove control approval', destructive: true, description: 'Remote Stop, Restart, replacement and automatic shutdown turn off for this Custom server. Local controls remain available.' })) void customCertificationAction(profile.id, 'revoke') }}>Remove control approval</Button>}
                  </div>
                  {!certification?.certified && status?.state !== 'Offline' && !certification?.inProgress && certification?.stage !== 'Failed' && <small>Stop the server on this PC before starting the test.</small>}
                </div>}
                {(hostServerTab === 'overview' || hostServerTab === 'setup') && recovery && (recovery.state === 'Suspended'
                  ? <DismissibleNotice className="safety-status warning-text" role="alert" scope={profile.id} noticeKey={recovery.attempts}>
                    <strong>Crash recovery: Suspended</strong><p>Suspended after {recovery.attempts} failed attempts.</p>{recovery.lastFailure && <small>{recovery.lastFailure}</small>}
                  </DismissibleNotice>
                  : <div className="safety-status"><strong>Crash recovery: {recovery.state}</strong><p>{recovery.state === 'Pending' && recovery.nextAttemptUtc ? `Attempt ${recovery.attempts + 1} of 3 after ${new Date(recovery.nextAttemptUtc).toLocaleString()}.` : recovery.state === 'Starting' ? `Recovery attempt ${recovery.attempts} of 3 is starting.` : recovery.state === 'Recovered' ? `Ready again after ${recovery.attempts} attempt${recovery.attempts === 1 ? '' : 's'}.` : `Suspended after ${recovery.attempts} failed attempts.`}</p>{recovery.lastFailure && <small>{recovery.lastFailure}</small>}</div>)}
                {serverToolsProfileId === profile.id && <SettingsDialog title="Server tools" description={`Actions for ${profile.name}.`} busy={!!pending}
                  onClose={() => setServerToolsProfileId('')} footer={<Button className="secondary" disabled={!!pending} onClick={() => setServerToolsProfileId('')}>Done</Button>}>

                  <div className="server-tools-menu actions">
                    {status?.state === 'Ready' && <Button className="secondary" disabled={!!pending || dirty || dataRecovery?.lifecycleBlocked} title={dataRecovery?.lifecycleBlocked ? 'Resolve the local data recovery warning first.' : undefined} onClick={async () => { if (!await confirmOwnerLifecycle(profile.id, 'Restart')) return; hideConnectionDetails(addressKey); hideConnectionDetails(passwordKey); void run(`restart-${profile.id}`, `/api/local/profiles/${profile.id}/restart`, 'POST') }}><Icon name={pending === `restart-${profile.id}` ? 'loader' : 'refresh'} />{pending === `restart-${profile.id}` ? 'Restarting…' : 'Restart server'}</Button>}
                    {status?.state === 'Ready' && <Button className="secondary" disabled={!!pending || dirty || dataRecovery?.lifecycleBlocked} onClick={() => void safeRestart(profile.id)}><Icon name={pending === `safe-restart-${profile.id}` ? 'loader' : 'archive'} />{pending === `safe-restart-${profile.id}` ? 'Safely restarting…' : 'Back up and restart'}</Button>}
                    <Button className="secondary" disabled={!!pending || status?.state !== 'Offline'} onClick={() => openSetup(profile.id)}><Icon name="settings" />Edit setup</Button><Button className="secondary" onClick={() => openHostSettings('network')}><Icon name="link" />Network</Button><Button className="secondary" disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/health`, 'POST')}><Icon name="refresh" />Check server health</Button>
                    <Button className="secondary" onClick={() => openHostSettings('access', profile.id)}><Icon name="users" />Friend access</Button>
                    {profile.kind === 'Custom' && <Button className="text-button" onClick={() => selectHostTab('setup')}>Review Custom control test</Button>}
                    <Button className="secondary" onClick={() => setServerNotification({ id: profile.id, name: profile.name })}><Icon name="bell" />Server notifications</Button>
                    {status?.state === 'Failed' && <Button className="text-button" disabled={!!pending || dirty} onClick={async () => {
                      if (await confirm({ title: 'Archive this session?', confirmLabel: 'Archive session', description: 'TogetherServer must confirm that this server has stopped.' }))
                        void run(profile.id, `/api/local/profiles/${profile.id}/forget`, 'POST')
                    }}>Archive stopped session</Button>}</div>
                  {pending && <p className="helper-text" role="status">Completing the current action…</p>}
                  {dirty && <DismissibleNotice scope={connectionKey} as="p" className="warning-text">Save or cancel setup changes before using server tools.</DismissibleNotice>}
                  {dataRecovery?.lifecycleBlocked && <DismissibleNotice scope={connectionKey} as="p" className="warning-text">Resolve local data recovery before restarting.</DismissibleNotice>}
                  {status?.state !== 'Offline' && <p className="helper-text">Stop this server before editing its setup.</p>}
                  {status?.state === 'Unknown' && !isPlayerCountObservationUnavailable(status) && <DismissibleNotice scope={connectionKey} as="p" className="warning-text">TogetherServer cannot confirm which server is running. Start, Stop, archive, restore, and world reuse are blocked. Review Recovery before continuing.</DismissibleNotice>}
                <details className="server-support-disclosure"><summary><Icon name="plug" />Status & connection checks</summary>
                  {status && ['Offline', 'Ready'].includes(status.state) && <p className="helper-text">{status.detail}</p>}
                  {status?.state === 'Ready' && <small>{profile.kind === 'Fixture' ? 'Test data only; no real game was checked.' : 'The game reports ready on this PC. Test joining from another PC.'}</small>}
                  {status?.state === 'Offline' && <HostStartConnectionNotice profile={profile} ports={portDiagnostics} routeCheck={internetRouteCheck}
                    onOpenConnection={() => openHostSettings('network')}
                    onTestControl={() => void checkInternetRoute()} testingControl={checkingInternetRoute} />}
                  <PaneErrorBoundary title="Connection readiness" resetKey={profile.id}><ServerReadiness profileId={profile.id} run={status} status={status?.state ?? 'Unknown'} ports={portDiagnostics} routeCheck={internetRouteCheck}
                    busy={checkingPorts || !!pending} refreshing={checkingPorts} onRefresh={() => void checkPorts(true)} onOpenConnection={() => openHostSettings('network')} /></PaneErrorBoundary>
                </details>
                </SettingsDialog>}
                  {hostServerTab === 'setup' && ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(profile.kind) && <details className="advanced-block"><summary>Update the game server safely</summary><ol className="update-game-steps"><li>Stop this server, then choose Back up now while it is Offline.</li><li>Verify the backup. Copy it to another location or test restore if needed.</li><li>Use the game provider's installer or update action yourself.</li><li>Start the updated server. Test a Friend join and a saved change, then record them in Connection help. Earlier confirmations no longer apply after an update.</li></ol><Button className="text-button" onClick={() => openHostSettings('network')}>Open Connection Doctor</Button></details>}

                </div>
              </article>
            })}
              </div>

          {companion?.devices.some(device => device.paired && !device.revoked) && <div className="access-strip"><span>Friend controls are <strong>{draft.remoteControlsEnabled ? 'on' : 'paused'}</strong> · {companion.devices.filter(device => device.paired && !device.revoked).length} connected PC{companion.devices.filter(device => device.paired && !device.revoked).length === 1 ? '' : 's'}</span>
            <Button className="secondary" disabled={!!pending || dirty} onClick={() => openHostSettings('access', selectedHostProfile?.id)}>Manage friend access</Button></div>}
            </section>
          </div>
        </section>
        </>}



        {savedProfiles.length === 0 && !showSetup && <HostFirstServer busy={!!pending}
          resume={draft.profiles.length > 0 && dirty || !!pausedSetupProfileId} recovered={setupDraftRecovery.recovered !== null}
          onCreate={addProfile} onResume={continueSetup} onJoin={() => void switchMode('friend')}
          onMove={() => openHostSettings('advanced')}
          recovery={<EditorDraftRecovery {...setupDraftRecovery} purpose="setup" scope="host-setup" disabled={!!pending}
            onRecover={recoverSetupDraft} onDiscard={() => void discardSetupDraft()} />} />}

        {!showSetup && savedProfiles.length > 0 && <EditorDraftRecovery {...setupDraftRecovery} purpose="setup" scope="host-setup" disabled={!!pending}
          onRecover={recoverSetupDraft} onDiscard={() => void discardSetupDraft()} />}
        {!showSetup && savedProfiles.length > 0 && pausedSetupProfileId && <Button className="secondary" disabled={!!pending} onClick={continueSetup}>Resume unfinished setup</Button>}
        {!showSetup && firstSessionProfileId && (() => {
          const profile = savedProfiles.find(item => item.id === firstSessionProfileId)
          return profile && <SetupFirstSessionGuide profile={profile} state={snapshot.runs.find(run => run.profileId === profile.id)?.state ?? 'Unknown'}
            onDismiss={dismissFirstSessionGuide} onOpenTool={tool => {
              if (tool === 'connection' || tool === 'acceptance') openHostSettings('network')
              else if (tool === 'invite') { selectHostServer(profile.id, 'overview'); void inviteFriend(profile.id) }
              else selectHostServer(profile.id, tool === 'backups' ? 'backups' : 'overview')
            }} />
        })()}
        {showSetup && <HostSetupDialog dialogRef={setupRef} snapshot={snapshot} draft={draft}
          savedProfiles={savedProfiles} editedProfile={editedProfile} notice={notice} pending={pending} dirty={dirty}
          setupStep={setupStep} setupIssues={setupIssues} stepIssues={stepIssues} discovery={discovery}
          minecraftDiscovery={minecraftDiscovery} sourceRoots={sourceRoots} passwords={passwords}
          showPasswords={showPasswords} minecraftSetupMode={minecraftSetupMode} minecraftTerms={minecraftTerms}
          customScripts={customScripts} customScriptsSaved={customScriptsSaved}
          customScriptsLoading={customScriptsLoading} customScriptsChanged={customScriptsChanged}
          dataRecoveryBlocked={!!dataRecovery?.lifecycleBlocked} freshWorldsOnly={appInstance?.freshWorldsOnly ?? false} onCancel={cancelSetup}
          onFinishLater={finishSetupLater} onAddProfile={addProfile} onStepChange={setSetupStep}
          onReuseProfile={reuseProfile} setupDraftRecovery={setupDraftRecovery}
          onRecoverSetupDraft={recoverSetupDraft} onDiscardSetupDraft={() => void discardSetupDraft()}
          importReview={importReview} onConfirmImport={() => void confirmImport()} onCancelImport={cancelImport}
          onChangeGameKind={changeGameKind} onUpdateProfile={updateProfile}
          onImportWorld={(profile, root, worldId, folder) => void importWorld(profile, root, worldId, folder)}
          onBrowseWorld={(profile, folder) => void browseWorld(profile, folder)}
          onSourceRootChange={setSourceRoot}
          onPasswordChange={setPassword}
          onShowPasswordChange={setShowPassword}
          onBrowseCustomDirectory={profile => void browseCustomDirectory(profile)}
          onBrowseFactorio={(profile, target) => void browseFactorio(profile, target)}
          onBrowseTerraria={(profile, target) => void browseTerraria(profile, target)}
          onMinecraftSetupModeChange={setMinecraftSetupModeFor}
          onBrowseMinecraft={(profile, target) => void browseMinecraft(profile, target)}
          onApplyMinecraftInstallation={applyMinecraftInstallation} onScanMinecraft={folder => void scanMinecraft(folder)}
          onInstallMinecraft={profile => void installMinecraft(profile)}
          onMinecraftTermsChange={setMinecraftTermsFor}
          onScanValheim={() => void scanValheim()} onBrowseServer={profile => void browseServer(profile)}
          onEditCustomScripts={editCustomScripts} onUpdateCustomPort={updateCustomPort}
          onAddCustomPort={addCustomPort} onRemoveCustomPort={removeCustomPort}
          onRemoveProfile={profile => void removeProfile(profile)} onSave={startAfterSave => void saveSetup(startAfterSave)} />}
        </>}
        {hostScope === 'pc' && <section className="settings-workspace host-pc-workspace" aria-label="This Host PC">
          <div className="context-heading-utility"><Button className="secondary icon-button" disabled={!!pending} title="Return to selected server" aria-label="Return to selected server" onClick={() => void closeHostSettings()}><Icon name="server" /></Button></div>
          <div className="settings-content">
            {dirty && <DismissibleNotice as="p" className="warning-text" role="status">Finish or cancel your server setup changes before editing Host settings.</DismissibleNotice>}
            <ActionFeedback notice={notice} />
            {hostSettingsSection === 'access' && <section className="settings-section">
              <SettingsSectionHeading icon="users" title="Friend access" description="Choose a PC to manage its servers, permissions, and access time." />
              <div className="access-toggles settings-controls"><label className="setting-toggle"><span><strong>Allow Friend app connections</strong><small>Connections stay available while this Host app is running.</small></span><Input type="checkbox" checked={draft.companionListeningEnabled} disabled={!!pending} onChange={event => void saveHostFlags({ companionListeningEnabled: event.target.checked })} /></label>
                <label className="setting-toggle"><span><strong>Allow remote Start and Stop</strong><small>Each PC keeps its own permissions and safety checks.</small></span><Input type="checkbox" checked={draft.remoteControlsEnabled} disabled={!!pending || !draft.companionListeningEnabled} onChange={event => void saveHostFlags({ remoteControlsEnabled: event.target.checked })} /></label></div>
              <FriendPcFilters value={deviceFilters} servers={savedProfiles} resultCount={visibleDevices.length}
                totalCount={companion?.devices.filter(device => deviceFilters.status === 'revoked' ? device.revoked : !device.revoked).length ?? 0}
                onChange={setDeviceFilters} />
              {!companion ? <p role="status">Loading Friend access...</p> : visibleDevices.length ? <div className="friend-pc-grid" aria-label="Friend PCs">
                {visibleDevices.map(device => <FriendPcCard key={device.id} device={device} servers={savedProfiles} nowMs={nowMs} busy={!!pending}
                  onManage={() => openDeviceServerAccess(device)} onApprove={() => void approveDevice(device.id)} onDelete={() => void deleteDevice(device)}
                  feedback={<ActionFeedback notice={currentActionNotice(actionNotices[device.id], snapshot, feedbackNowMs)} />} />)}
              </div> : <div className="empty compact-empty">{deviceFilters.search || deviceFilters.status !== 'all' || deviceFilters.serverId !== 'all' ? <><p>No Friend PCs match these filters.</p><Button className="secondary" onClick={() => setDeviceFilters(defaultFriendPcFilters)}>Clear filters</Button></> :
                <p>No current Friend PCs. Choose Invite friends on a server to copy its private code.</p>}</div>}
            </section>}
            {(hostSettingsSection === 'stop' || hostSettingsSection === 'network' || hostSettingsSection === 'advanced') && <HostSettingsTools key={hostSettingsSection}
              section={hostSettingsSection} snapshot={snapshot} companion={companion} ports={portDiagnostics} routeCheck={internetRouteCheck}
              guideChecks={gettingStarted.active === 'host' && gettingStarted.step === 4}
              previousRouteVerdict={previousRouteVerdict} routeDiscovery={routeDiscovery} selectedProfileId={selectedHostProfileId} busy={!!pending} dirty={dirty}
              checkingPorts={checkingPorts} checkingRoute={checkingInternetRoute} detectingAddress={detectingPublicIp} friendAppStatus={friendAppStatus}
              addressError={publicIpDetection && !publicIpDetection.ok ? publicIpDetection.message : null}
              isStaging={appInstance?.isStaging ?? false} notice={notice} moveKit={moveKit} onClearNotice={() => setNotice(null)} onDraftStateChange={setHostSettingsDraftDirty} onSave={saveSettingsPatch}
              onAutoShutdown={enabled => void saveHostFlags({ autoShutdownEnabled: enabled })}
              keepAwake={{ enabled: draft.keepAwakeWhileHosting, detail: snapshot.hostingPower ? `${snapshot.hostingPower.state}. ${snapshot.hostingPower.message}` : undefined,
                onChange: enabled => void saveHostFlags({ keepAwakeWhileHosting: enabled }) }}
              onRefresh={() => { void checkPorts(true); void refreshCompanion() }} onTestRoute={() => void checkInternetRoute()} onDetectAddress={() => void detectPublicIp()}
              onOpenAccess={() => openHostSettings('access')} onOpenDiagnostics={() => openHostSettings('diagnostics')}
              onCertificate={action => void certificateAction(action)} onInspectMoveKit={() => void inspectMoveKit()} onNewHost={addProfile} />}
            {hostSettingsSection === 'diagnostics' && <PaneErrorBoundary title="App checks" resetKey={selectedHostProfileId}><OwnerDiagnostics
              selectedProfileId={selectedHostProfileId} profileOrder={savedProfiles.map(profile => profile.id)} onSelectedProfileIdChange={setSelectedHostProfileId} /></PaneErrorBoundary>}
          </div>
        </section>}
        {workspacePage === 'host' && hostScope === 'pc' && serverAccessDevice && <SettingsDialog title={`Manage access for ${serverAccessDevice.profileName?.trim() ? `${serverAccessDevice.profileName.trim()} (PC: ${serverAccessDevice.name})` : serverAccessDevice.name}`} wide busy={!!pending || deadlineDraftState.saving}
          description="Choose what this PC can access." onClose={closeDeviceServerAccess}
          footer={<><Button className="secondary" disabled={!!pending || deadlineDraftState.saving} onClick={closeDeviceServerAccess}>{deviceAccessTab === 'servers' ? 'Cancel' : 'Done'}</Button>
            {deviceAccessTab === 'servers' && <Button disabled={!!pending || !serverAccessDirty || serverAccessDevice.temporaryHelperActive || !serverAccessDevice.paired || serverAccessDevice.approvalPending}
              onClick={() => void saveDeviceServerAccess()}><Icon name={pending === serverAccessDevice.id ? 'loader' : 'check'} />{pending === serverAccessDevice.id ? 'Saving...' : 'Save access'}</Button>}</>}>
          <nav className="settings-dialog-tabs" aria-label="PC access sections">
            {([['permissions', 'shield', 'Permissions'], ['servers', 'server', 'Servers'], ['time', 'clock', 'Access time'], ['details', 'monitor', 'PC details']] as const).map(([tab, icon, label]) =>
              <Button key={tab} aria-current={deviceAccessTab === tab ? 'page' : undefined} className={deviceAccessTab === tab ? 'selected' : ''} disabled={!!pending || deadlineDraftState.saving}
                onClick={() => { if (tab === 'servers' && !serverAccessDirty) prepareDeviceServerAccess(serverAccessDevice); setDeviceAccessTab(tab); setRemoveDeviceConfirm(false); setRemoveServerConfirm(false) }}><Icon name={icon} />{label}</Button>)}
          </nav>
          <ActionFeedback notice={currentActionNotice(actionNotices[serverAccessDevice.id], snapshot, feedbackNowMs)} />
          {serverRemovalScope?.deviceId === serverAccessDevice.id && serverAccessDevice.assignedProfileIds.includes(serverRemovalScope.profileId) &&
            <section className="settings-dialog-stack" aria-label={`Access to ${serverRemovalScope.name}`}>
              <div className="settings-danger-row"><span><strong>Access to {serverRemovalScope.name}</strong><small>Remove only this server. Other server access stays saved.</small></span>
                <Button className="danger-outline" disabled={!!pending || serverAccessHasDraft || deadlineDraftState.saving}
                  onClick={() => setRemoveServerConfirm(true)}>Remove {serverRemovalScope.name} access</Button></div>
              {serverAccessHasDraft && <p className="helper-text">Save or discard your edits before removing server access.</p>}
              {removeServerConfirm && <div className="settings-inline-confirm" role="alert"><strong>Remove {serverRemovalScope.name} access for this PC?</strong>
                <p>This removes access to {serverRemovalScope.name}. You can invite this PC to that server again later.</p>
                <div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => setRemoveServerConfirm(false)}>Keep server access</Button>
                  <Button className="danger-outline" disabled={!!pending || serverAccessHasDraft || deadlineDraftState.saving}
                    onClick={() => void removeSelectedServerAccess()}>Confirm remove {serverRemovalScope.name} access</Button></div></div>}
            </section>}
          {notice && !notice.good && <DismissibleNotice noticeKey={notice.receivedAtMs} as="p" className="warning-text" role="status">{notice.text}</DismissibleNotice>}
          {discardDeviceEdits && <div className="settings-inline-confirm" role="alert"><strong>Discard unsaved PC changes?</strong><p>Changes you have already saved remain in place.</p><div className="actions"><Button className="secondary" disabled={!!pending || deadlineDraftState.saving} onClick={() => setDiscardDeviceEdits(false)}>Continue editing</Button><Button className="danger-outline" disabled={!!pending || deadlineDraftState.saving} onClick={finishDeviceServerAccess}>Discard changes</Button></div></div>}
          {deviceAccessTab === 'permissions' && <div className="settings-dialog-stack">
            {serverAccessDevice.temporaryHelperActive && <DismissibleNotice as="p" className="warning-text" role="status">End temporary helper access under Access time, then refresh to edit saved permissions.</DismissibleNotice>}
            {serverAccessDirty && <DismissibleNotice as="p" className="warning-text" role="status">Save or discard your server edits before changing usual permissions. <Button className="text-button" onClick={() => setDeviceAccessTab('servers')}>Review server edits</Button></DismissibleNotice>}
            <fieldset className="device-permissions-editor" disabled={!!pending || serverAccessDevice.temporaryHelperActive || !serverAccessDevice.paired || serverAccessDevice.approvalPending || serverAccessDirty}>
              <legend>Usual permissions</legend>
              {(() => {
                const selectedPreset = permissionPresetDraft[serverAccessDevice.id] ?? matchingPermissionPreset(serverAccessDevice)
                const preset = selectedPreset === 'custom' ? null : permissionPresets[selectedPreset]
                const changed = selectedPreset !== matchingPermissionPreset(serverAccessDevice)
                return <div className="device-preset-editor"><label>Permission preset<Select value={selectedPreset} onChange={event => setPermissionPresetDraft(current => ({ ...current, [serverAccessDevice.id]: event.target.value as PermissionPreset }))}>
                  <option value="status">Status only</option><option value="start">Can start</option><option value="helper">Trusted helper</option><option value="custom">Custom</option></Select></label>
                  <p className="helper-text">{preset?.detail ?? 'Choose permissions for this PC or each server.'}</p>
                  {preset && changed && <><FriendAccessReview before={{ ...serverAccessDevice, serverPermissions: serverAccessDevice.serverPermissions ?? [] }}
                    after={{ ...serverAccessDevice, ...permissionPresetRequest(selectedPreset as Exclude<PermissionPreset, 'custom'>), serverPermissions: [] }} servers={savedProfiles} clearsExceptions />
                    <div className="actions"><Button onClick={() => void applyDevicePermissionPreset(serverAccessDevice, selectedPreset)}><Icon name="check" />Apply preset</Button></div></>}
                </div>
              })()}
              <details className="settings-disclosure"><summary><Icon name="settings" />Individual permissions</summary><p className="helper-text">Changes save immediately. Other permissions stay as they are.</p>
                <div className="device-permissions-grid">{(Object.keys(permissionLabels) as PermissionAction[]).map(action => {
                  const mix = permissionMix(serverAccessDevice, action)
                  return <label className="device-permission-toggle" key={action}><MixedCheckbox type="checkbox" mixed={mix.mixed} checked={mix.all} onChange={event => void setDevicePermissions(serverAccessDevice, action, mix.mixed ? true : event.target.checked)} />
                    <span><strong><Icon name={permissionLabels[action].icon} />{permissionLabels[action].label}</strong><small>{mix.mixed ? `${mix.all ? 'On' : 'Off'} with server exceptions` : mix.all ? 'Allowed on every assigned server' : 'Off on every assigned server'}</small></span></label>
                })}</div><p className="helper-text">Stop requires an empty server. Restart needs Start and Stop permission. Logs and extra time have separate permissions.</p>
              </details>
            </fieldset>
            <label className="password-access-choice"><MixedCheckbox type="checkbox" aria-label="Share game passwords" mixed={passwordAccessMixed}
              checked={serverAccessDevice.canViewPassword ?? false}
              disabled={!!pending || serverAccessDirty || !serverAccessDevice.paired || serverAccessDevice.approvalPending || serverAccessDevice.revoked}
              onChange={event => void setDevicePasswordPermission(serverAccessDevice, passwordAccessMixed ? true : event.target.checked)} />
              <span><strong><Icon name="shield" />Share game passwords</strong><small>Let this PC show or copy the current Valheim password for its assigned servers. Passwords use the encrypted app connection. A copied password cannot be recalled.</small>
                <small>{passwordAccessMixed ? 'Some servers have different password permissions. ' : ''}{passwordAccessCount} of {passwordAccessServers.length} assigned Valheim servers allow it.</small>
                <small>This permission is separate from helper access and presets. Choose Servers to set an exception for one server.</small></span></label>
            <div className="settings-detail-row device-assignment-row"><div><strong>{serverAccessDevice.assignedProfileIds.length} assigned {serverAccessDevice.assignedProfileIds.length === 1 ? 'server' : 'servers'}</strong><small>{serverAssignmentPreview(serverAccessDevice, savedProfiles)}</small></div>
              <Button className="secondary" disabled={!!pending} onClick={() => { if (!serverAccessDirty) prepareDeviceServerAccess(serverAccessDevice); setDeviceAccessTab('servers') }}><Icon name="server" />Choose servers</Button></div>
            <details className="settings-disclosure"><summary><Icon name="eye" />Current effective access</summary><FriendAccessSummary device={serverAccessDevice} servers={savedProfiles} nowMs={nowMs} /></details>
          </div>}
          {deviceAccessTab === 'servers' && <div className="settings-dialog-stack">
            <p className="helper-text">Sharing a server lets this PC see its status and address. Choose its other permissions below.</p>
            {serverAccessDevice.temporaryHelperActive && <DismissibleNotice as="p" className="warning-text" role="status">End temporary helper access and refresh before editing saved server permissions.</DismissibleNotice>}
            <fieldset className="server-picker-fieldset" disabled={!!pending || serverAccessDevice.temporaryHelperActive || !serverAccessDevice.paired || serverAccessDevice.approvalPending}>
              <legend className="sr-only">Server access</legend>
              <label className="server-picker-search">Search servers<Input value={serverAccessSearch} placeholder="Search by server or game" onChange={event => setServerAccessSearch(event.target.value)} /></label>
              <div className="server-picker-toolbar"><strong>{serverAccessDraft.length} of {savedProfiles.length} selected</strong><div className="actions"><Button className="text-button" disabled={!visibleServerAccessProfiles.length} onClick={() => setServerAccessDraft(current => [...new Set([...current, ...visibleServerAccessProfiles.map(profile => profile.id)])])}>{normalizedServerSearch ? 'Select all results' : 'Select all'}</Button><Button className="text-button" disabled={!serverAccessDraft.length} onClick={() => setServerAccessDraft([])}>Clear all</Button></div></div>
              <div className="server-picker-list" role="group" aria-label="Saved servers">{visibleServerAccessProfiles.map(profile => {
                const assigned = serverAccessDraft.includes(profile.id)
                const permission = serverPermissionDraft[profile.id] ?? { canStart: serverAccessDevice.canStart, canStop: serverAccessDevice.canStop, canExtendTimer: serverAccessDevice.canExtendTimer, canViewLogs: serverAccessDevice.canViewLogs,
                  canViewPassword: devicePasswordPermission(serverAccessDevice, profile.id) }
                return <div className="server-picker-option" key={profile.id}><label className="server-picker-access"><Input type="checkbox" checked={assigned} onChange={event => setServerAccessDraft(current => event.target.checked ? [...new Set([...current, profile.id])] : current.filter(id => id !== profile.id))} /><span><strong>{profile.name}</strong><small>{profileGameLabel(profile)}</small></span></label>
                  <div className="server-picker-permissions" aria-label={`${profile.name} permissions`}>{(['canStart', 'canStop', 'canExtendTimer', 'canViewLogs'] as PermissionAction[]).map(action => <label key={action}><Input type="checkbox" checked={permission[action]} disabled={!assigned} onChange={event => setServerPermissionDraft(current => ({ ...current, [profile.id]: { ...permission, [action]: event.target.checked } }))} /><Icon name={permissionLabels[action].icon} />{action === 'canStart' ? 'Start' : action === 'canStop' ? 'Stop' : action === 'canExtendTimer' ? 'Add time' : 'View logs'}</label>)}
                    {profile.kind === 'Valheim' && <label><Input type="checkbox" aria-label={`Game password for ${profile.name}`} checked={permission.canViewPassword ?? false} disabled={!assigned}
                      onChange={event => setServerPermissionDraft(current => ({ ...current, [profile.id]: { ...permission, canViewPassword: event.target.checked } }))} /><Icon name="shield" />Game password</label>}</div></div>
              })}{!visibleServerAccessProfiles.length && <p className="server-picker-empty">{savedProfiles.length ? `No servers match “${serverAccessSearch.trim()}”.` : 'No saved servers are available.'}</p>}</div>
            </fieldset>
            {serverAccessDirty && <><FriendAccessReview before={{ ...serverAccessDevice, serverPermissions: serverAccessDevice.serverPermissions ?? [] }}
              after={{ ...serverAccessDevice, assignedProfileIds: serverAccessDraft, serverPermissions: serverAccessDraft.map(profileId => ({ ...devicePermission(serverAccessDevice, profileId), ...serverPermissionDraft[profileId] })) }} servers={savedProfiles} />
              <div className="actions"><Button className="text-button" disabled={!!pending} onClick={() => prepareDeviceServerAccess(serverAccessDevice)}>Discard server edits</Button></div></>}
          </div>}
          <div className="settings-dialog-stack" hidden={deviceAccessTab !== 'time'}>
            <OwnerAccessDeadlineEditor device={serverAccessDevice} disabled={!!pending || !serverAccessDevice.paired} onSave={request => saveDeviceAccessExpiry(serverAccessDevice.id, request)} onRefresh={refreshCompanion} onDraftStateChange={setDeadlineDraftState} />
            <TemporaryHelperAccess device={serverAccessDevice} nowMs={nowMs} busy={!!pending || serverAccessDirty || deadlineDraftState.saving}
              onGrant={duration => void saveTemporaryHelper(serverAccessDevice.id, duration)} onEnd={() => void saveTemporaryHelper(serverAccessDevice.id)} />
            {serverAccessDirty && <p className="helper-text">Save or discard server edits before changing temporary helper access.</p>}
          </div>
          {deviceAccessTab === 'details' && <div className="settings-dialog-stack">
            <div className="device-name-editor"><label>PC name<Input value={deviceNames[serverAccessDevice.id] ?? serverAccessDevice.name} maxLength={48} disabled={!!pending} onChange={event => setDeviceNames(current => ({ ...current, [serverAccessDevice.id]: event.target.value }))} /></label>
              <Button className="secondary" disabled={!!pending || !(deviceNames[serverAccessDevice.id] ?? serverAccessDevice.name).trim() || (deviceNames[serverAccessDevice.id] ?? serverAccessDevice.name).trim() === serverAccessDevice.name} onClick={() => void saveDeviceName(serverAccessDevice.id)}>Save name</Button></div>
            <div className="settings-detail-row"><strong>Paired with</strong><span>{serverAccessDevice.profileId === '00000000-0000-0000-0000-000000000000' ? 'An older code' : `Code for ${savedProfiles.find(profile => profile.id === serverAccessDevice.profileId)?.name ?? 'a removed server'}`}</span></div>
            <div className="settings-detail-row"><strong>Last authenticated report</strong><span>{serverAccessDevice.lastHeartbeatUtc ? new Date(serverAccessDevice.lastHeartbeatUtc).toLocaleString() : 'No fresh report'}</span></div>
            <div className="settings-detail-row"><strong>Renewable credential</strong><span>{serverAccessDevice.credentialExpiresUtc ? `Expires ${new Date(serverAccessDevice.credentialExpiresUtc).toLocaleString()}` : 'Expiry unavailable'}</span><small>Separate from the access limit under Access time.</small></div>
            {serverAccessDevice.approvalPending && <div className="actions"><Button disabled={!!pending} onClick={() => void approveDevice(serverAccessDevice.id)}>Approve this PC</Button></div>}
            <details className="settings-disclosure"><summary>All server access</summary>
              <div className="settings-danger-row"><span><strong>Remove all server access</strong><small>Ends this saved PC connection for every assigned server.</small></span><Button className="danger-outline" disabled={!!pending || serverAccessHasDraft || deadlineDraftState.saving} onClick={() => setRemoveDeviceConfirm(true)}>Remove all server access</Button></div>
              {removeDeviceConfirm && <div className="settings-inline-confirm" role="alert"><strong>Remove all server access for {serverAccessDevice.name}?</strong><p>This PC will need a new code to connect again.</p><div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => setRemoveDeviceConfirm(false)}>Keep access</Button><Button className="danger-outline" disabled={!!pending || serverAccessHasDraft || deadlineDraftState.saving} onClick={() => void revokeDevice(serverAccessDevice.id)}>Confirm remove all server access</Button></div></div>}
            </details>
          </div>}
        </SettingsDialog>}
        </ResizableWorkspace>}
      </div></main>
    </div>
    {serverNotification && <SettingsDialog title={`Notifications for ${serverNotification.name}`} busy={serverNotificationBusy}
      description="Notification choices for this server on this PC." onClose={() => setServerNotification(null)}
      footer={<Button className="secondary" disabled={serverNotificationBusy} onClick={() => setServerNotification(null)}>Done</Button>}>
      <ServerNotificationSettings serverId={serverNotification.id} serverName={serverNotification.name}
        connectionId={serverNotification.connectionId} onBusyChange={setServerNotificationBusy} />
    </SettingsDialog>}
    {gettingStarted.welcome && <GuideWelcome intent={gettingStarted.intent} busy={gettingStarted.busy || !!pending} error={gettingStarted.error}
      onStart={kind => void startGuide(kind)} onClose={() => void gettingStarted.dismiss()} />}
    {gettingStarted.active && <GuidedWalkthrough kind={gettingStarted.active} steps={guideSteps} stepIndex={gettingStarted.step}
      suspended={showSetup} busy={!!pending} onLocate={index => void locateGuideStep(gettingStarted.active!, index)}
      onStep={index => void locateGuideStep(gettingStarted.active!, index).then(ok => { if (ok) gettingStarted.stepTo(index) })}
      onClose={() => closeGuide()} onFinish={() => closeGuide(true)} />}
    {confirmationDialog}
    <StatusStrip hostText={snapshot?.mode === 'Host' ? activeRuns ? `${activeRuns} server${activeRuns === 1 ? '' : 's'} running` : 'Host idle' : 'Host capability available'}
      friendText={snapshot?.mode === 'Friend' ? friendConnectionHidden ? 'Choose a Host' : snapshot.state : friendAppStatus} pending={pendingDescription(pending, snapshot?.mode === 'Friend' ? friendProfiles : savedProfiles, currentOperationProfile.current[pending])}
      version={update?.currentVersion ?? '...'} />
  </div>
}

const appRoot = document.getElementById('root')
if (appRoot) {
  const root = createRoot(appRoot)
  if (new URLSearchParams(window.location.search).get('demo') === 'sharing') {
    void import('./developmentDemoBootstrap').then(async ({ prepareDevelopmentDemo }) => {
      const demo = await prepareDevelopmentDemo(window.location.search)
      if (!demo) {
        root.render(<p role="alert">This demo requires the TogetherServer DEVELOPMENT DEMO application.</p>)
        return
      }
      const activeDemo = demo
      function DemoApplication() {
        const revision = activeDemo.useDevelopmentDemoRevision()
        return <App key={revision} developmentDemoControls={<activeDemo.DevelopmentDemoControls />} />
      }
      root.render(<React.StrictMode><AppErrorBoundary><DemoApplication /></AppErrorBoundary></React.StrictMode>)
    }).catch(() => {
      root.render(<p role="alert">The isolated demo could not start. Reopen TogetherServer DEVELOPMENT DEMO.</p>)
    })
  } else {
    root.render(<React.StrictMode><AppErrorBoundary><App /></AppErrorBoundary></React.StrictMode>)
  }
}

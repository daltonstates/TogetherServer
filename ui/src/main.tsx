import React, { useCallback, useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { ApiError, changeJson, errorMessage, getJson } from './api'
import { AppErrorBoundary } from './AppErrorBoundary'
import { FriendAccessExpiredNotice, OwnerAccessDeadlineEditor, putDeviceAccessExpiry } from './AccessExpiry'
import { Button, Input, Select } from './Controls'
import { ConnectionDoctor } from './ConnectionDoctor'
import { FriendConnectionDoctor } from './FriendConnectionDoctor'
import { FriendRemoteRehearsal, HostRemoteRehearsal } from './RemoteRehearsal'
import { ConnectionDetails } from './ConnectionDetails'
import { JoinGuide } from './JoinGuide'
import { GameCompatibilityPanel } from './GameCompatibilityPanel'
import { OpenGameButton } from './OpenGameButton'
import { GameSettingsPanel } from './GameSettingsPanel'
import { BackupBookmarks } from './BackupBookmarks'
import { WeeklySummary } from './WeeklySummary'
import { MaintenanceGuide } from './MaintenanceGuide'
import { TemporaryHelperAccess } from './TemporaryHelperAccess'
import { DataRecoveryPanel } from './DataRecoveryPanel'
import { OwnerDiagnostics } from './OwnerDiagnostics'
import { PaneErrorBoundary } from './PaneErrorBoundary'
import { PlayersPanel } from './PlayersPanel'
import { RecentSessions } from './RecentSessions'
import { AllServerOverview } from './AllServerOverview'
import { WorldLoadRehearsalPanel } from './WorldLoadRehearsal'
import {
  HostSetupDialog
} from './features/setup/HostSetupDialog'
import { useHostSetup } from './features/setup/useHostSetup'
import {
  parseActionResult, parseAppInstance, parseBackupSafetyResult, parseBasicResult, parseCompanionInfo, parseCustomCertificationResult,
  parseDataRecoveryView, parseDesktopPreferenceResult, parseDesktopPreferences,
  parseFriendSnapshot, parseGameEndpointResult, parseInternetRouteCheck, parseInviteResult,
  parseInviteState, parsePasswordResult, parsePortDiagnostics, parsePublicIpDetection, parseRouteDiscovery,
  parseSnapshot, parseUpdateView, parseWorldBackupList, parseWorldBackupVerificationResult, parseHostMoveKitResult,
  type ActionResult, type AppInstanceView, type BasicResult, type CompanionInfo,
  type DataRecoveryView, type DesktopPreferences, type Device, type FriendIssue,
  type DeviceAccessExpiryRequest, type DeviceAccessExpiryResult,
  type FriendSnapshot, type GameEndpointResult, type PublicIpDetection, type PublicProfile, type RouteDiscovery, type Settings,
  type Snapshot, type UpdateView, type WorldBackupList, type WorldBackupVerificationResult, type HostMoveKitResult
} from './contracts'
import { Icon } from './Icon'
import { ServerReadiness, currentOutsideResult, type PortDiagnostics, type InternetRouteCheck } from './ServerReadiness'
import { FriendStartConnectionNotice, HostStartConnectionNotice } from './StartConnectionNotice'
import { ServerLogViewer, friendLogAvailability } from './ServerLogViewer'
import { ServerChat } from './ServerChat'
import { ServerFilesPanel } from './ServerFilesPanel'
import { FriendSharedWorlds, HostSharedSaves } from './SharedWorldControls'
import { gameLabel, profileGameLabel, type Profile } from './GameProfile'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'
import {
  devicePermission, globalPermissionRequest, matchingPermissionPreset, permissionMix,
  permissionPresetRequest, permissionPresets, type PermissionAction, type PermissionPreset
} from './permissionState'
import {
  activityAfterMarker,
  activityDestination,
  type ActivityDestination,
  collapseRepeatedActivity,
  readActivityClearMarkersFrom,
  withActivityClearMarker,
  writeActivityClearMarkersTo
} from './notificationState'
import {
  CommandPalette, StatusStrip, WorkspaceNavigation,
  type WorkspaceCommand, type WorkspacePage
} from './WorkspaceChrome'
import './theme.css'
import './style.css'
import './companion.css'

type HostSettingsSection = 'app' | 'access' | 'stop' | 'network' | 'diagnostics' | 'advanced'
type HostServerTab = 'overview' | 'chat' | 'players' | 'logs' | 'sessions' | 'backups' | 'files' | 'setup'
type ConnectionActivity = Record<string, 'copy' | 'reveal'>
type PermissionDraft = Record<string, { canStart: boolean; canStop: boolean; canExtendTimer: boolean; canViewLogs: boolean }>

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
        return { friend: 'Paste the current server code from the Host. An old code may have been replaced, or the Host may have removed this PC\'s access.',
          host: 'Open Invite friends and copy the current code. Check this Friend PC’s access if it connected before.' }
      case 'HostAddressMismatch': case 'InviteAddressInvalid':
        return { friend: 'Check the address you entered for an older code. New server codes already include the Host address.',
          host: 'Copy the current server code and check its public HTTPS address against the router’s WAN address.' }
      case 'HostIdentityMismatch':
        return { friend: 'Stop using this code and request a fresh copy through your usual trusted channel. Do not bypass the secure Host check.',
          host: 'Copy the current server code from the running Host app and verify its published address.' }
      case 'FriendNetworkUnavailable':
        return { friend: 'Restore this PC’s internet connection, then check that the server code has the Host’s current address.',
          host: 'If the Friend PC is online and still cannot connect, verify the published address.' }
      case 'HostPortClosed':
        return { friend: 'Check that the server code is current and that this PC can use the internet.',
          host: 'Keep TogetherServer running. Check the HTTPS listener, inbound Windows Firewall, and router TCP forwarding to the Host PC.' }
      case 'HostPortTimedOut': case 'HostTimedOut': case 'HostUnreachable':
        return { friend: 'Check this PC’s internet connection and the address in the current server code.',
          host: 'Check the HTTPS listener, inbound Windows Firewall, and router TCP forwarding. Compare the router WAN address with the server code; ask your ISP about shared-address NAT or inbound filtering if they differ.' }
      case 'HostBusy':
        return { friend: 'Wait a moment before trying the same server code again.',
          host: 'Keep the Host app running and check whether it is limiting or failing requests.' }
      case 'HostUnavailable': case 'HostInvalidResponse':
        return { friend: 'Wait until the Host confirms their app is running, then check the connection again.',
          host: 'Check the Host app and HTTPS listener. If it is responding with an error, review its local connection status.' }
      case 'HostAccessDenied':
        return { friend: 'Ask the Host whether this PC still has access. Use the current server code if the Host replaced it.',
          host: 'Check this Friend PC under Friend access in the Host app.' }
      case 'LocalAppUnavailable':
        return { friend: 'Reopen TogetherServer on this PC and try again.',
          host: 'No Host network change is needed until the Friend app can reach its own local service.' }
      default:
        return { friend: 'Check this PC’s internet connection and the address in the current server code.',
          host: 'Keep TogetherServer running. Check its HTTPS listener, inbound Windows Firewall, router TCP forwarding, and whether the ISP uses shared-address NAT.' }
    }
  })()
  return <div className="friend-connection-help"><div><strong>Check on this PC</strong><p>{steps.friend}</p></div>
    <div><strong>Ask the Host to check</strong><p>{steps.host}</p></div>
    <small>The Friend app connects outward. This PC does not need an inbound port forward.</small></div>
}

function FriendStopBlockers({ snapshot, profile }: { snapshot: FriendSnapshot; profile: PublicProfile }) {
  const authenticated = snapshot.state === 'Connected' || snapshot.state === 'Disabled'
  const stopVisible = profile.state === 'Ready' && snapshot.state === 'Connected' && profile.canStop && profile.canStopNow
  if (!authenticated || stopVisible) return null
  const blockers: string[] = []
  if (!snapshot.remoteControlsEnabled) blockers.push('The Host has paused remote Start and Stop.')
  if (!profile.canStop) blockers.push('Ask the Host to allow Stop requests for this server on this PC.')
  if (profile.stopReason) blockers.push(`Host safety: ${profile.stopReason}`)
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
  if (seconds === 0) return 'Stopping now…'
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

function ServerActivity({ state, online, capacity, deadline, timerReason, nowMs, players,
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
    {timerReason && <small className="idle-reason">Timer not running · {timerReason}</small>}
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

function change(path: string, method: 'POST' | 'PUT', body?: unknown, signal?: AbortSignal): Promise<BasicResult> {
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

function App() {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [appInstance, setAppInstance] = useState<AppInstanceView | null>(null)
  const [nowMs, setNowMs] = useState(() => Date.now())
  const [pending, setPending] = useState('')
  const [notice, setNotice] = useState<{ good: boolean; text: string } | null>(null)
  const [loadError, setLoadError] = useState('')
  const [update, setUpdate] = useState<UpdateView | null>(null)
  const [updateBusy, setUpdateBusy] = useState(false)
  const [notificationUnread, setNotificationUnread] = useState(false)
  const [workspacePage, setWorkspacePage] = useState<WorkspacePage>('host')
  const [commandPaletteOpen, setCommandPaletteOpen] = useState(false)
  const [selectedHostProfileId, setSelectedHostProfileId] = useState('')
  const [hostServerTab, setHostServerTab] = useState<HostServerTab>('overview')
  const [friendLogProfileId, setFriendLogProfileId] = useState('')
  const [friendChatProfileId, setFriendChatProfileId] = useState('')
  const [hostMobileDetail, setHostMobileDetail] = useState(false)
  const [activityClearMarkers, setActivityClearMarkers] = useState<Record<string, string>>(() =>
    readActivityClearMarkersFrom(() => window.localStorage))
  const [dismissedUpdateVersion, setDismissedUpdateVersion] = useState<string | null>(null)
  const [showUpdatePrompt, setShowUpdatePrompt] = useState(false)
  const [desktopPreferences, setDesktopPreferences] = useState<DesktopPreferences | null>(null)
  const [desktopBusy, setDesktopBusy] = useState(false)
  const [companion, setCompanion] = useState<CompanionInfo | null>(null)
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
  const [gameEndpointResults, setGameEndpointResults] = useState<Record<string, GameEndpointResult>>({})
  const [backupLists, setBackupLists] = useState<Record<string, WorldBackupList>>({})
  const [backupVerifications, setBackupVerifications] = useState<Record<string, WorldBackupVerificationResult>>({})
  const [moveKit, setMoveKit] = useState<HostMoveKitResult | null>(null)
  const [pairIssue, setPairIssue] = useState<FriendIssue | null>(null)
  const [showPairing, setShowPairing] = useState(false)
  const [countdownExtensions, setCountdownExtensions] = useState<Record<string, string>>({})
  const [dataRecovery, setDataRecovery] = useState<DataRecoveryView | null>(null)
  const [recoveryConfirmed, setRecoveryConfirmed] = useState(false)
  const [serverAccessDeviceId, setServerAccessDeviceId] = useState('')
  const [serverAccessDraft, setServerAccessDraft] = useState<string[]>([])
  const [serverPermissionDraft, setServerPermissionDraft] = useState<PermissionDraft>({})
  const [serverAccessSearch, setServerAccessSearch] = useState('')
  const [hostSettingsSection, setHostSettingsSection] = useState<HostSettingsSection>('app')
  const [revealedConnections, setRevealedConnections] = useState<Record<string, boolean>>({})
  const [revealedGamePasswords, setRevealedGamePasswords] = useState<Record<string, string>>({})
  const [connectionActivity, setConnectionActivity] = useState<ConnectionActivity>({})
  const inviteLoad = useRef(0)
  const snapshotEpochRef = useRef(0)
  const liveConnectionKeysRef = useRef<Set<string>>(new Set())
  const connectionRevealRequestRef = useRef<Record<string, number>>({})
  const addressRecoveryRef = useRef<HTMLDetailsElement>(null)
  const recoveryAddressInputRef = useRef<HTMLInputElement>(null)
  const serverAccessRef = useModalDialog(!!serverAccessDeviceId)
  const updatePromptRef = useModalDialog(showUpdatePrompt)
  const workspaceNavigationRef = useRef<(page: WorkspacePage) => void>(() => {})

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
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
      const page = ({ '1': 'host', '2': 'join', '3': 'attention', '4': 'settings' } as const)[event.key]
      if (page) { event.preventDefault(); workspaceNavigationRef.current(page) }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [commandPaletteOpen, hostMobileDetail, snapshot?.mode, workspacePage])

  const applySnapshot = useCallback((next: Snapshot) => {
    snapshotEpochRef.current += 1
    setSnapshot(next)
  }, [])
  const setup = useHostSetup({ snapshot, pending, setPending, setNotice, applySnapshot,
    dataRecoveryBlocked: !!dataRecovery?.lifecycleBlocked, instance: appInstance })
  const {
    draft, dirty, passwords, customScripts, customScriptsSaved, customScriptsLoading, discovery,
    minecraftDiscovery, minecraftTerms, sourceRoots, showSetup, setupStep, minecraftSetupMode,
    showPasswords, editedProfile, setupIssues, stepIssues, customScriptsChanged, setupRef,
    sensitiveDraft, edit, acceptSavedSettings, setDraftIfClean, syncControlPolicy, syncDetectedPublicIp,
    synchronizeHostSnapshot, resetForMode, saveSetup, updateProfile, editCustomScripts, updateCustomPort,
    addCustomPort, removeCustomPort, browseCustomDirectory, browseFactorio, browseTerraria, applyMinecraftInstallation, scanMinecraft,
    installMinecraft, changeGameKind, addProfile, removeProfile, cancelSetup, finishSetupLater, openSetup,
    continueSetup, scanValheim, importWorld, browseServer, browseMinecraft, browseWorld, setSetupStep,
    setSourceRoot, setPassword, setShowPassword, setMinecraftSetupModeFor, setMinecraftTermsFor
  } = setup
  const currentMode = snapshot?.mode
  const currentFriendEndpoint = snapshot?.mode === 'Friend' ? snapshot.endpoint : ''
  const currentFriendConnectionId = snapshot?.mode === 'Friend' ? snapshot.connectionId : ''
  const currentFriendConnectionName = snapshot?.mode === 'Friend' ? snapshot.connectionName : null
  const activitySource = snapshot?.mode === 'Host' ? 'host' :
    snapshot?.mode === 'Friend' ? `friend:${snapshot.connectionId || 'unpaired'}` : 'loading'
  const recentActivity = snapshot?.activity ?? []
  const visibleActivity = activityAfterMarker(recentActivity, activityClearMarkers[activitySource])
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
      setSelectedHostProfileId(profiles[0]?.id ?? '')
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

  const latestActivityId = visibleActivity[0]?.id ?? null
  useEffect(() => {
    if (latestActivityId) setNotificationUnread(true)
  }, [activitySource, latestActivityId])

  useEffect(() => {
    const availableVersion = update?.state === 'Available' ? update.latestVersion : null
    setShowUpdatePrompt(!!availableVersion && dismissedUpdateVersion !== availableVersion)
  }, [dismissedUpdateVersion, update?.latestVersion, update?.state])

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
    try {
      const result = await change('/api/local/quit', 'POST')
      if (!result.ok) setNotice({ good: false, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
  }

  useSingleFlightPolling(async signal => {
    try { setUpdate(await getJson('/api/local/update', parseUpdateView, signal)) }
    catch { /* An update check must not interrupt hosting or joining. */ }
  }, 30 * 60 * 1000)

  const checkUpdate = async () => {
    setUpdateBusy(true)
    try {
      const result = await changeJson('/api/local/update/check', 'POST', parseUpdateView)
      setUpdate(result)
      if (result.state !== 'Available') setNotice({ good: result.state === 'Current' || result.state === 'NoRelease', text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setUpdateBusy(false) }
  }

  const installUpdate = async () => {
    setUpdateBusy(true)
    setNotice(null)
    try {
      const result = await change('/api/local/update/install', 'POST')
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setUpdateBusy(false) }
  }

  const dismissUpdatePrompt = () => {
    if (update?.latestVersion) setDismissedUpdateVersion(update.latestVersion)
    setShowUpdatePrompt(false)
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

  useSingleFlightPolling(async signal => {
    const startedEpoch = snapshotEpochRef.current
    const [next, recovery, currentInstance] = await Promise.all([
      readSnapshot(signal),
      getJson('/api/local/data-recovery', parseDataRecoveryView, signal),
      getJson('/api/local/instance', parseAppInstance, signal)
    ])
    const hostDetails = next.mode === 'Host'
      ? await Promise.all([
        getJson('/api/local/companion', parseCompanionInfo, signal),
        getJson('/api/local/network/ports', parsePortDiagnostics, signal)
      ])
      : null
    if (signal.aborted || snapshotEpochRef.current !== startedEpoch) return

    setSnapshot(next)
    setAppInstance(currentInstance)
    setDataRecovery(recovery)
    setLoadError('')
    if (next.mode !== 'Host') return

    synchronizeHostSnapshot(next)

    if (hostDetails) {
      const [current, ports] = hostDetails
      setCompanion(current)
      setPortDiagnostics(ports)
      setDeviceNames(names => {
        const updated = { ...names }
        for (const device of current.devices) if (updated[device.id] === undefined) updated[device.id] = device.name
        return updated
      })
    }
  }, 3000, error => setLoadError(errorMessage(error)))

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
    } catch {
      setNotice({ good: false, text: `Could not copy ${label.toLowerCase()}. ${fallback}` })
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
    try { await copyText(value, label, 'Show the value, then select and copy it instead.') }
    finally { setConnectionBusy(key, null) }
  }
  const detectPublicIp = useCallback(async () => {
    setDetectingPublicIp(true)
    try {
      const result = await changeJson('/api/local/network/detect-public-ip', 'POST', parsePublicIpDetection)
      setPublicIpDetection(result)
      if (result.ok && result.address && result.snapshot) {
        applySnapshot(result.snapshot)
        syncDetectedPublicIp(result.address, result.snapshot.settings)
      }
    } catch {
      setPublicIpDetection({ ok: false, code: 'PublicIpUnavailable', address: null,
        message: 'Could not check the public IPv4 address. Check this PC’s Internet connection and retry.' })
    } finally { setDetectingPublicIp(false) }
  }, [applySnapshot, syncDetectedPublicIp])
  useEffect(() => {
    if (currentMode !== 'Host') return
    void detectPublicIp()
    const timer = window.setInterval(() => void detectPublicIp(), 15 * 60 * 1000)
    return () => window.clearInterval(timer)
  }, [currentMode, detectPublicIp])
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
      setInternetRouteCheck({ state: 'Unavailable', detail: `Internet TCP test failed: ${errorMessage(error)}`,
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
    if (refresh && !window.confirm('Replace this server code? PCs that used the old code will lose access and must connect again with the new code. Any other servers you gave those PCs will also be removed.')) return null
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
        ? result.listenerWarning || `The HTTPS listener on TCP ${draft?.companionPort ?? 'the configured port'} did not start. Check Connection help before sharing this code.`
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
    if (!window.confirm('Revoke this Friend device now? Its next request will be denied.')) return
    setPending(id)
    try {
      const result = await change(`/api/local/devices/${id}/revoke`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const approveDevice = async (id: string) => {
    setPending(id)
    try {
      const result = await change(`/api/local/devices/${id}/approve`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const saveDeviceName = async (id: string) => {
    setPending(id)
    try {
      const name = (deviceNames[id] ?? '').trim()
      const result = await change(`/api/local/devices/${id}/name`, 'PUT', { name })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) setDeviceNames(current => ({ ...current, [id]: name }))
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const setDevicePermissions = async (device: Device, action: PermissionAction, value: boolean) => {
    setPending(device.id)
    try {
      const result = await change(`/api/local/devices/${device.id}/permissions`, 'PUT',
        globalPermissionRequest(device, action, value))
      setNotice({ good: result.ok, text: result.message })
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const applyDevicePermissionPreset = async (device: Device, preset: PermissionPreset) => {
    if (preset === 'custom') return
    setPending(device.id)
    try {
      const result = await change(`/api/local/devices/${device.id}/permissions`, 'PUT',
        permissionPresetRequest(preset))
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) setPermissionPresetDraft(current => ({ ...current, [device.id]: preset }))
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const saveTemporaryHelper = async (deviceId: string, duration?: 'OneHour' | 'EightHours') => {
    setPending(`temporary-helper-${deviceId}`)
    try {
      const result = await change(`/api/local/devices/${deviceId}/temporary-helper`, 'PUT',
        duration ? { duration } : { clear: true })
      setNotice({ good: result.ok, text: result.message })
      await refreshCompanion()
      setPermissionPresetDraft(current => { const next = { ...current }; delete next[deviceId]; return next })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
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
      setPairIssue({ code: 'InvalidInvite', message: 'Paste the invite from your friend.' })
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
  const selectFriendConnection = async (id: string) => {
    setPending('select-connection')
    try {
      const result = await change(`/api/local/friend/connections/${id}/select`, 'POST')
      if (!result.ok) setNotice({ good: false, text: result.message })
      else {
        setShowPairing(false)
        applySnapshot(await readSnapshot())
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
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
  const openFriendAddressRecovery = () => {
    if (addressRecoveryRef.current) {
      addressRecoveryRef.current.open = true
      addressRecoveryRef.current.scrollIntoView({ block: 'center' })
    }
    recoveryAddressInputRef.current?.focus()
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
  const forgetFriendConnection = async () => {
    if (snapshot?.mode !== 'Friend' || !snapshot.connectionId ||
        !window.confirm('Forget this saved Host? TogetherServer will first try to revoke this PC on the Host.')) return
    setPending('forget-connection')
    try {
      const result = await change(`/api/local/friend/connections/${snapshot.connectionId}/forget`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) { setShowPairing(false); applySnapshot(await readSnapshot()) }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const probeGameEndpoint = async (profileId: string) => {
    setPending(`probe-game-${profileId}`)
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/probe-game`, 'POST', parseGameEndpointResult)
      setGameEndpointResults(current => ({ ...current, [profileId]: result }))
    } catch (error) {
      setGameEndpointResults(current => ({ ...current, [profileId]: { answered: false,
        code: 'ProbeFailed', message: errorMessage(error), checkedUtc: new Date().toISOString(), onlinePlayers: null, maxPlayers: null } }))
    } finally { setPending('') }
  }
  const friendAction = async (id: string, action: 'start' | 'stop' | 'restart' | 'replace' | 'extend' | 'refresh') => {
    const key = `friend-${action}-${id}`
    setPending(key)
    if (action === 'stop' || action === 'restart') hideConnectionDetails(`friend-${snapshot?.mode === 'Friend' ? snapshot.connectionId : ''}-${id}-address`)
    try {
      const result = await change(`/api/local/friend/${id}/${action}`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      applySnapshot(await readSnapshot())
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const switchMode = async (mode: 'host' | 'friend') => {
    if ((dirty || sensitiveDraft) && !window.confirm('Discard unsaved server settings, passwords, and custom scripts and change pages?')) return
    setPending('mode')
    setNotice(null)
    try {
      const result = await change(`/api/local/mode/${mode}`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        setRevealedConnections({})
        setRevealedGamePasswords({})
        const next = await readSnapshot()
        applySnapshot(next)
        resetForMode(next)
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const acknowledgeDataRecovery = async () => {
    if (snapshot?.mode !== 'Host') return
    if (!recoveryConfirmed) {
      setNotice({ good: false, text: dataRecovery?.lifecycleBlocked
        ? 'Confirm that no TogetherServer-managed game server is still running.'
        : 'Confirm that you reviewed the quarantined local data.' })
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
      setNotice({ good: false, text: 'Resolve the local data recovery warning before starting or restarting a server.' })
      return
    }
    setPending(key)
    setNotice(null)
    try {
      const result = await changeAction(path, method, body)
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      if (result.ok && key.startsWith('save')) acceptSavedSettings(result.snapshot.settings)
      if (result.ok) void checkPorts()
    } catch (error) {
      setNotice({ good: false, text: errorMessage(error) })
    } finally { setPending('') }
  }
  const loadBackups = async (profileId: string) => {
    setPending(`backups-${profileId}`)
    try {
      const result = await getJson(`/api/local/profiles/${profileId}/backups`, parseWorldBackupList)
      setBackupLists(current => ({ ...current, [profileId]: result }))
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const createManualBackup = async (profileId: string) => {
    setPending(`manual-backup-${profileId}`)
    setNotice(null)
    try {
      const result = await changeAction(`/api/local/profiles/${profileId}/backups/manual`, 'POST')
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        const list = await getJson(`/api/local/profiles/${profileId}/backups`, parseWorldBackupList)
        setBackupLists(current => ({ ...current, [profileId]: list }))
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const safeRestart = async (profileId: string) => {
    if (!window.confirm('Safe restart will gracefully stop this server, create an offline checkpoint, and only then start it again. If the checkpoint fails, the server stays offline. Continue?')) return
    setPending(`safe-restart-${profileId}`)
    setNotice(null)
    try {
      const result = await changeAction(`/api/local/profiles/${profileId}/safe-restart`, 'POST')
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      const list = await getJson(`/api/local/profiles/${profileId}/backups`, parseWorldBackupList)
      setBackupLists(current => ({ ...current, [profileId]: list }))
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const verifyBackup = async (profileId: string, backupId: string) => {
    setPending(`verify-backup-${backupId}`)
    setNotice(null)
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/backups/${backupId}/verify`, 'POST',
        parseWorldBackupVerificationResult)
      setBackupVerifications(current => ({ ...current, [backupId]: result }))
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const copyBackupToVault = async (profileId: string, backupId: string) => {
    setPending(`vault-backup-${backupId}`)
    setNotice(null)
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/backups/${backupId}/vault`, 'POST',
        parseBackupSafetyResult)
      if (result.code !== 'Canceled') setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const prepareMoveKit = async (profileId: string, backupId: string) => {
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
    setPending(`rehearse-backup-${backupId}`)
    setNotice(null)
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/backups/${backupId}/rehearse`, 'POST',
        parseBackupSafetyResult)
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const restoreBackup = async (profileId: string, backupId: string, createdUtc: string) => {
    if (!window.confirm(`Restore the backup from ${new Date(createdUtc).toLocaleString()}? The server must remain offline. TogetherServer will first retain a pre-restore snapshot.`)) return
    setPending(`restore-${profileId}`)
    setNotice(null)
    try {
      const result = await changeAction(`/api/local/profiles/${profileId}/backups/${backupId}/restore`, 'POST')
      applySnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      await loadBackups(profileId)
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
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
  const openDeviceServerAccess = (device: Device) => {
    const savedIds = new Set(snapshot?.mode === 'Host' ? snapshot.settings.profiles.map(profile => profile.id) : [])
    setNotice(null)
    setServerAccessSearch('')
    setServerAccessDraft(device.assignedProfileIds.filter(id => savedIds.has(id)))
    setServerPermissionDraft(Object.fromEntries([...savedIds].map(profileId => {
      const permission = devicePermission(device, profileId)
      return [profileId, { canStart: permission.canStart, canStop: permission.canStop,
        canExtendTimer: permission.canExtendTimer, canViewLogs: permission.canViewLogs }]
    })))
    setServerAccessDeviceId(device.id)
  }
  const closeDeviceServerAccess = () => {
    if (pending) return
    setServerAccessDeviceId('')
    setServerAccessDraft([])
    setServerPermissionDraft({})
    setServerAccessSearch('')
    setNotice(null)
  }
  const saveDeviceServerAccess = async () => {
    if (!serverAccessDeviceId) return
    setPending(serverAccessDeviceId)
    try {
      const result = await change(`/api/local/devices/${serverAccessDeviceId}/servers`, 'PUT',
        { profileIds: serverAccessDraft, permissions: serverAccessDraft.map(profileId => ({
          profileId,
          canStart: serverPermissionDraft[profileId]?.canStart ?? serverAccessDevice?.canStart ?? false,
          canStop: serverPermissionDraft[profileId]?.canStop ?? serverAccessDevice?.canStop ?? false,
          canExtendTimer: serverPermissionDraft[profileId]?.canExtendTimer ?? serverAccessDevice?.canExtendTimer ?? false,
          canViewLogs: serverPermissionDraft[profileId]?.canViewLogs ?? serverAccessDevice?.canViewLogs ?? false
        })) })
      setNotice({ good: result.ok, text: result.message })
      await refreshCompanion()
      if (result.ok) {
        setServerAccessDeviceId('')
        setServerAccessDraft([])
        setServerPermissionDraft({})
        setServerAccessSearch('')
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const extendCountdown = async (profileId: string) => {
    const minutes = Number(countdownExtensions[profileId] ?? '15')
    if (!Number.isSafeInteger(minutes) || minutes < 1) {
      setNotice({ good: false, text: 'Enter a positive whole number of extra minutes.' })
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
        ? enabled ? 'Maintenance mode enabled. Friends can still see status, but remote actions are paused.' : 'Maintenance mode ended.'
        : result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const changeRoute = (mode: Settings['connectionRoute']['mode'], address = '') => {
    if (!draft) return
    const selected = address.trim()
    const endpoint = selected ? `https://${selected}:${draft.companionPort}` :
      mode === 'DirectInternet' && draft.publicGameIp ? `https://${draft.publicGameIp}:${draft.companionPort}` : draft.companionEndpoint
    edit({ ...draft, connectionRoute: { mode, address: selected }, companionEndpoint: endpoint,
      companionBindAddress: mode === 'DirectInternet' ? '0.0.0.0' : selected || draft.companionBindAddress })
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
  const openHostSettings = (section: HostSettingsSection = 'access') => {
    setNotice(null)
    setHostSettingsSection(section)
    setWorkspacePage('settings')
    if (!routeDiscovery) void getJson('/api/local/network/routes', parseRouteDiscovery)
      .then(setRouteDiscovery)
      .catch(() => { /* Manual route entry remains available. */ })
  }
  const closeHostSettings = () => {
    if (!snapshot || pending) return
    if (snapshot.mode === 'Host' && dirty && !window.confirm('Discard unsaved advanced settings?')) return
    if (snapshot.mode === 'Host') acceptSavedSettings(snapshot.settings)
    setNotice(null)
    setWorkspacePage(snapshot.mode === 'Host' ? 'host' : 'join')
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

  const navigateWorkspace = (page: WorkspacePage) => {
    if (page === 'attention') {
      setNotificationUnread(false)
      setWorkspacePage(page)
      return
    }
    if (page === 'settings') {
      setHostSettingsSection('app')
      setWorkspacePage(page)
      return
    }
    if ((page === 'host' && snapshot?.mode === 'Host') || (page === 'join' && snapshot?.mode === 'Friend')) {
      setWorkspacePage(page)
      return
    }
    void switchMode(page === 'host' ? 'host' : 'friend')
  }
  workspaceNavigationRef.current = navigateWorkspace

  const openActivityDestination = (destination: ActivityDestination) => {
    if (destination.profileId) setSelectedHostProfileId(destination.profileId)
    if (destination.workspace === 'settings') {
      setHostSettingsSection(destination.section)
      setWorkspacePage('settings')
      return
    }
    setHostServerTab(destination.section)
    setHostMobileDetail(true)
    setWorkspacePage('host')
  }
  const openActivity = (item: NonNullable<Snapshot['activity']>[number]) => {
    const destination = activityDestination(item)
    if (destination) openActivityDestination(destination)
  }

  const detectedGameIp = snapshot?.mode === 'Host' && snapshot.settings.publicGameIpCheckedUtc &&
    Date.now() - Date.parse(snapshot.settings.publicGameIpCheckedUtc) < 60 * 60 * 1000
    ? snapshot.settings.publicGameIp : ''
  const friendAppAddress = hostAddress(draft?.companionEndpoint ?? '') ||
    (detectedGameIp ? `${detectedGameIp}${draft?.companionPort === 5131 ? '' : `:${draft?.companionPort}`}` : '')
  const savedProfiles = snapshot?.mode === 'Host' ? snapshot.settings.profiles : []
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
  const normalizedServerSearch = serverAccessSearch.trim().toLocaleLowerCase()
  const visibleServerAccessProfiles = savedProfiles.filter(profile => !normalizedServerSearch ||
    `${profile.name} ${profileGameLabel(profile)}`.toLocaleLowerCase().includes(normalizedServerSearch))
  const activeRuns = snapshot?.mode === 'Host'
    ? snapshot.runs.filter(run => ['Process running', 'Starting', 'Listening', 'Ready'].includes(run.state)).length : 0
  const currentRouteResult = !dirty && companion?.listenerActive
    ? currentOutsideResult(portDiagnostics?.control, internetRouteCheck) : null
  const previousRouteVerdict = !currentRouteResult &&
    (internetRouteCheck?.state === 'Reachable' || internetRouteCheck?.state === 'Not reachable')
  const activeInviteWarning = inviteListenerWarning || (invitation && companion?.listenerActive === false
    ? companion.listenerWarning || 'Friend app connections are off. Choose Invite friends again to start the HTTPS listener.'
    : null)
  const friendAppStatus = portDiagnostics?.control.remoteState === 'Friend connected' ? 'Friend connected'
    : currentRouteResult?.state === 'Reachable' ? 'reachable outside this network; Friend connection not tested'
      : companion?.listenerActive ? 'listening on this PC, outside route unconfirmed'
        : companion?.listenerState === 'Idle' ? 'waiting for a server code'
          : companion?.listenerWarning ? 'needs attention' : 'off'
  const developmentControlPort = snapshot?.mode === 'Host'
    ? snapshot.settings.companionPort : appInstance?.companionPort
  const updateBlockedReason = dirty ? 'Save setup changes before updating.' :
    activeRuns > 0 ? 'Stop hosted servers before updating.' : undefined
  const selectedHostProfile = savedProfiles.find(profile => profile.id === selectedHostProfileId) ?? savedProfiles[0]
  const selectedHostRun = snapshot?.mode === 'Host' && selectedHostProfile
    ? snapshot.runs.find(run => run.profileId === selectedHostProfile.id) : undefined
  const storageWarnings = snapshot?.mode === 'Host' ? snapshot.storageHealth?.warnings ?? [] : []
  const attentionCount = visibleActivity.length + storageWarnings.length + (notice ? 1 : 0) + (update?.state === 'Available' ? 1 : 0)
  const groupedActivity = collapseRepeatedActivity(visibleActivity)
  const commands: WorkspaceCommand[] = [
    { id: 'nav-host', label: 'Open Host', detail: 'Manage servers on this PC', icon: 'server', keywords: 'Alt+1', run: () => navigateWorkspace('host') },
    { id: 'nav-join', label: 'Open Join', detail: 'Connect to a friend without interrupting hosting', icon: 'link', keywords: 'Alt+2', run: () => navigateWorkspace('join') },
    { id: 'nav-attention', label: 'Open Attention Center', detail: `${attentionCount} current item${attentionCount === 1 ? '' : 's'}`, icon: 'bell', keywords: 'notifications activity Alt+3', run: () => navigateWorkspace('attention') },
    { id: 'nav-settings', label: 'Open Settings', detail: 'App, access, timer, and connection settings', icon: 'settings', keywords: 'Alt+4 preferences', run: () => navigateWorkspace('settings') },
    { id: 'add-server', label: 'Add a server', detail: 'Open the guided Host setup', icon: 'server', disabled: snapshot?.mode !== 'Host' || !!pending || dirty,
      run: () => { navigateWorkspace('host'); addProfile() } },
    { id: 'refresh-connections', label: 'Refresh connection details', detail: 'Run the existing read-only Host checks', icon: 'refresh', disabled: snapshot?.mode !== 'Host' || !!pending,
      run: () => { navigateWorkspace('host'); void checkPorts(true) } },
    { id: 'open-friend-access', label: 'Open Friend access', detail: 'Manage connected PCs and what they can do', icon: 'invite', disabled: snapshot?.mode !== 'Host' || savedProfiles.length === 0,
      run: () => openHostSettings('access') },
    { id: 'open-diagnostics', label: 'Open diagnostics', detail: 'Read-only preflight checks and redacted support export', icon: 'warning',
      keywords: 'support preflight report', disabled: snapshot?.mode !== 'Host', run: () => openHostSettings('diagnostics') },
    { id: 'guarded-lifecycle', label: `${selectedHostRun?.state === 'Offline' ? 'Start' : 'Stop'} selected server`,
      detail: 'Open Overview and use the server button there', icon: selectedHostRun?.state === 'Offline' ? 'play' : 'stop',
      disabled: !selectedHostProfile, run: () => { navigateWorkspace('host'); setHostServerTab('overview'); setHostMobileDetail(true) } }
  ]
  const pageTitle = workspacePage === 'host' ? 'Host' : workspacePage === 'join' ? 'Join' : workspacePage === 'attention' ? 'Attention Center' : 'Settings'
  const pageDescription = workspacePage === 'host'
    ? savedProfiles.length === 0 ? 'Set up a server, or switch to Join if a friend sent you a code.' : activeRuns ? `${activeRuns} ${activeRuns === 1 ? 'server is' : 'servers are'} running.` : 'Choose a server, then act from its focused workspace.'
    : workspacePage === 'join' ? 'Connect to a server without interrupting anything you host on this PC.'
      : workspacePage === 'attention' ? 'Updates, notices, and recent Host or Friend activity in one place.'
        : 'Application preferences and Host controls stay in one full-window workspace.'

  return <div className={appInstance?.isStaging ? 'shell staging-shell' : 'shell'}>
    <header className="topbar">
      <div className="brand"><span className="brand-mark">T</span><span><strong>{appInstance?.displayName ?? 'TogetherServer'}</strong><small>Game server workspace</small></span></div>
      <nav className="mode-switch legacy-mode-switch" aria-label="App pages">
        <Button aria-current={snapshot?.mode === 'Host' ? 'page' : undefined} className={snapshot?.mode === 'Host' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Host'} onClick={() => void switchMode('host')}><span className={activeRuns ? 'mode-dot active' : 'mode-dot'} />Host{activeRuns ? ` · ${activeRuns}` : ''}</Button>
        <Button aria-current={snapshot?.mode === 'Friend' ? 'page' : undefined} className={snapshot?.mode === 'Friend' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Friend'} onClick={() => void switchMode('friend')}>Join</Button>
      </nav>
      <div className="header-tools">
        <details className="notification-menu" onToggle={event => { if (event.currentTarget.open) setNotificationUnread(false) }}>
          <summary aria-label={notificationUnread ? 'Notifications, new activity' : 'Notifications'} title={notificationUnread ? 'New activity' : 'Notifications'}>
            <Icon name="bell" size={19} />
            {notificationUnread && <span className="notification-badge"><span className="sr-only">New activity</span></span>}
          </summary>
          <div className="notification-panel">
            <div className="notification-panel-heading"><div><strong>Notifications</strong><small>Recent app and connection activity</small></div>{visibleActivity.length > 0 && <Button className="text-button" onClick={clearNotificationActivity}>Clear activity</Button>}</div>
            {update?.state === 'Available' && <div className="notification-item update" role="status"><span><Icon name="refresh" /></span><div><strong>Update available · v{update.latestVersion}</strong><p>{updateBlockedReason ?? 'Restart TogetherServer to install the latest version.'}</p><Button disabled={updateBusy || !!pending || !!updateBlockedReason} title={updateBlockedReason} onClick={() => void installUpdate()}>{updateBusy ? <><Icon name="loader" />Preparing update…</> : 'Update and restart'}</Button></div></div>}
            {notice && <div className={`notification-item ${notice.good ? 'good' : 'bad'}`} role="status"><span><Icon name={notice.good ? 'check' : 'warning'} /></span><div><strong>{notice.good ? 'Updated' : 'Needs attention'}</strong><p>{notice.text}</p></div></div>}
            {visibleActivity.slice(0, 8).map(item => <div className={`notification-item ${item.severity === 'Warning' ? 'bad' : item.severity === 'Important' ? 'good' : ''}`} key={item.id}><span><Icon name={item.severity === 'Warning' ? 'warning' : 'check'} /></span><div><strong>{item.category}</strong><p>{item.message}</p><small>{new Date(item.occurredUtc).toLocaleString()}</small></div></div>)}
            {!notice && update?.state !== 'Available' && visibleActivity.length === 0 && <p className="notification-empty">No recent activity.</p>}
          </div>
        </details>
        <details className="app-menu"><summary aria-label="App settings" title="App settings"><Icon name="settings" size={19} /></summary><div className="app-menu-panel"><strong>App settings</strong>
          <div className="app-version"><span>Version {update?.currentVersion ?? 'checking…'}</span><Button className="text-button" disabled={updateBusy || !!pending || appInstance?.updatesAvailable === false} onClick={() => void checkUpdate()}>{appInstance?.updatesAvailable === false ? 'Updates off in staging' : updateBusy ? 'Checking…' : 'Check for updates'}</Button></div>
          <label className="check-row"><Input type="checkbox" checked={desktopPreferences?.launchAtLogin ?? false} disabled={!desktopPreferences?.available || !desktopPreferences.startupAvailable || desktopBusy} onChange={event => void saveDesktopPreference({ launchAtLogin: event.target.checked })} />Open at Windows sign-in</label><small>{appInstance?.isStaging ? 'Disabled in staging so the stable app keeps its sign-in setting.' : 'Starts quietly in the tray.'}</small>
          <label className="check-row"><Input type="checkbox" checked={desktopPreferences?.closeToTray ?? false} disabled={!desktopPreferences?.available || desktopBusy} onChange={event => void saveDesktopPreference({ closeToTray: event.target.checked })} />Close to tray</label><small>Hosting and Friend checks keep running.</small>
          <Button className="app-menu-quit" disabled={!desktopPreferences?.available} onClick={() => void quitApp()}>Quit {appInstance?.displayName ?? 'TogetherServer'}</Button>
        </div></details>
        <Button className="command-trigger" onClick={() => setCommandPaletteOpen(true)}><Icon name="search" size={16} /><span>Commands</span><kbd>Ctrl K</kbd></Button>
      </div>
    </header>

    {update?.state === 'Available' && <aside className="update-banner" role="status"><div><strong>TogetherServer {update.latestVersion} is available</strong><span>{updateBlockedReason ?? `Update and restart when ready · ${update.publisherTrust}.`}</span></div><Button disabled={updateBusy || !!pending || !!updateBlockedReason} title={updateBlockedReason} onClick={() => void installUpdate()}>{updateBusy ? <><Icon name="loader" />Preparing update…</> : 'Update and restart'}</Button></aside>}

    {appInstance?.isStaging && <aside className="staging-banner" role="status"><strong>DEVELOPMENT / STAGING</strong><span>Isolated ports: local app <code>{appInstance.localPort}</code> · Friend control <code>{developmentControlPort}</code>. Development servers, saved access, settings, and worlds stay in this separate instance. Production data is not loaded or copied.</span></aside>}

    {showUpdatePrompt && update?.state === 'Available' && <dialog ref={updatePromptRef} className="panel modal-dialog update-dialog" aria-labelledby="update-dialog-title" onCancel={event => { event.preventDefault(); dismissUpdatePrompt() }}>
      <div className="modal-heading"><div><h2 id="update-dialog-title">Update TogetherServer</h2><p>Version {update.latestVersion} is available. TogetherServer will reopen after the update.</p></div></div>
      <div className="update-dialog-content"><p>{updateBlockedReason ?? 'Before closing, TogetherServer verifies the download, preserves the previous EXE, and creates a same-user settings/access recovery checkpoint.'}</p><p className="helper-text">Publisher trust: {update.publisherTrust}. A trusted Windows publisher still requires the owner’s signing certificate; unsigned builds remain clearly labeled.</p>{notice && !notice.good && <div className="notice bad" role="status">{notice.text}</div>}</div>
      <div className="actions update-dialog-actions"><Button className="secondary" disabled={updateBusy} onClick={dismissUpdatePrompt}>Not now</Button><Button disabled={updateBusy || !!pending || !!updateBlockedReason} title={updateBlockedReason} onClick={() => void installUpdate()}>{updateBusy ? <><Icon name="loader" />Preparing update…</> : 'Update and restart'}</Button></div>
    </dialog>}

    <CommandPalette open={commandPaletteOpen} commands={commands} onClose={() => setCommandPaletteOpen(false)} />
    <div className="app-body">
      <WorkspaceNavigation page={workspacePage} activeRuns={activeRuns} unread={notificationUnread} onNavigate={navigateWorkspace} />
      <main className="workspace-main"><div className="workspace-scroll">
      <div className="page-heading"><div><span className="workspace-eyebrow">Workspace</span><h1>{pageTitle}</h1><p>{pageDescription}</p></div>
        {workspacePage === 'host' && savedProfiles.length > 0 && <div className="page-heading-actions"><Button disabled={!!pending || dirty} onClick={addProfile}>Add server</Button><Button className="secondary" disabled={!!pending || dirty} onClick={() => openHostSettings('access')}><Icon name="invite" />Friend access</Button></div>}
      </div>

      {loadError && <div className="notice bad" role="alert">Connection to this local app failed: {loadError}</div>}
      {(!snapshot || !appInstance) && !loadError && <section className="panel">Loading local state…</section>}

      {dataRecovery && <PaneErrorBoundary title="Data recovery" resetKey={dataRecovery.notices.map(item => item.detectedUtc).join('|')}><DataRecoveryPanel recovery={dataRecovery} mode={snapshot?.mode ?? null}
        runs={snapshot?.mode === 'Host' ? snapshot.runs : []}
        configuredProfileIds={snapshot?.mode === 'Host' ? snapshot.settings.profiles.map(profile => profile.id) : []}
        pending={pending} confirmed={recoveryConfirmed} onConfirmedChange={setRecoveryConfirmed}
        onAcknowledge={() => void acknowledgeDataRecovery()} onSwitchToHost={() => void switchMode('host')}
        onStopRecordedRun={profileId => void run(`recovery-stop-${profileId}`, `/api/local/profiles/${profileId}/stop`, 'POST')}
        onForgetRecordedRun={profileId => void run(`recovery-forget-${profileId}`, `/api/local/profiles/${profileId}/forget`, 'POST')} /></PaneErrorBoundary>}

      {snapshot?.mode === 'Host' && snapshot.startupRecovery?.previousSessionInterrupted &&
        <section className="panel startup-recovery" aria-labelledby="startup-recovery-title"><div className="section-heading"><div><h2 id="startup-recovery-title">Recovered app session</h2><p>{snapshot.startupRecovery.message}</p></div></div>
          <div className="choices">{snapshot.startupRecovery.items.map(item => {
            const profile = snapshot.settings.profiles.find(candidate => candidate.id === item.profileId)
            return <div className="choice" key={item.profileId}><span><strong>{profile?.name ?? 'Saved server'} · {item.state}</strong><small>{item.detail}</small></span>
              {item.canResume && <Button disabled={!!pending || dirty} onClick={() => {
                if (window.confirm('Resume hosting through the normal Start checks? TogetherServer will never start a second copy of a process it reattached to.'))
                  void run(`resume-${item.profileId}`, `/api/local/profiles/${item.profileId}/resume-hosting`, 'POST')
              }}>{pending === `resume-${item.profileId}` ? 'Resuming…' : 'Resume hosting'}</Button>}</div>
          })}</div>
          <small>{snapshot.startupRecovery.windowsRestartRegistered ? 'Windows can reopen this app after an application failure.' : 'Windows application-restart registration is unavailable in this build.'} Reopening the app never starts a game automatically.</small>
        </section>}

      {workspacePage === 'attention' && <PaneErrorBoundary title="Attention Center" resetKey={visibleActivity[0]?.id ?? 'empty'}><section className="attention-workspace" aria-label="Notifications and activity">
        <div className="attention-toolbar"><div><strong>{attentionCount ? `${attentionCount} current item${attentionCount === 1 ? '' : 's'}` : 'You are all caught up'}</strong><span>Recent app and connection activity</span></div>
          <div className="attention-toolbar-actions">{snapshot?.mode === 'Host' && <Button className="secondary" onClick={() => openHostSettings('diagnostics')}>Preflight & diagnostics</Button>}
            {visibleActivity.length > 0 && <Button className="secondary" onClick={clearNotificationActivity}>Clear activity</Button>}</div></div>
        <div className="attention-list">
          {snapshot?.mode === 'Host' && storageWarnings.map((warning, index) => <article className="notification-item bad" role="status" key={`storage-${index}`}><span><Icon name="warning" /></span><div><strong>Storage health</strong><p>{warning}</p></div></article>)}
          {update?.state === 'Available' && <article className="notification-item update" role="status"><span><Icon name="refresh" /></span><div><strong>Update available - v{update.latestVersion}</strong><p>{updateBlockedReason ?? 'Restart TogetherServer to install the latest version.'}</p><Button disabled={updateBusy || !!pending || !!updateBlockedReason} title={updateBlockedReason} onClick={() => void installUpdate()}>{updateBusy ? <><Icon name="loader" />Preparing update...</> : 'Update and restart'}</Button></div></article>}
          {notice && <article className={`notification-item ${notice.good ? 'good' : 'bad'}`} role="status"><span><Icon name={notice.good ? 'check' : 'warning'} /></span><div><strong>{notice.good ? 'Updated' : 'Needs attention'}</strong><p>{notice.text}</p></div></article>}
          {groupedActivity.map(({ item, repeatCount }) => {
            const destination = activityDestination(item)
            return <article className={`notification-item ${item.severity === 'Warning' ? 'bad' : item.severity === 'Important' ? 'good' : ''}`} key={item.id}><span><Icon name={item.severity === 'Warning' ? 'warning' : 'check'} /></span><div><strong>{item.category}</strong><p>{item.message}</p><small>{new Date(item.occurredUtc).toLocaleString()}{repeatCount > 1 ? ` · Repeated ${repeatCount} times` : ''}</small>{destination && snapshot?.mode === 'Host' && <Button className="text-button" onClick={() => openActivity(item)}>{destination.label}</Button>}</div></article>
          })}
          {!notice && update?.state !== 'Available' && visibleActivity.length === 0 && storageWarnings.length === 0 && <div className="attention-empty"><Icon name="check" size={22} /><strong>No recent activity</strong><p>Important Host, Friend, update, and recovery events will appear here.</p></div>}
        </div>
        {snapshot?.mode === 'Host' && snapshot.storageHealth && <details className="advanced-block storage-health-details"><summary>Storage and resource details</summary>
          <div className="storage-health-grid">{snapshot.storageHealth.locations.map(location => <div className="storage-health-item" key={location.id}><span><strong>{location.label}</strong><small>{location.state}</small></span><span>{location.availableSpaceBytes == null ? 'Space unavailable' : `${formatBytes(location.availableSpaceBytes)} available`}{location.usedBytes > 0 ? ` · ${formatBytes(location.usedBytes)} measured` : ''}</span><small>{location.detail}</small></div>)}</div>
          <p className="helper-text">{snapshot.storageHealth.resources.logicalProcessors} logical processors · {formatBytes(snapshot.storageHealth.resources.processWorkingSetBytes)} app working set. {snapshot.storageHealth.resources.evidenceBoundary}</p>
        </details>}
      </section></PaneErrorBoundary>}

      {workspacePage === 'settings' && snapshot?.mode === 'Friend' && <section className="settings-workspace" aria-labelledby="friend-app-settings-title">
        <div className="modal-heading"><div><h2 id="friend-app-settings-title">App settings</h2><p>Windows behavior and update preferences for this app.</p></div><Button className="secondary" disabled={!!pending} onClick={closeHostSettings}>Back to Join</Button></div>
        <div className="settings-content"><section className="settings-section app-settings-page"><h3>Application</h3>
          <div className="app-settings-grid">
            <label className="setting-toggle"><span><strong>Open at Windows sign-in</strong><small>{appInstance?.isStaging ? 'Disabled in staging so the stable app keeps its sign-in setting.' : 'Starts quietly in the tray.'}</small></span><Input type="checkbox" checked={desktopPreferences?.launchAtLogin ?? false} disabled={!desktopPreferences?.available || !desktopPreferences.startupAvailable || desktopBusy} onChange={event => void saveDesktopPreference({ launchAtLogin: event.target.checked })} /></label>
            <label className="setting-toggle"><span><strong>Close to tray</strong><small>Hosting and Friend checks keep running.</small></span><Input type="checkbox" checked={desktopPreferences?.closeToTray ?? false} disabled={!desktopPreferences?.available || desktopBusy} onChange={event => void saveDesktopPreference({ closeToTray: event.target.checked })} /></label>
          </div>
          <div className="settings-version-row"><span><strong>Version {update?.currentVersion ?? 'checking...'}</strong><small>{appInstance?.updatesAvailable === false ? 'Stable updates are disabled in staging.' : `Updates install only when you choose · ${update?.publisherTrust ?? 'checking trust'}. A verified local-state checkpoint is required before replacement.`}</small></span><Button className="secondary" disabled={updateBusy || !!pending || appInstance?.updatesAvailable === false} onClick={() => void checkUpdate()}>{appInstance?.updatesAvailable === false ? 'Updates off in staging' : updateBusy ? 'Checking...' : 'Check for updates'}</Button></div>
          <div className="settings-danger-row"><span><strong>Quit TogetherServer</strong><small>Hosted servers on this PC still keep the existing Quit guard.</small></span><Button className="secondary" disabled={!desktopPreferences?.available} onClick={() => void quitApp()}>Quit {appInstance?.displayName ?? 'TogetherServer'}</Button></div>
          <div className="shortcut-reference"><strong>Keyboard shortcuts</strong><span><kbd>Ctrl K</kbd> Command palette</span><span><kbd>Alt 1</kbd> Host</span><span><kbd>Alt 2</kbd> Join</span><span><kbd>Alt 3</kbd> Attention</span><span><kbd>Alt 4</kbd> Settings</span><span><kbd>Esc</kbd> Close or go back</span></div>
        </section></div>
      </section>}

      {workspacePage === 'join' && snapshot?.mode === 'Friend' && <>
        {(snapshot.connections?.length ?? 0) > 1 && <section className="panel saved-connections"><div className="section-heading"><div><h2>Saved servers</h2><p>Choose which Host you want to view.</p></div></div>
          <div className="choices">{snapshot.connections!.map(connection => <div className="choice" key={connection.connectionId}>
            <span><strong>{connection.connectionName ?? connection.profiles[0]?.name ?? hostAddress(connection.endpoint)}</strong><small>{gameLabel(connection.profiles[0]?.kind ?? 'Server')} · {connection.state}</small></span>
            <Button className="secondary" disabled={!!pending || connection.connectionId === snapshot.connectionId} onClick={() => void selectFriendConnection(connection.connectionId)}>{connection.connectionId === snapshot.connectionId ? 'Showing' : 'Show'}</Button>
          </div>)}</div>
        </section>}
        <section className="panel friend-panel friend-primary">
          <div className="section-heading"><div><h2>{snapshot.endpoint && !showPairing ? 'Connection' : snapshot.endpoint ? 'Add another server' : 'Paste your server code'}</h2><p>{snapshot.endpoint && !showPairing ? snapshot.detail : "Ask the Host to copy this server's current code."}</p></div></div>
          {snapshot.endpoint && !showPairing ? <>
            <div className={pending === 'poll' ? 'compact-status refreshing' : 'compact-status'} aria-busy={pending === 'poll'}><span className={`status ${statusTone(snapshot.state)}`}>{pending === 'poll' && <Icon name="loader" />}{snapshot.state === 'Disconnected/Unknown' ? 'Connection unknown' : snapshot.state}</span>
              <span>{snapshot.lastConnectedUtc ? `Last reached ${new Date(snapshot.lastConnectedUtc).toLocaleTimeString()}` : 'Waiting for a reply from the Host'}</span></div>
            {snapshot.expiryWarning && <div className="notice bad" role="status">{snapshot.expiryWarning}</div>}
            <FriendAccessExpiredNotice connectionCode={snapshot.connectionCode} />
            {snapshot.connectionCode && (snapshot.state === 'Disconnected/Unknown' || snapshot.state === 'Revoked' || snapshot.state === 'Awaiting approval') && <details className="troubleshoot-block" open><summary>{snapshot.state === 'Awaiting approval' ? 'Waiting for Host approval' : 'Troubleshoot connection'}</summary>{snapshot.state === 'Awaiting approval' ? <p>This PC is saved. Ask the Host to approve it under Friend access; you do not need a new code.</p> : <FriendConnectionHelp code={snapshot.connectionCode} />}</details>}
            <div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => void checkFriendConnection()}>{pending === 'poll' ? <><Icon name="loader" />Refreshing…</> : 'Check connection'}</Button>
              <Button className="text-button" onClick={() => { setShowPairing(true); setFriendHostAddress(''); setFriendInvite(''); setPairIssue(null) }}>Add another server</Button></div>
            <details className="advanced-block" ref={addressRecoveryRef}><summary>Connection identity and recovery</summary>
              <label>Saved connection name<div className="field-with-button"><Input value={friendConnectionName} maxLength={48} onChange={event => setFriendConnectionName(event.target.value)} /><Button className="secondary" disabled={!!pending || !friendConnectionName.trim() || friendConnectionName.trim() === snapshot.connectionName} onClick={() => void renameFriendConnection()}>Rename</Button></div></label>
              <p className="helper-text">Route: {snapshot.routeMode === 'PrivateMesh' ? 'Private mesh' : snapshot.routeMode === 'AdvancedAddress' ? 'Advanced address' : 'Direct Internet'}{snapshot.routeAddress ? ` (${snapshot.routeAddress})` : ''}. Host {snapshot.hostVersion ?? 'unknown'} · this app {snapshot.friendVersion ?? 'unknown'} · protocol {snapshot.hostProtocolVersion ?? 'unknown'}.</p>
              <p className="helper-text">Saved access expires {snapshot.credentialExpiresUtc ? new Date(snapshot.credentialExpiresUtc).toLocaleString() : 'unknown'}. Secure Host identity expires {snapshot.certificateExpiresUtc ? new Date(snapshot.certificateExpiresUtc).toLocaleString() : 'unknown'}.</p>
              <label>New Host address<Input ref={recoveryAddressInputRef} value={recoveryEndpoint} onChange={event => setRecoveryEndpoint(event.target.value.trim())} placeholder={`https://100.64.0.2:${appInstance?.companionPort ?? 5131}`} /><small>The saved Host identity and this PC's access must both work at the new address. TogetherServer will not trust a different Host automatically.</small></label>
              <Button className="secondary" disabled={!!pending || !recoveryEndpoint} onClick={() => void recoverFriendEndpoint()}>{pending === 'recover-endpoint' ? 'Checking...' : 'Check and update address'}</Button>
              <Button className="text-button danger" disabled={!!pending} onClick={() => void forgetFriendConnection()}>{pending === 'forget-connection' ? 'Forgetting...' : 'Forget this Host'}</Button>
            </details>
          </> : <>
            <form className="join-row" onSubmit={event => { event.preventDefault(); void pairFriend() }}>
              <label className="invite-input">Server code<Input autoFocus type="password" autoComplete="off" value={friendInvite} onChange={event => { setFriendInvite(event.target.value.trim()); setPairIssue(null) }} placeholder="Paste the invite here" /></label>
              <Button type="submit" disabled={!!pending || !friendInvite}>{pending === 'pair' ? 'Connecting…' : 'Connect'}</Button>
            </form>
            {pairIssue && <div className="connection-warning" role="alert"><strong>{pairIssue.message}</strong><FriendConnectionHelp code={pairIssue.code} /></div>}
            <details className="advanced-block"><summary>Using an older invite?</summary><label>Host IP<Input value={friendHostAddress} onChange={event => setFriendHostAddress(event.target.value.trim())} placeholder="123.45.67.89" /><small>Older TS1 invites need the Host IP. New invites already include it.</small></label></details>
          </>}
          {snapshot.endpoint && !showPairing && <FriendConnectionDoctor snapshot={snapshot}
            gameResults={gameEndpointResults} busy={!!pending}
            onRefresh={() => void checkFriendConnection()} onProbe={profileId => void probeGameEndpoint(profileId)} />}
          {appInstance?.isStaging && snapshot.endpoint && !showPairing && <FriendRemoteRehearsal key={snapshot.connectionId} snapshot={snapshot} />}
          {snapshot.endpoint && !showPairing && snapshot.profiles.length === 0 && (snapshot.state === 'Connected' || snapshot.state === 'Disabled') && <div className="empty compact-empty"><p>The Host has not assigned any servers to this PC. Ask the Host to open Friend access and choose the servers you can control.</p></div>}
          {snapshot.endpoint && !showPairing && snapshot.profiles.length > 0 && <div className="friend-server-list"><h3>{snapshot.profiles.length === 1 ? 'Server' : 'Servers'}</h3>
          {snapshot.profiles.map(profile => {
            const connectionKey = `friend-${snapshot.connectionId}-${profile.id}`
            const logAvailability = friendLogAvailability(profile.canViewLogs, snapshot.hostCapabilities)
            const addressKey = `${connectionKey}-address`
            const addressActivity = connectionActivity[addressKey] ?? null
            const operationBusy = profile.operation?.state === 'Pending' || profile.operation?.state === 'Running'
            const operationConflict = profile.operation?.code === 'PortConflict' && profile.operation.portConflicts?.length
              ? { message: profile.operation.message, conflicts: profile.operation.portConflicts } : null
            return <article className="profile-card" key={connectionKey} aria-busy={pending === 'poll' || pending.endsWith(profile.id)}>
              <div className="profile-top"><div><h3>{profile.name}</h3><p>{gameLabel(profile.kind)}</p><ServerActivity state={profile.state} online={profile.onlinePlayers} capacity={profile.maxPlayers} deadline={profile.autoShutdownAtUtc} timerReason={profile.autoShutdownReason} nowMs={nowMs}
                refreshing={pending === `friend-refresh-${profile.id}`} refreshDisabled={!!pending || !['Connected', 'Disabled'].includes(snapshot.state)}
                onRefresh={() => void friendAction(profile.id, 'refresh')} /></div><span className={`status ${statusTone(profile.state)}`}>{pending === 'poll' && <Icon name="loader" />}{profile.state === 'Ready' ? 'Ready to join' : profile.state}</span></div>
              {profile.operation && <div className={`notice ${profile.operation.state === 'Failed' || profile.operation.state === 'Interrupted' ? 'bad' : 'good'}`} role="status"><strong>{profile.operation.action[0].toUpperCase() + profile.operation.action.slice(1)}: {profile.operation.state}</strong><p>{profile.operation.message}</p></div>}
              {profile.maintenanceEnabled && <div className="notice bad" role="status"><strong>Maintenance mode</strong><p>{profile.maintenanceMessage || 'The Host has paused remote actions for this server.'}</p></div>}
              {['Ready', 'Listening'].includes(profile.state) && profile.joinAddress && <ConnectionDetails
                fields={[{ id: addressKey, label: 'Server IP', value: profile.joinAddress,
                  revealed: !!revealedConnections[addressKey], copying: addressActivity === 'copy', revealing: false,
                  onReveal: () => revealConnectionDetails(addressKey), onHide: () => hideConnectionDetails(addressKey),
                  onCopy: () => void copyConnectionValue(addressKey, profile.joinAddress!, 'Server IP') }]}
                 refreshing={pending === 'poll'} note={profile.state === 'Listening'
                   ? 'Only the local listener was detected. A real join and saved change still need checking.'
                   : profile.kind === 'Valheim' ? 'The game password is shared separately by your Host.' : undefined}
                 />}
              {['Ready', 'Listening'].includes(profile.state) && profile.joinAddress && <JoinGuide kind={profile.kind} />}
              {['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(profile.gameKind ?? profile.kind) &&
                ['Connected', 'Disabled'].includes(snapshot.state) && <PaneErrorBoundary title="Game requirements" resetKey={`${snapshot.connectionId}-${profile.id}`}>
                  <GameCompatibilityPanel key={`${snapshot.connectionId}-${profile.id}-${profile.gameKind ?? profile.kind}`} profileId={profile.id} host={false} />
                </PaneErrorBoundary>}
              {['Ready', 'Listening'].includes(profile.state) && profile.joinAddress && <OpenGameButton
                key={`${snapshot.connectionId}-${profile.id}-${profile.gameKind ?? profile.kind}`} profileId={profile.id} kind={profile.gameKind ?? (['MinecraftJava', 'MinecraftBedrock'].includes(profile.kind) ? profile.kind : 'Unknown')}
                available={['Connected', 'Disabled'].includes(snapshot.state) && !pending} />}
              {profile.state === 'Offline' && snapshot.state === 'Connected' && profile.canStart &&
                <FriendStartConnectionNotice />}
              <div className="actions server-actions">
                {profile.state === 'Offline' && snapshot.state === 'Connected' && profile.canStart && <Button disabled={!!pending || operationBusy || profile.maintenanceEnabled} onClick={() => void friendAction(profile.id, 'start')}>{pending === `friend-start-${profile.id}` ? <><Icon name="loader" />Starting…</> : <><Icon name="play" />Start server</>}</Button>}
                {profile.state === 'Ready' && snapshot.state === 'Connected' && profile.canStop && profile.canStopNow && <Button className="secondary" disabled={!!pending || operationBusy || profile.maintenanceEnabled} onClick={() => void friendAction(profile.id, 'stop')}>{pending === `friend-stop-${profile.id}` ? <><Icon name="loader" />Stopping…</> : <><Icon name="stop" />Stop server</>}</Button>}
                {profile.state === 'Ready' && snapshot.state === 'Connected' && profile.canRestartNow && <Button className="secondary" disabled={!!pending || operationBusy || profile.maintenanceEnabled} onClick={() => void friendAction(profile.id, 'restart')}>{pending === `friend-restart-${profile.id}` ? <><Icon name="loader" />Restarting…</> : <><Icon name="refresh" />Restart server</>}</Button>}
                {profile.state === 'Ready' && snapshot.state === 'Connected' && profile.canExtendTimer && <Button className="secondary" disabled={!!pending || operationBusy || profile.maintenanceEnabled || profile.timerExtensionRemainingMinutes < profile.timerExtensionMinutes} onClick={() => void friendAction(profile.id, 'extend')}>{pending === `friend-extend-${profile.id}` ? <><Icon name="loader" />Adding time…</> : <>Add {profile.timerExtensionMinutes} minutes</>}</Button>}
                {['Ready', 'Listening'].includes(profile.state) && ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Terraria'].includes(profile.kind) && <Button className="text-button" disabled={!!pending || !profile.joinAddress} onClick={() => void probeGameEndpoint(profile.id)}>{pending === `probe-game-${profile.id}` ? 'Checking game connection...' : 'Check game connection from this PC'}</Button>}
              </div>
                {logAvailability.visible && <div className="friend-log-surface">
                  <Button className="secondary" aria-expanded={friendLogProfileId === profile.id}
                  onClick={() => setFriendLogProfileId(current => current === profile.id ? '' : profile.id)}>
                  <Icon name="server" />{friendLogProfileId === profile.id ? 'Hide logs' : 'View logs'}
                </Button>
                {friendLogProfileId === profile.id && <PaneErrorBoundary title="Shared server logs" resetKey={profile.id}><ServerLogViewer
                  endpoint={`/api/local/friend/${profile.id}/logs`}
                  visible={workspacePage === 'join' && friendLogProfileId === profile.id}
                  unsupported={logAvailability.unsupported} /></PaneErrorBoundary>}
              </div>}
              {gameEndpointResults[profile.id] && <p className={gameEndpointResults[profile.id].answered ? 'helper-text' : 'warning-text'}>{gameEndpointResults[profile.id].message}</p>}
              {operationConflict && <div className="port-conflict-action" role="alert"><strong>Shared game port</strong><p>{operationConflict.message}</p>
                {operationConflict.conflicts.every(conflict => conflict.canReplace) ? <Button disabled={!!pending || operationBusy} onClick={() => void friendAction(profile.id, 'replace')}>{pending === `friend-replace-${profile.id}` ? <><Icon name="loader" />Switching…</> : <>Stop empty server and start this one</>}</Button>
                  : <small>{operationConflict.conflicts.find(conflict => !conflict.canReplace)?.blockReason ?? 'The other server cannot be stopped safely.'}</small>}</div>}
              <FriendStopBlockers snapshot={snapshot} profile={profile} />
              {profile.kind !== 'Custom' && <FriendSharedWorlds profileId={profile.id}
                available={snapshot.hostCapabilities.includes('shared-worlds-v2')}
                onAddressChange={openFriendAddressRecovery} />}
              <div className="friend-chat-surface"><Button className="secondary"
                aria-expanded={friendChatProfileId === profile.id}
                onClick={() => setFriendChatProfileId(current => current === profile.id ? '' : profile.id)}>
                {friendChatProfileId === profile.id ? 'Hide chat' : 'Open chat'}</Button>
                {friendChatProfileId === profile.id && <PaneErrorBoundary title="Server chat" resetKey={profile.id}>
                  <ServerChat profileId={profile.id} host={false}
                    visible={workspacePage === 'join' && friendChatProfileId === profile.id}
                    supported={snapshot.hostCapabilities.includes('server-chat-v1')} />
                </PaneErrorBoundary>}</div>
              {profile.state === 'Offline' && !profile.canStart && snapshot.state === 'Connected' && <p className="helper-text">The Host has not allowed this PC to start this server.</p>}
              {['Ready', 'Listening'].includes(profile.state) && !profile.joinAddress && <p className="helper-text">The Host has not found a current game address yet.</p>}
            </article>
          })}
          </div>}
          {snapshot.endpoint && !showPairing && snapshot.state === 'Disconnected/Unknown' &&
            !!snapshot.chatProfiles?.some(profile => profile.supported) &&
            <div className="friend-server-list"><h3>Chats saved on this PC</h3>
              <p className="helper-text">The Host connection is unavailable. You can write a message now; it will wait on this PC until the Host reconnects.</p>
              {snapshot.chatProfiles.filter(profile => profile.supported).map(profile =>
                <article className="profile-card" key={profile.id}><div className="profile-top"><h3>{profile.name}</h3>
                  <Button className="secondary" aria-expanded={friendChatProfileId === profile.id}
                    onClick={() => setFriendChatProfileId(current => current === profile.id ? '' : profile.id)}>
                    {friendChatProfileId === profile.id ? 'Hide chat' : 'Open chat'}</Button></div>
                  {friendChatProfileId === profile.id && <PaneErrorBoundary title="Saved server chat" resetKey={profile.id}>
                    <ServerChat profileId={profile.id} host={false}
                      visible={workspacePage === 'join' && friendChatProfileId === profile.id} />
                  </PaneErrorBoundary>}</article>)}</div>}
        </section>
      </>}

      {(workspacePage === 'host' || workspacePage === 'settings') && snapshot?.mode === 'Host' && draft && <>
        {workspacePage === 'host' && <>
        {appInstance?.isStaging && <HostRemoteRehearsal />}
        <PaneErrorBoundary title="All servers" resetKey={snapshot.settings.profiles.length}>
          <AllServerOverview snapshot={snapshot} selectedProfileId={selectedHostProfileId} nowMs={nowMs}
            error={loadError} onOpen={openActivityDestination} />
        </PaneErrorBoundary>
        {savedProfiles.length > 0 && <>
        <section className="host-workspace-shell">
          <div className={hostMobileDetail ? 'host-master-detail detail-open' : 'host-master-detail'}>
            <aside className="server-master" aria-label="Saved servers"><div className="server-master-heading"><strong>Saved servers</strong><span>{savedProfiles.length}</span></div>
              <div className="server-master-list">{snapshot.settings.profiles.map(profile => {
                const run = snapshot.runs.find(item => item.profileId === profile.id)
                return <Button key={profile.id} className={selectedHostProfile?.id === profile.id ? 'server-master-item selected' : 'server-master-item'}
                  aria-current={selectedHostProfile?.id === profile.id ? 'true' : undefined} onClick={() => { setSelectedHostProfileId(profile.id); setHostServerTab('overview'); setHostMobileDetail(true) }}>
                  <span><strong>{profile.name}</strong><small>{profileGameLabel(profile)}</small></span><span className={`status ${statusTone(run?.state ?? 'Unknown')}`}>{run?.state === 'Process running' ? 'Starting' : run?.state ?? 'Unknown'}</span>
                </Button>
              })}</div>
              <Button className="server-master-add secondary" disabled={!!pending || dirty} onClick={addProfile}><Icon name="server" />Add server</Button>
            </aside>
            <section className="server-detail" data-server-tab={hostServerTab} aria-label={selectedHostProfile ? `${selectedHostProfile.name} workspace` : 'Server workspace'}>
              <Button className="mobile-back secondary" onClick={() => setHostMobileDetail(false)}>Back to all servers</Button>
              <nav className="server-tabs" aria-label="Selected server sections">
                {(['overview', 'chat', 'players', 'logs', 'sessions', 'backups', 'files', 'setup'] as HostServerTab[]).map(tab => <Button key={tab} className={hostServerTab === tab ? 'selected' : ''} aria-current={hostServerTab === tab ? 'page' : undefined} onClick={() => setHostServerTab(tab)}>{tab[0].toUpperCase() + tab.slice(1)}</Button>)}
              </nav>
              <div className="server-detail-pane">
            {snapshot.settings.profiles.filter(profile => profile.id === selectedHostProfile?.id).map(profile => {
              const status = snapshot.runs.find(run => run.profileId === profile.id)
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
              <div className="profile-top"><div><h3>{profile.name}</h3><p>{profileGameLabel(profile)} · World {profile.worldId}</p><ServerActivity state={status?.state ?? 'Unknown'} online={status?.onlinePlayers ?? null} capacity={status?.maxPlayers ?? null} deadline={status?.autoShutdownAtUtc ?? null} timerReason={status?.autoShutdownReason ?? null} nowMs={nowMs} players={status?.playerNames}
                refreshing={pending === `players-${profile.id}`} refreshDisabled={!!pending || dirty}
                onRefresh={() => void run(`players-${profile.id}`, `/api/local/profiles/${profile.id}/players/refresh`, 'POST')} /></div>
                  <span className={`status ${statusTone(status?.state ?? 'Unknown')}`}>{(pending === `start-${profile.id}` || pending === `stop-${profile.id}` || pending === `restart-${profile.id}`) && <Icon name="loader" />}{status?.state === 'Process running' ? 'Starting' : status?.state ?? 'Unknown'}</span></div>
                {status?.state === 'Offline' && (hostServerTab === 'overview' || hostServerTab === 'files') &&
                  <HostStartConnectionNotice profile={profile} ports={portDiagnostics} routeCheck={internetRouteCheck}
                    onOpenConnection={() => openHostSettings('network')}
                    onTestControl={() => void checkInternetRoute()} testingControl={checkingInternetRoute} />}
                {hostServerTab === 'logs' && <PaneErrorBoundary title="Server logs" resetKey={profile.id}><ServerLogViewer endpoint={`/api/local/profiles/${profile.id}/logs`}
                  visible={workspacePage === 'host' && hostServerTab === 'logs'} /></PaneErrorBoundary>}
                {hostServerTab === 'chat' && <PaneErrorBoundary title="Server chat" resetKey={profile.id}>
                  <ServerChat profileId={profile.id} host visible={workspacePage === 'host' && hostServerTab === 'chat'} />
                </PaneErrorBoundary>}
                {hostServerTab === 'overview' && ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(profile.kind) &&
                  <PaneErrorBoundary title="Pre-join requirements" resetKey={profile.id}><GameCompatibilityPanel key={profile.id} profileId={profile.id} host /></PaneErrorBoundary>}
                {hostServerTab === 'files' && <PaneErrorBoundary title="Game settings" resetKey={profile.id}><GameSettingsPanel
                  key={profile.id} profileId={profile.id} state={status?.state ?? 'Unknown'} maintenance={!!profile.maintenance?.enabled}
                  busy={!!pending || dirty} recoveryBlocked={!!dataRecovery?.lifecycleBlocked}
                  onPrepareMaintenance={() => setHostServerTab('setup')} /></PaneErrorBoundary>}
                {hostServerTab === 'files' && <PaneErrorBoundary title="Server files" resetKey={profile.id}><ServerFilesPanel
                  profileId={profile.id} state={status?.state ?? 'Unknown'} maintenance={!!profile.maintenance?.enabled}
                  busy={!!pending || dirty} recoveryBlocked={!!dataRecovery?.lifecycleBlocked}
                  onPrepareMaintenance={() => setHostServerTab('setup')}
                  onStart={() => void run(`start-${profile.id}`, `/api/local/profiles/${profile.id}/start`, 'POST')}
                  onOpenDoctor={() => openHostSettings('network')} /></PaneErrorBoundary>}
                <PaneErrorBoundary title="Recent sessions" resetKey={profile.id}><RecentSessions profileId={profile.id}
                  visible={workspacePage === 'host' && hostServerTab === 'sessions'} /></PaneErrorBoundary>
                {hostServerTab === 'sessions' && <PaneErrorBoundary title="Seven-day summary" resetKey={profile.id}><WeeklySummary
                  key={profile.id} profileId={profile.id} visible={workspacePage === 'host'} /></PaneErrorBoundary>}
                {hostServerTab === 'backups' && <PaneErrorBoundary title="Backup names and pins" resetKey={profile.id}><BackupBookmarks
                  key={profile.id} profileId={profile.id} visible={workspacePage === 'host'} onChanged={() => void loadBackups(profile.id)} /></PaneErrorBoundary>}
                <div hidden={hostServerTab !== 'overview'}><PaneErrorBoundary title="Connection readiness" resetKey={profile.id}><ServerReadiness profileId={profile.id} status={status?.state ?? 'Unknown'} ports={portDiagnostics} routeCheck={internetRouteCheck}
                  busy={checkingPorts || !!pending} refreshing={checkingPorts} onRefresh={() => void checkPorts(true)} onOpenConnection={() => openHostSettings('network')} /></PaneErrorBoundary></div>
                {hostServerTab === 'players' && status && <PaneErrorBoundary title="Players workspace" resetKey={profile.id}><PlayersPanel run={status} activity={snapshot.activity} nowMs={nowMs}
                  refreshing={pending === `players-${profile.id}`} disabled={!!pending || dirty}
                  onRefresh={() => void run(`players-${profile.id}`, `/api/local/profiles/${profile.id}/players/refresh`, 'POST')} /></PaneErrorBoundary>}
                {profile.maintenance?.enabled && <div hidden={hostServerTab !== 'players'} className="notice bad" role="status"><strong>Maintenance mode is on</strong><p>{profile.maintenance.message || 'Friends can see status, but remote Start, Stop, and Restart are paused.'}</p><Button className="secondary" disabled={!!pending || dirty} onClick={() => void saveMaintenance(profile, false)}>End maintenance</Button></div>}
                <div hidden={hostServerTab !== 'setup'}><MaintenanceGuide enabled={!!profile.maintenance?.enabled}
                  message={maintenanceMessages[profile.id] ?? profile.maintenance?.message ?? ''}
                  state={status?.state ?? 'Unknown'} onlinePlayers={status?.onlinePlayers ?? null}
                  countTrusted={!!status?.playerCountTrusted} lastBackupUtc={shownBackupStatus?.lastSuccessfulUtc ?? null}
                  busy={!!pending || dirty} onMessage={message => setMaintenanceMessages(current => ({ ...current, [profile.id]: message }))}
                  onToggle={enabled => void saveMaintenance(profile, enabled)}
                  onStop={() => void run(`stop-${profile.id}`, `/api/local/profiles/${profile.id}/stop`, 'POST')}
                  onBackup={() => void createManualBackup(profile.id)}
                  onStart={() => void run(`start-${profile.id}`, `/api/local/profiles/${profile.id}/start`, 'POST')}
                  onOpenDoctor={() => openHostSettings('network')} /></div>
                {hostServerTab === 'overview' && ['Ready', 'Listening'].includes(status?.state ?? '') && gameAddress && <ConnectionDetails fields={connectionFields}
                  refreshing={checkingPorts || detectingPublicIp}
                  note={status?.state === 'Listening' ? 'Only the local listener was detected. A real join and saved change still need checking.' : undefined} />}
                {status?.state === 'Ready' && snapshot.settings.autoShutdownEnabled && status.playerCountTrusted && status.onlinePlayers !== null && <div className="timer-extension"><label>Add shutdown time<Input type="number" min="1" step="1" value={countdownExtensions[profile.id] ?? '15'} disabled={!!pending || dirty} onChange={event => setCountdownExtensions(current => ({ ...current, [profile.id]: event.target.value }))} /><small>Saved if players join and applied when the server next reaches 0 players.</small></label><Button className="secondary" disabled={!!pending || dirty} onClick={() => void extendCountdown(profile.id)}>{pending === `extend-${profile.id}` ? 'Adding…' : 'Add time'}</Button></div>}
                <div hidden={hostServerTab !== 'overview'} className="actions server-actions">
                  {status?.state === 'Offline' && <Button disabled={!!pending || dirty || dataRecovery?.lifecycleBlocked} title={dataRecovery?.lifecycleBlocked ? 'Resolve the local data recovery warning first.' : undefined} onClick={() => void run(`start-${profile.id}`, `/api/local/profiles/${profile.id}/start`, 'POST')}>{pending === `start-${profile.id}` ? <><Icon name="loader" /><span>Starting…</span></> : <><Icon name="play" /><span>Start server</span></>}</Button>}
                  {['Process running', 'Starting', 'Listening', 'Ready'].includes(status?.state ?? '') && <Button disabled={!!pending || dirty} onClick={() => { hideConnectionDetails(addressKey); hideConnectionDetails(passwordKey); void run(`stop-${profile.id}`, `/api/local/profiles/${profile.id}/stop`, 'POST') }}>{pending === `stop-${profile.id}` ? <><Icon name="loader" /><span>Stopping…</span></> : <><Icon name="stop" /><span>Stop server</span></>}</Button>}
                  {status?.state === 'Ready' && <Button className="secondary" disabled={!!pending || dirty || dataRecovery?.lifecycleBlocked} title={dataRecovery?.lifecycleBlocked ? 'Resolve the local data recovery warning first.' : undefined} onClick={() => { hideConnectionDetails(addressKey); hideConnectionDetails(passwordKey); void run(`restart-${profile.id}`, `/api/local/profiles/${profile.id}/restart`, 'POST') }}>{pending === `restart-${profile.id}` ? <><Icon name="loader" /><span>Restarting…</span></> : <><Icon name="refresh" /><span>Restart server</span></>}</Button>}
                  <Button className="secondary server-invite-button" disabled={!!pending || dirty || !friendAppAddress} onClick={() => void inviteFriend(profile.id)}><Icon name="invite" /><span>Invite friends</span></Button>
                </div>
                {hostServerTab === 'overview' && inviteProfileId === profile.id && <div className="inline-invite">
                  {invitation ? <><div className="invite-ready"><span><Icon name={activeInviteWarning ? 'warning' : 'check'} /></span><div><strong>{activeInviteWarning ? 'Server code ready, connection needs attention' : 'Server code ready'}</strong><p>{activeInviteWarning ? 'Fix the connection below before sending the code.' : 'Send it privately. The same code keeps working until you replace it.'}</p></div></div>
                    {activeInviteWarning && <p className="connection-warning" role="alert">{activeInviteWarning}</p>}
                    {!activeInviteWarning && currentRouteResult?.state === 'Not reachable' && <p className="connection-warning" role="alert">The internet test could not reach this PC at {new Date(currentRouteResult.checkedUtc).toLocaleTimeString()}. Open Friend access to fix the connection before sharing.</p>}
                    <div className="actions">{activeInviteWarning
                      ? <Button disabled={!!pending} onClick={() => void inviteFriend(profile.id)}><Icon name="refresh" />Try connection again</Button>
                      : <Button onClick={() => void copyText(invitation, 'Server code')}><Icon name="copy" />Copy code</Button>}
                      <Button className="text-button" onClick={() => { setInviteProfileId(''); setInvitation(''); setInviteListenerWarning(null) }}>Done</Button></div>
                    <details className="advanced-block"><summary>New PCs and replacing the code</summary><label className="check-row"><Input type="checkbox" checked={pairingRequireApproval} onChange={event => setPairingRequireApproval(event.target.checked)} />Ask me to approve each new PC before it can connect</label><div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => void issueInvite(profile.id)}>Save approval setting</Button><Button className="danger-outline" disabled={!!pending} onClick={() => void issueInvite(profile.id, true)}>Replace code and remove old access</Button></div><small>Replacing the code disconnects PCs that joined with the old code. To remove only one PC, use Friend access.</small></details></>
                    : inviteListenerWarning ? <><p className="connection-warning" role="alert">{inviteListenerWarning}</p>
                      <div className="actions"><Button disabled={!!pending} onClick={() => void inviteFriend(profile.id)}><Icon name="refresh" />Try again</Button>
                        <Button className="text-button" onClick={() => { setInviteProfileId(''); setInviteListenerWarning(null) }}>Done</Button></div></>
                      : pending === 'invite' ? <p className="helper-text" role="status">Getting server code…</p>
                        : <><p className="connection-warning" role="alert">The server code was not loaded.</p><div className="actions"><Button onClick={() => void inviteFriend(profile.id)}>Try again</Button><Button className="text-button" onClick={() => setInviteProfileId('')}>Done</Button></div></>}
                </div>}
                {status?.state === 'Ready' && !detectedGameIp && <div className="next-action"><span>Your public game address is not available yet.</span><Button className="text-button" onClick={() => openHostSettings('network')}>Check connection</Button></div>}
                {profile.kind === 'Custom' && <div className="custom-certification">
                  <div><strong>Owner-certified Custom control</strong><span className={`pill ${certification?.certified ? 'certified' : ''}`}>{certification?.certified ? 'Remote-ready' : certification?.inProgress ? 'Certification in progress' : 'Not certified'}</span></div>
                  <p>{certification?.message ?? 'Live certification is required for remote Stop, Restart, replacement, and automatic shutdown.'}</p>
                  {certification?.onlinePlayers != null && <small>Latest certification observation: {certification.onlinePlayers} player{certification.onlinePlayers === 1 ? '' : 's'} online.</small>}
                  <div className="actions">
                    {!certification?.certified && !certification?.inProgress && certification?.stage !== 'Failed' && <Button className="secondary" disabled={!!pending || dirty || status?.state !== 'Offline'} onClick={() => void customCertificationAction(profile.id, 'begin')}>Begin live certification</Button>}
                    {certification?.inProgress && <Button className="secondary" disabled={!!pending || dirty} onClick={() => void customCertificationAction(profile.id, 'status')}>{pending === `certification-status-${profile.id}` ? 'Checking…' : 'Check certification step'}</Button>}
                    {certification?.inProgress && ['ConfirmFirstChange', 'ConfirmSecondChange'].includes(certification.stage) && <Button disabled={!!pending || dirty} onClick={() => void customCertificationAction(profile.id, 'confirm')}>{certification.stage === 'ConfirmFirstChange' ? 'Confirm change was made' : 'Confirm change survived'}</Button>}
                    {(certification?.inProgress || certification?.stage === 'Failed') && <Button className="text-button" disabled={!!pending} onClick={() => void customCertificationAction(profile.id, 'cancel')}>Cancel certification</Button>}
                    {certification?.certified && <Button className="danger-outline" disabled={!!pending} onClick={() => { if (window.confirm('Turn off remote Stop, Restart, replacement, and automatic shutdown for this Custom server? Local controls will remain available.')) void customCertificationAction(profile.id, 'revoke') }}>Revoke certification</Button>}
                  </div>
                  {!certification?.certified && status?.state !== 'Offline' && !certification?.inProgress && certification?.stage !== 'Failed' && <small>Stop the server locally before beginning certification.</small>}
                </div>}
                {recovery && <div className={`safety-status ${recovery.state === 'Suspended' ? 'warning-text' : ''}`}><strong>Crash recovery: {recovery.state}</strong><p>{recovery.state === 'Pending' && recovery.nextAttemptUtc ? `Attempt ${recovery.attempts + 1} of 3 after ${new Date(recovery.nextAttemptUtc).toLocaleString()}.` : recovery.state === 'Starting' ? `Recovery attempt ${recovery.attempts} of 3 is starting.` : recovery.state === 'Recovered' ? `Ready again after ${recovery.attempts} attempt${recovery.attempts === 1 ? '' : 's'}.` : `Suspended after ${recovery.attempts} failed attempts.`}</p>{recovery.lastFailure && <small>{recovery.lastFailure}</small>}</div>}
                <details open={hostServerTab === 'backups'} className="advanced-block card-manage"><summary>{hostServerTab === 'backups' ? 'World protection' : 'More server actions'}</summary>
                  <p className="helper-text">Playing on this PC? Join <code>127.0.0.1:{profile.gamePort}</code>.</p>
                  <div className="actions"><Button className="secondary" disabled={!!pending || status?.state !== 'Offline'} onClick={() => openSetup(profile.id)}>Edit setup</Button><Button className="secondary" onClick={() => openHostSettings('network')}>Connection help</Button><Button className="secondary" disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/health`, 'POST')}>Check server health</Button>
                    {status?.state === 'Failed' && <Button className="text-button" disabled={!!pending || dirty} onClick={() => {
                      if (window.confirm('Archive this session only if TogetherServer confirms that its saved server process is no longer running?'))
                        void run(profile.id, `/api/local/profiles/${profile.id}/forget`, 'POST')
                    }}>Archive exited record</Button>}</div>
                  {status?.state === 'Unknown' && <p className="warning-text">Process identity is uncertain. Start, Stop, archive, backup restore, and world reuse remain blocked; TogetherServer will not clear this record on PID reuse, executable mismatch, or access failure.</p>}
                  {['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(profile.kind) && <PaneErrorBoundary title="World Safety Center" resetKey={profile.id}><div className="world-protection-summary"><div className="world-safety-heading"><div><strong>World Safety Center</strong><p>{profile.kind === 'Factorio' || profile.kind === 'Terraria' ? 'Automatic crash recovery is unavailable in this preview' : `Crash recovery is ${profile.crashRecovery?.enabled ? 'on' : 'off'}`} · rolling backup after graceful Stop is {profile.backups?.enabled ? 'on' : 'off'}.</p></div><span className={`pill ${status?.state === 'Offline' ? 'certified' : ''}`}>{status?.state === 'Offline' ? 'Safe for offline backup' : 'Server must be offline'}</span></div>
                    {shownBackupStatus?.lastSuccessfulUtc && <small>Last successful backup {new Date(shownBackupStatus.lastSuccessfulUtc).toLocaleString()} · {shownBackupStatus.completedCount} retained · {formatBytes(shownBackupStatus.retainedSizeBytes)} used.</small>}
                    {shownBackupStatus?.availableSpaceBytes != null && <small>{formatBytes(shownBackupStatus.availableSpaceBytes)} available on the backup drive.</small>}
                    {shownBackupStatus?.lastFailureUtc && <p className="warning-text">Last backup issue {new Date(shownBackupStatus.lastFailureUtc).toLocaleString()}: {shownBackupStatus.lastFailure}</p>}
                    <div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => void loadBackups(profile.id)}>{pending === `backups-${profile.id}` ? 'Loading backups…' : backupList ? 'Refresh backups' : 'Show backups'}</Button><Button className="secondary" disabled={!!pending || status?.state !== 'Offline'} title={status?.state !== 'Offline' ? 'Stop the server before copying its world.' : undefined} onClick={() => void createManualBackup(profile.id)}>{pending === `manual-backup-${profile.id}` ? 'Backing up…' : 'Back up now'}</Button>{status?.state === 'Ready' && <Button className="secondary" disabled={!!pending || dirty || dataRecovery?.lifecycleBlocked} onClick={() => void safeRestart(profile.id)}>{pending === `safe-restart-${profile.id}` ? 'Safely restarting…' : 'Safe restart'}</Button>}<Button className="text-button" disabled={!!pending || status?.state !== 'Offline'} onClick={() => openSetup(profile.id)}>Change protection settings</Button></div>
                    <small>Safe restart stops gracefully, makes an offline checkpoint, and starts only after that checkpoint succeeds. “Copy to vault” uses a Windows folder picker, verifies every hash after transfer, and keeps the local backup. Choose an external or network location when you want another-device protection. “Test restore” uses disposable scratch storage and never swaps the live world.</small>
                    <HostSharedSaves profileId={profile.id} devices={companion?.devices ?? []}
                      rollingBackupEnabled={profile.backups?.enabled === true}
                      currentAddress={snapshot.mode === 'Host' && draft?.companionEndpoint === snapshot.settings.companionEndpoint
                        ? snapshot.settings.companionEndpoint : ''}
                      onGrantChanged={refreshCompanion} />
                    {!profile.worldLoadRehearsalId && <WorldLoadRehearsalPanel profileId={profile.id} backups={backupList?.backups ?? []} />}
                    {backupList && <div className="backup-list">{backupList.backups.length === 0 ? <p className="helper-text">No completed backups yet. Stop the server and choose Back up now, or enable rolling backups after graceful Stop.</p> : backupList.backups.map(backup => {
                      const verification = backupVerifications[backup.id]
                      const category = backup.backupKind === 'PreRestore' ? 'Pre-restore snapshot' : backup.backupKind === 'Manual' ? 'Manual checkpoint' : 'Rolling backup'
                      const label = `${backup.label || category}${backup.pinned ? ' · Pinned' : ''}`
                      return <div className="device backup-record" key={backup.id}><div><strong>{label}</strong><small>{new Date(backup.createdUtc).toLocaleString()} · {backup.fileCount} files · {formatBytes(backup.sizeBytes)}</small>{verification && <small className={verification.ok ? 'verification-ok' : 'warning-text'}>{verification.ok ? 'Integrity verified' : 'Integrity check failed'} {new Date(verification.checkedUtc).toLocaleString()}. The live world was not changed.</small>}</div><div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => void verifyBackup(profile.id, backup.id)}>{pending === `verify-backup-${backup.id}` ? 'Verifying…' : 'Verify'}</Button><Button className="secondary" disabled={!!pending} onClick={() => void copyBackupToVault(profile.id, backup.id)}>{pending === `vault-backup-${backup.id}` ? 'Copying…' : 'Copy to vault'}</Button><Button className="secondary" disabled={!!pending || status?.state !== 'Offline'} onClick={() => void prepareMoveKit(profile.id, backup.id)}>{pending === `move-kit-${backup.id}` ? 'Preparing…' : 'Prepare move kit'}</Button><Button className="secondary" disabled={!!pending} onClick={() => void rehearseBackupRestore(profile.id, backup.id)}>{pending === `rehearse-backup-${backup.id}` ? 'Testing…' : 'Test restore'}</Button><Button className="secondary" disabled={!!pending || status?.state !== 'Offline'} onClick={() => void restoreBackup(profile.id, backup.id, backup.createdUtc)}>Restore</Button></div></div>
                    })}</div>}
                  </div></PaneErrorBoundary>}
                  {['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(profile.kind) && <details className="advanced-block"><summary>Update the game server safely</summary><ol className="update-game-steps"><li>Stop this server, then choose Back up now while it is Offline.</li><li>Verify the backup. Copy it to another location or test restore if needed.</li><li>Use the game provider's installer or update action yourself.</li><li>Start the updated server. Test a real Friend join and a saved change, then record those checks again in Connection Doctor. Changed game files make earlier confirmations stale.</li></ol><Button className="text-button" onClick={() => openHostSettings('network')}>Open Connection Doctor</Button></details>}
                </details>
              </article>
            })}
              </div>
          {dirty && <p className="warning-text">Save your setup changes before starting or stopping a server.</p>}
          {companion?.devices.some(device => device.paired && !device.revoked) && <div className="access-strip"><span>Friend controls are <strong>{draft.remoteControlsEnabled ? 'on' : 'paused'}</strong> · {companion.devices.filter(device => device.paired && !device.revoked).length} connected PC{companion.devices.filter(device => device.paired && !device.revoked).length === 1 ? '' : 's'}</span>
            <Button className="secondary" disabled={!!pending || dirty} onClick={() => openHostSettings('access')}>Manage friend access</Button></div>}
            </section>
          </div>
        </section>
        </>}

        <section className="panel host-move-guide"><details><summary>Move hosting to another PC</summary>
          <p>On the old Host, stop the server, make a new backup, then choose Prepare move kit beside that backup. Take the verified .backup folder to the new PC.</p>
          <Button className="secondary" disabled={!!pending} onClick={() => void inspectMoveKit()}>{pending === 'inspect-move-kit' ? 'Checking…' : 'Inspect move kit on this PC'}</Button>
          {moveKit?.kit && <div className="device"><strong>{moveKit.kit.name} · {gameLabel(moveKit.kit.kind)}</strong><small>World {moveKit.kit.worldId} · port {moveKit.kit.gamePort} · {moveKit.fileCount} verified files</small>
            <ol><li>Install the same game server version and create a fresh server on this PC.</li><li>While it is offline, copy the kit’s payload into that server’s save location. Keep any existing save separate; do not overwrite it. Use the world name shown above.</li><li>Set up TogetherServer for that server and select its locally installed executable. Recreate game settings and passwords, then test a real join and saved change.</li><li>Give Friends a new server code and pair each PC again. Retire the old Host only after the new one passes your checks.</li></ol>
            <small>The move kit contains world files and basic setup details. It does not contain game binaries, saved Friend access, private keys, or game passwords.</small>
            <Button className="secondary" onClick={addProfile}>Set up new Host</Button></div>}
        </details></section>

        {savedProfiles.length === 0 && !showSetup && <section className="panel welcome-panel"><div className="section-heading"><div><h2>What would you like to do?</h2><p>You can host and join at the same time. Switching pages never stops a running server.</p></div></div>
          {draft.profiles.length > 0 && dirty ? <div className="welcome-choice"><div><strong>Continue server setup</strong><p>Your unfinished non-secret setup details are still here. Re-enter the game password before saving.</p></div><Button onClick={continueSetup}>Continue setup</Button></div> : <div className="welcome-grid">
            <Button className="welcome-choice" disabled={!!pending} onClick={addProfile}><span className="section-icon"><Icon name="server" /></span><span><strong>Host a server</strong><small>{appInstance?.freshWorldsOnly ? 'Create and keep a world in separate development storage.' : 'Create a new world or use a server already on this PC.'}</small></span></Button>
            <Button className="welcome-choice secondary-choice" disabled={!!pending} onClick={() => void switchMode('friend')}><span className="section-icon"><Icon name="link" /></span><span><strong>Join a server</strong><small>Paste the private code your friend sent you.</small></span></Button>
          </div>}
        </section>}

        {showSetup && <HostSetupDialog dialogRef={setupRef} snapshot={snapshot} draft={draft}
          savedProfiles={savedProfiles} editedProfile={editedProfile} notice={notice} pending={pending} dirty={dirty}
          setupStep={setupStep} setupIssues={setupIssues} stepIssues={stepIssues} discovery={discovery}
          minecraftDiscovery={minecraftDiscovery} sourceRoots={sourceRoots} passwords={passwords}
          showPasswords={showPasswords} minecraftSetupMode={minecraftSetupMode} minecraftTerms={minecraftTerms}
          customScripts={customScripts} customScriptsSaved={customScriptsSaved}
          customScriptsLoading={customScriptsLoading} customScriptsChanged={customScriptsChanged}
          dataRecoveryBlocked={!!dataRecovery?.lifecycleBlocked} freshWorldsOnly={appInstance?.freshWorldsOnly ?? false} onCancel={cancelSetup}
          onFinishLater={finishSetupLater} onAddProfile={addProfile} onStepChange={setSetupStep}
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
        {workspacePage === 'settings' && <section className="settings-workspace host-settings-dialog" aria-labelledby="host-settings-title">
          <div className="modal-heading"><div><h2 id="host-settings-title">Settings</h2><p>App preferences, Friend access, timers, and advanced Host controls.</p></div>
            <Button className="secondary" disabled={!!pending} onClick={closeHostSettings}>{dirty ? 'Cancel changes' : 'Back to Host'}</Button></div>
          {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
          <nav className="settings-tabs" aria-label="Host settings sections">
            <Button aria-current={hostSettingsSection === 'app' ? 'page' : undefined} className={hostSettingsSection === 'app' ? 'selected' : ''} onClick={() => setHostSettingsSection('app')}>App</Button>
            <Button aria-current={hostSettingsSection === 'access' ? 'page' : undefined} className={hostSettingsSection === 'access' ? 'selected' : ''} onClick={() => setHostSettingsSection('access')}>Friend access</Button>
            <Button aria-current={hostSettingsSection === 'stop' ? 'page' : undefined} className={hostSettingsSection === 'stop' ? 'selected' : ''} onClick={() => setHostSettingsSection('stop')}>Stop & timer</Button>
            <Button aria-current={hostSettingsSection === 'network' ? 'page' : undefined} className={hostSettingsSection === 'network' ? 'selected' : ''} onClick={() => setHostSettingsSection('network')}>Connection help</Button>
            <Button aria-current={hostSettingsSection === 'diagnostics' ? 'page' : undefined} className={hostSettingsSection === 'diagnostics' ? 'selected' : ''} onClick={() => setHostSettingsSection('diagnostics')}>Diagnostics</Button>
            <Button aria-current={hostSettingsSection === 'advanced' ? 'page' : undefined} className={hostSettingsSection === 'advanced' ? 'selected' : ''} onClick={() => setHostSettingsSection('advanced')}>Advanced</Button>
          </nav>
            <div className="settings-content">
              {hostSettingsSection === 'app' && <section className="settings-section app-settings-page"><h3>Application</h3>
                <p>These preferences affect this Windows app. Hosting and Friend checks continue when the window is hidden.</p>
                <div className="app-settings-grid">
                  <label className="setting-toggle"><span><strong>Open at Windows sign-in</strong><small>{appInstance?.isStaging ? 'Disabled in staging so the stable app keeps its sign-in setting.' : 'Starts quietly in the tray.'}</small></span><Input type="checkbox" checked={desktopPreferences?.launchAtLogin ?? false} disabled={!desktopPreferences?.available || !desktopPreferences.startupAvailable || desktopBusy} onChange={event => void saveDesktopPreference({ launchAtLogin: event.target.checked })} /></label>
                  <label className="setting-toggle"><span><strong>Close to tray</strong><small>Hosting and Friend checks keep running.</small></span><Input type="checkbox" checked={desktopPreferences?.closeToTray ?? false} disabled={!desktopPreferences?.available || desktopBusy} onChange={event => void saveDesktopPreference({ closeToTray: event.target.checked })} /></label>
                  <label className="setting-toggle"><span><strong>Keep Windows awake while hosting</strong><small>Uses a scoped idle-sleep request only while an exact managed server process is running. Manual sleep can still interrupt hosting.</small>{snapshot.hostingPower && <small>Current state: {snapshot.hostingPower.state}. {snapshot.hostingPower.message}</small>}</span><Input type="checkbox" checked={draft.keepAwakeWhileHosting} disabled={!!pending} onChange={event => void saveHostFlags({ keepAwakeWhileHosting: event.target.checked })} /></label>
                </div>
                <div className="settings-version-row"><span><strong>Version {update?.currentVersion ?? 'checking...'}</strong><small>{appInstance?.updatesAvailable === false ? 'Stable updates are disabled in staging.' : `Updates are checked automatically, installed only when you choose, and require a verified local-state recovery checkpoint · ${update?.publisherTrust ?? 'checking trust'}.`}</small></span><Button className="secondary" disabled={updateBusy || !!pending || appInstance?.updatesAvailable === false} onClick={() => void checkUpdate()}>{appInstance?.updatesAvailable === false ? 'Updates off in staging' : updateBusy ? 'Checking...' : 'Check for updates'}</Button></div>
                <div className="settings-danger-row"><span><strong>Quit TogetherServer</strong><small>Active or unresolved managed servers still block Quit.</small></span><Button className="secondary" disabled={!desktopPreferences?.available} onClick={() => void quitApp()}>Quit {appInstance?.displayName ?? 'TogetherServer'}</Button></div>
                <div className="shortcut-reference"><strong>Keyboard shortcuts</strong><span><kbd>Ctrl K</kbd> Command palette</span><span><kbd>Alt 1</kbd> Host</span><span><kbd>Alt 2</kbd> Join</span><span><kbd>Alt 3</kbd> Attention</span><span><kbd>Alt 4</kbd> Settings</span><span><kbd>Esc</kbd> Close or go back</span></div>
              </section>}
              {hostSettingsSection === 'access' && <section className="settings-section"><h3>Friend access</h3>
                <p>Friend PCs can keep seeing status while controls are paused. Start and Stop requests are always checked again on this Host.</p>
                <div className="access-toggles"><label className="setting-toggle"><span><strong>Allow Friend app connections</strong><small>Once a server code exists, Friend connections stay available while the Host app is running.</small></span><Input type="checkbox" checked={draft.companionListeningEnabled} disabled={!!pending} onChange={event => void saveHostFlags({ companionListeningEnabled: event.target.checked })} /></label>
                  <label className="setting-toggle"><span><strong>Allow remote Start and Stop</strong><small>Individual PC permissions below still apply.</small></span><Input type="checkbox" checked={draft.remoteControlsEnabled} disabled={!!pending || !draft.companionListeningEnabled} onChange={event => void saveHostFlags({ remoteControlsEnabled: event.target.checked })} /></label></div>
                {companion?.devices.filter(device => !device.revoked).length ? <div className="device-list"><h3>Connected Friend PCs</h3><p className="helper-text">A new PC starts with only the server whose code it used. You can give that PC access to any of your saved servers.</p>{companion.devices.filter(device => !device.revoked).map(device => <div className="device access-device" key={device.id}>
                  <div className="device-header"><div className="device-main"><label>PC name<Input value={deviceNames[device.id] ?? device.name} maxLength={48} onChange={event => setDeviceNames(current => ({ ...current, [device.id]: event.target.value }))} /></label><small>{device.approvalPending ? 'Waiting for local approval' : device.lastHeartbeatUtc ? `Last report ${new Date(device.lastHeartbeatUtc).toLocaleTimeString()}` : device.paired ? 'No fresh report' : 'Waiting for this PC to connect'} · {device.profileId === '00000000-0000-0000-0000-000000000000' ? 'Connected with an older code' : `Connected with the code for ${savedProfiles.find(profile => profile.id === device.profileId)?.name ?? 'a removed server'}`}</small></div>
                    <div className="actions device-card-actions">{device.credentialExpiresUtc && <small>Saved access expires {new Date(device.credentialExpiresUtc).toLocaleDateString()}</small>}{device.approvalPending && <Button disabled={!!pending} onClick={() => void approveDevice(device.id)}>Approve this PC</Button>}<Button className="secondary" disabled={!!pending || !(deviceNames[device.id] ?? device.name).trim() || (deviceNames[device.id] ?? device.name).trim() === device.name} onClick={() => void saveDeviceName(device.id)}>Save name</Button><Button className="text-button danger" disabled={!!pending} onClick={() => void revokeDevice(device.id)}>Remove access</Button></div></div>
                  <OwnerAccessDeadlineEditor device={device} disabled={!!pending || !device.paired}
                    onSave={request => saveDeviceAccessExpiry(device.id, request)} onRefresh={refreshCompanion} />
                  <TemporaryHelperAccess device={device} nowMs={nowMs} busy={!!pending}
                    onGrant={duration => void saveTemporaryHelper(device.id, duration)}
                    onEnd={() => void saveTemporaryHelper(device.id)} />
                  <fieldset className="usual-permissions" disabled={device.temporaryHelperActive}><legend>Usual permissions</legend>
                  {(() => {
                    const selectedPreset = permissionPresetDraft[device.id] ?? matchingPermissionPreset(device)
                    const preset = selectedPreset === 'custom' ? null : permissionPresets[selectedPreset]
                    return <div className="permission-preset"><label>Permission preset<Select value={selectedPreset}
                      disabled={!!pending || !device.paired || device.approvalPending}
                      onChange={event => setPermissionPresetDraft(current => ({ ...current, [device.id]: event.target.value as PermissionPreset }))}>
                      <option value="status">Status only</option><option value="start">Can start</option>
                      <option value="helper">Trusted helper</option><option value="custom">Custom</option>
                    </Select></label><div><p>{preset?.detail ?? 'This PC has individual or per-server choices. Use the controls below to keep editing them.'}</p>
                      <small>Applying a preset updates every assigned server and clears its per-server exceptions.</small></div>
                      <Button className="secondary" disabled={!!pending || !preset || selectedPreset === matchingPermissionPreset(device)}
                        onClick={() => void applyDevicePermissionPreset(device, selectedPreset)}>Apply preset</Button></div>
                  })()}
                  <div className="device-access-grid"><div className="device-server-summary"><div className="device-summary-copy"><span>Server access</span><strong>{device.assignedProfileIds.length} {device.assignedProfileIds.length === 1 ? 'server' : 'servers'}</strong><small title={serverAssignmentPreview(device, savedProfiles)}>{serverAssignmentPreview(device, savedProfiles)}</small></div><Button className="secondary" disabled={!!pending || !device.paired || device.approvalPending} onClick={() => openDeviceServerAccess(device)}><Icon name="server" />Choose servers</Button></div>
                    <label className="device-permission-toggle"><MixedCheckbox type="checkbox" mixed={permissionMix(device, 'canStart').mixed} checked={permissionMix(device, 'canStart').all} disabled={!!pending || !device.paired || device.approvalPending} onChange={event => void setDevicePermissions(device, 'canStart', permissionMix(device, 'canStart').mixed ? true : event.target.checked)} /><span><strong>Start servers</strong><small>{permissionMix(device, 'canStart').mixed ? device.canStart ? 'On with server exceptions' : 'Off with server exceptions' : permissionMix(device, 'canStart').all ? 'Allowed on every assigned server' : 'Off on every assigned server'}</small></span></label>
                    <label className="device-permission-toggle"><MixedCheckbox type="checkbox" mixed={permissionMix(device, 'canStop').mixed} checked={permissionMix(device, 'canStop').all} disabled={!!pending || !device.paired || device.approvalPending} onChange={event => void setDevicePermissions(device, 'canStop', permissionMix(device, 'canStop').mixed ? true : event.target.checked)} /><span><strong>Request Stop</strong><small>{permissionMix(device, 'canStop').mixed ? device.canStop ? 'On with server exceptions' : 'Off with server exceptions' : permissionMix(device, 'canStop').all ? 'Allowed on every assigned server' : 'Off on every assigned server'}</small></span></label>
                    <label className="device-permission-toggle"><MixedCheckbox type="checkbox" mixed={permissionMix(device, 'canExtendTimer').mixed} checked={permissionMix(device, 'canExtendTimer').all} disabled={!!pending || !device.paired || device.approvalPending} onChange={event => void setDevicePermissions(device, 'canExtendTimer', permissionMix(device, 'canExtendTimer').mixed ? true : event.target.checked)} /><span><strong>Add shutdown time</strong><small>Off by default. Friends can add only the fixed increment, including while players are online.</small></span></label>
                     <label className="device-permission-toggle"><MixedCheckbox type="checkbox" mixed={permissionMix(device, 'canViewLogs').mixed} checked={permissionMix(device, 'canViewLogs').all} disabled={!!pending || !device.paired || device.approvalPending} onChange={event => void setDevicePermissions(device, 'canViewLogs', permissionMix(device, 'canViewLogs').mixed ? true : event.target.checked)} /><span><strong>View logs</strong><small>{permissionMix(device, 'canViewLogs').mixed ? device.canViewLogs ? 'On with server exceptions' : 'Off with server exceptions' : permissionMix(device, 'canViewLogs').all ? 'Allowed on every assigned server' : 'Off on every assigned server'}. Read-only and independent of remote controls.</small></span></label></div>
                  </fieldset>
                </div>)}</div> : <div className="empty compact-empty"><p>No Friend PCs have connected yet. Choose Invite friends on a server card to copy a private server code.</p></div>}
              </section>}
              {hostSettingsSection === 'network' && <section className="settings-section">
              <h3>Connection checks</h3>
              <PaneErrorBoundary title="Connection Doctor" resetKey={selectedHostProfileId}><ConnectionDoctor
                profile={selectedHostProfile} run={selectedHostRun} ports={portDiagnostics}
                routeCheck={internetRouteCheck} devices={companion?.devices ?? []}
                busy={checkingPorts || checkingInternetRoute || !!pending} routeMode={draft.connectionRoute?.mode ?? 'DirectInternet'}
                onRefresh={() => { void checkPorts(true); void refreshCompanion() }}
                onTestRoute={() => void checkInternetRoute()} onOpenAccess={() => setHostSettingsSection('access')}
                onOpenDiagnostics={() => setHostSettingsSection('diagnostics')} /></PaneErrorBoundary>
              <div className="settings-grid companion-fields">
                <label>Friend route<Select value={draft.connectionRoute?.mode ?? 'DirectInternet'} onChange={event => changeRoute(event.target.value as Settings['connectionRoute']['mode'], event.target.value === 'DirectInternet' ? '' : draft.connectionRoute?.address ?? '')}>
                  <option value="DirectInternet">Direct Internet</option><option value="PrivateMesh">Private mesh</option><option value="AdvancedAddress">Advanced address</option>
                </Select><small>Mesh and advanced routes use networking you install and manage. TogetherServer still verifies the Host, each Friend PC, and which servers it can use.</small></label>
                {(draft.connectionRoute?.mode ?? 'DirectInternet') !== 'DirectInternet' && <label>Selected route IPv4<Input value={draft.connectionRoute?.address ?? ''} onChange={event => changeRoute(draft.connectionRoute.mode, event.target.value)} placeholder="100.64.0.2" /><small>TogetherServer only reads adapters; it does not install clients or change network policy.</small></label>}
                {draft.connectionRoute?.mode === 'PrivateMesh' && <label>Detected private-network adapter<Select value="" onChange={event => event.target.value && changeRoute('PrivateMesh', event.target.value)}><option value="">Choose a detected address</option>{routeDiscovery?.privateMeshCandidates.map(candidate => <option key={`${candidate.interfaceName}-${candidate.address}`} value={candidate.address}>{candidate.provider} · {candidate.address} · {candidate.interfaceName}</option>)}</Select><small>{routeDiscovery?.privateMeshCandidates.length ? 'Selecting an address does not configure that network.' : 'No known Tailscale or ZeroTier adapter is currently up; enter an address manually if appropriate.'}</small></label>}
              </div>
              <p>Game address: {detectedGameIp ? `${detectedGameIp} detected, friend join untested` : 'unavailable'}. Friend app: {friendAppStatus}.</p>
              {companion?.listenerWarning && <p className="warning-text">{companion.listenerWarning}</p>}
              {publicIpDetection && !publicIpDetection.ok && <p className="warning-text">{publicIpDetection.message}</p>}
              <div className="actions"><Button className="secondary" disabled={detectingPublicIp} onClick={() => void detectPublicIp()}>{detectingPublicIp ? <><Icon name="loader" />Refreshing…</> : <><Icon name="refresh" />Refresh public address</>}</Button></div>
              <div className={checkingInternetRoute ? 'internet-route-test refreshing' : 'internet-route-test'} aria-busy={checkingInternetRoute}><Button className="secondary" disabled={checkingInternetRoute || !!pending || dirty} onClick={() => void checkInternetRoute()}>{checkingInternetRoute ? <><Icon name="loader" />Testing TCP port…</> : 'Test Friend app port from internet'}</Button>
                <small>This checks the Friend app TCP port through portchecker.io. That service sees this PC's public IP and port; no server code or saved access is sent.</small>
                {internetRouteCheck && <p className={`internet-route-result ${previousRouteVerdict ? 'neutral' : internetRouteCheck.state === 'Reachable' ? 'good' : internetRouteCheck.state === 'Not reachable' ? 'bad' : 'neutral'}`} role="status">
                  <strong>{previousRouteVerdict ? `Previous TCP ${internetRouteCheck.port} result` : internetRouteCheck.state === 'Reachable' ? `Reachable outside network · TCP ${internetRouteCheck.port}` : internetRouteCheck.state === 'Not reachable' ? `TCP ${internetRouteCheck.port} not reachable` : `${internetRouteCheck.state} · TCP ${internetRouteCheck.port}`}</strong>
                  <span>{internetRouteCheck.detail}</span><small>Checked {new Date(internetRouteCheck.checkedUtc).toLocaleString()}. {previousRouteVerdict && 'This result is no longer current for the saved connection, server code address, or time; test again after checking them. '}This tests TCP access only; your Friend still needs to connect with the code, and the game join needs its own test.</small>
                </p>}
              </div>
              {portDiagnostics?.control.lanForwardDetail && <div className="lan-target-hint"><strong>Router forwarding target on this PC</strong>
                <p>{portDiagnostics.control.lanForwardDetail}</p>
                {portDiagnostics.control.lanAddresses?.map(item => <p key={`${item.interfaceName}-${item.address}`}><code>{item.address}</code> · {item.interfaceName} · gateway {item.gateway}</p>)}
              </div>}
              <p className="helper-text">A Friend on another network must test the app connection and game join separately. Router and firewall changes remain yours to approve.</p>
              <div className="next-action"><span>Friend app connections are <strong>{draft.companionListeningEnabled ? 'on' : 'off'}</strong>.</span><Button className="text-button" onClick={() => setHostSettingsSection('access')}>Manage access</Button></div>
              <details className="advanced-block"><summary>Technical connection details</summary><p className="helper-text">Friend app HTTPS uses TCP {draft.companionPort}. Game ports are separate. Friend PCs connect outbound.</p>{companion?.fingerprint && <p className="footnote">Pinned Host identity: <code>{companion.fingerprint}</code></p>}</details>
              </section>}

              {hostSettingsSection === 'stop' && <section className="settings-section"><h3>Empty-server countdown</h3>
                <p>When a Ready server reports 0 players, TogetherServer counts down and stops it gracefully. Friend apps do not gate the timer. A player or unavailable count pauses the countdown, while added time stays saved and is applied to the next zero-player countdown. A fresh zero-player server check is still required at the end.</p>
                <div className="idle-settings"><label className="setting-toggle"><span><strong>Stop empty servers automatically</strong><small>Off by default. Host and Friend cards share the countdown or explain why it is paused.</small></span><Input type="checkbox" checked={draft.autoShutdownEnabled} disabled={!!pending} onChange={event => void saveHostFlags({ autoShutdownEnabled: event.target.checked })} /></label>
                  <label>Wait after the server reaches 0 players<Input type="number" min="1" max="1440" value={draft.idleMinutes} disabled={!!pending} onChange={event => edit({ ...draft, idleMinutes: Number(event.target.value) })} /><small>Minutes, from 1 to 1440.</small></label>
                  <label>Friend extension increment<Input type="number" min="1" max="120" value={draft.friendTimerExtensionMinutes} disabled={!!pending} onChange={event => edit({ ...draft, friendTimerExtensionMinutes: Number(event.target.value) })} /><small>Fixed minutes added per approved Friend request.</small></label>
                  <label>Friend extension maximum<Input type="number" min="1" max="1440" value={draft.friendTimerExtensionMaximumMinutes} disabled={!!pending} onChange={event => edit({ ...draft, friendTimerExtensionMaximumMinutes: Number(event.target.value) })} /><small>Total Friend-added minutes allowed during one server run.</small></label>
                  <div className="actions"><Button disabled={!dirty || !!pending} onClick={() => void run('save-idle', '/api/local/settings', 'PUT', draft)}>Save timer</Button></div></div>
                <h3>Remote Stop safety</h3>
                <p>There are no player IDs to enter. Each game server reports its current online-player count. A Friend Stop request is allowed only at 0, then TogetherServer checks the count again immediately before sending the graceful stop command.</p>
                <div className="stop-checklist"><strong>How it works</strong><ul>
                  <li className="done"><Icon name="check" />The server must be running and Ready</li>
                  <li className="done"><Icon name="check" />Unknown player counts block Friend Stop</li>
                  <li className="done"><Icon name="check" />One or more online players block Friend Stop</li>
                  <li className={draft.remoteControlsEnabled ? 'done' : ''}><Icon name={draft.remoteControlsEnabled ? 'check' : 'warning'} />Friend controls are {draft.remoteControlsEnabled ? 'on' : 'paused'}</li>
                </ul></div>
                {savedProfiles.map(profile => {
                  const run = snapshot.runs.find(item => item.profileId === profile.id)
                  const safety = companion?.stopSafety?.[profile.id]
                  return <div className="safety-status" key={profile.id}>
                    <strong>{profile.name}: {run?.state === 'Ready' ? playerCount(run.onlinePlayers, run.maxPlayers) : run?.state ?? 'Unknown'}</strong>
                    <p>{safety?.reason ?? 'Checking the server player count…'}</p>
                    <small>The local Stop button on the Host page remains available for the owner's decision.</small>
                  </div>
                })}
              </section>}

              {hostSettingsSection === 'diagnostics' && <PaneErrorBoundary title="Preflight & diagnostics" resetKey={selectedHostProfileId}><OwnerDiagnostics
                selectedProfileId={selectedHostProfileId}
                onSelectedProfileIdChange={setSelectedHostProfileId} /></PaneErrorBoundary>}

              {hostSettingsSection === 'advanced' && <section className="settings-section"><h3>Advanced network and game paths</h3>
              <div className="settings-grid companion-fields">
                <label>Maximum servers running at once<Input type="number" min="1" max="16" value={draft.maxConcurrentServers} onChange={event => edit({ ...draft, maxConcurrentServers: Number(event.target.value) })} /><small>Most homes should leave this at 1.</small></label>
                <label>Friend app TCP port<Input type="number" min="1024" max="65535" value={draft.companionPort} onChange={event => {
                  const port = Number(event.target.value)
                  let endpoint = draft.companionEndpoint
                  if (endpoint) try { const url = new URL(endpoint); url.port = String(port); endpoint = url.origin } catch { /* Validation explains a custom endpoint. */ }
                  edit({ ...draft, companionPort: port, companionEndpoint: endpoint })
                }} /></label>
                <label>Custom secure address<Input value={draft.companionEndpoint} onChange={event => edit({ ...draft, companionEndpoint: event.target.value.trim() })} placeholder={`https://127.0.0.1:${appInstance?.companionPort ?? 5131}`} /></label>
                <label>Bind IP<Input value={draft.companionBindAddress} onChange={event => edit({ ...draft, companionBindAddress: event.target.value })} placeholder="127.0.0.1" /></label>
              </div>
              {companion?.fingerprint && <p className="footnote">Pinned Host identity: <code>{companion.fingerprint}</code></p>}
              {companion?.certificates && <div className="safety-status"><strong>Secure Host identity</strong><p>Current identity is valid until {new Date(companion.certificates.activeExpiresUtc).toLocaleString()}.</p>
                {companion.certificates.nextFingerprint ? <p>The next identity is ready and connected Friends are receiving it.</p> : <p>TogetherServer prepares the next identity automatically within 30 days of expiry.</p>}
                {companion.certificates.previousAcceptedUntilUtc && <p>The old identity will be accepted until {new Date(companion.certificates.previousAcceptedUntilUtc).toLocaleString()}.</p>}
                <div className="actions"><Button className="secondary" disabled={!!pending || !!companion.certificates.nextFingerprint} onClick={() => void certificateAction('stage')}>Prepare next identity</Button>
                  <Button className="secondary" disabled={!!pending || !companion.certificates.nextFingerprint} onClick={() => void certificateAction('activate')}>Use prepared identity</Button>
                  {companion.certificates.previousFingerprint && <Button className="text-button danger" disabled={!!pending} onClick={() => void certificateAction('retire-previous')}>Stop accepting old identity</Button>}</div>
              </div>}
              <div className="actions"><Button disabled={!dirty || !!pending} onClick={() => void run('save', '/api/local/settings', 'PUT', draft)}>Save settings</Button></div>
              {companion?.devices.some(device => device.revoked) ? <details className="advanced-block"><summary>Revoked Friend PCs</summary><div className="profile-list device-list">{companion.devices.filter(device => device.revoked).map(device => <div className="device" key={device.id}>
                <div><strong>{device.name} · {savedProfiles.find(profile => profile.id === device.profileId)?.name ?? 'Legacy access'}</strong><small>Revoked</small></div>
              </div>)}</div></details> : null}
              </section>}
            </div>
        </section>}
        {savedProfiles.length > 0 && serverAccessDevice && <dialog ref={serverAccessRef} className="panel modal-dialog server-access-dialog" aria-labelledby="server-access-title" onCancel={event => { event.preventDefault(); closeDeviceServerAccess() }}>
          <div className="modal-heading"><div><h2 id="server-access-title">Choose servers for {serverAccessDevice.name}</h2><p>Selected servers expose their connection details. Start, Stop, Add shutdown time, and View logs stay independent.</p></div><Button className="secondary" disabled={!!pending} onClick={closeDeviceServerAccess}>Cancel</Button></div>
          {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
          <label className="server-picker-search">Search servers<Input value={serverAccessSearch} autoFocus placeholder="Search by server or game" onChange={event => setServerAccessSearch(event.target.value)} /></label>
          <div className="server-picker-toolbar"><strong>{serverAccessDraft.length} of {savedProfiles.length} selected</strong><div className="actions"><Button className="text-button" disabled={!!pending || visibleServerAccessProfiles.length === 0} onClick={() => setServerAccessDraft(current => [...new Set([...current, ...visibleServerAccessProfiles.map(profile => profile.id)])])}>{normalizedServerSearch ? 'Select all results' : 'Select all'}</Button><Button className="text-button" disabled={!!pending || serverAccessDraft.length === 0} onClick={() => setServerAccessDraft([])}>Clear all</Button></div></div>
          <div className="server-picker-list" role="group" aria-label="Saved servers">{visibleServerAccessProfiles.map(profile => {
            const assigned = serverAccessDraft.includes(profile.id)
            const permission = serverPermissionDraft[profile.id] ?? { canStart: serverAccessDevice.canStart, canStop: serverAccessDevice.canStop, canExtendTimer: serverAccessDevice.canExtendTimer, canViewLogs: serverAccessDevice.canViewLogs }
            return <div className="server-picker-option" key={profile.id}><label className="server-picker-access"><Input type="checkbox" checked={assigned} disabled={!!pending} onChange={event => setServerAccessDraft(current => event.target.checked ? [...new Set([...current, profile.id])] : current.filter(id => id !== profile.id))} /><span><strong>{profile.name}</strong><small>{profileGameLabel(profile)}</small></span></label>
              <div className="server-picker-permissions" aria-label={`${profile.name} permissions`}><label><Input type="checkbox" checked={permission.canStart} disabled={!!pending || !assigned} onChange={event => setServerPermissionDraft(current => ({ ...current, [profile.id]: { ...permission, canStart: event.target.checked } }))} /> Start</label><label><Input type="checkbox" checked={permission.canStop} disabled={!!pending || !assigned} onChange={event => setServerPermissionDraft(current => ({ ...current, [profile.id]: { ...permission, canStop: event.target.checked } }))} /> Stop</label><label><Input type="checkbox" checked={permission.canExtendTimer} disabled={!!pending || !assigned} onChange={event => setServerPermissionDraft(current => ({ ...current, [profile.id]: { ...permission, canExtendTimer: event.target.checked } }))} /> Add time</label><label><Input type="checkbox" checked={permission.canViewLogs} disabled={!!pending || !assigned} onChange={event => setServerPermissionDraft(current => ({ ...current, [profile.id]: { ...permission, canViewLogs: event.target.checked } }))} /> View logs</label></div></div>
          })}
            {visibleServerAccessProfiles.length === 0 && <div className="server-picker-empty">No servers match “{serverAccessSearch.trim()}”.</div>}</div>
          <div className="server-picker-footer"><span>Changes apply when you save.</span><div className="actions"><Button className="secondary" disabled={!!pending} onClick={closeDeviceServerAccess}>Cancel</Button><Button disabled={!!pending} onClick={() => void saveDeviceServerAccess()}>{pending === serverAccessDevice.id ? 'Saving…' : 'Save access'}</Button></div></div>
        </dialog>}
      </>}
      </div></main>
    </div>
    <StatusStrip hostText={snapshot?.mode === 'Host' ? activeRuns ? `${activeRuns} server${activeRuns === 1 ? '' : 's'} running` : 'Host idle' : 'Host capability available'}
      friendText={snapshot?.mode === 'Friend' ? snapshot.state : friendAppStatus} pending={pending}
      version={update?.currentVersion ?? '...'} />
  </div>
}

createRoot(document.getElementById('root')!).render(
  <React.StrictMode><AppErrorBoundary><App /></AppErrorBoundary></React.StrictMode>)

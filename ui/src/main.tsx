import React, { useCallback, useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { ApiError, changeJson, errorMessage, getJson } from './api'
import { AppErrorBoundary } from './AppErrorBoundary'
import { Button, Input, Select } from './Controls'
import { ConnectionDetails } from './ConnectionDetails'
import { DataRecoveryPanel } from './DataRecoveryPanel'
import {
  HostSetupDialog
} from './features/setup/HostSetupDialog'
import { useHostSetup } from './features/setup/useHostSetup'
import {
  parseActionResult, parseAppInstance, parseBasicResult, parseCompanionInfo, parseCustomCertificationResult,
  parseDataRecoveryView, parseDesktopPreferenceResult, parseDesktopPreferences,
  parseFriendSnapshot, parseGameEndpointResult, parseInternetRouteCheck, parseInviteResult,
  parseInviteState, parsePasswordResult, parsePortDiagnostics, parsePublicIpDetection, parseRouteDiscovery,
  parseSnapshot, parseUpdateView, parseWorldBackupList,
  type ActionResult, type AppInstanceView, type BasicResult, type CompanionInfo,
  type DataRecoveryView, type DesktopPreferences, type Device, type FriendIssue,
  type FriendSnapshot, type GameEndpointResult, type PublicIpDetection, type PublicProfile, type RouteDiscovery, type Settings,
  type Snapshot, type UpdateView, type WorldBackupList
} from './contracts'
import { Icon } from './Icon'
import { ServerReadiness, currentOutsideResult, type PortDiagnostics, type InternetRouteCheck } from './ServerReadiness'
import { gameLabel, profileGameLabel, type Profile } from './GameProfile'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'
import './theme.css'
import './style.css'
import './companion.css'

type HostSettingsSection = 'access' | 'stop' | 'network' | 'advanced'
type ConnectionActivity = Record<string, 'copy' | 'reveal'>
type PermissionDraft = Record<string, { canStart: boolean; canStop: boolean; canExtendTimer: boolean }>

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
        return { friend: 'Paste the latest invite from the Host. A saved code may have been refreshed or your PC may have been revoked.',
          host: 'Open Invite friends and copy the current code. Check this Friend PC’s access if it was paired before.' }
      case 'HostAddressMismatch': case 'InviteAddressInvalid':
        return { friend: 'Check the address you entered for an older invite. New invites already include the Host address.',
          host: 'Copy the current invite and check its public HTTPS address against the router’s WAN address.' }
      case 'HostIdentityMismatch':
        return { friend: 'Stop using this invite and request a fresh copy through your usual trusted channel. Do not bypass the HTTPS identity check.',
          host: 'Copy the current invite from the running Host app and verify its published address.' }
      case 'FriendNetworkUnavailable':
        return { friend: 'Restore this PC’s internet connection, then check that the invite has the Host’s current address.',
          host: 'If the Friend PC is online and still cannot connect, verify the published address.' }
      case 'HostPortClosed':
        return { friend: 'Check that the invite is current and that this PC can use the internet.',
          host: 'Keep TogetherServer running. Check the HTTPS listener, inbound Windows Firewall, and router TCP forwarding to the Host PC.' }
      case 'HostPortTimedOut': case 'HostTimedOut': case 'HostUnreachable':
        return { friend: 'Check this PC’s internet connection and the address in the latest invite.',
          host: 'Check the HTTPS listener, inbound Windows Firewall, and router TCP forwarding. Compare the router WAN address with the invite; ask your ISP about shared-address NAT or inbound filtering if they differ.' }
      case 'HostBusy':
        return { friend: 'Wait a moment before trying the same current invite again.',
          host: 'Keep the Host app running and check whether it is limiting or failing requests.' }
      case 'HostUnavailable': case 'HostInvalidResponse':
        return { friend: 'Wait until the Host confirms their app is running, then check the connection again.',
          host: 'Check the Host app and HTTPS listener. If it is responding with an error, review its local connection status.' }
      case 'HostAccessDenied':
        return { friend: 'Ask the Host whether this PC still has access. Use a fresh invite if they refreshed it.',
          host: 'Check the Friend PC’s pairing and access in the Host app.' }
      case 'LocalAppUnavailable':
        return { friend: 'Reopen TogetherServer on this PC and try again.',
          host: 'No Host network change is needed until the Friend app can reach its own local service.' }
      default:
        return { friend: 'Check this PC’s internet connection and the address in the latest invite.',
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
  if (['Disabled', 'Starting', 'Stopping'].includes(state)) return 'paused'
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

function devicePermission(device: Device, profileId: string) {
  return device.serverPermissions?.find(permission => permission.profileId === profileId) ??
    { profileId, canStart: device.canStart, canStop: device.canStop, canExtendTimer: device.canExtendTimer }
}

function permissionMix(device: Device, action: 'canStart' | 'canStop' | 'canExtendTimer') {
  const values = device.assignedProfileIds.map(profileId => devicePermission(device, profileId)[action])
  const global = device[action]
  return { mixed: values.some(value => value !== global), all: global }
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
  const [maintenanceMessages, setMaintenanceMessages] = useState<Record<string, string>>({})
  const [invitation, setInvitation] = useState('')
  const [pairingDurationMinutes, setPairingDurationMinutes] = useState('30')
  const [pairingDeviceLimit, setPairingDeviceLimit] = useState('1')
  const [pairingRequireApproval, setPairingRequireApproval] = useState(false)
  const [pairingExpiresUtc, setPairingExpiresUtc] = useState<string | null>(null)
  const [friendInvite, setFriendInvite] = useState('')
  const [friendHostAddress, setFriendHostAddress] = useState('')
  const [recoveryEndpoint, setRecoveryEndpoint] = useState('')
  const [friendConnectionName, setFriendConnectionName] = useState('')
  const [gameEndpointResults, setGameEndpointResults] = useState<Record<string, GameEndpointResult>>({})
  const [backupLists, setBackupLists] = useState<Record<string, WorldBackupList>>({})
  const [pairIssue, setPairIssue] = useState<FriendIssue | null>(null)
  const [showPairing, setShowPairing] = useState(false)
  const [countdownExtensions, setCountdownExtensions] = useState<Record<string, string>>({})
  const [dataRecovery, setDataRecovery] = useState<DataRecoveryView | null>(null)
  const [recoveryConfirmed, setRecoveryConfirmed] = useState(false)
  const [showHostSettings, setShowHostSettings] = useState(false)
  const [serverAccessDeviceId, setServerAccessDeviceId] = useState('')
  const [serverAccessDraft, setServerAccessDraft] = useState<string[]>([])
  const [serverPermissionDraft, setServerPermissionDraft] = useState<PermissionDraft>({})
  const [serverAccessSearch, setServerAccessSearch] = useState('')
  const [hostSettingsSection, setHostSettingsSection] = useState<HostSettingsSection>('access')
  const [revealedConnections, setRevealedConnections] = useState<Record<string, boolean>>({})
  const [revealedGamePasswords, setRevealedGamePasswords] = useState<Record<string, string>>({})
  const [connectionActivity, setConnectionActivity] = useState<ConnectionActivity>({})
  const inviteLoad = useRef(0)
  const snapshotEpochRef = useRef(0)
  const liveConnectionKeysRef = useRef<Set<string>>(new Set())
  const connectionRevealRequestRef = useRef<Record<string, number>>({})
  const hostSettingsRef = useModalDialog(showHostSettings)
  const serverAccessRef = useModalDialog(!!serverAccessDeviceId)

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
    addCustomPort, removeCustomPort, browseCustomDirectory, applyMinecraftInstallation, scanMinecraft,
    installMinecraft, changeGameKind, addProfile, removeProfile, cancelSetup, finishSetupLater, openSetup,
    continueSetup, scanValheim, importWorld, browseServer, browseMinecraft, browseWorld, setSetupStep,
    setSourceRoot, setPassword, setShowPassword, setMinecraftSetupModeFor, setMinecraftTermsFor
  } = setup
  const currentMode = snapshot?.mode
  const currentFriendEndpoint = snapshot?.mode === 'Friend' ? snapshot.endpoint : ''
  const currentFriendConnectionId = snapshot?.mode === 'Friend' ? snapshot.connectionId : ''
  const currentFriendConnectionName = snapshot?.mode === 'Friend' ? snapshot.connectionName : null
  const recoverySignature = dataRecovery ? JSON.stringify({ lifecycleBlocked: dataRecovery.lifecycleBlocked,
    notices: dataRecovery.notices.map(item => [item.stateFile, item.quarantinedFile, item.detectedUtc]) }) : ''

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

  const latestActivityId = snapshot?.activity?.[0]?.id ?? null
  useEffect(() => {
    if (latestActivityId) setNotificationUnread(true)
  }, [latestActivityId])

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
  const issueInvite = async (profileId: string, refresh = false, preserveActivePolicy = false): Promise<string | null> => {
    if (refresh && !window.confirm('Refresh this server code? Every Friend PC that connected with this code will lose its credential and need to connect again. Any extra servers assigned to those credentials will also be removed.')) return null
    setPending('invite')
    setNotice(null)
    try {
      let durationMinutes = Number(pairingDurationMinutes)
      let deviceLimit = Number(pairingDeviceLimit)
      let requireApproval = pairingRequireApproval
      if (!Number.isSafeInteger(durationMinutes) || durationMinutes < 5 || durationMinutes > 1440 ||
          !Number.isSafeInteger(deviceLimit) || deviceLimit < 1 || deviceLimit > 25) {
        setNotice({ good: false, text: 'Use 5 to 1440 minutes and a limit of 1 to 25 PCs.' })
        return null
      }
      const current = await changeJson(`/api/local/servers/${profileId}/invite/current`, 'POST', parseInviteState)
      const canStart = current.canStart
      if (preserveActivePolicy && current.exists && current.open) {
        durationMinutes = current.durationMinutes
        deviceLimit = current.deviceLimit
        requireApproval = current.requireApproval
        setPairingDurationMinutes(String(durationMinutes))
        setPairingDeviceLimit(String(deviceLimit))
        setPairingRequireApproval(requireApproval)
      }
      const result = await changeJson(`/api/local/servers/${profileId}/invite`, 'POST', parseInviteResult,
        { refresh, canStart, enableConnections: true, durationMinutes, deviceLimit, requireApproval })
      const listenerWarning = result.ok && result.listenerActive !== true
        ? result.listenerWarning || `The HTTPS listener on TCP ${draft?.companionPort ?? 'the configured port'} did not start. Check Connection help before sharing this code.`
        : null
      setInviteListenerWarning(listenerWarning || (!result.ok ? result.message : null))
      setNotice({ good: result.ok && !listenerWarning, text: listenerWarning || result.message })
      if (result.ok && result.password) {
        setInvitation(result.password)
        setPairingExpiresUtc(result.expiresUtc ?? new Date(Date.now() + durationMinutes * 60_000).toISOString())
        await refreshCompanion()
        const host = await readSnapshot()
        if (host.mode === 'Host') {
          applySnapshot(host)
          setDraftIfClean(host.settings)
        }
        await checkPorts()
        return listenerWarning ? null : result.password
      }
      return null
    } catch (error) { setInviteListenerWarning(errorMessage(error)); setNotice({ good: false, text: errorMessage(error) }); return null }
    finally { setPending('') }
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
  const pairingPolicyAction = async (profileId: string, action: 'close' | 'emergency-revoke') => {
    if (action === 'emergency-revoke' && !window.confirm('Emergency-revoke every PC credential issued through this server code? This does not affect PCs paired through other server codes.')) return
    setPending('invite')
    try {
      const result = await change(`/api/local/servers/${profileId}/pairing/${action}`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) { setInvitation(''); setPairingExpiresUtc(null); setInviteProfileId('') }
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
  const setDevicePermissions = async (device: Device, canStart: boolean, canStop: boolean,
    canExtendTimer: boolean, scope: 'start' | 'stop' | 'extend') => {
    setPending(device.id)
    try {
      const result = await change(`/api/local/devices/${device.id}/permissions`, 'PUT',
        { canStart, canStop, canExtendTimer, scope })
      setNotice({ good: result.ok, text: result.message })
      await refreshCompanion()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const saveHostFlags = async (patch: Partial<Pick<Settings, 'companionListeningEnabled' | 'remoteControlsEnabled' | 'autoShutdownEnabled'>>) => {
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
        canExtendTimer: permission.canExtendTimer }]
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
          canExtendTimer: serverPermissionDraft[profileId]?.canExtendTimer ?? serverAccessDevice?.canExtendTimer ?? false
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
    setShowHostSettings(true)
    if (!routeDiscovery) void getJson('/api/local/network/routes', parseRouteDiscovery)
      .then(setRouteDiscovery)
      .catch(() => { /* Manual route entry remains available. */ })
  }
  const closeHostSettings = () => {
    if (snapshot?.mode !== 'Host' || pending) return
    if (dirty && !window.confirm('Discard unsaved advanced settings?')) return
    acceptSavedSettings(snapshot.settings)
    setShowHostSettings(false)
    setNotice(null)
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
          setNotice({ good: true, text: 'Server code copied. Share it privately with your friends.' })
        }
        catch { setNotice({ good: false, text: 'Invite ready, but it could not be copied. Choose Copy again.' }) }
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
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
    ? snapshot.runs.filter(run => ['Process running', 'Starting', 'Ready'].includes(run.state)).length : 0
  const currentRouteResult = !dirty && companion?.listenerActive
    ? currentOutsideResult(portDiagnostics?.control, internetRouteCheck) : null
  const previousRouteVerdict = !currentRouteResult &&
    (internetRouteCheck?.state === 'Reachable' || internetRouteCheck?.state === 'Not reachable')
  const activeInviteWarning = inviteListenerWarning || (invitation && companion?.listenerActive === false
    ? companion.listenerWarning || 'Friend app connections are off. Choose Invite friends again to start the HTTPS listener.'
    : null)
  const recentActivity = snapshot?.activity ?? []

  return <div className="shell">
    <header className="topbar">
      <nav className="mode-switch" aria-label="App pages">
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
            <div className="notification-panel-heading"><strong>Notifications</strong><small>Recent app and connection activity</small></div>
            {update?.state === 'Available' && <div className="notification-item update" role="status"><span><Icon name="refresh" /></span><div><strong>Update available · v{update.latestVersion}</strong><p>Stop hosted servers before updating.</p><Button disabled={updateBusy || !!pending || dirty || activeRuns > 0} title={dirty ? 'Save setup changes before updating.' : activeRuns > 0 ? 'Stop hosted servers before updating.' : undefined} onClick={() => void installUpdate()}>{updateBusy ? <><Icon name="loader" />Preparing update…</> : 'Update and restart'}</Button></div></div>}
            {notice && <div className={`notification-item ${notice.good ? 'good' : 'bad'}`} role="status"><span><Icon name={notice.good ? 'check' : 'warning'} /></span><div><strong>{notice.good ? 'Updated' : 'Needs attention'}</strong><p>{notice.text}</p></div></div>}
            {recentActivity.slice(0, 8).map(item => <div className={`notification-item ${item.severity === 'Warning' ? 'bad' : item.severity === 'Important' ? 'good' : ''}`} key={item.id}><span><Icon name={item.severity === 'Warning' ? 'warning' : 'check'} /></span><div><strong>{item.category}</strong><p>{item.message}</p><small>{new Date(item.occurredUtc).toLocaleString()}</small></div></div>)}
            {!notice && update?.state !== 'Available' && recentActivity.length === 0 && <p className="notification-empty">No recent activity.</p>}
          </div>
        </details>
        <details className="app-menu"><summary aria-label="App settings" title="App settings"><Icon name="settings" size={19} /></summary><div className="app-menu-panel"><strong>App settings</strong>
          <div className="app-version"><span>Version {update?.currentVersion ?? 'checking…'}</span><Button className="text-button" disabled={updateBusy || !!pending || appInstance?.updatesAvailable === false} onClick={() => void checkUpdate()}>{appInstance?.updatesAvailable === false ? 'Updates off in staging' : updateBusy ? 'Checking…' : 'Check for updates'}</Button></div>
          <label className="check-row"><Input type="checkbox" checked={desktopPreferences?.launchAtLogin ?? false} disabled={!desktopPreferences?.available || !desktopPreferences.startupAvailable || desktopBusy} onChange={event => void saveDesktopPreference({ launchAtLogin: event.target.checked })} />Open at Windows sign-in</label><small>{appInstance?.isStaging ? 'Disabled in staging so the stable app keeps its sign-in setting.' : 'Starts quietly in the tray.'}</small>
          <label className="check-row"><Input type="checkbox" checked={desktopPreferences?.closeToTray ?? false} disabled={!desktopPreferences?.available || desktopBusy} onChange={event => void saveDesktopPreference({ closeToTray: event.target.checked })} />Close to tray</label><small>Hosting and Friend checks keep running.</small>
          <Button className="app-menu-quit" disabled={!desktopPreferences?.available} onClick={() => void quitApp()}>Quit {appInstance?.displayName ?? 'TogetherServer'}</Button>
        </div></details>
      </div>
    </header>

    {appInstance?.isStaging && <aside className="staging-banner" role="status"><strong>STAGING</strong><span>Fresh disposable worlds only. Production profiles, credentials, settings, runs, and world saves are not loaded or copied.</span></aside>}

    <main>
      <div className="page-heading"><div><h1>{snapshot?.mode === 'Friend' ? 'Join' : 'Host'}</h1>
        <p>{snapshot?.mode === 'Friend' ? 'Connect to a server without interrupting anything you host on this PC.' : savedProfiles.length === 0 ? 'Set up a server, or switch to Join if a friend sent you a code.' : activeRuns ? `${activeRuns} ${activeRuns === 1 ? 'server is' : 'servers are'} running.` : 'Start a saved server when your group is ready.'}</p></div>
      </div>

      {loadError && <div className="notice bad" role="alert">Connection to this local app failed: {loadError}</div>}
      {(!snapshot || !appInstance) && !loadError && <section className="panel">Loading local state…</section>}

      {dataRecovery && <DataRecoveryPanel recovery={dataRecovery} mode={snapshot?.mode ?? null}
        runs={snapshot?.mode === 'Host' ? snapshot.runs : []}
        configuredProfileIds={snapshot?.mode === 'Host' ? snapshot.settings.profiles.map(profile => profile.id) : []}
        pending={pending} confirmed={recoveryConfirmed} onConfirmedChange={setRecoveryConfirmed}
        onAcknowledge={() => void acknowledgeDataRecovery()} onSwitchToHost={() => void switchMode('host')}
        onStopRecordedRun={profileId => void run(`recovery-stop-${profileId}`, `/api/local/profiles/${profileId}/stop`, 'POST')}
        onForgetRecordedRun={profileId => void run(`recovery-forget-${profileId}`, `/api/local/profiles/${profileId}/forget`, 'POST')} />}

      {snapshot?.mode === 'Friend' && <>
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
            {snapshot.connectionCode && (snapshot.state === 'Disconnected/Unknown' || snapshot.state === 'Revoked' || snapshot.state === 'Awaiting approval') && <details className="troubleshoot-block" open><summary>{snapshot.state === 'Awaiting approval' ? 'Waiting for Host approval' : 'Troubleshoot connection'}</summary>{snapshot.state === 'Awaiting approval' ? <p>The credential is saved. Ask the Host owner to approve this PC in Friend access; no new code is needed.</p> : <FriendConnectionHelp code={snapshot.connectionCode} />}</details>}
            <div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => void checkFriendConnection()}>{pending === 'poll' ? <><Icon name="loader" />Refreshing…</> : 'Check connection'}</Button>
              <Button className="text-button" onClick={() => { setShowPairing(true); setFriendHostAddress(''); setFriendInvite(''); setPairIssue(null) }}>Add another server</Button></div>
            <details className="advanced-block"><summary>Connection identity and recovery</summary>
              <label>Saved connection name<div className="field-with-button"><Input value={friendConnectionName} maxLength={48} onChange={event => setFriendConnectionName(event.target.value)} /><Button className="secondary" disabled={!!pending || !friendConnectionName.trim() || friendConnectionName.trim() === snapshot.connectionName} onClick={() => void renameFriendConnection()}>Rename</Button></div></label>
              <p className="helper-text">Route: {snapshot.routeMode === 'PrivateMesh' ? 'Private mesh' : snapshot.routeMode === 'AdvancedAddress' ? 'Advanced address' : 'Direct Internet'}{snapshot.routeAddress ? ` (${snapshot.routeAddress})` : ''}. Host {snapshot.hostVersion ?? 'unknown'} · this app {snapshot.friendVersion ?? 'unknown'} · protocol {snapshot.hostProtocolVersion ?? 'unknown'}.</p>
              <p className="helper-text">Credential expires {snapshot.credentialExpiresUtc ? new Date(snapshot.credentialExpiresUtc).toLocaleString() : 'unknown'}. Certificate expires {snapshot.certificateExpiresUtc ? new Date(snapshot.certificateExpiresUtc).toLocaleString() : 'unknown'}.</p>
              <label>New Host endpoint<Input value={recoveryEndpoint} onChange={event => setRecoveryEndpoint(event.target.value.trim())} placeholder={`https://100.64.0.2:${appInstance?.companionPort ?? 5131}`} /><small>The existing Host certificate pin and this PC's credential must both work at the new address. A different certificate is never trusted silently.</small></label>
              <Button className="secondary" disabled={!!pending || !recoveryEndpoint} onClick={() => void recoverFriendEndpoint()}>{pending === 'recover-endpoint' ? 'Verifying...' : 'Verify and update endpoint'}</Button>
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
          {snapshot.endpoint && !showPairing && snapshot.profiles.length === 0 && (snapshot.state === 'Connected' || snapshot.state === 'Disabled') && <div className="empty compact-empty"><p>The Host has not assigned any servers to this PC. Ask the Host to open Friend access and choose the servers you can control.</p></div>}
          {snapshot.endpoint && !showPairing && snapshot.profiles.length > 0 && <div className="friend-server-list"><h3>{snapshot.profiles.length === 1 ? 'Server' : 'Servers'}</h3>
          {snapshot.profiles.map(profile => {
            const connectionKey = `friend-${snapshot.connectionId}-${profile.id}`
            const addressKey = `${connectionKey}-address`
            const addressActivity = connectionActivity[addressKey] ?? null
            const operationBusy = profile.operation?.state === 'Pending' || profile.operation?.state === 'Running'
            const operationConflict = profile.operation?.code === 'PortConflict' && profile.operation.portConflicts?.length
              ? { message: profile.operation.message, conflicts: profile.operation.portConflicts } : null
            return <article className="profile-card" key={profile.id} aria-busy={pending === 'poll' || pending.endsWith(profile.id)}>
              <div className="profile-top"><div><h3>{profile.name}</h3><p>{gameLabel(profile.kind)}</p><ServerActivity state={profile.state} online={profile.onlinePlayers} capacity={profile.maxPlayers} deadline={profile.autoShutdownAtUtc} timerReason={profile.autoShutdownReason} nowMs={nowMs}
                refreshing={pending === `friend-refresh-${profile.id}`} refreshDisabled={!!pending || !['Connected', 'Disabled'].includes(snapshot.state)}
                onRefresh={() => void friendAction(profile.id, 'refresh')} /></div><span className={`status ${statusTone(profile.state)}`}>{pending === 'poll' && <Icon name="loader" />}{profile.state === 'Ready' ? 'Ready to join' : profile.state}</span></div>
              {profile.operation && <div className={`notice ${profile.operation.state === 'Failed' || profile.operation.state === 'Interrupted' ? 'bad' : 'good'}`} role="status"><strong>{profile.operation.action[0].toUpperCase() + profile.operation.action.slice(1)}: {profile.operation.state}</strong><p>{profile.operation.message}</p></div>}
              {profile.maintenanceEnabled && <div className="notice bad" role="status"><strong>Maintenance mode</strong><p>{profile.maintenanceMessage || 'The Host has paused remote actions for this server.'}</p></div>}
              {profile.state === 'Ready' && profile.joinAddress && <ConnectionDetails
                fields={[{ id: addressKey, label: 'Server IP', value: profile.joinAddress,
                  revealed: !!revealedConnections[addressKey], copying: addressActivity === 'copy', revealing: false,
                  onReveal: () => revealConnectionDetails(addressKey), onHide: () => hideConnectionDetails(addressKey),
                  onCopy: () => void copyConnectionValue(addressKey, profile.joinAddress!, 'Server IP') }]}
                refreshing={pending === 'poll'} note={profile.kind === 'Valheim' ? 'The game password is shared separately by your Host.' : undefined}
                />}
              <div className="actions server-actions">
                {profile.state === 'Offline' && snapshot.state === 'Connected' && profile.canStart && <Button disabled={!!pending || operationBusy || profile.maintenanceEnabled} onClick={() => void friendAction(profile.id, 'start')}>{pending === `friend-start-${profile.id}` ? <><Icon name="loader" />Starting…</> : <><Icon name="play" />Start server</>}</Button>}
                {profile.state === 'Ready' && snapshot.state === 'Connected' && profile.canStop && profile.canStopNow && <Button className="secondary" disabled={!!pending || operationBusy || profile.maintenanceEnabled} onClick={() => void friendAction(profile.id, 'stop')}>{pending === `friend-stop-${profile.id}` ? <><Icon name="loader" />Stopping…</> : <><Icon name="stop" />Stop server</>}</Button>}
                {profile.state === 'Ready' && snapshot.state === 'Connected' && profile.canRestartNow && <Button className="secondary" disabled={!!pending || operationBusy || profile.maintenanceEnabled} onClick={() => void friendAction(profile.id, 'restart')}>{pending === `friend-restart-${profile.id}` ? <><Icon name="loader" />Restarting…</> : <><Icon name="refresh" />Restart server</>}</Button>}
                {profile.autoShutdownAtUtc && snapshot.state === 'Connected' && profile.canExtendTimer && <Button className="secondary" disabled={!!pending || operationBusy || profile.maintenanceEnabled || profile.timerExtensionRemainingMinutes < profile.timerExtensionMinutes} onClick={() => void friendAction(profile.id, 'extend')}>{pending === `friend-extend-${profile.id}` ? <><Icon name="loader" />Adding time…</> : <>Add {profile.timerExtensionMinutes} minutes</>}</Button>}
                {profile.state === 'Ready' && ['Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(profile.kind) && <Button className="text-button" disabled={!!pending || !profile.joinAddress} onClick={() => void probeGameEndpoint(profile.id)}>{pending === `probe-game-${profile.id}` ? 'Checking game endpoint...' : 'Check game endpoint from this PC'}</Button>}
              </div>
              {gameEndpointResults[profile.id] && <p className={gameEndpointResults[profile.id].answered ? 'helper-text' : 'warning-text'}>{gameEndpointResults[profile.id].message}</p>}
              {operationConflict && <div className="port-conflict-action" role="alert"><strong>Shared game port</strong><p>{operationConflict.message}</p>
                {operationConflict.conflicts.every(conflict => conflict.canReplace) ? <Button disabled={!!pending || operationBusy} onClick={() => void friendAction(profile.id, 'replace')}>{pending === `friend-replace-${profile.id}` ? <><Icon name="loader" />Switching…</> : <>Stop empty server and start this one</>}</Button>
                  : <small>{operationConflict.conflicts.find(conflict => !conflict.canReplace)?.blockReason ?? 'The other server cannot be stopped safely.'}</small>}</div>}
              <FriendStopBlockers snapshot={snapshot} profile={profile} />
              {profile.state === 'Offline' && !profile.canStart && snapshot.state === 'Connected' && <p className="helper-text">The Host has not allowed this PC to start this server.</p>}
              {profile.state === 'Ready' && !profile.joinAddress && <p className="helper-text">The Host has not found a current game address yet.</p>}
            </article>
          })}
          </div>}
        </section>
      </>}

      {snapshot?.mode === 'Host' && draft && <>
        {savedProfiles.length > 0 && <>
        <section className="panel">
          <div className="section-heading server-heading"><div><h2>Servers</h2></div>
            <div className="server-toolbar"><Button disabled={!!pending || dirty} onClick={addProfile}>Add server</Button>
              <Button className="secondary" disabled={!!pending || dirty} onClick={() => openHostSettings('access')}><Icon name="invite" />Friend access</Button></div></div>
          <div className="profile-list server-grid">
            {snapshot.settings.profiles.map(profile => {
              const status = snapshot.runs.find(run => run.profileId === profile.id)
              const certification = snapshot.customCertifications?.[profile.id]
              const recovery = snapshot.crashRecovery?.[profile.id]
              const backupStatus = snapshot.backups?.[profile.id]
              const backupList = backupLists[profile.id]
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
              return <article className="profile-card" key={profile.id} aria-busy={checkingPorts || detectingPublicIp || pending.endsWith(profile.id)}>
              <div className="profile-top"><div><h3>{profile.name}</h3><p>{profileGameLabel(profile)} · World {profile.worldId}</p><ServerActivity state={status?.state ?? 'Unknown'} online={status?.onlinePlayers ?? null} capacity={status?.maxPlayers ?? null} deadline={status?.autoShutdownAtUtc ?? null} timerReason={status?.autoShutdownReason ?? null} nowMs={nowMs} players={status?.playerNames}
                refreshing={pending === `players-${profile.id}`} refreshDisabled={!!pending || dirty}
                onRefresh={() => void run(`players-${profile.id}`, `/api/local/profiles/${profile.id}/players/refresh`, 'POST')} /></div>
                  <span className={`status ${statusTone(status?.state ?? 'Unknown')}`}>{(pending === `start-${profile.id}` || pending === `stop-${profile.id}` || pending === `restart-${profile.id}`) && <Icon name="loader" />}{status?.state === 'Process running' ? 'Starting' : status?.state ?? 'Unknown'}</span></div>
                <ServerReadiness profileId={profile.id} status={status?.state ?? 'Unknown'} ports={portDiagnostics} routeCheck={internetRouteCheck}
                  busy={checkingPorts || !!pending} refreshing={checkingPorts} onRefresh={() => void checkPorts(true)} onOpenConnection={() => openHostSettings('network')} />
                {profile.maintenance?.enabled && <div className="notice bad" role="status"><strong>Maintenance mode is on</strong><p>{profile.maintenance.message || 'Friends can see status, but remote lifecycle actions are paused.'}</p><Button className="secondary" disabled={!!pending || dirty} onClick={() => void saveMaintenance(profile, false)}>End maintenance</Button></div>}
                <details className="advanced-block"><summary>Friend coordination and maintenance</summary>
                  <label>Message for assigned Friends<Input maxLength={200} value={maintenanceMessages[profile.id] ?? profile.maintenance?.message ?? ''} onChange={event => setMaintenanceMessages(current => ({ ...current, [profile.id]: event.target.value }))} placeholder="Updating mods until 8 PM" /><small>Up to 200 characters. Status remains visible while remote Start, Stop, Restart, replacement, and timer extension are denied.</small></label>
                  <div className="actions"><Button className="secondary" disabled={!!pending || dirty || profile.maintenance?.enabled} onClick={() => void saveMaintenance(profile, true)}>Enable maintenance</Button>{profile.maintenance?.enabled && <Button className="text-button" disabled={!!pending || dirty} onClick={() => void saveMaintenance(profile, false)}>End maintenance</Button>}</div>
                </details>
                {status?.state === 'Ready' && gameAddress && <ConnectionDetails fields={connectionFields}
                  refreshing={checkingPorts || detectingPublicIp} />}
                {status?.autoShutdownAtUtc && <div className="timer-extension"><label>Extend this countdown<Input type="number" min="1" step="1" value={countdownExtensions[profile.id] ?? '15'} disabled={!!pending || dirty} onChange={event => setCountdownExtensions(current => ({ ...current, [profile.id]: event.target.value }))} /><small>Extra minutes for this countdown only.</small></label><Button className="secondary" disabled={!!pending || dirty} onClick={() => void extendCountdown(profile.id)}>{pending === `extend-${profile.id}` ? 'Adding…' : 'Add time'}</Button></div>}
                <div className="actions server-actions">
                  {status?.state === 'Offline' && <Button disabled={!!pending || dirty || dataRecovery?.lifecycleBlocked} title={dataRecovery?.lifecycleBlocked ? 'Resolve the local data recovery warning first.' : undefined} onClick={() => void run(`start-${profile.id}`, `/api/local/profiles/${profile.id}/start`, 'POST')}>{pending === `start-${profile.id}` ? <><Icon name="loader" /><span>Starting…</span></> : <><Icon name="play" /><span>Start server</span></>}</Button>}
                  {['Process running', 'Starting', 'Ready'].includes(status?.state ?? '') && <Button disabled={!!pending || dirty} onClick={() => { hideConnectionDetails(addressKey); hideConnectionDetails(passwordKey); void run(`stop-${profile.id}`, `/api/local/profiles/${profile.id}/stop`, 'POST') }}>{pending === `stop-${profile.id}` ? <><Icon name="loader" /><span>Stopping…</span></> : <><Icon name="stop" /><span>Stop server</span></>}</Button>}
                  {status?.state === 'Ready' && <Button className="secondary" disabled={!!pending || dirty || dataRecovery?.lifecycleBlocked} title={dataRecovery?.lifecycleBlocked ? 'Resolve the local data recovery warning first.' : undefined} onClick={() => { hideConnectionDetails(addressKey); hideConnectionDetails(passwordKey); void run(`restart-${profile.id}`, `/api/local/profiles/${profile.id}/restart`, 'POST') }}>{pending === `restart-${profile.id}` ? <><Icon name="loader" /><span>Restarting…</span></> : <><Icon name="refresh" /><span>Restart server</span></>}</Button>}
                  <Button className="secondary server-invite-button" disabled={!!pending || dirty || !friendAppAddress} onClick={() => void inviteFriend(profile.id)}><Icon name="invite" /><span>Invite friends</span></Button>
                </div>
                {inviteProfileId === profile.id && <div className="inline-invite">
                  {invitation ? <><div className="invite-ready"><span><Icon name={activeInviteWarning ? 'warning' : 'check'} /></span><div><strong>{activeInviteWarning ? 'Friend connection needs attention' : 'Server code copied'}</strong><p>{activeInviteWarning ? 'Fix the issue below before sharing this code.' : 'Send the copied code privately. Your Friend still needs to test Connect.'}</p></div></div>
                    {pairingExpiresUtc && <p className="helper-text">This window closes {new Date(pairingExpiresUtc).toLocaleString()}, or sooner when its device limit is reached.</p>}
                    {activeInviteWarning && <p className="connection-warning" role="alert">{activeInviteWarning}</p>}
                    {!activeInviteWarning && currentRouteResult?.state === 'Not reachable' && <p className="connection-warning" role="alert">The internet test could not reach this PC at {new Date(currentRouteResult.checkedUtc).toLocaleTimeString()}. Open Friend access to fix the connection before sharing.</p>}
                    <div className="actions">{activeInviteWarning
                      ? <Button disabled={!!pending} onClick={() => void inviteFriend(profile.id)}><Icon name="refresh" />Try connection again</Button>
                      : <Button onClick={() => void copyText(invitation, 'Server code')}><Icon name="copy" />Copy again</Button>}
                      <Button className="secondary" disabled={!!pending} onClick={() => void pairingPolicyAction(profile.id, 'close')}>Close pairing</Button>
                      <Button className="danger-outline" disabled={!!pending} onClick={() => void pairingPolicyAction(profile.id, 'emergency-revoke')}>Emergency-revoke code credentials</Button>
                      <Button className="text-button" onClick={() => { setInviteProfileId(''); setInvitation(''); setInviteListenerWarning(null) }}>Done</Button></div>
                    <details className="advanced-block"><summary>Pairing window options</summary><div className="settings-grid"><label>Window minutes<Input type="number" min="5" max="1440" value={pairingDurationMinutes} onChange={event => setPairingDurationMinutes(event.target.value)} /></label><label>New PC limit<Input type="number" min="1" max="25" value={pairingDeviceLimit} onChange={event => setPairingDeviceLimit(event.target.value)} /></label></div><label className="check-row"><Input type="checkbox" checked={pairingRequireApproval} onChange={event => setPairingRequireApproval(event.target.checked)} />Require local Host approval for each new PC</label><Button className="secondary" disabled={!!pending} onClick={() => void issueInvite(profile.id)}>Apply options and copy code</Button><small>The same active policy keeps its current window. Changing the policy opens a replacement window without revoking already paired PCs.</small></details>
                    <p>Close pairing only blocks future PCs. Revoke a single PC under Friend access. Emergency revoke affects every PC issued through this server code.</p></>
                    : inviteListenerWarning ? <><p className="connection-warning" role="alert">{inviteListenerWarning}</p>
                      <div className="actions"><Button disabled={!!pending} onClick={() => void inviteFriend(profile.id)}><Icon name="refresh" />Try again</Button>
                        <Button className="text-button" onClick={() => { setInviteProfileId(''); setInviteListenerWarning(null) }}>Done</Button></div></>
                      : <p className="helper-text">Preparing this server's invite…</p>}
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
                    {certification?.certified && <Button className="danger-outline" disabled={!!pending} onClick={() => { if (window.confirm('Revoke remote lifecycle authority for this Custom server? Local owner controls will remain available.')) void customCertificationAction(profile.id, 'revoke') }}>Revoke certification</Button>}
                  </div>
                  {!certification?.certified && status?.state !== 'Offline' && !certification?.inProgress && certification?.stage !== 'Failed' && <small>Stop the server locally before beginning certification.</small>}
                </div>}
                {recovery && <div className={`safety-status ${recovery.state === 'Suspended' ? 'warning-text' : ''}`}><strong>Crash recovery: {recovery.state}</strong><p>{recovery.state === 'Pending' && recovery.nextAttemptUtc ? `Attempt ${recovery.attempts + 1} of 3 after ${new Date(recovery.nextAttemptUtc).toLocaleString()}.` : recovery.state === 'Starting' ? `Recovery attempt ${recovery.attempts} of 3 is starting.` : recovery.state === 'Recovered' ? `Ready again after ${recovery.attempts} attempt${recovery.attempts === 1 ? '' : 's'}.` : `Suspended after ${recovery.attempts} failed attempts.`}</p>{recovery.lastFailure && <small>{recovery.lastFailure}</small>}</div>}
                <details className="advanced-block card-manage"><summary>Manage server</summary>
                  <p className="helper-text">Playing on this PC? Join <code>127.0.0.1:{profile.gamePort}</code>.</p>
                  <div className="actions"><Button className="secondary" disabled={!!pending || status?.state !== 'Offline'} onClick={() => openSetup(profile.id)}>Edit setup</Button><Button className="secondary" onClick={() => openHostSettings('network')}>Connection help</Button><Button className="secondary" disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/health`, 'POST')}>Check server health</Button>
                    {status?.state === 'Failed' && <Button className="text-button" disabled={!!pending || dirty} onClick={() => {
                      if (window.confirm('Archive this run only if TogetherServer can prove the exact recorded process is absent?'))
                        void run(profile.id, `/api/local/profiles/${profile.id}/forget`, 'POST')
                    }}>Archive exited record</Button>}</div>
                  {status?.state === 'Unknown' && <p className="warning-text">Process identity is uncertain. Start, Stop, archive, backup restore, and world reuse remain blocked; TogetherServer will not clear this record on PID reuse, executable mismatch, or access failure.</p>}
                  {['Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(profile.kind) && <div className="world-protection-summary"><strong>World protection</strong><p>Crash recovery is {profile.crashRecovery?.enabled ? 'on' : 'off'} · rolling backup after graceful Stop is {profile.backups?.enabled ? 'on' : 'off'}.</p>
                    {backupStatus?.lastSuccessfulUtc && <small>Last successful backup {new Date(backupStatus.lastSuccessfulUtc).toLocaleString()} · {backupStatus.completedCount} retained.</small>}
                    {backupStatus?.lastFailureUtc && <p className="warning-text">Last backup issue {new Date(backupStatus.lastFailureUtc).toLocaleString()}: {backupStatus.lastFailure}</p>}
                    <div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => void loadBackups(profile.id)}>{pending === `backups-${profile.id}` ? 'Loading backups…' : backupList ? 'Refresh backups' : 'Show backups'}</Button><Button className="text-button" disabled={!!pending || status?.state !== 'Offline'} onClick={() => openSetup(profile.id)}>Change protection settings</Button></div>
                    {backupList && <div className="backup-list">{backupList.backups.length === 0 ? <p className="helper-text">No completed backups yet. A backup is created only after a confirmed graceful Stop while rolling backups are enabled.</p> : backupList.backups.map(backup => <div className="device" key={backup.id}><div><strong>{backup.backupKind === 'PreRestore' ? 'Pre-restore snapshot' : 'Rolling backup'}</strong><small>{new Date(backup.createdUtc).toLocaleString()} · {backup.fileCount} files · {(backup.sizeBytes / 1048576).toFixed(1)} MB</small></div><Button className="secondary" disabled={!!pending || status?.state !== 'Offline'} onClick={() => void restoreBackup(profile.id, backup.id, backup.createdUtc)}>Restore</Button></div>)}</div>}
                  </div>}
                </details>
              </article>
            })}
          </div>
          {dirty && <p className="warning-text">Save your setup changes before starting or stopping a server.</p>}
          {companion?.devices.some(device => device.paired && !device.revoked) && <div className="access-strip"><span>Friend controls are <strong>{draft.remoteControlsEnabled ? 'on' : 'paused'}</strong> · {companion.devices.filter(device => device.paired && !device.revoked).length} paired PC{companion.devices.filter(device => device.paired && !device.revoked).length === 1 ? '' : 's'}</span>
            <Button className="secondary" disabled={!!pending || dirty} onClick={() => openHostSettings('access')}>Manage friend access</Button></div>}
        </section>
        </>}

        {savedProfiles.length === 0 && !showSetup && <section className="panel welcome-panel"><div className="section-heading"><div><h2>What would you like to do?</h2><p>You can host and join at the same time. Switching pages never stops a running server.</p></div></div>
          {draft.profiles.length > 0 && dirty ? <div className="welcome-choice"><div><strong>Continue server setup</strong><p>Your unfinished non-secret setup details are still here. Re-enter the game password before saving.</p></div><Button onClick={continueSetup}>Continue setup</Button></div> : <div className="welcome-grid">
            <Button className="welcome-choice" disabled={!!pending} onClick={addProfile}><span className="section-icon"><Icon name="server" /></span><span><strong>Host a server</strong><small>{appInstance?.freshWorldsOnly ? 'Create a fresh disposable staging world.' : 'Create a new world or use a server already on this PC.'}</small></span></Button>
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
          onMinecraftSetupModeChange={setMinecraftSetupModeFor}
          onBrowseMinecraft={(profile, target) => void browseMinecraft(profile, target)}
          onApplyMinecraftInstallation={applyMinecraftInstallation} onScanMinecraft={folder => void scanMinecraft(folder)}
          onInstallMinecraft={profile => void installMinecraft(profile)}
          onMinecraftTermsChange={setMinecraftTermsFor}
          onScanValheim={() => void scanValheim()} onBrowseServer={profile => void browseServer(profile)}
          onEditCustomScripts={editCustomScripts} onUpdateCustomPort={updateCustomPort}
          onAddCustomPort={addCustomPort} onRemoveCustomPort={removeCustomPort}
          onRemoveProfile={profile => void removeProfile(profile)} onSave={startAfterSave => void saveSetup(startAfterSave)} />}
        {savedProfiles.length > 0 && showHostSettings && <dialog ref={hostSettingsRef} className="panel modal-dialog host-settings-dialog" aria-labelledby="host-settings-title" onCancel={event => { event.preventDefault(); closeHostSettings() }}>
          <div className="modal-heading"><div><h2 id="host-settings-title">Friend access and settings</h2><p>Everyday permissions first. Network and game paths stay under Advanced.</p></div>
            <Button className="secondary" disabled={!!pending} onClick={closeHostSettings}>{dirty ? 'Cancel' : 'Close'}</Button></div>
          {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
          <nav className="settings-tabs" aria-label="Host settings sections">
            <Button aria-current={hostSettingsSection === 'access' ? 'page' : undefined} className={hostSettingsSection === 'access' ? 'selected' : ''} onClick={() => setHostSettingsSection('access')}>Friend access</Button>
            <Button aria-current={hostSettingsSection === 'stop' ? 'page' : undefined} className={hostSettingsSection === 'stop' ? 'selected' : ''} onClick={() => setHostSettingsSection('stop')}>Stop & timer</Button>
            <Button aria-current={hostSettingsSection === 'network' ? 'page' : undefined} className={hostSettingsSection === 'network' ? 'selected' : ''} onClick={() => setHostSettingsSection('network')}>Connection help</Button>
            <Button aria-current={hostSettingsSection === 'advanced' ? 'page' : undefined} className={hostSettingsSection === 'advanced' ? 'selected' : ''} onClick={() => setHostSettingsSection('advanced')}>Advanced</Button>
          </nav>
            <div className="settings-content">
              {hostSettingsSection === 'access' && <section className="settings-section"><h3>Friend access</h3>
                <p>Friend PCs can keep seeing status while controls are paused. Start and Stop requests are always checked again on this Host.</p>
                <div className="access-toggles"><label className="setting-toggle"><span><strong>Allow Friend app connections</strong><small>Needed for pairing, status, and remote requests.</small></span><Input type="checkbox" checked={draft.companionListeningEnabled} disabled={!!pending} onChange={event => void saveHostFlags({ companionListeningEnabled: event.target.checked })} /></label>
                  <label className="setting-toggle"><span><strong>Allow remote Start and Stop</strong><small>Individual PC permissions below still apply.</small></span><Input type="checkbox" checked={draft.remoteControlsEnabled} disabled={!!pending || !draft.companionListeningEnabled} onChange={event => void saveHostFlags({ remoteControlsEnabled: event.target.checked })} /></label></div>
                {companion?.devices.filter(device => !device.revoked).length ? <div className="device-list"><h3>Paired Friend PCs</h3><p className="helper-text">A new PC starts with only the server whose code it used. You can assign that PC to any combination of your saved servers.</p>{companion.devices.filter(device => !device.revoked).map(device => <div className="device access-device" key={device.id}>
                  <div className="device-header"><div className="device-main"><label>PC name<Input value={deviceNames[device.id] ?? device.name} maxLength={48} onChange={event => setDeviceNames(current => ({ ...current, [device.id]: event.target.value }))} /></label><small>{device.approvalPending ? 'Waiting for local approval' : device.lastHeartbeatUtc ? `Last report ${new Date(device.lastHeartbeatUtc).toLocaleTimeString()}` : device.paired ? 'No fresh report' : 'Waiting for this PC to connect'} · {device.profileId === '00000000-0000-0000-0000-000000000000' ? 'Paired with an older code' : `Paired with the code for ${savedProfiles.find(profile => profile.id === device.profileId)?.name ?? 'a removed server'}`}</small></div>
                    <div className="actions device-card-actions">{device.credentialExpiresUtc && <small>Credential expires {new Date(device.credentialExpiresUtc).toLocaleDateString()}</small>}{device.approvalPending && <Button disabled={!!pending} onClick={() => void approveDevice(device.id)}>Approve this PC</Button>}<Button className="secondary" disabled={!!pending || !(deviceNames[device.id] ?? device.name).trim() || (deviceNames[device.id] ?? device.name).trim() === device.name} onClick={() => void saveDeviceName(device.id)}>Save name</Button><Button className="text-button danger" disabled={!!pending} onClick={() => void revokeDevice(device.id)}>Revoke</Button></div></div>
                  <div className="device-access-grid"><div className="device-server-summary"><div className="device-summary-copy"><span>Server access</span><strong>{device.assignedProfileIds.length} {device.assignedProfileIds.length === 1 ? 'server' : 'servers'}</strong><small title={serverAssignmentPreview(device, savedProfiles)}>{serverAssignmentPreview(device, savedProfiles)}</small></div><Button className="secondary" disabled={!!pending || !device.paired || device.approvalPending} onClick={() => openDeviceServerAccess(device)}><Icon name="server" />Choose servers</Button></div>
                    <label className="device-permission-toggle"><MixedCheckbox type="checkbox" mixed={permissionMix(device, 'canStart').mixed} checked={permissionMix(device, 'canStart').all} disabled={!!pending || !device.paired || device.approvalPending} onChange={event => void setDevicePermissions(device, permissionMix(device, 'canStart').mixed ? true : event.target.checked, device.canStop, device.canExtendTimer, 'start')} /><span><strong>Start servers</strong><small>{permissionMix(device, 'canStart').mixed ? device.canStart ? 'On with server exceptions' : 'Off with server exceptions' : permissionMix(device, 'canStart').all ? 'Allowed on every assigned server' : 'Off on every assigned server'}</small></span></label>
                    <label className="device-permission-toggle"><MixedCheckbox type="checkbox" mixed={permissionMix(device, 'canStop').mixed} checked={permissionMix(device, 'canStop').all} disabled={!!pending || !device.paired || device.approvalPending} onChange={event => void setDevicePermissions(device, device.canStart, permissionMix(device, 'canStop').mixed ? true : event.target.checked, device.canExtendTimer, 'stop')} /><span><strong>Request Stop</strong><small>{permissionMix(device, 'canStop').mixed ? device.canStop ? 'On with server exceptions' : 'Off with server exceptions' : permissionMix(device, 'canStop').all ? 'Allowed on every assigned server' : 'Off on every assigned server'}</small></span></label>
                    <label className="device-permission-toggle"><MixedCheckbox type="checkbox" mixed={permissionMix(device, 'canExtendTimer').mixed} checked={permissionMix(device, 'canExtendTimer').all} disabled={!!pending || !device.paired || device.approvalPending} onChange={event => void setDevicePermissions(device, device.canStart, device.canStop, permissionMix(device, 'canExtendTimer').mixed ? true : event.target.checked, 'extend')} /><span><strong>Extend empty-server timer</strong><small>Off by default. Friends get only the fixed increment and maximum configured by the Host.</small></span></label></div>
                </div>)}</div> : <div className="empty compact-empty"><p>No Friend PCs are paired yet. Choose Invite friends on a server card to copy a private server code.</p></div>}
              </section>}
              {hostSettingsSection === 'network' && <section className="settings-section">
              <h3>Connection checks</h3>
              <div className="settings-grid companion-fields">
                <label>Friend route<Select value={draft.connectionRoute?.mode ?? 'DirectInternet'} onChange={event => changeRoute(event.target.value as Settings['connectionRoute']['mode'], event.target.value === 'DirectInternet' ? '' : draft.connectionRoute?.address ?? '')}>
                  <option value="DirectInternet">Direct Internet</option><option value="PrivateMesh">Private mesh</option><option value="AdvancedAddress">Advanced address</option>
                </Select><small>Mesh and advanced routes use networking you install and manage. TogetherServer still requires TLS pins, credentials, assignments, and permissions.</small></label>
                {(draft.connectionRoute?.mode ?? 'DirectInternet') !== 'DirectInternet' && <label>Selected route IPv4<Input value={draft.connectionRoute?.address ?? ''} onChange={event => changeRoute(draft.connectionRoute.mode, event.target.value)} placeholder="100.64.0.2" /><small>TogetherServer only reads adapters; it does not install clients or change network policy.</small></label>}
                {draft.connectionRoute?.mode === 'PrivateMesh' && <label>Detected private-network adapter<Select value="" onChange={event => event.target.value && changeRoute('PrivateMesh', event.target.value)}><option value="">Choose a detected address</option>{routeDiscovery?.privateMeshCandidates.map(candidate => <option key={`${candidate.interfaceName}-${candidate.address}`} value={candidate.address}>{candidate.provider} · {candidate.address} · {candidate.interfaceName}</option>)}</Select><small>{routeDiscovery?.privateMeshCandidates.length ? 'Selecting an address does not configure that network.' : 'No known Tailscale or ZeroTier adapter is currently up; enter an address manually if appropriate.'}</small></label>}
              </div>
              <p>Game address: {detectedGameIp ? `${detectedGameIp} detected, friend join untested` : 'unavailable'}. Friend app: {portDiagnostics?.control.remoteState === 'Friend connected' ? 'Friend connected' : currentRouteResult?.state === 'Reachable' ? 'reachable outside this network; Friend pairing untested' : companion?.listenerActive ? 'listening on this PC, outside route unconfirmed' : 'off'}.</p>
              {companion?.listenerWarning && <p className="warning-text">{companion.listenerWarning}</p>}
              {publicIpDetection && !publicIpDetection.ok && <p className="warning-text">{publicIpDetection.message}</p>}
              <div className="actions"><Button className="secondary" disabled={detectingPublicIp} onClick={() => void detectPublicIp()}>{detectingPublicIp ? <><Icon name="loader" />Refreshing…</> : <><Icon name="refresh" />Refresh public address</>}</Button></div>
              <div className={checkingInternetRoute ? 'internet-route-test refreshing' : 'internet-route-test'} aria-busy={checkingInternetRoute}><Button className="secondary" disabled={checkingInternetRoute || !!pending || dirty} onClick={() => void checkInternetRoute()}>{checkingInternetRoute ? <><Icon name="loader" />Testing TCP port…</> : 'Test Friend app port from internet'}</Button>
                <small>This checks the Friend app TCP port through portchecker.io. That service sees this PC's public IP and port; no invite or credential is sent.</small>
                {internetRouteCheck && <p className={`internet-route-result ${previousRouteVerdict ? 'neutral' : internetRouteCheck.state === 'Reachable' ? 'good' : internetRouteCheck.state === 'Not reachable' ? 'bad' : 'neutral'}`} role="status">
                  <strong>{previousRouteVerdict ? `Previous TCP ${internetRouteCheck.port} result` : internetRouteCheck.state === 'Reachable' ? `Reachable outside network · TCP ${internetRouteCheck.port}` : internetRouteCheck.state === 'Not reachable' ? `TCP ${internetRouteCheck.port} not reachable` : `${internetRouteCheck.state} · TCP ${internetRouteCheck.port}`}</strong>
                  <span>{internetRouteCheck.detail}</span><small>Checked {new Date(internetRouteCheck.checkedUtc).toLocaleString()}. {previousRouteVerdict && 'This result is no longer current for the saved listener, invite address, or time; test again after checking them. '}This tests TCP access only; your Friend still needs to pair, and the game join needs its own test.</small>
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
                <p>When a Ready server reports 0 players, TogetherServer counts down and stops it gracefully. Friend apps do not gate the timer. A player or unavailable server count cancels it, and a fresh zero-player server check is required again at the end.</p>
                <div className="idle-settings"><label className="setting-toggle"><span><strong>Stop empty servers automatically</strong><small>Off by default. Host and Friend cards share the countdown or explain why it is paused.</small></span><Input type="checkbox" checked={draft.autoShutdownEnabled} disabled={!!pending} onChange={event => void saveHostFlags({ autoShutdownEnabled: event.target.checked })} /></label>
                  <label>Wait after the server reaches 0 players<Input type="number" min="1" max="1440" value={draft.idleMinutes} disabled={!!pending} onChange={event => edit({ ...draft, idleMinutes: Number(event.target.value) })} /><small>Minutes, from 1 to 1440.</small></label>
                  <label>Friend extension increment<Input type="number" min="1" max="120" value={draft.friendTimerExtensionMinutes} disabled={!!pending} onChange={event => edit({ ...draft, friendTimerExtensionMinutes: Number(event.target.value) })} /><small>Fixed minutes added per approved Friend request.</small></label>
                  <label>Friend extension maximum<Input type="number" min="1" max="1440" value={draft.friendTimerExtensionMaximumMinutes} disabled={!!pending} onChange={event => edit({ ...draft, friendTimerExtensionMaximumMinutes: Number(event.target.value) })} /><small>Total Friend-added minutes allowed during one countdown.</small></label>
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

              {hostSettingsSection === 'advanced' && <section className="settings-section"><h3>Advanced network and game paths</h3>
              <div className="settings-grid companion-fields">
                <label>Maximum servers running at once<Input type="number" min="1" max="16" value={draft.maxConcurrentServers} onChange={event => edit({ ...draft, maxConcurrentServers: Number(event.target.value) })} /><small>Most homes should leave this at 1.</small></label>
                <label>Friend app TCP port<Input type="number" min="1024" max="65535" value={draft.companionPort} onChange={event => {
                  const port = Number(event.target.value)
                  let endpoint = draft.companionEndpoint
                  if (endpoint) try { const url = new URL(endpoint); url.port = String(port); endpoint = url.origin } catch { /* Validation explains a custom endpoint. */ }
                  edit({ ...draft, companionPort: port, companionEndpoint: endpoint })
                }} /></label>
                <label>Custom HTTPS endpoint<Input value={draft.companionEndpoint} onChange={event => edit({ ...draft, companionEndpoint: event.target.value.trim() })} placeholder={`https://127.0.0.1:${appInstance?.companionPort ?? 5131}`} /></label>
                <label>Bind IP<Input value={draft.companionBindAddress} onChange={event => edit({ ...draft, companionBindAddress: event.target.value })} placeholder="127.0.0.1" /></label>
              </div>
              {companion?.fingerprint && <p className="footnote">Pinned Host identity: <code>{companion.fingerprint}</code></p>}
              {companion?.certificates && <div className="safety-status"><strong>Host certificate</strong><p>Active until {new Date(companion.certificates.activeExpiresUtc).toLocaleString()}.</p>
                {companion.certificates.nextFingerprint ? <p>Next certificate is staged and is being announced to authenticated Friends.</p> : <p>No next certificate is staged yet. TogetherServer stages one automatically within 30 days of expiry.</p>}
                {companion.certificates.previousAcceptedUntilUtc && <p>Previous pin grace ends {new Date(companion.certificates.previousAcceptedUntilUtc).toLocaleString()}.</p>}
                <div className="actions"><Button className="secondary" disabled={!!pending || !!companion.certificates.nextFingerprint} onClick={() => void certificateAction('stage')}>Stage next certificate</Button>
                  <Button className="secondary" disabled={!!pending || !companion.certificates.nextFingerprint} onClick={() => void certificateAction('activate')}>Activate staged certificate</Button>
                  {companion.certificates.previousFingerprint && <Button className="text-button danger" disabled={!!pending} onClick={() => void certificateAction('retire-previous')}>Retire previous pin</Button>}</div>
              </div>}
              <div className="actions"><Button disabled={!dirty || !!pending} onClick={() => void run('save', '/api/local/settings', 'PUT', draft)}>Save settings</Button></div>
              {companion?.devices.some(device => device.revoked) ? <details className="advanced-block"><summary>Revoked Friend PCs</summary><div className="profile-list device-list">{companion.devices.filter(device => device.revoked).map(device => <div className="device" key={device.id}>
                <div><strong>{device.name} · {savedProfiles.find(profile => profile.id === device.profileId)?.name ?? 'Legacy access'}</strong><small>Revoked</small></div>
              </div>)}</div></details> : null}
              </section>}
            </div>
        </dialog>}
        {savedProfiles.length > 0 && serverAccessDevice && <dialog ref={serverAccessRef} className="panel modal-dialog server-access-dialog" aria-labelledby="server-access-title" onCancel={event => { event.preventDefault(); closeDeviceServerAccess() }}>
          <div className="modal-heading"><div><h2 id="server-access-title">Choose servers for {serverAccessDevice.name}</h2><p>Selected servers expose their connection details. Start and Stop can be allowed separately for each one.</p></div><Button className="secondary" disabled={!!pending} onClick={closeDeviceServerAccess}>Cancel</Button></div>
          {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
          <label className="server-picker-search">Search servers<Input value={serverAccessSearch} autoFocus placeholder="Search by server or game" onChange={event => setServerAccessSearch(event.target.value)} /></label>
          <div className="server-picker-toolbar"><strong>{serverAccessDraft.length} of {savedProfiles.length} selected</strong><div className="actions"><Button className="text-button" disabled={!!pending || visibleServerAccessProfiles.length === 0} onClick={() => setServerAccessDraft(current => [...new Set([...current, ...visibleServerAccessProfiles.map(profile => profile.id)])])}>{normalizedServerSearch ? 'Select all results' : 'Select all'}</Button><Button className="text-button" disabled={!!pending || serverAccessDraft.length === 0} onClick={() => setServerAccessDraft([])}>Clear all</Button></div></div>
          <div className="server-picker-list" role="group" aria-label="Saved servers">{visibleServerAccessProfiles.map(profile => {
            const assigned = serverAccessDraft.includes(profile.id)
            const permission = serverPermissionDraft[profile.id] ?? { canStart: serverAccessDevice.canStart, canStop: serverAccessDevice.canStop, canExtendTimer: serverAccessDevice.canExtendTimer }
            return <div className="server-picker-option" key={profile.id}><label className="server-picker-access"><Input type="checkbox" checked={assigned} disabled={!!pending} onChange={event => setServerAccessDraft(current => event.target.checked ? [...new Set([...current, profile.id])] : current.filter(id => id !== profile.id))} /><span><strong>{profile.name}</strong><small>{profileGameLabel(profile)}</small></span></label>
              <div className="server-picker-permissions" aria-label={`${profile.name} permissions`}><label><Input type="checkbox" checked={permission.canStart} disabled={!!pending || !assigned} onChange={event => setServerPermissionDraft(current => ({ ...current, [profile.id]: { ...permission, canStart: event.target.checked } }))} /> Start</label><label><Input type="checkbox" checked={permission.canStop} disabled={!!pending || !assigned} onChange={event => setServerPermissionDraft(current => ({ ...current, [profile.id]: { ...permission, canStop: event.target.checked } }))} /> Stop</label><label><Input type="checkbox" checked={permission.canExtendTimer} disabled={!!pending || !assigned} onChange={event => setServerPermissionDraft(current => ({ ...current, [profile.id]: { ...permission, canExtendTimer: event.target.checked } }))} /> Extend timer</label></div></div>
          })}
            {visibleServerAccessProfiles.length === 0 && <div className="server-picker-empty">No servers match “{serverAccessSearch.trim()}”.</div>}</div>
          <div className="server-picker-footer"><span>Changes apply when you save.</span><div className="actions"><Button className="secondary" disabled={!!pending} onClick={closeDeviceServerAccess}>Cancel</Button><Button disabled={!!pending} onClick={() => void saveDeviceServerAccess()}>{pending === serverAccessDevice.id ? 'Saving…' : 'Save access'}</Button></div></div>
        </dialog>}
      </>}
    </main>
  </div>
}

createRoot(document.getElementById('root')!).render(
  <React.StrictMode><AppErrorBoundary><App /></AppErrorBoundary></React.StrictMode>)

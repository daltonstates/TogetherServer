import React, { useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { Button, Input, Select, TextArea } from './Controls'
import { ConnectionDetails } from './ConnectionDetails'
import { Icon } from './Icon'
import { ServerReadiness, currentOutsideResult, type PortDiagnostics, type InternetRouteCheck } from './ServerReadiness'
import { gameLabel, type Profile } from './GameProfile'
import { MinecraftWorldSetup, MinecraftServerSetup, minecraftSetupIssues, type MinecraftDiscovery, type MinecraftInstallation } from './MinecraftSetup'
import './style.css'
import './companion.css'

type Settings = {
  maxConcurrentServers: number
  idleMinutes: number
  autoShutdownEnabled: boolean
  remoteControlsEnabled: boolean
  companionListeningEnabled: boolean
  companionBindAddress: string
  companionEndpoint: string
  companionPort: number
  publicGameIp: string
  publicGameIpCheckedUtc: string | null
  profiles: Profile[]
}
type Run = { profileId: string; state: string; detail: string; processId: number | null; onlinePlayers: number | null; maxPlayers: number | null; autoShutdownAtUtc: string | null; autoShutdownReason: string | null }
type HostSnapshot = { mode: 'Host'; evidence: string; settings: Settings; runs: Run[]; passwordConfigured: Record<string, boolean>; managedWorldsRoot: string }
type PublicProfile = { id: string; name: string; state: string; joinAddress: string | null; canStopNow: boolean; stopReason: string | null; kind: string; onlinePlayers: number | null; maxPlayers: number | null; autoShutdownAtUtc: string | null; autoShutdownReason: string | null }
type Device = { id: string; profileId: string; assignedProfileIds: string[]; name: string; canStart: boolean; canStop: boolean; revoked: boolean; paired: boolean; lastHeartbeatUtc: string | null }
type CompanionInfo = { listenerActive: boolean; listenerWarning: string | null; endpoint: string; fingerprint: string | null; devices: Device[]; stopSafety: Record<string, { available: boolean; reason: string }> }
type FriendSnapshot = { mode: 'Friend'; state: string; detail: string; endpoint: string; lastConnectedUtc: string | null; remoteControlsEnabled: boolean; canStart: boolean; canStop: boolean; profiles: PublicProfile[]; connectionId: string; connections: FriendSnapshot[] | null; connectionCode?: string | null }
type Snapshot = HostSnapshot | FriendSnapshot
type BasicResult = { ok: boolean; code: string; message: string }
type ActionResult = { ok: boolean; code: string; message: string; snapshot: HostSnapshot }
type PublicIpDetection = BasicResult & { address: string | null; snapshot?: HostSnapshot }
type Discovery = { installations: { executablePath: string; source: string }[]; worlds: { name: string; saveRoot: string; sourceFolder: string; format: string }[] }
type ImportResult = BasicResult & { worldDirectory: string | null }
type ServerBrowseResult = BasicResult & { executablePath?: string }
type MinecraftBrowseResult = BasicResult & { path?: string }
type MinecraftInstallResult = BasicResult & { installation?: MinecraftInstallation }
type WorldBrowseResult = BasicResult & { worldId: string | null; sourceSaveRoot: string | null; sourceFolder: string }
type UpdateView = { state: 'Checking' | 'Current' | 'Available' | 'NoRelease' | 'Unavailable' | 'Unsupported'; currentVersion: string; latestVersion: string | null; message: string }
type DesktopPreferences = { available: boolean; launchAtLogin: boolean; closeToTray: boolean; startupAvailable: boolean }
type DesktopPreferenceResult = BasicResult & { preferences: DesktopPreferences }
type FriendIssue = { code: string; message: string }
type SetupStep = 'game' | 'world' | 'server' | 'review'
type HostSettingsSection = 'access' | 'stop' | 'network' | 'advanced'
type BrowseResult = BasicResult & { path?: string | null }
type ConnectionActivity = Record<string, 'copy' | 'reveal'>

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
  const stopVisible = profile.state === 'Ready' && snapshot.state === 'Connected' && snapshot.canStop && profile.canStopNow
  if (!authenticated || stopVisible) return null
  const blockers: string[] = []
  if (!snapshot.remoteControlsEnabled) blockers.push('The Host has paused remote Start and Stop.')
  if (!snapshot.canStop) blockers.push('Ask the Host to open Friend access and allow Stop requests for this PC.')
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

function ServerActivity({ state, online, capacity, deadline, timerReason, nowMs }: {
  state: string; online: number | null; capacity: number | null; deadline: string | null; timerReason: string | null; nowMs: number
}) {
  if (state !== 'Ready') return null
  const countdown = online === 0 ? countdownLabel(deadline, nowMs) : null
  return <div className="server-activity-wrap"><div className="server-activity">
      <span className={`player-count ${online === null ? 'unknown' : ''}`}>{playerCount(online, capacity)}</span>
      {countdown && <span className="idle-countdown" role="timer" title="No players are online and automatic shutdown is on.">{countdown}</span>}
    </div>
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

const localHeaders = { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' }
const setupDraftKey = 'togetherserver-first-server-draft-v1'

async function readSnapshot(): Promise<Snapshot> {
  const response = await fetch('/api/local/snapshot', { cache: 'no-store' })
  if (!response.ok) throw new Error(`Local app returned ${response.status}`)
  return response.json()
}

async function change<T extends BasicResult>(path: string, method: 'POST' | 'PUT', body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: localHeaders,
    body: body === undefined ? undefined : JSON.stringify(body)
  })
  if (!response.ok) throw new Error(`Local app returned ${response.status}`)
  return response.json()
}

function getSetupIssues(profile: Profile, hasPassword: boolean, enteredPassword: string): string[] {
  const issues: string[] = []
  if (profile.kind === 'Valheim') {
    if (!profile.serverName.trim()) issues.push('Name your world.')
    if (profile.worldSource === 'Existing' && (!profile.worldId || !profile.worldDirectory))
      issues.push('Choose an existing world to copy.')
    if (profile.worldSource === 'New') {
      if (!profile.worldId.trim()) issues.push('Name your new world.')
      if (!profile.worldDirectory.trim()) issues.push('Choose where the new world will be saved.')
    }
    if (!profile.executablePath.trim()) issues.push('Select your installed Valheim Dedicated Server.')
    if (!hasPassword && !enteredPassword) issues.push('Enter a game password.')
    if (enteredPassword && (enteredPassword.length < 5 || enteredPassword.length > 64 || /[\x00-\x1f\x7f]/.test(enteredPassword)))
      issues.push('Use a game password of 5 to 64 characters.')
  } else if (profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') {
    issues.push(...minecraftSetupIssues(profile))
  } else {
    if (!profile.name.trim()) issues.push('Enter a test profile name in step 1.')
    if (!profile.worldId.trim()) issues.push('Enter a world ID in step 1.')
    if (!profile.worldDirectory.trim()) issues.push('Choose a disposable directory in step 1.')
    if (!profile.executablePath.trim()) issues.push('Select the fixture executable in step 2.')
  }
  return issues
}

function getStepIssues(step: SetupStep, profile: Profile, hasPassword: boolean, enteredPassword: string): string[] {
  if (step === 'game') return []
  if (step === 'review') return getSetupIssues(profile, hasPassword, enteredPassword)
  if (step === 'world') {
    if (profile.kind === 'Valheim') {
      if (profile.worldSource === 'Existing' && (!profile.worldId || !profile.worldDirectory)) return ['Choose a world to copy.']
      if (profile.worldSource === 'New' && !profile.worldId.trim()) return ['Name your new world.']
      if (!hasPassword && !enteredPassword) return ['Enter the password friends will use in Valheim.']
      if (enteredPassword && (enteredPassword.length < 5 || enteredPassword.length > 64 || /[\x00-\x1f\x7f]/.test(enteredPassword)))
        return ['Use a game password of 5 to 64 characters.']
    }
    if ((profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') && !profile.name.trim())
      return ['Name this server.']
    return []
  }
  if (profile.kind === 'Valheim' && !profile.executablePath.trim()) return ['Choose the installed Valheim Dedicated Server.']
  if ((profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') && !profile.executablePath.trim())
    return ['Choose or install the game server.']
  if (profile.kind === 'Fixture' && !profile.executablePath.trim()) return ['Choose the fixture executable.']
  return []
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
  const [nowMs, setNowMs] = useState(() => Date.now())
  const [draft, setDraft] = useState<Settings | null>(null)
  const [dirty, setDirty] = useState(false)
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
  const [detectingPublicIp, setDetectingPublicIp] = useState(false)
  const [inviteProfileId, setInviteProfileId] = useState('')
  const [inviteListenerWarning, setInviteListenerWarning] = useState<string | null>(null)
  const [deviceNames, setDeviceNames] = useState<Record<string, string>>({})
  const [invitation, setInvitation] = useState('')
  const [friendInvite, setFriendInvite] = useState('')
  const [friendHostAddress, setFriendHostAddress] = useState('')
  const [pairIssue, setPairIssue] = useState<FriendIssue | null>(null)
  const [showPairing, setShowPairing] = useState(false)
  const [countdownExtensions, setCountdownExtensions] = useState<Record<string, string>>({})
  const [passwords, setPasswords] = useState<Record<string, string>>({})
  const [discovery, setDiscovery] = useState<Discovery | null>(null)
  const [minecraftDiscovery, setMinecraftDiscovery] = useState<MinecraftDiscovery | null>(null)
  const [minecraftTerms, setMinecraftTerms] = useState<Record<string, boolean>>({})
  const [sourceRoots, setSourceRoots] = useState<Record<string, string>>({})
  const [showSetup, setShowSetup] = useState(false)
  const [showHostSettings, setShowHostSettings] = useState(false)
  const [serverAccessDeviceId, setServerAccessDeviceId] = useState('')
  const [serverAccessDraft, setServerAccessDraft] = useState<string[]>([])
  const [serverAccessSearch, setServerAccessSearch] = useState('')
  const [setupStep, setSetupStep] = useState<SetupStep>('game')
  const [hostSettingsSection, setHostSettingsSection] = useState<HostSettingsSection>('access')
  const [minecraftSetupMode, setMinecraftSetupMode] = useState<Record<string, 'existing' | 'install'>>({})
  const [showPasswords, setShowPasswords] = useState<Record<string, boolean>>({})
  const [revealedConnections, setRevealedConnections] = useState<Record<string, boolean>>({})
  const [revealedGamePasswords, setRevealedGamePasswords] = useState<Record<string, string>>({})
  const [connectionActivity, setConnectionActivity] = useState<ConnectionActivity>({})
  const [activeProfileId, setActiveProfileId] = useState('')
  const initialDraftSet = useRef(false)
  const inviteLoad = useRef(0)
  const dirtyRef = useRef(false)
  const liveConnectionKeysRef = useRef<Set<string>>(new Set())
  const connectionRevealRequestRef = useRef<Record<string, number>>({})
  const setupRef = useModalDialog(showSetup)
  const hostSettingsRef = useModalDialog(showHostSettings)
  const serverAccessRef = useModalDialog(!!serverAccessDeviceId)

  useEffect(() => {
    const timer = window.setInterval(() => setNowMs(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])

  useEffect(() => {
    if (notice || update?.state === 'Available') setNotificationUnread(true)
  }, [notice, update?.state, update?.latestVersion])

  useEffect(() => {
    let alive = true
    void fetch('/api/local/desktop/preferences', { cache: 'no-store' })
      .then(response => response.ok ? response.json() as Promise<DesktopPreferences> : null)
      .then(result => { if (alive && result) setDesktopPreferences(result) })
      .catch(() => { /* The server and Friend controls remain usable. */ })
    return () => { alive = false }
  }, [])

  const saveDesktopPreference = async (preference: { launchAtLogin: boolean } | { closeToTray: boolean }) => {
    setDesktopBusy(true)
    try {
      const result = await change<DesktopPreferenceResult>('/api/local/desktop/preferences', 'PUT', preference)
      setDesktopPreferences(result.preferences)
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setDesktopBusy(false) }
  }

  const quitApp = async () => {
    try {
      const result = await change<BasicResult>('/api/local/quit', 'POST')
      if (!result.ok) setNotice({ good: false, text: result.message })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
  }

  useEffect(() => {
    let alive = true
    const check = async () => {
      try {
        const response = await fetch('/api/local/update', { cache: 'no-store' })
        if (response.ok && alive) setUpdate(await response.json())
      } catch { /* An update check must not interrupt hosting or joining. */ }
    }
    void check()
    const timer = window.setInterval(() => void check(), 30 * 60 * 1000)
    return () => { alive = false; window.clearInterval(timer) }
  }, [])

  const checkUpdate = async () => {
    setUpdateBusy(true)
    try {
      const response = await fetch('/api/local/update/check', { method: 'POST', headers: localHeaders })
      if (!response.ok) throw new Error(`Update check returned ${response.status}`)
      const result: UpdateView = await response.json()
      setUpdate(result)
      if (result.state !== 'Available') setNotice({ good: result.state === 'Current' || result.state === 'NoRelease', text: result.message })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setUpdateBusy(false) }
  }

  const installUpdate = async () => {
    setUpdateBusy(true)
    setNotice(null)
    try {
      const result = await change<BasicResult>('/api/local/update/install', 'POST')
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setUpdateBusy(false) }
  }

  useEffect(() => {
    let alive = true
    const refresh = async () => {
      try {
        const next = await readSnapshot()
        if (!alive) return
        setSnapshot(next)
        setLoadError('')
        if (next.mode === 'Host') {
          if (!initialDraftSet.current) {
            initialDraftSet.current = true
            let restored: Settings | null = null
            if (next.settings.profiles.length === 0) try {
              const saved = window.localStorage.getItem(setupDraftKey)
              if (saved) restored = JSON.parse(saved) as Settings
            } catch { window.localStorage.removeItem(setupDraftKey) }
            if (restored?.profiles?.length) {
              setDraft(restored)
              setActiveProfileId(restored.profiles[0].id)
              dirtyRef.current = true
              setDirty(true)
            } else setDraft(next.settings)
          } else if (!dirtyRef.current) setDraft(next.settings)
          const response = await fetch('/api/local/companion', { cache: 'no-store' })
          if (response.ok && alive) {
            const current: CompanionInfo = await response.json()
            setCompanion(current)
            setDeviceNames(names => {
              const next = { ...names }
              for (const device of current.devices) if (next[device.id] === undefined) next[device.id] = device.name
              return next
            })
          }
          const ports = await fetch('/api/local/network/ports', { cache: 'no-store' })
          if (ports.ok && alive) setPortDiagnostics(await ports.json())
        }
      } catch (error) {
        if (alive) setLoadError(String(error))
      }
    }
    void refresh()
    const timer = window.setInterval(refresh, 3000)
    return () => { alive = false; window.clearInterval(timer) }
  }, [])

  useEffect(() => {
    if (snapshot?.mode !== 'Host' || !showSetup || !draft || !discovery) return
    const serverPath = discovery.installations.length === 1 ? discovery.installations[0].executablePath : ''
    const profiles = draft.profiles.map(profile => profile.kind === 'Valheim' && !profile.executablePath && serverPath
      ? { ...profile, executablePath: serverPath } : profile)
    if (profiles.some((profile, index) => profile !== draft.profiles[index])) {
      setDraft({ ...draft, profiles })
      dirtyRef.current = true
      setDirty(true)
    }
  }, [snapshot?.mode, showSetup, discovery, draft])

  useEffect(() => {
    if (snapshot?.mode !== 'Host' || !draft?.profiles.some(profile => profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') || minecraftDiscovery) return
    let alive = true
    void fetch('/api/local/minecraft/discover', { cache: 'no-store' })
      .then(response => response.ok ? response.json() as Promise<MinecraftDiscovery> : null)
      .then(result => { if (alive && result) setMinecraftDiscovery(result) })
      .catch(() => { /* Manual browsing and installation remain available. */ })
    return () => { alive = false }
  }, [snapshot?.mode, draft?.profiles, minecraftDiscovery])

  useEffect(() => {
    if (snapshot?.mode !== 'Host' || !showSetup || !draft || !minecraftDiscovery) return
    const profiles = draft.profiles.map(profile => {
      if (profile.kind !== 'MinecraftJava' && profile.kind !== 'MinecraftBedrock') return profile
      if (profile.worldDirectory || profile.minecraft?.serverJarPath || profile.executablePath) return profile
      const found = minecraftDiscovery.installations.filter(item => item.kind === profile.kind)
      return found.length === 1 ? minecraftProfile(profile, found[0]) : profile
    })
    if (profiles.some((profile, index) => profile !== draft.profiles[index])) {
      setDraft({ ...draft, profiles })
      dirtyRef.current = true
      setDirty(true)
    }
  }, [snapshot?.mode, showSetup, draft, minecraftDiscovery])

  useEffect(() => {
    if (snapshot?.mode === 'Friend' && snapshot.endpoint && !friendHostAddress)
      setFriendHostAddress(hostAddress(snapshot.endpoint))
  }, [snapshot?.mode, snapshot?.mode === 'Friend' ? snapshot.endpoint : ''])

  useEffect(() => {
    if (snapshot?.mode !== 'Host' || snapshot.settings.profiles.length !== 0 || !dirty || !draft) return
    try { window.localStorage.setItem(setupDraftKey, JSON.stringify(draft)) }
    catch { /* Setup remains usable even when browser storage is unavailable. */ }
  }, [snapshot?.mode, snapshot?.mode === 'Host' ? snapshot.settings.profiles.length : -1, dirty, draft])

  const edit = (next: Settings) => {
    setDraft(next)
    dirtyRef.current = true
    setDirty(true)
  }
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
    const response = await fetch(`/api/local/profiles/${profile.id}/game-password/reveal`, { method: 'POST', headers: localHeaders })
    const result: BasicResult & { password?: string } = await response.json()
    if (!response.ok || !result.ok || !result.password)
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
  const copyGameDetails = async (profile: Profile, address: string, key: string) => {
    setConnectionBusy(key, 'copy')
    try {
      const password = await readGamePassword(profile)
      await copyText(`Valheim Join IP: ${address}\nGame password: ${password}`, 'Game details', 'Reveal the details, then select and copy them instead.')
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setConnectionBusy(key, null) }
  }
  const revealHostConnectionDetails = async (profile: Profile, key: string) => {
    if (profile.kind !== 'Valheim') { revealConnectionDetails(key); return }
    const request = (connectionRevealRequestRef.current[key] ?? 0) + 1
    connectionRevealRequestRef.current[key] = request
    setConnectionBusy(key, 'reveal')
    try {
      const password = await readGamePassword(profile)
      if (connectionRevealRequestRef.current[key] !== request || !liveConnectionKeysRef.current.has(key) ||
        document.hidden || !document.hasFocus()) return
      setRevealedGamePasswords(current => ({ ...current, [key]: password }))
      revealConnectionDetails(key)
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setConnectionBusy(key, null) }
  }
  const copyConnectionValue = async (key: string, value: string, label: string) => {
    setConnectionBusy(key, 'copy')
    try { await copyText(value, label, 'Reveal the details, then select and copy them instead.') }
    finally { setConnectionBusy(key, null) }
  }
  const detectPublicIp = async () => {
    setDetectingPublicIp(true)
    try {
      const result = await change<PublicIpDetection>('/api/local/network/detect-public-ip', 'POST')
      setPublicIpDetection(result)
      if (result.ok && result.address && result.snapshot) {
        setSnapshot(current => current?.mode === 'Host' ? result.snapshot! : current)
        setDraft(current => current ? { ...current, publicGameIp: result.address!,
          publicGameIpCheckedUtc: result.snapshot!.settings.publicGameIpCheckedUtc } : current)
      }
    } catch {
      setPublicIpDetection({ ok: false, code: 'PublicIpUnavailable', address: null,
        message: 'Could not check the public IPv4 address. Check this PC’s Internet connection and retry.' })
    } finally { setDetectingPublicIp(false) }
  }
  useEffect(() => {
    if (snapshot?.mode !== 'Host') return
    void detectPublicIp()
    const timer = window.setInterval(() => void detectPublicIp(), 15 * 60 * 1000)
    return () => window.clearInterval(timer)
  }, [snapshot?.mode])
  const checkPorts = async (announce = false) => {
    if (announce) setCheckingPorts(true)
    try {
      const response = await fetch('/api/local/network/ports', { cache: 'no-store' })
      if (!response.ok) throw new Error(`Connection check returned ${response.status}`)
      setPortDiagnostics(await response.json())
      if (announce) setNotice({ good: true, text: 'Connection details updated.' })
    } catch (error) {
      if (announce) setNotice({ good: false, text: `Could not refresh connection details: ${String(error)}` })
      /* The regular refresh will retry without replacing the current evidence. */
    } finally {
      if (announce) setCheckingPorts(false)
    }
  }
  const checkInternetRoute = async () => {
    setCheckingInternetRoute(true)
    try {
      const response = await fetch('/api/local/network/test-friend-route', { method: 'POST', headers: localHeaders })
      const result: InternetRouteCheck = await response.json()
      setInternetRouteCheck(result)
    } catch (error) {
      setInternetRouteCheck({ state: 'Unavailable', detail: `Internet TCP test failed: ${String(error)}`,
        port: draft?.companionPort ?? 0, checkedUtc: new Date().toISOString() })
    } finally { setCheckingInternetRoute(false) }
  }
  const issueInvite = async (profileId: string, refresh = false): Promise<string | null> => {
    if (refresh && !window.confirm('Refresh this server code? Every Friend PC that connected with this code will lose its credential and need to connect again. Any extra servers assigned to those credentials will also be removed.')) return null
    setPending('invite')
    setNotice(null)
    try {
      let canStart = true
      if (refresh) {
        const currentResponse = await fetch(`/api/local/servers/${profileId}/invite/current`, { method: 'POST', headers: localHeaders })
        if (!currentResponse.ok) throw new Error('Could not read this server code before refreshing access.')
        const current: { canStart: boolean } = await currentResponse.json()
        if (typeof current.canStart !== 'boolean') throw new Error('Could not confirm this invite’s Start permission before refreshing access.')
        canStart = current.canStart
      }
      const response = await fetch(`/api/local/servers/${profileId}/invite`, { method: 'POST', headers: localHeaders,
        body: JSON.stringify({ refresh, canStart, enableConnections: true }) })
      const result: { ok: boolean; code: string; message: string; password?: string; listenerActive?: boolean; listenerWarning?: string } = await response.json()
      const listenerWarning = result.ok && result.listenerActive !== true
        ? result.listenerWarning || `The HTTPS listener on TCP ${draft?.companionPort ?? 'the configured port'} did not start. Check Connection help before sharing this code.`
        : null
      setInviteListenerWarning(listenerWarning || (!result.ok ? result.message : null))
      setNotice({ good: result.ok && !listenerWarning, text: listenerWarning || result.message })
      if (result.ok && result.password) {
        setInvitation(result.password)
        const latest = await fetch('/api/local/companion')
        if (latest.ok) setCompanion(await latest.json())
        const host = await readSnapshot()
        if (host.mode === 'Host') {
          setSnapshot(host)
          if (!dirty) setDraft(host.settings)
        }
        await checkPorts()
        return listenerWarning ? null : result.password
      }
      return null
    } catch (error) { setInviteListenerWarning(String(error)); setNotice({ good: false, text: String(error) }); return null }
    finally { setPending('') }
  }
  const revokeDevice = async (id: string) => {
    if (!window.confirm('Revoke this Friend device now? Its next request will be denied.')) return
    setPending(id)
    try {
      const response = await fetch(`/api/local/devices/${id}/revoke`, { method: 'POST', headers: localHeaders })
      const result: { ok: boolean; code: string; message: string } = await response.json()
      setNotice({ good: result.ok, text: result.message })
      const latest = await fetch('/api/local/companion')
      if (latest.ok) setCompanion(await latest.json())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const saveDeviceName = async (id: string) => {
    setPending(id)
    try {
      const name = (deviceNames[id] ?? '').trim()
      const result = await change<BasicResult>(`/api/local/devices/${id}/name`, 'PUT', { name })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) setDeviceNames(current => ({ ...current, [id]: name }))
      const latest = await fetch('/api/local/companion')
      if (latest.ok) setCompanion(await latest.json())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const setDevicePermissions = async (device: Device, canStart: boolean, canStop: boolean) => {
    setPending(device.id)
    try {
      const result = await change<BasicResult>(`/api/local/devices/${device.id}/permissions`, 'PUT',
        { canStart, canStop })
      setNotice({ good: result.ok, text: result.message })
      const latest = await fetch('/api/local/companion')
      if (latest.ok) setCompanion(await latest.json())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const saveHostFlags = async (patch: Partial<Settings>) => {
    if (!draft) return
    setPending('host-flags')
    setNotice(null)
    try {
      const next = { ...draft, ...patch }
      const result = await change<ActionResult>('/api/local/settings', 'PUT', next)
      setSnapshot(result.snapshot)
      setDraft(result.snapshot.settings)
      dirtyRef.current = false
      setDirty(false)
      setNotice({ good: result.ok, text: result.message })
      const latest = await fetch('/api/local/companion')
      if (latest.ok) setCompanion(await latest.json())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
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
      const response = await fetch('/api/local/friend/pair', { method: 'POST', headers: localHeaders,
        body: JSON.stringify({ invitation: friendInvite, hostAddress: friendHostAddress || null }) })
      const result: BasicResult = await response.json()
      if (!response.ok || !result.ok) {
        const message = result.message || `Local app returned ${response.status} while connecting.`
        setPairIssue({ code: result.code || 'Disconnected', message })
        return
      }
      setNotice({ good: true, text: result.message })
      if (result.ok) {
        setFriendInvite('')
        setShowPairing(false)
        await fetch('/api/local/friend/poll', { method: 'POST', headers: localHeaders })
        setSnapshot(await readSnapshot())
      }
    } catch (error) { setPairIssue({ code: 'LocalAppUnavailable', message: `Could not finish connecting to this app: ${String(error)}` }) }
    finally { setPending('') }
  }
  const checkFriendConnection = async () => {
    setPending('poll')
    try {
      const response = await fetch('/api/local/friend/poll', { method: 'POST', headers: localHeaders })
      if (!response.ok) throw new Error(`Local app returned ${response.status}`)
      const next: FriendSnapshot = await response.json()
      setSnapshot(next)
      setNotice({ good: next.state === 'Connected' || next.state === 'Disabled', text: next.detail })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const selectFriendConnection = async (id: string) => {
    setPending('select-connection')
    try {
      const result = await change<BasicResult>(`/api/local/friend/connections/${id}/select`, 'POST')
      if (!result.ok) setNotice({ good: false, text: result.message })
      else {
        setShowPairing(false)
        setSnapshot(await readSnapshot())
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const friendAction = async (id: string, action: 'start' | 'stop') => {
    const key = `friend-${action}-${id}`
    setPending(key)
    if (action === 'stop') hideConnectionDetails(`friend-${snapshot?.mode === 'Friend' ? snapshot.connectionId : ''}-${id}`)
    try {
      const result = await change<BasicResult>(`/api/local/friend/${id}/${action}`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      setSnapshot(await readSnapshot())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const saveSetup = async (startAfterSave = false) => {
    if (!draft) return
    const profile = draft.profiles.find(item => item.id === activeProfileId) ?? draft.profiles[0]
    if (!profile) return
    const unmet = getSetupIssues(profile,
      snapshot?.mode === 'Host' && !!snapshot.passwordConfigured[profile.id], passwords[profile.id] ?? '')
      .map(message => ({ id: profile.id, message }))
    if (unmet.length) {
      setActiveProfileId(unmet[0].id)
      setSetupStep('review')
      setNotice({ good: false, text: unmet[0].message })
      return
    }
    const password = profile ? passwords[profile.id] ?? '' : ''
    if (password && (password.length < 5 || password.length > 64 || /[\x00-\x1f\x7f]/.test(password))) {
      setNotice({ good: false, text: 'Use a server password of 5 to 64 characters without control characters.' })
      return
    }
    setPending('save')
    setNotice(null)
    try {
      let result: ActionResult | null = null
      if (dirty) {
        result = await change<ActionResult>('/api/local/settings', 'PUT', draft)
        setSnapshot(result.snapshot)
        if (!result.ok) { setNotice({ good: false, text: result.message }); return }
        setDraft(result.snapshot.settings)
        dirtyRef.current = false
        setDirty(false)
      }
      if (profile?.kind === 'Valheim' && password) {
        result = await change<ActionResult>(`/api/local/profiles/${profile.id}/password`, 'POST', { password })
        setSnapshot(result.snapshot)
        if (!result.ok) { setNotice({ good: false, text: result.message }); return }
        setPasswords(current => ({ ...current, [profile.id]: '' }))
      }
      const passwordWasConfigured = snapshot?.mode === 'Host' && profile ? snapshot.passwordConfigured[profile.id] : false
      const needsPassword = profile?.kind === 'Valheim' && !password && !passwordWasConfigured
      if (startAfterSave && !needsPassword && profile) {
        const started = await change<ActionResult>(`/api/local/profiles/${profile.id}/start`, 'POST')
        setSnapshot(started.snapshot)
        setNotice({ good: started.ok, text: started.message })
        if (!started.ok) return
      } else setNotice({ good: true, text: needsPassword ? 'Add a game password before starting.' : 'Server setup saved.' })
      if (!needsPassword && profile) {
        window.localStorage.removeItem(setupDraftKey)
        setShowSetup(false)
        window.scrollTo({ top: 0, behavior: 'smooth' })
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const switchMode = async (mode: 'host' | 'friend') => {
    if (dirty && !window.confirm('Discard unsaved server settings and change pages?')) return
    setPending('mode')
    setNotice(null)
    try {
      const response = await fetch(`/api/local/mode/${mode}`, { method: 'POST', headers: localHeaders })
      const result: { ok: boolean; code: string; message: string } = await response.json()
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        setRevealedConnections({})
        setRevealedGamePasswords({})
        const next = await readSnapshot()
        setSnapshot(next)
        setDraft(next.mode === 'Host' ? next.settings : null)
        setShowSetup(false)
        dirtyRef.current = false
        setDirty(false)
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const run = async (key: string, path: string, method: 'POST' | 'PUT', body?: unknown) => {
    setPending(key)
    setNotice(null)
    try {
      const result = await change<ActionResult>(path, method, body)
      setSnapshot(result.snapshot)
      setNotice({ good: result.ok, text: result.message })
      if (result.ok && key.startsWith('save')) { setDraft(result.snapshot.settings); dirtyRef.current = false; setDirty(false) }
      if (result.ok) void checkPorts()
    } catch (error) {
      setNotice({ good: false, text: String(error) })
    } finally { setPending('') }
  }
  const openDeviceServerAccess = (device: Device) => {
    const savedIds = new Set(snapshot?.mode === 'Host' ? snapshot.settings.profiles.map(profile => profile.id) : [])
    setNotice(null)
    setServerAccessSearch('')
    setServerAccessDraft(device.assignedProfileIds.filter(id => savedIds.has(id)))
    setServerAccessDeviceId(device.id)
  }
  const closeDeviceServerAccess = () => {
    if (pending) return
    setServerAccessDeviceId('')
    setServerAccessDraft([])
    setServerAccessSearch('')
    setNotice(null)
  }
  const saveDeviceServerAccess = async () => {
    if (!serverAccessDeviceId) return
    setPending(serverAccessDeviceId)
    try {
      const result = await change<BasicResult>(`/api/local/devices/${serverAccessDeviceId}/servers`, 'PUT',
        { profileIds: serverAccessDraft })
      setNotice({ good: result.ok, text: result.message })
      const latest = await fetch('/api/local/companion')
      if (latest.ok) setCompanion(await latest.json())
      if (result.ok) {
        setServerAccessDeviceId('')
        setServerAccessDraft([])
        setServerAccessSearch('')
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
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

  const updateProfile = (id: string, patch: Partial<Profile>) => {
    if (!draft) return
    edit({ ...draft, profiles: draft.profiles.map(profile => profile.id === id ? { ...profile, ...patch } : profile) })
  }

  const minecraftProfile = (profile: Profile, item: MinecraftInstallation): Profile => ({
    ...profile,
    name: profile.name || `${item.kind === 'MinecraftJava' ? 'Java' : 'Bedrock'} server`,
    worldId: item.worldName,
    worldDirectory: item.serverDirectory,
    gamePort: item.gamePort,
    executablePath: item.executablePath || profile.executablePath,
    minecraft: item.kind === 'MinecraftJava' ? { serverJarPath: item.artifactPath } : null
  })

  const useMinecraft = (profile: Profile, item: MinecraftInstallation, announce = true) => {
    setDraft(current => current ? { ...current, profiles: current.profiles.map(saved =>
      saved.id === profile.id ? minecraftProfile(saved, item) : saved) } : current)
    dirtyRef.current = true
    setDirty(true)
    if (announce) setNotice({ good: true, text: `${item.kind === 'MinecraftJava' ? 'Java' : 'Bedrock'} server selected. Continue to review.` })
  }

  const scanMinecraft = async (folder?: string) => {
    try {
      const path = '/api/local/minecraft/discover' + (folder ? `?folder=${encodeURIComponent(folder)}` : '')
      const response = await fetch(path, { cache: 'no-store' })
      if (!response.ok) throw new Error(`Minecraft search returned ${response.status}`)
      setMinecraftDiscovery(await response.json() as MinecraftDiscovery)
    } catch (error) { setNotice({ good: false, text: String(error) }) }
  }

  const installMinecraft = async (profile: Profile) => {
    setPending('install-minecraft')
    setNotice(null)
    try {
      const result = await change<MinecraftInstallResult>('/api/local/minecraft/install', 'POST', {
        kind: profile.kind, worldName: profile.worldId.trim() || 'world', gamePort: profile.gamePort,
        acceptedTerms: !!minecraftTerms[profile.id]
      })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok && result.installation) {
        useMinecraft(profile, result.installation, false)
        setMinecraftTerms(current => ({ ...current, [profile.id]: false }))
        void scanMinecraft(result.installation.serverDirectory)
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }

  const changeGameKind = (profile: Profile, kind: Profile['kind']) => {
    if (profile.kind === kind || snapshot?.mode !== 'Host') return
    setPasswords(current => ({ ...current, [profile.id]: '' }))
    setMinecraftTerms(current => ({ ...current, [profile.id]: false }))
    setMinecraftSetupMode(current => ({ ...current, [profile.id]: 'existing' }))
    updateProfile(profile.id, { kind, name: '', serverName: '', crossplay: false, publicListing: false,
      worldId: '', worldSource: kind === 'Valheim' ? 'New' : 'Existing',
      worldDirectory: kind === 'Valheim' ? `${snapshot.managedWorldsRoot}\\${profile.id.replaceAll('-', '')}` : '',
      gamePort: kind === 'Valheim' ? 2456 : kind === 'MinecraftJava' ? 25565 : kind === 'MinecraftBedrock' ? 19132 : 2456,
      executablePath: '', minecraft: kind === 'MinecraftJava' ? { serverJarPath: '' } : null })
  }

  const addProfile = () => {
    if (!draft || snapshot?.mode !== 'Host') return
    setNotice(null)
    const id = crypto.randomUUID()
    edit({ ...draft, profiles: [...draft.profiles, { id, kind: 'Valheim', name: '', serverName: '', crossplay: false,
      publicListing: false, worldId: '', worldSource: 'New',
      worldDirectory: `${snapshot.managedWorldsRoot}\\${id.replaceAll('-', '')}`, gamePort: 2456, executablePath: '' }] })
    setActiveProfileId(id)
    setSetupStep('game')
    setMinecraftSetupMode(current => ({ ...current, [id]: 'existing' }))
    setShowSetup(true)
    if (!discovery) void scanValheim()
  }

  const removeProfile = async (profile: Profile) => {
    if (!draft || !window.confirm(`Remove ${profile.name || profile.serverName || 'this server'} from TogetherServer? Its world files are left in place.`)) return
    setPending('remove-profile')
    try {
      const profiles = draft.profiles.filter(item => item.id !== profile.id)
      const result = await change<ActionResult>('/api/local/settings', 'PUT', { ...draft, profiles,
        companionListeningEnabled: profiles.length > 0 && draft.companionListeningEnabled,
        remoteControlsEnabled: profiles.length > 0 && draft.remoteControlsEnabled })
      setSnapshot(result.snapshot)
      setDraft(result.snapshot.settings)
      dirtyRef.current = false
      setDirty(false)
      if (result.ok) setShowSetup(false)
      setNotice({ good: result.ok, text: result.ok ? 'Server removed from TogetherServer. Its world files were left in place.' : result.message })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }

  const cancelSetup = () => {
    if (snapshot?.mode !== 'Host' || pending) return
    if (dirty && !window.confirm('Discard these setup changes?')) return
    setDraft(snapshot.settings)
    setActiveProfileId(snapshot.settings.profiles[0]?.id ?? '')
    setPasswords({})
    setMinecraftTerms({})
    setSourceRoots({})
    dirtyRef.current = false
    setDirty(false)
    window.localStorage.removeItem(setupDraftKey)
    setShowSetup(false)
    setNotice(null)
  }

  const finishSetupLater = () => {
    if (pending) return
    setShowSetup(false)
    setNotice({ good: true, text: 'Setup is paused. Choose Continue setup when you are ready.' })
  }

  const openSetup = (id: string) => {
    setNotice(null)
    setActiveProfileId(id)
    setSetupStep('review')
    setShowSetup(true)
  }
  const openHostSettings = (section: HostSettingsSection = 'access') => {
    setNotice(null)
    setHostSettingsSection(section)
    setShowHostSettings(true)
  }
  const closeHostSettings = () => {
    if (snapshot?.mode !== 'Host' || pending) return
    if (dirty && !window.confirm('Discard unsaved advanced settings?')) return
    setDraft(snapshot.settings)
    dirtyRef.current = false
    setDirty(false)
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
      const ready = await issueInvite(profileId)
      if (ready && request === inviteLoad.current) {
        try {
          await navigator.clipboard.writeText(ready)
          setNotice({ good: true, text: 'Server code copied. Share it privately with your friends.' })
        }
        catch { setNotice({ good: false, text: 'Invite ready, but it could not be copied. Choose Copy again.' }) }
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
  }

  const scanValheim = async () => {
    setPending('scan')
    try {
      const response = await fetch('/api/local/valheim/discover', { cache: 'no-store' })
      if (!response.ok) throw new Error(`Discovery returned ${response.status}`)
      const result: Discovery = await response.json()
      setDiscovery(result)
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const importWorld = async (profile: Profile, sourceSaveRoot: string, worldId: string, sourceFolder = 'worlds_local') => {
    if (sourceFolder === 'worlds' && !window.confirm('Close Valheim and wait for Steam Cloud to finish syncing before copying this cached world folder. Continue?')) return
    setPending(profile.id)
    try {
      const result = await change<ImportResult>('/api/local/valheim/import', 'POST', {
        profileId: profile.id, sourceSaveRoot, worldId, sourceFolder
      })
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}${result.ok ? ' Save the profile next.' : ''}` })
      if (result.ok && result.worldDirectory) updateProfile(profile.id, {
        name: profile.name || worldId, serverName: profile.serverName || worldId,
        worldId, worldSource: 'Existing', worldDirectory: result.worldDirectory
      })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const browseServer = async (profile: Profile) => {
    setPending(profile.id)
    try {
      const result = await change<ServerBrowseResult>('/api/local/valheim/browse-server', 'POST')
      if (result.ok && result.executablePath) updateProfile(profile.id, { executablePath: result.executablePath })
      if (result.code !== 'Canceled') setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const browseMinecraft = async (profile: Profile, target: 'folder' | 'executable' | 'jar') => {
    setPending(profile.id)
    try {
      const result = await change<MinecraftBrowseResult>('/api/local/minecraft/browse', 'POST', { kind: profile.kind, target })
      if (result.ok && result.path) {
        if (target === 'folder') { updateProfile(profile.id, { worldDirectory: result.path }); void scanMinecraft(result.path) }
        if (target === 'executable') updateProfile(profile.id, { executablePath: result.path,
          worldDirectory: profile.kind === 'MinecraftBedrock' && !profile.worldDirectory
            ? result.path.slice(0, result.path.lastIndexOf('\\')) : profile.worldDirectory })
        if (target === 'jar') updateProfile(profile.id, { minecraft: { serverJarPath: result.path },
          worldDirectory: profile.worldDirectory || result.path.slice(0, result.path.lastIndexOf('\\')) })
        if (target === 'jar') void scanMinecraft(result.path.slice(0, result.path.lastIndexOf('\\')))
      }
      if (result.code !== 'Canceled') setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const browseWorld = async (profile: Profile, folder = false) => {
    setPending(profile.id)
    try {
      const result = await change<WorldBrowseResult>(folder ? '/api/local/valheim/browse-world-folder' : '/api/local/valheim/browse-world', 'POST')
      if (result.ok && result.sourceSaveRoot && result.worldId) {
        if (result.sourceFolder === 'worlds_local')
          setSourceRoots(current => ({ ...current, [profile.id]: result.sourceSaveRoot! }))
        await importWorld(profile, result.sourceSaveRoot, result.worldId, result.sourceFolder)
      } else if (result.code !== 'Canceled') setNotice({ good: false, text: `${result.code}: ${result.message}` })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }

  const editedProfile = draft?.profiles.find(profile => profile.id === activeProfileId) ?? draft?.profiles[0]
  const detectedGameIp = snapshot?.mode === 'Host' && snapshot.settings.publicGameIpCheckedUtc &&
    Date.now() - Date.parse(snapshot.settings.publicGameIpCheckedUtc) < 60 * 60 * 1000
    ? snapshot.settings.publicGameIp : ''
  const friendAppAddress = hostAddress(draft?.companionEndpoint ?? '') ||
    (detectedGameIp ? `${detectedGameIp}${draft?.companionPort === 5131 ? '' : `:${draft?.companionPort}`}` : '')
  const setupIssues = editedProfile ? getSetupIssues(editedProfile,
    snapshot?.mode === 'Host' && !!snapshot.passwordConfigured[editedProfile.id], passwords[editedProfile.id] ?? '')
    .map(message => ({ id: editedProfile.id, name: editedProfile.name || editedProfile.serverName || 'New server', message })) : []
  const stepIssues = editedProfile ? getStepIssues(setupStep, editedProfile,
    snapshot?.mode === 'Host' && !!snapshot.passwordConfigured[editedProfile.id], passwords[editedProfile.id] ?? '') : []
  const setupSteps: SetupStep[] = ['game', 'world', 'server', 'review']
  const setupStepIndex = setupSteps.indexOf(setupStep)
  const savedProfiles = snapshot?.mode === 'Host' ? snapshot.settings.profiles : []
  const liveConnectionKeySignature = JSON.stringify((snapshot?.mode === 'Host'
    ? snapshot.runs.filter(run => run.state === 'Ready' && detectedGameIp).map(run => `host-${run.profileId}`)
    : snapshot?.profiles.filter(profile => profile.state === 'Ready' && profile.joinAddress)
      .map(profile => `friend-${snapshot.connectionId}-${profile.id}`) ?? []).sort())
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
    `${profile.name} ${gameLabel(profile.kind)}`.toLocaleLowerCase().includes(normalizedServerSearch))
  const activeRuns = snapshot?.mode === 'Host'
    ? snapshot.runs.filter(run => ['Process running', 'Starting', 'Ready'].includes(run.state)).length : 0
  const currentRouteResult = !dirty && companion?.listenerActive
    ? currentOutsideResult(portDiagnostics?.control, internetRouteCheck) : null
  const previousRouteVerdict = !currentRouteResult &&
    (internetRouteCheck?.state === 'Reachable' || internetRouteCheck?.state === 'Not reachable')
  const activeInviteWarning = inviteListenerWarning || (invitation && companion?.listenerActive === false
    ? companion.listenerWarning || 'Friend app connections are off. Choose Invite friends again to start the HTTPS listener.'
    : null)

  return <div className="shell">
    <header className="topbar">
      <nav className="mode-switch" aria-label="App pages">
        <Button className={snapshot?.mode === 'Host' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Host'} onClick={() => void switchMode('host')}><span className={activeRuns ? 'mode-dot active' : 'mode-dot'} />Host{activeRuns ? ` · ${activeRuns}` : ''}</Button>
        <Button className={snapshot?.mode === 'Friend' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Friend'} onClick={() => void switchMode('friend')}>Join</Button>
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
            {!notice && update?.state !== 'Available' && <p className="notification-empty">No new notifications.</p>}
          </div>
        </details>
        <details className="app-menu"><summary aria-label="App settings" title="App settings"><Icon name="settings" size={19} /></summary><div className="app-menu-panel"><strong>App settings</strong>
          <div className="app-version"><span>Version {update?.currentVersion ?? 'checking…'}</span><Button className="text-button" disabled={updateBusy || !!pending} onClick={() => void checkUpdate()}>{updateBusy ? 'Checking…' : 'Check for updates'}</Button></div>
          <label className="check-row"><Input type="checkbox" checked={desktopPreferences?.launchAtLogin ?? false} disabled={!desktopPreferences?.available || !desktopPreferences.startupAvailable || desktopBusy} onChange={event => void saveDesktopPreference({ launchAtLogin: event.target.checked })} />Open at Windows sign-in</label><small>Starts quietly in the tray.</small>
          <label className="check-row"><Input type="checkbox" checked={desktopPreferences?.closeToTray ?? false} disabled={!desktopPreferences?.available || desktopBusy} onChange={event => void saveDesktopPreference({ closeToTray: event.target.checked })} />Close to tray</label><small>Hosting and Friend checks keep running.</small>
          <Button className="app-menu-quit" disabled={!desktopPreferences?.available} onClick={() => void quitApp()}>Quit TogetherServer</Button>
        </div></details>
      </div>
    </header>

    <main>
      <div className="page-heading"><div><h1>{snapshot?.mode === 'Friend' ? 'Join' : 'Host'}</h1>
        <p>{snapshot?.mode === 'Friend' ? 'Connect to a server without interrupting anything you host on this PC.' : savedProfiles.length === 0 ? 'Set up a server, or switch to Join if a friend sent you a code.' : activeRuns ? `${activeRuns} ${activeRuns === 1 ? 'server is' : 'servers are'} running.` : 'Start a saved server when your group is ready.'}</p></div>
      </div>

      {loadError && <div className="notice bad" role="alert">Connection to this local app failed: {loadError}</div>}
      {!snapshot && !loadError && <section className="panel">Loading local state…</section>}

      {snapshot?.mode === 'Friend' && <>
        {(snapshot.connections?.length ?? 0) > 1 && <section className="panel saved-connections"><div className="section-heading"><div><h2>Saved servers</h2><p>Choose which Host you want to view.</p></div></div>
          <div className="choices">{snapshot.connections!.map(connection => <div className="choice" key={connection.connectionId}>
            <span><strong>{connection.profiles[0]?.name ?? hostAddress(connection.endpoint)}</strong><small>{gameLabel(connection.profiles[0]?.kind ?? 'Server')} · {connection.state}</small></span>
            <Button className="secondary" disabled={!!pending || connection.connectionId === snapshot.connectionId} onClick={() => void selectFriendConnection(connection.connectionId)}>{connection.connectionId === snapshot.connectionId ? 'Showing' : 'Show'}</Button>
          </div>)}</div>
        </section>}
        <section className="panel friend-panel friend-primary">
          <div className="section-heading"><div><h2>{snapshot.endpoint && !showPairing ? 'Connection' : snapshot.endpoint ? 'Add another server' : 'Paste your server code'}</h2><p>{snapshot.endpoint && !showPairing ? snapshot.detail : "Ask the Host to copy this server's current code."}</p></div></div>
          {snapshot.endpoint && !showPairing ? <>
            <div className={pending === 'poll' ? 'compact-status refreshing' : 'compact-status'} aria-busy={pending === 'poll'}><span className={`status ${statusTone(snapshot.state)}`}>{pending === 'poll' && <Icon name="loader" />}{snapshot.state === 'Disconnected/Unknown' ? 'Connection unknown' : snapshot.state}</span>
              <span>{snapshot.lastConnectedUtc ? `Last reached ${new Date(snapshot.lastConnectedUtc).toLocaleTimeString()}` : 'Waiting for a reply from the Host'}</span></div>
            {snapshot.connectionCode && (snapshot.state === 'Disconnected/Unknown' || snapshot.state === 'Revoked') && <details className="troubleshoot-block" open><summary>Troubleshoot connection</summary><FriendConnectionHelp code={snapshot.connectionCode} /></details>}
            <div className="actions"><Button className="secondary" disabled={!!pending} onClick={() => void checkFriendConnection()}>{pending === 'poll' ? <><Icon name="loader" />Refreshing…</> : 'Check connection'}</Button>
              <Button className="text-button" onClick={() => { setShowPairing(true); setFriendHostAddress(''); setFriendInvite(''); setPairIssue(null) }}>Add another server</Button></div>
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
            const activity = connectionActivity[connectionKey] ?? null
            return <article className="profile-card" key={profile.id} aria-busy={pending === 'poll' || pending.endsWith(profile.id)}>
              <div className="profile-top"><div><h3>{profile.name}</h3><p>{gameLabel(profile.kind)}</p><ServerActivity state={profile.state} online={profile.onlinePlayers} capacity={profile.maxPlayers} deadline={profile.autoShutdownAtUtc} timerReason={profile.autoShutdownReason} nowMs={nowMs} /></div><span className={`status ${statusTone(profile.state)}`}>{pending === 'poll' && <Icon name="loader" />}{profile.state === 'Ready' ? 'Ready to join' : profile.state}</span></div>
              {profile.state === 'Ready' && profile.joinAddress && <ConnectionDetails
                fields={[{ label: 'Join address', value: profile.joinAddress }]}
                revealed={!!revealedConnections[connectionKey]} copying={activity === 'copy'} revealing={false}
                refreshing={pending === 'poll'} note={profile.kind === 'Valheim' ? 'The game password is shared separately by your Host.' : undefined}
                onReveal={() => revealConnectionDetails(connectionKey)} onHide={() => hideConnectionDetails(connectionKey)}
                onCopy={() => void copyConnectionValue(connectionKey, profile.joinAddress!, 'Join address')} />}
              <div className="actions server-actions">
                {profile.state === 'Offline' && snapshot.state === 'Connected' && snapshot.canStart && <Button disabled={!!pending} onClick={() => void friendAction(profile.id, 'start')}>{pending === `friend-start-${profile.id}` ? <><Icon name="loader" />Starting…</> : <><Icon name="play" />Start server</>}</Button>}
                {profile.state === 'Ready' && snapshot.state === 'Connected' && snapshot.canStop && profile.canStopNow && <Button className="secondary" disabled={!!pending} onClick={() => void friendAction(profile.id, 'stop')}>{pending === `friend-stop-${profile.id}` ? <><Icon name="loader" />Stopping…</> : <><Icon name="stop" />Stop server</>}</Button>}
              </div>
              <FriendStopBlockers snapshot={snapshot} profile={profile} />
              {profile.state === 'Offline' && !snapshot.canStart && snapshot.state === 'Connected' && <p className="helper-text">The Host has not allowed this PC to start the server.</p>}
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
              const connectionKey = `host-${profile.id}`
              const activity = connectionActivity[connectionKey] ?? null
              const gameAddress = detectedGameIp ? `${detectedGameIp}:${profile.gamePort}` : ''
              const connectionFields = [{ label: 'Join address', value: gameAddress },
                ...(profile.kind === 'Valheim' ? [{ label: 'Game password', value: revealedGamePasswords[connectionKey] ?? '' }] : [])]
              return <article className="profile-card" key={profile.id} aria-busy={checkingPorts || detectingPublicIp || pending.endsWith(profile.id)}>
                <div className="profile-top"><div><h3>{profile.name}</h3><p>{gameLabel(profile.kind)} · World {profile.worldId}</p><ServerActivity state={status?.state ?? 'Unknown'} online={status?.onlinePlayers ?? null} capacity={status?.maxPlayers ?? null} deadline={status?.autoShutdownAtUtc ?? null} timerReason={status?.autoShutdownReason ?? null} nowMs={nowMs} /></div>
                  <span className={`status ${statusTone(status?.state ?? 'Unknown')}`}>{(pending === `start-${profile.id}` || pending === `stop-${profile.id}`) && <Icon name="loader" />}{status?.state === 'Process running' ? 'Starting' : status?.state ?? 'Unknown'}</span></div>
                <ServerReadiness profileId={profile.id} status={status?.state ?? 'Unknown'} ports={portDiagnostics} routeCheck={internetRouteCheck}
                  busy={checkingPorts || !!pending} refreshing={checkingPorts} onRefresh={() => void checkPorts(true)} onOpenConnection={() => openHostSettings('network')} />
                {status?.state === 'Ready' && gameAddress && <ConnectionDetails fields={connectionFields}
                  revealed={!!revealedConnections[connectionKey]} copying={activity === 'copy'} revealing={activity === 'reveal'}
                  refreshing={checkingPorts || detectingPublicIp}
                  note={profile.kind === 'Valheim' ? 'Copying includes the saved game password.' : undefined}
                  onReveal={() => void revealHostConnectionDetails(profile, connectionKey)} onHide={() => hideConnectionDetails(connectionKey)}
                  onCopy={() => profile.kind === 'Valheim'
                    ? void copyGameDetails(profile, gameAddress, connectionKey)
                    : void copyConnectionValue(connectionKey, gameAddress, 'Join address')} />}
                {status?.autoShutdownAtUtc && <div className="timer-extension"><label>Extend this countdown<Input type="number" min="1" step="1" value={countdownExtensions[profile.id] ?? '15'} disabled={!!pending || dirty} onChange={event => setCountdownExtensions(current => ({ ...current, [profile.id]: event.target.value }))} /><small>Extra minutes for this countdown only.</small></label><Button className="secondary" disabled={!!pending || dirty} onClick={() => void extendCountdown(profile.id)}>{pending === `extend-${profile.id}` ? 'Adding…' : 'Add time'}</Button></div>}
                <div className="actions server-actions">
                  {status?.state === 'Offline' && <Button disabled={!!pending || dirty} onClick={() => void run(`start-${profile.id}`, `/api/local/profiles/${profile.id}/start`, 'POST')}>{pending === `start-${profile.id}` ? <><Icon name="loader" /><span>Starting…</span></> : <><Icon name="play" /><span>Start server</span></>}</Button>}
                  {['Process running', 'Starting', 'Ready'].includes(status?.state ?? '') && <Button disabled={!!pending || dirty} onClick={() => { hideConnectionDetails(connectionKey); void run(`stop-${profile.id}`, `/api/local/profiles/${profile.id}/stop`, 'POST') }}>{pending === `stop-${profile.id}` ? <><Icon name="loader" /><span>Stopping…</span></> : <><Icon name="stop" /><span>Stop server</span></>}</Button>}
                  <Button className="secondary server-invite-button" disabled={!!pending || dirty || !friendAppAddress} onClick={() => void inviteFriend(profile.id)}><Icon name="invite" /><span>Invite friends</span></Button>
                </div>
                {inviteProfileId === profile.id && <div className="inline-invite">
                  {invitation ? <><div className="invite-ready"><span><Icon name={activeInviteWarning ? 'warning' : 'check'} /></span><div><strong>{activeInviteWarning ? 'Friend connection needs attention' : 'Server code copied'}</strong><p>{activeInviteWarning ? 'Fix the issue below before sharing this code.' : 'Send the copied code privately. Your Friend still needs to test Connect.'}</p></div></div>
                    {activeInviteWarning && <p className="connection-warning" role="alert">{activeInviteWarning}</p>}
                    {!activeInviteWarning && currentRouteResult?.state === 'Not reachable' && <p className="connection-warning" role="alert">The internet test could not reach this PC at {new Date(currentRouteResult.checkedUtc).toLocaleTimeString()}. Open Friend access to fix the connection before sharing.</p>}
                    <div className="actions">{activeInviteWarning
                      ? <Button disabled={!!pending} onClick={() => void inviteFriend(profile.id)}><Icon name="refresh" />Try connection again</Button>
                      : <Button onClick={() => void copyText(invitation, 'Server code')}><Icon name="copy" />Copy again</Button>}
                      <Button className="danger-outline" disabled={!!pending || !!activeInviteWarning} onClick={() => void issueInvite(profile.id, true)}><Icon name="refresh" />Revoke all access and create a new code</Button>
                      <Button className="text-button" onClick={() => { setInviteProfileId(''); setInvitation(''); setInviteListenerWarning(null) }}>Done</Button></div>
                    <p>Creating a new code revokes every PC credential issued by the old code, including any extra servers later assigned to those credentials.</p></>
                    : inviteListenerWarning ? <><p className="connection-warning" role="alert">{inviteListenerWarning}</p>
                      <div className="actions"><Button disabled={!!pending} onClick={() => void inviteFriend(profile.id)}><Icon name="refresh" />Try again</Button>
                        <Button className="text-button" onClick={() => { setInviteProfileId(''); setInviteListenerWarning(null) }}>Done</Button></div></>
                      : <p className="helper-text">Preparing this server's invite…</p>}
                </div>}
                {status?.state === 'Ready' && !detectedGameIp && <div className="next-action"><span>Your public game address is not available yet.</span><Button className="text-button" onClick={() => openHostSettings('network')}>Check connection</Button></div>}
                <details className="advanced-block card-manage"><summary>Manage server</summary>
                  <p className="helper-text">Playing on this PC? Join <code>127.0.0.1:{profile.gamePort}</code>.</p>
                  <div className="actions"><Button className="secondary" disabled={!!pending || status?.state !== 'Offline'} onClick={() => openSetup(profile.id)}>Edit setup</Button><Button className="secondary" onClick={() => openHostSettings('network')}>Connection help</Button><Button className="secondary" disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/health`, 'POST')}>Check server health</Button>
                    {(status?.state === 'Unknown' || status?.state === 'Failed') && <Button className="text-button" disabled={!!pending || dirty} onClick={() => {
                      if (window.confirm('Clear this unresolved run record only after verifying the original process is stopped?'))
                        void run(profile.id, `/api/local/profiles/${profile.id}/forget`, 'POST')
                    }}>Clear unresolved record</Button>}</div>
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
          {draft.profiles.length > 0 && dirty ? <div className="welcome-choice"><div><strong>Continue server setup</strong><p>Your unfinished non-secret setup details are still here. Re-enter the game password before saving.</p></div><Button onClick={() => { setActiveProfileId(draft.profiles[0].id); setSetupStep('world'); setShowSetup(true) }}>Continue setup</Button></div> : <div className="welcome-grid">
            <Button className="welcome-choice" disabled={!!pending} onClick={addProfile}><span className="section-icon"><Icon name="server" /></span><span><strong>Host a server</strong><small>Create a new world or use a server already on this PC.</small></span></Button>
            <Button className="welcome-choice secondary-choice" disabled={!!pending} onClick={() => void switchMode('friend')}><span className="section-icon"><Icon name="link" /></span><span><strong>Join a server</strong><small>Paste the private code your friend sent you.</small></span></Button>
          </div>}
        </section>}

        {showSetup && <dialog ref={setupRef} className="panel settings-panel modal-dialog" aria-labelledby="setup-title" onCancel={event => { event.preventDefault(); cancelSetup() }}>
          <div className="section-heading"><span className="section-icon"><Icon name="server" /></span><div><h2 id="setup-title">{savedProfiles.some(profile => profile.id === editedProfile?.id) ? 'Server settings' : 'Add new server'}</h2><p>Choose the game, world, and server files.</p></div></div>
          {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
          <ol className="setup-progress" aria-label="Setup progress">{(['game', 'world', 'server', 'review'] as SetupStep[]).map((step, index) => <li className={setupStep === step ? 'current' : index < setupStepIndex ? 'complete' : ''} key={step}><span>{index + 1}</span>{step === 'game' ? 'Game' : step === 'world' ? 'World' : step === 'server' ? 'Server app' : 'Review'}</li>)}</ol>
          {!editedProfile && <div className="empty"><p>Start with one game server.</p><div className="actions"><Button onClick={addProfile}>Set up a server</Button></div></div>}
          {draft.profiles.filter(profile => profile.id === editedProfile?.id).map(profile => <div className="profile-form" key={profile.id}>
            {setupStep === 'game' && <div className="setup-stage"><h3>Choose a game</h3><p className="helper-text">TogetherServer uses reviewed built-in server controls. You can change technical defaults during Review.</p><div className="game-choice-grid">
              <Button className={profile.kind === 'Valheim' ? 'game-choice selected' : 'game-choice'} onClick={() => changeGameKind(profile, 'Valheim')}><strong>Valheim</strong><small>Established local Host flow</small></Button>
              <Button className={profile.kind === 'MinecraftJava' ? 'game-choice selected' : 'game-choice'} onClick={() => changeGameKind(profile, 'MinecraftJava')}><strong>Minecraft Java</strong><small>Preview · real-server acceptance pending</small></Button>
              <Button className={profile.kind === 'MinecraftBedrock' ? 'game-choice selected' : 'game-choice'} onClick={() => changeGameKind(profile, 'MinecraftBedrock')}><strong>Minecraft Bedrock</strong><small>Preview · real-server acceptance pending</small></Button>
              {profile.kind === 'Fixture' && <Button className="game-choice selected"><strong>Synthetic fixture</strong><small>Development checks only</small></Button>}
            </div></div>}
            {setupStep === 'world' && <div className="setup-step world-step"><h3><Icon name="game" /> {profile.kind === 'Valheim' ? 'Choose a world' : 'Name this server'}</h3>
              {profile.kind === 'Valheim' && <div className="choice-pills">
                <Button className={profile.worldSource === 'New' ? 'selected' : 'secondary'} onClick={() => updateProfile(profile.id, { worldSource: 'New', worldId: '', name: '', serverName: '', worldDirectory: `${snapshot.managedWorldsRoot}\\${profile.id.replaceAll('-', '')}` })}>Create new</Button>
                <Button className={profile.worldSource === 'Existing' ? 'selected' : 'secondary'} onClick={() => updateProfile(profile.id, { worldSource: 'Existing', worldId: '', name: '', serverName: '', worldDirectory: '' })}>Use existing</Button>
              </div>}
              {profile.kind === 'Valheim' && profile.worldSource === 'Existing' && <>
                {profile.worldId && profile.worldDirectory && <p className="selection-summary">Copy ready: <strong>{profile.worldId}</strong>. Your original save stays separate.</p>}
                {discovery && <div className="choices"><strong>Worlds found on this PC</strong>{discovery.worlds.length === 0 ? <p>None found. Browse to a world folder below.</p> : discovery.worlds.map(world => <div className="choice" key={world.saveRoot + world.sourceFolder + world.name}><span>{world.name} <small>{world.format === 'Steam cloud folder' ? 'Steam Cloud' : 'Local save'} · {world.saveRoot}</small></span><Button className="secondary" disabled={!!pending} onClick={() => void importWorld(profile, world.saveRoot, world.name, world.sourceFolder)}>Copy world</Button></div>)}</div>}
                <div className="setup-tools"><Button className="secondary" disabled={!!pending} onClick={() => void browseWorld(profile, true)}>{pending === profile.id ? 'Browsing…' : 'Browse for a world folder'}</Button></div>
                <p className="helper-text">Close Valheim and let Steam finish syncing before copying a cloud world.</p>
                <details className="advanced-block"><summary>Older saves and custom paths</summary>
                  <Button className="secondary" disabled={!!pending} onClick={() => void browseWorld(profile)}>Choose an older .db or .fwl file</Button>
                  <div className="settings-grid"><label>Local save root<Input value={sourceRoots[profile.id] ?? ''} onChange={event => setSourceRoots(current => ({ ...current, [profile.id]: event.target.value }))} placeholder="C:\\...\\IronGate\\Valheim" /></label><label>World ID<Input value={profile.worldId} onChange={event => updateProfile(profile.id, { worldId: event.target.value })} placeholder="World folder name" /></label></div>
                  <Button className="secondary" disabled={!!pending || !sourceRoots[profile.id] || !profile.worldId} onClick={() => void importWorld(profile, sourceRoots[profile.id], profile.worldId)}>Copy named world</Button>
                </details>
              </>}
              {profile.kind === 'Valheim' && profile.worldSource === 'New' && <div className="quick-setup-fields"><label className="invite-input">World name<Input value={profile.worldId} onChange={event => updateProfile(profile.id, { worldId: event.target.value, name: event.target.value, serverName: event.target.value })} placeholder="My Valheim world" /><small>Stored in TogetherServer's private data.</small></label>
                <label className="invite-input">Game password<Input type={showPasswords[profile.id] ? 'text' : 'password'} autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => setPasswords(current => ({ ...current, [profile.id]: event.target.value }))} placeholder={snapshot.passwordConfigured[profile.id] ? 'Saved already; leave blank to keep it' : '5 or more characters'} /><small>Friends use this inside Valheim.</small><span className="show-password"><Input type="checkbox" checked={!!showPasswords[profile.id]} onChange={event => setShowPasswords(current => ({ ...current, [profile.id]: event.target.checked }))} /> Show password</span></label></div>}
              {profile.kind === 'Fixture' && <div className="settings-grid"><label>Test profile name<Input value={profile.name} onChange={event => updateProfile(profile.id, { name: event.target.value })} /></label><label>World ID<Input value={profile.worldId} onChange={event => updateProfile(profile.id, { worldId: event.target.value })} /></label><label className="wide">Disposable directory<Input value={profile.worldDirectory} onChange={event => updateProfile(profile.id, { worldDirectory: event.target.value })} /></label></div>}
              {(profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') && <><div className="choice-pills">
                <Button className={(minecraftSetupMode[profile.id] ?? 'existing') === 'existing' ? 'selected' : 'secondary'} onClick={() => setMinecraftSetupMode(current => ({ ...current, [profile.id]: 'existing' }))}>Use an existing server</Button>
                <Button className={minecraftSetupMode[profile.id] === 'install' ? 'selected' : 'secondary'} onClick={() => setMinecraftSetupMode(current => ({ ...current, [profile.id]: 'install' }))}>Install a new official server</Button>
              </div><MinecraftWorldSetup profile={profile} busy={!!pending} onChange={patch => updateProfile(profile.id, patch)} /></>}
              {profile.kind === 'Valheim' && profile.worldSource === 'Existing' && <label className="invite-input setup-password">Game password<Input type={showPasswords[profile.id] ? 'text' : 'password'} autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => setPasswords(current => ({ ...current, [profile.id]: event.target.value }))} placeholder={snapshot.passwordConfigured[profile.id] ? 'Saved already; leave blank to keep it' : '5 or more characters'} /><small>Friends use this inside Valheim.</small><span className="show-password"><Input type="checkbox" checked={!!showPasswords[profile.id]} onChange={event => setShowPasswords(current => ({ ...current, [profile.id]: event.target.checked }))} /> Show password</span></label>}
            </div>}
            {setupStep === 'server' && <div className="setup-step server-step"><h3><Icon name="search" /> Server app</h3>
              <div className="server-step-content">{profile.kind === 'Valheim' ? <>
                {profile.executablePath ? <p className="selection-summary"><Icon name="check" /> Valheim Dedicated Server found <small>{profile.executablePath}</small></p> : <>
                  {discovery && <div className="choices">{discovery.installations.length === 0 ? <p>Valheim Dedicated Server was not found.</p> : discovery.installations.map(item => <div className="choice" key={item.executablePath}><span>{item.executablePath}</span><Button className="secondary" onClick={() => updateProfile(profile.id, { executablePath: item.executablePath })}>Use this install</Button></div>)}</div>}
                  <div className="setup-tools"><Button className="secondary" disabled={!!pending} onClick={() => void scanValheim()}>{pending === 'scan' ? 'Searching…' : 'Search this PC'}</Button><Button className="secondary" disabled={!!pending} onClick={() => void browseServer(profile)}>Browse for server</Button>{discovery?.installations.length === 0 && <a href="steam://install/896660">Open install in Steam</a>}</div>
                  <p className="helper-text">Steam handles installation and any terms when you open it.</p>
                </>}
              </> : profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock' ?
                <MinecraftServerSetup profile={profile} busy={!!pending} onChange={patch => updateProfile(profile.id, patch)}
                  onBrowse={target => void browseMinecraft(profile, target)} discovery={minecraftDiscovery}
                  mode={minecraftSetupMode[profile.id] ?? 'existing'}
                  onSelect={item => useMinecraft(profile, item)} onScan={() => void scanMinecraft(profile.worldDirectory)}
                  onInstall={() => void installMinecraft(profile)} acceptedTerms={!!minecraftTerms[profile.id]}
                  onTermsChange={accepted => setMinecraftTerms(current => ({ ...current, [profile.id]: accepted }))}
                  installBusy={pending === 'install-minecraft'} />
              : <label>Fixture executable path<Input value={profile.executablePath} onChange={event => updateProfile(profile.id, { executablePath: event.target.value })} placeholder="C:\\...\\TogetherServer.Fixture.exe" /></label>}</div>
            </div>}
            {setupStep === 'review' && <div className="setup-stage review-stage"><h3>Review and start</h3><div className="review-summary"><div><span>Game</span><strong>{gameLabel(profile.kind)}</strong></div><div><span>Server</span><strong>{profile.name || profile.serverName || 'Needs a name'}</strong></div><div><span>World</span><strong>{profile.worldId || 'Not selected'}</strong></div><div><span>Server app</span><strong>{profile.executablePath ? 'Selected' : 'Not selected'}</strong></div></div>
            <details className="advanced-block"><summary>Advanced server settings</summary>
              <div className="settings-grid">{profile.kind === 'Valheim' && <><label>Game UDP start port<Input type="number" value={profile.gamePort} onChange={event => updateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label><label>Server listing name<Input value={profile.serverName} onChange={event => updateProfile(profile.id, { serverName: event.target.value, name: event.target.value })} /></label><label className="wide">Installed server path<Input value={profile.executablePath} onChange={event => updateProfile(profile.id, { executablePath: event.target.value })} /></label><label className="wide">Save directory<Input value={profile.worldDirectory} onChange={event => updateProfile(profile.id, { worldDirectory: event.target.value })} /></label></>}</div>
              {profile.kind === 'Valheim' && <div className="device-options"><label className="check-row"><Input type="checkbox" checked={profile.crossplay} onChange={event => updateProfile(profile.id, { crossplay: event.target.checked })} /> Crossplay relay</label><label className="check-row"><Input type="checkbox" checked={profile.publicListing} onChange={event => updateProfile(profile.id, { publicListing: event.target.checked })} /> Show in server list</label></div>}
              {profile.worldDirectory && <p className="helper-text">Server save location: <code>{profile.worldDirectory}</code></p>}
            </details></div>}
          </div>)}
          {editedProfile && <>{setupStep === 'review' && savedProfiles.some(saved => saved.id === editedProfile.id) && <details className="advanced-block danger-zone"><summary>Danger zone</summary><p className="helper-text">Removing this server forgets its setup and Friend access. TogetherServer leaves its world files in place.</p><Button className="text-button danger" disabled={!!pending} onClick={() => void removeProfile(editedProfile)}>Remove from TogetherServer</Button></details>}
          {(setupStep === 'review' ? setupIssues : stepIssues).length > 0 && <p className="field-error" role="status">{setupStep === 'review' ? setupIssues[0].message : stepIssues[0]}</p>}
          <div className="wizard-footer">{savedProfiles.length === 0
            ? <Button className="text-button" disabled={!!pending} onClick={finishSetupLater}>Finish later</Button>
            : <Button className="text-button" disabled={!!pending} onClick={cancelSetup}>Cancel</Button>}<div className="actions">
            {setupStepIndex > 0 && <Button className="secondary" disabled={!!pending} onClick={() => setSetupStep(setupSteps[setupStepIndex - 1])}>Back</Button>}
            {setupStep !== 'review' && <Button disabled={stepIssues.length > 0 || !!pending} onClick={() => setSetupStep(setupSteps[setupStepIndex + 1])}>Continue</Button>}
            {setupStep === 'review' && <><Button className="secondary" disabled={setupIssues.length > 0 || (!dirty && !passwords[editedProfile.id]) || !!pending} onClick={() => void saveSetup()}>Save for later</Button><Button disabled={setupIssues.length > 0 || (!dirty && !passwords[editedProfile.id]) || !!pending} onClick={() => void saveSetup(true)}><Icon name="play" />{pending === 'save' ? 'Starting…' : 'Save and start'}</Button></>}
          </div></div></>}
        </dialog>}
        {savedProfiles.length > 0 && showHostSettings && <dialog ref={hostSettingsRef} className="panel modal-dialog host-settings-dialog" aria-labelledby="host-settings-title" onCancel={event => { event.preventDefault(); closeHostSettings() }}>
          <div className="modal-heading"><div><h2 id="host-settings-title">Friend access and settings</h2><p>Everyday permissions first. Network and game paths stay under Advanced.</p></div>
            <Button className="secondary" disabled={!!pending} onClick={closeHostSettings}>{dirty ? 'Cancel' : 'Close'}</Button></div>
          {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
          <nav className="settings-tabs" aria-label="Host settings sections">
            <Button className={hostSettingsSection === 'access' ? 'selected' : ''} onClick={() => setHostSettingsSection('access')}>Friend access</Button>
            <Button className={hostSettingsSection === 'stop' ? 'selected' : ''} onClick={() => setHostSettingsSection('stop')}>Stop & timer</Button>
            <Button className={hostSettingsSection === 'network' ? 'selected' : ''} onClick={() => setHostSettingsSection('network')}>Connection help</Button>
            <Button className={hostSettingsSection === 'advanced' ? 'selected' : ''} onClick={() => setHostSettingsSection('advanced')}>Advanced</Button>
          </nav>
            <div className="settings-content">
              {hostSettingsSection === 'access' && <section className="settings-section"><h3>Friend access</h3>
                <p>Friend PCs can keep seeing status while controls are paused. Start and Stop requests are always checked again on this Host.</p>
                <div className="access-toggles"><label className="setting-toggle"><span><strong>Allow Friend app connections</strong><small>Needed for pairing, status, and remote requests.</small></span><Input type="checkbox" checked={draft.companionListeningEnabled} disabled={!!pending} onChange={event => void saveHostFlags({ companionListeningEnabled: event.target.checked, remoteControlsEnabled: event.target.checked ? draft.remoteControlsEnabled : false })} /></label>
                  <label className="setting-toggle"><span><strong>Allow remote Start and Stop</strong><small>Individual PC permissions below still apply.</small></span><Input type="checkbox" checked={draft.remoteControlsEnabled} disabled={!!pending || !draft.companionListeningEnabled} onChange={event => void saveHostFlags({ remoteControlsEnabled: event.target.checked })} /></label></div>
                {companion?.devices.filter(device => !device.revoked).length ? <div className="device-list"><h3>Paired Friend PCs</h3><p className="helper-text">A new PC starts with only the server whose code it used. You can assign that PC to any combination of your saved servers.</p>{companion.devices.filter(device => !device.revoked).map(device => <div className="device access-device" key={device.id}>
                  <div className="device-header"><div className="device-main"><label>PC name<Input value={deviceNames[device.id] ?? device.name} maxLength={48} onChange={event => setDeviceNames(current => ({ ...current, [device.id]: event.target.value }))} /></label><small>{device.lastHeartbeatUtc ? `Last report ${new Date(device.lastHeartbeatUtc).toLocaleTimeString()}` : device.paired ? 'No fresh report' : 'Waiting for this PC to connect'} · {device.profileId === '00000000-0000-0000-0000-000000000000' ? 'Paired with an older code' : `Paired with the code for ${savedProfiles.find(profile => profile.id === device.profileId)?.name ?? 'a removed server'}`}</small></div>
                    <div className="actions device-card-actions"><Button className="secondary" disabled={!!pending || !(deviceNames[device.id] ?? device.name).trim() || (deviceNames[device.id] ?? device.name).trim() === device.name} onClick={() => void saveDeviceName(device.id)}>Save name</Button><Button className="text-button danger" disabled={!!pending} onClick={() => void revokeDevice(device.id)}>Revoke</Button></div></div>
                  <div className="device-access-grid"><div className="device-server-summary"><div className="device-summary-copy"><span>Server access</span><strong>{device.assignedProfileIds.length} {device.assignedProfileIds.length === 1 ? 'server' : 'servers'}</strong><small title={serverAssignmentPreview(device, savedProfiles)}>{serverAssignmentPreview(device, savedProfiles)}</small></div><Button className="secondary" disabled={!!pending || !device.paired} onClick={() => openDeviceServerAccess(device)}><Icon name="server" />Choose servers</Button></div>
                    <label className="device-permission-toggle"><Input type="checkbox" checked={device.canStart} disabled={!!pending || !device.paired} onChange={event => void setDevicePermissions(device, event.target.checked, device.canStop)} /><span><strong>Start servers</strong><small>Allow on assigned servers</small></span></label>
                    <label className="device-permission-toggle"><Input type="checkbox" checked={device.canStop} disabled={!!pending || !device.paired} onChange={event => void setDevicePermissions(device, device.canStart, event.target.checked)} /><span><strong>Request Stop</strong><small>Allow on assigned servers</small></span></label></div>
                </div>)}</div> : <div className="empty compact-empty"><p>No Friend PCs are paired yet. Choose Invite friends on a server card to copy a private server code.</p></div>}
              </section>}
              {hostSettingsSection === 'network' && <section className="settings-section">
              <h3>Connection checks</h3>
              <p>Game address: {detectedGameIp ? `${detectedGameIp} detected, friend join untested` : 'unavailable'}. Friend app: {companion?.listenerActive ? 'listening on this PC, public route untested' : 'off'}.</p>
              {companion?.listenerWarning && <p className="warning-text">{companion.listenerWarning}</p>}
              {publicIpDetection && !publicIpDetection.ok && <p className="warning-text">{publicIpDetection.message}</p>}
              <div className="actions"><Button className="secondary" disabled={detectingPublicIp} onClick={() => void detectPublicIp()}>{detectingPublicIp ? <><Icon name="loader" />Refreshing…</> : <><Icon name="refresh" />Refresh public address</>}</Button></div>
              <div className={checkingInternetRoute ? 'internet-route-test refreshing' : 'internet-route-test'} aria-busy={checkingInternetRoute}><Button className="secondary" disabled={checkingInternetRoute || !!pending || dirty} onClick={() => void checkInternetRoute()}>{checkingInternetRoute ? <><Icon name="loader" />Testing TCP port…</> : 'Test Friend app port from internet'}</Button>
                <small>This checks the Friend app TCP port through portchecker.io. That service sees this PC's public IP and port; no invite or credential is sent.</small>
                {internetRouteCheck && <p className={`internet-route-result ${previousRouteVerdict ? 'neutral' : internetRouteCheck.state === 'Reachable' ? 'good' : internetRouteCheck.state === 'Not reachable' ? 'bad' : 'neutral'}`} role="status">
                  <strong>{previousRouteVerdict ? `Previous TCP ${internetRouteCheck.port} result` : internetRouteCheck.state === 'Reachable' ? `TCP ${internetRouteCheck.port} reached` : internetRouteCheck.state === 'Not reachable' ? `TCP ${internetRouteCheck.port} not reachable` : `${internetRouteCheck.state} · TCP ${internetRouteCheck.port}`}</strong>
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
                <div className="idle-settings"><label className="setting-toggle"><span><strong>Stop empty servers automatically</strong><small>Off by default. Host and Friend cards share the countdown or explain why it is paused.</small></span><Input type="checkbox" checked={draft.autoShutdownEnabled} disabled={!!pending || dirty} onChange={event => void saveHostFlags({ autoShutdownEnabled: event.target.checked })} /></label>
                  <label>Wait after the server reaches 0 players<Input type="number" min="1" max="1440" value={draft.idleMinutes} disabled={!!pending} onChange={event => edit({ ...draft, idleMinutes: Number(event.target.value) })} /><small>Minutes, from 1 to 1440.</small></label>
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
                <label>Custom HTTPS endpoint<Input value={draft.companionEndpoint} onChange={event => edit({ ...draft, companionEndpoint: event.target.value.trim() })} placeholder="https://127.0.0.1:5131" /></label>
                <label>Bind IP<Input value={draft.companionBindAddress} onChange={event => edit({ ...draft, companionBindAddress: event.target.value })} placeholder="127.0.0.1" /></label>
              </div>
              {companion?.fingerprint && <p className="footnote">Pinned Host identity: <code>{companion.fingerprint}</code></p>}
              <div className="actions"><Button disabled={!dirty || !!pending} onClick={() => void run('save', '/api/local/settings', 'PUT', draft)}>Save settings</Button></div>
              {companion?.devices.some(device => device.revoked) ? <details className="advanced-block"><summary>Revoked Friend PCs</summary><div className="profile-list device-list">{companion.devices.filter(device => device.revoked).map(device => <div className="device" key={device.id}>
                <div><strong>{device.name} · {savedProfiles.find(profile => profile.id === device.profileId)?.name ?? 'Legacy access'}</strong><small>Revoked</small></div>
              </div>)}</div></details> : null}
              </section>}
            </div>
        </dialog>}
        {savedProfiles.length > 0 && serverAccessDevice && <dialog ref={serverAccessRef} className="panel modal-dialog server-access-dialog" aria-labelledby="server-access-title" onCancel={event => { event.preventDefault(); closeDeviceServerAccess() }}>
          <div className="modal-heading"><div><h2 id="server-access-title">Choose servers for {serverAccessDevice.name}</h2><p>This PC will see and control only the servers selected here.</p></div><Button className="secondary" disabled={!!pending} onClick={closeDeviceServerAccess}>Cancel</Button></div>
          {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
          <label className="server-picker-search">Search servers<Input value={serverAccessSearch} autoFocus placeholder="Search by server or game" onChange={event => setServerAccessSearch(event.target.value)} /></label>
          <div className="server-picker-toolbar"><strong>{serverAccessDraft.length} of {savedProfiles.length} selected</strong><div className="actions"><Button className="text-button" disabled={!!pending || visibleServerAccessProfiles.length === 0} onClick={() => setServerAccessDraft(current => [...new Set([...current, ...visibleServerAccessProfiles.map(profile => profile.id)])])}>{normalizedServerSearch ? 'Select all results' : 'Select all'}</Button><Button className="text-button" disabled={!!pending || serverAccessDraft.length === 0} onClick={() => setServerAccessDraft([])}>Clear all</Button></div></div>
          <div className="server-picker-list" role="group" aria-label="Saved servers">{visibleServerAccessProfiles.map(profile => <label className="server-picker-option" key={profile.id}><Input type="checkbox" checked={serverAccessDraft.includes(profile.id)} disabled={!!pending} onChange={event => setServerAccessDraft(current => event.target.checked ? [...new Set([...current, profile.id])] : current.filter(id => id !== profile.id))} /><span><strong>{profile.name}</strong><small>{gameLabel(profile.kind)}</small></span></label>)}
            {visibleServerAccessProfiles.length === 0 && <div className="server-picker-empty">No servers match “{serverAccessSearch.trim()}”.</div>}</div>
          <div className="server-picker-footer"><span>Changes apply when you save.</span><div className="actions"><Button className="secondary" disabled={!!pending} onClick={closeDeviceServerAccess}>Cancel</Button><Button disabled={!!pending} onClick={() => void saveDeviceServerAccess()}>{pending === serverAccessDevice.id ? 'Saving…' : 'Save access'}</Button></div></div>
        </dialog>}
      </>}
    </main>
  </div>
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>)

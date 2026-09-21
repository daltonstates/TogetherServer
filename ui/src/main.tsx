import React, { useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { Icon } from './Icon'
import { ServerReadiness, type PortDiagnostics } from './ServerReadiness'
import './style.css'
import './companion.css'

type Profile = {
  id: string
  kind: 'Fixture' | 'Valheim'
  name: string
  serverName: string
  crossplay: boolean
  publicListing: boolean
  worldId: string
  worldSource: 'Existing' | 'New'
  worldDirectory: string
  gamePort: number
  executablePath: string
}
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
  ownerClientExecutablePath: string
  ownerPlatformUserId: string
  permittedPlayersVerified: boolean
  profiles: Profile[]
}
type Run = { profileId: string; state: string; detail: string; processId: number | null }
type HostSnapshot = { mode: 'Host'; evidence: string; settings: Settings; runs: Run[]; ownerGameRunning: boolean | null; ownerCheckedUtc: string; passwordConfigured: Record<string, boolean>; managedWorldsRoot: string }
type PublicProfile = { id: string; name: string; state: string; joinAddress: string | null; canStopNow: boolean; stopReason: string | null }
type Device = { id: string; profileId: string; name: string; canStart: boolean; canStop: boolean; revoked: boolean; paired: boolean; lastHeartbeatUtc: string | null; gameRunning: boolean | null; platformUserId: string }
type CompanionInfo = { listenerActive: boolean; listenerWarning: string | null; endpoint: string; fingerprint: string | null; devices: Device[]; stopSafety: Record<string, { available: boolean; reason: string }> }
type FriendSnapshot = { mode: 'Friend'; state: string; detail: string; endpoint: string; lastConnectedUtc: string | null; localGameRunning: boolean | null; remoteControlsEnabled: boolean; canStart: boolean; canStop: boolean; profiles: PublicProfile[]; clientExecutablePath: string }
type Snapshot = HostSnapshot | FriendSnapshot
type BasicResult = { ok: boolean; code: string; message: string }
type ActionResult = { ok: boolean; code: string; message: string; snapshot: HostSnapshot }
type PublicIpDetection = BasicResult & { address: string | null; snapshot?: HostSnapshot }
type Discovery = { installations: { executablePath: string; source: string }[]; clients: { executablePath: string; source: string }[]; worlds: { name: string; saveRoot: string; sourceFolder: string; format: string }[] }
type ImportResult = BasicResult & { worldDirectory: string | null }
type ServerBrowseResult = BasicResult & { executablePath?: string }
type WorldBrowseResult = BasicResult & { worldId: string | null; sourceSaveRoot: string | null; sourceFolder: string }
type UpdateView = { state: 'Checking' | 'Current' | 'Available' | 'NoRelease' | 'Unavailable' | 'Unsupported'; currentVersion: string; latestVersion: string | null; message: string }
type DesktopPreferences = { available: boolean; launchAtLogin: boolean; closeToTray: boolean; startupAvailable: boolean }
type DesktopPreferenceResult = BasicResult & { preferences: DesktopPreferences }

function hostAddress(endpoint: string): string {
  try {
    const url = new URL(endpoint)
    return url.port === '5131' ? url.hostname : url.host
  } catch { return '' }
}

const localHeaders = { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' }

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
  } else {
    if (!profile.name.trim()) issues.push('Enter a test profile name in step 1.')
    if (!profile.worldId.trim()) issues.push('Enter a world ID in step 1.')
    if (!profile.worldDirectory.trim()) issues.push('Choose a disposable directory in step 1.')
    if (!profile.executablePath.trim()) issues.push('Select the fixture executable in step 2.')
  }
  return issues
}

function App() {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [draft, setDraft] = useState<Settings | null>(null)
  const [dirty, setDirty] = useState(false)
  const [pending, setPending] = useState('')
  const [notice, setNotice] = useState<{ good: boolean; text: string } | null>(null)
  const [loadError, setLoadError] = useState('')
  const [update, setUpdate] = useState<UpdateView | null>(null)
  const [updateBusy, setUpdateBusy] = useState(false)
  const [desktopPreferences, setDesktopPreferences] = useState<DesktopPreferences | null>(null)
  const [desktopBusy, setDesktopBusy] = useState(false)
  const [companion, setCompanion] = useState<CompanionInfo | null>(null)
  const [publicIpDetection, setPublicIpDetection] = useState<PublicIpDetection | null>(null)
  const [portDiagnostics, setPortDiagnostics] = useState<PortDiagnostics | null>(null)
  const [detectingPublicIp, setDetectingPublicIp] = useState(false)
  const [deviceStart, setDeviceStart] = useState(true)
  const [inviteProfileId, setInviteProfileId] = useState('')
  const [playerIds, setPlayerIds] = useState<Record<string, string>>({})
  const [invitation, setInvitation] = useState('')
  const [friendInvite, setFriendInvite] = useState('')
  const [friendHostAddress, setFriendHostAddress] = useState('')
  const [showPairing, setShowPairing] = useState(false)
  const [friendClientPath, setFriendClientPath] = useState('')
  const [passwords, setPasswords] = useState<Record<string, string>>({})
  const [discovery, setDiscovery] = useState<Discovery | null>(null)
  const [sourceRoots, setSourceRoots] = useState<Record<string, string>>({})
  const [showSetup, setShowSetup] = useState(false)
  const [activeProfileId, setActiveProfileId] = useState('')
  const initialSetupSet = useRef(false)
  const initialProfileSet = useRef(false)
  const inviteLoad = useRef(0)
  const dirtyRef = useRef(false)
  const setupRef = useRef<HTMLElement | null>(null)
  const friendClientEdited = useRef(false)

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
    const timer = window.setInterval(() => void check(), 6 * 60 * 60 * 1000)
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
          if (next.settings.profiles.length === 0 && !initialProfileSet.current) {
            initialProfileSet.current = true
            const id = crypto.randomUUID()
            setDraft(current => current?.profiles.length ? current : { ...next.settings, profiles: [{ id, kind: 'Valheim', name: '', serverName: '', crossplay: false,
              publicListing: false, worldId: '', worldSource: 'New',
              worldDirectory: `${next.managedWorldsRoot}\\${id.replaceAll('-', '')}`, gamePort: 2456, executablePath: '' }] })
            setActiveProfileId(id)
            dirtyRef.current = true
            setDirty(true)
            void scanValheim()
          } else if (!dirtyRef.current) setDraft(next.settings)
          if (!initialSetupSet.current) {
            setShowSetup(next.settings.profiles.length === 0)
            initialSetupSet.current = true
          }
          const response = await fetch('/api/local/companion', { cache: 'no-store' })
          if (response.ok && alive) {
            const current: CompanionInfo = await response.json()
            setCompanion(current)
            setPlayerIds(ids => {
              const next = { ...ids }
              for (const device of current.devices) if (next[device.id] === undefined) next[device.id] = device.platformUserId
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
    if (snapshot?.mode !== 'Friend' || discovery) return
    let alive = true
    void fetch('/api/local/valheim/discover', { cache: 'no-store' })
      .then(response => response.ok ? response.json() as Promise<Discovery> : null)
      .then(result => { if (alive && result) setDiscovery(result) })
      .catch(() => { /* The editable client path remains available if discovery fails. */ })
    return () => { alive = false }
  }, [snapshot?.mode, discovery])

  useEffect(() => {
    if (snapshot?.mode !== 'Host' || !draft || !discovery) return
    const serverPath = discovery.installations.length === 1 ? discovery.installations[0].executablePath : ''
    const clientPath = draft.profiles.some(profile => profile.kind === 'Valheim') && discovery.clients.length === 1
      ? discovery.clients[0].executablePath : ''
    const profiles = draft.profiles.map(profile => profile.kind === 'Valheim' && !profile.executablePath && serverPath
      ? { ...profile, executablePath: serverPath } : profile)
    const ownerClientExecutablePath = draft.ownerClientExecutablePath || clientPath
    if (profiles.some((profile, index) => profile !== draft.profiles[index]) || ownerClientExecutablePath !== draft.ownerClientExecutablePath) {
      setDraft({ ...draft, profiles, ownerClientExecutablePath })
      dirtyRef.current = true
      setDirty(true)
    }
  }, [snapshot?.mode, discovery, draft])

  useEffect(() => {
    if (snapshot?.mode !== 'Friend' || friendClientEdited.current) return
    const detected = discovery?.clients.length === 1 ? discovery.clients[0].executablePath : ''
    setFriendClientPath(snapshot.clientExecutablePath || detected)
  }, [snapshot?.mode, snapshot?.mode === 'Friend' ? snapshot.clientExecutablePath : '', discovery])

  useEffect(() => {
    if (snapshot?.mode === 'Friend' && snapshot.endpoint && !friendHostAddress)
      setFriendHostAddress(hostAddress(snapshot.endpoint))
  }, [snapshot?.mode, snapshot?.mode === 'Friend' ? snapshot.endpoint : ''])

  const edit = (next: Settings) => { setDraft(next); dirtyRef.current = true; setDirty(true) }
  const copyText = async (value: string, label: string) => {
    try {
      await navigator.clipboard.writeText(value)
      setNotice({ good: true, text: `${label} copied.` })
    } catch {
      setNotice({ good: false, text: `Could not copy ${label.toLowerCase()}. Select and copy it instead.` })
    }
  }
  const copyGameDetails = async (profile: Profile, address: string) => {
    setPending(`game-details-${profile.id}`)
    try {
      const response = await fetch(`/api/local/profiles/${profile.id}/game-password/reveal`, { method: 'POST', headers: localHeaders })
      const result: BasicResult & { password?: string } = await response.json()
      if (!response.ok || !result.ok || !result.password) {
        setNotice({ good: false, text: result.message ?? 'Could not read the saved game password.' })
        return
      }
      await copyText(`Valheim Join IP: ${address}\nGame password: ${result.password}`, 'Game details')
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
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
  const checkPorts = async () => {
    try {
      const response = await fetch('/api/local/network/ports', { cache: 'no-store' })
      if (response.ok) setPortDiagnostics(await response.json())
    } catch { /* The regular refresh will retry without replacing the current evidence. */ }
  }
  const issueInvite = async (profileId: string, refresh = false): Promise<string | null> => {
    if (refresh && !window.confirm('Refresh this server code? All Friend PCs paired to this server will lose access and need to connect again.')) return null
    setPending('invite')
    setNotice(null)
    try {
      const response = await fetch(`/api/local/servers/${profileId}/invite`, { method: 'POST', headers: localHeaders,
        body: JSON.stringify({ refresh, canStart: deviceStart, enableConnections: true }) })
      const result: { ok: boolean; code: string; message: string; password?: string; listenerActive?: boolean; listenerWarning?: string } = await response.json()
      setNotice({ good: result.ok && result.listenerActive !== false,
        text: result.listenerWarning || result.message })
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
        return result.password
      }
      return null
    } catch (error) { setNotice({ good: false, text: String(error) }); return null }
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
  const savePlayerId = async (id: string) => {
    setPending(id)
    try {
      const result = await change<BasicResult>(`/api/local/devices/${id}/player-id`, 'PUT',
        { platformUserId: playerIds[id] ?? '' })
      setNotice({ good: result.ok, text: result.message })
      const latest = await fetch('/api/local/companion')
      if (latest.ok) setCompanion(await latest.json())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const setDevicePermissions = async (device: Device, canStop: boolean) => {
    setPending(device.id)
    try {
      const result = await change<BasicResult>(`/api/local/devices/${device.id}/permissions`, 'PUT',
        { canStart: device.canStart, canStop })
      setNotice({ good: result.ok, text: result.message })
      const latest = await fetch('/api/local/companion')
      if (latest.ok) setCompanion(await latest.json())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const createPermittedList = async (id: string) => {
    setPending(id)
    try {
      const result = await change<BasicResult>(`/api/local/profiles/${id}/permitted-list`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      const latest = await fetch('/api/local/companion')
      if (latest.ok) setCompanion(await latest.json())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const pairFriend = async () => {
    if (!friendInvite) {
      setNotice({ good: false, text: 'Paste the invite from your friend.' })
      return
    }
    setPending('pair')
    try {
      const result = await change<BasicResult>('/api/local/friend/pair', 'POST', { invitation: friendInvite,
        clientExecutablePath: friendClientPath, hostAddress: friendHostAddress || null })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        friendClientEdited.current = false
        setFriendInvite('')
        setShowPairing(false)
        await fetch('/api/local/friend/poll', { method: 'POST', headers: localHeaders })
        setSnapshot(await readSnapshot())
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
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
  const saveFriendClientPath = async () => {
    setPending('client-path')
    try {
      const result = await change<BasicResult>('/api/local/friend/client-path', 'POST', { path: friendClientPath })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) {
        friendClientEdited.current = false
        await fetch('/api/local/friend/poll', { method: 'POST', headers: localHeaders })
        setSnapshot(await readSnapshot())
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const friendAction = async (id: string, action: 'start' | 'stop') => {
    setPending(id)
    try {
      const result = await change<BasicResult>(`/api/local/friend/${id}/${action}`, 'POST')
      setNotice({ good: result.ok, text: result.message })
      setSnapshot(await readSnapshot())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const saveSetup = async (startAfterSave = false) => {
    if (!draft) return
    const unmet = draft.profiles.flatMap(item => getSetupIssues(item,
      snapshot?.mode === 'Host' && !!snapshot.passwordConfigured[item.id], passwords[item.id] ?? '')
      .map(message => ({ id: item.id, message })))
    if (unmet.length) {
      setActiveProfileId(unmet[0].id)
      setNotice({ good: false, text: unmet[0].message })
      return
    }
    const profile = draft.profiles.find(item => item.id === activeProfileId) ?? draft.profiles[0]
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
        const next = await readSnapshot()
        setSnapshot(next)
        setDraft(next.mode === 'Host' ? next.settings : null)
        if (next.mode === 'Host') setShowSetup(next.settings.profiles.length === 0)
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
      if (result.ok && key === 'save') { setDraft(result.snapshot.settings); dirtyRef.current = false; setDirty(false) }
      if (result.ok) void checkPorts()
    } catch (error) {
      setNotice({ good: false, text: String(error) })
    } finally { setPending('') }
  }

  const updateProfile = (id: string, patch: Partial<Profile>) => {
    if (!draft) return
    edit({ ...draft, profiles: draft.profiles.map(profile => profile.id === id ? { ...profile, ...patch } : profile) })
  }

  const addProfile = () => {
    if (!draft || snapshot?.mode !== 'Host') return
    const id = crypto.randomUUID()
    edit({ ...draft, profiles: [...draft.profiles, { id, kind: 'Valheim', name: '', serverName: '', crossplay: false,
      publicListing: false, worldId: '', worldSource: 'New',
      worldDirectory: `${snapshot.managedWorldsRoot}\\${id.replaceAll('-', '')}`, gamePort: 2456, executablePath: '' }] })
    setActiveProfileId(id)
    setShowSetup(true)
    window.setTimeout(() => setupRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 0)
    if (!discovery) void scanValheim()
  }

  const openSetup = (id: string) => {
    setActiveProfileId(id)
    setShowSetup(true)
    window.setTimeout(() => setupRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 0)
  }
  const inviteFriend = async (profileId: string) => {
    const request = ++inviteLoad.current
    setInviteProfileId(profileId)
    setInvitation('')
    setPending('invite')
    try {
      const response = await fetch(`/api/local/servers/${profileId}/invite/current`, { method: 'POST', headers: localHeaders })
      if (!response.ok) throw new Error('Could not read this server code.')
      const result: { password: string | null; canStart: boolean } = await response.json()
      if (request === inviteLoad.current) {
        setDeviceStart(result.canStart)
        if (result.password) {
          setInvitation(result.password)
          await copyText(result.password, 'Invite')
        } else {
          setPending('')
          const created = await issueInvite(profileId)
          if (created && request === inviteLoad.current) {
            try { await navigator.clipboard.writeText(created) }
            catch { setNotice({ good: false, text: 'Invite ready, but it could not be copied. Choose Copy again.' }) }
          }
        }
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
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
  const setupIssues = draft?.profiles.flatMap(profile => getSetupIssues(profile,
    snapshot?.mode === 'Host' && !!snapshot.passwordConfigured[profile.id], passwords[profile.id] ?? '')
    .map(message => ({ id: profile.id, name: profile.name || profile.serverName || 'New server', message }))) ?? []
  const savedProfiles = snapshot?.mode === 'Host' ? snapshot.settings.profiles : []
  const activeRuns = snapshot?.mode === 'Host'
    ? snapshot.runs.filter(run => ['Process running', 'Starting', 'Ready'].includes(run.state)).length : 0

  return <div className="shell">
    <header className="topbar">
      <div className="brand"><span className="brand-mark">T</span><div><strong>TogetherServer</strong><small>Local companion</small></div></div>
      <div className="topbar-actions"><div className="header-tools"><button className="update-check" aria-label="Check for updates" disabled={updateBusy || !!pending} onClick={() => void checkUpdate()} title={update?.message ?? 'Check for updates'}><Icon name="refresh" />{update?.state === 'Available' ? `v${update.latestVersion} ready` : `v${update?.currentVersion ?? '...'}`}</button><details className="app-menu"><summary aria-label="App settings" title="App settings"><Icon name="settings" size={19} /></summary><div className="app-menu-panel"><strong>App settings</strong><label className="check-row"><input type="checkbox" checked={desktopPreferences?.launchAtLogin ?? false} disabled={!desktopPreferences?.available || !desktopPreferences.startupAvailable || desktopBusy} onChange={event => void saveDesktopPreference({ launchAtLogin: event.target.checked })} />Open at Windows sign-in</label><small>Starts quietly in the tray.</small><label className="check-row"><input type="checkbox" checked={desktopPreferences?.closeToTray ?? false} disabled={!desktopPreferences?.available || desktopBusy} onChange={event => void saveDesktopPreference({ closeToTray: event.target.checked })} />Close to tray</label><small>Hosting and Friend checks keep running.</small><button className="app-menu-quit" disabled={!desktopPreferences?.available} onClick={() => void quitApp()}>Quit TogetherServer</button></div></details></div><nav className="mode-switch" aria-label="App pages">
        <button className={snapshot?.mode === 'Host' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Host'} onClick={() => void switchMode('host')}>My server</button>
        <button className={snapshot?.mode === 'Friend' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Friend'} onClick={() => void switchMode('friend')}>Join a friend</button>
      </nav></div>
    </header>

    <main>
      <div className="hero hero-compact"><div><h1>{snapshot?.mode === 'Friend' ? "Join a friend's server" : 'My Valheim server'}</h1>
        <p>{snapshot?.mode === 'Friend' ? 'Paste one invite to connect. This does not close a server you host on this PC.' : savedProfiles.length === 0 ? 'Choose a world and password to get started.' : activeRuns ? 'Your server is running.' : 'Your server is ready to start.'}</p></div>
        {snapshot?.mode === 'Host' && savedProfiles.length > 0 && <div className="hero-badge">{activeRuns ? 'Running' : 'Offline'}<small>{savedProfiles.length > 1 ? `${savedProfiles.length} servers saved` : savedProfiles[0].name}</small></div>}
      </div>

      {update?.state === 'Available' && <div className="update-notice" role="status"><div><strong>Update available · v{update.latestVersion}</strong><span>Download from the TogetherServer GitHub release, verify it, then restart. Stop hosted servers first.</span></div><button disabled={updateBusy || !!pending || dirty || activeRuns > 0} title={dirty ? 'Save setup changes before updating.' : activeRuns > 0 ? 'Stop hosted servers before updating.' : undefined} onClick={() => void installUpdate()}>{updateBusy ? 'Preparing update…' : 'Update and restart'}</button></div>}

      {loadError && <div className="notice bad" role="alert">Connection to this local app failed: {loadError}</div>}
      {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
      {!snapshot && !loadError && <section className="panel">Loading local state…</section>}

      {snapshot?.mode === 'Friend' && <>
        <section className="panel friend-panel friend-primary">
          <div className="section-heading"><div><h2>{snapshot.endpoint && !showPairing ? 'Your connection' : 'Paste your invite'}</h2><p>{snapshot.endpoint && !showPairing ? snapshot.detail : "Ask the Host for this server's current code."}</p></div></div>
          {snapshot.endpoint && !showPairing ? <>
            <div className="compact-status"><span className={`status ${snapshot.state === 'Connected' ? 'running' : 'unknown'}`}>{snapshot.state === 'Disconnected/Unknown' ? 'Connection unknown' : snapshot.state}</span>
              <span>{snapshot.lastConnectedUtc ? `Last reached ${new Date(snapshot.lastConnectedUtc).toLocaleTimeString()}` : 'Waiting for a reply from the Host'}</span></div>
            <div className="actions"><button className="secondary" disabled={!!pending} onClick={() => void checkFriendConnection()}>{pending === 'poll' ? 'Checking…' : 'Check connection'}</button>
              <button className="text-button" onClick={() => { setShowPairing(true); setFriendHostAddress(''); setFriendInvite('') }}>Use another invite</button></div>
          </> : <>
            <form className="join-row" onSubmit={event => { event.preventDefault(); void pairFriend() }}>
              <label className="invite-input">Invite code<input type="password" autoComplete="off" value={friendInvite} onChange={event => setFriendInvite(event.target.value.trim())} placeholder="Paste the invite here" /></label>
              <button disabled={!!pending || !friendInvite}>{pending === 'pair' ? 'Connecting…' : 'Connect'}</button>
            </form>
            <details className="advanced-block"><summary>Using an older invite?</summary><label>Host IP<input value={friendHostAddress} onChange={event => setFriendHostAddress(event.target.value.trim())} placeholder="123.45.67.89" /><small>Older TS1 invites need the Host IP. New invites already include it.</small></label></details>
          </>}
          {snapshot.endpoint && snapshot.localGameRunning === null && <p className="warning-text">Valheim was not found. Choose its install path under Game check.</p>}
          {snapshot.endpoint && <details className="advanced-block"><summary>Game check · {snapshot.localGameRunning === null ? 'Unknown' : snapshot.localGameRunning ? 'Running' : 'Closed'}</summary>
            <p className="helper-text">This app checks whether Valheim is running; it does not open the game.</p>
            <div className="settings-grid"><label>Valheim game client<input value={friendClientPath} onChange={event => { friendClientEdited.current = true; setFriendClientPath(event.target.value) }} placeholder="Auto-detecting Steam installation…" /></label></div>
            {discovery && discovery.clients.length > 1 && <div className="choices"><strong>Valheim installs found</strong>{discovery.clients.map(item => <div className="choice" key={item.executablePath}><span>{item.executablePath}</span><button className="secondary" onClick={() => { friendClientEdited.current = true; setFriendClientPath(item.executablePath) }}>Use this install</button></div>)}</div>}
            <button className="secondary" disabled={!snapshot.endpoint || !!pending} onClick={() => void saveFriendClientPath()}>Save game check</button>
          </details>}
        </section>
        {snapshot.endpoint && !showPairing && snapshot.profiles.length > 0 && <section className="panel">
          <div className="section-heading"><div><h2>Valheim server</h2></div></div>
          {snapshot.profiles.map(profile => <article className="profile-card" key={profile.id}>
            <div className="profile-top"><h3>{profile.name}</h3><span className={`status ${profile.state === 'Ready' ? 'running' : profile.state === 'Offline' ? 'offline' : 'unknown'}`}>{profile.state}</span></div>
            <div className="actions server-actions">
              {profile.state === 'Offline' && snapshot.state === 'Connected' && snapshot.canStart && <button disabled={!!pending} onClick={() => void friendAction(profile.id, 'start')}><Icon name="play" />Start server</button>}
              {profile.state === 'Ready' && profile.joinAddress && <button className="secondary" onClick={() => void copyText(profile.joinAddress!, 'Game address')}><Icon name="copy" />Copy game address</button>}
              {profile.state === 'Ready' && snapshot.state === 'Connected' && snapshot.canStop && profile.canStopNow && <button className="secondary" disabled={!!pending} onClick={() => void friendAction(profile.id, 'stop')}><Icon name="stop" />Stop server</button>}
            </div>
            {snapshot.canStop && profile.state === 'Ready' && !profile.canStopNow && <p className="helper-text">Stop unavailable: {profile.stopReason}</p>}
            {profile.state === 'Offline' && !snapshot.canStart && snapshot.state === 'Connected' && <p className="helper-text">The Host has not allowed this PC to start the server.</p>}
            {profile.state === 'Ready' && !profile.joinAddress && <p className="helper-text">The Host has not found a current game address yet.</p>}
          </article>)}
        </section>}
      </>}

      {snapshot?.mode === 'Host' && draft && <>
        {savedProfiles.length > 0 && <>
        <section className="panel">
          <div className="section-heading"><div><h2>My server</h2></div></div>
          <div className="profile-list">
            {snapshot.settings.profiles.map(profile => {
              const status = snapshot.runs.find(run => run.profileId === profile.id)
              return <article className="profile-card" key={profile.id}>
                <div className="profile-top"><div><h3>{profile.name}</h3><p>World {profile.worldId}</p></div>
                  <span className={`status ${status?.state === 'Process running' || status?.state === 'Ready' ? 'running' : status?.state === 'Offline' ? 'offline' : 'unknown'}`}>{status?.state ?? 'Unknown'}</span></div>
                <ServerReadiness profileId={profile.id} status={status?.state ?? 'Unknown'} ports={portDiagnostics}
                  busy={!!pending} onRefresh={() => void checkPorts()} />
                <div className="actions server-actions">
                  {status?.state === 'Offline' && <button disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/start`, 'POST')}><Icon name="play" />Start server</button>}
                  {['Process running', 'Starting', 'Ready'].includes(status?.state ?? '') && <button disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/stop`, 'POST')}><Icon name="stop" />Stop server</button>}
                  {profile.kind === 'Valheim' && status?.state === 'Ready' && detectedGameIp && <button className="secondary" disabled={!!pending} onClick={() => void copyGameDetails(profile, `${detectedGameIp}:${profile.gamePort}`)}><Icon name="copy" />Game details</button>}
                  <button className="secondary" disabled={!!pending || dirty || !friendAppAddress} onClick={() => void inviteFriend(profile.id)}><Icon name="invite" />Invite friend</button>
                </div>
                {inviteProfileId === profile.id && <div className="inline-invite">
                  {invitation ? <><div className="invite-ready"><span><Icon name="check" /></span><div><strong>Invite ready</strong><p>The current server code is ready to paste into TogetherServer on each Friend PC.</p></div></div>
                    <div className="actions"><button onClick={() => void copyText(invitation, 'Invite code')}><Icon name="copy" />Copy again</button>
                      <button className="secondary" disabled={!!pending} onClick={() => void issueInvite(profile.id, true)}><Icon name="refresh" />Refresh access</button>
                      <button className="text-button" onClick={() => setInviteProfileId('')}>Done</button></div>
                    <details><summary>Show code and access note</summary><p>One code for {profile.name}. Refreshing it revokes every paired PC for this server.</p><textarea readOnly rows={3} value={invitation} /></details></>
                    : <p className="helper-text">Preparing this server's invite…</p>}
                </div>}
                {!friendAppAddress && <p className="helper-text"><Icon name="warning" /> A public address is still being checked. You can start now and invite when it appears.</p>}
                {profile.kind === 'Valheim' && status?.state === 'Ready' && !detectedGameIp && <p className="helper-text">Your public game address is not available yet. Check Network in Settings.</p>}
                <details className="advanced-block"><summary>More server options</summary>
                  <p className="helper-text">Playing on this PC? Join <code>127.0.0.1:{profile.gamePort}</code>.</p>
                  <div className="actions"><button className="secondary" disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/health`, 'POST')}>Check server health</button>
                    <button className="secondary" disabled={!!pending || status?.state !== 'Offline'} onClick={() => openSetup(profile.id)}>Edit setup</button>
                    {(status?.state === 'Unknown' || status?.state === 'Failed') && <button className="text-button" disabled={!!pending || dirty} onClick={() => {
                      if (window.confirm('Clear this unresolved run record only after verifying the original process is stopped?'))
                        void run(profile.id, `/api/local/profiles/${profile.id}/forget`, 'POST')
                    }}>Clear unresolved record</button>}</div>
                </details>
              </article>
            })}
          </div>
          {dirty && <p className="warning-text">Save your setup changes before starting or stopping a server.</p>}
          {companion?.devices.some(device => device.paired && !device.revoked) && <div className="compact-status"><span>Remote control: {draft.remoteControlsEnabled ? 'On' : 'Paused'}</span>
            <button className="secondary" disabled={!!pending || dirty} onClick={() => void run('save', '/api/local/settings', 'PUT', { ...draft, remoteControlsEnabled: !draft.remoteControlsEnabled })}>{draft.remoteControlsEnabled ? 'Pause remote controls' : 'Allow remote controls'}</button></div>}
          <details className="advanced-block"><summary>More hosting settings</summary><button className="secondary" disabled={!!pending || dirty} onClick={addProfile}>Add another server</button>
            <p className="helper-text">Limit: {draft.maxConcurrentServers} managed server{draft.maxConcurrentServers === 1 ? '' : 's'}. A Ready signal is not a verified game join.</p></details>
        </section>
        </>}

        {(showSetup || savedProfiles.length === 0) && <section ref={setupRef} className="panel settings-panel">
          <div className="section-heading"><span className="section-icon"><Icon name="server" /></span><div><h2>{savedProfiles.length ? 'Edit server setup' : 'Start a Valheim server'}</h2><p>Name the world, add its password, then start. TogetherServer finds the installed server automatically.</p></div></div>
          <div className="setup-header">
            {draft.profiles.length > 1 && <label>Editing server<select value={editedProfile?.id ?? ''} onChange={event => setActiveProfileId(event.target.value)}>{draft.profiles.map(profile => <option key={profile.id} value={profile.id}>{profile.name || profile.serverName || profile.worldId || 'New server'}</option>)}</select></label>}
            {savedProfiles.length > 0 && !dirty && <button className="secondary" onClick={() => setShowSetup(false)}>Done</button>}
          </div>
          {!editedProfile && <div className="empty"><p>Start with one Valheim world.</p><div className="actions"><button onClick={addProfile}>Set up a server</button></div></div>}
          {draft.profiles.filter(profile => profile.id === editedProfile?.id).map(profile => <div className="profile-form" key={profile.id}>
            <div className="setup-step world-step"><h3><Icon name="game" /> World</h3>
              {profile.kind === 'Valheim' && <div className="choice-pills">
                <button className={profile.worldSource === 'New' ? 'selected' : 'secondary'} onClick={() => updateProfile(profile.id, { worldSource: 'New', worldId: '', name: '', serverName: '', worldDirectory: `${snapshot.managedWorldsRoot}\\${profile.id.replaceAll('-', '')}` })}>Create new</button>
                <button className={profile.worldSource === 'Existing' ? 'selected' : 'secondary'} onClick={() => updateProfile(profile.id, { worldSource: 'Existing', worldId: '', name: '', serverName: '', worldDirectory: '' })}>Use existing</button>
              </div>}
              {profile.kind === 'Valheim' && profile.worldSource === 'Existing' && <>
                {profile.worldId && profile.worldDirectory && <p className="selection-summary">Copy ready: <strong>{profile.worldId}</strong>. Your original save stays separate.</p>}
                {discovery && <div className="choices"><strong>Worlds found on this PC</strong>{discovery.worlds.length === 0 ? <p>None found. Browse to a world folder below.</p> : discovery.worlds.map(world => <div className="choice" key={world.saveRoot + world.sourceFolder + world.name}><span>{world.name} <small>{world.format === 'Steam cloud folder' ? 'Steam Cloud' : 'Local save'} · {world.saveRoot}</small></span><button className="secondary" disabled={!!pending} onClick={() => void importWorld(profile, world.saveRoot, world.name, world.sourceFolder)}>Copy world</button></div>)}</div>}
                <div className="setup-tools"><button className="secondary" disabled={!!pending} onClick={() => void browseWorld(profile, true)}>{pending === profile.id ? 'Browsing…' : 'Browse for a world folder'}</button></div>
                <p className="helper-text">Close Valheim and let Steam finish syncing before copying a cloud world.</p>
                <details className="advanced-block"><summary>Older saves and custom paths</summary>
                  <button className="secondary" disabled={!!pending} onClick={() => void browseWorld(profile)}>Choose an older .db or .fwl file</button>
                  <div className="settings-grid"><label>Local save root<input value={sourceRoots[profile.id] ?? ''} onChange={event => setSourceRoots(current => ({ ...current, [profile.id]: event.target.value }))} placeholder="C:\\...\\IronGate\\Valheim" /></label><label>World ID<input value={profile.worldId} onChange={event => updateProfile(profile.id, { worldId: event.target.value })} placeholder="World folder name" /></label></div>
                  <button className="secondary" disabled={!!pending || !sourceRoots[profile.id] || !profile.worldId} onClick={() => void importWorld(profile, sourceRoots[profile.id], profile.worldId)}>Copy named world</button>
                </details>
              </>}
              {profile.kind === 'Valheim' && profile.worldSource === 'New' && <div className="quick-setup-fields"><label className="invite-input">World name<input value={profile.worldId} onChange={event => updateProfile(profile.id, { worldId: event.target.value, name: event.target.value, serverName: event.target.value })} placeholder="My Valheim world" /><small>Stored in TogetherServer's private data.</small></label>
                <label className="invite-input">Game password<input type="password" autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => setPasswords(current => ({ ...current, [profile.id]: event.target.value }))} placeholder={snapshot.passwordConfigured[profile.id] ? 'Saved already; leave blank to keep it' : '5 or more characters'} /><small>Friends use this inside Valheim.</small></label></div>}
              {profile.kind === 'Fixture' && <div className="settings-grid"><label>Test profile name<input value={profile.name} onChange={event => updateProfile(profile.id, { name: event.target.value })} /></label><label>World ID<input value={profile.worldId} onChange={event => updateProfile(profile.id, { worldId: event.target.value })} /></label><label className="wide">Disposable directory<input value={profile.worldDirectory} onChange={event => updateProfile(profile.id, { worldDirectory: event.target.value })} /></label></div>}
              {profile.kind === 'Valheim' && profile.worldSource === 'Existing' && <label className="invite-input setup-password">Game password<input type="password" autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => setPasswords(current => ({ ...current, [profile.id]: event.target.value }))} placeholder={snapshot.passwordConfigured[profile.id] ? 'Saved already; leave blank to keep it' : '5 or more characters'} /></label>}
            </div>
            <div className="setup-step server-step"><h3><Icon name="search" /> Server app</h3>
              <div className="server-step-content">{profile.kind === 'Valheim' ? <>
                {profile.executablePath ? <p className="selection-summary"><Icon name="check" /> Valheim Dedicated Server found <small>{profile.executablePath}</small></p> : <>
                  {discovery && <div className="choices">{discovery.installations.length === 0 ? <p>Valheim Dedicated Server was not found.</p> : discovery.installations.map(item => <div className="choice" key={item.executablePath}><span>{item.executablePath}</span><button className="secondary" onClick={() => updateProfile(profile.id, { executablePath: item.executablePath })}>Use this install</button></div>)}</div>}
                  <div className="setup-tools"><button className="secondary" disabled={!!pending} onClick={() => void scanValheim()}>{pending === 'scan' ? 'Searching…' : 'Search this PC'}</button><button className="secondary" disabled={!!pending} onClick={() => void browseServer(profile)}>Browse for server</button>{discovery?.installations.length === 0 && <a href="steam://install/896660">Open install in Steam</a>}</div>
                  <p className="helper-text">Steam handles installation and any terms when you open it.</p>
                </>}
              </> : <label>Fixture executable path<input value={profile.executablePath} onChange={event => updateProfile(profile.id, { executablePath: event.target.value })} placeholder="C:\\...\\TogetherServer.Fixture.exe" /></label>}</div>
            </div>
            <details className="advanced-block"><summary>Advanced server settings</summary>
              <div className="settings-grid"><label>Game type<select value={profile.kind} onChange={event => updateProfile(profile.id, { kind: event.target.value as Profile['kind'] })}><option value="Valheim">Valheim</option><option value="Fixture">Synthetic test fixture</option></select></label><label>Game UDP start port<input type="number" value={profile.gamePort} onChange={event => updateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label>{profile.kind === 'Valheim' && <><label>Server listing name<input value={profile.serverName} onChange={event => updateProfile(profile.id, { serverName: event.target.value, name: event.target.value })} /></label><label className="wide">Installed server path<input value={profile.executablePath} onChange={event => updateProfile(profile.id, { executablePath: event.target.value })} /></label><label className="wide">Save directory<input value={profile.worldDirectory} onChange={event => updateProfile(profile.id, { worldDirectory: event.target.value })} /></label></>}</div>
              {profile.kind === 'Valheim' && <div className="device-options"><label className="check-row"><input type="checkbox" checked={profile.crossplay} onChange={event => updateProfile(profile.id, { crossplay: event.target.checked })} /> Crossplay relay</label><label className="check-row"><input type="checkbox" checked={profile.publicListing} onChange={event => updateProfile(profile.id, { publicListing: event.target.checked })} /> Show in server list</label></div>}
              {profile.worldDirectory && <p className="helper-text">Server save location: <code>{profile.worldDirectory}</code></p>}
            </details>
          </div>)}
          {editedProfile && <>{savedProfiles.length > 0 && <details className="advanced-block"><summary>More setup options</summary><div className="settings-grid"><label>Maximum managed servers<input type="number" min="1" max="16" value={draft.maxConcurrentServers} onChange={event => edit({ ...draft, maxConcurrentServers: Number(event.target.value) })} /></label></div><button className="text-button danger" onClick={() => {
            const profiles = draft.profiles.filter(item => item.id !== editedProfile.id)
            edit({ ...draft, profiles, companionListeningEnabled: profiles.length > 0 && draft.companionListeningEnabled,
              remoteControlsEnabled: profiles.length > 0 && draft.remoteControlsEnabled })
          }}>Remove this server</button></details>}
          {setupIssues.length > 0 && <p className="helper-text" role="status">To continue: {setupIssues[0].message}</p>}
          <div className="save-row"><span>{setupIssues.length ? 'Finish the highlighted setup item.' : 'Ready to start.'}</span><div className="actions"><button className="secondary" disabled={setupIssues.length > 0 || (!dirty && !passwords[editedProfile.id]) || !!pending} onClick={() => void saveSetup()}>Save only</button><button disabled={setupIssues.length > 0 || (!dirty && !passwords[editedProfile.id]) || !!pending} onClick={() => void saveSetup(true)}><Icon name="play" />{pending === 'save' ? 'Starting…' : 'Start server'}</button></div></div></>}
        </section>}
        {savedProfiles.length > 0 && <>
        <section className="panel">
          <details className="advanced-block main-settings"><summary>Settings and safety</summary>
            <div className="settings-content">
              <h3>Connection checks</h3>
              <p>Game address: {detectedGameIp ? `${detectedGameIp} detected, friend join untested` : 'unavailable'}. Friend app: {companion?.listenerActive ? 'listening on this PC, public route untested' : 'off'}.</p>
              {companion?.listenerWarning && <p className="warning-text">{companion.listenerWarning}</p>}
              {publicIpDetection && !publicIpDetection.ok && <p className="warning-text">{publicIpDetection.message}</p>}
              <div className="actions"><button className="secondary" disabled={detectingPublicIp} onClick={() => void detectPublicIp()}>{detectingPublicIp ? 'Checking…' : 'Refresh public address'}</button>
                {detectedGameIp && <button className="secondary" onClick={() => void copyText(`${detectedGameIp}:${savedProfiles[0].gamePort}`, 'Game address')}>Copy game address</button>}</div>
              <p className="helper-text">A Friend on another network must test the app connection and Valheim game join separately. Router and firewall changes remain yours to approve.</p>
              <label className="check-row"><input type="checkbox" checked={draft.companionListeningEnabled} onChange={event => edit({ ...draft, companionListeningEnabled: event.target.checked, remoteControlsEnabled: event.target.checked ? draft.remoteControlsEnabled : false })} /> Allow Friend app connections</label>

              <h3>Safe remote Stop</h3>
              <p>Valheim must admit only the owner and paired Friend PCs. Set their Valheim player IDs, then create a player-only access list while the server is off. Stop works only when every allowed PC reports its game closed.</p>
              <label>My Valheim player ID (only if I play)<input value={draft.ownerPlatformUserId} onChange={event => edit({ ...draft, ownerPlatformUserId: event.target.value.trim() })} placeholder="V_123456789" /><small>Find it in Valheim's F2 panel. Leave blank if this PC never joins the server.</small></label>
              {companion?.devices.filter(device => !device.revoked).map(device => <div className="device" key={device.id}>
                <div><strong>{device.name} · {savedProfiles.find(profile => profile.id === device.profileId)?.name ?? 'Legacy access'}</strong><small>{device.paired ? device.gameRunning === null ? 'Game check unknown' : device.gameRunning ? 'Game running' : 'Game closed' : 'Invite pending'} · Start {device.canStart ? 'allowed' : 'off'} · Stop {device.canStop ? 'allowed' : 'off'}</small>
                  <label>Valheim player ID<input value={playerIds[device.id] ?? ''} onChange={event => setPlayerIds(current => ({ ...current, [device.id]: event.target.value }))} placeholder="V_123456789" /></label></div>
                <div className="actions"><button className="secondary" disabled={!!pending} onClick={() => void savePlayerId(device.id)}>Save ID</button>
                  {device.paired && <button className="secondary" disabled={!!pending || (!device.canStop && !Object.values(companion?.stopSafety ?? {}).some(item => item.available))} onClick={() => void setDevicePermissions(device, !device.canStop)}>{device.canStop ? 'Remove Stop access' : 'Allow Stop'}</button>}</div>
              </div>)}
              {savedProfiles.filter(profile => profile.kind === 'Valheim').map(profile => <div className="safety-status" key={profile.id}><strong>{profile.name}: {companion?.stopSafety?.[profile.id]?.available ? 'Remote Stop ready' : 'Remote Stop waiting'}</strong>
                <p>{companion?.stopSafety?.[profile.id]?.reason ?? 'Checking player coverage…'}</p>
                <button className="secondary" disabled={!!pending || dirty || snapshot.runs.find(run => run.profileId === profile.id)?.state !== 'Offline'} onClick={() => void createPermittedList(profile.id)}>Create player-only access list</button>
                <small>Existing lists are never overwritten. Start or restart the server after creating one.</small></div>)}

              <h3>Advanced network and game paths</h3>
              <div className="settings-grid companion-fields">
                <label>Friend app TCP port<input type="number" min="1024" max="65535" value={draft.companionPort} onChange={event => {
                  const port = Number(event.target.value)
                  let endpoint = draft.companionEndpoint
                  if (endpoint) try { const url = new URL(endpoint); url.port = String(port); endpoint = url.origin } catch { /* Validation explains a custom endpoint. */ }
                  edit({ ...draft, companionPort: port, companionEndpoint: endpoint })
                }} /></label>
                <label>Custom HTTPS endpoint<input value={draft.companionEndpoint} onChange={event => edit({ ...draft, companionEndpoint: event.target.value.trim() })} placeholder="https://127.0.0.1:5131" /></label>
                <label>Bind IP<input value={draft.companionBindAddress} onChange={event => edit({ ...draft, companionBindAddress: event.target.value })} placeholder="127.0.0.1" /></label>
                <label>Owner Valheim game path<input value={draft.ownerClientExecutablePath} onChange={event => edit({ ...draft, ownerClientExecutablePath: event.target.value })} placeholder="Auto-detected game install" /></label>
              </div>
              {companion?.fingerprint && <p className="footnote">Pinned Host identity: <code>{companion.fingerprint}</code></p>}
              <div className="actions"><button disabled={!dirty || !!pending} onClick={() => void run('save', '/api/local/settings', 'PUT', draft)}>Save settings</button></div>
              {companion?.devices.length ? <details className="advanced-block"><summary>Manage Friend PCs ({companion.devices.length})</summary><div className="profile-list device-list">{companion.devices.map(device => <div className="device" key={device.id}>
                <div><strong>{device.name} · {savedProfiles.find(profile => profile.id === device.profileId)?.name ?? 'Legacy access'}</strong><small>{device.revoked ? 'Revoked' : device.lastHeartbeatUtc ? `Last report ${new Date(device.lastHeartbeatUtc).toLocaleTimeString()}` : device.paired ? 'No fresh report' : 'Invite pending'}</small></div>
                <div className="actions"><button className="text-button danger" disabled={!!pending || device.revoked} onClick={() => void revokeDevice(device.id)}>Revoke</button></div>
              </div>)}</div></details> : null}
            </div>
          </details>
        </section>
        </>}
      </>}
      <p className="footnote">{desktopPreferences?.closeToTray ? 'Closing the window keeps TogetherServer running in the tray. Use Quit to exit after stopping managed servers.' : 'Minimize the app to keep hosting. The title-bar close button exits after managed servers stop.'}</p>
    </main>
  </div>
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>)

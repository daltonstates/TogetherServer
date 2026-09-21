import React, { useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
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
  permittedPlayersVerified: boolean
  profiles: Profile[]
}
type Run = { profileId: string; state: string; detail: string; processId: number | null }
type HostSnapshot = { mode: 'Host'; evidence: string; settings: Settings; runs: Run[]; ownerGameRunning: boolean | null; ownerCheckedUtc: string; passwordConfigured: Record<string, boolean> }
type PublicProfile = { id: string; name: string; state: string; joinAddress: string | null }
type Device = { id: string; name: string; canStart: boolean; canStop: boolean; revoked: boolean; paired: boolean; lastHeartbeatUtc: string | null; gameRunning: boolean | null }
type CompanionInfo = { listenerActive: boolean; listenerWarning: string | null; endpoint: string; fingerprint: string | null; devices: Device[] }
type FriendSnapshot = { mode: 'Friend'; state: string; detail: string; endpoint: string; lastConnectedUtc: string | null; localGameRunning: boolean | null; remoteControlsEnabled: boolean; canStart: boolean; canStop: boolean; profiles: PublicProfile[]; clientExecutablePath: string }
type Snapshot = HostSnapshot | FriendSnapshot
type BasicResult = { ok: boolean; code: string; message: string }
type ActionResult = { ok: boolean; code: string; message: string; snapshot: HostSnapshot }
type PublicIpDetection = BasicResult & { address: string | null; snapshot?: HostSnapshot }
type Discovery = { installations: { executablePath: string; source: string }[]; clients: { executablePath: string; source: string }[]; worlds: { name: string; saveRoot: string; sourceFolder: string; format: string }[] }
type ImportResult = BasicResult & { worldDirectory: string | null }
type ServerBrowseResult = BasicResult & { executablePath?: string }
type WorldBrowseResult = BasicResult & { worldId: string | null; sourceSaveRoot: string | null; sourceFolder: string }

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
    if (!profile.serverName.trim()) issues.push('Enter a server name in step 1.')
    if (profile.worldSource === 'Existing' && (!profile.worldId || !profile.worldDirectory))
      issues.push('Find and copy a world in step 1.')
    if (profile.worldSource === 'New') {
      if (!profile.worldId.trim()) issues.push('Name the new world in step 1.')
      if (!profile.worldDirectory.trim()) issues.push('Choose an existing save folder in step 1.')
    }
    if (!profile.executablePath.trim()) issues.push('Find and select the installed server in step 2.')
    if (!hasPassword && !enteredPassword) issues.push('Enter a server password in step 3.')
    if (enteredPassword && (enteredPassword.length < 5 || enteredPassword.length > 64 || /[\x00-\x1f\x7f]/.test(enteredPassword)))
      issues.push('Use a password of 5 to 64 characters in step 3.')
  } else {
    if (!profile.name.trim()) issues.push('Enter a test profile name in step 1.')
    if (!profile.worldId.trim()) issues.push('Enter a world ID in step 1.')
    if (!profile.worldDirectory.trim()) issues.push('Choose a disposable directory in step 1.')
    if (!profile.executablePath.trim()) issues.push('Select the fixture executable in step 2.')
  }
  return issues
}

function worldChosen(profile: Profile): boolean {
  return !!(profile.worldId.trim() && profile.worldDirectory.trim() &&
    (profile.kind === 'Valheim' ? profile.serverName.trim() : profile.name.trim()))
}

function App() {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [draft, setDraft] = useState<Settings | null>(null)
  const [dirty, setDirty] = useState(false)
  const [pending, setPending] = useState('')
  const [notice, setNotice] = useState<{ good: boolean; text: string } | null>(null)
  const [loadError, setLoadError] = useState('')
  const [companion, setCompanion] = useState<CompanionInfo | null>(null)
  const [publicIpDetection, setPublicIpDetection] = useState<PublicIpDetection | null>(null)
  const [detectingPublicIp, setDetectingPublicIp] = useState(false)
  const [deviceStart, setDeviceStart] = useState(false)
  const [deviceStop, setDeviceStop] = useState(false)
  const [invitation, setInvitation] = useState('')
  const [invitationName, setInvitationName] = useState('')
  const [friendInvite, setFriendInvite] = useState('')
  const [friendHostAddress, setFriendHostAddress] = useState('')
  const [showPairing, setShowPairing] = useState(false)
  const [friendClientPath, setFriendClientPath] = useState('')
  const [passwords, setPasswords] = useState<Record<string, string>>({})
  const [discovery, setDiscovery] = useState<Discovery | null>(null)
  const [sourceRoots, setSourceRoots] = useState<Record<string, string>>({})
  const [showSetup, setShowSetup] = useState(false)
  const [activeProfileId, setActiveProfileId] = useState('')
  const [exiting, setExiting] = useState(false)
  const exited = useRef(false)
  const initialSetupSet = useRef(false)
  const setupRef = useRef<HTMLElement | null>(null)
  const shareRef = useRef<HTMLElement | null>(null)
  const friendClientEdited = useRef(false)

  useEffect(() => {
    let alive = true
    const refresh = async () => {
      if (exited.current) return
      try {
        const next = await readSnapshot()
        if (!alive) return
        setSnapshot(next)
        setLoadError('')
        setDraft(current => current ?? (next.mode === 'Host' ? next.settings : null))
        if (next.mode === 'Host') {
          if (!initialSetupSet.current) {
            setShowSetup(next.settings.profiles.length === 0)
            initialSetupSet.current = true
          }
          const response = await fetch('/api/local/companion', { cache: 'no-store' })
          if (response.ok && alive) setCompanion(await response.json())
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

  const edit = (next: Settings) => { setDraft(next); setDirty(true) }
  const copyText = async (value: string, label: string) => {
    try {
      await navigator.clipboard.writeText(value)
      setNotice({ good: true, text: `${label} copied.` })
    } catch {
      setNotice({ good: false, text: `Could not copy ${label.toLowerCase()}. Select and copy it instead.` })
    }
  }
  const copyGamePassword = async (profileId: string) => {
    setPending(`game-password-${profileId}`)
    try {
      const response = await fetch(`/api/local/profiles/${profileId}/game-password/reveal`, { method: 'POST', headers: localHeaders })
      const result: BasicResult & { password?: string } = await response.json()
      if (!response.ok || !result.ok || !result.password) {
        setNotice({ good: false, text: result.message ?? 'Could not read the saved game password.' })
        return
      }
      await copyText(result.password, 'Game password')
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
  const issueInvite = async (rotateDeviceId?: string, name = 'Friend PC', canStart = deviceStart, canStop = deviceStop) => {
    setPending('invite')
    setNotice(null)
    try {
      const response = await fetch('/api/local/devices/invite', { method: 'POST', headers: localHeaders,
        body: JSON.stringify({ name, canStart, canStop, rotateDeviceId: rotateDeviceId ?? null }) })
      const result: { ok: boolean; code: string; message: string; password?: string; deviceName?: string } = await response.json()
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
      if (result.ok && result.password) {
        setInvitation(result.password)
        setInvitationName(result.deviceName ?? 'Friend PC')
        const latest = await fetch('/api/local/companion')
        if (latest.ok) setCompanion(await latest.json())
        const host = await readSnapshot()
        if (host.mode === 'Host') {
          setSnapshot(host)
          if (!dirty) setDraft(host.settings)
        }
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const revokeDevice = async (id: string) => {
    if (!window.confirm('Revoke this Friend device now? Its next request will be denied.')) return
    setPending(id)
    try {
      const response = await fetch(`/api/local/devices/${id}/revoke`, { method: 'POST', headers: localHeaders })
      const result: { ok: boolean; code: string; message: string } = await response.json()
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
      const latest = await fetch('/api/local/companion')
      if (latest.ok) setCompanion(await latest.json())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const pairFriend = async () => {
    if (!friendHostAddress || !friendInvite) {
      setNotice({ good: false, text: 'Enter the Host IP and paste the password from the Host PC.' })
      return
    }
    setPending('pair')
    try {
      const result = await change<BasicResult>('/api/local/friend/pair', 'POST', { invitation: friendInvite,
        clientExecutablePath: friendClientPath, hostAddress: friendHostAddress })
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
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
      setNotice({ good: next.state === 'Connected' || next.state === 'Disabled', text: `${next.state}: ${next.detail}` })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const saveFriendClientPath = async () => {
    setPending('client-path')
    try {
      const result = await change<BasicResult>('/api/local/friend/client-path', 'POST', { path: friendClientPath })
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
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
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
      setSnapshot(await readSnapshot())
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const saveSetup = async () => {
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
        if (!result.ok) { setNotice({ good: false, text: `${result.code}: ${result.message}` }); return }
        setDraft(result.snapshot.settings)
        setDirty(false)
      }
      if (profile?.kind === 'Valheim' && password) {
        result = await change<ActionResult>(`/api/local/profiles/${profile.id}/password`, 'POST', { password })
        setSnapshot(result.snapshot)
        if (!result.ok) { setNotice({ good: false, text: `${result.code}: ${result.message}` }); return }
        setPasswords(current => ({ ...current, [profile.id]: '' }))
      }
      const passwordWasConfigured = snapshot?.mode === 'Host' && profile ? snapshot.passwordConfigured[profile.id] : false
      const needsPassword = profile?.kind === 'Valheim' && !password && !passwordWasConfigured
      setNotice({ good: true, text: needsPassword ? 'Settings saved. Add a server password to start Valheim.' : 'Setup saved. Start your server below.' })
      if (!needsPassword && profile) {
        setShowSetup(false)
        window.scrollTo({ top: 0, behavior: 'smooth' })
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const switchMode = async (mode: 'host' | 'friend') => {
    if (dirty && !window.confirm('Discard unsaved Host settings and switch mode?')) return
    setPending('mode')
    setNotice(null)
    try {
      const response = await fetch(`/api/local/mode/${mode}`, { method: 'POST', headers: localHeaders })
      const result: { ok: boolean; code: string; message: string } = await response.json()
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
      if (result.ok) {
        const next = await readSnapshot()
        setSnapshot(next)
        setDraft(next.mode === 'Host' ? next.settings : null)
        if (next.mode === 'Host') setShowSetup(next.settings.profiles.length === 0)
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
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
      if (result.ok && key === 'save') { setDraft(result.snapshot.settings); setDirty(false) }
    } catch (error) {
      setNotice({ good: false, text: String(error) })
    } finally { setPending('') }
  }

  const updateProfile = (id: string, patch: Partial<Profile>) => {
    if (!draft) return
    edit({ ...draft, profiles: draft.profiles.map(profile => profile.id === id ? { ...profile, ...patch } : profile) })
  }

  const addProfile = () => {
    if (!draft) return
    const id = crypto.randomUUID()
    edit({ ...draft, profiles: [...draft.profiles, { id, kind: 'Valheim', name: '', serverName: '', crossplay: false,
      publicListing: false, worldId: '', worldSource: 'Existing', worldDirectory: '', gamePort: 2456, executablePath: '' }] })
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

  const scanValheim = async () => {
    setPending('scan')
    try {
      const response = await fetch('/api/local/valheim/discover', { cache: 'no-store' })
      if (!response.ok) throw new Error(`Discovery returned ${response.status}`)
      const result: Discovery = await response.json()
      setDiscovery(result)
      setNotice({ good: true, text: `Found ${result.installations.length} server path candidate(s) and ${result.worlds.length} world save(s).` })
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

  const quitApp = async () => {
    if (dirty && !window.confirm('Discard unsaved settings and quit TogetherServer?')) return
    setPending('quit')
    try {
      const result = await change<BasicResult>('/api/local/quit', 'POST')
      if (result.ok) { exited.current = true; setExiting(true) }
      else setNotice({ good: false, text: `${result.code}: ${result.message}` })
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }

  if (exiting) return <div className="shell"><main><section className="panel"><h1>TogetherServer is closing</h1><p>This window will close. Double-click TogetherServer.exe to open the app again.</p></section></main></div>

  const editedProfile = draft?.profiles.find(profile => profile.id === activeProfileId) ?? draft?.profiles[0]
  const detectedGameIp = snapshot?.mode === 'Host' && snapshot.settings.publicGameIpCheckedUtc &&
    Date.now() - Date.parse(snapshot.settings.publicGameIpCheckedUtc) < 60 * 60 * 1000
    ? snapshot.settings.publicGameIp : ''
  const friendAppAddress = hostAddress(draft?.companionEndpoint ?? '') ||
    (detectedGameIp ? `${detectedGameIp}${draft?.companionPort === 5131 ? '' : `:${draft?.companionPort}`}` : '')
  const connectionView = snapshot?.mode === 'Friend'
  const setupIssues = draft?.profiles.flatMap(profile => getSetupIssues(profile,
    snapshot?.mode === 'Host' && !!snapshot.passwordConfigured[profile.id], passwords[profile.id] ?? '')
    .map(message => ({ id: profile.id, name: profile.name || profile.serverName || 'New server', message }))) ?? []
  const savedProfiles = snapshot?.mode === 'Host' ? snapshot.settings.profiles : []
  const activeRuns = snapshot?.mode === 'Host'
    ? snapshot.runs.filter(run => ['Process running', 'Starting', 'Ready'].includes(run.state)).length : 0

  return <div className="shell">
    <header className="topbar">
      <div className="brand"><span className="brand-mark">T</span><div><strong>TogetherServer</strong><small>Local companion</small></div></div>
      <div className="topbar-actions"><nav className="mode-switch" aria-label="Application mode">
        <button className={snapshot?.mode === 'Host' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Host'} onClick={() => void switchMode('host')}>Host</button>
        <button className={snapshot?.mode === 'Friend' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Friend'} onClick={() => void switchMode('friend')}>Friend</button>
      </nav><button className="quit-button" disabled={!!pending} onClick={() => void quitApp()}>Quit app</button></div>
    </header>

    <main>
      <div className="eyebrow">{snapshot?.mode === 'Friend' ? 'FRIEND MODE' : 'HOST MODE'} <span>·</span> THIS PC ONLY</div>
      <div className={`hero ${connectionView ? 'hero-compact' : ''}`}><div><h1>{snapshot?.mode === 'Friend' ? snapshot.endpoint ? 'Your Host PC' : 'Connect to your Host' : 'Host your Valheim server'}</h1>
        <p>{snapshot?.mode === 'Friend' ? snapshot.endpoint ? 'Check the connection, see your game server, and request actions the Host allows.' : 'Enter the Host IP and password once to connect this PC.' : savedProfiles.length === 0 ? 'Add a server to get started. We will guide you through the world, install, and password.' : activeRuns ? 'Your server is running. Share the join address and give friends access below.' : 'Your setup is saved. Start a server, then share the join address or invite friends.'}</p></div>
        {snapshot?.mode === 'Host' && <div className="hero-badge">{activeRuns} running<small>{savedProfiles.length} saved · limit {snapshot.settings.maxConcurrentServers}</small></div>}
      </div>

      {loadError && <div className="notice bad" role="alert">Connection to this local app failed: {loadError}</div>}
      {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
      {!snapshot && !loadError && <section className="panel">Loading local state…</section>}

      {snapshot?.mode === 'Friend' && <>
        <section className="panel friend-panel">
          <div className="section-heading"><span className="section-icon">↗</span><div><h2>{snapshot.endpoint && !showPairing ? 'Your Host' : 'Connect to a Host'}</h2><p>{snapshot.detail}</p></div></div>
          {snapshot.endpoint && !showPairing ? <>
            <div className="connection-address"><span>Host IP · {snapshot.state}</span><strong>{hostAddress(snapshot.endpoint)}</strong><small>{snapshot.detail} {snapshot.lastConnectedUtc ? `Last verified ${new Date(snapshot.lastConnectedUtc).toLocaleTimeString()}.` : ''}</small></div>
            <div className="actions"><button disabled={!!pending} onClick={() => void checkFriendConnection()}>{pending === 'poll' ? 'Checking…' : 'Check connection now'}</button>
              <button className="secondary" onClick={() => { setShowPairing(true); setFriendHostAddress(''); setFriendInvite('') }}>Connect to another Host PC</button></div>
          </> : <>
            <div className="settings-grid">
              <label>Host IP (port optional)<input value={friendHostAddress} onChange={event => setFriendHostAddress(event.target.value.trim())} placeholder="123.45.67.89" /><small>Port 5131 is assumed. Use IP:port if the Host changed it.</small></label>
              <label>Password<input type="password" autoComplete="off" value={friendInvite} onChange={event => setFriendInvite(event.target.value.trim())} placeholder="Paste the password from the Host" /><small>The Host generates one password per PC. Paste it once; this app saves its own access.</small></label>
            </div>
            <div className="save-row"><span>Use the TogetherServer Host IP, not the Valheim game port.</span><button disabled={!!pending} onClick={() => void pairFriend()}>{pending === 'pair' ? 'Connecting…' : 'Connect to Host PC'}</button></div>
          </>}
          <details className="advanced-block"><summary>Game running check · {snapshot.localGameRunning === null ? 'Unknown' : snapshot.localGameRunning ? 'Running' : 'Closed'}</summary>
            <p className="helper-text">TogetherServer checks whether Valheim is running on this PC. It never starts your game.</p>
            <div className="settings-grid"><label>Valheim game client<input value={friendClientPath} onChange={event => { friendClientEdited.current = true; setFriendClientPath(event.target.value) }} placeholder="Auto-detecting Steam installation…" /></label></div>
            {discovery && discovery.clients.length > 1 && <div className="choices"><strong>Valheim installs found</strong>{discovery.clients.map(item => <div className="choice" key={item.executablePath}><span>{item.executablePath}</span><button className="secondary" onClick={() => { friendClientEdited.current = true; setFriendClientPath(item.executablePath) }}>Use this install</button></div>)}</div>}
            <button className="secondary" disabled={!snapshot.endpoint || !!pending} onClick={() => void saveFriendClientPath()}>Save game check</button>
          </details>
        </section>
        {snapshot.profiles.length > 0 && <section className="panel">
          <div className="section-heading"><span className="section-icon">◎</span><div><h2>Host servers</h2><p>Requests name a saved profile only. The Host checks permissions and player state.</p></div></div>
          {snapshot.profiles.map(profile => <article className="profile-card" key={profile.id}>
            <div className="profile-top"><div><h3>{profile.name}</h3><p>{profile.state}</p></div><span className={`status ${profile.state === 'Process running' || profile.state === 'Ready' ? 'running' : 'offline'}`}>{profile.state}</span></div>
            {profile.joinAddress && <div className="join-line"><span>Valheim Join IP: <code>{profile.joinAddress}</code></span><button className="secondary" onClick={() => void copyText(profile.joinAddress!, 'Valheim join address')}>Copy address</button></div>}
            {!profile.joinAddress && <p className="helper-text">The Host has no fresh public Valheim game address yet.</p>}
            <div className="actions"><button disabled={!!pending || snapshot.state !== 'Connected' || !snapshot.canStart} onClick={() => void friendAction(profile.id, 'start')}>Request Start</button>
              <button className="secondary" disabled={!!pending || snapshot.state !== 'Connected' || !snapshot.canStop} onClick={() => void friendAction(profile.id, 'stop')}>Request Stop</button></div>
          </article>)}
          <p className="footnote">Remote Stop remains unavailable until all allowed players and game access are verified. A server log signal is not a client join.</p>
        </section>}
      </>}

      {snapshot?.mode === 'Host' && draft && <>
        {savedProfiles.length > 0 && <>
        <section className="panel">
          <div className="section-heading"><span className="section-icon">2</span><div><h2>Start and manage your server</h2><p>Use the server you saved in step 1. Status comes from this app's managed process.</p></div></div>
          <div className="profile-list">
            {snapshot.settings.profiles.map(profile => {
              const status = snapshot.runs.find(run => run.profileId === profile.id)
              return <article className="profile-card" key={profile.id}>
                <div className="profile-top"><div><h3>{profile.name}</h3><p>World {profile.worldId} <span>·</span> UDP {profile.gamePort}–{profile.gamePort + 1}</p></div>
                  <span className={`status ${status?.state === 'Process running' || status?.state === 'Ready' ? 'running' : status?.state === 'Offline' ? 'offline' : 'unknown'}`}>{status?.state ?? 'Unknown'}</span></div>
                <p className="status-detail">{status?.detail}</p>
                {profile.kind === 'Valheim' && <p className="helper-text">Playing on this PC? Join <code>127.0.0.1:{profile.gamePort}</code>. The address to share with friends is in step 3 below.</p>}
                <div className="actions">
                  <button disabled={!!pending || dirty || status?.state !== 'Offline'} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/start`, 'POST')}>Start server</button>
                  <button className="secondary" disabled={!!pending || dirty || !['Process running', 'Starting', 'Ready'].includes(status?.state ?? '')} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/stop`, 'POST')}>Stop server</button>
                  {profile.kind === 'Valheim' && status?.state === 'Ready' && <button className="secondary" onClick={() => shareRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })}>Share with friends</button>}
                  <button className="text-button" disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/health`, 'POST')}>Health check</button>
                  <button className="text-button" disabled={!!pending || status?.state !== 'Offline'} onClick={() => openSetup(profile.id)}>Edit setup</button>
                  {(status?.state === 'Unknown' || status?.state === 'Failed') && <button className="text-button" disabled={!!pending || dirty} onClick={() => {
                    if (window.confirm('Clear this unresolved run record only after verifying the original process is stopped?'))
                      void run(profile.id, `/api/local/profiles/${profile.id}/forget`, 'POST')
                  }}>Clear record</button>}
                </div>
              </article>
            })}
          </div>
          {dirty && <p className="warning-text">Save your changes below before starting or stopping a server.</p>}
          <div className="setup-tools"><button className="secondary" disabled={!!pending || dirty} onClick={addProfile}>Add another server</button></div>
          <p className="footnote">Owner game client: {snapshot.ownerGameRunning === null ? 'Unknown' : snapshot.ownerGameRunning ? 'Running' : 'Closed'}. Valheim Ready means its server-connected log was seen; a client join is still needed.</p>
        </section>
        </>}

        {(showSetup || savedProfiles.length === 0) && <section ref={setupRef} className="panel settings-panel">
          <div className="section-heading"><span className="section-icon">1</span><div><h2>Add a server</h2><p>Choose a world, select your installed server, and save its game password.</p></div></div>
          <div className="setup-header">
            {draft.profiles.length > 1 && <label>Editing server<select value={editedProfile?.id ?? ''} onChange={event => setActiveProfileId(event.target.value)}>{draft.profiles.map(profile => <option key={profile.id} value={profile.id}>{profile.name || profile.serverName || profile.worldId || 'New server'}</option>)}</select></label>}
            {savedProfiles.length > 0 && !dirty && <button className="secondary" onClick={() => setShowSetup(false)}>Done editing</button>}
          </div>
          {!editedProfile && <div className="empty"><p>Choose your world and installed Valheim server in a few steps.</p><div className="actions"><button onClick={addProfile}>Add Valheim server</button></div></div>}
          {editedProfile && <div className="setup-tools"><button className="secondary" disabled={!!pending} onClick={() => void scanValheim()}>{pending === 'scan' ? 'Searching…' : 'Find installed server and worlds'}</button><small>Searches Steam libraries and local saves on this PC.</small></div>}
          {draft.profiles.filter(profile => profile.id === editedProfile?.id).map(profile => <div className="profile-form" key={profile.id}>
            <div className="form-head"><strong>{profile.name || profile.serverName || 'New server'}</strong><button className="text-button danger" onClick={() => edit({ ...draft, profiles: draft.profiles.filter(item => item.id !== profile.id) })}>Remove server</button></div>
            <div className="setup-step"><h3><span>1</span> Choose a world</h3>
              {profile.kind === 'Valheim' && <div className="settings-grid">
                <label>Server name<input value={profile.serverName} onChange={event => updateProfile(profile.id, { serverName: event.target.value, name: event.target.value })} placeholder="My Valheim server" /></label>
                <label>World<select value={profile.worldSource} onChange={event => updateProfile(profile.id, { worldSource: event.target.value as Profile['worldSource'], worldDirectory: '', worldId: '' })}><option value="Existing">Use an existing save</option><option value="New">Create a new world</option></select></label>
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
              {profile.kind === 'Valheim' && profile.worldSource === 'New' && <div className="settings-grid"><label>New world name<input value={profile.worldId} onChange={event => updateProfile(profile.id, { worldId: event.target.value })} placeholder="MyNewWorld" /></label><label>Save directory<input value={profile.worldDirectory} onChange={event => updateProfile(profile.id, { worldDirectory: event.target.value })} placeholder="C:\\...\\Valheim" /><small>Choose an existing empty directory. An existing world with this name blocks Start.</small></label></div>}
              {profile.kind === 'Fixture' && <div className="settings-grid"><label>Test profile name<input value={profile.name} onChange={event => updateProfile(profile.id, { name: event.target.value })} /></label><label>World ID<input value={profile.worldId} onChange={event => updateProfile(profile.id, { worldId: event.target.value })} /></label><label className="wide">Disposable directory<input value={profile.worldDirectory} onChange={event => updateProfile(profile.id, { worldDirectory: event.target.value })} /></label></div>}
            </div>
            {!worldChosen(profile) && <p className="helper-text">Complete the world choice above to continue to the server install.</p>}
            {worldChosen(profile) && <div className="setup-step"><h3><span>2</span> Select the server app</h3>
              {profile.kind === 'Valheim' ? <>
                {profile.executablePath && <p className="selection-summary">Selected: <code>{profile.executablePath}</code></p>}
                {discovery && <div className="choices"><strong>Dedicated Server installs found</strong>{discovery.installations.length === 0 ? <p>None found. Browse to an installed copy or open Steam below.</p> : discovery.installations.map(item => <div className="choice" key={item.executablePath}><span>{item.executablePath}</span><button className="secondary" onClick={() => updateProfile(profile.id, { executablePath: item.executablePath })}>Use this install</button></div>)}</div>}
                <div className="setup-tools"><button className="secondary" disabled={!!pending} onClick={() => void browseServer(profile)}>{pending === profile.id ? 'Browsing…' : 'Browse for valheim_server.exe'}</button>{(!discovery || discovery.installations.length === 0) && <a href="steam://install/896660">Open install in Steam</a>}</div>
                <p className="helper-text">Steam handles installation and any terms after you choose to open it.</p>
              </> : <label>Fixture executable path<input value={profile.executablePath} onChange={event => updateProfile(profile.id, { executablePath: event.target.value })} placeholder="C:\\...\\TogetherServer.Fixture.exe" /></label>}
            </div>}
            {profile.kind === 'Valheim' && worldChosen(profile) && !!profile.executablePath && <div className="setup-step"><h3><span>3</span> Set a server password</h3>
              <div className="password-row"><label>Server password<input type="password" autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => setPasswords(current => ({ ...current, [profile.id]: event.target.value }))} placeholder={snapshot.passwordConfigured[profile.id] ? 'Password already saved; leave blank to keep it' : '5 or more characters'} /></label></div>
            </div>}
            <details className="advanced-block"><summary>Advanced server options</summary>
              <div className="settings-grid"><label>Game type<select value={profile.kind} onChange={event => updateProfile(profile.id, { kind: event.target.value as Profile['kind'] })}><option value="Valheim">Valheim</option><option value="Fixture">Synthetic test fixture</option></select></label><label>Game UDP start port<input type="number" value={profile.gamePort} onChange={event => updateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label>{profile.kind === 'Valheim' && <label className="wide">Installed server path<input value={profile.executablePath} onChange={event => updateProfile(profile.id, { executablePath: event.target.value })} /></label>}</div>
              {profile.kind === 'Valheim' && <div className="device-options"><label className="check-row"><input type="checkbox" checked={profile.crossplay} onChange={event => updateProfile(profile.id, { crossplay: event.target.checked })} /> Crossplay relay</label><label className="check-row"><input type="checkbox" checked={profile.publicListing} onChange={event => updateProfile(profile.id, { publicListing: event.target.checked })} /> Show in server list</label></div>}
              {profile.worldDirectory && <p className="helper-text">Server save location: <code>{profile.worldDirectory}</code></p>}
            </details>
          </div>)}
          {editedProfile && <><details className="advanced-block"><summary>Host limits and idle settings</summary><div className="settings-grid"><label>Maximum managed servers<input type="number" min="1" max="16" value={draft.maxConcurrentServers} onChange={event => edit({ ...draft, maxConcurrentServers: Number(event.target.value) })} /></label><label>Idle minutes<input type="number" min="1" max="1440" value={draft.idleMinutes} onChange={event => edit({ ...draft, idleMinutes: Number(event.target.value) })} /><small>Automatic shutdown remains unavailable until player coverage is verified.</small></label></div></details>
          {setupIssues.length > 0 && <div className="setup-needs" role="status"><strong>Finish these choices before saving:</strong><ul>{setupIssues.map((issue, index) => <li key={`${issue.id}-${index}`}>{draft.profiles.length > 1 ? `${issue.name}: ` : ''}{issue.message}</li>)}</ul></div>}
          <div className="save-row"><span>{setupIssues.length ? 'Choose the items above to finish this server.' : dirty || passwords[editedProfile.id] ? 'Ready to save this setup.' : 'Settings saved locally.'}</span><button disabled={setupIssues.length > 0 || (!dirty && !passwords[editedProfile.id]) || !!pending} onClick={() => void saveSetup()}>{pending === 'save' ? 'Saving…' : 'Save setup'}</button></div></>}
        </section>}
        {savedProfiles.length > 0 && <>
        <section ref={shareRef} className="panel">
          <div className="section-heading"><span className="section-icon">3</span><div><h2>Share and invite friends</h2><p>Copy the game address for joining. Give each Friend PC the app address and its own password.</p></div></div>
          <div className="connection-grid">
            <div className="connection-address"><span>Valheim game · UDP</span>{snapshot.settings.profiles.filter(profile => profile.kind === 'Valheim').map(profile => {
              const ready = snapshot.runs.some(run => run.profileId === profile.id && run.state === 'Ready')
              return <div className="connection-game" key={profile.id}><small>{profile.name}</small><strong>{detectedGameIp ? `${detectedGameIp}:${profile.gamePort}` : 'Unavailable'}</strong><small>{ready ? 'Ready signal received. A friend join is still unverified.' : 'Start this server and wait for Ready before joining.'}</small><div className="actions"><button className="secondary" disabled={!detectedGameIp} onClick={() => void copyText(`${detectedGameIp}:${profile.gamePort}`, 'Valheim join address')}>Copy Join IP</button><button className="secondary" disabled={!!pending || !snapshot.passwordConfigured[profile.id]} onClick={() => void copyGamePassword(profile.id)}>Copy game password</button></div></div>
            })}{!snapshot.settings.profiles.some(profile => profile.kind === 'Valheim') && <small>Set up a Valheim server to show its game Join IP.</small>}</div>
            <div className="connection-address"><span>TogetherServer app · TCP {draft.companionPort}</span><strong>{friendAppAddress || (detectingPublicIp ? 'Checking…' : 'Unavailable')}</strong><small>Use this IP in Friend mode. Port 5131 is assumed unless you changed it.</small><button className="secondary" disabled={!friendAppAddress} onClick={() => void copyText(friendAppAddress, 'Host app address')}>Copy Host address</button></div>
          </div>
          <button className="text-button" disabled={detectingPublicIp} onClick={() => void detectPublicIp()}>{detectingPublicIp ? 'Checking address…' : 'Refresh detected IP'}</button>
          {publicIpDetection && !publicIpDetection.ok && <p className="warning-text">{publicIpDetection.message}</p>}
          <p className="footnote">The app detects the outbound public IP. A Friend on another network must test whether either port is reachable. TogetherServer does not change router or firewall settings.</p>
        </section>
        <section className="panel">
          <div className="section-heading"><span className="section-icon">↗</span><div><h2>Give a Friend access</h2><p>Your Friend opens this app on their PC and enters your Host IP and a password. You never enter their IP.</p></div></div>
          <div className="device-options"><label className="check-row"><input type="checkbox" checked={deviceStart} onChange={event => setDeviceStart(event.target.checked)} /> May request Start</label><label className="check-row"><input type="checkbox" checked={deviceStop} onChange={event => setDeviceStop(event.target.checked)} /> May request Stop</label></div>
          <div className="save-row"><span>{dirty ? 'Save your other settings first.' : !friendAppAddress ? 'Wait for the Host address check.' : 'Make a separate password for each PC. It expires in 30 minutes.'}</span><button disabled={!!pending || dirty || !friendAppAddress} onClick={() => void issueInvite()}>{pending === 'invite' ? 'Creating…' : 'Generate password'}</button></div>
          {invitation && <div className="invite-box"><strong>One-time password for {invitationName}</strong><p>Privately share the Host IP and this password. {companion?.listenerActive ? 'Your Friend can connect now.' : 'Enable Friend app connections, save, and reopen this app before your Friend clicks Connect.'}</p><div className="actions"><button onClick={() => void copyText(invitation, 'Password')}>Copy password</button><button className="secondary" onClick={() => setInvitation('')}>Hide</button></div><details><summary>Show password to copy manually</summary><textarea readOnly rows={3} value={invitation} /></details></div>}
          {(draft.companionListeningEnabled || companion?.devices.some(device => !device.revoked)) && <div className="policy-line"><div><strong>Allow Friend app connections</strong><p>Requires this app to restart after saving. No router or firewall changes are made.</p></div><label className="check-row"><input type="checkbox" checked={draft.companionListeningEnabled} onChange={event => edit({ ...draft, companionListeningEnabled: event.target.checked })} /> Allow</label></div>}
          {(draft.remoteControlsEnabled || companion?.devices.some(device => device.paired && !device.revoked)) && <div className="policy-line"><div><strong>Remote Start and Stop</strong><p>Turning this off blocks requests immediately without stopping a running server.</p></div><label className="check-row"><input type="checkbox" checked={draft.remoteControlsEnabled} onChange={event => edit({ ...draft, remoteControlsEnabled: event.target.checked })} /> Allow</label></div>}
          {!companion?.devices.some(device => device.paired && !device.revoked) && companion?.devices.some(device => !device.revoked) && <p className="helper-text">Remote controls can be enabled after a Friend PC pairs.</p>}
          {snapshot.settings.companionListeningEnabled && !companion?.listenerActive && <p className="warning-text">Friend access is saved but not listening yet. Quit and reopen TogetherServer to activate it.</p>}
          {companion?.listenerWarning && <p className="warning-text">{companion.listenerWarning}</p>}
          {(dirty || draft.companionListeningEnabled || companion?.devices.some(device => !device.revoked)) && <div className="save-row"><span>{dirty ? 'Changes need saving.' : companion?.listenerActive ? 'Friend app connection is active.' : 'Friend app connection is off.'}</span><button disabled={!dirty || !!pending} onClick={() => void run('save', '/api/local/settings', 'PUT', draft)}>{pending === 'save' ? 'Saving…' : 'Save access settings'}</button></div>}
          <details className="advanced-block"><summary>Advanced address, port, and game check</summary>
            <div className="settings-grid companion-fields">
              <label>Friend app TCP port<input type="number" min="1024" max="65535" value={draft.companionPort} onChange={event => {
                const port = Number(event.target.value)
                let endpoint = draft.companionEndpoint
                if (endpoint) try { const url = new URL(endpoint); url.port = String(port); endpoint = url.origin } catch { /* Validation explains a custom endpoint. */ }
                edit({ ...draft, companionPort: port, companionEndpoint: endpoint })
              }} /><small>Standard port: 5131. Choose a different port before creating the first pairing code.</small></label>
              <label>Custom HTTPS endpoint<input value={draft.companionEndpoint} onChange={event => edit({ ...draft, companionEndpoint: event.target.value.trim() })} placeholder="https://127.0.0.1:5131" /><small>Only needed for local testing or a custom IP.</small></label>
              <label>Bind IP<input value={draft.companionBindAddress} onChange={event => edit({ ...draft, companionBindAddress: event.target.value })} placeholder="127.0.0.1" /></label>
              <label>Owner Valheim client<input value={draft.ownerClientExecutablePath} onChange={event => edit({ ...draft, ownerClientExecutablePath: event.target.value })} placeholder="Auto-detected game install" /><small>Unknown until the owner's game client is found.</small></label>
            </div>
            {companion?.fingerprint && <p className="footnote">Pinned Host identity: <code>{companion.fingerprint}</code></p>}
          </details>
          {companion?.devices.length ? <details className="advanced-block"><summary>Paired PCs and pending codes ({companion.devices.length})</summary><div className="profile-list device-list">{companion.devices.map(device => <div className="device" key={device.id}>
            <div><strong>{device.name}</strong><small>{device.revoked ? 'Revoked' : device.lastHeartbeatUtc ? `Heartbeat ${new Date(device.lastHeartbeatUtc).toLocaleTimeString()} · Game ${device.gameRunning === null ? 'Unknown' : device.gameRunning ? 'running' : 'closed'}` : device.paired ? 'Heartbeat Unknown' : 'Invite pending'} · {device.canStart ? 'Start allowed' : 'Start denied'} · {device.canStop ? 'Stop allowed' : 'Stop denied'}</small></div>
            <div className="actions"><button className="secondary" disabled={!!pending || device.revoked} onClick={() => void issueInvite(device.id, device.name, device.canStart, device.canStop)}>Rotate</button><button className="text-button danger" disabled={!!pending || device.revoked} onClick={() => void revokeDevice(device.id)}>Revoke</button></div>
          </div>)}</div></details> : null}
        </section>
        </>}
      </>}
      <p className="footnote">Minimize this window to keep TogetherServer running. Close the window or use Quit app to exit after managed servers stop.</p>
    </main>
  </div>
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>)

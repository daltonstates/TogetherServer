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
  ownerClientExecutablePath: string
  permittedPlayersVerified: boolean
  profiles: Profile[]
}
type Run = { profileId: string; state: string; detail: string; processId: number | null }
type HostSnapshot = { mode: 'Host'; evidence: string; settings: Settings; runs: Run[]; ownerGameRunning: boolean | null; ownerCheckedUtc: string; passwordConfigured: Record<string, boolean> }
type PublicProfile = { id: string; name: string; state: string }
type Device = { id: string; name: string; canStart: boolean; canStop: boolean; revoked: boolean; paired: boolean; lastHeartbeatUtc: string | null; gameRunning: boolean | null }
type CompanionInfo = { listenerActive: boolean; listenerWarning: string | null; endpoint: string; fingerprint: string | null; devices: Device[] }
type FriendSnapshot = { mode: 'Friend'; state: string; detail: string; endpoint: string; lastConnectedUtc: string | null; localGameRunning: boolean | null; remoteControlsEnabled: boolean; canStart: boolean; canStop: boolean; profiles: PublicProfile[] }
type Snapshot = HostSnapshot | FriendSnapshot
type BasicResult = { ok: boolean; code: string; message: string }
type ActionResult = { ok: boolean; code: string; message: string; snapshot: HostSnapshot }
type Discovery = { installations: { executablePath: string; source: string }[]; worlds: { name: string; saveRoot: string; sourceFolder: string; format: string }[] }
type ImportResult = BasicResult & { worldDirectory: string | null }
type ServerBrowseResult = BasicResult & { executablePath?: string }
type WorldBrowseResult = BasicResult & { worldId: string | null; sourceSaveRoot: string | null; sourceFolder: string }

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

function App() {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [draft, setDraft] = useState<Settings | null>(null)
  const [dirty, setDirty] = useState(false)
  const [pending, setPending] = useState('')
  const [notice, setNotice] = useState<{ good: boolean; text: string } | null>(null)
  const [loadError, setLoadError] = useState('')
  const [companion, setCompanion] = useState<CompanionInfo | null>(null)
  const [deviceName, setDeviceName] = useState('')
  const [deviceStart, setDeviceStart] = useState(false)
  const [deviceStop, setDeviceStop] = useState(false)
  const [invitation, setInvitation] = useState('')
  const [friendInvite, setFriendInvite] = useState('')
  const [friendClientPath, setFriendClientPath] = useState('')
  const [passwords, setPasswords] = useState<Record<string, string>>({})
  const [discovery, setDiscovery] = useState<Discovery | null>(null)
  const [sourceRoots, setSourceRoots] = useState<Record<string, string>>({})
  const [hostView, setHostView] = useState<'servers' | 'setup' | 'friends'>('servers')
  const [activeProfileId, setActiveProfileId] = useState('')
  const [exiting, setExiting] = useState(false)
  const exited = useRef(false)
  const initialHostViewSet = useRef(false)

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
          if (!initialHostViewSet.current) {
            setHostView(next.settings.profiles.length ? 'servers' : 'setup')
            initialHostViewSet.current = true
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

  const edit = (next: Settings) => { setDraft(next); setDirty(true) }
  const issueInvite = async (rotateDeviceId?: string, name = deviceName, canStart = deviceStart, canStop = deviceStop) => {
    setPending('invite')
    setNotice(null)
    try {
      const response = await fetch('/api/local/devices/invite', { method: 'POST', headers: localHeaders,
        body: JSON.stringify({ name, canStart, canStop, rotateDeviceId: rotateDeviceId ?? null }) })
      const result: { ok: boolean; code: string; message: string; invitation?: string } = await response.json()
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
      if (result.ok && result.invitation) {
        setInvitation(result.invitation)
        const latest = await fetch('/api/local/companion')
        if (latest.ok) setCompanion(await latest.json())
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
    setPending('pair')
    try {
      const result = await change<BasicResult>('/api/local/friend/pair', 'POST', { invitation: friendInvite, clientExecutablePath: friendClientPath })
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
      if (result.ok) {
        setFriendInvite('')
        await fetch('/api/local/friend/poll', { method: 'POST', headers: localHeaders })
        setSnapshot(await readSnapshot())
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const saveFriendClientPath = async () => {
    setPending('client-path')
    try {
      const result = await change<BasicResult>('/api/local/friend/client-path', 'POST', { path: friendClientPath })
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
      if (result.ok) {
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
      setNotice({ good: true, text: needsPassword ? 'Settings saved. Add a server password to start Valheim.' : 'Setup saved. You can start this server from Servers.' })
      if (!needsPassword && profile) setHostView('servers')
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
        if (next.mode === 'Host') setHostView(next.settings.profiles.length ? 'servers' : 'setup')
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
    setHostView('setup')
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
      <div className="hero"><div><h1>{snapshot?.mode === 'Friend' ? 'Your connection to the host' : 'Your server, in your hands.'}</h1>
        <p>{snapshot?.mode === 'Friend' ? 'Pair this PC, report whether your game client is running, and request approved Host actions.' : 'Set up a Valheim server, manage it here, and invite friends when you are ready.'}</p></div>
        <div className="hero-badge">{snapshot?.mode === 'Host' ? 'Local Host' : 'Local Friend'}<small>127.0.0.1 only</small></div>
      </div>

      {loadError && <div className="notice bad" role="alert">Connection to this local app failed: {loadError}</div>}
      {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
      {!snapshot && !loadError && <section className="panel">Loading local state…</section>}

      {snapshot?.mode === 'Friend' && <>
        <div className="stats">
          <div className="stat"><span>Host connection</span><strong className="small-stat">{snapshot.state}</strong><small>{snapshot.lastConnectedUtc ? `Last verified ${new Date(snapshot.lastConnectedUtc).toLocaleTimeString()}` : 'No verified reply yet'}</small></div>
          <div className="stat"><span>Remote controls</span><strong>{snapshot.remoteControlsEnabled ? 'On' : 'Off'}</strong><small>Host decides on every request</small></div>
          <div className="stat"><span>Your game client</span><strong>{snapshot.localGameRunning === null ? 'Unknown' : snapshot.localGameRunning ? 'Running' : 'Closed'}</strong><small>Exact executable path check</small></div>
        </div>
        <section className="panel friend-panel">
          <div className="section-heading"><span className="section-icon">↗</span><div><h2>{snapshot.state}</h2><p>{snapshot.detail}</p></div></div>
          {snapshot.endpoint && <p>Saved Host endpoint: <code>{snapshot.endpoint}</code></p>}
          <div className="settings-grid">
            <label>One-time Host invitation<textarea rows={5} value={friendInvite} onChange={event => setFriendInvite(event.target.value)} placeholder="Paste the invite copied by the Host" /></label>
            <label>Installed Valheim game client path<input value={friendClientPath} onChange={event => setFriendClientPath(event.target.value)} placeholder="C:\\...\\valheim.exe" /><small>Without a verified path, your heartbeat reports Unknown.</small></label>
          </div>
          <div className="save-row"><span>Pairing connects only to the invited Host TLS fingerprint.</span><button disabled={!friendInvite || !!pending} onClick={() => void pairFriend()}>{pending === 'pair' ? 'Pairing…' : 'Pair this PC'}</button><button className="secondary" disabled={!snapshot.endpoint || !!pending} onClick={() => void saveFriendClientPath()}>Save client path</button></div>
        </section>
        {snapshot.profiles.length > 0 && <section className="panel">
          <div className="section-heading"><span className="section-icon">◎</span><div><h2>Host servers</h2><p>Requests name a saved profile only. The Host checks permissions and player state.</p></div></div>
          {snapshot.profiles.map(profile => <article className="profile-card" key={profile.id}>
            <div className="profile-top"><div><h3>{profile.name}</h3><p>{profile.state}</p></div><span className={`status ${profile.state === 'Process running' || profile.state === 'Ready' ? 'running' : 'offline'}`}>{profile.state}</span></div>
            <div className="actions"><button disabled={!!pending || snapshot.state !== 'Connected' || !snapshot.canStart} onClick={() => void friendAction(profile.id, 'start')}>Request Start</button>
              <button className="secondary" disabled={!!pending || snapshot.state !== 'Connected' || !snapshot.canStop} onClick={() => void friendAction(profile.id, 'stop')}>Request Stop</button></div>
          </article>)}
          <p className="footnote">Remote Stop remains unavailable until all allowed players and game access are verified. A server log signal is not a client join.</p>
        </section>}
      </>}

      {snapshot?.mode === 'Host' && draft && <>
        <nav className="host-tabs" aria-label="Host sections">
          <button className={hostView === 'servers' ? 'selected' : ''} onClick={() => setHostView('servers')}>Servers</button>
          <button className={hostView === 'setup' ? 'selected' : ''} onClick={() => setHostView('setup')}>Setup{dirty ? ' •' : ''}</button>
          <button className={hostView === 'friends' ? 'selected' : ''} onClick={() => setHostView('friends')}>Friends</button>
        </nav>
        {hostView === 'servers' && <>
        <div className="stats">
          <div className="stat"><span>Managed processes</span><strong>{snapshot.runs.filter(r => ['Process running', 'Starting', 'Ready'].includes(r.state)).length}<em> / {snapshot.settings.maxConcurrentServers}</em></strong><small>Exact recorded process identity</small></div>
          <div className="stat"><span>Remote controls</span><strong>{snapshot.settings.remoteControlsEnabled ? 'On' : 'Off'}</strong><small>{companion?.listenerActive ? 'Authenticated HTTPS listener active' : 'Listener off or restart needed'}</small></div>
          <div className="stat"><span>Owner game client</span><strong>{snapshot.ownerGameRunning === null ? 'Unknown' : snapshot.ownerGameRunning ? 'Running' : 'Closed'}</strong><small>Checked even with this tab closed</small></div>
        </div>

        <section className="panel">
          <div className="section-heading"><span className="section-icon">◎</span><div><h2>Your servers</h2><p>Start, stop, and check the servers this app manages.</p></div></div>
          <div className="profile-list">
            {snapshot.settings.profiles.length === 0 && <div className="empty">No server set up yet. <button className="text-button" onClick={addProfile}>Set up your first server</button></div>}
            {snapshot.settings.profiles.map(profile => {
              const status = snapshot.runs.find(run => run.profileId === profile.id)
              return <article className="profile-card" key={profile.id}>
                <div className="profile-top"><div><h3>{profile.name}</h3><p>World {profile.worldId} <span>·</span> UDP {profile.gamePort}–{profile.gamePort + 1}</p></div>
                  <span className={`status ${status?.state === 'Process running' || status?.state === 'Ready' ? 'running' : status?.state === 'Offline' ? 'offline' : 'unknown'}`}>{status?.state ?? 'Unknown'}</span></div>
                <p className="status-detail">{status?.detail}</p>
                <div className="actions">
                  <button disabled={!!pending || dirty || status?.state !== 'Offline'} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/start`, 'POST')}>Start {profile.kind}</button>
                  <button className="secondary" disabled={!!pending || dirty || !['Process running', 'Starting', 'Ready'].includes(status?.state ?? '')} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/stop`, 'POST')}>Stop</button>
                  <button className="text-button" disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/health`, 'POST')}>Health check</button>
                  {(status?.state === 'Unknown' || status?.state === 'Failed') && <button className="text-button" disabled={!!pending || dirty} onClick={() => {
                    if (window.confirm('Clear this unresolved run record only after verifying the original process is stopped?'))
                      void run(profile.id, `/api/local/profiles/${profile.id}/forget`, 'POST')
                  }}>Clear record</button>}
                </div>
              </article>
            })}
          </div>
          {dirty && <p className="warning-text">Save your changes in Setup before starting or stopping a server.</p>}
          <p className="footnote">Valheim Ready means its server-connected log was seen. A client join is needed to verify gameplay.</p>
        </section>
        </>}

        {hostView === 'setup' && <section className="panel settings-panel">
          <div className="section-heading"><span className="section-icon">⚙</span><div><h2>Set up a server</h2><p>Choose a world, select your installed server, and save a password.</p></div></div>
          <div className="setup-header">
            {draft.profiles.length > 1 && <label>Editing server<select value={editedProfile?.id ?? ''} onChange={event => setActiveProfileId(event.target.value)}>{draft.profiles.map(profile => <option key={profile.id} value={profile.id}>{profile.name || profile.serverName || profile.worldId || 'New server'}</option>)}</select></label>}
            {draft.profiles.length > 0 && <button className="secondary" onClick={addProfile}>Add another server</button>}
          </div>
          {!editedProfile && <div className="empty">Start by adding a server. <button className="text-button" onClick={addProfile}>Add Valheim server</button></div>}
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
            <div className="setup-step"><h3><span>2</span> Select the server app</h3>
              {profile.kind === 'Valheim' ? <>
                {profile.executablePath && <p className="selection-summary">Selected: <code>{profile.executablePath}</code></p>}
                {discovery && <div className="choices"><strong>Dedicated Server installs found</strong>{discovery.installations.length === 0 ? <p>None found. Browse to an installed copy or open Steam below.</p> : discovery.installations.map(item => <div className="choice" key={item.executablePath}><span>{item.executablePath}</span><button className="secondary" onClick={() => updateProfile(profile.id, { executablePath: item.executablePath })}>Use this install</button></div>)}</div>}
                <div className="setup-tools"><button className="secondary" disabled={!!pending} onClick={() => void browseServer(profile)}>{pending === profile.id ? 'Browsing…' : 'Browse for valheim_server.exe'}</button>{(!discovery || discovery.installations.length === 0) && <a href="steam://install/896660">Open install in Steam</a>}</div>
                <p className="helper-text">Steam handles installation and any terms after you choose to open it.</p>
              </> : <label>Fixture executable path<input value={profile.executablePath} onChange={event => updateProfile(profile.id, { executablePath: event.target.value })} placeholder="C:\\...\\TogetherServer.Fixture.exe" /></label>}
            </div>
            {profile.kind === 'Valheim' && <div className="setup-step"><h3><span>3</span> Set a server password</h3>
              <div className="password-row"><label>Server password<input type="password" autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => setPasswords(current => ({ ...current, [profile.id]: event.target.value }))} placeholder={snapshot.passwordConfigured[profile.id] ? 'Password already saved; leave blank to keep it' : '5 or more characters'} /></label></div>
            </div>}
            <details className="advanced-block"><summary>Advanced server options</summary>
              <div className="settings-grid"><label>Game type<select value={profile.kind} onChange={event => updateProfile(profile.id, { kind: event.target.value as Profile['kind'] })}><option value="Valheim">Valheim</option><option value="Fixture">Synthetic test fixture</option></select></label><label>Game UDP start port<input type="number" value={profile.gamePort} onChange={event => updateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label>{profile.kind === 'Valheim' && <label className="wide">Installed server path<input value={profile.executablePath} onChange={event => updateProfile(profile.id, { executablePath: event.target.value })} /></label>}</div>
              {profile.kind === 'Valheim' && <div className="device-options"><label className="check-row"><input type="checkbox" checked={profile.crossplay} onChange={event => updateProfile(profile.id, { crossplay: event.target.checked })} /> Crossplay relay</label><label className="check-row"><input type="checkbox" checked={profile.publicListing} onChange={event => updateProfile(profile.id, { publicListing: event.target.checked })} /> Show in server list</label></div>}
              {profile.worldDirectory && <p className="helper-text">Server save location: <code>{profile.worldDirectory}</code></p>}
            </details>
          </div>)}
          <details className="advanced-block"><summary>Host limits and idle settings</summary><div className="settings-grid"><label>Maximum managed servers<input type="number" min="1" max="16" value={draft.maxConcurrentServers} onChange={event => edit({ ...draft, maxConcurrentServers: Number(event.target.value) })} /></label><label>Idle minutes<input type="number" min="1" max="1440" value={draft.idleMinutes} onChange={event => edit({ ...draft, idleMinutes: Number(event.target.value) })} /><small>Automatic shutdown remains unavailable until player coverage is verified.</small></label></div></details>
          <div className="save-row"><span>{dirty || (editedProfile && passwords[editedProfile.id]) ? 'Save this setup before using process controls.' : 'Settings saved locally.'}</span><button disabled={(!dirty && !passwords[editedProfile?.id ?? '']) || !!pending} onClick={() => void saveSetup()}>{pending === 'save' ? 'Saving…' : 'Save setup'}</button></div>
        </section>}
        {hostView === 'friends' && <>
        <section className="panel">
          <div className="section-heading"><span className="section-icon">↗</span><div><h2>Friend access</h2><p>Pair each PC separately, then choose whether friends may control the server.</p></div></div>
          <div className="policy-line"><div><strong>Remote Start and Stop</strong><p>Turning this off blocks requests immediately without stopping a running server.</p></div><label className="check-row"><input type="checkbox" checked={draft.remoteControlsEnabled} disabled={!draft.remoteControlsEnabled && !companion?.devices.some(device => device.paired && !device.revoked)} onChange={event => edit({ ...draft, remoteControlsEnabled: event.target.checked })} /> Allow</label></div>
          {!companion?.devices.some(device => device.paired && !device.revoked) && <p className="helper-text">Pair a Friend device before enabling remote controls.</p>}
          <label className="check-row"><input type="checkbox" checked={draft.companionListeningEnabled} onChange={event => edit({ ...draft, companionListeningEnabled: event.target.checked })} /> Enable authenticated HTTPS access on next launch</label>
          <p className="helper-text">The Host GUI stays local. Router and firewall setup is always manual.</p>
          <details className="advanced-block"><summary>Connection and player check settings</summary>
            <div className="settings-grid companion-fields">
              <label>HTTPS endpoint friends will use<input value={draft.companionEndpoint} onChange={event => edit({ ...draft, companionEndpoint: event.target.value })} placeholder="https://127.0.0.1:5131" /></label>
              <label>Bind IP address<input value={draft.companionBindAddress} onChange={event => edit({ ...draft, companionBindAddress: event.target.value })} placeholder="127.0.0.1" /></label>
              <label>Companion HTTPS port<input type="number" value={draft.companionPort} onChange={event => edit({ ...draft, companionPort: Number(event.target.value) })} /></label>
              <label>Owner Valheim client path<input value={draft.ownerClientExecutablePath} onChange={event => edit({ ...draft, ownerClientExecutablePath: event.target.value })} placeholder="C:\\...\\valheim.exe" /><small>Until configured, the owner's game-running state is Unknown.</small></label>
            </div>
          </details>
          {companion?.listenerWarning && <p className="warning-text">{companion.listenerWarning}</p>}
          <div className="save-row"><span>{dirty ? 'Unsaved changes.' : companion?.listenerActive ? 'Authenticated HTTPS active.' : 'Friend listener off.'}</span><button disabled={!dirty || !!pending} onClick={() => void run('save', '/api/local/settings', 'PUT', draft)}>{pending === 'save' ? 'Saving…' : 'Save settings'}</button></div>
        </section>
        <section className="panel">
          <div className="section-heading"><span className="section-icon">↗</span><div><h2>Paired Friend devices</h2><p>Each PC gets its own one-time invite, credential, and Start/Stop permissions.</p></div></div>
          <div className="settings-grid">
            <label>Device name<input value={deviceName} onChange={event => setDeviceName(event.target.value)} placeholder="Friend's PC" /></label>
            <div className="device-options"><label className="check-row"><input type="checkbox" checked={deviceStart} onChange={event => setDeviceStart(event.target.checked)} /> May request Start</label><label className="check-row"><input type="checkbox" checked={deviceStop} onChange={event => setDeviceStop(event.target.checked)} /> May request Stop</label></div>
          </div>
          <div className="save-row"><span>Invites expire after 30 minutes and work once.</span><button disabled={!!pending || dirty || !deviceName} onClick={() => void issueInvite()}>{pending === 'invite' ? 'Creating…' : 'Create invite'}</button></div>
          {invitation && <div className="invite-box"><strong>One-time invitation</strong><p>Copy privately. This is shown only in this app session.</p><textarea readOnly rows={5} value={invitation} /><div className="actions"><button onClick={() => void navigator.clipboard.writeText(invitation)}>Copy invite</button><button className="secondary" onClick={() => setInvitation('')}>Hide</button></div></div>}
          <div className="profile-list device-list">{companion?.devices.map(device => <div className="device" key={device.id}>
            <div><strong>{device.name}</strong><small>{device.revoked ? 'Revoked' : device.lastHeartbeatUtc ? `Heartbeat ${new Date(device.lastHeartbeatUtc).toLocaleTimeString()} · Game ${device.gameRunning === null ? 'Unknown' : device.gameRunning ? 'running' : 'closed'}` : device.paired ? 'Heartbeat Unknown' : 'Invite pending'} · {device.canStart ? 'Start allowed' : 'Start denied'} · {device.canStop ? 'Stop allowed' : 'Stop denied'}</small></div>
            <div className="actions"><button className="secondary" disabled={!!pending || device.revoked} onClick={() => void issueInvite(device.id, device.name, device.canStart, device.canStop)}>Rotate</button><button className="text-button danger" disabled={!!pending || device.revoked} onClick={() => void revokeDevice(device.id)}>Revoke</button></div>
          </div>)}</div>
          {companion?.fingerprint && <p className="footnote">Pinned Host certificate fingerprint: <code>{companion.fingerprint}</code></p>}
        </section>
        <p className="footnote">Remote Stop stays blocked while a player is running Valheim or their status is Unknown. Automatic shutdown remains off until all players and game access are verified.</p>
        </>}
      </>}
      <p className="footnote">Minimize this window to keep TogetherServer running. Close the window or use Quit app to exit after managed servers stop.</p>
    </main>
  </div>
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>)

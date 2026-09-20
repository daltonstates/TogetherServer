import React, { useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import './style.css'

type Profile = {
  id: string
  name: string
  worldId: string
  worldDirectory: string
  gamePort: number
  executablePath: string
}
type Settings = {
  maxConcurrentServers: number
  idleMinutes: number
  autoShutdownEnabled: boolean
  remoteControlsEnabled: boolean
  profiles: Profile[]
}
type Run = { profileId: string; state: string; detail: string; processId: number | null }
type HostSnapshot = { mode: 'Host'; evidence: string; settings: Settings; runs: Run[] }
type FriendSnapshot = { mode: 'Friend'; state: string; detail: string }
type Snapshot = HostSnapshot | FriendSnapshot
type ActionResult = { ok: boolean; code: string; message: string; snapshot: HostSnapshot }

const localHeaders = { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' }

async function readSnapshot(): Promise<Snapshot> {
  const response = await fetch('/api/local/snapshot', { cache: 'no-store' })
  if (!response.ok) throw new Error(`Local app returned ${response.status}`)
  return response.json()
}

async function change(path: string, method: 'POST' | 'PUT', body?: unknown): Promise<ActionResult> {
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

  useEffect(() => {
    let alive = true
    const refresh = async () => {
      try {
        const next = await readSnapshot()
        if (!alive) return
        setSnapshot(next)
        setLoadError('')
        setDraft(current => current ?? (next.mode === 'Host' ? next.settings : null))
      } catch (error) {
        if (alive) setLoadError(String(error))
      }
    }
    void refresh()
    const timer = window.setInterval(refresh, 3000)
    return () => { alive = false; window.clearInterval(timer) }
  }, [])

  const edit = (next: Settings) => { setDraft(next); setDirty(true) }
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
        setDirty(false)
      }
    } catch (error) { setNotice({ good: false, text: String(error) }) }
    finally { setPending('') }
  }
  const run = async (key: string, path: string, method: 'POST' | 'PUT', body?: unknown) => {
    setPending(key)
    setNotice(null)
    try {
      const result = await change(path, method, body)
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

  return <div className="shell">
    <header className="topbar">
      <div className="brand"><span className="brand-mark">T</span><div><strong>TogetherServer</strong><small>Local companion</small></div></div>
      <nav className="mode-switch" aria-label="Application mode">
        <button className={snapshot?.mode === 'Host' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Host'} onClick={() => void switchMode('host')}>Host</button>
        <button className={snapshot?.mode === 'Friend' ? 'selected' : ''} disabled={!!pending || snapshot?.mode === 'Friend'} onClick={() => void switchMode('friend')}>Friend</button>
      </nav>
    </header>

    <main>
      <div className="eyebrow">{snapshot?.mode === 'Friend' ? 'FRIEND MODE' : 'HOST MODE'} <span>·</span> THIS PC ONLY</div>
      <div className="hero"><div><h1>{snapshot?.mode === 'Friend' ? 'Your connection to the host' : 'Your server, in your hands.'}</h1>
        <p>{snapshot?.mode === 'Friend' ? 'Friend pairing and heartbeat arrive in the next slice.' : 'Configure a local profile and test fixed process controls with a synthetic fixture.'}</p></div>
        <div className="hero-badge">{snapshot?.mode === 'Host' ? 'Local Host' : 'Local Friend'}<small>127.0.0.1 only</small></div>
      </div>

      {loadError && <div className="notice bad" role="alert">Connection to this local app failed: {loadError}</div>}
      {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
      {!snapshot && !loadError && <section className="panel">Loading local state…</section>}

      {snapshot?.mode === 'Friend' && <section className="panel friend-panel">
        <div className="section-heading"><span className="section-icon">↗</span><div><h2>{snapshot.state}</h2><p>{snapshot.detail}</p></div></div>
        <p>This instance has no Host endpoint or credential. It sends no heartbeat and makes no remote requests yet.</p>
        <div className="hint">Use the mode switch above, or start this app directly in Friend mode with <code>TogetherServer.exe --friend</code>.</div>
      </section>}

      {snapshot?.mode === 'Host' && draft && <>
        <div className="stats">
          <div className="stat"><span>Managed processes</span><strong>{snapshot.runs.filter(r => r.state === 'Process running').length}<em> / {snapshot.settings.maxConcurrentServers}</em></strong><small>Fixture processes only</small></div>
          <div className="stat"><span>Remote controls</span><strong>Off</strong><small>No public listener</small></div>
          <div className="stat"><span>Auto shutdown</span><strong>Off</strong><small>Player coverage unverified</small></div>
        </div>

        <section className="panel">
          <div className="section-heading"><span className="section-icon">◎</span><div><h2>Server profiles</h2><p>Each world and game port pair can have one managed writer.</p></div></div>
          <div className="profile-list">
            {snapshot.settings.profiles.length === 0 && <div className="empty">No profile saved yet. Add one below, choose the built fixture executable and an existing disposable save directory, then save settings.</div>}
            {snapshot.settings.profiles.map(profile => {
              const status = snapshot.runs.find(run => run.profileId === profile.id)
              return <article className="profile-card" key={profile.id}>
                <div className="profile-top"><div><h3>{profile.name}</h3><p>World {profile.worldId} <span>·</span> UDP {profile.gamePort}–{profile.gamePort + 1}</p></div>
                  <span className={`status ${status?.state === 'Process running' ? 'running' : status?.state === 'Offline' ? 'offline' : 'unknown'}`}>{status?.state ?? 'Unknown'}</span></div>
                <p className="status-detail">{status?.detail}</p>
                <div className="actions">
                  <button disabled={!!pending || dirty || status?.state !== 'Offline'} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/start`, 'POST')}>Start fixture</button>
                  <button className="secondary" disabled={!!pending || dirty || status?.state !== 'Process running'} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/stop`, 'POST')}>Stop</button>
                  <button className="text-button" disabled={!!pending || dirty} onClick={() => void run(profile.id, `/api/local/profiles/${profile.id}/health`, 'POST')}>Health check</button>
                  {(status?.state === 'Unknown' || status?.state === 'Failed') && <button className="text-button" disabled={!!pending || dirty} onClick={() => {
                    if (window.confirm('Clear this unresolved run record only after verifying the original process is stopped?'))
                      void run(profile.id, `/api/local/profiles/${profile.id}/forget`, 'POST')
                  }}>Clear record</button>}
                </div>
              </article>
            })}
          </div>
          <p className="footnote">Health confirms only that the recorded fixture process still matches its identity. It does not prove Valheim readiness, joining, or a saved world.</p>
        </section>

        <section className="panel settings-panel">
          <div className="section-heading"><span className="section-icon">⚙</span><div><h2>Host settings</h2><p>Stored under your Windows local application data folder.</p></div></div>
          <div className="settings-grid">
            <label>Maximum managed servers<input type="number" min="1" max="16" value={draft.maxConcurrentServers} onChange={event => edit({ ...draft, maxConcurrentServers: Number(event.target.value) })} /></label>
            <label>Idle minutes<input type="number" min="1" max="1440" value={draft.idleMinutes} onChange={event => edit({ ...draft, idleMinutes: Number(event.target.value) })} /><small>Saved for later; automatic shutdown is unavailable.</small></label>
          </div>
          <div className="policy-line"><div><strong>Remote Start / Stop</strong><p>Disabled until TLS, pairing, and permissions are in place.</p></div><span className="pill">Off</span></div>
          <div className="policy-line"><div><strong>Automatic shutdown</strong><p>Disabled until every allowed player and game access are verified.</p></div><span className="pill">Off</span></div>
          <div className="subheading"><h3>Approved fixture profiles</h3><button className="secondary" onClick={() => edit({ ...draft, profiles: [...draft.profiles, { id: crypto.randomUUID(), name: '', worldId: '', worldDirectory: '', gamePort: 2456, executablePath: '' }] })}>Add profile</button></div>
          {draft.profiles.map((profile, index) => <div className="profile-form" key={profile.id}>
            <div className="form-head"><strong>Profile {index + 1}</strong><button className="text-button danger" onClick={() => edit({ ...draft, profiles: draft.profiles.filter(p => p.id !== profile.id) })}>Remove</button></div>
            <div className="settings-grid">
              <label>Display name<input value={profile.name} onChange={event => updateProfile(profile.id, { name: event.target.value })} placeholder="My test world" /></label>
              <label>World ID<input value={profile.worldId} onChange={event => updateProfile(profile.id, { worldId: event.target.value })} placeholder="testworld" /></label>
              <label>Existing save directory<input value={profile.worldDirectory} onChange={event => updateProfile(profile.id, { worldDirectory: event.target.value })} placeholder="C:\\...\\disposable-world" /></label>
              <label>Game UDP start port<input type="number" value={profile.gamePort} onChange={event => updateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label>
              <label className="wide">Built TogetherServer.Fixture.exe path<input value={profile.executablePath} onChange={event => updateProfile(profile.id, { executablePath: event.target.value })} placeholder="C:\\...\\TogetherServer.Fixture.exe" /></label>
            </div>
          </div>)}
          <div className="save-row"><span>{dirty ? 'Unsaved changes. Save before using process controls.' : 'Settings saved locally.'}</span><button disabled={!dirty || !!pending} onClick={() => void run('save', '/api/local/settings', 'PUT', draft)}>{pending === 'save' ? 'Saving…' : 'Save settings'}</button></div>
        </section>
        <div className="hint">The mode switch is available when no managed run is active. Fixture work does not alter a real Valheim world.</div>
      </>}
    </main>
  </div>
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>)

import { useSyncExternalStore } from 'react'
import { Button, Select } from './Controls'
// @ts-expect-error The shared renderer fixtures are plain JavaScript; this adapter types the consumed state below.
import { createFixtureState, routeFixture, fixtureIds, FIXED_NOW } from '../../scripts/ui-redesign-fixtures.mjs'
import type { AppInstanceView, Device, FriendSnapshot, HostSnapshot, Run } from './contracts'
import type { LocalHostingStatus } from './FriendLocalHosting'
import type { Grants } from './HostSharingPermissions'
import './developmentDemo.css'

export const developmentDemoScenes = {
  'host-sharing': 'Your servers · sharing permissions',
  'friend-hosting': 'Friend servers · host this save',
  'friend-missing-server': 'Friend servers · missing server app',
  'friend-offline': 'Friend servers · owner offline, verified copy',
  'friend-players-online': 'Friend servers · players online',
  'friend-players-checking': 'Friend servers · background player check',
  'players-refreshing': 'Players · checking quietly',
  'players-repeated-issue': 'Players · repeated lookup issue'
} as const
export type DevelopmentDemoScene = keyof typeof developmentDemoScenes
export type DevelopmentDemoController = { reset: (scene?: DevelopmentDemoScene) => void; uninstall: () => void }

type FixtureState = {
  mode: 'Host' | 'Friend'; host: HostSnapshot; friend: FriendSnapshot
  devices: (Device & { sharedWorldGrants: Record<string, Grants> })[]
  sharingEnabled: boolean; consented: boolean; receivedSaveState: string | null
  desktopPreferences: { available: boolean; startupAvailable: boolean; launchAtLogin: boolean; closeToTray: boolean }
  requests: { method: string; path: string }[]
}
type FixtureResponse = { status: number; body: unknown; pending?: boolean }
type DemoSession = {
  scene: DevelopmentDemoScene; state: FixtureState; clockOffset: number; revision: number
  rosterRevision: number; prepared: boolean; running: boolean; serverInstalled: boolean
  instance: AppInstanceView; displayName: string
}
const ids = fixtureIds as Record<'primary' | 'secondary' | 'third' | 'device' | 'otherDevice' | 'session', string>
const branchHash = 'D'.repeat(64)
const demoRoot = 'C:\\TogetherServer-DEMO-FAKE-DATA'
const demoBoundary = 'DEMO only. Fake data in this window; no game, network, world files or real PC changes.'
const noGrants = (): Grants => ({ receive: false, eligibleHost: false, recoveryVoter: false, manageSharing: false, reshare: false })
const listeners = new Set<() => void>()
let activeSession: DemoSession | null = null
let activeController: DevelopmentDemoController | null = null
let nextRevision = 0
const notify = () => listeners.forEach(listener => listener())
const subscribe = (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener) } }

export function useDevelopmentDemoRevision(): number {
  return useSyncExternalStore(subscribe, () => activeSession?.revision ?? 0, () => 0)
}

export function DevelopmentDemoControls() {
  useDevelopmentDemoRevision()
  if (!activeSession || !activeController) return null
  return <aside className="development-demo-controls" aria-label="Demo controls">
    <div><strong>DEMO · FAKE DATA</strong><span>No real game, save files, network or PC changes. Resets when this window reloads.</span>
      <span>Isolated demo app: {activeSession.instance.localPort} · fake Friend control: {activeSession.instance.companionPort}</span></div>
    <label>Demo scene<Select value={activeSession.scene} onChange={event => activeController?.reset(event.target.value as DevelopmentDemoScene)}>
      {Object.entries(developmentDemoScenes).map(([id, label]) => <option value={id} key={id}>{label}</option>)}
    </Select></label>
    <Button type="button" onClick={() => activeController?.reset()}>Reset fake data</Button>
  </aside>
}

function makeSession(scene: DevelopmentDemoScene, instance: AppInstanceView): DemoSession {
  const state = createFixtureState('host-shared') as FixtureState
  const profile = state.host.settings.profiles[0]
  Object.assign(profile, { name: 'Meadow friends · DEMO', serverName: 'Meadow friends · DEMO', worldId: 'demo-meadow',
    executablePath: `${demoRoot}\\Valheim\\valheim_server.exe`, worldDirectory: `${demoRoot}\\worlds\\meadow`,
    gamePort: instance.valheimPort, sharedSavesEnabled: true })
  const second = structuredClone(profile)
  Object.assign(second, { id: ids.secondary, kind: 'MinecraftJava', name: 'Lantern village · DEMO', serverName: 'Lantern village · DEMO',
    worldId: 'demo-lantern', gamePort: instance.minecraftJavaPort, sharedSavesEnabled: false,
    executablePath: `${demoRoot}\\Java\\java.exe`, minecraft: { serverJarPath: `${demoRoot}\\Java\\server.jar` },
    worldDirectory: `${demoRoot}\\worlds\\lantern` })
  state.host.settings.profiles.push(second)
  const baseRun = state.host.runs[0]
  state.host.runs = [profile, second].map(item => ({ ...structuredClone(baseRun), profileId: item.id, state: 'Offline',
    processId: null, runOperationId: null, onlinePlayers: null, maxPlayers: null, playerCountTrusted: false,
    playerCountObservedUtc: null, playerObservationSource: null, playerCountRefreshing: false, playerCountFailureCount: 0,
    detail: demoBoundary, autoShutdownAtUtc: null }))
  state.host.evidence = demoBoundary
  state.host.managedWorldsRoot = `${demoRoot}\\worlds`
  Object.assign(state.host.settings, { companionPort: instance.companionPort, autoShutdownEnabled: false,
    companionEndpoint: `https://192.0.2.10:${instance.companionPort}` })
  state.host.passwordConfigured[ids.secondary] = false
  if (state.host.backups?.[ids.primary]) state.host.backups[ids.secondary] = { ...state.host.backups[ids.primary], profileId: ids.secondary }
  Object.assign(state.friend.profiles[0], { name: 'Morgan’s Meadow · DEMO', state: 'Ready', joinAddress: `192.0.2.10:${instance.valheimPort}` })
  Object.assign(state.friend, { connectionName: 'Morgan’s Host · DEMO', detail: demoBoundary, hostProfileName: 'Morgan' })
  state.devices.forEach((device, index) => {
    device.name = index === 0 ? 'Riley’s desktop · DEMO' : 'Mira’s laptop · DEMO'
    device.profileName = index === 0 ? 'Riley Demo' : 'Mira Demo'
    device.sharedWorldGrants[ids.primary] = { ...noGrants(), receive: true, eligibleHost: index === 0 }
  })
  state.consented = scene === 'friend-offline'
  state.receivedSaveState = state.consented ? 'verified' : null
  state.sharingEnabled = true
  state.mode = scene.startsWith('friend-') ? 'Friend' : 'Host'
  state.desktopPreferences = { available: false, startupAvailable: false, launchAtLogin: false, closeToTray: false }
  if (scene === 'friend-offline') {
    Object.assign(state.friend, { state: 'Disconnected', lastConnectedUtc: null,
      detail: 'DEMO: the owner is offline. This PC retains a complete fake verified copy.' })
    state.friend.profiles.forEach(item => Object.assign(item, { state: 'Unknown', onlinePlayers: null,
      maxPlayers: null, joinAddress: null, canStopNow: false, canRestartNow: false }))
  }
  if (scene === 'players-refreshing' || scene === 'players-repeated-issue') Object.assign(state.host.runs[0], {
    state: 'Unknown', processId: 4242, runOperationId: ids.session, playerCountRefreshing: scene === 'players-refreshing',
    playerCountFailureCount: scene === 'players-repeated-issue' ? 2 : 0,
    playerObservationSource: scene === 'players-refreshing' ? 'ObservationPending' : 'HealthProbeFailed',
    detail: 'DEMO: waiting for a reliable player count. No real server is queried.'
  })
  if (scene === 'friend-players-online' || scene === 'friend-players-checking') Object.assign(state.friend.profiles[0], {
    onlinePlayers: scene === 'friend-players-online' ? 2 : null,
    playerCountTrusted: scene === 'friend-players-online', playerCountObservedUtc: new Date(FIXED_NOW).toISOString(), maxPlayers: 10,
    playerCountRefreshing: scene === 'friend-players-checking', playerCountFailureCount: 0,
    playerObservationSource: scene === 'friend-players-online' ? 'ValheimReady' : 'ObservationPending',
    canStop: true, canStopNow: false, canRestartNow: false
  })
  if (scene.startsWith('players-')) state.host.activity = [{
    id: '99999999-9999-4999-8999-999999999999', occurredUtc: new Date(FIXED_NOW).toISOString(),
    category: 'Players', action: 'CountUnavailable', profileId: ids.primary, deviceId: null,
    severity: 'Warning', visibility: 'AssignedFriends', message: 'DEMO: earlier count check unavailable.'
  }]
  if (state.host.hostingPower) Object.assign(state.host.hostingPower, { managedServerRunning: false, message: demoBoundary })
  return { scene, state, clockOffset: Date.now() - FIXED_NOW, revision: ++nextRevision, rosterRevision: 1,
    prepared: false, running: false, serverInstalled: scene !== 'friend-missing-server', instance: structuredClone(instance),
    displayName: 'Riley Demo' }
}

const reply = (body: unknown, status = 200): FixtureResponse => ({ status, body })
const result = (extra: Record<string, unknown> = {}) => ({ ok: true, code: 'DemoSimulated', message: demoBoundary, ...extra })
const denied = (message: string, code = 'DemoActionUnavailable') => reply(result({ ok: false, code, message }), 409)
function hostingStatus(session: DemoSession): LocalHostingStatus {
  const grants = session.state.devices[0].sharedWorldGrants[ids.primary]
  const allowed = session.state.sharingEnabled && grants.receive && grants.eligibleHost
  const prepared = session.prepared && allowed
  return { ok: true, code: !allowed ? 'HostingNotAllowed' : session.running ? 'LocalHostingRunning' :
    prepared ? 'LocalHostingPrepared' : 'NeedsPreparation',
  message: !allowed ? 'DEMO: turn on Can host this save for Riley’s desktop in the server’s sharing settings.' :
    session.running ? 'DEMO: this local server is simulated; no game process is running.' : prepared ?
      'DEMO: the fake copy is prepared. Start server is a separate simulated action.' :
      'DEMO: approve a local copy and prepare this PC. Nothing is installed or copied.',
  allowed, consented: session.state.consented, game: 'Valheim', name: 'Morgan’s Meadow · DEMO',
  needsServerFile: !session.serverInstalled, needsPassword: !session.serverInstalled,
  serverFile: session.serverInstalled ? `${demoRoot}\\Valheim\\valheim_server.exe` : null, gameVersion: 'Demo version',
  gamePort: session.instance.valheimPort + 2, controlPort: session.instance.companionPort,
  localProfileId: session.prepared ? ids.third : null, branchHash: session.prepared ? branchHash : null,
  readyForManualStart: prepared && !session.running, running: session.running,
  steps: [
    { key: 'permission', state: allowed ? 'Ready' : 'ActionRequired', message: allowed ? 'DEMO: the owner allows this PC to host.' : 'DEMO: owner hosting permission is off.' },
    { key: 'server', state: session.serverInstalled ? 'Ready' : 'ActionRequired', message: session.serverInstalled ? 'DEMO: a fake installed server app is available.' : 'DEMO: choose the fake server app with Browse; no real file picker opens.' },
    { key: 'save', state: prepared || session.running ? 'Ready' : 'ActionRequired', message: prepared || session.running ? 'DEMO: complete fake copy and hashes.' : 'DEMO: allow receiving and prepare the fake copy.' },
    { key: 'ports', state: prepared || session.running ? 'Ready' : 'Unknown', message: prepared || session.running ? 'DEMO: fake local ports are available.' : 'DEMO: local ports are checked during simulated preparation.' },
    { key: 'connection', state: 'Unknown', message: 'Incoming Friend access is unverified. This demo never opens a public listener.' }
  ] }
}

function changeRun(session: DemoSession, profileId: string, running: boolean): Run | undefined {
  const run = session.state.host.runs.find(item => item.profileId === profileId)
  if (!run) return undefined
  Object.assign(run, { state: running ? 'Ready' : 'Offline', processId: running ? 4242 : null,
    runOperationId: running ? ids.session : null, onlinePlayers: running ? 0 : null, maxPlayers: running ? 10 : null,
    playerCountTrusted: running, playerObservationSource: running ? 'FixtureReady' : null,
    playerCountObservedUtc: running ? new Date(FIXED_NOW).toISOString() : null, detail: demoBoundary,
    playerCountRefreshing: false, playerCountFailureCount: 0,
    autoShutdownAtUtc: null, autoShutdownReason: 'DEMO: automatic shutdown is off.' })
  if (session.state.host.hostingPower) session.state.host.hostingPower.managedServerRunning = session.state.host.runs.some(item => item.state === 'Ready')
  return run
}

function demoRoute(session: DemoSession, path: string, method: string, body: Record<string, unknown>): FixtureResponse | null {
  const { state } = session
  if (path === '/api/local/instance' && method === 'GET') return reply(session.instance)
  if (method === 'GET' && (path === '/api/local/companion' || path === '/api/local/network/ports')) {
    const fixture = routeFixture(state, { path, method }) as FixtureResponse
    const info = fixture.body as { endpoint?: string; control?: { port: number; endpoint: string } }
    if (path === '/api/local/companion') info.endpoint = state.host.settings.companionEndpoint
    if (info.control) Object.assign(info.control, { port: session.instance.companionPort, endpoint: state.host.settings.companionEndpoint })
    return fixture
  }
  if (path === '/api/local/profile') {
    if (method === 'GET') return reply({ displayName: session.displayName })
    if (method === 'PUT' && typeof body.displayName === 'string' && body.displayName.trim().length > 0 && body.displayName.length <= 80) {
      session.displayName = body.displayName.trim(); return reply(result({ displayName: session.displayName }))
    }
    return denied('DEMO: enter a name with 1–80 characters.')
  }
  if (path === '/api/local/required-setup' && method === 'GET') return reply({ features: [
    { id: 'profile-name', revision: 1, title: 'Your name', description: 'The demo uses a fictional name.', completed: true }
  ] })
  if (path === '/api/local/shared-worlds/browse-server-file' && method === 'POST')
    return reply(result({ path: `${demoRoot}\\Valheim\\valheim_server.exe` }))
  if (path === '/api/local/friend/pair' && method === 'POST') return denied('DEMO: use a scene to select a fake saved Host. Real server codes are never used.')
  const grantsMatch = /^\/api\/local\/devices\/([^/]+)\/shared-world\/([^/]+)\/grants$/.exec(path)
  if (grantsMatch && method === 'PUT') {
    if (state.mode !== 'Host') return denied('DEMO: change owner permissions from Host.', 'PermissionDenied')
    const device = state.devices.find(item => item.id === grantsMatch[1] && !item.revoked)
    if (!device || grantsMatch[2] !== ids.primary || !body.grants || typeof body.grants !== 'object') return denied('DEMO: unknown member or world.')
    const grants = body.grants as Record<string, unknown>
    const keys = Object.keys(noGrants()) as (keyof Grants)[]
    if (Object.keys(body).length !== 1 || Object.keys(grants).length !== keys.length || keys.some(key => typeof grants[key] !== 'boolean')
      || grants.eligibleHost === true && grants.receive !== true) return denied('DEMO: submit the complete, consistent sharing permission set.')
    device.sharedWorldGrants[ids.primary] = { ...grants } as Grants
    device.saveReceiveProfileIds = grants.receive ? [ids.primary] : []
    // The owner contract enables sharing and Receive + EligibleHost in one mutation.
    if (grants.receive === true && grants.eligibleHost === true) {
      state.sharingEnabled = true; state.host.settings.profiles[0].sharedSavesEnabled = true
    }
    session.rosterRevision++
    return reply(result({ message: 'DEMO: fake hosting permission saved. Select Morgan’s server to try hosting on this PC.' }))
  }
  const hostShared = `/api/local/profiles/${ids.primary}/shared-world`
  if (path === `${hostShared}/governance` && method === 'GET') return reply({ revision: session.rosterRevision, ownerOverride: true,
    members: state.devices.map(item => ({ deviceId: item.id, grants: item.sharedWorldGrants[ids.primary], revoked: item.revoked, accessExpiresUtc: item.accessExpiresUtc })) })
  if (path === hostShared && method === 'PUT') {
    if (state.mode !== 'Host' || typeof body.enabled !== 'boolean') return denied('DEMO: owner sharing changes require Host mode.')
    state.sharingEnabled = body.enabled; state.host.settings.profiles[0].sharedSavesEnabled = body.enabled
    return reply(result())
  }
  if (path === hostShared && method === 'GET') return reply({ enabled: state.sharingEnabled, latest: state.sharingEnabled ? {
    number: 5, versionHash: 'A'.repeat(64), createdUtc: '2026-10-10T15:00:00Z', captureKind: 'PostStopBackup' } : null,
    error: null, confirmedCopies: state.consented ? 1 : 0, canManageSharing: true,
    authority: { state: 'NoTakeover', message: demoBoundary, head: null, competingHeads: [], exactManagedProcessRunning: false },
    liveSave: { available: false, message: demoBoundary } })
  const friendBase = `/api/local/friend/${ids.primary}/shared-world`
  if (path === `${friendBase}/hosting` && method === 'GET') return reply(hostingStatus(session))
  if (path === `${friendBase}/hosting/prepare` && method === 'POST') {
    if (!hostingStatus(session).allowed) return denied('DEMO: the owner must allow hosting first.', 'PermissionDenied')
    if (body.allowReceiving !== true || body.acceptSplitWarning !== true) return denied('DEMO: approve receiving and the separate-history notice first.')
    if (!session.serverInstalled) {
      if (body.serverFile !== `${demoRoot}\\Valheim\\valheim_server.exe` || typeof body.gamePassword !== 'string' || body.gamePassword.length < 5)
        return denied('DEMO: Browse selects a fake server path. Enter any demo password of at least five characters; it stays only in this request.')
      session.serverInstalled = true
    }
    state.consented = true; state.receivedSaveState = 'verified'; session.prepared = true
    if (!state.host.settings.profiles.some(item => item.id === ids.third)) {
      const copy = { ...structuredClone(state.host.settings.profiles[0]), id: ids.third, name: 'Morgan’s Meadow · local DEMO copy',
        serverName: 'Morgan’s Meadow · local DEMO copy', worldId: 'demo-meadow-separate', worldDirectory: `${demoRoot}\\worlds\\separate`,
        gamePort: session.instance.valheimPort + 2, separateCopySourceProfileId: ids.primary, separateCopyBranchHash: branchHash }
      state.host.settings.profiles.push(copy)
      state.host.runs.push({ ...structuredClone(state.host.runs[0]), profileId: ids.third })
      changeRun(session, ids.third, false)
      state.host.passwordConfigured[ids.third] = true
    }
    return reply(hostingStatus(session))
  }
  const friendStart = path === `${friendBase}/recovery/separate/start`
  const ownerStart = path === `/api/local/profiles/${ids.third}/shared-world/hosting/start`
  if ((friendStart || ownerStart) && method === 'POST') {
    if (!session.prepared || !hostingStatus(session).allowed || !state.consented || body.branchHash !== branchHash || body.acceptSplitWarning !== true
      || friendStart && body.localProfileId !== ids.third || ownerStart && state.mode !== 'Host')
      return denied('DEMO: current hosting permission, prepared copy and explicit separate-history consent are required.', 'HostingChecksRequired')
    session.running = true; changeRun(session, ids.third, true)
    return reply(result({ message: 'DEMO: local server started in fake data only. No game was launched.', ...(ownerStart ? { snapshot: state.host } : {}) }))
  }
  const localAction = /^\/api\/local\/profiles\/([^/]+)\/(start|stop|restart|health|players\/refresh)$/.exec(path)
  if (localAction && method === 'POST') {
    const [, id, action] = localAction
    if (state.mode !== 'Host' || !state.host.settings.profiles.some(item => item.id === id)) return denied('DEMO: select a known local Host server.')
    if (id === ids.third && (action === 'start' || action === 'restart' && (!session.prepared || !hostingStatus(session).allowed)))
      return denied('DEMO: shared copies use the guarded Host this save action with current permission.', 'HostingChecksRequired')
    if (action === 'stop') { changeRun(session, id, false); if (id === ids.third) session.running = false }
    if (action === 'start' || action === 'restart') changeRun(session, id, true)
    if (action === 'players/refresh' && state.host.runs.find(run => run.profileId === id)?.state !== 'Offline') changeRun(session, id, true)
    return reply(result({ snapshot: state.host, ...(action === 'players/refresh' ? { code: 'PlayerCountRefreshed' } : {}) }))
  }
  return null
}

function rebaseDates(value: unknown, session: DemoSession, key = ''): unknown {
  if (Array.isArray(value)) return value.map(item => rebaseDates(item, session))
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([name, item]) => [name, rebaseDates(item, session, name)]))
  if (typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d/.test(value) && Number.isFinite(Date.parse(value))) {
    const fresh = ['lastConnectedUtc', 'lastHeartbeatUtc', 'playerCountObservedUtc', 'checkedUtc'].includes(key)
    return new Date(fresh ? Date.now() : Date.parse(value) + session.clockOffset).toISOString()
  }
  return value
}

/** Install only after a real local instance response proves this is the isolated DEMO executable. */
export function installDevelopmentDemo(instance: AppInstanceView): DevelopmentDemoController {
  if (!instance.isStaging || instance.kind !== 'Staging' || instance.isDemo !== true)
    throw new Error('Fake data requires a verified isolated DEMO app instance.')
  if (activeController) throw new Error('The DEMO transport is already installed.')
  const previousFetch = globalThis.fetch
  activeSession = makeSession('host-sharing', instance)
  let installed = true
  const fakeFetch: typeof fetch = async (input, init = {}) => {
    const session = activeSession
    if (!installed || !session) throw new Error('The DEMO transport is unavailable.')
    const request = typeof Request !== 'undefined' && input instanceof Request ? input : null
    const signal = init.signal ?? request?.signal
    if (signal?.aborted) throw new DOMException('The operation was aborted.', 'AbortError')
    const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
    let path: string
    try {
      // Absolute and scheme-relative requests are intentionally rejected, including production loopback ports.
      if (!rawUrl.startsWith('/api/local/') || rawUrl.startsWith('//')) throw new Error('outside demo')
      const url = new URL(rawUrl, 'http://demo.invalid')
      if (!url.pathname.startsWith('/api/local/')) throw new Error('outside demo')
      path = url.pathname
    } catch {
      return new Response(JSON.stringify(result({ ok: false, code: 'DemoRequestBlocked', message: 'DEMO: this request is outside the fake local API.' })),
        { status: 503, headers: { 'Content-Type': 'application/json' } })
    }
    const method = (init.method ?? request?.method ?? 'GET').toUpperCase()
    let rawBody: unknown = init.body
    if (rawBody === undefined && request && method !== 'GET' && method !== 'HEAD') rawBody = await request.clone().text()
    if (signal?.aborted) throw new DOMException('The operation was aborted.', 'AbortError')
    let response: FixtureResponse
    try {
      if (rawBody !== undefined && rawBody !== null && typeof rawBody !== 'string' || typeof rawBody === 'string' && rawBody.length > 65536)
        throw new Error('invalid body')
      const body: unknown = typeof rawBody === 'string' && rawBody ? JSON.parse(rawBody) : {}
      if (!body || typeof body !== 'object' || Array.isArray(body)) throw new Error('invalid body')
      response = demoRoute(session, path, method, body as Record<string, unknown>)
        ?? routeFixture(session.state, { path: rawUrl, method, body }) as FixtureResponse | null
        ?? { status: 503, body: result({ ok: false, code: 'DemoRequestBlocked', message: 'DEMO: this action has no fake implementation. No real request was sent.' }) }
    } catch {
      response = { status: 400, body: result({ ok: false, code: 'DemoInvalidRequest', message: 'DEMO: invalid fake request. No real request was sent.' }) }
    }
    // Fixture request history is diagnostic only; keep long-running demo sessions bounded.
    if (session.state.requests.length > 200) session.state.requests.splice(0, session.state.requests.length - 200)
    const payload = path === '/api/local/instance' ? response.body : rebaseDates(response.body, session)
    return new Response(JSON.stringify(payload), { status: response.status, headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'X-TogetherServer-Demo': '1' } })
  }
  globalThis.fetch = fakeFetch
  const controller: DevelopmentDemoController = {
    reset(scene = activeSession?.scene ?? 'host-sharing') {
      if (!installed || !Object.hasOwn(developmentDemoScenes, scene)) return
      activeSession = makeSession(scene, instance); notify()
    },
    uninstall() {
      if (!installed) return
      installed = false
      if (globalThis.fetch === fakeFetch) globalThis.fetch = previousFetch
      activeSession = null; activeController = null; notify()
    }
  }
  activeController = controller
  notify()
  return controller
}

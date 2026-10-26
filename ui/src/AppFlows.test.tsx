import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import hostWire from '../../contracts/host-snapshot.v1.json'
import { parseSnapshot, type Device, type FriendSnapshot, type HostSnapshot, type Snapshot } from './contracts'
import type { EditorDraftGuardChange } from './editorProtectedDraft'
import type { ChatDraftState } from './ServerChat'
import { readProtectedDraft } from './protectedUiDrafts'
import { serializeProtectedSetupDraft } from './setupDraft'
import type { DesktopNotificationTarget } from './desktopNotificationBridge'
import type { NotificationPreferences } from './desktopPreferencesWire'
import type { DesktopUpdateDetails } from './desktopPreferencesWire'
import type { LocalHostingStatus } from './FriendLocalHosting'
import { App } from './main'
import { directoryWithSnapshot } from './ServerDirectory'
import themeCss from './theme.css?inline'
import baseCss from './style.css?inline'
import companionCss from './companion.css?inline'
import qolCss from './qol.css?inline'
import coreCss from './core.css?inline'
import settingsCss from './settings.css?inline'
import settingsIntegrationCss from './settings-integration.css?inline'
import friendAccessCss from './friend-access.css?inline'
import redesignCss from './redesign.css?inline'
import taskFirstPagesCss from './TaskFirstPages.css?inline'
import directoryCss from './ServerDirectory.css?inline'

// vitest.config.ts explicitly enables these inline imports; load the whole cascade.
const shellCss = [themeCss, baseCss, companionCss, qolCss, coreCss, settingsCss, settingsIntegrationCss, friendAccessCss, redesignCss, taskFirstPagesCss, directoryCss].join('\n')

// Exercise the real shell, selection, guards and handlers. Leaf data/workflows have their own suites.
const transport = vi.hoisted(() => ({ read: vi.fn(), change: vi.fn(), flush: vi.fn(), failures: { logs: false, sessions: false, files: false } }))
vi.mock('./api', async importOriginal => ({
  ...await importOriginal<typeof import('./api')>(),
  getJson: (...args: unknown[]) => transport.read(...args),
  getLocalJson: (...args: unknown[]) => transport.read(...args),
  changeJson: (...args: unknown[]) => transport.change(...args)
}))
vi.mock('./protectedUiDrafts', () => ({
  readProtectedDraft: vi.fn(async () => ({ ok: true, text: null, revision: 1, message: '' })),
  saveProtectedDraft: vi.fn(async () => ({ ok: true, revision: 2, message: '' })),
  clearProtectedDraft: vi.fn(async () => ({ ok: true, revision: 2, message: '' }))
}))
vi.mock('./GameCompatibilityPanel', () => ({ GameCompatibilityPanel: () => <section aria-label="Game requirements">Before friends join</section> }))
vi.mock('./ServerChat', async () => {
  const { useEffect, useState } = await import('react')
  return {
    ServerChat: function TestChat({ profileId, onDraftStateChange }: {
      profileId: string; onDraftStateChange: (guard: ChatDraftState) => void
    }) {
      const [draft, setDraft] = useState('')
      useEffect(() => {
        onDraftStateChange({ dirty: !!draft, saving: false, recovering: false, flush: transport.flush })
        return () => onDraftStateChange({ dirty: false, saving: false, recovering: false, flush: transport.flush })
      }, [draft, onDraftStateChange])
      return <section aria-label="Server chat" data-profile-id={profileId}>Chat messages
        <label>Test chat draft<textarea value={draft} onChange={event => setDraft(event.target.value)} /></label>
      </section>
    },
    ServerChatCardSummary: () => <p>Chat summary</p>
  }
})
vi.mock('./ServerLogViewer', async importOriginal => ({
  ...await importOriginal<typeof import('./ServerLogViewer')>(),
  ServerLogViewer: ({ endpoint, compact, expectedRunId }: { endpoint: string; compact?: boolean; expectedRunId?: string }) => {
    if (transport.failures.logs) throw new Error('Synthetic pane failure')
    return <section aria-label={compact ? 'Startup console log' : 'Server logs'} data-endpoint={endpoint} data-expected-run={expectedRunId}>Diagnostic records</section>
  }
}))
vi.mock('./RecentSessions', () => ({ RecentSessions: ({ visible }: { visible: boolean }) => {
  if (visible && transport.failures.sessions) throw new Error('Synthetic pane failure')
  return <section hidden={!visible}>Recent sessions</section>
} }))
vi.mock('./WeeklySummary', () => ({ WeeklySummary: () => <section aria-label="Seven-day summary">Retained seven-day evidence</section> }))
vi.mock('./BackupCatalog', () => ({ BackupCatalog: () => <section aria-label="Backup catalog">Completed backups</section> }))
vi.mock('./SharedWorldControls', () => ({
  HostSharedSaves: ({ profileId }: { profileId: string }) => <section aria-label="Shared saves" data-profile-id={profileId}>Shared save controls</section>,
  FriendSharedWorlds: ({ profileId, serverStatusFresh, onOpenHostSetup, onOpenHostControls }: {
    profileId: string; serverStatusFresh: boolean; onOpenHostSetup?: () => void; onOpenHostControls?: () => void
  }) => <section aria-label="Shared worlds" data-profile-id={profileId} data-status-fresh={String(serverStatusFresh)}>Received copies
    {onOpenHostSetup && <button type="button" onClick={onOpenHostSetup}>Open Host setup</button>}
    {onOpenHostControls && <button type="button" onClick={onOpenHostControls}>Open Host controls</button>}
  </section>
}))
vi.mock('./WorldLoadRehearsal', async importOriginal => ({
  ...await importOriginal<typeof import('./WorldLoadRehearsal')>(),
  WorldLoadRehearsalPanel: () => <section aria-label="Game restore drill">Test a copy in the game</section>
}))
vi.mock('./GameSettingsPanel', () => ({ GameSettingsPanel: () => <section aria-label="Simple game settings">Common game settings</section> }))
vi.mock('./ServerFilesPanel', async () => {
  const { useEffect, useState } = await import('react')
  return { ServerFilesPanel: function TestFiles({ profileId, onDraftGuardChange }: { profileId: string; onDraftGuardChange: EditorDraftGuardChange }) {
    const [draft, setDraft] = useState('')
    useEffect(() => {
      onDraftGuardChange(`file:${profileId}`, draft ? { dirty: true, saving: false, flush: transport.flush } : null)
      return () => onDraftGuardChange(`file:${profileId}`, null)
    }, [draft, onDraftGuardChange, profileId])
    if (transport.failures.files) throw new Error('Synthetic pane failure')
    return <section aria-label="Server files"><label>Test file draft<input value={draft} onChange={event => setDraft(event.target.value)} /></label></section>
  } }
})

const id = hostWire.settings.profiles[0].id
let state: Snapshot
let localDirectorySnapshot: HostSnapshot
let friendDirectorySnapshot: FriendSnapshot
let recoveryBlocked: boolean
let gettingStartedState = { offered: false, intent: 'Choose', dismissed: false }
const instance = {
  kind: 'Production', displayName: 'TogetherServer', isStaging: false, freshWorldsOnly: false,
  startupAvailable: false, updatesAvailable: false, localPort: 5127, companionPort: 5131,
  valheimPort: 2456, minecraftJavaPort: 25565, minecraftBedrockPort: 19132,
  dataRoot: 'C:\\synthetic', dataIsolation: 'Synthetic fixture'
}
const companion = { listenerActive: false, listenerState: 'Idle', listenerWarning: null,
  endpoint: '', fingerprint: null, certificates: null, route: { mode: 'DirectInternet', address: '' }, devices: [], stopSafety: {} }
const ports = { control: { state: 'Idle', port: 5131, detail: 'Fixture listener is off.', remoteState: 'Unknown' }, games: [] }
const notificationPreferences: NotificationPreferences = { quietMode: false, allowedEvents: ['Lifecycle', 'Backup', 'Update'],
  servers: [], systemState: 'AcceptsNotifications', systemAllowsNotifications: true }
const host = () => state as HostSnapshot

beforeEach(() => {
  localStorage.clear()
  sessionStorage.clear()
  vi.mocked(readProtectedDraft).mockReset().mockResolvedValue({ ok: true, text: null, revision: 1, message: '' })
  recoveryBlocked = false
  gettingStartedState = { offered: false, intent: 'Choose', dismissed: false }
  transport.failures.logs = transport.failures.sessions = transport.failures.files = false
  transport.flush.mockReset().mockResolvedValue(true)
  state = parseSnapshot(structuredClone(hostWire))
  host().settings.publicGameIp = '192.0.2.10'
  host().settings.publicGameIpCheckedUtc = new Date().toISOString()
  host().runs[0].state = 'Offline'
  host().runs[0].detail = 'The exact recorded process is offline.'
  host().runs[0].playerCountObservedUtc = new Date().toISOString()
  localDirectorySnapshot = host()
  friendDirectorySnapshot = friend()
  transport.read.mockReset().mockImplementation(async (path: string) => {
    if (path === '/api/local/snapshot') return state
    // The canonical endpoint admits locations independently of the UI's cache.
    if (path === '/api/local/servers') return {
      localServers: directoryWithSnapshot(null, state.mode === 'Host' ? state : localDirectorySnapshot).localServers,
      friendHosts: directoryWithSnapshot(null, state.mode === 'Friend' ? state : friendDirectorySnapshot).friendHosts
    }
    if (path === '/api/local/profile') return { displayName: 'Alex' }
    if (path === '/api/local/getting-started') return gettingStartedState
    if (path === '/api/local/required-setup') return { features: [{ id: 'profile-name', revision: 1, title: 'Your name', description: 'Share your name.', completed: true }] }
    if (path === '/api/local/data-recovery') return { lifecycleBlocked: recoveryBlocked, notices: [] }
    if (path === '/api/local/instance') return instance
    if (path === '/api/local/companion') return companion
    if (path === '/api/local/network/ports') return ports
    if (path === '/api/local/desktop/preferences') return { available: false, launchAtLogin: false, closeToTray: false, startupAvailable: false }
    if (path === '/api/local/desktop/notifications') return structuredClone(notificationPreferences)
    if (path.endsWith('/shared-world/hosting/access')) return { allowed: false, code: 'HostingPermissionRequired', message: 'The owner has not allowed hosting.' }
    throw new Error('Synthetic leaf data unavailable')
  })
  transport.change.mockReset().mockImplementation(async (path: string, _method: string, _decoder: unknown, body: Record<string, unknown>) => {
    if (path === '/api/local/getting-started') {
      gettingStartedState = { offered: true, intent: String(body.intent), dismissed: body.dismissed === true }
      return gettingStartedState
    }
    if (path === '/api/local/mode/friend') { if (state.mode === 'Host') localDirectorySnapshot = state; state = friendDirectorySnapshot }
    if (path === '/api/local/mode/host') { if (state.mode === 'Friend') friendDirectorySnapshot = state; state = localDirectorySnapshot }
    return { ok: true, code: 'Requested', message: 'Request accepted.', snapshot: state }
  })
  vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('No network in shell tests') }))
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { callback(0); return 1 })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {})
  Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { configurable: true, value: vi.fn() })
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: function (this: HTMLDialogElement) { this.setAttribute('open', '') } })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: function (this: HTMLDialogElement) { this.removeAttribute('open') } })
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: vi.fn(async () => {}) } })
  document.head.querySelector('[data-flow-styles]')?.remove()
  const styles = document.createElement('style')
  styles.dataset.flowStyles = 'true'
  styles.textContent = shellCss
  document.head.append(styles)
})

async function openHost() {
  render(<App />)
  const workspace = await screen.findByRole('region', { name: 'Contract fixture workspace' })
  // The shell appears before its initial selection/preferences effects finish.
  await act(async () => { await Promise.resolve() })
  return workspace
}

function before(first: Element, second: Element) {
  expect(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
}

function expectNoServerAction() {
  expect(transport.change.mock.calls.filter(([path]) => /\/profiles\/[^/]+\/(start|stop|restart|safe-restart)$/.test(path))).toHaveLength(0)
}

// Public-IP discovery is an existing startup observation, not a user mutation.
function mutationCalls() {
  return transport.change.mock.calls.filter(([path]) => path !== '/api/local/network/detect-public-ip')
}

function expectNoMutation() {
  expect(mutationCalls()).toHaveLength(0)
}

describe('installer guide handoff in the real shell', () => {
  it('offers the selected Host guide after required setup and highlights the real New server action', async () => {
    gettingStartedState = { offered: true, intent: 'Host', dismissed: false }
    await openHost()
    const welcome = await screen.findByRole('dialog', { name: 'Make room for your next game night' })
    fireEvent.click(within(welcome).getByRole('button', { name: /Host on this PC/ }))
    expect(await screen.findByRole('heading', { name: 'Make this PC your Host' })).toBeVisible()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(gettingStartedState).toEqual({ offered: true, intent: 'Host', dismissed: true })
    expectNoServerAction()
    fireEvent.click(screen.getByRole('button', { name: 'New server' }))
    expect(await screen.findByRole('dialog', { name: 'Add new server' })).toBeVisible()
    expect(screen.queryByRole('heading', { name: 'Make this PC your Host' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Setup tip')).toBeVisible()
  }, 10_000)
  it('takes Join through the real code field without installing or starting a game', async () => {
    gettingStartedState = { offered: true, intent: 'Join', dismissed: false }
    await openHost()
    const welcome = await screen.findByRole('dialog', { name: 'Make room for your next game night' })
    fireEvent.click(within(welcome).getByRole('button', { name: /Join a friend/ }))
    expect(await screen.findByRole('heading', { name: 'Start with your friend’s server code' })).toBeVisible()
    expect(screen.getByLabelText('Server code')).toBeVisible()
    expect(state.mode).toBe('Friend')
    expect(gettingStartedState).toEqual({ offered: true, intent: 'Join', dismissed: true })
    expectNoServerAction()
    expect(mutationCalls().map(([path]) => path)).toEqual(['/api/local/mode/friend', '/api/local/getting-started'])
  })
  it('keeps guides available for an existing installation and lets setup wait', async () => {
    await openHost()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Setup guides' }))
    const welcome = await screen.findByRole('dialog', { name: 'Make room for your next game night' })
    fireEvent.click(within(welcome).getByRole('button', { name: 'Set up later' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expectNoServerAction()
    expect(mutationCalls().map(([path]) => path)).toEqual(['/api/local/getting-started'])
  })
})

describe('required app setup shell gate', () => {
  const nameFeature = { id: 'profile-name', revision: 1, title: 'Your name', description: 'Share a name your friends recognize.', completed: false }
  function pendingSetup(initialName = '') {
    let displayName = initialName
    let completed = false
    let setupFailure = ''
    let completionFailure = ''
    let checkFailureAfterSave = false
    let extraFeature = false
    const read = transport.read.getMockImplementation()!
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => {
      if (path === '/api/local/profile') return { displayName }
      if (path === '/api/local/required-setup') {
        if (setupFailure || checkFailureAfterSave && displayName) throw new Error(setupFailure || 'Setup check unavailable')
        return { features: [{ ...nameFeature, completed: completed || !!displayName },
          ...(extraFeature ? [{ ...nameFeature, id: 'later-feature', title: 'Later feature' }] : [])] }
      }
      return read(path, ...args)
    })
    transport.change.mockImplementation(async (path: string, _method: string, _decoder: unknown, body: Record<string, unknown>) => {
      if (path === '/api/local/profile') {
        displayName = String(body.displayName)
        return { ok: true, code: 'Saved', message: 'Saved', displayName }
      }
      if (path === '/api/local/required-setup/profile-name/complete') {
        if (completionFailure) return { ok: false, code: 'Unavailable', message: completionFailure, features: [nameFeature] }
        completed = true
        return { ok: true, code: 'Completed', message: 'Completed', features: [{ ...nameFeature, completed }] }
      }
      return { ok: true, code: 'Requested', message: 'Request accepted.', snapshot: state }
    })
    return {
      failLoad: (value: string) => { setupFailure = value },
      failCompletion: (value: string) => { completionFailure = value },
      failCheckAfterSave: (value: boolean) => { checkFailureAfterSave = value },
      addLaterFeature: () => { extraFeature = true }
    }
  }

  it.each(['fresh installation', 'legacy upgrade'])('requires a name on %s and blocks shortcuts and notification navigation until completion', async () => {
    pendingSetup()
    const notify = nativeNotifications()
    localStorage.setItem('together-required-setup-complete', 'true')
    render(<App />)
    await screen.findByRole('heading', { name: 'Your name' })
    await waitFor(() => expect(screen.getByLabelText('Your name')).toBeEnabled())
    expect(screen.queryByRole('navigation', { name: 'TogetherServer navigation' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'App settings' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Commands' })).not.toBeInTheDocument()
    fireEvent.keyDown(window, { key: 'k', ctrlKey: true })
    fireEvent.keyDown(window, { key: '2', altKey: true })
    fireEvent.keyDown(window, { key: '4', altKey: true })
    await notify({ workspace: 'join', section: 'play', profileId: id, connectionId: secondPcId })
    expect(screen.queryByRole('dialog', { name: /command/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Contract fixture workspace' })).not.toBeInTheDocument()
    expect(transport.change.mock.calls.some(([path]) => path.startsWith('/api/local/mode/'))).toBe(false)
    expect(screen.getByText('Save and continue')).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: '  Sam  ' } })
    fireEvent.click(screen.getByText('Save and continue'))
    await screen.findByRole('region', { name: 'Contract fixture workspace' })
    expect(transport.change.mock.calls.find(([path]) => path === '/api/local/profile')?.[3]).toEqual({ displayName: 'Sam' })
    expect(transport.change.mock.calls.find(([path]) => path.endsWith('/complete'))?.[3]).toEqual({ revision: 1 })
    expectNoServerAction()
  })

  it('skips setup for a preserved name and lets the owner edit it in normal Profile settings', async () => {
    pendingSetup('Alex')
    await openHost()
    expect(screen.queryByRole('region', { name: 'Required app setup' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Edit profile' }))
    expect(await screen.findByRole('heading', { name: 'Profile' })).toBeInTheDocument()
    expect(screen.getByLabelText('Your name')).toHaveValue('Alex')
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'Sam' } })
    fireEvent.click(screen.getByText('Save name'))
    await screen.findByText('Name saved and shared with connected PCs.')
    expectNoServerAction()
  })

  it('keeps setup visible after a failed initial check and rechecks before showing any workspace', async () => {
    const setup = pendingSetup()
    setup.failLoad('Setup unavailable')
    render(<App />)
    await screen.findByText('Setup unavailable')
    expect(screen.queryByRole('navigation', { name: 'TogetherServer navigation' })).not.toBeInTheDocument()
    setup.failLoad('')
    fireEvent.click(screen.getByText('Retry setup'))
    await waitFor(() => expect(screen.getByLabelText('Your name')).toBeEnabled())
    setup.failCheckAfterSave(true)
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'Sam' } })
    fireEvent.click(screen.getByText('Save and continue'))
    await screen.findByText('Setup check unavailable')
    expect(screen.getByLabelText('Your name')).toHaveValue('Sam')
    expect(screen.queryByRole('navigation', { name: 'TogetherServer navigation' })).not.toBeInTheDocument()
    setup.failCheckAfterSave(false)
    fireEvent.click(screen.getByText('Retry setup'))
    await screen.findByRole('region', { name: 'Contract fixture workspace' })
  })

  it('preserves the name after completion failure and shows a later required feature after restart', async () => {
    const setup = pendingSetup()
    setup.failCompletion('Completion unavailable')
    const view = render(<App />)
    await waitFor(() => expect(screen.getByLabelText('Your name')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'Sam' } })
    fireEvent.click(screen.getByText('Save and continue'))
    await screen.findByText('Completion unavailable')
    expect(screen.getByLabelText('Your name')).toHaveValue('Sam')
    expect(screen.queryByRole('navigation', { name: 'TogetherServer navigation' })).not.toBeInTheDocument()
    view.unmount()
    setup.addLaterFeature()
    render(<App />)
    await screen.findByRole('alert')
    expect(screen.getByRole('region', { name: 'Required app setup' })).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'TogetherServer navigation' })).not.toBeInTheDocument()
    expectNoServerAction()
  })

  it('keeps confirmed graceful Stop available during setup without revealing Start, Restart or ordinary navigation', async () => {
    pendingSetup()
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 2
    render(<App />)
    await screen.findByRole('heading', { name: 'Your name' })
    fireEvent.click(screen.getByText('Existing hosting and recovery'))
    fireEvent.click(await screen.findByRole('button', { name: 'Stop server' }))
    const confirmation = await screen.findByRole('dialog', { name: 'Stop Contract fixture?' })
    expect(confirmation).toHaveTextContent('2 players')
    expectNoServerAction()
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Stop server' }))
    await waitFor(() => expect(transport.change.mock.calls.filter(([path]) => path.endsWith('/stop'))).toHaveLength(1))
    expect(screen.getByRole('region', { name: 'Required app setup' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'TogetherServer navigation' })).not.toBeInTheDocument()
  })

  it('can review this PC hosting from a saved Join page while required setup stays gated', async () => {
    const hosting = structuredClone(host())
    hosting.runs[0].state = 'Ready'
    state = friend()
    pendingSetup()
    const change = transport.change.getMockImplementation()!
    transport.change.mockImplementation(async (path: string, ...args: unknown[]) => {
      if (path === '/api/local/mode/host') {
        state = hosting
        return { ok: true, code: 'ModeChanged', message: 'Hosting view selected.', snapshot: state }
      }
      return change(path, ...args)
    })
    render(<App />)
    await screen.findByRole('heading', { name: 'Your name' })
    await waitFor(() => expect(screen.getByLabelText('Your name')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'Unfinished name' } })
    fireEvent.click(screen.getByText('Existing hosting and recovery'))
    fireEvent.click(await screen.findByRole('button', { name: 'Review hosting on this PC' }))
    expect(await screen.findByRole('button', { name: 'Stop server' })).toBeInTheDocument()
    expect(screen.getByLabelText('Your name')).toHaveValue('Unfinished name')
    expect(screen.getByRole('region', { name: 'Required app setup' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'TogetherServer navigation' })).not.toBeInTheDocument()
    expectNoServerAction()
  })

  it('exposes existing local recovery while setup is blocked and never reveals the workspace through acknowledgement', async () => {
    pendingSetup()
    recoveryBlocked = true
    const read = transport.read.getMockImplementation()!
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => path === '/api/local/data-recovery'
      ? { lifecycleBlocked: recoveryBlocked, notices: [{ stateFile: 'required-setup.protected', quarantinedFile: 'required-setup.bad', reason: 'Synthetic unreadable setup record', detectedUtc: '2026-10-18T12:00:00Z', blocksLifecycle: true }] }
      : read(path, ...args))
    render(<App />)
    await screen.findByRole('heading', { name: 'Your name' })
    fireEvent.click(screen.getByText('Existing hosting and recovery'))
    await screen.findByRole('heading', { name: 'Local data needs review' })
    fireEvent.click(screen.getByRole('checkbox', { name: 'I confirm no game server managed by TogetherServer is still running.' }))
    fireEvent.click(screen.getByRole('button', { name: 'Confirm and re-enable server controls' }))
    await waitFor(() => expect(transport.change.mock.calls.some(([path]) => path === '/api/local/data-recovery/acknowledge')).toBe(true))
    expect(screen.getByRole('region', { name: 'Required app setup' })).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'TogetherServer navigation' })).not.toBeInTheDocument()
    expectNoServerAction()
  })
})

function serverSections() {
  return within(screen.getByRole('navigation', { name: 'Selected server sections' }))
}

function joinSections() {
  return serverSections()
}

function directory() { return within(screen.getByRole('complementary', { name: 'Server navigation' })) }

function pcSections() {
  const disclosure = directory().getByText('This PC settings').closest('details')!
  if (!disclosure.open) fireEvent.click(disclosure.querySelector('summary')!)
  return within(directory().getByRole('navigation', { name: 'This Host PC sections' }))
}

function workspaces() {
  return within(screen.getByRole('navigation', { name: 'TogetherServer navigation' }))
}

async function openServerTools(workspace: HTMLElement) {
  fireEvent.click(within(workspace).getByRole('button', { name: 'More server actions' }))
  return screen.findByRole('dialog', { name: 'Server tools' })
}

// Deliver the production bridge message shape without opening a native window.
function nativeNotifications() {
  const listeners = new Set<(event: { data: unknown }) => void>()
  vi.stubGlobal('chrome', { webview: {
    addEventListener: (_type: string, listener: (event: { data: unknown }) => void) => listeners.add(listener),
    removeEventListener: (_type: string, listener: (event: { data: unknown }) => void) => listeners.delete(listener),
    postMessage: vi.fn()
  } })
  return async (destination: DesktopNotificationTarget) => {
    await waitFor(() => expect(listeners.size).toBeGreaterThan(0))
    await act(async () => {
      listeners.forEach(listener => listener({ data: { type: 'together-notification', destination } }))
    })
  }
}

describe('Task-first app and Host PC navigation', () => {
  it('keeps update review open while a reminder save is pending, then permits closing after failure', async () => {
    const update: DesktopUpdateDetails = { state: 'Available', currentVersion: '0.3.2', latestVersion: '0.4.0',
      message: 'Available.', publisherTrust: 'GitHub digest only', includePrereleases: false, latestIsPrerelease: false, releaseNotes: '', releaseNotesUrl: null,
      promptSnoozed: false, snoozedUntilUtc: null }
    const read = transport.read.getMockImplementation()!
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => path === '/api/local/update' ? update
      : path === '/api/local/instance' ? { ...instance, updatesAvailable: true } : read(path, ...args))
    let finish!: (result: { ok: boolean; message: string }) => void
    transport.change.mockImplementation((path: string) => {
      if (path === '/api/local/update/snooze') return new Promise(resolve => { finish = resolve })
      if (path === '/api/local/update/check') return Promise.resolve(update)
      return Promise.resolve({ ok: true, code: 'Requested', message: 'Request accepted.', snapshot: state })
    })
    await openHost()
    fireEvent.click(workspaces().getByRole('button', { name: 'App Settings' }))
    fireEvent.click(within(await screen.findByRole('navigation', { name: 'App settings sections' })).getByRole('button', { name: 'Updates' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Review update' }))
    const dialog = await screen.findByRole('dialog', { name: 'Update TogetherServer' })
    fireEvent.change(within(dialog).getByRole('combobox', { name: 'Remind me' }), { target: { value: 'OneHour' } })
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Not now' })).toBeDisabled())
    fireEvent(dialog, new Event('cancel', { cancelable: true }))
    expect(dialog).toHaveAttribute('open')
    expect(screen.getByRole('dialog')).toBe(dialog)
    await act(async () => { finish({ ok: false, message: 'Could not save reminder.' }) })
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Not now' })).toBeEnabled())
    fireEvent.click(within(dialog).getByRole('button', { name: 'Not now' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expectNoServerAction()
  })

  it('allows a lifecycle warning to close while keeping Start blocked by canonical recovery', async () => {
    recoveryBlocked = true
    const workspace = await openHost()
    const message = await within(workspace).findByText('Review data recovery before starting or restarting.')
    const warning = message.closest('.dismissible-notice') as HTMLElement
    expect(warning).not.toBeNull()
    expect(within(workspace).getByRole('button', { name: 'Start server' })).toBeDisabled()
    fireEvent.click(within(warning).getByRole('button', { name: 'Dismiss notification' }))
    expect(within(workspace).queryByText('Review data recovery before starting or restarting.')).not.toBeInTheDocument()
    expect(within(workspace).getByRole('button', { name: 'Start server' })).toBeDisabled()
    expectNoMutation()
  })

  it('keeps dismissed recovery off Host and global Settings while retaining an Attention review', async () => {
    host().startupRecovery = { previousSessionInterrupted: true, previousStartedUtc: '2026-10-10T12:00:00Z',
      items: [], message: 'No previously active saved server needs action.', windowsRestartRegistered: true }
    await openHost()
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss app recovery notice' }))
    expect(screen.queryByRole('region', { name: 'App reopened after an interruption' })).not.toBeInTheDocument()
    fireEvent.click(workspaces().getByRole('button', { name: 'App Settings' }))
    await screen.findByRole('region', { name: 'App Settings' })
    expect(screen.queryByRole('region', { name: 'App reopened after an interruption' })).not.toBeInTheDocument()
    fireEvent.click(workspaces().getByRole('button', { name: 'Servers' }))
    await screen.findByRole('region', { name: 'Contract fixture workspace' })
    expect(screen.queryByRole('region', { name: 'App reopened after an interruption' })).not.toBeInTheDocument()
    const notifications = workspaces().getByLabelText(/^Notifications/)
    fireEvent.click(notifications)
    fireEvent.click(workspaces().getByRole('button', { name: 'Open Attention Center' }))
    expect(notifications).toHaveFocus()
    expect(notifications.closest('details')).not.toHaveAttribute('open')
    fireEvent.click(await screen.findByText('Session recovery'))
    expect(screen.getByRole('region', { name: 'App reopened after an interruption' })).toBeVisible()
    expectNoMutation()
  })

  it('keeps global Settings and sidebar widths after switching from Host to Join and back', async () => {
    const savedHost = structuredClone(host())
    transport.change.mockImplementation(async (path: string) => {
      if (path === '/api/local/mode/friend') state = friend()
      if (path === '/api/local/mode/host') state = savedHost
      return { ok: true, code: 'Requested', message: 'Mode changed.', snapshot: state }
    })
    await openHost()
    expect(document.querySelector('.workspace-nav')).toBeNull()
    expect(screen.getByRole('navigation', { name: 'TogetherServer navigation' }).closest('header')).not.toBeNull()
    fireEvent.keyDown(screen.getByRole('separator'), { key: 'ArrowRight' })
    for (const mode of ['Host', 'Join', 'Host']) {
      if (mode === 'Join') {
        await act(async () => { fireEvent.click(workspaces().getByRole('button', { name: 'Servers' })) })
        const friendServer = await screen.findByRole('button', { name: 'Open Synthetic world on Evening Host' })
        await act(async () => { fireEvent.click(friendServer) })
        await screen.findByRole('region', { name: 'Play' })
      } else if (state.mode === 'Friend') {
        await act(async () => { fireEvent.click(workspaces().getByRole('button', { name: 'Servers' })) })
        const localServer = await screen.findByRole('button', { name: 'Open Contract fixture on this PC' })
        await act(async () => { fireEvent.click(localServer) })
        await screen.findByRole('region', { name: 'Contract fixture workspace' })
      }
      await act(async () => { await Promise.resolve() })
      expect(screen.getByRole('separator')).toHaveAttribute('aria-valuenow', '278')
      await act(async () => { fireEvent.click(workspaces().getByRole('button', { name: 'App Settings' })) })
      const settings = await screen.findByRole('region', { name: 'App Settings' })
      expect(screen.getByRole('separator')).toHaveAttribute('aria-valuenow', '268')
      expect(within(settings).getByRole('heading', { name: 'Appearance' })).toBeVisible()
      const sections = within(within(settings).getByRole('navigation', { name: 'App settings sections' }))
      expect(sections.getAllByRole('button').map(button => button.textContent)).toEqual([
        'Profile', 'Appearance', 'Notifications', 'Windows behavior', 'Updates', 'Keyboard shortcuts'
      ])
      await act(async () => { fireEvent.click(sections.getByRole('button', { name: 'Windows behavior' })) })
      expect(await within(settings).findByRole('heading', { name: 'Windows behavior' })).toBeVisible()
      expect(within(settings).queryByRole('checkbox', { name: /awake/i })).not.toBeInTheDocument()
      expect(screen.queryByRole('navigation', { name: 'This Host PC sections' })).not.toBeInTheDocument()
      expect(screen.queryByRole('region', { name: 'This Host PC' })).not.toBeInTheDocument()
    }
    expect(mutationCalls().map(([path]) => path)).toEqual(['/api/local/mode/friend', '/api/local/mode/host'])
    expectNoServerAction()
  }, 10_000)

  it.each(['Host', 'Friend'] as const)('opens continuous App Settings from the header gear in %s without changing mode', async mode => {
    if (mode === 'Friend') state = friend()
    render(<App />)
    await screen.findByRole('region', { name: mode === 'Host' ? 'Contract fixture workspace' : 'Play' })
    await act(async () => { await Promise.resolve() })
    fireEvent.click(within(document.querySelector('.header-tools')! as HTMLElement)
      .getByRole('button', { name: 'App Settings' }))
    const settings = await screen.findByRole('region', { name: 'App Settings' })
    expect(within(settings).getByRole('heading', { name: 'Appearance' })).toBeVisible()
    const sections = within(settings).getByRole('navigation', { name: 'App settings sections' })
    expect(within(sections).getAllByRole('button').map(button => button.textContent)).toEqual([
      'Profile', 'Appearance', 'Notifications', 'Windows behavior', 'Updates', 'Keyboard shortcuts'
    ])
    expect(within(sections).getByRole('button', { name: 'Profile' })).toHaveAttribute('aria-current', 'location')
    expect(within(settings).getByRole('combobox', { name: 'Theme' })).toBeVisible()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(state.mode).toBe(mode)
    expect(transport.change.mock.calls.filter(([path]) => path.startsWith('/api/local/mode/'))).toHaveLength(0)
    expectNoMutation()
    expectNoServerAction()
  })

  it.each(['Host', 'Friend'] as const)('opens the same continuous App Settings from %s without changing mode', async mode => {
    if (mode === 'Friend') state = friend()
    render(<App />)
    await screen.findByRole('region', { name: mode === 'Host' ? 'Contract fixture workspace' : 'Play' })
    await act(async () => { await Promise.resolve() })
    fireEvent.click(workspaces().getByRole('button', { name: 'App Settings' }))
    const settings = await screen.findByRole('region', { name: 'App Settings' })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(within(settings).getByRole('heading', { name: 'Appearance' })).toBeVisible()
    const sections = within(settings).getByRole('navigation', { name: 'App settings sections' })
    expect(within(sections).getAllByRole('button').map(button => button.textContent)).toEqual([
      'Profile', 'Appearance', 'Notifications', 'Windows behavior', 'Updates', 'Keyboard shortcuts'
    ])
    expect(within(sections).getByRole('button', { name: 'Profile' })).toHaveAttribute('aria-current', 'location')
    expect(within(settings).getByRole('combobox', { name: 'Theme' })).toHaveValue('dark')
    expect(within(settings).getByRole('combobox', { name: 'Layout' })).toHaveValue('comfortable')
    expect(within(settings).getByRole('combobox', { name: 'Text size' })).toHaveValue('100')
    expect(within(settings).queryByRole('checkbox', { name: /Keep.*awake/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'This Host PC sections' })).not.toBeInTheDocument()
    fireEvent.change(within(settings).getByRole('combobox', { name: 'Theme' }), { target: { value: 'light' } })
    fireEvent.change(within(settings).getByRole('combobox', { name: 'Layout' }), { target: { value: 'compact' } })
    await waitFor(() => expect(document.documentElement).toHaveAttribute('data-theme', 'light'))
    expect(JSON.parse(localStorage.getItem('togetherserver.ui-preferences.v1')!)).toMatchObject({ theme: 'light', density: 'compact' })
    expectNoMutation()
    await act(async () => {
      fireEvent.click(workspaces().getByRole('button', { name: 'Servers' }))
    })
    await screen.findByRole('region', { name: mode === 'Host' ? 'Contract fixture workspace' : 'Play' })
    expect(state.mode).toBe(mode)
    await act(async () => {
      fireEvent.click(workspaces().getByRole('button', { name: 'App Settings' }))
    })
    expect(await screen.findByRole('combobox', { name: 'Theme' })).toHaveValue('light')
    expect(screen.getByRole('combobox', { name: 'Layout' })).toHaveValue('compact')
    expectNoMutation()
  })

  it.each(['Host', 'Friend'] as const)('sets a shared name from the %s sidebar and keeps the draft between Settings pages', async mode => {
    if (mode === 'Friend') state = friend()
    transport.change.mockImplementation(async (...args: unknown[]) =>
      args[0] === '/api/local/profile'
        ? { ok: true, code: 'ProfileSaved', message: 'Saved', displayName: (args[3] as { displayName: string }).displayName }
        : { ok: true, code: 'Requested', message: 'Request accepted.', snapshot: state })
    render(<App />)
    await screen.findByRole('region', { name: mode === 'Host' ? 'Contract fixture workspace' : 'Play' })
    await act(async () => { await Promise.resolve() })
    fireEvent.click(screen.getByRole('button', { name: 'Edit profile' }))
    const settings = await screen.findByRole('region', { name: 'App Settings' })
    const navigation = within(within(settings).getByRole('navigation', { name: 'App settings sections' }))
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Your name' })).toBeEnabled())
    fireEvent.change(screen.getByRole('textbox', { name: 'Your name' }), { target: { value: 'Sam' } })
    fireEvent.click(navigation.getByRole('button', { name: 'Appearance' }))
    fireEvent.click(navigation.getByRole('button', { name: 'Profile' }))
    expect(screen.getByRole('textbox', { name: 'Your name' })).toHaveValue('Sam')
    fireEvent.click(screen.getByRole('button', { name: 'Save name' }))
    await screen.findByText('Name saved and shared with connected PCs.')
    fireEvent.click(workspaces().getByRole('button', { name: 'Servers' }))
    await screen.findByRole('region', { name: mode === 'Host' ? 'Contract fixture workspace' : 'Play' })
    expect(screen.getByText('Sharing as', { exact: false })).toHaveTextContent('Sharing as Sam')
    expect(mutationCalls().map(([path]) => path)).toEqual(['/api/local/profile'])
    expectNoServerAction()
    expect(state.mode).toBe(mode)
  })

  it('opens Friend access from This Host PC and returns to the same selected server', async () => {
    const workspace = await openHost()
    const sections = pcSections()
    expect(sections.getAllByRole('button').map(button => button.textContent)).toEqual([
      'Access', 'Shutdown', 'Network', 'Advanced'
    ])
    fireEvent.click(sections.getByRole('button', { name: 'Access' }))
    const pc = await screen.findByRole('region', { name: 'This Host PC' })
    expect(workspaces().getByRole('button', { name: 'Servers' })).toHaveAttribute('aria-current', 'page')
    expect(sections.getByRole('button', { name: 'Access' })).toHaveAttribute('aria-current', 'page')
    expect(workspace).not.toBeInTheDocument()
    expect(within(pc).getByRole('heading', { name: 'Friend access' })).toBeVisible()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    fireEvent.click(within(pc).getByRole('button', { name: 'Return to selected server' }))
    await screen.findByRole('region', { name: 'Contract fixture workspace' })
    expect(serverSections().getByRole('button', { name: 'Overview' })).toHaveAttribute('aria-current', 'page')
    expectNoServerAction()
  })

  it.each(['rejected', 'unavailable'] as const)('shows a %s connection policy save beside Friend access and permits retry', async failure => {
    host().settings.companionListeningEnabled = false
    host().settings.remoteControlsEnabled = false
    const message = failure === 'rejected'
      ? 'Choose Invite friends to set up the secure Friend connection.'
      : 'Could not connect to TogetherServer. Reopen the app and try again.'
    const change = transport.change.getMockImplementation()!
    let attempts = 0
    transport.change.mockImplementation(async (path: string, ...args: unknown[]) => {
      if (path !== '/api/local/settings/control-policy') return change(path, ...args)
      if (++attempts === 1) {
        if (failure === 'unavailable') throw new Error(message)
        return { ok: false, code: 'PairingRequired', message, snapshot: state }
      }
      state = { ...host(), settings: { ...host().settings, companionListeningEnabled: true } }
      localDirectorySnapshot = state
      return { ok: true, code: 'SettingsSaved', message: 'Friend connection choice saved.', snapshot: state }
    })

    await openAccess()
    const pc = screen.getByRole('region', { name: 'This Host PC' })
    const listening = within(pc).getByRole('checkbox', { name: /Allow Friend app connections/ })
    fireEvent.click(listening)

    const feedback = await within(pc).findByText(message)
    expect(feedback).toBeVisible()
    expect(feedback).toHaveAttribute('role', 'status')
    expect(listening).not.toBeChecked()
    await waitFor(() => expect(listening).toBeEnabled())
    expect(transport.change.mock.calls.filter(([path]) => path === '/api/local/settings/control-policy'))
      .toEqual([['/api/local/settings/control-policy', 'PUT', expect.any(Function), { companionListeningEnabled: true }, undefined]])

    fireEvent.click(listening)
    await waitFor(() => expect(listening).toBeChecked())
    expect(within(pc).getByText('Friend connection choice saved.')).toBeVisible()
    expect(attempts).toBe(2)
    expectNoServerAction()
  })

  it.each([
    ['access', 'Access'], ['stop', 'Shutdown'], ['network', 'Network'], ['advanced', 'Advanced']
  ])('routes the old native Settings/%s alias to This Host PC', async (section, label) => {
    const notify = nativeNotifications()
    await openHost()
    await notify({ workspace: 'settings', section, profileId: null, connectionId: null })
    await screen.findByRole('region', { name: 'This Host PC' })
    expect(workspaces().getByRole('button', { name: 'Servers' })).toHaveAttribute('aria-current', 'page')
    expect(pcSections().getByRole('button', { name: label }))
      .toHaveAttribute('aria-current', 'page')
    expect(screen.queryByRole('region', { name: 'App Settings' })).not.toBeInTheDocument()
    expectNoMutation()
  })

  it('keeps legacy native diagnostics links reachable inside PC settings without a separate navigation item', async () => {
    const notify = nativeNotifications()
    await openHost()
    await notify({ workspace: 'settings', section: 'diagnostics', profileId: id, connectionId: null })
    await screen.findByRole('heading', { name: 'Server diagnostics' })
    expect(pcSections().queryByRole('button', { name: 'Diagnostics' })).not.toBeInTheDocument()
    expectNoMutation()
  })

  it('keeps the old native Settings/app alias mode-independent and opens Appearance', async () => {
    const notify = nativeNotifications()
    state = friend()
    // Settle the initial Friend/setup effects before delivering the native message once.
    await act(async () => { render(<App />) })
    await screen.findByRole('region', { name: 'Play' })
    await notify({ workspace: 'settings', section: 'app', profileId: null, connectionId: null })
    const settings = await screen.findByRole('region', { name: 'App Settings' })
    expect(within(settings).getByRole('heading', { name: 'Appearance' })).toBeVisible()
    expect(state.mode).toBe('Friend')
    expectNoMutation()
  })

  it('resolves an old native Host PC alias from Join through an explicit Host mode switch', async () => {
    const notify = nativeNotifications()
    const savedHost = structuredClone(host())
    state = friend()
    transport.change.mockImplementation(async (path: string) => {
      if (path === '/api/local/mode/host') state = savedHost
      return { ok: true, code: 'Requested', message: 'Mode changed.', snapshot: state }
    })
    render(<App />)
    await screen.findByRole('region', { name: 'Play' })
    await notify({ workspace: 'settings', section: 'access', profileId: null, connectionId: null })
    await screen.findByRole('region', { name: 'This Host PC' })
    expect(workspaces().getByRole('button', { name: 'Servers' })).toHaveAttribute('aria-current', 'page')
    expect(transport.change).toHaveBeenCalledWith('/api/local/mode/host', 'POST', expect.any(Function), undefined, undefined)
    expect(mutationCalls()).toHaveLength(1)
    expectNoServerAction()
  })

  it.each(['server', 'pc'] as const)('holds the %s scope picker destination until the protected file draft flush succeeds', async destination => {
    const secondId = '33333333-3333-4333-8333-333333333333'
    host().settings.profiles.push({ ...host().settings.profiles[0], id: secondId, name: 'Second synthetic server' })
    const workspace = await openHost()
    fireEvent.click(serverSections().getByRole('button', { name: 'Settings & files' }))
    fireEvent.click(await within(workspace).findByRole('tab', { name: 'Server files' }))
    const editor = await within(workspace).findByRole('textbox', { name: 'Test file draft' })
    fireEvent.change(editor, { target: { value: 'Protected final keystroke' } })
    const navigate = () => {
      if (destination === 'pc') {
        fireEvent.click(pcSections().getByRole('button', { name: 'Access' }))
      } else {
        fireEvent.click(directory().getByRole('button', { name: 'Open Second synthetic server on this PC' }))
      }
    }
    transport.flush.mockResolvedValue(false)
    navigate()
    await waitFor(() => expect(transport.flush).toHaveBeenCalledOnce())
    expect(editor).toHaveValue('Protected final keystroke')
    expect(screen.getByRole('region', { name: 'Contract fixture workspace' })).toHaveAttribute('data-server-tab', 'files')
    expect(screen.queryByRole('region', { name: 'This Host PC' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Second synthetic server workspace' })).not.toBeInTheDocument()
    let finish!: (saved: boolean) => void
    transport.flush.mockImplementationOnce(() => new Promise<boolean>(resolve => { finish = resolve }))
    navigate()
    await waitFor(() => expect(transport.flush).toHaveBeenCalledTimes(2))
    expect(editor).toBeInTheDocument()
    await act(async () => { finish(true) })
    await screen.findByRole('region', { name: destination === 'pc' ? 'This Host PC' : 'Second synthetic server workspace' })
    expectNoServerAction()
  })

  it.each(['section', 'workspace', 'native'] as const)('guards %s navigation while a notification default is saving', async destination => {
    const notify = nativeNotifications()
    const read = transport.read.getMockImplementation()!
    let preferences = structuredClone(notificationPreferences)
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => path === '/api/local/desktop/notifications'
      ? preferences : read(path, ...args))
    let finish!: (value: NotificationPreferences) => void
    transport.change.mockImplementation((path: string) => {
      if (path !== '/api/local/desktop/notifications') throw new Error('Unexpected mutation while saving preferences')
      return new Promise<NotificationPreferences>(resolve => { finish = resolve })
    })
    await openHost()
    fireEvent.click(workspaces().getByRole('button', { name: 'App Settings' }))
    const sections = await screen.findByRole('navigation', { name: 'App settings sections' })
    fireEvent.click(within(sections).getByRole('button', { name: 'Notifications' }))
    const quiet = await screen.findByRole('checkbox', { name: /Quiet mode/ })
    fireEvent.click(quiet)
    await waitFor(() => expect(quiet).toBeDisabled())
    expect(transport.change).toHaveBeenCalledWith('/api/local/desktop/notifications', 'PUT', expect.any(Function),
      { quietMode: true }, expect.any(AbortSignal))
    const navigate = async () => {
      if (destination === 'section') fireEvent.click(within(sections).getByRole('button', { name: 'Appearance' }))
      else if (destination === 'workspace') fireEvent.click(workspaces().getByRole('button', { name: 'Servers' }))
      else await notify({ workspace: 'settings', section: 'access', profileId: null, connectionId: null })
    }
    await act(async () => { await navigate() })
    expect(workspaces().getByRole('button', { name: 'App Settings' })).toHaveAttribute('aria-current', 'page')
    expect(within(sections).getByRole('button', { name: 'Notifications' })).toHaveAttribute('aria-current', 'location')
    expect(await within(screen.getByRole('region', { name: 'App Settings' }))
      .findByText('Wait for the change to save before leaving.')).toBeVisible()
    expect(mutationCalls()).toHaveLength(1)
    preferences = { ...preferences, quietMode: true }
    await act(async () => { finish(preferences) })
    await waitFor(() => expect(quiet).toBeEnabled())
    expect(quiet).toBeChecked()
    // The blocked navigation is deliberately retried, rather than replayed implicitly.
    expect(workspaces().getByRole('button', { name: 'App Settings' })).toHaveAttribute('aria-current', 'page')
    await act(async () => { await navigate() })
    if (destination === 'section') expect(await screen.findByRole('combobox', { name: 'Theme' })).toBeVisible()
    else await screen.findByRole('region', { name: destination === 'workspace' ? 'Contract fixture workspace' : 'This Host PC' })
    expectNoServerAction()
  })

  it('guards leaving a server notification editor until its server-scoped preference save completes', async () => {
    const workspace = await openHost()
    const tools = await openServerTools(workspace)
    fireEvent.click(within(tools).getByRole('button', { name: 'Server notifications' }))
    const notifications = await screen.findByRole('dialog', { name: 'Notifications for Contract fixture' })
    const alerts = await within(notifications).findByRole('combobox', { name: 'Server alerts' })
    let finish!: (value: NotificationPreferences) => void
    transport.change.mockImplementation(() => new Promise<NotificationPreferences>(resolve => { finish = resolve }))
    fireEvent.change(alerts, { target: { value: 'off' } })
    await waitFor(() => expect(alerts).toBeDisabled())
    expect(transport.change).toHaveBeenCalledWith('/api/local/desktop/notifications', 'PUT', expect.any(Function),
      { profileId: id, connectionId: null, allowedEvents: [] }, expect.any(AbortSignal))
    fireEvent.click(workspaces().getByRole('button', { name: 'App Settings' }))
    await act(async () => { await Promise.resolve() })
    expect(workspaces().getByRole('button', { name: 'Servers' })).toHaveAttribute('aria-current', 'page')
    expect(notifications).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'App Settings' })).not.toBeInTheDocument()
    expect(mutationCalls()).toHaveLength(1)
    await act(async () => { finish({ ...notificationPreferences, servers: [{ profileId: id, connectionId: null, allowedEvents: [] }] }) })
    await waitFor(() => expect(alerts).toBeEnabled())
    expect(alerts).toHaveValue('off')
    fireEvent.click(within(notifications).getByRole('button', { name: 'Done' }))
    fireEvent.click(within(tools).getByRole('button', { name: 'Done' }))
    fireEvent.click(workspaces().getByRole('button', { name: 'App Settings' }))
    await screen.findByRole('region', { name: 'App Settings' })
    expectNoServerAction()
  })
})

describe('Host task structure', () => {
  it('opens the running continuation when ordinary Start follows the latest shared save', async () => {
    const continuationId = '22222222-2222-4222-8222-222222222222'
    const workspace = await openHost()
    const change = transport.change.getMockImplementation()!
    transport.change.mockImplementation(async (path: string, ...args: unknown[]) => {
      if (path === `/api/local/profiles/${id}/start`) {
        const current = host()
        state = { ...current, settings: { ...current.settings, profiles: [...current.settings.profiles,
          { ...current.settings.profiles[0], id: continuationId, name: 'Latest shared continuation',
            separateCopySourceProfileId: id, separateCopyBranchHash: 'A'.repeat(64) }] },
          runs: [...current.runs, { ...current.runs[0], profileId: continuationId, state: 'Starting', detail: 'Starting the latest shared save.' }] }
        localDirectorySnapshot = state as HostSnapshot
        return { ok: true, code: 'SeparateCopyStarted', message: 'Starting the latest shared save.', snapshot: state }
      }
      return change(path, ...args)
    })
    fireEvent.click(within(workspace.querySelector('.server-command-header')!).getByRole('button', { name: 'Start server' }))
    expect(await screen.findByRole('region', { name: 'Latest shared continuation workspace' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(mutationCalls().filter(([path]) => path.endsWith('/start'))).toHaveLength(1)
  })

  it('starts a retained Friend copy through its exact normal Start checks without an obsolete split-save dialog', async () => {
    host().settings.profiles[0].separateCopySourceProfileId = '33333333-3333-4333-8333-333333333333'
    host().settings.profiles[0].separateCopyBranchHash = 'B'.repeat(64)
    const workspace = await openHost()
    fireEvent.click(within(workspace.querySelector('.server-command-header')!).getByRole('button', { name: 'Start server' }))
    await waitFor(() => expect(transport.change).toHaveBeenCalledWith(
      `/api/local/profiles/${id}/start`, 'POST', expect.any(Function), undefined, undefined))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(transport.change.mock.calls.some(([path]) => path.includes('/shared-world/hosting/start'))).toBe(false)
  })

  it('targets the owner’s original sharing policy from a retained continuation without changing settings', async () => {
    const childId = '22222222-2222-4222-8222-222222222222'
    host().runs[0].processId = null
    const original = host().settings.profiles[0]
    host().settings.profiles.push({ ...original, id: childId, name: 'Retained owner continuation',
      separateCopySourceProfileId: id, separateCopyBranchHash: 'B'.repeat(64) })
    host().runs.push({ ...host().runs[0], profileId: childId })
    const settings = structuredClone(host().settings)
    localStorage.setItem('togetherserver.ui-preferences.v1', JSON.stringify({ selectedServer: childId }))
    render(<App />)
    const workspace = await screen.findByRole('region', { name: 'Retained owner continuation workspace' })
    await act(async () => { await Promise.resolve() })
    expect(screen.queryByText(/Hosting for a friend|Hosted for a friend/)).not.toBeInTheDocument()
    expect(directory().getByRole('button', { name: 'Open Contract fixture on this PC' })).toHaveAttribute('aria-current', 'true')
    expect(directory().queryByRole('button', { name: 'Open Retained owner continuation on this PC' })).not.toBeInTheDocument()
    fireEvent.click(serverSections().getByRole('button', { name: 'Backups' }))
    fireEvent.click(await within(workspace).findByRole('button', { name: 'Sharing & hosting' }))
    expect(await within(workspace).findByRole('region', { name: 'Shared saves' })).toHaveAttribute('data-profile-id', id)
    expect(host().settings).toEqual(settings)
    expectNoMutation()
  })

  it('starts an old owner continuation through the original root and selects the newly running continuation', async () => {
    const childId = '22222222-2222-4222-8222-222222222222'
    const unresolvedId = '33333333-3333-4333-8333-333333333333'
    const latestId = '44444444-4444-4444-8444-444444444444'
    host().runs[0].processId = null
    const original = host().settings.profiles[0]
    host().settings.profiles.push({ ...original, id: childId, name: 'Earlier owner continuation',
      separateCopySourceProfileId: id, separateCopyBranchHash: 'B'.repeat(64) },
    { ...original, id: unresolvedId, name: 'Unresolved retained copy', separateCopySourceProfileId: id, separateCopyBranchHash: 'C'.repeat(64) })
    host().runs.push({ ...host().runs[0], profileId: childId },
      { ...host().runs[0], profileId: unresolvedId, state: 'Unknown', processId: null, runOperationId: null })
    localStorage.setItem('togetherserver.ui-preferences.v1', JSON.stringify({ selectedServer: childId }))
    render(<App />)
    const workspace = await screen.findByRole('region', { name: 'Earlier owner continuation workspace' })
    await act(async () => { await Promise.resolve() })
    let finish!: (value: unknown) => void
    const change = transport.change.getMockImplementation()!
    transport.change.mockImplementation((path: string, ...args: unknown[]) => path === `/api/local/profiles/${id}/start`
      ? new Promise(resolve => { finish = resolve }) : change(path, ...args))
    fireEvent.click(within(workspace.querySelector('.server-command-header')!).getByRole('button', { name: 'Start server' }))
    expect(await within(workspace).findByRole('progressbar', { name: 'Checking setup and starting' })).toBeVisible()
    expect(transport.change).toHaveBeenCalledWith(`/api/local/profiles/${id}/start`, 'POST', expect.any(Function), undefined, undefined)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    const current = host()
    state = { ...current, settings: { ...current.settings, profiles: [...current.settings.profiles,
      { ...original, id: latestId, name: 'Newest owner continuation', separateCopySourceProfileId: id, separateCopyBranchHash: 'D'.repeat(64) }] },
    runs: [...current.runs, { ...current.runs[0], profileId: latestId, state: 'Starting', runOperationId: '55555555-5555-4555-8555-555555555555' }] }
    localDirectorySnapshot = state as HostSnapshot
    await act(async () => { finish({ ok: true, code: 'SeparateCopyStarted', message: 'Starting the latest save.', snapshot: state }) })
    const running = await screen.findByRole('region', { name: 'Newest owner continuation workspace' })
    expect(within(running).getByRole('progressbar', { name: 'Waiting for the game' })).toBeVisible()
    expect(await directory().findByRole('button', { name: 'Open Newest owner continuation on this PC' }, { timeout: 4000 })).toHaveAttribute('aria-current', 'true')
    expect(directory().getByRole('button', { name: 'Open Unresolved retained copy on this PC' })).toBeInTheDocument()
    expect(directory().queryByRole('button', { name: 'Open Earlier owner continuation on this PC' })).not.toBeInTheDocument()
    expect(mutationCalls().filter(([path]) => path.endsWith('/start')).map(([path]) => path)).toEqual([`/api/local/profiles/${id}/start`])
  })

  it('opens usable header disclosures and lets Escape dismiss them before leaving Settings', async () => {
    await openHost()
    fireEvent.click(workspaces().getByRole('button', { name: 'App Settings' }))
    await screen.findByRole('heading', { name: 'App Settings', level: 1 })
    const notifications = within(document.querySelector('.header-tools')! as HTMLElement).getByLabelText('Notifications')
    fireEvent.click(notifications)
    const panel = document.querySelector('.notification-panel')!
    expect(shellCss).toContain('.notification-panel')
    // This used to be 100% of a 34px bell, despite its wider declared inline size.
    expect(getComputedStyle(panel).maxInlineSize).toMatch(/100vw/)
    expect(getComputedStyle(notifications.closest('details')!).position).toBe('static')
    expect(getComputedStyle(document.querySelector('.header-tools')!).position).toBe('relative')
    fireEvent.keyDown(notifications, { key: 'Escape' })
    expect(notifications.closest('details')).not.toHaveAttribute('open')
    expect(notifications).toHaveFocus()
    expect(screen.getByRole('heading', { name: 'App Settings', level: 1 })).toBeInTheDocument()
    expectNoServerAction()
  })

  it('leads the selected page with one Start and keeps connection checks available in Server tools', async () => {
    transport.change.mockImplementation(async (path: string) => {
      if (path.endsWith('/start')) {
        const next = structuredClone(host())
        next.runs[0].state = 'Starting'
        next.runs[0].detail = 'Waiting for a real readiness observation.'
        state = next
      }
      return { ok: true, message: 'Request accepted.', snapshot: state }
    })
    const workspace = await openHost()
    const start = within(workspace).getByRole('button', { name: 'Start server' })
    expect(screen.getAllByRole('button', { name: 'Start server' })).toHaveLength(1)
    expect(serverSections().getByRole('button', { name: 'Overview' })).toHaveAttribute('aria-current', 'page')
    before(start, within(workspace).getByText('Start this server to play. Join details appear here when it is running.'))
    const tools = await openServerTools(workspace)
    const checks = within(tools).getByText('Status & connection checks').closest('details')!
    expect(checks).not.toHaveAttribute('open')
    fireEvent.click(checks.querySelector('summary')!)
    expect(within(checks).getByRole('note', { name: 'Connection check before Start' })).toBeVisible()
    expectNoServerAction()
    fireEvent.click(within(tools).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(tools).not.toBeInTheDocument())
    fireEvent.click(start)
    await waitFor(() => expect(transport.change).toHaveBeenCalledWith(`/api/local/profiles/${id}/start`, 'POST', expect.any(Function), undefined, undefined))
    expect(await within(workspace).findByText('Request accepted.')).toBeInTheDocument()
    expect(within(workspace.querySelector('.server-command-header')!).getByText('Waiting for a real readiness observation.')).toBeVisible()
    expect(within(workspace).getByRole('progressbar', { name: 'Waiting for the game' })).toBeVisible()
    expect(within(workspace).getByRole('button', { name: 'Stop server' })).toBeInTheDocument()
  })

  it('shows startup immediately, keeps it through the first pending game check, and lets the owner stop', async () => {
    let finishStart!: (value: unknown) => void
    transport.change.mockImplementation((path: string) => path.endsWith('/start')
      ? new Promise(resolve => { finishStart = resolve })
      : Promise.resolve({ ok: true, message: 'Request accepted.', snapshot: state }))
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Start server' }))
    expect(await within(workspace).findByRole('progressbar', { name: 'Checking setup and starting' })).toBeVisible()
    expect(within(workspace).getByRole('button', { name: 'Starting…' })).toBeDisabled()
    expect(within(workspace).queryByText('Start this server to play. Join details appear here when it is running.')).not.toBeInTheDocument()
    const next = structuredClone(host())
    next.runs[0].state = 'Unknown'
    next.runs[0].runOperationId = '22222222-2222-4222-8222-222222222222'
    next.runs[0].playerObservationSource = 'ObservationPending'
    next.runs[0].detail = 'Waiting for the next server check.'
    state = next
    await act(async () => { finishStart({ ok: true, message: 'Server launched.', snapshot: state }) })
    expect(await within(workspace).findByRole('progressbar', { name: 'Waiting for the game' })).toBeVisible()
    expect(within(workspace).getByRole('button', { name: 'Stop server' })).toBeEnabled()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Stop server' }))
    expect(await screen.findByRole('dialog', { name: 'Stop Contract fixture?' })).toBeVisible()
    expect(transport.change.mock.calls.filter(([path]) => path.endsWith('/stop'))).toHaveLength(0)
  })

  it('shows this run’s console under startup and swaps to a single full log viewer on request', async () => {
    host().settings.profiles[0].kind = 'Valheim'
    host().runs[0].state = 'Starting'
    host().runs[0].runOperationId = '22222222-2222-4222-8222-222222222222'
    const workspace = await openHost()
    const preview = within(workspace).getByRole('region', { name: 'Startup console log' })
    expect(preview).toHaveAttribute('data-endpoint', `/api/local/profiles/${id}/logs`)
    expect(preview).toHaveAttribute('data-expected-run', host().runs[0].runOperationId)
    expect(within(workspace).getByRole('button', { name: 'Stop server' })).toBeEnabled()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Open full logs' }))
    expect(await within(workspace).findByRole('region', { name: 'Server logs' })).toHaveAttribute('data-endpoint', `/api/local/profiles/${id}/logs`)
    expect(within(workspace).queryByRole('region', { name: 'Startup console log' })).not.toBeInTheDocument()
    expectNoServerAction()
  })

  it('prepares an invite, shows its result next to the controls, copies it and can close the review', async () => {
    const read = transport.read.getMockImplementation()!
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => path === '/api/local/companion'
      ? { ...companion, listenerActive: true, listenerState: 'Listening' } : read(path, ...args))
    transport.change.mockImplementation(async (path: string) => path.endsWith('/invite/current')
      ? { exists: true, canStart: true, requireApproval: false }
      : path.endsWith('/invite') ? { ok: true, password: 'TS3-synthetic-review-code', listenerActive: true, message: 'Synthetic code ready.' }
        : { ok: true, message: 'Request accepted.', snapshot: state })
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Invite friends' }))
    expect(await within(workspace).findByText('Server code ready')).toBeVisible()
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith('TS3-synthetic-review-code')
    expect(within(workspace).getByRole('button', { name: 'Copy code' })).toBeVisible()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Done' }))
    expect(within(workspace).queryByRole('button', { name: 'Copy code' })).not.toBeInTheDocument()
    expectNoServerAction()
  })

  it('clears startup progress and offers Start again after a failed launch request', async () => {
    transport.change.mockImplementation(async (path: string) => {
      if (path.endsWith('/start')) throw new Error('The server could not start.')
      return { ok: true, message: 'Request accepted.', snapshot: state }
    })
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Start server' }))
    expect(await within(workspace).findByText('The server could not start.')).toBeVisible()
    expect(within(workspace).queryByRole('progressbar')).not.toBeInTheDocument()
    expect(within(workspace).getByRole('button', { name: 'Start server' })).toBeEnabled()
  })

  it('removes startup receipts from the header and notification menu when a fresh snapshot reaches Ready', async () => {
    const message = 'Valheim process launched. Waiting for its server-connected log signal.'
    transport.change.mockImplementation(async (path: string) => {
      if (path.endsWith('/start')) {
        const next = structuredClone(host())
        next.runs[0].state = 'Starting'
        state = next
      }
      return { ok: true, message, snapshot: state }
    })
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Start server' }))
    await within(workspace).findByText(message)
    const ready = structuredClone(host())
    ready.runs[0].state = 'Ready'
    state = ready
    await waitFor(() => expect(within(workspace.querySelector('.server-command-header')!).getByText('Ready', { selector: '.status' })).toBeVisible(), { timeout: 5000 })
    // Fresh canonical status must supersede the initial request result.
    expect(within(workspace).queryByRole('progressbar')).not.toBeInTheDocument()
    expect(within(workspace).queryByText(message)).not.toBeInTheDocument()
    fireEvent.click(screen.getByLabelText(/Notifications/))
    expect(document.querySelector('.notification-panel')).not.toHaveTextContent(message)
    expect(transport.change.mock.calls.filter(([path]) => path.endsWith('/start'))).toHaveLength(1)
  }, 8000)

  it('keeps the player-impact confirmation when Stop moves next to server status', async () => {
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 2
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Stop server' }))
    const confirmation = await screen.findByRole('dialog', { name: 'Stop Contract fixture?' })
    expect(confirmation).toHaveTextContent(/2.*players/i)
    expectNoServerAction()
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(confirmation).not.toBeInTheDocument())
    expectNoServerAction()
  })

  it('sends exactly one Stop only after choosing the app confirmation action', async () => {
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 0
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Stop server' }))
    const confirmation = await screen.findByRole('dialog', { name: 'Stop Contract fixture?' })
    expect(confirmation).toHaveTextContent('0 players')
    expect(confirmation).toHaveTextContent(/asks the game to save and stop.*cannot confirm every save/i)
    expectNoServerAction()
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Stop server' }))
    await waitFor(() => expect(transport.change).toHaveBeenCalledWith(`/api/local/profiles/${id}/stop`, 'POST',
      expect.any(Function), undefined, undefined))
    expect(transport.change.mock.calls.filter(([path]) => path.endsWith('/stop'))).toHaveLength(1)
    await waitFor(() => expect(confirmation).not.toBeInTheDocument())
  })

  it('opens Server tools over the current page and declining Restart leaves all POSTs unsent', async () => {
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 2
    const workspace = await openHost()
    fireEvent.click(serverSections().getByRole('button', { name: 'Logs' }))
    await within(workspace).findByRole('region', { name: 'Server logs' })
    const tools = await openServerTools(workspace)
    expect(tools).toHaveTextContent('Actions for Contract fixture.')
    expect(within(tools).getByRole('button', { name: 'Back up and restart' })).toBeEnabled()
    expect(serverSections().getByRole('button', { name: 'Logs' })).toHaveAttribute('aria-current', 'page')
    fireEvent.click(within(tools).getByRole('button', { name: 'Restart server' }))
    const confirmation = await screen.findByRole('dialog', { name: 'Restart Contract fixture?' })
    expect(confirmation).toHaveTextContent('2 players')
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(confirmation).not.toBeInTheDocument())
    expectNoMutation()
    fireEvent.click(within(tools).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(tools).not.toBeInTheDocument())
    expect(within(workspace).getByRole('region', { name: 'Server logs' })).toBeVisible()
    expect(serverSections().getByRole('button', { name: 'Logs' })).toHaveAttribute('aria-current', 'page')
  })

  it('leads Overview with Stop and join details before supporting content', async () => {
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 0
    const workspace = await openHost()
    const primary = within(workspace.querySelector('.server-command-header')!)
    before(primary.getByRole('button', { name: 'Stop server' }), within(workspace).getByRole('region', { name: 'Connection details' }))
    expect(serverSections().getByRole('button', { name: 'Overview' })).toHaveAttribute('aria-current', 'page')
    expect(within(workspace).queryByText('Chat summary')).not.toBeInTheDocument()
    expectNoServerAction()
    fireEvent.click(primary.getByRole('button', { name: 'Stop server' }))
    const confirmation = await screen.findByRole('dialog', { name: 'Stop Contract fixture?' })
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(confirmation).not.toBeInTheDocument())
    expectNoServerAction()
  })

  it.each(['Players', 'Chat', 'Logs', 'Sessions', 'Backups'])('keeps the same primary Stop available while browsing %s', async tab => {
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 0
    const workspace = await openHost()
    fireEvent.click(serverSections().getByRole('button', { name: tab }))
    await waitFor(() => expect(serverSections().getByRole('button', { name: tab })).toHaveAttribute('aria-current', 'page'))
    expect(within(workspace.querySelector('.server-command-header')!).getByRole('button', { name: 'Stop server' })).toBeVisible()
    expectNoServerAction()
  })

  it('keeps an active empty-server countdown visible while browsing other tabs', async () => {
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 0
    host().runs[0].autoShutdownAtUtc = new Date(Date.now() + 300_000).toISOString()
    const workspace = await openHost()
    const header = within(workspace.querySelector('.server-command-header')!)
    expect(header.getByRole('timer')).toHaveTextContent('Stops in')
    fireEvent.click(serverSections().getByRole('button', { name: 'Chat' }))
    await waitFor(() => expect(serverSections().getByRole('button', { name: 'Chat' })).toHaveAttribute('aria-current', 'page'))
    expect(header.getByRole('timer')).toBeVisible()
    expectNoServerAction()
  })

  it('keeps local and friend servers visible together without changing the selected server', async () => {
    host().settings.profiles.push({ ...host().settings.profiles[0], id: '22222222-2222-4222-8222-222222222222', name: 'Second synthetic server' })
    await openHost()
    expect(screen.queryByRole('dialog', { name: 'All servers' })).not.toBeInTheDocument()
    expect(directory().getByRole('heading', { name: 'Your servers' })).toBeVisible()
    expect(directory().getByRole('heading', { name: 'Friend servers' })).toBeVisible()
    expect(directory().getByRole('button', { name: 'Open Contract fixture on this PC' })).toHaveAttribute('aria-current', 'true')
    expect(directory().getByRole('button', { name: 'Open Second synthetic server on this PC' })).toBeVisible()
    expect(directory().getByRole('button', { name: 'Open Synthetic world on Evening Host' })).toBeVisible()
    expect(screen.getByRole('region', { name: 'Contract fixture workspace' })).toBeInTheDocument()
    expectNoMutation()
  })

  it('keeps backup and maintenance tools reachable in their owning pane, without adding them to chat or logs', async () => {
    host().settings.profiles[0].kind = 'Valheim'
    const workspace = await openHost()
    expect(within(workspace).queryByRole('button', { name: 'Back up now' })).not.toBeInTheDocument()
    fireEvent.click(serverSections().getByRole('button', { name: 'Backups' }))
    expect(await within(workspace).findByRole('region', { name: 'Backup catalog' })).toBeInTheDocument()
    const backupAction = within(workspace).getByRole('button', { name: 'Back up now' })
    expect(backupAction).toBeEnabled()
    expect(backupAction).toBeVisible()
    const protection = within(workspace).getByRole('region', { name: 'World protection' })
    const shared = within(protection).getByLabelText('Shared saves')
    expect(shared).not.toBeVisible()
    fireEvent.click(within(protection).getByRole('button', { name: 'Sharing & hosting' }))
    expect(shared).toBeVisible()
    expect(within(workspace).getByRole('button', { name: 'Start server' })).toBeEnabled()
    fireEvent.click(serverSections().getByRole('button', { name: 'Chat' }))
    expect(await within(workspace).findByRole('region', { name: 'Server chat' })).toBeInTheDocument()
    expect(within(workspace).queryByRole('button', { name: 'Back up now' })).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Server tools' })).not.toBeInTheDocument()
    fireEvent.click(serverSections().getByRole('button', { name: 'Logs' }))
    expect(await within(workspace).findByRole('region', { name: 'Server logs' })).toBeInTheDocument()
    fireEvent.click(serverSections().getByRole('button', { name: 'Sessions' }))
    expect(await within(workspace).findByRole('region', { name: 'Seven-day summary' })).toBeInTheDocument()
    fireEvent.click(serverSections().getByRole('button', { name: 'Setup & maintenance' }))
    expect(await within(workspace).findByText('Update the game server safely')).toBeInTheDocument()
    expect(within(workspace.querySelector('.server-command-header')!).getByRole('button', { name: 'Start server' })).toBeEnabled()
    expect(within(workspace).getByRole('button', { name: 'Begin maintenance' })).toBeEnabled()
    expectNoServerAction()
  })

  it('keeps Start blocked by recovery and exposes the recovery destination', async () => {
    recoveryBlocked = true
    const workspace = await openHost()
    expect(within(workspace).getByRole('button', { name: 'Start server' })).toBeDisabled()
    expect(within(workspace).getByRole('button', { name: 'Review recovery' })).toBeInTheDocument()
    expectNoServerAction()
  })

  it('blocks the persistent Start until unsaved server file changes are saved or discarded', async () => {
    const workspace = await openHost()
    fireEvent.click(serverSections().getByRole('button', { name: 'Settings & files' }))
    fireEvent.click(await within(workspace).findByRole('tab', { name: 'Server files' }))
    const editor = await within(workspace).findByRole('textbox', { name: 'Test file draft' })
    fireEvent.change(editor, { target: { value: 'An unsaved configuration change' } })
    fireEvent.click(within(workspace).getByRole('tab', { name: 'Game settings' }))
    expect(editor).not.toBeVisible()
    const start = within(workspace.querySelector('.server-command-header')!).getByRole('button', { name: 'Start server' })
    expect(start).toBeDisabled()
    fireEvent.click(start)
    expectNoServerAction()
    fireEvent.click(within(workspace).getByRole('tab', { name: 'Server files' }))
    expect(editor).toBeVisible()
    expect(editor).toHaveValue('An unsaved configuration change')
    fireEvent.change(editor, { target: { value: '' } })
    await waitFor(() => expect(within(workspace.querySelector('.server-command-header')!).getByRole('button', { name: 'Start server' })).toBeEnabled())
    expectNoServerAction()
  })

  it('routes recovery to diagnostics even when maintenance is also enabled', async () => {
    recoveryBlocked = true
    host().settings.profiles[0].maintenance = { enabled: true, message: 'Updating synthetic settings.' }
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Review recovery' }))
    expect(await screen.findByRole('heading', { name: 'Server diagnostics' })).toBeInTheDocument()
    expectNoServerAction()
  })

  it.each(['logs', 'sessions', 'files'] as const)('keeps %s render failures and retry visible with the actual pane styles', async failure => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    transport.failures[failure] = true
    const workspace = await openHost()
    fireEvent.click(serverSections().getByRole('button', { name: { logs: 'Logs', sessions: 'Sessions', files: 'Settings & files' }[failure] }))
    if (failure === 'files') fireEvent.click(await within(workspace).findByRole('tab', { name: 'Server files' }))
    await waitFor(() => expect(workspace).toHaveAttribute('data-server-tab', failure), { timeout: 3000 })
    const error = await within(workspace).findByRole('alert')
    expect(error).toBeVisible()
    const retry = within(error.closest('.pane-error') as HTMLElement).getByRole('button', { name: 'Try again' })
    expect(retry).toBeVisible()
    fireEvent.click(within(error).getByRole('button', { name: 'Dismiss section error' }))
    expect(error).not.toBeInTheDocument()
    expect(retry).toBeVisible()
    transport.failures[failure] = false
    fireEvent.click(retry)
    await waitFor(() => expect(within(workspace).queryByRole('alert')).not.toBeInTheDocument())
    expectNoServerAction()
  })

  it('keeps a failed draft save on the editing screen and permits navigation after retry', async () => {
    const workspace = await openHost()
    fireEvent.click(serverSections().getByRole('button', { name: 'Settings & files' }))
    fireEvent.click(await within(workspace).findByRole('tab', { name: 'Server files' }))
    const editor = await within(workspace).findByRole('textbox', { name: 'Test file draft' })
    fireEvent.change(editor, { target: { value: 'Last unsaved keystroke' } })
    fireEvent.click(within(workspace).getByRole('tab', { name: 'Game settings' }))
    expect(editor).not.toBeVisible()
    transport.flush.mockResolvedValue(false)
    fireEvent.click(serverSections().getByRole('button', { name: 'Chat' }))
    await waitFor(() => expect(transport.flush).toHaveBeenCalledOnce())
    await waitFor(() => expect(within(workspace).getByRole('tab', { name: 'Server files' })).toHaveAttribute('aria-selected', 'true'))
    expect(editor).toBeVisible()
    expect(editor).toHaveValue('Last unsaved keystroke')
    expect(within(workspace).queryByRole('region', { name: 'Server chat' })).not.toBeInTheDocument()
    transport.flush.mockResolvedValue(true)
    fireEvent.click(serverSections().getByRole('button', { name: 'Chat' }))
    expect(await within(workspace).findByRole('region', { name: 'Server chat' })).toBeInTheDocument()
    expectNoServerAction()
  })

  it.each(['Unknown', 'Starting', 'Stopping', 'Failed'])('does not offer Start for %s and retains honest state detail', async serverState => {
    host().runs[0].state = serverState
    host().runs[0].detail = `Synthetic ${serverState} observation.`
    const workspace = await openHost()
    expect(within(workspace).queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
    expect(within(workspace).getByText(`Synthetic ${serverState} observation.`)).toBeVisible()
    expectNoServerAction()
  })

  it('opens the Custom certification from Setup & maintenance while chat stays focused on conversation', async () => {
    host().settings.profiles[0].kind = 'Custom'
    const workspace = await openHost()
    fireEvent.click(serverSections().getByRole('button', { name: 'Setup & maintenance' }))
    expect(await within(workspace).findByRole('button', { name: 'Start control test' })).toBeInTheDocument()
    fireEvent.click(serverSections().getByRole('button', { name: 'Chat' }))
    expect(await within(workspace).findByRole('region', { name: 'Server chat' })).toBeInTheDocument()
    expect(within(workspace).queryByRole('button', { name: 'Start control test' })).not.toBeInTheDocument()
    expectNoServerAction()
  })

  it('remembers command-palette destinations for returning to a server and exposes Chat and Players commands', async () => {
    const workspace = await openHost()
    fireEvent.click(screen.getByRole('button', { name: 'Commands' }))
    const search = await screen.findByRole('combobox', { name: 'Search commands' })
    fireEvent.change(search, { target: { value: 'Contract fixture players' } })
    fireEvent.keyDown(search, { key: 'Enter' })
    // The selected tab commits before its preferences are written by the shell effect.
    await waitFor(() => {
      expect(workspace).toHaveAttribute('data-server-tab', 'players')
      expect(serverSections().getByRole('button', { name: 'Players' })).toHaveAttribute('aria-current', 'page')
      expect(JSON.parse(localStorage.getItem('togetherserver.ui-preferences.v1')!)).toMatchObject({
        selectedServer: id, serverTabs: { [id]: 'players' }
      })
    })
    fireEvent.click(screen.getByRole('button', { name: 'Commands' }))
    fireEvent.change(await screen.findByRole('combobox', { name: 'Search commands' }), { target: { value: 'Contract fixture chat' } })
    expect(screen.getByRole('option', { name: /Contract fixture · Chat/ })).toBeInTheDocument()
    expectNoServerAction()
  })

  it('provides a clear empty Host state without exposing game operations', async () => {
    host().settings.profiles = []
    host().runs = []
    render(<App />)
    expect(await screen.findByRole('button', { name: /Host a server/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Join a server/ })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
    expectNoServerAction()
  })

  it('keeps staging rehearsal out of first setup and reachable through Network troubleshooting', async () => {
    host().settings.profiles = []
    host().runs = []
    const read = transport.read.getMockImplementation()!
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => path === '/api/local/instance'
      ? { ...instance, kind: 'Staging', isStaging: true, freshWorldsOnly: true } : read(path, ...args))
    render(<App />)
    expect(await screen.findByRole('region', { name: 'Host or join a server' })).toBeInTheDocument()
    expect(screen.queryByText('Test with another owned PC')).not.toBeInTheDocument()
    fireEvent.click(pcSections().getByRole('button', { name: 'Network' }))
    expect(screen.queryByText('Test with another owned PC')).not.toBeInTheDocument()
    fireEvent.click(await screen.findByText('Troubleshooting'))
    expect(await screen.findByText('Test with another owned PC')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Prepare connection test' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Test with another owned PC Open rehearsal' }))
    expect(screen.getByRole('button', { name: 'Prepare connection test' })).toBeInTheDocument()
    expectNoMutation()
  })

  it('reviews recovered first-server setup inside the same entry area before opening its saved step', async () => {
    const recoveredProfile = { ...host().settings.profiles[0], kind: 'Valheim' as const, name: 'Unfinished world setup' }
    host().settings.profiles = []
    host().runs = []
    vi.mocked(readProtectedDraft).mockResolvedValue({ ok: true, revision: 2, message: '',
      text: serializeProtectedSetupDraft([recoveredProfile], 'world', recoveredProfile.id) })
    render(<App />)
    const entry = await screen.findByRole('region', { name: 'Host or join a server' })
    const recovery = await within(entry).findByRole('region', { name: 'Recovered server setup' })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    fireEvent.click(within(recovery).getByRole('button', { name: 'Review saved setup' }))
    const setup = await screen.findByRole('dialog')
    expect(within(setup).getByText('World').closest('li')).toHaveAttribute('aria-current', 'step')
    expect(transport.change.mock.calls.filter(([path]) => path === '/api/local/settings' || path.endsWith('/start'))).toHaveLength(0)
  })

  it('shows loading, then a local-service error, and recovers on the next safe poll', async () => {
    let finish!: (value: Snapshot) => void
    const read = transport.read.getMockImplementation()!
    const held = new Promise<Snapshot>(resolve => { finish = resolve })
    let first = true
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => {
      if (path === '/api/local/snapshot' && first) { first = false; await held; throw new Error('Synthetic local service unavailable') }
      return read(path, ...args)
    })
    render(<App />)
    expect(await screen.findByText('Loading TogetherServer…')).toBeVisible()
    finish(state)
    expect(await screen.findByRole('alert')).toHaveTextContent('Synthetic local service unavailable')
    expect(await screen.findByRole('region', { name: 'Contract fixture workspace' }, { timeout: 4500 })).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expectNoServerAction()
  }, 6000)
})

function friend(): FriendSnapshot {
  return {
    mode: 'Friend', state: 'Connected', detail: 'Synthetic authenticated connection.', endpoint: 'https://192.0.2.10:5131',
    lastConnectedUtc: new Date().toISOString(), remoteControlsEnabled: true, canStart: true, canStop: true,
    connectionId: '22222222-2222-4222-8222-222222222222', connectionName: 'Evening Host', connections: null,
    hostCapabilities: ['game-requirements-v1', 'server-chat-v1'], protocolCompatible: true,
    profiles: [{ id, kind: 'MinecraftJava', name: 'Synthetic world', state: 'Ready', joinAddress: '192.0.2.10:25565',
      runOperationId: null, canStart: true, canStop: false, canStopNow: false, stopReason: 'Status only Stop access.',
      canRestartNow: false, restartReason: 'Stop is not granted.', onlinePlayers: 1, maxPlayers: 10,
      autoShutdownAtUtc: null, autoShutdownReason: null, maintenanceEnabled: false, maintenanceMessage: null,
      canExtendTimer: false, timerExtensionMinutes: 15, timerExtensionRemainingMinutes: 60, canViewLogs: false }]
  }
}

async function openSharedHostingDetails() {
  return screen.findByRole('region', { name: 'Shared hosting details' })
}

function approvedLocalHosting(localProfileId: string): LocalHostingStatus {
  return { ok: true, code: 'NeedsPreparation', message: 'Check this PC before hosting.', allowed: true,
    consented: true, game: 'Valheim', name: 'Shared world', needsServerFile: false, needsPassword: false,
    serverFile: 'C:/synthetic/valheim_server.exe', gameVersion: 'fixture', gamePort: 2456, controlPort: 5131,
    localProfileId, branchHash: 'A'.repeat(64), readyForManualStart: false, running: false,
    steps: ['permission', 'server', 'save', 'ports', 'connection'].map(key => ({
      key: key as LocalHostingStatus['steps'][number]['key'], state: 'Ready', message: 'Local check passed.' })) }
}

function testPc(id: string, name: string): Device {
  return { id, name, profileId: hostWire.settings.profiles[0].id, assignedProfileIds: [hostWire.settings.profiles[0].id],
    canStart: true, canStop: false, canExtendTimer: false, canViewLogs: false,
    revoked: false, paired: true, approvalPending: false,
    credentialExpiresUtc: new Date(Date.now() + 90 * 86400000).toISOString(), lastHeartbeatUtc: new Date().toISOString(),
    serverPermissions: [], accessExpiresUtc: null, accessExpired: false, temporaryHelperActive: false }
}

function withPcs(devices: Device[]) {
  const info = { ...companion, devices }
  const read = transport.read.getMockImplementation()!
  transport.read.mockImplementation(async (path: string, ...args: unknown[]) => path === '/api/local/companion' ? info : read(path, ...args))
  transport.change.mockImplementation(async (path: string, method: string, _parse: unknown, rawBody: unknown) => {
    const device = info.devices.find(device => path.includes(device.id))
    if (device && path.endsWith('/servers')) {
      const request = rawBody as { profileIds: string[]; permissions: Device['serverPermissions'] }
      device.assignedProfileIds = request.profileIds
      device.serverPermissions = request.permissions
    }
    if (device && path.endsWith('/permissions')) {
      const request = rawBody as { canStart: boolean; canStop: boolean; canExtendTimer: boolean; canViewLogs: boolean; canViewPassword?: boolean; scope?: string | null }
      if (request.scope === 'password') {
        device.canViewPassword = request.canViewPassword
        device.serverPermissions = device.serverPermissions.map(permission => ({ ...permission, canViewPassword: request.canViewPassword }))
      } else {
        device.canStart = request.canStart; device.canStop = request.canStop
        device.canExtendTimer = request.canExtendTimer; device.canViewLogs = request.canViewLogs
        device.serverPermissions = device.serverPermissions.map(permission => ({ profileId: permission.profileId,
          canStart: request.canStart, canStop: request.canStop, canExtendTimer: request.canExtendTimer,
          canViewLogs: request.canViewLogs, canViewPassword: permission.canViewPassword }))
      }
    }
    if (device && path.endsWith('/approve')) device.approvalPending = false
    if (device && path.endsWith('/name')) device.name = (rawBody as { name: string }).name
    if (device && path.endsWith('/access-expiry')) {
      device.accessExpiresUtc = new Date(Date.now() + 30 * 86400000).toISOString()
      return { ok: true, code: 'Saved', message: 'Deadline saved.', accessExpiresUtc: device.accessExpiresUtc, accessExpired: false }
    }
    if (device && path.endsWith('/revoke')) device.revoked = true
    const scopedRemoval = /^\/api\/local\/devices\/([^/]+)\/servers\/([^/]+)\/access$/.exec(path)
    if (device && method === 'DELETE' && scopedRemoval) {
      expect(rawBody).toBeUndefined()
      device.assignedProfileIds = device.assignedProfileIds.filter(id => id !== scopedRemoval[2])
      device.serverPermissions = device.serverPermissions.filter(permission => permission.profileId !== scopedRemoval[2])
    }
    if (device && method === 'DELETE' && path === `/api/local/devices/${device.id}`)
      info.devices = info.devices.filter(item => item.id !== device.id)
    return { ok: true, code: 'Saved', message: 'Access saved.', snapshot: state }
  })
  return info
}

async function openAccess() {
  await openHost()
  fireEvent.click(pcSections().getByRole('button', { name: 'Access' }))
  await screen.findByRole('region', { name: 'This Host PC' })
}

const pcId = '44444444-4444-4444-8444-444444444441'
const secondPcId = '44444444-4444-4444-8444-444444444442'

describe('Friend PC settings flow', () => {
  it('defaults server-origin access to that server, retains All servers through rerenders, and opens global access for all servers', async () => {
    const otherId = '33333333-3333-4333-8333-333333333333'
    host().settings.profiles.push({ ...host().settings.profiles[0], id: otherId, name: 'Other world' })
    withPcs([testPc(pcId, 'Selected world PC'), { ...testPc(secondPcId, 'Other world PC'), assignedProfileIds: [otherId] }])
    await openHost()
    fireEvent.click(screen.getByRole('button', { name: 'Manage friend access' }))
    await screen.findByRole('region', { name: 'This Host PC' })
    const filter = screen.getByRole('combobox', { name: 'Assigned server' })
    expect(filter).toHaveValue(id)
    expect(screen.getByRole('article', { name: 'Selected world PC' })).toBeInTheDocument()
    expect(screen.queryByRole('article', { name: 'Other world PC' })).not.toBeInTheDocument()
    fireEvent.change(filter, { target: { value: 'all' } })
    expect(screen.getByRole('article', { name: 'Other world PC' })).toBeInTheDocument()
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search Friend PCs' }), { target: { value: 'world' } })
    expect(screen.getByRole('combobox', { name: 'Assigned server' })).toHaveValue('all')
    fireEvent.click(screen.getByRole('button', { name: 'Return to selected server' }))
    await screen.findByRole('region', { name: 'Contract fixture workspace' })
    fireEvent.click(pcSections().getByRole('button', { name: 'Access' }))
    await screen.findByRole('region', { name: 'This Host PC' })
    expect(screen.getByRole('combobox', { name: 'Assigned server' })).toHaveValue('all')
    expect(screen.getByRole('article', { name: 'Other world PC' })).toBeInTheDocument()
    expectNoMutation()
  })

  it('removes only the captured server through the atomic scope route and preserves concurrent other-server access', async () => {
    const otherId = '33333333-3333-4333-8333-333333333333'
    const addedId = '55555555-5555-4555-8555-555555555555'
    for (const profileId of [otherId, addedId]) host().settings.profiles.push({ ...host().settings.profiles[0], id: profileId, name: `Other ${profileId}` })
    const pc = { ...testPc(pcId, 'Laptop'), assignedProfileIds: [id, otherId],
      serverPermissions: [{ profileId: otherId, canStart: false, canStop: true, canExtendTimer: false, canViewLogs: true }] }
    withPcs([pc])
    await openAccess()
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage access for Laptop' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove Contract fixture access' }))
    const otherGrant = structuredClone(pc.serverPermissions[0])
    pc.assignedProfileIds.push(addedId)
    pc.serverPermissions.push({ profileId: addedId, canStart: true, canStop: false, canExtendTimer: true, canViewLogs: false })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm remove Contract fixture access' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(transport.change.mock.calls.filter(([path]) => path.startsWith('/api/local/devices/')))
      .toEqual([[`/api/local/devices/${pcId}/servers/${id}/access`, 'DELETE', expect.any(Function), undefined, undefined]])
    expect(pc.assignedProfileIds).toEqual([otherId, addedId])
    expect(pc.serverPermissions[0]).toEqual(otherGrant)
    expect(pc.serverPermissions[1].profileId).toBe(addedId)
    expect(pc.revoked).toBe(false)
    expect(pc.canStart).toBe(true)
    expect(pc.credentialExpiresUtc).not.toBeNull()
    expectNoServerAction()
  })

  it('blocks scoped removal while PC drafts are unsaved and distinguishes all-server removal', async () => {
    withPcs([testPc(pcId, 'Laptop')])
    await openAccess()
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage access for Laptop' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'PC details' }))
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'PC name' }), { target: { value: 'Unsaved name' } })
    expect(within(dialog).getByRole('button', { name: 'Remove Contract fixture access' })).toBeDisabled()
    expect(within(dialog).getByText('Save or discard your edits before removing server access.')).toBeInTheDocument()
    fireEvent.click(within(dialog).getByText('All server access', { exact: true }))
    expect(within(dialog).getByRole('button', { name: 'Remove all server access' })).toBeDisabled()
    expectNoMutation()
  })

  it('rechecks current server existence before scoped removal and leaves the PC untouched when it changed', async () => {
    const pc = testPc(pcId, 'Laptop')
    withPcs([pc])
    await openAccess()
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage access for Laptop' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove Contract fixture access' }))
    const read = transport.read.getMockImplementation()!
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => path === '/api/local/snapshot'
      ? { ...host(), settings: { ...host().settings, profiles: [] } } : read(path, ...args))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirm remove Contract fixture access' }))
    await within(dialog).findByText('This PC or server changed. Refresh access before removing it.')
    expect(pc.assignedProfileIds).toEqual([id])
    expect(pc.revoked).toBe(false)
    expectNoMutation()
  })

  it('saves password sharing through its separate scope and preserves it when changing an action preset', async () => {
    const pc = testPc(pcId, 'Password friend')
    withPcs([pc])
    await openAccess()
    fireEvent.click(within(screen.getByRole('article', { name: 'Password friend' })).getByRole('button', { name: 'Manage access' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage access for Password friend' })
    fireEvent.click(within(dialog).getByRole('checkbox', { name: 'Share game passwords' }))
    await waitFor(() => expect(pc.canViewPassword).toBe(true))
    expect(transport.change).toHaveBeenCalledWith(`/api/local/devices/${pcId}/permissions`, 'PUT', expect.any(Function),
      expect.objectContaining({ scope: 'password', canViewPassword: true }), undefined)
    fireEvent.change(within(dialog).getByRole('combobox', { name: 'Permission preset' }), { target: { value: 'helper' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Apply preset' }))
    await waitFor(() => expect(pc.canStop).toBe(true))
    expect(pc.canViewPassword).toBe(true)
    const preset = transport.change.mock.calls.filter(([path]) => path.endsWith('/permissions')).at(-1)![3] as Record<string, unknown>
    expect(preset).not.toHaveProperty('canViewPassword')
  })
  it('separates PCs into cards and opens only the chosen PC editor', async () => {
    withPcs([testPc(pcId, 'Living room PC'), testPc(secondPcId, 'Laptop')])
    await openAccess()
    const first = screen.getByRole('article', { name: 'Living room PC' })
    const second = screen.getByRole('article', { name: 'Laptop' })
    expect(first).not.toBe(second)
    expect(first).toHaveClass('friend-pc-card')
    expect(screen.queryByRole('textbox', { name: 'PC name' })).not.toBeInTheDocument()
    expect(screen.queryByRole('combobox', { name: 'Permission preset' })).not.toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    fireEvent.click(within(second).getByRole('button', { name: 'Manage access' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage access for Laptop' })
    expect(within(dialog).getByRole('combobox', { name: 'Permission preset' })).toHaveValue('start')
    expect(within(dialog).queryByText('Review access changes')).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('textbox', { name: 'PC name' })).not.toBeInTheDocument()
    expect(transport.change.mock.calls.filter(([path]) => path.startsWith('/api/local/devices/'))).toHaveLength(0)
  })

  it('reviews a changed preset and applies all four grants in one request without a native confirmation', async () => {
    withPcs([testPc(pcId, 'Living room PC')])
    await openAccess()
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage access for Living room PC' })
    fireEvent.change(within(dialog).getByRole('combobox', { name: 'Permission preset' }), { target: { value: 'helper' } })
    expect(within(dialog).getByRole('list', { name: 'Server access before and after' })).toBeInTheDocument()
    expect(within(dialog).queryByRole('table')).not.toBeInTheDocument()
    expect(transport.change.mock.calls.filter(([path]) => path.startsWith('/api/local/devices/'))).toHaveLength(0)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Apply preset' }))
    await waitFor(() => expect(transport.change).toHaveBeenCalledWith(`/api/local/devices/${pcId}/permissions`, 'PUT', expect.any(Function),
      { canStart: true, canStop: true, canExtendTimer: true, canViewLogs: true, scope: null }, undefined))
    await waitFor(() => expect(within(dialog).queryByText('Review access changes')).not.toBeInTheDocument())
    expect(screen.getAllByRole('dialog')).toEqual([dialog])
    expectNoServerAction()
  })

  it('requires a deliberate discard for unsaved server changes and reopens the saved assignment', async () => {
    withPcs([testPc(pcId, 'Living room PC')])
    await openAccess()
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    let dialog = screen.getByRole('dialog', { name: 'Manage access for Living room PC' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Servers' }))
    fireEvent.click(within(dialog).getByRole('checkbox', { name: new RegExp(hostWire.settings.profiles[0].name) }))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(within(dialog).getByRole('alert')).toHaveTextContent('Discard unsaved PC changes?')
    expect(transport.change.mock.calls.filter(([path]) => path.startsWith('/api/local/devices/'))).toHaveLength(0)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard changes' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    dialog = screen.getByRole('dialog', { name: 'Manage access for Living room PC' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Servers' }))
    expect(within(dialog).getByRole('checkbox', { name: new RegExp(hostWire.settings.profiles[0].name) })).toBeChecked()
  })

  it('keeps an unsaved PC name when saving server access from another tab', async () => {
    withPcs([testPc(pcId, 'Living room PC')])
    await openAccess()
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage access for Living room PC' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'PC details' }))
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'PC name' }), { target: { value: 'Unsaved name' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Servers' }))
    fireEvent.click(within(dialog).getByRole('checkbox', { name: new RegExp(hostWire.settings.profiles[0].name) }))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save access' }))
    await waitFor(() => expect(transport.change).toHaveBeenCalledWith(`/api/local/devices/${pcId}/servers`, 'PUT', expect.any(Function),
      { profileIds: [], permissions: [] }, undefined))
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Save access' })).toBeDisabled())
    fireEvent.click(within(dialog).getByRole('button', { name: 'PC details' }))
    expect(within(dialog).getByRole('textbox', { name: 'PC name' })).toHaveValue('Unsaved name')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Done' }))
    expect(within(dialog).getByRole('alert')).toHaveTextContent('Discard unsaved PC changes?')
    expect(transport.change.mock.calls.some(([path]) => path.endsWith('/name'))).toBe(false)
  })

  it('preserves a deadline choice across tabs, guards closing, and saves only the deadline', async () => {
    const pc = testPc(pcId, 'Living room PC')
    withPcs([pc])
    await openAccess()
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage access for Living room PC' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Access time' }))
    fireEvent.click(within(dialog).getByText('Change deadline'))
    fireEvent.click(within(dialog).getByRole('radio', { name: '30 days' }))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Permissions' }))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Access time' }))
    expect(within(dialog).getByRole('radio', { name: '30 days' })).toBeChecked()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Done' }))
    expect(within(dialog).getByRole('alert')).toHaveTextContent('Discard unsaved PC changes?')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Continue editing' }))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save deadline' }))
    await waitFor(() => expect(transport.change).toHaveBeenCalledWith(`/api/local/devices/${pcId}/access-expiry`, 'PUT', expect.any(Function), { duration: 'ThirtyDays' }))
    await within(dialog).findByText('Deadline saved.')
    expect(pc.assignedProfileIds).toEqual([id])
    expect(pc.canStart).toBe(true)
    expect(pc.canStop).toBe(false)
    expect(pc.canViewLogs).toBe(false)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Done' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('blocks saved permission editing after an expired helper projection until a fresh Host view arrives', async () => {
    withPcs([{ ...testPc(pcId, 'Living room PC'), temporaryHelperActive: true,
      temporaryHelperUntilUtc: new Date(Date.now() - 1000).toISOString(), canStop: true, canExtendTimer: true, canViewLogs: true }])
    await openAccess()
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage access for Living room PC' })
    expect(within(dialog).getByRole('combobox', { name: 'Permission preset' })).toBeDisabled()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Servers' }))
    expect(within(dialog).getByRole('checkbox', { name: new RegExp(hostWire.settings.profiles[0].name) })).toBeDisabled()
    expect(within(dialog).getByRole('button', { name: 'Save access' })).toBeDisabled()
    expect(transport.change.mock.calls.filter(([path]) => path.startsWith('/api/local/devices/'))).toHaveLength(0)
  })

  it('distinguishes an empty filter result from having no paired PCs', async () => {
    withPcs([testPc(pcId, 'Living room PC')])
    await openAccess()
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search Friend PCs' }), { target: { value: 'No matching PC' } })
    expect(screen.getByText('No Friend PCs match these filters.')).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(screen.getByRole('article', { name: 'Living room PC' })).toBeInTheDocument()
    expectNoServerAction()
  })
})

describe('Friend task structure', () => {
  it.each([
    ['Disconnected/Unknown', 'HostUnreachable'], ['Disconnected/Unknown', 'CredentialExpired'], ['Disabled', null]
  ])('keeps approved local hosting available with %s and empty remembered remote capabilities (%s)', async (connectionState, connectionCode) => {
    const connected = friend()
    state = { ...connected, state: connectionState, connectionCode, hostCapabilities: [], remoteControlsEnabled: false,
      profiles: [{ ...connected.profiles[0], state: 'Offline', joinAddress: null, canStart: false }] }
    const checks: LocalHostingStatus = { ok: true, code: 'LocalHostingPrepared', message: 'Prepared locally.', allowed: true,
      consented: true, game: 'Valheim', name: 'Shared world', needsServerFile: false, needsPassword: false,
      serverFile: 'C:\\synthetic\\valheim_server.exe', gameVersion: 'fixture', gamePort: 2456, controlPort: 5131,
      localProfileId: id, branchHash: 'A'.repeat(64), readyForManualStart: true, running: false,
      steps: ['permission', 'server', 'save', 'ports', 'connection'].map(key => ({
        key: key as LocalHostingStatus['steps'][number]['key'], state: key === 'connection' ? 'Unknown' : 'Ready',
        message: key === 'connection' ? 'Incoming connection is unverified.' : 'Local check passed.' })) }
    const read = transport.read.getMockImplementation()!
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => /\/shared-world\/hosting(?:\/access)?$/.test(path) ? checks : read(path, ...args))
    render(<App />)
    const play = await screen.findByRole('region', { name: 'Play' })
    const local = await within(play).findByRole('button', { name: 'Host locally' })
    expect(local.closest('.friend-primary-actions')).not.toBeNull()
    expect(local).toBeEnabled()
    expect(local).toHaveAccessibleName('Host locally')
    expect(screen.queryByRole('dialog', { name: 'Host locally' })).not.toBeInTheDocument()
    expectNoMutation()
  })

  it.each(['Revoked', 'AccessExpired', 'ApprovalPending'])('does not expose local hosting after explicit %s', async connectionCode => {
    state = { ...friend(), connectionCode }
    render(<App />)
    await screen.findByRole('navigation', { name: 'Selected server sections' })
    expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
    expect(transport.read.mock.calls.some(([path]) => path.endsWith('/shared-world/hosting'))).toBe(false)
    expectNoMutation()
  })

  it('opens a native saved-Host Chat notification from Add Host and retains the masked unsent invite', async () => {
    const notify = nativeNotifications()
    const connected = friend()
    connected.connections = [structuredClone(connected)]
    state = connected
    render(<App />)
    await screen.findByRole('region', { name: 'Play' })
    fireEvent.click(directory().getByRole('button', { name: 'Connect' }))
    const invite = await screen.findByLabelText('Server code')
    fireEvent.change(invite, { target: { value: 'TS3-synthetic-unsent-private-invite' } })
    expect(invite).toHaveAttribute('type', 'password')
    await notify({ workspace: 'join', section: 'chat', profileId: id, connectionId: connected.connectionId })
    const chat = await screen.findByRole('region', { name: 'Server chat' })
    expect(chat).toBeVisible()
    expect(chat).toHaveAttribute('data-profile-id', id)
    expect(joinSections().getByRole('button', { name: 'Chat' })).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByLabelText('Server code')).not.toBeInTheDocument()
    fireEvent.click(directory().getByRole('button', { name: 'Connect' }))
    const retainedInvite = await screen.findByLabelText('Server code')
    expect(retainedInvite).toHaveValue('TS3-synthetic-unsent-private-invite')
    expect(retainedInvite).toHaveAttribute('type', 'password')
    expect(document.body).not.toHaveTextContent('TS3-synthetic-unsent-private-invite')
    expect(localStorage.getItem('togetherserver.ui-preferences.v1')).not.toContain('TS3-synthetic-unsent-private-invite')
    expectNoMutation()
    expectNoServerAction()
  })

  it.each(['setup', 'controls'] as const)('opens local Host %s for the shared Join world without retaining another selected server', async destination => {
    const secondId = '33333333-3333-4333-8333-333333333333'
    const localHost = structuredClone(host())
    localHost.settings.profiles.push({ ...localHost.settings.profiles[0], id: secondId, name: 'Local shared world B' })
    localHost.runs.push({ ...localHost.runs[0], profileId: secondId })
    state = localHost
    const connected = friend()
    connected.hostCapabilities.push('shared-worlds-v2')
    connected.profiles = [{ ...connected.profiles[0], id: secondId, name: 'Shared Join world B' }]
    friendDirectorySnapshot = connected
    transport.change.mockImplementation(async (path: string) => {
      if (path === '/api/local/mode/friend') state = connected
      else if (path === '/api/local/mode/host') state = localHost
      return { ok: true, code: 'Requested', message: 'Mode changed.', snapshot: state }
    })
    const read = transport.read.getMockImplementation()!
    const checks = approvedLocalHosting(secondId)
    if (destination === 'setup') checks.remedy = { kind: 'Connection', title: 'Let friends connect to this PC', message: 'Review this PC connection.' }
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => /\/shared-world\/hosting(?:\/access)?$/.test(path) ? checks : read(path, ...args))
    await openHost()
    expect(directory().getByRole('button', { name: 'Open Contract fixture on this PC' })).toHaveAttribute('aria-current', 'true')
    fireEvent.click(directory().getByRole('button', { name: 'Open Shared Join world B on Evening Host' }))
    await screen.findByRole('region', { name: 'Play' })
    fireEvent.click(joinSections().getByRole('button', { name: 'Shared saves' }))
    await openSharedHostingDetails()
    const localHosting = await screen.findByRole('button', { name: 'Host locally' })
    await waitFor(() => expect(localHosting).toBeEnabled())
    fireEvent.click(localHosting)
    const dialog = await screen.findByRole('dialog', { name: 'Host locally' })
    await waitFor(() => expect(dialog).not.toHaveAttribute('aria-busy', 'true'))
    fireEvent.click(within(dialog).getByRole('button', { name: destination === 'setup' ? 'Connection help' : 'Local server & connection details' }))
    if (destination === 'setup') {
      const pc = await screen.findByRole('region', { name: 'This Host PC' })
      expect(pcSections().getByRole('button', { name: 'Network' })).toHaveAttribute('aria-current', 'page')
      expect(directory().getByRole('button', { name: 'Open Local shared world B on this PC' })).toHaveAttribute('aria-current', 'true')
      await waitFor(() => expect(JSON.parse(localStorage.getItem('togetherserver.ui-preferences.v1')!).selectedServer).toBe(secondId))
      fireEvent.click(within(pc).getByRole('button', { name: 'Return to selected server' }))
    }
    expect(await screen.findByRole('region', { name: 'Local shared world B workspace' })).toBeVisible()
    expect(screen.queryByRole('region', { name: 'Contract fixture workspace' })).not.toBeInTheDocument()
    expect(serverSections().getByRole('button', { name: 'Overview' })).toHaveAttribute('aria-current', 'page')
    expect(directory().getByRole('button', { name: 'Open Local shared world B on this PC' })).toHaveAttribute('aria-current', 'true')
    await waitFor(() => expect(JSON.parse(localStorage.getItem('togetherserver.ui-preferences.v1')!).selectedServer).toBe(secondId))
    expect(mutationCalls().map(([path]) => path)).toEqual(['/api/local/mode/friend', '/api/local/mode/host'])
    expectNoServerAction()
  })

  it.each(['mobile picker', 'sidebar button'] as const)(
    'keeps the latest Chat navigation from the %s when an earlier directory selection finishes late', async control => {
      const connected = friend()
      state = connected
      render(<App />)
      await screen.findByRole('region', { name: 'Play' })
      await act(async () => {})
      const read = transport.read.getMockImplementation()!
      let finishSelection!: (snapshot: Snapshot) => void
      const selectedSnapshot = new Promise<Snapshot>(resolve => { finishSelection = resolve })
      let heldSelection = false
      transport.read.mockImplementation((path: string, ...args: unknown[]) => {
        if (path === '/api/local/snapshot' && !heldSelection) {
          heldSelection = true
          return selectedSnapshot
        }
        return read(path, ...args)
      })
      fireEvent.click(directory().getByRole('button', { name: 'Open Synthetic world on Evening Host' }))
      await waitFor(() => expect(heldSelection).toBe(true))
      if (control === 'mobile picker') fireEvent.change(screen.getByLabelText('Selected server sections', { selector: 'select' }),
        { target: { value: 'chat' } })
      else fireEvent.click(joinSections().getByRole('button', { name: 'Chat' }))
      const chat = await screen.findByRole('region', { name: 'Server chat' })
      expect(chat).toHaveAttribute('data-profile-id', id)
      expect(screen.getByLabelText('Selected server sections', { selector: 'select' })).toHaveValue('chat')
      await act(async () => { finishSelection(connected) })
      expect(screen.getByRole('region', { name: 'Server chat' })).toBe(chat)
      expect(joinSections().getByRole('button', { name: 'Chat' })).toHaveAttribute('aria-current', 'page')
      expect(screen.getByLabelText('Selected server sections', { selector: 'select' })).toHaveValue('chat')
      expect(screen.queryByRole('region', { name: 'Play' })).not.toBeInTheDocument()
      expect(directory().getByRole('button', { name: 'Open Synthetic world on Evening Host' })).toHaveAttribute('aria-current', 'true')
      expectNoMutation()
    })

  it('flushes the protected chat draft before changing the selected Join server', async () => {
    const connected = friend()
    const secondId = '33333333-3333-4333-8333-333333333333'
    connected.profiles.push({ ...connected.profiles[0], id: secondId, name: 'Second assigned world' })
    state = connected
    render(<App />)
    await screen.findByRole('region', { name: 'Play' })
    fireEvent.click(joinSections().getByRole('button', { name: 'Chat' }))
    const editor = await screen.findByRole('textbox', { name: 'Test chat draft' })
    fireEvent.change(editor, { target: { value: 'Keep this message with its original server' } })
    transport.flush.mockResolvedValue(false)
    const target = directory().getByRole('button', { name: 'Open Second assigned world on Evening Host' })
    fireEvent.click(target)
    await waitFor(() => expect(transport.flush).toHaveBeenCalledOnce())
    expect(directory().getByRole('button', { name: 'Open Synthetic world on Evening Host' })).toHaveAttribute('aria-current', 'true')
    expect(editor).toHaveValue('Keep this message with its original server')
    expect(screen.getByRole('region', { name: 'Server chat' })).toHaveAttribute('data-profile-id', id)
    let finish!: (value: boolean) => void
    transport.flush.mockImplementationOnce(() => new Promise<boolean>(resolve => { finish = resolve }))
    fireEvent.click(target)
    await waitFor(() => expect(transport.flush).toHaveBeenCalledTimes(2))
    expect(directory().getByRole('button', { name: 'Open Synthetic world on Evening Host' })).toHaveAttribute('aria-current', 'true')
    await act(async () => { finish(true) })
    await waitFor(() => expect(target).toHaveAttribute('aria-current', 'true'))
    expect(await screen.findByRole('region', { name: 'Play' })).toBeVisible()
    fireEvent.click(joinSections().getByRole('button', { name: 'Chat' }))
    expect(await screen.findByRole('region', { name: 'Server chat' })).toHaveAttribute('data-profile-id', secondId)
    expect(joinSections().getByRole('button', { name: 'Chat' })).toHaveAttribute('aria-current', 'page')
    expectNoMutation()
  })

  it('rejects a native Logs destination when the selected server has no View logs grant', async () => {
    const notify = nativeNotifications()
    const connected = friend()
    connected.hostCapabilities.push('server-logs-v1')
    connected.connections = [structuredClone(connected)]
    state = connected
    render(<App />)
    await screen.findByRole('region', { name: 'Play' })
    await notify({ workspace: 'join', section: 'logs', profileId: id, connectionId: connected.connectionId })
    expect(screen.getByRole('region', { name: 'Play' })).toBeVisible()
    expect(joinSections().getByRole('button', { name: 'Play' })).toHaveAttribute('aria-current', 'page')
    expect(joinSections().queryByRole('button', { name: 'Logs' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Server logs' })).not.toBeInTheDocument()
    expectNoMutation()
  })

  it('renders only the selected Join server and gives Chat, Logs and Shared saves their own pages', async () => {
    const connected = friend()
    connected.hostCapabilities.push('server-logs-v1', 'shared-worlds-v2')
    connected.profiles[0].canViewLogs = true
    const secondId = '33333333-3333-4333-8333-333333333333'
    connected.profiles.push({ ...connected.profiles[0], id: secondId, name: 'Second assigned world', joinAddress: '192.0.2.10:25566' })
    state = connected
    render(<App />)
    await screen.findByRole('region', { name: 'Play' })
    expect(screen.getByRole('heading', { name: 'Synthetic world' })).toBeVisible()
    expect(screen.queryByRole('heading', { name: 'Second assigned world' })).not.toBeInTheDocument()
    expect(screen.getAllByRole('region', { name: 'Play' })).toHaveLength(1)
    expect(screen.queryByRole('region', { name: 'Server chat' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Server logs' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Shared worlds' })).not.toBeInTheDocument()
    fireEvent.click(directory().getByRole('button', { name: 'Open Second assigned world on Evening Host' }))
    expect(await screen.findByRole('heading', { name: 'Second assigned world' })).toBeVisible()
    expect(screen.queryByRole('heading', { name: 'Synthetic world' })).not.toBeInTheDocument()
    fireEvent.click(joinSections().getByRole('button', { name: 'Chat' }))
    const chat = await screen.findByRole('region', { name: 'Server chat' })
    expect(chat).toBeVisible()
    expect(chat).toHaveAttribute('data-profile-id', secondId)
    expect(chat.closest('details')).toBeNull()
    expect(screen.queryByRole('region', { name: 'Play' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Server logs' })).not.toBeInTheDocument()
    fireEvent.click(joinSections().getByRole('button', { name: 'Logs' }))
    const logs = await screen.findByRole('region', { name: 'Server logs' })
    expect(logs).toBeVisible()
    expect(logs).toHaveAttribute('data-endpoint', `/api/local/friend/${secondId}/logs`)
    expect(logs.closest('details')).toBeNull()
    expect(chat).not.toBeInTheDocument()
    fireEvent.click(joinSections().getByRole('button', { name: 'Shared saves' }))
    const saves = await openSharedHostingDetails()
    expect(saves).toBeVisible()
    expect(saves).toHaveAttribute('data-profile-id', secondId)
    expect(within(saves).queryByRole('checkbox')).not.toBeInTheDocument()
    expect(saves.closest('details')).toBeNull()
    expect(logs).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Play' })).not.toBeInTheDocument()
    fireEvent.click(joinSections().getByRole('button', { name: 'Play' }))
    expect(await screen.findByRole('region', { name: 'Play' })).toBeVisible()
    expect(directory().getByRole('button', { name: 'Open Second assigned world on Evening Host' })).toHaveAttribute('aria-current', 'true')
    expect(screen.getByRole('heading', { name: 'Second assigned world' })).toBeVisible()
    expect(saves).not.toBeInTheDocument()
    expectNoMutation()
  })

  it('declining Forget Host in the app confirmation preserves the selected connection without a POST', async () => {
    state = friend()
    render(<App />)
    await screen.findByRole('region', { name: 'Play' })
    fireEvent.click(joinSections().getByRole('button', { name: 'Settings' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Forget this Host' }))
    const confirmation = await screen.findByRole('dialog', { name: 'Forget this saved Host?' })
    expect(confirmation).toHaveTextContent('TogetherServer will first ask the Host to remove this PC’s access.')
    expectNoMutation()
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(confirmation).not.toBeInTheDocument())
    expect(screen.getByRole('textbox', { name: /^Saved connection name/ })).toHaveValue('Evening Host')
    expectNoMutation()
  })

  it.each(['Offline', 'Ready'] as const)('does not inherit global grants when the selected %s server has no explicit permissions', async serverState => {
    const connected = friend()
    connected.hostCapabilities.push('server-logs-v1')
    Object.assign(connected.profiles[0], { state: serverState, canStart: undefined, canStop: undefined, canExtendTimer: undefined,
      canViewLogs: undefined, canStopNow: true, canRestartNow: true, onlinePlayers: 0 })
    // Snapshot-wide grants are deliberately true; only the selected server grants authorize actions.
    state = connected
    render(<App />)
    const play = await screen.findByRole('region', { name: 'Play' })
    const start = within(play).queryByRole('button', { name: 'Start server' })
    if (start) { expect(start).toBeDisabled(); fireEvent.click(start) }
    expect(within(play).queryByRole('button', { name: 'Stop server' })).not.toBeInTheDocument()
    expect(joinSections().queryByRole('button', { name: 'Logs' })).not.toBeInTheDocument()
    fireEvent.click(joinSections().getByRole('button', { name: 'Settings' }))
    await screen.findByRole('textbox', { name: /^Saved connection name/u })
    const restart = screen.queryByRole('button', { name: 'Restart server' })
    if (restart) { expect(restart).toBeDisabled(); fireEvent.click(restart) }
    expect(screen.queryByRole('button', { name: /Add \d+ minutes/ })).not.toBeInTheDocument()
    expectNoMutation()
  })

  it.each(['stale', 'Unknown connection', 'Unknown players'] as const)('fails closed for %s even when remembered permissions and safety flags allow control', async evidence => {
    const connected = friend()
    Object.assign(connected.profiles[0], { canStart: true, canStop: true, canStopNow: true, canRestartNow: true,
      canExtendTimer: true, onlinePlayers: 0 })
    if (evidence === 'stale') connected.lastConnectedUtc = new Date(Date.now() - 120_000).toISOString()
    if (evidence === 'Unknown connection') connected.state = 'Disconnected/Unknown'
    if (evidence === 'Unknown players') connected.profiles[0].onlinePlayers = null
    state = connected
    render(<App />)
    const play = await screen.findByRole('region', { name: 'Play' })
    const stop = within(play).queryByRole('button', { name: 'Stop server' })
    if (stop) { expect(stop).toBeDisabled(); fireEvent.click(stop) }
    expect(within(play).queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
    if (evidence !== 'Unknown players') {
      expect(within(play).queryByRole('region', { name: 'Connection details' })).not.toBeInTheDocument()
      expect(screen.getByText('Last known / Unknown')).toBeVisible()
    }
    fireEvent.click(joinSections().getByRole('button', { name: 'Settings' }))
    await screen.findByRole('textbox', { name: /^Saved connection name/u })
    for (const name of ['Restart server', 'Add 15 minutes']) {
      const action = screen.queryByRole('button', { name })
      if (action) { expect(action).toBeDisabled(); fireEvent.click(action) }
    }
    expectNoMutation()
    fireEvent.click(joinSections().getByRole('button', { name: 'Shared saves' }))
    const saves = await openSharedHostingDetails()
    expect(within(saves).queryByRole('checkbox')).not.toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument())
    expectNoMutation()
  })

  it('does not keep a completed remote startup banner on a Ready server', async () => {
    const connected = friend()
    const message = 'Remote process launched. Waiting for readiness.'
    connected.profiles[0].operation = { id: 'operation', action: 'start', state: 'Succeeded', ok: true,
      code: 'Started', message, requestedUtc: new Date().toISOString(), completedUtc: new Date().toISOString() }
    state = connected
    render(<App />)
    await screen.findByRole('region', { name: 'Play' })
    expect(screen.queryByText(message)).not.toBeInTheDocument()
    expectNoServerAction()
  })
  it('puts play before connection maintenance and opens the moved doctor with keyboard focus', async () => {
    state = friend()
    render(<App />)
    const play = await screen.findByRole('region', { name: 'Play' })
    expect(screen.queryByRole('textbox', { name: /^Saved connection name/u })).not.toBeInTheDocument()
    expect(joinSections().getByRole('button', { name: 'Play' })).toHaveAttribute('aria-current', 'page')
    const support = within(play).getByLabelText('Server tools and connection help').closest('details')!
    fireEvent.click(support.querySelector('summary')!)
    expect(screen.getAllByText(/^Your access:/)).toHaveLength(1)
    expect(screen.queryByText(/^Host permissions:/)).not.toBeInTheDocument()
    fireEvent.click(within(play).getByRole('button', { name: 'Open Connection help' }))
    const summary = await screen.findByText(/^Connection help ·/)
    await waitFor(() => {
      expect(summary.closest('details')).toHaveAttribute('open')
      expect(summary).toHaveFocus()
    })
    expect(joinSections().getByRole('button', { name: 'Settings' })).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByRole('button', { name: 'Stop' })).not.toBeInTheDocument()
    expectNoServerAction()
  })

  it('keeps adding another Host cancelable and returns to the selected saved connection', async () => {
    state = friend()
    render(<App />)
    await screen.findByRole('region', { name: 'Play' })
    fireEvent.click(directory().getByRole('button', { name: 'Connect' }))
    const code = await screen.findByLabelText('Server code')
    fireEvent.change(code, { target: { value: 'Synthetic unsent code' } })
    fireEvent.click(screen.getByRole('button', { name: 'Cancel and return to saved Host' }))
    expect(await screen.findByRole('region', { name: 'Play' })).toBeInTheDocument()
    expect(joinSections().getByRole('button', { name: 'Play' })).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByLabelText('Server code')).not.toBeInTheDocument()
    expectNoMutation()
  })

  it('keeps manual game joining available when controls are paused', async () => {
    state = { ...friend(), state: 'Disabled', remoteControlsEnabled: false }
    render(<App />)
    const play = await screen.findByRole('region', { name: 'Play' })
    expect(within(play).getByText(/Open Minecraft from Windows Start/)).toBeInTheDocument()
    expect(within(play).queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
    expect(screen.getByText(/Connected · controls paused/)).toBeInTheDocument()
    expectNoServerAction()
  })

  it('retains the saved Host and explicit owner-expiry notice while denying join actions', async () => {
    state = { ...friend(), state: 'Access expired', connectionCode: 'AccessExpired' }
    render(<App />)
    const play = await screen.findByRole('region', { name: 'Play' })
    expect(screen.getByText('The Host set an end date for this PC’s access. That date has passed.')).toBeVisible()
    expect(within(play).queryByText(/Open Minecraft from Windows Start/)).not.toBeInTheDocument()
    expect(within(play).queryByRole('button', { name: 'Copy Server IP' })).not.toBeInTheDocument()
    fireEvent.click(joinSections().getByRole('button', { name: 'Settings' }))
    expect(await screen.findByRole('textbox', { name: /^Saved connection name/u })).toBeVisible()
    expect(screen.getByRole('textbox', { name: /^Saved connection name/ })).toHaveValue('Evening Host')
    expectNoMutation()
  })
})

describe('current profiles and removed access', () => {
  it('hides removed PCs and their totals until requested, and deletes only after confirmation', async () => {
    const info = withPcs([{ ...testPc(pcId, 'Old PC'), revoked: true }, testPc(secondPcId, 'Current PC')])
    await openAccess()
    expect(screen.queryByRole('article', { name: 'Old PC' })).not.toBeInTheDocument()
    expect(screen.getByText('1 of 1 Friend PCs shown.')).toBeInTheDocument()
    fireEvent.change(screen.getByRole('combobox', { name: 'PC status' }), { target: { value: 'revoked' } })
    fireEvent.click(within(screen.getByRole('article', { name: 'Old PC' })).getByRole('button', { name: 'Delete PC' }))
    let dialog = await screen.findByRole('dialog', { name: 'Delete Old PC?' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(transport.change.mock.calls.some(([, method]) => method === 'DELETE')).toBe(false)
    expect(info.devices.some(device => device.id === pcId)).toBe(true)
    fireEvent.click(within(screen.getByRole('article', { name: 'Old PC' })).getByRole('button', { name: 'Delete PC' }))
    dialog = await screen.findByRole('dialog', { name: 'Delete Old PC?' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete PC' }))
    await waitFor(() => expect(transport.change).toHaveBeenCalledWith(`/api/local/devices/${pcId}`, 'DELETE', expect.any(Function), undefined, undefined))
    await waitFor(() => expect(screen.queryByText('Old PC')).not.toBeInTheDocument())
    expect(info.devices.map(device => device.id)).toEqual([secondPcId])
    fireEvent.change(screen.getByRole('combobox', { name: 'PC status' }), { target: { value: 'all' } })
    expect(await screen.findByRole('article', { name: 'Current PC' })).toBeInTheDocument()
    expectNoServerAction()
  })

  it('keeps a removed record and shows a failed delete so it can be retried', async () => {
    withPcs([{ ...testPc(pcId, 'Old PC'), revoked: true }])
    const change = transport.change.getMockImplementation()!
    transport.change.mockImplementation(async (path: string, method: string, ...args: unknown[]) =>
      method === 'DELETE' ? { ok: false, code: 'SuccessorRosterReadOnly', message: 'Signed membership needs owner review.' } :
        change(path, method, ...args))
    await openAccess()
    expect(screen.getByText('0 of 0 Friend PCs shown.')).toBeInTheDocument()
    expect(screen.getByText(/No current Friend PCs/)).toBeInTheDocument()
    fireEvent.change(screen.getByRole('combobox', { name: 'PC status' }), { target: { value: 'revoked' } })
    fireEvent.click(screen.getByRole('button', { name: 'Delete PC' }))
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Delete Old PC?' })).getByRole('button', { name: 'Delete PC' }))
    await waitFor(() => expect(screen.getByRole('article', { name: 'Old PC' })).toHaveTextContent('Signed membership needs owner review.'))
    expect(within(screen.getByRole('article', { name: 'Old PC' })).getByRole('button', { name: 'Delete PC' })).toBeEnabled()
    expectNoServerAction()
  })

  it('preserves the selected server and lists both local worlds without recreating the old favorite picker', async () => {
    const activeId = '33333333-3333-4333-8333-333333333333'
    host().settings.profiles.push({ ...host().settings.profiles[0], id: activeId, name: 'Running world' })
    host().runs.push({ ...host().runs[0], profileId: activeId, state: 'Ready' })
    localStorage.setItem('togetherserver.ui-preferences.v1', JSON.stringify({ selectedServer: id,
      favoriteServers: [id], serverOrder: [id, activeId] }))
    await openHost()
    expect(directory().getByRole('button', { name: 'Open Running world on this PC' })).toBeVisible()
    expect(directory().getByRole('button', { name: 'Open Contract fixture on this PC' })).toHaveAttribute('aria-current', 'true')
    expect(screen.queryByLabelText('Selected server choices')).not.toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Contract fixture workspace' })).toBeInTheDocument()
    expect(host().settings.profiles.map(profile => profile.id)).toEqual([id, activeId])
    expectNoServerAction()
  })

  it('hides a selected revoked Host and its cached servers until requested, then deletes that exact saved Host', async () => {
    const removed = { ...friend(), state: 'Revoked', connectionCode: 'Revoked', connectionName: 'Old Host' }
    const active = { ...friend(), connectionId: secondPcId, connectionName: 'Current Host' }
    state = { ...removed, connections: [structuredClone(removed), structuredClone(active)] }
    transport.change.mockImplementation(async (path: string) => {
      if (path.endsWith('/forget')) state = { ...active, connections: [structuredClone(active)] }
      return { ok: true, code: 'ConnectionForgottenLocally', message: 'Saved connection deleted.' }
    })
    render(<App />)
    await screen.findByRole('heading', { name: 'Join a server' })
    expect(screen.queryByText('Old Host')).not.toBeInTheDocument()
    expect(screen.queryByText('Revoked')).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Synthetic world' })).not.toBeInTheDocument()
    fireEvent.click(directory().getByRole('checkbox', { name: 'Show removed access' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Open Synthetic world on Old Host' }))
    await screen.findByRole('region', { name: 'Play' })
    fireEvent.click(joinSections().getByRole('button', { name: 'Settings' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Forget this Host' }))
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Delete this saved Host?' })).getByRole('button', { name: 'Delete Host' }))
    await waitFor(() => expect(transport.change).toHaveBeenCalledWith(`/api/local/friend/connections/${removed.connectionId}/forget`, 'POST', expect.any(Function), undefined, undefined))
    await waitFor(() => expect(screen.queryByText('Old Host')).not.toBeInTheDocument())
    expect(directory().getByRole('button', { name: 'Open Synthetic world on Current Host' })).toHaveAttribute('aria-current', 'true')
    expectNoServerAction()
  })
})

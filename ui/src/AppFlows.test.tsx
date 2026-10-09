import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import hostWire from '../../contracts/host-snapshot.v1.json'
import { parseSnapshot, type FriendSnapshot, type HostSnapshot, type Snapshot } from './contracts'
import type { EditorDraftGuardChange } from './editorProtectedDraft'
import { readProtectedDraft } from './protectedUiDrafts'
import { serializeProtectedSetupDraft } from './setupDraft'
import { App } from './main'
import themeCss from './theme.css?inline'
import baseCss from './style.css?inline'
import companionCss from './companion.css?inline'
import qolCss from './qol.css?inline'
import coreCss from './core.css?inline'

// vitest.config.ts explicitly enables these inline imports; load the whole cascade.
const shellCss = [themeCss, baseCss, companionCss, qolCss, coreCss].join('\n')

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
vi.mock('./ServerChat', () => ({
  ServerChat: () => <section aria-label="Server chat">Chat messages</section>,
  ServerChatCardSummary: () => <p>Chat summary</p>
}))
vi.mock('./ServerLogViewer', async importOriginal => ({
  ...await importOriginal<typeof import('./ServerLogViewer')>(),
  ServerLogViewer: () => {
    if (transport.failures.logs) throw new Error('Synthetic pane failure')
    return <section aria-label="Server logs">Diagnostic records</section>
  }
}))
vi.mock('./RecentSessions', () => ({ RecentSessions: ({ visible }: { visible: boolean }) => {
  if (visible && transport.failures.sessions) throw new Error('Synthetic pane failure')
  return <section hidden={!visible}>Recent sessions</section>
} }))
vi.mock('./WeeklySummary', () => ({ WeeklySummary: () => <section aria-label="Seven-day summary">Retained seven-day evidence</section> }))
vi.mock('./BackupCatalog', () => ({ BackupCatalog: () => <section aria-label="Backup catalog">Completed backups</section> }))
vi.mock('./SharedWorldControls', () => ({
  HostSharedSaves: () => <section aria-label="Shared saves">Shared save controls</section>,
  FriendSharedWorlds: () => <section aria-label="Shared worlds">Received copies</section>
}))
vi.mock('./WorldLoadRehearsal', async importOriginal => ({
  ...await importOriginal<typeof import('./WorldLoadRehearsal')>(),
  WorldLoadRehearsalPanel: () => <section aria-label="Game restore drill">Test a copy in the game</section>
}))
vi.mock('./GameSettingsPanel', () => ({ GameSettingsPanel: () => <section aria-label="Simple game settings">Common game settings</section> }))
vi.mock('./ServerFilesPanel', async () => {
  const { useEffect, useState } = await import('react')
  return { ServerFilesPanel: function TestFiles({ onDraftGuardChange }: { onDraftGuardChange: EditorDraftGuardChange }) {
    const [draft, setDraft] = useState('')
    useEffect(() => {
      onDraftGuardChange('synthetic-file', draft ? { dirty: true, saving: false, flush: transport.flush } : null)
      return () => onDraftGuardChange('synthetic-file', null)
    }, [draft, onDraftGuardChange])
    if (transport.failures.files) throw new Error('Synthetic pane failure')
    return <section aria-label="Server files"><label>Test file draft<input value={draft} onChange={event => setDraft(event.target.value)} /></label></section>
  } }
})

const id = hostWire.settings.profiles[0].id
let state: Snapshot
let recoveryBlocked: boolean
const instance = {
  kind: 'Production', displayName: 'TogetherServer', isStaging: false, freshWorldsOnly: false,
  startupAvailable: false, updatesAvailable: false, localPort: 5127, companionPort: 5131,
  valheimPort: 2456, minecraftJavaPort: 25565, minecraftBedrockPort: 19132,
  dataRoot: 'C:\\synthetic', dataIsolation: 'Synthetic fixture'
}
const companion = { listenerActive: false, listenerState: 'Idle', listenerWarning: null,
  endpoint: '', fingerprint: null, certificates: null, route: { mode: 'DirectInternet', address: '' }, devices: [], stopSafety: {} }
const ports = { control: { state: 'Idle', port: 5131, detail: 'Fixture listener is off.', remoteState: 'Unknown' }, games: [] }
const host = () => state as HostSnapshot

beforeEach(() => {
  localStorage.clear()
  vi.mocked(readProtectedDraft).mockReset().mockResolvedValue({ ok: true, text: null, revision: 1, message: '' })
  recoveryBlocked = false
  transport.failures.logs = transport.failures.sessions = transport.failures.files = false
  transport.flush.mockReset().mockResolvedValue(true)
  state = parseSnapshot(structuredClone(hostWire))
  host().settings.publicGameIp = '192.0.2.10'
  host().settings.publicGameIpCheckedUtc = new Date().toISOString()
  host().runs[0].state = 'Offline'
  host().runs[0].detail = 'The exact recorded process is offline.'
  host().runs[0].playerCountObservedUtc = new Date().toISOString()
  transport.read.mockReset().mockImplementation(async (path: string) => {
    if (path === '/api/local/snapshot') return state
    if (path === '/api/local/data-recovery') return { lifecycleBlocked: recoveryBlocked, notices: [] }
    if (path === '/api/local/instance') return instance
    if (path === '/api/local/companion') return companion
    if (path === '/api/local/network/ports') return ports
    if (path === '/api/local/desktop/preferences') return { available: false, launchAtLogin: false, closeToTray: false, startupAvailable: false }
    if (path === '/api/local/desktop/notifications') return { quietMode: false }
    throw new Error('Synthetic leaf data unavailable')
  })
  transport.change.mockReset().mockImplementation(async () => ({ ok: true, code: 'Requested', message: 'Request accepted.', snapshot: state }))
  vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('No network in shell tests') }))
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { callback(0); return 1 })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {})
  vi.spyOn(window, 'confirm').mockReturnValue(true)
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

describe('Host task structure', () => {
  it('opens usable header disclosures and lets Escape dismiss them before leaving Settings', async () => {
    await openHost()
    fireEvent.click(screen.getByRole('button', { name: 'Settings' }))
    await screen.findByRole('heading', { name: 'Settings', level: 1 })
    const notifications = screen.getByLabelText('Notifications')
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
    expect(screen.getByRole('heading', { name: 'Settings', level: 1 })).toBeInTheDocument()
    expectNoServerAction()
  })

  it('leads with one Start before tabs and keeps connection checks available on demand', async () => {
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
    before(start, within(workspace).getByRole('navigation', { name: 'Selected server sections' }))
    before(start, within(workspace).getByText('Chat summary'))
    const checks = within(workspace).getByText('Status & connection checks').closest('details')!
    expect(checks).not.toHaveAttribute('open')
    fireEvent.click(checks.querySelector('summary')!)
    expect(within(checks).getByRole('note', { name: 'Connection check before Start' })).toBeVisible()
    expectNoServerAction()
    fireEvent.click(start)
    await waitFor(() => expect(transport.change).toHaveBeenCalledWith(`/api/local/profiles/${id}/start`, 'POST', expect.any(Function), undefined, undefined))
    expect(await within(workspace).findByText('Request accepted.')).toBeInTheDocument()
    expect(within(workspace.querySelector('.server-command-header')!).getByText('Waiting for a real readiness observation.')).toBeVisible()
    expect(within(workspace).getByRole('button', { name: 'Stop server' })).toBeInTheDocument()
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

  it('keeps the player-impact confirmation when Stop moves next to server status', async () => {
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 2
    vi.mocked(window.confirm).mockReturnValue(false)
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Stop server' }))
    expect(window.confirm).toHaveBeenCalledWith(expect.stringMatching(/2.*players/i))
    expectNoServerAction()
  })

  it('leads Overview with Stop and join details before supporting content', async () => {
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 0
    const workspace = await openHost()
    const primary = within(workspace.querySelector('.server-command-header')!)
    before(primary.getByRole('button', { name: 'Stop server' }), within(workspace).getByRole('navigation', { name: 'Selected server sections' }))
    before(within(workspace).getByRole('region', { name: 'Connection details' }), within(workspace).getByText('Chat summary'))
    expectNoServerAction()
    vi.mocked(window.confirm).mockReturnValue(false)
    fireEvent.click(primary.getByRole('button', { name: 'Stop server' }))
    expect(window.confirm).toHaveBeenCalled()
    expectNoServerAction()
  })

  it.each(['Players', 'Chat', 'Logs', 'Sessions', 'Backups'])('keeps the same primary Stop available while browsing %s', async tab => {
    host().runs[0].state = 'Ready'
    host().runs[0].onlinePlayers = 0
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: tab }))
    await waitFor(() => expect(within(workspace).getByRole('button', { name: tab })).toHaveAttribute('aria-current', 'page'))
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
    fireEvent.click(within(workspace).getByRole('button', { name: 'Chat' }))
    await waitFor(() => expect(within(workspace).getByRole('button', { name: 'Chat' })).toHaveAttribute('aria-current', 'page'))
    expect(header.getByRole('timer')).toBeVisible()
    expectNoServerAction()
  })

  it('keeps comparison out of a single-server flow and makes it available without changing the selected server', async () => {
    host().settings.profiles.push({ ...host().settings.profiles[0], id: '22222222-2222-4222-8222-222222222222', name: 'Second synthetic server' })
    await openHost()
    const comparison = screen.getByText(/Compare all 2 servers/).closest('details')!
    expect(comparison).not.toHaveAttribute('open')
    fireEvent.click(comparison.querySelector('summary')!)
    expect(comparison).toHaveAttribute('open')
    expect(screen.getByRole('region', { name: 'All servers' })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Contract fixture workspace' })).toBeInTheDocument()
  })

  it('keeps backup and maintenance tools reachable in their owning pane, without adding them to chat or logs', async () => {
    host().settings.profiles[0].kind = 'Valheim'
    const workspace = await openHost()
    expect(within(workspace).queryByRole('button', { name: 'Back up now' })).not.toBeInTheDocument()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Backups' }))
    expect(await within(workspace).findByRole('region', { name: 'Backup catalog' })).toBeInTheDocument()
    expect(within(workspace).getByRole('button', { name: 'Back up now' })).toBeEnabled()
    expect(within(workspace).getByRole('region', { name: 'Shared saves' })).toBeInTheDocument()
    expect(within(workspace).getByRole('button', { name: 'Start server' })).toBeEnabled()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Chat' }))
    expect(await within(workspace).findByRole('region', { name: 'Server chat' })).toBeInTheDocument()
    expect(within(workspace).queryByRole('button', { name: 'Back up now' })).not.toBeInTheDocument()
    expect(within(workspace).queryByText('More server actions')).not.toBeVisible()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Logs' }))
    expect(await within(workspace).findByRole('region', { name: 'Server logs' })).toBeInTheDocument()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Sessions' }))
    expect(await within(workspace).findByRole('region', { name: 'Seven-day summary' })).toBeInTheDocument()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Maintenance & setup' }))
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
    fireEvent.click(within(workspace).getByRole('button', { name: 'Settings & files' }))
    const editor = await within(workspace).findByRole('textbox', { name: 'Test file draft' })
    fireEvent.change(editor, { target: { value: 'An unsaved configuration change' } })
    const start = within(workspace.querySelector('.server-command-header')!).getByRole('button', { name: 'Start server' })
    expect(start).toBeDisabled()
    fireEvent.click(start)
    expectNoServerAction()
    fireEvent.change(editor, { target: { value: '' } })
    await waitFor(() => expect(start).toBeEnabled())
    expectNoServerAction()
  })

  it('routes recovery to diagnostics even when maintenance is also enabled', async () => {
    recoveryBlocked = true
    host().settings.profiles[0].maintenance = { enabled: true, message: 'Updating synthetic settings.' }
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Review recovery' }))
    expect(await screen.findByRole('heading', { name: 'Preflight & diagnostics' })).toBeInTheDocument()
    expectNoServerAction()
  })

  it.each(['logs', 'sessions', 'files'] as const)('keeps %s render failures and retry visible with the actual pane styles', async failure => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    transport.failures[failure] = true
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: { logs: 'Logs', sessions: 'Sessions', files: 'Settings & files' }[failure] }))
    await waitFor(() => expect(workspace).toHaveAttribute('data-server-tab', failure), { timeout: 3000 })
    const error = await within(workspace).findByRole('alert')
    expect(error).toBeVisible()
    const retry = within(error).getByRole('button', { name: 'Try section again' })
    expect(retry).toBeVisible()
    transport.failures[failure] = false
    fireEvent.click(retry)
    await waitFor(() => expect(within(workspace).queryByRole('alert')).not.toBeInTheDocument())
    expectNoServerAction()
  })

  it('keeps a failed draft save on the editing screen and permits navigation after retry', async () => {
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Settings & files' }))
    const editor = await within(workspace).findByRole('textbox', { name: 'Test file draft' })
    fireEvent.change(editor, { target: { value: 'Last unsaved keystroke' } })
    transport.flush.mockResolvedValue(false)
    fireEvent.click(within(workspace).getByRole('button', { name: 'Chat' }))
    await waitFor(() => expect(transport.flush).toHaveBeenCalledOnce())
    expect(editor).toHaveValue('Last unsaved keystroke')
    expect(within(workspace).queryByRole('region', { name: 'Server chat' })).not.toBeInTheDocument()
    transport.flush.mockResolvedValue(true)
    fireEvent.click(within(workspace).getByRole('button', { name: 'Chat' }))
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

  it('opens the Custom certification from Maintenance & setup while chat stays focused on conversation', async () => {
    host().settings.profiles[0].kind = 'Custom'
    const workspace = await openHost()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Maintenance & setup' }))
    expect(await within(workspace).findByRole('button', { name: 'Begin live certification' })).toBeInTheDocument()
    fireEvent.click(within(workspace).getByRole('button', { name: 'Chat' }))
    expect(await within(workspace).findByRole('region', { name: 'Server chat' })).toBeInTheDocument()
    expect(within(workspace).queryByRole('button', { name: 'Begin live certification' })).not.toBeInTheDocument()
    expectNoServerAction()
  })

  it('remembers command-palette destinations for returning to a server and exposes Chat and Players commands', async () => {
    const workspace = await openHost()
    fireEvent.click(screen.getByRole('button', { name: 'Commands' }))
    const search = await screen.findByRole('combobox', { name: 'Search commands' })
    fireEvent.change(search, { target: { value: 'Contract fixture players' } })
    fireEvent.keyDown(search, { key: 'Enter' })
    await waitFor(() => expect(within(workspace).getByRole('button', { name: 'Players' })).toHaveAttribute('aria-current', 'page'))
    expect(JSON.parse(localStorage.getItem('togetherserver.ui-preferences.v1')!).serverTabs[id]).toBe('players')
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

  it('keeps staging rehearsal out of first setup and reachable through Connection help', async () => {
    host().settings.profiles = []
    host().runs = []
    const read = transport.read.getMockImplementation()!
    transport.read.mockImplementation(async (path: string, ...args: unknown[]) => path === '/api/local/instance'
      ? { ...instance, kind: 'Staging', isStaging: true, freshWorldsOnly: true } : read(path, ...args))
    render(<App />)
    expect(await screen.findByRole('region', { name: 'Host or join a server' })).toBeInTheDocument()
    expect(screen.queryByText('Test with another owned PC')).not.toBeInTheDocument()
    const workspaces = screen.getByRole('navigation', { name: 'TogetherServer workspaces' })
    const settings = within(workspaces).getByRole('button', { name: 'Settings' })
    await waitFor(() => expect(settings).toBeEnabled())
    // Navigation awaits draft guards before committing the Settings workspace.
    await act(async () => { fireEvent.click(settings) })
    expect(settings).toHaveAttribute('aria-current', 'page')
    const sections = await screen.findByRole('navigation', { name: 'Host settings sections' })
    fireEvent.click(within(sections).getByRole('button', { name: 'Connection help' }))
    expect(await screen.findByText('Test with another owned PC')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Prepare rehearsal' })).toBeInTheDocument()
    expect(transport.change).not.toHaveBeenCalled()
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
    expect(screen.getByText('Loading local state…')).toBeVisible()
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

describe('Friend task structure', () => {
  it('puts play before connection maintenance and opens the moved doctor with keyboard focus', async () => {
    state = friend()
    render(<App />)
    const play = await screen.findByRole('region', { name: 'Play' })
    const recovery = screen.getByText('Connection identity and recovery').closest('details')!
    before(play, recovery)
    expect(recovery).not.toHaveAttribute('open')
    fireEvent.click(within(play).getByRole('button', { name: 'Open Connection Doctor' }))
    const summary = screen.getByText(/^Connection Doctor ·/)
    expect(summary.closest('details')).toHaveAttribute('open')
    expect(summary).toHaveFocus()
    expect(screen.getAllByText(/^Your access:/)).toHaveLength(1)
    expect(screen.queryByText(/^Host permissions:/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Stop' })).not.toBeInTheDocument()
    expectNoServerAction()
  })

  it('keeps adding another Host cancelable and returns to the selected saved connection', async () => {
    state = friend()
    render(<App />)
    await screen.findByRole('region', { name: 'Play' })
    fireEvent.click(screen.getByRole('button', { name: 'Add another Host' }))
    const code = await screen.findByLabelText('Server code')
    fireEvent.change(code, { target: { value: 'Synthetic unsent code' } })
    fireEvent.click(screen.getByRole('button', { name: 'Cancel and return to saved Host' }))
    expect(await screen.findByRole('region', { name: 'Play' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Server code')).not.toBeInTheDocument()
    expect(transport.change).not.toHaveBeenCalled()
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
    expect(screen.getByText('The Host ended access for this PC at the saved time.')).toBeVisible()
    expect(screen.getByText('Connection identity and recovery')).toBeInTheDocument()
    expect(within(play).queryByText(/Open Minecraft from Windows Start/)).not.toBeInTheDocument()
    expect(within(play).queryByRole('button', { name: 'Copy Server IP' })).not.toBeInTheDocument()
    expect(transport.change).not.toHaveBeenCalled()
  })
})

import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import fixture from '../../contracts/host-snapshot.v1.json'
import { parseSnapshot, type FriendSnapshot } from './contracts'
import { directoryWithSnapshot, GuestHostingLabel, ordinaryLocalServers, parseServerDirectory, ServerDirectory,
  type DirectoryHost, type DirectoryServer, type ServerDirectoryView } from './ServerDirectory'

const now = Date.parse('2026-10-19T12:00:00Z')
const ids = { local: '11111111-1111-4111-8111-111111111111', guest: '22222222-2222-4222-8222-222222222222',
  source: '33333333-3333-4333-8333-333333333333', connection: '44444444-4444-4444-8444-444444444444',
  host: '55555555-5555-4555-8555-555555555555', removed: '66666666-6666-4666-8666-666666666666' }
function server(patch: Partial<DirectoryServer> = {}): DirectoryServer {
  return { id: ids.local, name: 'Our world', kind: 'Valheim', state: 'Ready', onlinePlayers: 0, maxPlayers: 10,
    playerCountTrusted: true, playerObservationSource: 'BuiltInQuery', playerCountObservedUtc: new Date(now).toISOString(),
    playerCountRefreshing: false, playerCountFailureCount: 0, separateCopySourceProfileId: null,
    sourceName: null, sourceHostName: null, ...patch }
}
function host(patch: Partial<DirectoryHost> = {}): DirectoryHost {
  return { connectionId: ids.connection, hostId: ids.host, connectionName: 'Morgan’s desktop', hostProfileName: 'Morgan',
    state: 'Connected', connectionCode: null, lastConnectedUtc: new Date(now).toISOString(),
    servers: [server({ id: ids.source, name: 'Shared meadow' })], ...patch }
}
function directory(): ServerDirectoryView {
  return { localServers: [server(), server({ id: ids.guest, name: 'Shared local copy',
    separateCopySourceProfileId: ids.source, sourceName: 'Shared meadow', sourceHostName: 'Morgan' })],
  friendHosts: [host()] }
}
function show(view = directory(), extra: Partial<Parameters<typeof ServerDirectory>[0]> = {}) {
  const actions = { onRetry: vi.fn(), onSelect: vi.fn(), onAddLocal: vi.fn(), onConnect: vi.fn() }
  render(<ServerDirectory view={view} selection={{ kind: 'local', profileId: ids.local }} nowMs={now} busy={false}
    {...actions} {...extra} />)
  return actions
}

describe('strict combined server directory', () => {
  it('decodes the bounded display projection with guest origins and all saved Hosts', () => {
    expect(parseServerDirectory(directory())).toEqual(directory())
    expect(parseServerDirectory({ localServers: [], friendHosts: [] })).toEqual({ localServers: [], friendHosts: [] })
  })

  it('rejects malformed metadata, duplicate identities, and oversized input', () => {
    for (const patch of [{ id: 'invalid' }, { onlinePlayers: -1 }, { onlinePlayers: '0' }, { playerCountTrusted: 'true' },
      { playerCountRefreshing: undefined }, { playerCountFailureCount: -1 }, { playerCountObservedUtc: 'never' },
      { separateCopySourceProfileId: 'world-name' }, { sourceHostName: 4 }])
      expect(() => parseServerDirectory({ localServers: [{ ...server(), ...patch }], friendHosts: [] })).toThrow()
    expect(() => parseServerDirectory({ localServers: [server(), server()], friendHosts: [] })).toThrow(/duplicate/)
    expect(() => parseServerDirectory({ localServers: [], friendHosts: [host(), host()] })).toThrow(/duplicate/)
    expect(() => parseServerDirectory({ localServers: Array.from({ length: 101 }, () => server()), friendHosts: [] })).toThrow(/large/)
    expect(() => parseServerDirectory({ localServers: [], friendHosts: [{ ...host(), lastConnectedUtc: 'bad' }] })).toThrow()
  })
})

describe('one grouped server list', () => {
  it('lists one inactive owner server without changing its retained profiles or selected logical identity', () => {
    const view: ServerDirectoryView = { localServers: [server({ state: 'Offline' }),
      server({ id: ids.guest, name: 'Earlier continuation', state: 'Offline', separateCopySourceProfileId: ids.local }),
      server({ id: ids.source, name: 'Later continuation', state: 'Offline', separateCopySourceProfileId: ids.local })], friendHosts: [] }
    const retained = structuredClone(view)
    const actions = show(view, { selection: { kind: 'local', profileId: ids.guest } })
    const own = screen.getByRole('button', { name: 'Open Our world on this PC' })
    expect(own).toHaveAttribute('aria-current', 'true')
    expect(screen.queryByRole('button', { name: /Open (Earlier|Later) continuation/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Hosting for friends' })).not.toBeInTheDocument()
    fireEvent.click(own)
    expect(actions.onSelect).toHaveBeenLastCalledWith({ kind: 'local', profileId: ids.local })
    expect(view).toEqual(retained)
  })

  it('keeps the returning owner’s running continuation under Your servers', () => {
    show({ localServers: [server({ id: ids.guest, name: 'Current continuation',
      separateCopySourceProfileId: ids.local, sourceName: 'Our world', state: 'Ready' }), server({ state: 'Offline' }),
    server({ id: ids.removed, name: 'Old copy', state: 'Offline', separateCopySourceProfileId: ids.local })],
    friendHosts: [host({ servers: [server({ id: ids.local, name: 'A saved Friend alias' })] })] })
    const active = screen.getByRole('button', { name: 'Open Current continuation on this PC' })
    expect(active.closest('.server-directory-group')).toHaveTextContent('Your servers')
    expect(active).not.toHaveTextContent('Hosted for')
    expect(screen.queryByRole('button', { name: 'Open Our world on this PC' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Open Old copy on this PC' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Hosting for friends' })).not.toBeInTheDocument()
  })

  it('retains every active, Unknown, Failed, and review-needed copy while hiding inactive duplicates', () => {
    const copies = ['Ready', 'Starting', 'Stopping', 'Unknown', 'Failed'].map((state, index) => server({
      id: `00000000-0000-4000-8000-${String(index).padStart(12, '0')}`, name: state, state, separateCopySourceProfileId: ids.local }))
    const review = server({ id: ids.connection, name: 'Needs review', state: 'Offline', requiresReview: true, separateCopySourceProfileId: ids.local })
    const original = server({ state: 'Ready' })
    const view = { localServers: [original,
      server({ id: ids.guest, state: 'Offline', separateCopySourceProfileId: ids.local }), ...copies, review], friendHosts: [] }
    expect(ordinaryLocalServers(view)).toEqual([original, ...copies, review])
    show(view)
    for (const copy of [...copies, review]) expect(screen.getByRole('button', { name: `Open ${copy.name} on this PC` })).toBeInTheDocument()
  })

  it('uses the joined server entry for inactive Friend copies and keeps an orphan available for review', () => {
    const first = server({ id: ids.guest, state: 'Offline', separateCopySourceProfileId: ids.source })
    const second = server({ id: ids.removed, state: 'Offline', separateCopySourceProfileId: ids.source })
    expect(ordinaryLocalServers({ localServers: [first, second], friendHosts: [host()] })).toEqual([])
    expect(ordinaryLocalServers({ localServers: [first, second], friendHosts: [] })).toEqual([first])
    expect(ordinaryLocalServers({ localServers: [first, second], friendHosts: [host({ state: 'Revoked' })] })).toEqual([first])
    expect(ordinaryLocalServers({ localServers: [{ ...first, state: 'Unknown' }, second], friendHosts: [host()] }))
      .toEqual([{ ...first, state: 'Unknown' }])
  })

  it('keeps own, guest-hosted, and Friend servers distinguishable and selects their exact identities', () => {
    const actions = show()
    expect(screen.getByRole('heading', { name: 'Your servers' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Hosting for friends' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Friend servers' })).toBeInTheDocument()
    const own = screen.getByRole('button', { name: 'Open Our world on this PC' })
    const guest = screen.getByRole('button', { name: 'Open Shared local copy on this PC' })
    const friend = screen.getByRole('button', { name: 'Open Shared meadow on Morgan' })
    expect(own).toHaveAttribute('aria-current', 'true')
    expect(guest).toHaveTextContent('Hosted for Morgan')
    fireEvent.click(guest)
    expect(actions.onSelect).toHaveBeenLastCalledWith({ kind: 'local', profileId: ids.guest })
    fireEvent.click(friend)
    expect(actions.onSelect).toHaveBeenLastCalledWith({ kind: 'friend', connectionId: ids.connection, profileId: ids.source })
  })

  it('retains explicit creation and connection actions while searching by source world', () => {
    const actions = show()
    fireEvent.click(screen.getByRole('button', { name: 'New server' }))
    fireEvent.click(screen.getByRole('button', { name: 'Connect' }))
    expect(actions.onAddLocal).toHaveBeenCalledOnce()
    expect(actions.onConnect).toHaveBeenCalledOnce()
    fireEvent.change(screen.getByRole('searchbox', { name: 'Find a server or friend' }), { target: { value: 'Shared meadow' } })
    expect(screen.queryByRole('button', { name: 'Open Our world on this PC' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Open Shared local copy on this PC' })).toBeInTheDocument()
    expect(actions.onSelect).not.toHaveBeenCalled()
  })

  it('hides revoked saved Hosts until deliberate reveal and leaves offline counts unavailable', () => {
    show({ localServers: [], friendHosts: [host({ state: 'Disconnected/Unknown', lastConnectedUtc: null }),
      host({ connectionId: ids.removed, connectionName: 'Removed PC', hostProfileName: 'Removed owner',
        state: 'Revoked', connectionCode: 'Revoked', servers: [] })] })
    expect(screen.getByRole('button', { name: 'Open Shared meadow on Morgan' })).toHaveTextContent('Host offline · status unavailable')
    expect(screen.queryByText('Removed owner')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('checkbox', { name: 'Show removed access' }))
    expect(screen.getByRole('button', { name: /Open Removed PC/ })).toHaveTextContent('No assigned servers')
  })

  it('keeps pending, first and repeated unavailable counts stable without loading labels or warnings', () => {
    const view = { localServers: [server({ onlinePlayers: null, playerCountTrusted: false, playerCountRefreshing: true,
      playerCountFailureCount: 0, playerCountObservedUtc: null }),
    server({ id: ids.guest, name: 'First miss', onlinePlayers: null, playerCountTrusted: false,
      playerCountRefreshing: false, playerCountFailureCount: 1 }),
    server({ id: ids.source, name: 'Repeated issue', onlinePlayers: null, playerCountTrusted: false,
      playerCountRefreshing: false, playerCountFailureCount: 2 })], friendHosts: [] }
    show(view)
    expect(screen.getByRole('button', { name: 'Open Our world on this PC' })).toHaveTextContent('Players —')
    expect(screen.getByRole('button', { name: 'Open First miss on this PC' })).toHaveTextContent('Players —')
    expect(screen.getByRole('button', { name: 'Open Repeated issue on this PC' })).toHaveTextContent('Players —')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('keeps fresh zero visible during refresh and refuses stale or future player evidence', () => {
    show({ localServers: [server({ playerCountRefreshing: true }),
      server({ id: ids.guest, name: 'Stale count', playerCountObservedUtc: new Date(now - 11_000).toISOString() }),
      server({ id: ids.source, name: 'Future count', playerCountObservedUtc: new Date(now + 1000).toISOString() })], friendHosts: [] })
    expect(screen.getByRole('button', { name: 'Open Our world on this PC' })).toHaveTextContent('0 / 10 online')
    expect(screen.getByRole('button', { name: 'Open Stale count on this PC' })).toHaveTextContent('Players —')
    expect(screen.getByRole('button', { name: 'Open Future count on this PC' })).toHaveTextContent('Players —')
  })

  it('shows clear empty groups and a retry without taking an implicit action', () => {
    const actions = show({ localServers: [], friendHosts: [] }, { error: 'Unavailable' })
    expect(screen.getByText('Create a server on this PC.')).toBeInTheDocument()
    expect(screen.getByText('Connect using a friend’s server code.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(actions.onRetry).toHaveBeenCalledOnce()
    expect(actions.onSelect).not.toHaveBeenCalled()
    expect(actions.onAddLocal).not.toHaveBeenCalled()
  })
})

it('overlays current local status without losing guest origin in the list or detail header', () => {
  const snapshot = parseSnapshot(fixture)
  if (snapshot.mode !== 'Host') throw new Error('Expected the shared Host fixture.')
  const guest = { ...snapshot.settings.profiles[0], id: ids.guest, name: 'Shared local copy', separateCopySourceProfileId: ids.source }
  snapshot.settings.profiles = [guest]
  snapshot.runs = [{ ...snapshot.runs[0], profileId: ids.guest, state: 'Ready', onlinePlayers: 3 }]
  const combined = directoryWithSnapshot(directory(), snapshot)
  expect(combined.localServers).toHaveLength(1)
  expect(combined.localServers[0]).toMatchObject({ onlinePlayers: 3, separateCopySourceProfileId: ids.source,
    sourceName: 'Shared meadow', sourceHostName: 'Morgan' })
  expect(combined.friendHosts).toHaveLength(1)
  render(<GuestHostingLabel server={combined.localServers[0]} />)
  expect(screen.getByText('Hosting for Morgan')).toBeInTheDocument()
  expect(within(screen.getByText('Hosting for Morgan').parentElement!).getByText('Shared save: Shared meadow')).toBeInTheDocument()
})

it.each(['process identity', 'session recovery', 'crash recovery'])('keeps an Offline retained copy visible for unresolved %s', reason => {
  const snapshot = parseSnapshot(fixture)
  if (snapshot.mode !== 'Host') throw new Error('Expected the shared Host fixture.')
  const original = { ...snapshot.settings.profiles[0], id: ids.local }
  snapshot.settings.profiles = [original, { ...original, id: ids.guest, separateCopySourceProfileId: ids.local }]
  snapshot.runs = [ids.local, ids.guest].map(profileId => ({ ...snapshot.runs[0], profileId, state: 'Offline', processId: null }))
  if (reason === 'process identity') snapshot.runs[1].processId = 123
  if (reason === 'session recovery') snapshot.startupRecovery = { previousSessionInterrupted: true, previousStartedUtc: null,
    items: [{ profileId: ids.guest, state: 'Needs review', detail: 'Review the recorded process.', canResume: false }], message: '', windowsRestartRegistered: false }
  if (reason === 'crash recovery') snapshot.crashRecovery = { [ids.guest]: { profileId: ids.guest, cycleId: 'review', state: 'Suspended',
    attempts: 1, crashDetectedUtc: new Date(now).toISOString(), nextAttemptUtc: null, readinessDeadlineUtc: null, recoveredUtc: null, lastFailure: 'Review needed.' } }
  const combined = directoryWithSnapshot(null, snapshot)
  expect(ordinaryLocalServers(combined)).toEqual([expect.objectContaining({ id: ids.guest, requiresReview: true })])
})

it('removes a forgotten friend immediately when the current snapshot supplies the complete saved-connection list', () => {
  const empty: FriendSnapshot = { mode: 'Friend', state: 'Not connected', detail: 'Forgotten.', endpoint: '',
    lastConnectedUtc: null, remoteControlsEnabled: false, canStart: false, canStop: false, profiles: [],
    connectionId: '', connections: [], hostCapabilities: [] }
  const combined = directoryWithSnapshot(directory(), empty)
  expect(combined.friendHosts).toEqual([])
  expect(combined.localServers).toEqual(directory().localServers)
})

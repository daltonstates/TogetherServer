import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SharedHostNotice } from './SharedHostNotice'

const profileId = '11111111-1111-4111-8111-111111111111'
const selfId = '22222222-2222-4222-8222-222222222222'
const remoteId = '33333333-3333-4333-8333-333333333333'
const otherId = '44444444-4444-4444-8444-444444444444'
const sessionId = '55555555-5555-4555-8555-555555555555'
const otherSessionId = '66666666-6666-4666-8666-666666666666'
const groupKey = 'A'.repeat(64)
const branchHash = 'B'.repeat(64)
const address = 'https://192.0.2.10:5131'
const otherAddress = 'https://192.0.2.20:5131'
const stamp = '2026-10-19T20:00:00Z'
const stoppedStamp = '2026-10-19T20:05:00Z'
const props = { profileId, connectionId: 'saved-host-a' }
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })

function group() {
  const peer = (deviceId: string, route: string) => ({ deviceId, publicKey: `synthetic-key-${deviceId}`, revision: 1,
    routes: [{ address: route, certificateSha256: 'D'.repeat(64) }], hasConflict: false, authorized: true,
    availableVersionHashes: [], announcedUtc: stamp, contact: null })
  const session = (deviceId: string, id: string, branch: string) => ({ deviceId, publicKey: `synthetic-key-${deviceId}`,
    hasConflict: false, authorized: true, session: { sessionId: id, branchHash: branch, revision: 1,
      state: 'Hosting', baseVersionHash: 'E'.repeat(64), headVersionHash: null, startedUtc: stamp, endedUtc: null as string | null } })
  return { groupKey, profileId, name: 'Weekend world', selfDeviceId: selfId, sharingEnabled: false,
    receiveConsented: false, canReceive: false, canReshare: false, canHost: false, warning: null as string | null,
    selection: null as { canChoose: boolean; needsChoice: boolean; heads: ReturnType<typeof defaultHead>[] } | null,
    state: { root: { ownerPublicKey: 'synthetic-owner-key', profileId, groupId: profileId }, groupKey,
      peers: [peer(remoteId, address), peer(otherId, otherAddress)], branches: [],
      sessions: [session(remoteId, sessionId, branchHash)], defaultChoices: [], needsChoice: false,
      knownSessions: 1, announcementCount: 2,
      replication: { knownPeers: 2, peersWithRoutes: 2, peersLastSyncSucceeded: 0, claimedCopyPeers: 0,
        lastSuccessfulSyncUtc: null, hasConflicts: false, capacityReached: false, state: 'No peer sync recorded' } } }
}
function stoppedGroup() {
  const current = group()
  current.state.sessions[0].session.state = 'Stopped'
  current.state.sessions[0].session.endedUtc = stoppedStamp
  current.state.peers[0].announcedUtc = stoppedStamp
  return current
}
function defaultHead(deviceId = remoteId, id = sessionId, branch = branchHash) {
  return { selectionHash: '9'.repeat(64), deviceId, publicKey: `synthetic-key-${deviceId}`, sessionId: id,
    branchHash: branch, authorized: true, targetKnown: true }
}
function deferred<T>() {
  let resolve!: (value: T) => void
  return { promise: new Promise<T>(done => { resolve = done }), resolve: (value: T) => resolve(value) }
}
function visibility(value: 'visible' | 'hidden') {
  Object.defineProperty(document, 'visibilityState', { configurable: true, value })
  fireEvent(document, new Event('visibilitychange'))
}
async function flush() { await act(async () => {}) }

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
})

describe('Shared Host notice', () => {
  it('shows only a compact historical caution next to actions and preserves details on explicit review', async () => {
    const fetcher = vi.fn(async () => reply([stoppedGroup()]))
    vi.stubGlobal('fetch', fetcher)
    render(<SharedHostNotice {...props} compact />)
    const summary = await screen.findByLabelText('Earlier Host differs from the original Host')
    expect(summary.closest('details')).not.toHaveAttribute('open')
    fireEvent.click(summary)
    expect(screen.getByRole('region', { name: 'Shared Host status details' })).toHaveTextContent('Historical stopped session report')
    expect(screen.getByText(/This is historical information, not a current incident/)).toBeInTheDocument()
    expect(screen.getByText(/A signed announcement does not prove live reachability/)).toBeInTheDocument()
    expect(fetcher).toHaveBeenCalledTimes(1)
  })

  it('suppresses original owner announcements by canonical zero member identity and owner key despite matching display names', async () => {
    const current = group()
    current.state.peers[0].deviceId = '00000000-0000-0000-0000-000000000000'
    current.state.peers[0].publicKey = current.state.root.ownerPublicKey
    current.state.sessions[0].deviceId = current.state.peers[0].deviceId
    current.state.sessions[0].publicKey = current.state.root.ownerPublicKey
    vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
    render(<SharedHostNotice {...props} compact />)
    await flush()
    expect(screen.queryByRole('region')).not.toBeInTheDocument()
    expect(screen.queryByText('PC ending 000000')).not.toBeInTheDocument()
    expect(document.querySelector('.shared-host-compact')).not.toBeInTheDocument()
  })

  it('shows a signed hosting report and approved route without changing permissions, defaults, connections or servers', async () => {
    const fetch = vi.fn(async () => reply([group()]))
    const connected = vi.fn()
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} onConnected={connected} />)
    expect(await screen.findByRole('heading', { name: 'Another PC reports hosting' })).toBeInTheDocument()
    expect(screen.getByText('PC ending 333333')).toBeInTheDocument()
    expect(screen.getByText(address)).toBeInTheDocument()
    expect(screen.getByText(/Reachability is checked when you connect/)).toBeInTheDocument()
    expect(screen.getByText(/open Join for the current game's address and port/)).toBeInTheDocument()
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(fetch.mock.calls[0]).toEqual(['/api/local/world-peers', expect.objectContaining({ cache: 'no-store', headers: { 'X-TogetherServer-Local': '1' } })])
    expect(connected).not.toHaveBeenCalled()
  })

  it('connects only after an explicit click using the exact selected member and session', async () => {
    const mutations: { path: string; body: unknown; signal: AbortSignal | null | undefined }[] = []
    const connected = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, body: JSON.parse(String(init.body)), signal: init.signal })
        return reply({ ok: true, code: 'PeerSessionConnected', message: 'The selected Host connection was verified and saved.' })
      }
      return reply([group()])
    }))
    render(<SharedHostNotice {...props} onConnected={connected} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Connect to PC ending 333333' }))
    await waitFor(() => expect(connected).toHaveBeenCalledTimes(1))
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/connect`, body: { deviceId: remoteId, sessionId }, signal: expect.any(AbortSignal) }])
    expect(await screen.findByRole('status')).toHaveTextContent('verified and saved')
  })

  it('labels companion port 5132 as TogetherServer HTTPS without inventing a game port 2456', async () => {
    const current = group()
    current.state.peers[0].routes[0].address = 'https://192.0.2.10:5132'
    vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
    render(<SharedHostNotice {...props} />)
    const notice = await screen.findByRole('article', { name: 'Hosting reports for Weekend world' })
    expect(within(notice).getByText('TogetherServer HTTPS')).toBeInTheDocument()
    expect(within(notice).getByText('https://192.0.2.10:5132')).toBeInTheDocument()
    expect(notice).toHaveTextContent('These HTTPS addresses connect the TogetherServer apps.')
    expect(notice).toHaveTextContent("open Join for the current game's address and port")
    expect(notice).not.toHaveTextContent('2456')
    expect(notice).not.toHaveTextContent('Server IP')
  })

  it('follows Preparing, Hosting and Stopped reports through read-only polls without remounting', async () => {
    vi.useFakeTimers()
    const current = group()
    current.state.sessions[0].session.state = 'Preparing'
    const fetch = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () => reply([current]))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} />)
    await flush()
    expect(screen.getByRole('heading', { name: 'Another PC is starting the server' })).toBeInTheDocument()
    current.state.sessions[0].session.state = 'Hosting'
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByRole('heading', { name: 'Another PC reports hosting' })).toBeInTheDocument()
    current.state.sessions[0].session.state = 'Stopped'
    current.state.sessions[0].session.endedUtc = stoppedStamp
    current.state.peers[0].announcedUtc = stoppedStamp
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByRole('heading', { name: 'Another PC reported a stopped session' })).toBeInTheDocument()
    expect(screen.getByText('PC ending 333333 reported that this hosting session stopped.')).toBeInTheDocument()
    expect(screen.getByText(/Other PCs may still be hosting/)).toHaveTextContent('Save delivery is checked separately.')
    expect(document.querySelector('time')).toHaveAttribute('datetime', stoppedStamp)
    expect(screen.queryByRole('button', { name: /^Connect|^Current saved Host/ })).not.toBeInTheDocument()
    expect(screen.queryByText('TogetherServer HTTPS')).not.toBeInTheDocument()
    expect(fetch).toHaveBeenCalledTimes(3)
    expect(fetch.mock.calls.every(call => call[0] === '/api/local/world-peers' && call.length === 2 &&
      !(call[1] as RequestInit | undefined)?.method)).toBe(true)
    visibility('hidden')
    await act(async () => { await vi.advanceTimersByTimeAsync(15000) })
    expect(screen.queryByRole('region', { name: 'Shared Host updates' })).not.toBeInTheDocument()
    expect(fetch).toHaveBeenCalledTimes(3)
  })

  it('shows only the latest eligible stopped report and offers no connection for it', async () => {
    const current = stoppedGroup()
    current.state.sessions.push({ ...structuredClone(current.state.sessions[0]), deviceId: otherId,
      publicKey: `synthetic-key-${otherId}`, session: { ...current.state.sessions[0].session,
        sessionId: otherSessionId, endedUtc: '2026-10-19T20:10:00Z' } })
    current.state.peers[1].announcedUtc = '2026-10-19T20:10:00Z'
    vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
    render(<SharedHostNotice {...props} currentHostAddress={otherAddress} currentHostSessionId={otherSessionId} />)
    expect(await screen.findByRole('heading', { name: 'Another PC reported a stopped session' })).toBeInTheDocument()
    expect(screen.getByText('PC ending 444444 reported that this hosting session stopped.')).toBeInTheDocument()
    expect(screen.queryByText('PC ending 333333 reported that this hosting session stopped.')).not.toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it.each(['session revoked', 'session conflict', 'peer revoked', 'peer conflict', 'wrong key', 'this PC', 'other world', 'later interrupted report'])(
    'does not present a stopped report after %s', async reason => {
      const current = stoppedGroup()
      if (reason === 'session revoked') current.state.sessions[0].authorized = false
      if (reason === 'session conflict') current.state.sessions[0].hasConflict = true
      if (reason === 'peer revoked') current.state.peers[0].authorized = false
      if (reason === 'peer conflict') current.state.peers[0].hasConflict = true
      if (reason === 'wrong key') current.state.peers[0].publicKey = 'another-synthetic-key'
      if (reason === 'this PC') current.selfDeviceId = remoteId
      if (reason === 'other world') { current.profileId = otherId; current.state.root.profileId = otherId }
      if (reason === 'later interrupted report') current.state.sessions.push({ ...structuredClone(current.state.sessions[0]),
        session: { ...current.state.sessions[0].session, sessionId: otherSessionId, state: 'Interrupted', endedUtc: '2026-10-19T20:10:00Z' } })
      vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
      render(<SharedHostNotice {...props} />)
      await flush()
      expect(screen.queryByRole('region', { name: 'Shared Host updates' })).not.toBeInTheDocument()
    })

  it('retains an active independent choice instead of claiming the whole group stopped', async () => {
    const current = stoppedGroup()
    current.state.sessions.push({ ...structuredClone(current.state.sessions[0]), deviceId: otherId,
      publicKey: `synthetic-key-${otherId}`, session: { ...current.state.sessions[0].session,
        sessionId: otherSessionId, state: 'Hosting', endedUtc: null } })
    vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
    render(<SharedHostNotice {...props} />)
    expect(await screen.findByRole('heading', { name: 'Another PC reports hosting' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Connect to PC ending 444444' })).toBeEnabled()
    expect(screen.queryByRole('heading', { name: 'Another PC reported a stopped session' })).not.toBeInTheDocument()
  })

  it('keeps concurrent hosting reports separate without showing a legacy preferred Host', async () => {
    const current = group()
    current.state.sessions.push({ ...structuredClone(current.state.sessions[0]), deviceId: otherId, publicKey: `synthetic-key-${otherId}`,
      session: { ...current.state.sessions[0].session, sessionId: otherSessionId, branchHash: 'C'.repeat(64) } })
    current.selection = { canChoose: true, needsChoice: false, heads: [defaultHead(otherId, otherSessionId, 'C'.repeat(64))] }
    current.state.needsChoice = true
    const fetch = vi.fn(async () => reply([current]))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} currentHostAddress={address} />)
    expect(await screen.findByRole('heading', { name: 'Multiple PCs report hosting' })).toBeInTheDocument()
    expect(screen.getByText(/These servers may have separate progress/)).toBeInTheDocument()
    expect(screen.queryByText(/different save histories or default choices/)).not.toBeInTheDocument()
    expect(screen.getByText('Saved Host address').closest('li')).toHaveTextContent('PC ending 333333')
    expect(screen.queryByText('Group default')).not.toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /^Connect to PC ending/ })).toHaveLength(2)
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('does not treat a matching address as the exact current session', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply([group()])))
    render(<SharedHostNotice {...props} currentHostAddress={`${address}/`} currentHostSessionId={otherSessionId} />)
    expect(await screen.findByText('Saved Host address')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Connect to PC ending 333333' })).toBeEnabled()
    expect(screen.queryByText('Current saved Host')).not.toBeInTheDocument()
  })

  it('marks an exact current session without offering another connection write', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply([group()])))
    render(<SharedHostNotice {...props} currentHostAddress={address} currentHostSessionId={sessionId} />)
    expect(await screen.findByRole('heading', { name: 'Your saved Host reports hosting' })).toBeInTheDocument()
    expect(screen.getByText('Current saved Host')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Current saved Host: PC ending 333333' })).toBeDisabled()
  })

  it.each(['revoked session', 'conflicting session', 'revoked peer', 'conflicting peer', 'wrong key', 'no route',
    'invalid route', 'Stopped', 'Interrupted', 'this PC', 'other world'])(
    'offers no notice or connect action for %s', async reason => {
      const current = group()
      if (reason === 'revoked session') current.state.sessions[0].authorized = false
      if (reason === 'conflicting session') current.state.sessions[0].hasConflict = true
      if (reason === 'revoked peer') current.state.peers[0].authorized = false
      if (reason === 'conflicting peer') current.state.peers[0].hasConflict = true
      if (reason === 'wrong key') current.state.peers[0].publicKey = 'another-synthetic-key'
      if (reason === 'no route') current.state.peers[0].routes = []
      if (reason === 'invalid route') current.state.peers[0].routes[0].address = 'https://user:password@example.invalid/?token=secret'
      if (['Stopped', 'Interrupted'].includes(reason)) current.state.sessions[0].session.state = reason
      if (reason === 'this PC') current.selfDeviceId = remoteId
      if (reason === 'other world') { current.profileId = otherId; current.state.root.profileId = otherId }
      const fetch = vi.fn(async () => reply([current]))
      vi.stubGlobal('fetch', fetch)
      render(<SharedHostNotice {...props} />)
      await flush()
      expect(fetch).toHaveBeenCalledTimes(1)
      expect(screen.queryByRole('region', { name: 'Shared Host updates' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button')).not.toBeInTheDocument()
    })

  it('announces a starting Host without claiming game readiness and connects only on request', async () => {
    const current = group()
    current.state.sessions[0].session.state = 'Preparing'
    const connected = vi.fn()
    const fetch = vi.fn(async (_url: string, init?: RequestInit) => init?.method
      ? reply({ ok: true, code: 'PeerSessionConnected', message: 'The selected Host connection was verified and saved.' })
      : reply([current]))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} onConnected={connected} />)
    expect(await screen.findByRole('heading', { name: 'Another PC is starting the server' })).toBeInTheDocument()
    expect(screen.getByText('Starting')).toBeInTheDocument()
    expect(screen.getByText(/Game readiness has not been confirmed/)).toBeInTheDocument()
    expect(connected).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Connect to PC ending 333333' }))
    await waitFor(() => expect(connected).toHaveBeenCalledOnce())
    expect(fetch).toHaveBeenCalledWith(`/api/local/world-peers/${groupKey}/connect`, expect.objectContaining({
      method: 'POST', body: JSON.stringify({ deviceId: remoteId, sessionId }),
    }))
  })

  it('shows another PC starting and then hosting within the next visible second without a page refresh', async () => {
    vi.useFakeTimers()
    const current = group()
    current.state.sessions[0].session.state = 'Preparing'
    const fetch = vi.fn().mockResolvedValueOnce(reply([])).mockImplementation(async () => reply([current]))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} connectionId={null} />)
    await flush()
    expect(screen.queryByRole('region', { name: 'Shared Host updates' })).not.toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByRole('heading', { name: 'Another PC is starting the server' })).toBeInTheDocument()
    expect(screen.getByText(/Game readiness has not been confirmed/)).toBeInTheDocument()
    current.state.sessions[0].session.state = 'Hosting'
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByRole('heading', { name: 'Another PC reports hosting' })).toBeInTheDocument()
    expect(screen.queryByText('Starting')).not.toBeInTheDocument()
    expect(fetch).toHaveBeenCalledTimes(3)
    expect(fetch.mock.calls.every(([, init]) => !(init as RequestInit | undefined)?.method)).toBe(true)
  })

  it('withdraws cached actions on unknown replies and restores them only after a successful retry', async () => {
    let failed = false
    vi.useFakeTimers()
    vi.stubGlobal('fetch', vi.fn(async () => {
      if (failed) throw new Error('Offline')
      return reply([group()])
    }))
    render(<SharedHostNotice {...props} />)
    await flush()
    expect(screen.getByRole('button', { name: 'Connect to PC ending 333333' })).toBeEnabled()
    failed = true
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByRole('status')).toHaveTextContent('Shared Host updates unavailable')
    expect(screen.getByRole('status')).toHaveTextContent('Saved reports cannot confirm who is hosting now')
    expect(screen.queryByRole('button', { name: /^Connect/ })).not.toBeInTheDocument()
    failed = false
    fireEvent.click(screen.getByRole('button', { name: 'Retry Host updates' }))
    await flush()
    expect(screen.getByRole('button', { name: 'Connect to PC ending 333333' })).toBeEnabled()
  })

  it('withdraws the stopped report after a failed read and restores it only from a new successful read', async () => {
    let failed = false
    vi.useFakeTimers()
    vi.stubGlobal('fetch', vi.fn(async () => {
      if (failed) throw new Error('Offline')
      return reply([stoppedGroup()])
    }))
    render(<SharedHostNotice {...props} />)
    await flush()
    expect(screen.getByRole('heading', { name: 'Another PC reported a stopped session' })).toBeInTheDocument()
    failed = true
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(screen.getByRole('status')).toHaveTextContent('Shared Host updates unavailable')
    expect(screen.queryByRole('heading', { name: 'Another PC reported a stopped session' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Connect/ })).not.toBeInTheDocument()
    failed = false
    fireEvent.click(screen.getByRole('button', { name: 'Retry Host updates' }))
    await flush()
    expect(screen.getByRole('heading', { name: 'Another PC reported a stopped session' })).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('rejects malformed world replies rather than presenting them as verified hosting', async () => {
    const current = group()
    current.state.root.profileId = otherId
    vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
    render(<SharedHostNotice {...props} />)
    expect(await screen.findByRole('status')).toHaveTextContent('Shared Host updates unavailable')
    expect(screen.queryByRole('button', { name: /^Connect/ })).not.toBeInTheDocument()
  })

  it('polls every visible second and pauses without overlapping a pending read', async () => {
    vi.useFakeTimers()
    const pending = deferred<Response>()
    const fetch = vi.fn().mockResolvedValueOnce(reply([group()])).mockImplementationOnce(() => pending.promise)
      .mockImplementation(async () => reply([group()]))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} />)
    await flush()
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(fetch).toHaveBeenCalledTimes(2)
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(fetch).toHaveBeenCalledTimes(2)
    await act(async () => { pending.resolve(reply([group()])) })
    visibility('hidden')
    await act(async () => { await vi.advanceTimersByTimeAsync(20000) })
    expect(fetch).toHaveBeenCalledTimes(2)
    expect(screen.queryByRole('region', { name: 'Shared Host updates' })).not.toBeInTheDocument()
    visibility('visible')
    await flush()
    expect(fetch).toHaveBeenCalledTimes(3)
    expect(screen.getByRole('button', { name: 'Connect to PC ending 333333' })).toBeEnabled()
  })

  it.each(['connection', 'profile', 'address', 'hidden prop', 'hidden page'])(
    'aborts a pending connection and ignores a late success after changing %s', async change => {
      const pending = deferred<Response>()
      const connected = vi.fn()
      let writeSignal: AbortSignal | null | undefined
      vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method) { writeSignal = init.signal; return pending.promise }
        return reply([group()])
      }))
      const view = render(<SharedHostNotice {...props} onConnected={connected} />)
      fireEvent.click(await screen.findByRole('button', { name: 'Connect to PC ending 333333' }))
      expect(writeSignal?.aborted).toBe(false)
      if (change === 'connection') view.rerender(<SharedHostNotice {...props} connectionId="saved-host-b" onConnected={connected} />)
      if (change === 'profile') view.rerender(<SharedHostNotice {...props} profileId={otherId} onConnected={connected} />)
      if (change === 'address') view.rerender(<SharedHostNotice {...props} currentHostAddress={otherAddress} onConnected={connected} />)
      if (change === 'hidden prop') view.rerender(<SharedHostNotice {...props} visible={false} onConnected={connected} />)
      if (change === 'hidden page') visibility('hidden')
      expect(writeSignal?.aborted).toBe(true)
      await act(async () => { pending.resolve(reply({ ok: true, code: 'PeerSessionConnected', message: 'Stale connection success.' })) })
      expect(connected).not.toHaveBeenCalled()
      expect(screen.queryByText('Stale connection success.')).not.toBeInTheDocument()
    })

  it.each(['hosting', 'stopped'])('aborts stale reads and cannot restore a withdrawn %s report from late replies', async kind => {
    const oldRead = deferred<Response>()
    let signal: AbortSignal | null | undefined
    const fetch = vi.fn().mockImplementationOnce((_url: string, init?: RequestInit) => { signal = init?.signal; return oldRead.promise })
      .mockImplementation(async () => reply([]))
    vi.stubGlobal('fetch', fetch)
    const view = render(<SharedHostNotice {...props} />)
    view.rerender(<SharedHostNotice {...props} connectionId="saved-host-b" />)
    expect(signal?.aborted).toBe(true)
    await act(async () => { oldRead.resolve(reply([kind === 'stopped' ? stoppedGroup() : group()])) })
    expect(screen.queryByRole('button', { name: /^Connect/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Another PC reported a stopped session' })).not.toBeInTheDocument()
  })

  it('has bounded read and connection waits and never retries a connection automatically', async () => {
    vi.useFakeTimers()
    const pendingRead = deferred<Response>()
    const fetch = vi.fn().mockImplementationOnce(() => pendingRead.promise).mockImplementation(async (_url: string, init?: RequestInit) =>
      init?.method ? new Promise<Response>(() => {}) : reply([group()]))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(12000) })
    expect(screen.getByRole('status')).toHaveTextContent('Host updates took too long')
    fireEvent.click(screen.getByRole('button', { name: 'Retry Host updates' }))
    await flush()
    fireEvent.click(screen.getByRole('button', { name: 'Connect to PC ending 333333' }))
    await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
    expect(screen.getByRole('alert')).toHaveTextContent('connection check took too long')
    expect(fetch.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method)).toHaveLength(1)
    await act(async () => { pendingRead.resolve(reply([group()])) })
    expect(screen.getByRole('alert')).toHaveTextContent('connection check took too long')
  })

  it('keeps failures visible, prevents duplicate clicks, and does not call onConnected on refusal', async () => {
    const pending = deferred<Response>()
    const connected = vi.fn()
    const fetch = vi.fn(async (_url: string, init?: RequestInit) => init?.method ? pending.promise : reply([group()]))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} onConnected={connected} />)
    const button = await screen.findByRole('button', { name: 'Connect to PC ending 333333' })
    fireEvent.click(button); fireEvent.click(button)
    expect(button).toBeDisabled()
    await act(async () => { pending.resolve(reply({ ok: false, code: 'PeerSessionUnreachable', message: 'This PC could not verify the selected Host.' })) })
    expect(screen.getByRole('alert')).toHaveTextContent('could not verify')
    expect(connected).not.toHaveBeenCalled()
    expect(fetch.mock.calls.filter(([, init]) => init?.method)).toHaveLength(1)
  })

  it('shows approved reports while disabled but offers no connection mutation', async () => {
    const fetch = vi.fn(async () => reply([group()]))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} disabled />)
    const button = await screen.findByRole('button', { name: 'Connect to PC ending 333333' })
    expect(button).toBeDisabled()
    fireEvent.click(button)
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it.each(['announced heads', 'legacy preference choices'])('does not classify %s as a save conflict', async evidence => {
    const current = group()
    if (evidence === 'announced heads') current.state.needsChoice = true
    else current.selection = { canChoose: false, needsChoice: true, heads: [defaultHead(), defaultHead(otherId, otherSessionId)] }
    vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
    render(<SharedHostNotice {...props} />)
    const notice = await screen.findByRole('article', { name: 'Hosting reports for Weekend world' })
    expect(notice.querySelector('.shared-host-caution')).toBeNull()
    expect(within(notice).queryByText('Group default')).not.toBeInTheDocument()
    expect(within(notice).getByRole('button', { name: 'Connect to PC ending 333333' })).toBeEnabled()
  })

  it.each(['peer', 'session'])('describes conflicting %s metadata while keeping an independent usable report', async conflict => {
    const current = group()
    current.state.sessions.push({ ...structuredClone(current.state.sessions[0]), deviceId: otherId, publicKey: `synthetic-key-${otherId}`,
      session: { ...current.state.sessions[0].session, sessionId: otherSessionId } })
    if (conflict === 'peer') current.state.peers[1].hasConflict = true
    else current.state.sessions[1].hasConflict = true
    vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
    render(<SharedHostNotice {...props} />)
    expect(await screen.findByText(/Some PCs reported different server or connection details/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Connect to PC ending 333333' })).toBeEnabled()
    expect(screen.queryByRole('button', { name: 'Connect to PC ending 444444' })).not.toBeInTheDocument()
    expect(screen.queryByText(/save histories|Group default/)).not.toBeInTheDocument()
  })
})

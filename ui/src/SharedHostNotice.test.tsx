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
const props = { profileId, connectionId: 'saved-host-a' }
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })

function group() {
  const peer = (deviceId: string, route: string) => ({ deviceId, publicKey: `synthetic-key-${deviceId}`, revision: 1,
    routes: [{ address: route, certificateSha256: 'D'.repeat(64) }], hasConflict: false, authorized: true,
    availableVersionHashes: [], announcedUtc: stamp, contact: null })
  const session = (deviceId: string, id: string, branch: string) => ({ deviceId, publicKey: `synthetic-key-${deviceId}`,
    hasConflict: false, authorized: true, session: { sessionId: id, branchHash: branch, revision: 1,
      state: 'Hosting', baseVersionHash: 'E'.repeat(64), headVersionHash: null, startedUtc: stamp, endedUtc: null } })
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
  it('shows a signed hosting report and approved route without changing permissions, defaults, connections or servers', async () => {
    const fetch = vi.fn(async () => reply([group()]))
    const connected = vi.fn()
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} onConnected={connected} />)
    expect(await screen.findByRole('heading', { name: 'Another PC reports hosting' })).toBeInTheDocument()
    expect(screen.getByText('PC ending 333333')).toBeInTheDocument()
    expect(screen.getByText(address)).toBeInTheDocument()
    expect(screen.getByText(/Reachability is checked when you connect/)).toBeInTheDocument()
    expect(screen.getByText(/Then use Join in the game/)).toBeInTheDocument()
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

  it('keeps competing hosts separate and marks only the exact signed default without choosing it', async () => {
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
    expect(screen.getByText(/A newer report does not choose a Host/)).toBeInTheDocument()
    expect(screen.getByText('Saved Host address').closest('li')).toHaveTextContent('PC ending 333333')
    expect(screen.getByText('Group default').closest('li')).toHaveTextContent('PC ending 444444')
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
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(screen.getByRole('status')).toHaveTextContent('Shared Host updates unavailable')
    expect(screen.getByRole('status')).toHaveTextContent('Saved reports cannot confirm who is hosting now')
    expect(screen.queryByRole('button', { name: /^Connect/ })).not.toBeInTheDocument()
    failed = false
    fireEvent.click(screen.getByRole('button', { name: 'Retry Host updates' }))
    await flush()
    expect(screen.getByRole('button', { name: 'Connect to PC ending 333333' })).toBeEnabled()
  })

  it('rejects malformed world replies rather than presenting them as verified hosting', async () => {
    const current = group()
    current.state.root.profileId = otherId
    vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
    render(<SharedHostNotice {...props} />)
    expect(await screen.findByRole('status')).toHaveTextContent('Shared Host updates unavailable')
    expect(screen.queryByRole('button', { name: /^Connect/ })).not.toBeInTheDocument()
  })

  it('polls every five visible seconds and pauses without overlapping a pending read', async () => {
    vi.useFakeTimers()
    const pending = deferred<Response>()
    const fetch = vi.fn().mockResolvedValueOnce(reply([group()])).mockImplementationOnce(() => pending.promise)
      .mockImplementation(async () => reply([group()]))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostNotice {...props} />)
    await flush()
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(fetch).toHaveBeenCalledTimes(2)
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
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

  it('aborts stale reads and cannot restore a withdrawn Host from their late replies', async () => {
    const oldRead = deferred<Response>()
    let signal: AbortSignal | null | undefined
    const fetch = vi.fn().mockImplementationOnce((_url: string, init?: RequestInit) => { signal = init?.signal; return oldRead.promise })
      .mockImplementation(async () => reply([]))
    vi.stubGlobal('fetch', fetch)
    const view = render(<SharedHostNotice {...props} />)
    view.rerender(<SharedHostNotice {...props} connectionId="saved-host-b" />)
    expect(signal?.aborted).toBe(true)
    await act(async () => { oldRead.resolve(reply([group()])) })
    expect(screen.queryByRole('button', { name: /^Connect/ })).not.toBeInTheDocument()
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

  it('distinguishes unresolved defaults from a verified single default', async () => {
    const current = group()
    current.selection = { canChoose: false, needsChoice: true, heads: [defaultHead(), defaultHead(otherId, otherSessionId)] }
    vi.stubGlobal('fetch', vi.fn(async () => reply([current])))
    render(<SharedHostNotice {...props} />)
    const notice = await screen.findByRole('article', { name: 'Hosting reports for Weekend world' })
    expect(within(notice).getByText(/different save histories or default choices/)).toBeInTheDocument()
    expect(within(notice).queryByText('Group default')).not.toBeInTheDocument()
    expect(within(notice).getByRole('button', { name: 'Connect to PC ending 333333' })).toBeEnabled()
  })
})

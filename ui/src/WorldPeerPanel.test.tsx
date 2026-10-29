import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { parseWorldPeerGroups, WorldPeerPanel } from './WorldPeerPanel'

const profileId = '11111111-1111-4111-8111-111111111111'
const selfId = '22222222-2222-4222-8222-222222222222'
const remoteId = '33333333-3333-4333-8333-333333333333'
const otherId = '44444444-4444-4444-8444-444444444444'
const sessionId = '55555555-5555-4555-8555-555555555555'
const groupKey = 'A'.repeat(64)
const branchHash = 'B'.repeat(64)
const secondBranch = 'C'.repeat(64)
const stamp = '2020-01-01T00:00:00Z'
const reply = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
const selectionHead = (deviceId = remoteId, selectedSession = sessionId, selectedBranch = branchHash) => ({
  selectionHash: '9'.repeat(64), deviceId, publicKey: 'synthetic-public-key', sessionId: selectedSession,
  branchHash: selectedBranch, authorized: true, targetKnown: true,
})
function group() {
  const peer = (deviceId: string, copies: string[]) => ({ deviceId, publicKey: 'synthetic-public-key', revision: 1,
    routes: [{ address: 'https://192.0.2.10:5131', certificateSha256: 'D'.repeat(64) }], hasConflict: false,
    authorized: true, availableVersionHashes: copies, announcedUtc: stamp, contact: null })
  const history = (deviceId: string, hash: string) => ({ deviceId, publicKey: 'synthetic-public-key',
    hasConflict: false, authorized: true, branch: { branchHash: hash, baseVersionHash: 'E'.repeat(64),
      headVersionHash: 'F'.repeat(64), parentVersionHash: 'E'.repeat(64), versionNumber: 7, authorityHash: null } })
  const session = (deviceId: string, id: string, hash: string) => ({ deviceId, publicKey: 'synthetic-public-key',
    hasConflict: false, authorized: true, session: { sessionId: id, branchHash: hash, revision: 1,
      state: 'Hosting', baseVersionHash: 'E'.repeat(64), headVersionHash: null, startedUtc: stamp, endedUtc: null } })
  return { groupKey, profileId, name: 'Weekend world', selfDeviceId: selfId, sharingEnabled: false,
    receiveConsented: false, canReceive: true, canReshare: false, canHost: true, warning: null,
    state: { root: { ownerPublicKey: 'synthetic-owner-key', profileId, groupId: profileId }, groupKey,
      peers: [peer(remoteId, ['F'.repeat(64)]), peer(otherId, [])],
      branches: [history(remoteId, branchHash), history(otherId, secondBranch)],
      sessions: [session(remoteId, sessionId, branchHash), session(otherId, profileId, secondBranch)],
      defaultChoices: [], needsChoice: true, knownSessions: 2, announcementCount: 2,
      replication: { knownPeers: 2, peersWithRoutes: 2, peersLastSyncSucceeded: 0, claimedCopyPeers: 1,
        lastSuccessfulSyncUtc: null, hasConflicts: false, capacityReached: false, state: 'No peer sync recorded' } } }
}
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

function transferProgress(patch: Record<string, unknown> = {}) {
  return { state: 'Working', phase: 'Pulling', operation: 'Catalog', httpStep: 'Exchange', result: 'None', error: 'None',
    httpStatus: null, startedUtc: stamp, updatedUtc: stamp, elapsedMilliseconds: 1000,
    submittedBytes: 1024, receivedBytes: 1024, completedFiles: 1, requestedFileIndex: null, requestedOffset: null,
    exactReceiptAccepted: false, totalBytes: 1024, totalFiles: 1, publicationNumber: 6, receiptPublicationNumber: null, ...patch }
}
function transferGroup(current: ReturnType<typeof transferProgress> | null,
  lastResult: ReturnType<typeof transferProgress> | null = null, lastFailure: ReturnType<typeof transferProgress> | null = null) {
  const value = group()
  return { ...value, state: { ...value.state, replication: { ...value.state.replication,
    lastSuccessfulSyncUtc: new Date().toISOString(), claimedCopyPeers: 3 } },
    backgroundTransfers: [{ routeOrdinal: 1, recipientRole: 'OriginalHost', current, lastResult, lastFailure }] }
}

describe('Background save transfer observation', () => {
  it('keeps newly retained local staging separate from wire counters and verified receipt claims', async () => {
    const value = transferGroup(null, transferProgress({ state: 'Pending', phase: 'Preparing', operation: 'None', result: 'WorkBudget',
      httpStatus: null, receivedBytes: 0, submittedBytes: 0, localStagedBytes: 1536, totalBytes: 4096 }))
    const fetcher = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async () => reply([value]))
    vi.stubGlobal('fetch', fetcher)
    render(<WorldPeerPanel readOnly profileId={profileId} />)
    const progress = await screen.findByRole('region', { name: 'Background save transfer for Weekend world' })
    expect(progress).toHaveTextContent('Preparing local save files')
    expect(progress).toHaveTextContent('0 B submitted · 0 B received')
    expect(progress).toHaveTextContent('1.5 KiB newly staged locally for later verification')
    expect(progress).toHaveTextContent('Waiting for another background pass')
    expect(progress).toHaveTextContent('No verified copy receipt is confirmed')
    expect(within(progress).queryByRole('alert')).not.toBeInTheDocument()
    expect(fetcher.mock.calls.every(([, init]) => !init?.method)).toBe(true)
  })

  it('shows delivery status in the main sharing view without opening permission settings', async () => {
    const value = transferGroup(null, transferProgress({ state: 'Pending', phase: 'Sending', operation: 'Seed',
      result: 'ByteBudget', httpStatus: 200, publicationNumber: 7 }))
    vi.stubGlobal('fetch', vi.fn(async () => reply([value])))
    render(<WorldPeerPanel profileId={profileId} />)
    const progress = await screen.findByRole('region', { name: 'Background save transfer for Weekend world' })
    expect(progress).toBeVisible()
    expect(progress).toHaveTextContent('Save 7')
    expect(progress).toHaveTextContent('More bytes remain for another background pass')
    const permissions = screen.getByText('Save sharing & permissions').closest('details')
    expect(permissions).not.toHaveAttribute('open')
    expect(permissions).not.toContainElement(progress)
    expect(progress).not.toHaveTextContent('Verified receipt for save 7 accepted')
  })

  it('keeps a busy or paused exchange pending without inventing a receipt or retrying an action', async () => {
    const calls: { url: string; method?: string }[] = []
    const value = transferGroup(null, transferProgress({ state: 'Pending', phase: 'Offering', operation: 'Seed',
      result: 'SeedPaused', error: 'None', httpStatus: 200, submittedBytes: 0, receivedBytes: 0, completedFiles: 0 }))
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method })
      return reply([value])
    }))
    render(<WorldPeerPanel readOnly profileId={profileId} />)
    const progress = await screen.findByRole('region', { name: 'Background save transfer for Weekend world' })
    expect(progress).toHaveTextContent('Wait for active transfers to finish')
    expect(progress).toHaveTextContent('Waiting for another background pass')
    expect(progress).toHaveTextContent('No verified copy receipt is confirmed')
    expect(within(progress).queryByRole('alert')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh details' }))
    await waitFor(() => expect(calls.length).toBeGreaterThanOrEqual(2))
    expect(calls.every(call => call.url === '/api/local/world-peers' && call.method === undefined)).toBe(true)
  })

  it('keeps fresh metadata, full byte counters and a successful HTTP response separate from a verified receipt', async () => {
    const value = transferGroup(null, transferProgress({ state: 'Succeeded', result: 'PassComplete', httpStatus: 200 }))
    vi.stubGlobal('fetch', vi.fn(async () => reply([value])))
    render(<WorldPeerPanel readOnly profileId={profileId} />)
    const world = await screen.findByRole('article', { name: 'Weekend world' })
    expect(within(world).getByText(/Last metadata contact/)).toBeInTheDocument()
    const progress = within(world).getByRole('region', { name: 'Background save transfer for Weekend world' })
    expect(progress).toHaveTextContent('This pass finished')
    expect(progress).toHaveTextContent(/This pass:.*submitted.*received.*1 file completed/)
    expect(progress).toHaveTextContent('No verified copy receipt is confirmed')
    expect(within(progress).queryByText(/Verified receipt for save .* accepted/)).not.toBeInTheDocument()
    expect(within(progress).queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('retains a payload failure through later metadata contact and read-only status polling', async () => {
    vi.useFakeTimers()
    const failure = transferProgress({ state: 'Failed', phase: 'Pulling', operation: 'Catalog', error: 'HttpRejected', httpStatus: 503 })
    const metadata = transferProgress({ phase: 'Metadata', operation: 'Metadata', submittedBytes: 0, receivedBytes: 0 })
    let value = transferGroup(metadata, transferProgress({ ...metadata, state: 'Succeeded', result: 'NoPayload' }), failure)
    const calls: { url: string; method?: string }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => { calls.push({ url, method: init?.method }); return reply([value]) }))
    render(<WorldPeerPanel readOnly profileId={profileId} />)
    await act(async () => {})
    const progress = within(screen.getByRole('region', { name: 'Background save transfer for Weekend world' }))
    expect(progress.getByRole('alert')).toHaveTextContent('Previous save transfer issue')
    expect(progress.getByRole('alert')).toHaveTextContent('approved PC could not accept the request')
    value = transferGroup({ ...metadata, updatedUtc: '2026-10-27T00:00:00Z' }, transferProgress({ ...metadata, state: 'Succeeded', result: 'NoPayload' }), failure)
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    fireEvent.click(screen.getByRole('button', { name: 'Refresh details' }))
    await act(async () => {})
    expect(progress.getByRole('alert')).toHaveTextContent('Previous save transfer issue')
    expect(progress.getAllByText(/Checking world metadata/).length).toBeGreaterThan(0)
    fireEvent.click(progress.getByText('Transfer diagnostics'))
    expect(progress.getByText('Pulling / Catalog')).toBeVisible()
    expect(progress.getByText('HttpRejected / 503')).toBeVisible()
    expect(calls.length).toBeGreaterThanOrEqual(3)
    expect(calls.every(call => call.url === '/api/local/world-peers' && call.method === undefined)).toBe(true)
    expect(progress.queryByRole('button')).not.toBeInTheDocument()
  })

  it('shows the explicitly accepted historical receipt even during a later failed pass', async () => {
    const observed = transferProgress({ state: 'Failed', result: 'Failed', error: 'HttpRejected', httpStatus: 503,
      publicationNumber: 6, receiptPublicationNumber: 5, exactReceiptAccepted: true })
    vi.stubGlobal('fetch', vi.fn(async () => reply([transferGroup(null, observed, observed)])))
    render(<WorldPeerPanel readOnly />)
    const progress = await screen.findByRole('region', { name: 'Background save transfer for Weekend world' })
    expect(progress).toHaveTextContent('Verified receipt for save 5 accepted.')
    expect(progress).toHaveTextContent('Save 6')
    expect(progress).toHaveTextContent('A receipt applies to that completed save.')
    expect(progress).not.toHaveTextContent('Verified receipt for save 6 accepted.')
  })

  it.each([{ exactReceiptAccepted: false, receiptPublicationNumber: 5 }, { exactReceiptAccepted: true, receiptPublicationNumber: null }])(
    'does not invent a receipt from incomplete evidence %j', async patch => {
      vi.stubGlobal('fetch', vi.fn(async () => reply([transferGroup(null, transferProgress({ state: 'Succeeded', result: 'CompleteReceiptAccepted', ...patch }))])))
      render(<WorldPeerPanel readOnly />)
      const progress = await screen.findByRole('region', { name: 'Background save transfer for Weekend world' })
      expect(progress).toHaveTextContent('No verified copy receipt is confirmed')
      expect(within(progress).queryByText(/Verified receipt for save .* accepted/)).not.toBeInTheDocument()
    })

  it('normalizes unknown telemetry codes without displaying input strings or private errors', async () => {
    const unsafe = 'untrusted private-path token=secret'
    const untrusted = transferProgress(Object.fromEntries(['state', 'phase', 'operation', 'httpStep', 'result', 'error'].map(key => [key, unsafe])))
    const value = transferGroup(untrusted, null, untrusted)
    value.backgroundTransfers[0].recipientRole = unsafe
    vi.stubGlobal('fetch', vi.fn(async () => reply([value])))
    render(<WorldPeerPanel readOnly />)
    const progress = await screen.findByRole('region', { name: 'Background save transfer for Weekend world' })
    fireEvent.click(within(progress).getByText('Transfer diagnostics'))
    expect(progress).toHaveTextContent('Transfer step unavailable')
    expect(progress).not.toHaveTextContent(unsafe)
    expect(progress).not.toHaveTextContent('secret')
    expect(parseWorldPeerGroups([value])[0].backgroundTransfers[0].current?.error).toBe('Unknown')
  })

  it('keeps automatic status reads separate from the explicit save catch-up action', async () => {
    vi.useFakeTimers()
    const calls: { url: string; method?: string; body?: BodyInit | null }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method, body: init?.body })
      return reply(init?.method ? { ok: true, code: 'WorldPeersChecked', message: 'Checked approved PCs.' } : [group()])
    }))
    render(<WorldPeerPanel />)
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    fireEvent.click(screen.getByText('Save sharing & permissions'))
    expect(screen.getByText(/Get world updates checks approved PCs/)).toHaveTextContent('may catch up completed save copies with current permission')
    expect(calls.every(call => call.method === undefined)).toBe(true)
    fireEvent.click(screen.getByRole('button', { name: 'Get world updates' }))
    await act(async () => {})
    expect(calls.filter(call => call.method)).toEqual([{ url: `/api/local/world-peers/${groupKey}/sync`, method: 'POST', body: '{}' }])
  })

  it.each([
    { submittedBytes: -1 }, { receivedBytes: 2 ** 50 + 1 }, { localStagedBytes: -1 }, { localStagedBytes: 2 ** 50 + 1 },
    { completedFiles: 100001 }, { elapsedMilliseconds: 86400001 },
    { requestedFileIndex: 100001 }, { requestedOffset: -1 }, { httpStatus: 99 }, { httpStatus: 600 },
    { publicationNumber: 0 }, { receiptPublicationNumber: Number.MAX_SAFE_INTEGER + 1 }, { totalBytes: '1024' },
    { exactReceiptAccepted: 'true' }, { startedUtc: 'yesterday' }
  ])('refuses malformed transfer evidence %j', patch => {
    expect(() => parseWorldPeerGroups([transferGroup(transferProgress(patch))])).toThrow()
  })

  it('accepts missing telemetry and refuses duplicate or unbounded route ordinals', () => {
    expect(parseWorldPeerGroups([group()])[0].backgroundTransfers).toEqual([])
    const value = transferGroup(null)
    expect(() => parseWorldPeerGroups([{ ...value, backgroundTransfers: [value.backgroundTransfers[0], value.backgroundTransfers[0]] }])).toThrow()
    expect(() => parseWorldPeerGroups([{ ...value, backgroundTransfers: [{ ...value.backgroundTransfers[0], routeOrdinal: 257 }] }])).toThrow()
  })
})

describe('World groups', () => {
  it('provides read-only diagnostics without receiving, forwarding, default or branch settings', async () => {
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) writes.push(url)
      return reply([group()])
    }))
    render(<WorldPeerPanel readOnly profileId={profileId} onRecoverBranch={vi.fn()} onRecoverOwnerBranch={vi.fn()} />)
    const world = await screen.findByRole('article', { name: 'Weekend world' })
    expect(screen.getByRole('region', { name: 'Shared hosting details' })).toHaveAttribute('data-profile-id', profileId)
    expect(screen.getByRole('heading', { name: 'Save sharing status' })).toBeVisible()
    const transfers = within(world).getByRole('region', { name: 'Background save transfer for Weekend world' })
    expect(transfers).toBeVisible()
    const connections = within(world).getByText('Other Hosts & connections').closest('details')
    expect(connections).not.toHaveAttribute('open')
    expect(connections).not.toContainElement(transfers)
    expect(within(world).getByText(/The app checks these copies automatically before hosting/)).toHaveAttribute('role', 'note')
    expect(within(world).queryByRole('checkbox')).not.toBeInTheDocument()
    expect(within(world).queryByRole('button')).not.toBeInTheDocument()
    expect(within(world).queryByText('Save sharing & permissions')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh details' }))
    await act(async () => {})
    expect(writes).toEqual([])
  })

  it('keeps world membership without receiving consent and preserves concurrent session choices', async () => {
    const sent: string[] = []
    const connected = vi.fn()
    const changed = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) sent.push(`${init.method} ${url}`)
      return reply([group()])
    }))
    render(<WorldPeerPanel onConnected={connected} onChanged={changed} />)
    const world = await screen.findByRole('article', { name: 'Weekend world' })
    expect(within(world).getByText('Save sharing & permissions').closest('details')).not.toHaveAttribute('open')
    expect(within(world).getByLabelText('Allow saves on this PC')).not.toBeVisible()
    expect(within(world).getAllByRole('button', { name: 'Connect to this Host' })).toHaveLength(2)
    expect(within(world).getByText(/All save histories are kept/)).toBeInTheDocument()
    expect(within(world).getAllByText(/Game connection and joining still need testing/)).toHaveLength(2)
    fireEvent.click(within(world).getByText('Save sharing & permissions'))
    expect(within(world).getByText('Off — allow saves on this PC first')).toBeVisible()
    expect(within(world).getByRole('checkbox', { name: 'Allow saves on this PC' })).not.toBeChecked()
    fireEvent.click(within(world).getByText('Save histories and reported copies'))
    expect(within(world).getByText(/These reports do not confirm that a transfer finished/)).toBeInTheDocument()
    expect(within(world).getByRole('region', { name: 'History 1' })).toHaveTextContent('reports saved version 7')
    expect(within(world).getByRole('region', { name: 'History 2' })).toHaveTextContent('reports saved version 7')
    expect(within(world).queryByRole('button', { name: 'Recover this history on this PC' })).not.toBeInTheDocument()
    expect(within(world).getByText('Technical details').closest('details')).not.toHaveAttribute('open')
    expect(sent).toEqual([])
    expect(connected).not.toHaveBeenCalled()
    expect(changed).not.toHaveBeenCalled()
  })

  it('changes the selected session only after its explicit Connect action', async () => {
    const current = group()
    const mutations: { path: string; body: unknown }[] = []
    const connected = vi.fn().mockResolvedValue(undefined)
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, body: JSON.parse(String(init.body)) })
        return reply({ ok: true, code: 'WorldSessionConnected', message: 'Selected session connected.' })
      }
      return reply([current])
    }))
    render(<WorldPeerPanel onConnected={connected} />)
    await screen.findByRole('article', { name: 'Weekend world' })
    current.state.sessions.reverse()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh groups' }))
    await act(async () => {})
    expect(connected).not.toHaveBeenCalled()
    expect(mutations).toEqual([])
    const selected = screen.getByText('PC ending 333333').closest('.world-peer-session')!
    fireEvent.click(within(selected as HTMLElement).getByRole('button', { name: 'Connect to this Host' }))
    await waitFor(() => expect(connected).toHaveBeenCalledTimes(1))
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/connect`, body: { deviceId: remoteId, sessionId } }])
  })

  it('allows an explicitly selected Preparing session without claiming that its game is ready', async () => {
    const current = group()
    current.state.sessions[0].session.state = 'Preparing'
    const mutations: { path: string; method: string; body: unknown }[] = []
    const connected = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, method: init.method, body: JSON.parse(String(init.body)) })
        return reply({ ok: true, code: 'WorldSessionConnected', message: 'Selected preparing session connected.' })
      }
      return reply([current])
    }))
    render(<WorldPeerPanel onConnected={connected} />)
    await screen.findByRole('article', { name: 'Weekend world' })
    fireEvent.click(screen.getByRole('button', { name: 'Refresh groups' }))
    await act(async () => {})
    const preparing = screen.getByText('PC ending 333333').closest('.world-peer-session') as HTMLElement
    expect(within(preparing).getByText(/^Preparing to host/)).toBeInTheDocument()
    expect(within(preparing).getByText(/Game connection and joining still need testing/)).toBeInTheDocument()
    expect(mutations).toEqual([])
    expect(connected).not.toHaveBeenCalled()
    fireEvent.click(within(preparing).getByRole('button', { name: 'Connect to this Host' }))
    await waitFor(() => expect(connected).toHaveBeenCalledTimes(1))
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/connect`, method: 'POST',
      body: { deviceId: remoteId, sessionId } }])
    expect(within(preparing).getByText(/Game connection and joining still need testing/)).toBeInTheDocument()
  })

  it.each(['access removed', 'different server details'] as const)(
    'withdraws Preparing connection actions when current world information reports %s', async reason => {
      const current = group()
      current.state.sessions[0].session.state = 'Preparing'
      const connected = vi.fn()
      const mutations: string[] = []
      vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
        if (init?.method) mutations.push(url)
        return reply([current])
      }))
      render(<WorldPeerPanel onConnected={connected} />)
      await screen.findByRole('article', { name: 'Weekend world' })
      let preparing = screen.getByText('PC ending 333333').closest('.world-peer-session') as HTMLElement
      expect(within(preparing).getByRole('button', { name: 'Connect to this Host' })).toBeInTheDocument()
      if (reason === 'access removed') current.state.peers[0].authorized = false
      else current.state.sessions[0].hasConflict = true
      fireEvent.click(screen.getByRole('button', { name: 'Refresh groups' }))
      await waitFor(() => {
        preparing = screen.getByText('PC ending 333333').closest('.world-peer-session') as HTMLElement
        expect(within(preparing).queryByRole('button', { name: 'Connect to this Host' })).not.toBeInTheDocument()
      })
      expect(within(preparing).getByText(/^Preparing to host/)).toBeInTheDocument()
      expect(mutations).toEqual([])
      expect(connected).not.toHaveBeenCalled()
    })

  it('requests peer contact sharing without granting save receiving or forwarding', async () => {
    const current = group()
    const mutations: { path: string; method: string; body: unknown }[] = []
    const changed = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        const body = JSON.parse(String(init.body)) as { enabled?: boolean }
        mutations.push({ path: url, method: init.method, body })
        if (url.endsWith('/sharing')) current.sharingEnabled = body.enabled === true
        return reply({ ok: true, code: 'Saved', message: 'Peer contact sharing saved.' })
      }
      return reply([current])
    }))
    render(<WorldPeerPanel onChanged={changed} />)
    fireEvent.click(await screen.findByText('Save sharing & permissions'))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Allow approved world PCs to contact this PC' }))
    await waitFor(() => expect(changed).toHaveBeenCalledTimes(1))
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/sharing`, method: 'PUT', body: { enabled: true } }])
    expect(screen.getByText('Off — allow saves on this PC first')).toBeInTheDocument()
    expect(screen.getByText('Owner permission required')).toBeInTheDocument()
  })

  it('keeps a metadata follower visible without allowing save consent before Receive permission is granted', async () => {
    const current = { ...group(), canReceive: false, canHost: false }
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(`${init.method} ${url}`)
      return reply([current])
    }))
    render(<WorldPeerPanel />)
    const world = await screen.findByRole('article', { name: 'Weekend world' })
    fireEvent.click(within(world).getByText('Save sharing & permissions'))
    const receiving = within(world).getByRole('checkbox', { name: 'Allow saves on this PC' })
    expect(receiving).not.toBeChecked()
    expect(receiving).toBeDisabled()
    expect(within(world).getByRole('checkbox', { name: 'Allow approved world PCs to contact this PC' })).not.toBeChecked()
    expect(within(world).getAllByRole('button', { name: 'Connect to this Host' })).toHaveLength(2)
    expect(mutations).toEqual([])
  })

  it('enables group save consent explicitly without enabling incoming peer contact', async () => {
    const current = group()
    const mutations: { path: string; method: string; body: unknown }[] = []
    const changed = vi.fn()
    const connected = vi.fn()
    let complete!: (value: Response) => void
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, method: init.method, body: JSON.parse(String(init.body)) })
        return new Promise<Response>(resolve => { complete = resolve })
      }
      return reply([current])
    }))
    render(<WorldPeerPanel onChanged={changed} onConnected={connected} />)
    const world = await screen.findByRole('article', { name: 'Weekend world' })
    fireEvent.click(within(world).getByText('Save sharing & permissions'))
    const receiving = within(world).getByRole('checkbox', { name: 'Allow saves on this PC' })
    const sharing = within(world).getByRole('checkbox', { name: 'Allow approved world PCs to contact this PC' })
    expect(receiving).toBeEnabled()
    expect(receiving).not.toBeChecked()
    expect(mutations).toEqual([])
    fireEvent.click(receiving)
    expect(receiving).toBeDisabled()
    expect(sharing).toBeDisabled()
    expect(sharing).not.toBeChecked()
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/receiving`, method: 'PUT', body: { enabled: true } }])
    current.receiveConsented = true
    await act(async () => { complete(reply({ ok: true, code: 'WorldReceivingEnabled', message: 'Save consent enabled for this world.' })) })
    await waitFor(() => expect(receiving).toBeChecked())
    expect(receiving).toBeEnabled()
    expect(sharing).not.toBeChecked()
    expect(within(world).getByText('Allowed on this PC')).toBeInTheDocument()
    expect(within(world).getByText('Save consent enabled for this world.')).toBeInTheDocument()
    expect(changed).toHaveBeenCalledTimes(1)
    expect(connected).not.toHaveBeenCalled()
    expect(mutations).toHaveLength(1)
  })

  it('allows withdrawing existing world consent after the Receive grant has been removed', async () => {
    const current = { ...group(), receiveConsented: true, canReceive: false }
    const mutations: { path: string; method: string; body: unknown }[] = []
    const changed = vi.fn()
    const connected = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, method: init.method, body: JSON.parse(String(init.body)) })
        current.receiveConsented = false
        return reply({ ok: true, code: 'WorldReceivingDisabled', message: 'Save consent withdrawn for this world.' })
      }
      return reply([current])
    }))
    render(<WorldPeerPanel onChanged={changed} onConnected={connected} />)
    const world = await screen.findByRole('article', { name: 'Weekend world' })
    fireEvent.click(within(world).getByText('Save sharing & permissions'))
    const receiving = within(world).getByRole('checkbox', { name: 'Allow saves on this PC' })
    expect(receiving).toBeChecked()
    expect(receiving).toBeEnabled()
    fireEvent.click(receiving)
    await waitFor(() => expect(receiving).not.toBeChecked())
    expect(receiving).toBeDisabled()
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/receiving`, method: 'PUT', body: { enabled: false } }])
    expect(within(world).getByRole('checkbox', { name: 'Allow approved world PCs to contact this PC' })).not.toBeChecked()
    expect(within(world).getByText('Save consent withdrawn for this world.')).toBeInTheDocument()
    expect(changed).toHaveBeenCalledTimes(1)
    expect(connected).not.toHaveBeenCalled()
  })

  it('imports only an explicitly pasted approved contact and preserves the group after rejection', async () => {
    const requests: { url: string; body?: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/contact-code')) { requests.push({ url }); return reply({ code: 'signed contact only' }) }
      if (init?.method) {
        requests.push({ url, body: JSON.parse(String(init.body)) })
        return reply({ ok: false, code: 'WorldMembershipRequired', message: 'This code names an unknown world.' }, 403)
      }
      return reply([group()])
    }))
    render(<WorldPeerPanel />)
    const world = await screen.findByRole('article', { name: 'Weekend world' })
    expect(requests).toEqual([])
    fireEvent.click(within(world).getByText('Contact another group PC'))
    fireEvent.click(within(world).getByRole('button', { name: "Show this PC's contact code" }))
    expect(await screen.findByLabelText('Contact code for Weekend world')).toHaveValue('signed contact only')
    fireEvent.change(within(world).getByLabelText('Contact code from another PC'), { target: { value: 'unapproved-world-code' } })
    fireEvent.click(within(world).getByText('Contact another group PC'))
    fireEvent.click(within(world).getByText('Save sharing & permissions'))
    expect(within(world).getByRole('checkbox', { name: 'Allow saves on this PC' })).not.toBeChecked()
    fireEvent.click(within(world).getByText('Save sharing & permissions'))
    fireEvent.click(within(world).getByText('Contact another group PC'))
    expect(within(world).getByLabelText('Contact code from another PC')).toHaveValue('unapproved-world-code')
    expect(requests).toEqual([{ url: `/api/local/world-peers/${groupKey}/contact-code` }])
    fireEvent.click(within(world).getByRole('button', { name: 'Add approved contact' }))
    expect(await screen.findByText(/This code names an unknown world/)).toBeInTheDocument()
    expect(requests).toEqual([{ url: `/api/local/world-peers/${groupKey}/contact-code` },
      { url: `/api/local/world-peers/${groupKey}/contact`, body: { code: 'unapproved-world-code' } }])
    expect(screen.getByRole('article', { name: 'Weekend world' })).toBeInTheDocument()
  })

  it('shows cached ages after refresh failure without disconnecting or dropping histories', async () => {
    let unavailable = false
    const connected = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async () => {
      if (unavailable) throw new Error('Local app temporarily unavailable')
      return reply([group()])
    }))
    render(<WorldPeerPanel onConnected={connected} />)
    await screen.findByRole('article', { name: 'Weekend world' })
    unavailable = true
    fireEvent.click(screen.getByRole('button', { name: 'Refresh groups' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Showing saved information checked')
    expect(screen.getByRole('article', { name: 'Weekend world' })).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Connect to this Host' })).toHaveLength(2)
    expect(connected).not.toHaveBeenCalled()
  })

  it('shares one pending information read and aborts it when the panel is hidden', async () => {
    let complete!: (value: Response) => void
    let signal: AbortSignal | null | undefined
    const fetch = vi.fn((_url: string, init?: RequestInit) => {
      signal = init?.signal
      return new Promise<Response>(resolve => { complete = resolve })
    })
    vi.stubGlobal('fetch', fetch)
    const view = render(<WorldPeerPanel />)
    fireEvent.click(screen.getByRole('button', { name: 'Refresh groups' }))
    fireEvent.click(screen.getByRole('button', { name: 'Refresh groups' }))
    expect(fetch).toHaveBeenCalledTimes(1)
    view.rerender(<WorldPeerPanel visible={false} />)
    expect(signal?.aborted).toBe(true)
    await act(async () => { complete(reply([group()])) })
    expect(screen.queryByRole('region', { name: 'World groups' })).not.toBeInTheDocument()
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('rejects data for another world and malformed permissions before offering session actions', () => {
    expect(parseWorldPeerGroups([group()])[0].selection).toBeNull()
    const wrongWorld = group()
    wrongWorld.state.root.profileId = remoteId
    expect(() => parseWorldPeerGroups([wrongWorld])).toThrow('another world')
    const duplicate = group()
    expect(() => parseWorldPeerGroups([duplicate, duplicate])).toThrow('repeated')
    expect(() => parseWorldPeerGroups([{ ...group(), canReceive: 'true' }])).toThrow()
    expect(() => parseWorldPeerGroups([{ ...group(), state: { ...group().state, sessions: [{ arbitrary: 'command' }] } }])).toThrow()
    expect(() => parseWorldPeerGroups([{ ...group(), selection: { canChoose: 'true', needsChoice: false, heads: [] } }])).toThrow()
    expect(() => parseWorldPeerGroups([{ ...group(), selection: { canChoose: true, needsChoice: false,
      heads: [{ ...selectionHead(), targetKnown: 'true' }] } }])).toThrow()
  })

  it('publishes a group default only after confirmation without changing the current connection', async () => {
    const current = { ...group(), selection: { canChoose: true, needsChoice: false, heads: [] as ReturnType<typeof selectionHead>[] } }
    const mutations: { path: string; method: string; body: unknown }[] = []
    const connected = vi.fn()
    const changed = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, method: init.method, body: JSON.parse(String(init.body)) })
        current.selection.heads = [selectionHead()]
        return reply({ ok: true, code: 'WorldDefaultSelected', message: 'Group default choice saved.' })
      }
      return reply([current])
    }))
    render(<WorldPeerPanel onConnected={connected} onChanged={changed} />)
    await screen.findByRole('article', { name: 'Weekend world' })
    const remoteSession = screen.getByText('PC ending 333333').closest('.world-peer-session') as HTMLElement
    fireEvent.click(within(remoteSession).getByRole('button', { name: 'Use as group default' }))
    const review = screen.getByRole('region', { name: 'Review group default' })
    expect(review).toHaveTextContent('All save histories and running servers are kept. It does not change your game connection')
    expect(mutations).toEqual([])
    expect(connected).not.toHaveBeenCalled()
    fireEvent.click(within(review).getByRole('button', { name: 'Confirm group default' }))
    expect(await screen.findByText('Group default')).toBeInTheDocument()
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/default`, method: 'POST',
      body: { deviceId: remoteId, sessionId } }])
    expect(changed).toHaveBeenCalledTimes(1)
    expect(connected).not.toHaveBeenCalled()
    expect(screen.queryByRole('region', { name: 'Review group default' })).not.toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Connect to this Host' })).toHaveLength(2)
  })

  it('keeps conflicting and pending default choices visible and invalidates a changed confirmation target', async () => {
    const current = { ...group(), selection: { canChoose: true, needsChoice: true,
      heads: [selectionHead(), { ...selectionHead(otherId, profileId, secondBranch), selectionHash: '8'.repeat(64), targetKnown: false }] } }
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      return reply([current])
    }))
    render(<WorldPeerPanel />)
    await screen.findByRole('article', { name: 'Weekend world' })
    expect(screen.getByText('The group has different default choices.')).toBeInTheDocument()
    expect(screen.getByText('Default choice needs review or more server details.')).toBeInTheDocument()
    expect(screen.queryByText('Group default')).not.toBeInTheDocument()
    const remoteSession = screen.getByText('PC ending 333333').closest('.world-peer-session') as HTMLElement
    fireEvent.click(within(remoteSession).getByRole('button', { name: 'Use as group default' }))
    expect(screen.getByRole('button', { name: 'Confirm group default' })).toBeEnabled()
    current.state.sessions[0].session.state = 'Stopped'
    current.selection.canChoose = false
    fireEvent.click(screen.getByRole('button', { name: 'Refresh groups' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Confirm group default' })).toBeDisabled())
    expect(screen.getByRole('alert')).toHaveTextContent('This server or your permission changed')
    expect(screen.getByText('The group has different default choices.')).toBeInTheDocument()
    expect(mutations).toEqual([])
  })

  it('opens recovery only for the history the person explicitly selects', async () => {
    const current = group()
    current.receiveConsented = true
    const recover = vi.fn()
    const connected = vi.fn()
    const mutations: { path: string; body: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, body: JSON.parse(String(init.body)) })
        return reply({ ok: true, code: 'WorldRecoverySourceSelected', message: 'Recovery source ready.' })
      }
      return reply([current])
    }))
    render(<WorldPeerPanel onRecoverBranch={recover} onConnected={connected} />)
    await screen.findByRole('article', { name: 'Weekend world' })
    expect(recover).not.toHaveBeenCalled()
    fireEvent.click(screen.getByText('Save histories and reported copies'))
    const history = screen.getByRole('region', { name: 'History 2' })
    fireEvent.click(within(history).getByRole('button', { name: 'Recover this history on this PC' }))
    await waitFor(() => expect(recover).toHaveBeenCalledTimes(1))
    expect(recover).toHaveBeenCalledWith(profileId, secondBranch)
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/recovery-source`, body: {} }])
    expect(connected).not.toHaveBeenCalled()
  })

  it('keeps the current connection when no approved recovery source is available', async () => {
    const current = { ...group(), receiveConsented: true }
    const recover = vi.fn()
    const connected = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => init?.method ?
      reply({ ok: false, code: 'WorldRecoverySourceUnavailable', message: 'No approved source link is available.' }) : reply([current])))
    render(<WorldPeerPanel onRecoverBranch={recover} onConnected={connected} />)
    await screen.findByRole('article', { name: 'Weekend world' })
    fireEvent.click(screen.getByText('Save histories and reported copies'))
    const history = screen.getByRole('region', { name: 'History 1' })
    fireEvent.click(within(history).getByRole('button', { name: 'Recover this history on this PC' }))
    expect(await screen.findByText('No approved source link is available.')).toBeInTheDocument()
    expect(recover).not.toHaveBeenCalled()
    expect(connected).not.toHaveBeenCalled()
  })
})

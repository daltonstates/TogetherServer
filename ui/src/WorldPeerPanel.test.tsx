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

describe('World groups', () => {
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
    expect(within(world).getByText('Off on this PC — local consent required')).toBeInTheDocument()
    expect(within(world).getAllByRole('button', { name: 'Connect to this session' })).toHaveLength(2)
    expect(within(world).getByText(/Competing histories are preserved/)).toBeInTheDocument()
    expect(within(world).getAllByText(/Game connection and real join unverified/)).toHaveLength(2)
    fireEvent.click(within(world).getByText('Save histories and copy claims'))
    expect(within(world).getByText(/Signed claims are not completed transfers or confirmed receipts/)).toBeInTheDocument()
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
    fireEvent.click(within(selected as HTMLElement).getByRole('button', { name: 'Connect to this session' }))
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
    expect(within(preparing).getByText(/Game connection and real join unverified/)).toBeInTheDocument()
    expect(mutations).toEqual([])
    expect(connected).not.toHaveBeenCalled()
    fireEvent.click(within(preparing).getByRole('button', { name: 'Connect to this session' }))
    await waitFor(() => expect(connected).toHaveBeenCalledTimes(1))
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/connect`, method: 'POST',
      body: { deviceId: remoteId, sessionId } }])
    expect(within(preparing).getByText(/Game connection and real join unverified/)).toBeInTheDocument()
  })

  it.each(['access removed', 'conflicting session'] as const)(
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
      expect(within(preparing).getByRole('button', { name: 'Connect to this session' })).toBeInTheDocument()
      if (reason === 'access removed') current.state.peers[0].authorized = false
      else current.state.sessions[0].hasConflict = true
      fireEvent.click(screen.getByRole('button', { name: 'Refresh groups' }))
      await waitFor(() => {
        preparing = screen.getByText('PC ending 333333').closest('.world-peer-session') as HTMLElement
        expect(within(preparing).queryByRole('button', { name: 'Connect to this session' })).not.toBeInTheDocument()
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
    fireEvent.click(await screen.findByLabelText('Allow world peers to contact this PC'))
    await waitFor(() => expect(changed).toHaveBeenCalledTimes(1))
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/sharing`, method: 'PUT', body: { enabled: true } }])
    expect(screen.getByText('Off on this PC — local consent required')).toBeInTheDocument()
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
    const receiving = within(world).getByRole('checkbox', { name: 'Allow saves on this PC' })
    expect(receiving).not.toBeChecked()
    expect(receiving).toBeDisabled()
    expect(within(world).getByRole('checkbox', { name: 'Allow world peers to contact this PC' })).not.toBeChecked()
    expect(within(world).getAllByRole('button', { name: 'Connect to this session' })).toHaveLength(2)
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
    const receiving = within(world).getByRole('checkbox', { name: 'Allow saves on this PC' })
    const sharing = within(world).getByRole('checkbox', { name: 'Allow world peers to contact this PC' })
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
    const receiving = within(world).getByRole('checkbox', { name: 'Allow saves on this PC' })
    expect(receiving).toBeChecked()
    expect(receiving).toBeEnabled()
    fireEvent.click(receiving)
    await waitFor(() => expect(receiving).not.toBeChecked())
    expect(receiving).toBeDisabled()
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/receiving`, method: 'PUT', body: { enabled: false } }])
    expect(within(world).getByRole('checkbox', { name: 'Allow world peers to contact this PC' })).not.toBeChecked()
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
    expect(await screen.findByRole('alert')).toHaveTextContent('Showing cached information checked')
    expect(screen.getByRole('article', { name: 'Weekend world' })).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Connect to this session' })).toHaveLength(2)
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
    expect(review).toHaveTextContent('All histories and running sessions stay preserved. Your current game connection stays selected')
    expect(mutations).toEqual([])
    expect(connected).not.toHaveBeenCalled()
    fireEvent.click(within(review).getByRole('button', { name: 'Confirm group default' }))
    expect(await screen.findByText('Group default')).toBeInTheDocument()
    expect(mutations).toEqual([{ path: `/api/local/world-peers/${groupKey}/default`, method: 'POST',
      body: { deviceId: remoteId, sessionId } }])
    expect(changed).toHaveBeenCalledTimes(1)
    expect(connected).not.toHaveBeenCalled()
    expect(screen.queryByRole('region', { name: 'Review group default' })).not.toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Connect to this session' })).toHaveLength(2)
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
    expect(screen.getByText('Conflicting group default choices.')).toBeInTheDocument()
    expect(screen.getByText('Default choice awaiting review or session details.')).toBeInTheDocument()
    expect(screen.queryByText('Group default')).not.toBeInTheDocument()
    const remoteSession = screen.getByText('PC ending 333333').closest('.world-peer-session') as HTMLElement
    fireEvent.click(within(remoteSession).getByRole('button', { name: 'Use as group default' }))
    expect(screen.getByRole('button', { name: 'Confirm group default' })).toBeEnabled()
    current.state.sessions[0].session.state = 'Stopped'
    current.selection.canChoose = false
    fireEvent.click(screen.getByRole('button', { name: 'Refresh groups' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Confirm group default' })).toBeDisabled())
    expect(screen.getByRole('alert')).toHaveTextContent('This session or your permission changed')
    expect(screen.getByText('Conflicting group default choices.')).toBeInTheDocument()
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
    fireEvent.click(screen.getByText('Save histories and copy claims'))
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
    fireEvent.click(screen.getByText('Save histories and copy claims'))
    const history = screen.getByRole('region', { name: 'History 1' })
    fireEvent.click(within(history).getByRole('button', { name: 'Recover this history on this PC' }))
    expect(await screen.findByText('No approved source link is available.')).toBeInTheDocument()
    expect(recover).not.toHaveBeenCalled()
    expect(connected).not.toHaveBeenCalled()
  })
})

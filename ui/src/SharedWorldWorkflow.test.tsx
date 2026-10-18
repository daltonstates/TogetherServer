import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FriendSharedWorlds, HostSharedSaves } from './SharedWorldControls'
import { SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'
import { sharedHostObservation, type SharedSignedHost } from './sharedWorldUx'

const profileId = '11111111-1111-4111-8111-111111111111'
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
const copy = { consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null }
const restore = { staged: false, restored: false, recordHash: null, message: 'No signed decision staged.',
  pendingChecks: [], preparedServerRoot: null, readyForManualStart: false, requiredAddOns: [],
  controlRouteFingerprint: null }
const recovery = { state: 'NoOffer', votes: 0, required: 0, version: null, versionHash: null,
  candidateAddress: null, candidateDeviceId: null, majorityReached: false, separateCopies: 0,
  proposalHash: null, authorityHeadHash: null }
afterEach(() => vi.useRealTimers())

describe('Shared saves guided workflow', () => {
  it('opens Play only for a fresh connected Ready server without asserting signed authority', async () => {
    const navigate = vi.fn()
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      return reply(url.endsWith('/recovery') ? recovery : copy)
    }))
    const props = { profileId, available: true, onOpenPlay: navigate, connectedHostName: 'Saved connection name' }
    const view = render(<FriendSharedWorlds {...props} serverState="Ready" serverStatusFresh />)
    await act(async () => {})
    expect(screen.getByText('Connected Host: Saved connection name')).toBeInTheDocument()
    expect(screen.getByText('Signed Host not checked')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Join connected Host' }))
    expect(navigate).toHaveBeenCalledTimes(1)
    view.rerender(<FriendSharedWorlds {...props} serverState="Ready" serverStatusFresh={false} />)
    expect(screen.queryByRole('button', { name: 'Join connected Host' })).not.toBeInTheDocument()
    view.rerender(<FriendSharedWorlds {...props} serverState="Starting" serverStatusFresh />)
    expect(screen.queryByRole('button', { name: 'Join connected Host' })).not.toBeInTheDocument()
    expect(mutations).toEqual([])
  })

  it('routes a different signed successor to pairing rather than changing the existing TLS address', async () => {
    const pair = vi.fn()
    const changeAddress = vi.fn()
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      return reply(url.endsWith('/recovery') ? { ...recovery, state: 'ObservedMajority', majorityReached: true,
        candidateAddress: 'https://192.0.2.20:5131', candidateDeviceId: profileId,
        authorityHeadHash: 'A'.repeat(64) } : copy)
    }))
    render(<FriendSharedWorlds profileId={profileId} available onPairHost={pair} onAddressChange={changeAddress} onOpenPlay={vi.fn()}
      serverState="Ready" serverStatusFresh observedHostAddress="https://192.0.2.10:5131" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Pair with the new Host' }))
    expect(screen.queryByRole('button', { name: 'Join connected Host' })).not.toBeInTheDocument()
    expect(pair).toHaveBeenCalledTimes(1)
    expect(changeAddress).not.toHaveBeenCalled()
    expect(mutations).toEqual([])
  })

  it('promotes manual Start review on the landing only while exact preparation remains fresh', async () => {
    vi.useFakeTimers()
    const navigate = vi.fn()
    const mutations: string[] = []
    let release!: (value: Response) => void
    let reads = 0
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/recovery')) return reply({ ...recovery, authorityHeadHash: 'A'.repeat(64) })
      if (url.endsWith('/handoff/restore')) {
        if (++reads > 2) return new Promise<Response>(resolve => { release = resolve })
        return reply({ ...restore, staged: true, restored: true, recordHash: 'A'.repeat(64), readyForManualStart: true })
      }
      return reply(copy)
    }))
    render(<FriendSharedWorlds profileId={profileId} available onOpenHostControls={navigate} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Host this save' }))
    fireEvent.click(screen.getByText("Transfer the group's default hosting"))
    await act(async () => {})
    const next = screen.getByRole('region', { name: 'Next shared-save action' })
    const review = within(next).getByRole('button', { name: 'Review Start server' })
    fireEvent.click(review)
    expect(navigate).toHaveBeenCalledTimes(1)
    expect(within(next).getByRole('button', { name: 'Host this save' })).toHaveClass('secondary')
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(within(next).queryByRole('button', { name: 'Review Start server' })).not.toBeInTheDocument()
    await act(async () => { release(reply({ ...restore, staged: true, restored: true, recordHash: 'A'.repeat(64), readyForManualStart: true })) })
    expect(within(next).getByRole('button', { name: 'Review Start server' })).toBeInTheDocument()
    expect(mutations).toEqual([])
  })

  it('keeps an unsaved hosting draft mounted during transfer polling, failure and metadata rechecks', async () => {
    vi.useFakeTimers()
    let receiving = false
    let fail = false
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/recovery')) return reply(recovery)
      if (url.endsWith('/handoff/restore')) return reply({ ...restore, staged: true, recordHash: 'A'.repeat(64) })
      if (fail) throw new Error('offline')
      return reply({ ...copy, hostVersion: receiving ? 4 : 3, state: receiving ? 'Receiving' : 'Ready',
        receivedBytes: receiving ? 32 : 0, totalBytes: receiving ? 64 : 0 })
    }))
    render(<FriendSharedWorlds profileId={profileId} available game="Valheim" />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Host this save' }))
    fireEvent.click(screen.getByText("Transfer the group's default hosting"))
    await act(async () => {})
    const values = [
      ['Valheim dedicated server', 'C:\\draft\\valheim_server.exe'],
      ['Installed game version', 'draft-version'], ['Future Friend control port', '5141'],
      ['Future game port', '2468'], ['New server name', 'Draft server'],
      ['New game password, if this game uses one', 'unsaved-password']
    ]
    for (const [label, value] of values) fireEvent.change(screen.getByLabelText(label), { target: { value } })
    fireEvent.click(screen.getByLabelText('I have set a new game password locally'))
    receiving = true
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(screen.getByRole('region', { name: 'Save transfer progress' })).toBeInTheDocument()
    for (const [label, value] of values) expect(screen.getByLabelText(label)).toHaveValue(value)
    fail = true
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    for (const [label, value] of values) expect(screen.getByLabelText(label)).toHaveValue(value)
    expect(screen.getByLabelText('I have set a new game password locally')).toBeChecked()
    expect(mutations).toEqual([])
  })

  it('downgrades the open guide when the parent head changes and ignores a deferred old restore read', async () => {
    vi.useFakeTimers()
    let head = 'A'.repeat(64)
    let restoreReads = 0
    let pendingOld!: (value: Response) => void
    let pendingNew!: (value: Response) => void
    const mutations: string[] = []
    const readyRestore = () => ({ ...restore, staged: true, restored: true, recordHash: head, readyForManualStart: true })
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/recovery')) return reply({ ...recovery, authorityHeadHash: head })
      if (url.endsWith('/handoff/restore')) {
        if (++restoreReads <= 2) return reply(readyRestore())
        return new Promise<Response>(resolve => { if (restoreReads === 3) pendingOld = resolve; else pendingNew = resolve })
      }
      return reply(copy)
    }))
    render(<FriendSharedWorlds profileId={profileId} available onOpenHostControls={vi.fn()} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Host this save' }))
    fireEvent.click(screen.getByText("Transfer the group's default hosting"))
    await act(async () => {})
    expect(screen.getByRole('button', { name: 'Review Start in Host' })).toBeEnabled()
    expect(screen.getByText('Start server', { selector: 'li' })).toHaveAttribute('aria-current', 'step')
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    head = 'B'.repeat(64)
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(screen.queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
    expect(screen.getByText('Start server', { selector: 'li' })).not.toHaveAttribute('aria-current')
    await act(async () => { pendingOld(reply({ ...readyRestore(), recordHash: 'A'.repeat(64) })) })
    expect(screen.queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
    await act(async () => { pendingNew(reply(readyRestore())) })
    expect(screen.getByRole('button', { name: 'Review Start in Host' })).toBeEnabled()
    expect(screen.getByText('Start server', { selector: 'li' })).toHaveAttribute('aria-current', 'step')
    expect(mutations).toEqual([])
  })

  it.each(['Receiving', 'Verifying', 'Receipt'] as const)('marks failed %s observation Unknown while retaining verified copy, consent and guarded recovery', async phase => {
    vi.useFakeTimers()
    let fail = false
    let completed = false
    const mutations: string[] = []
    const receivedUtc = '2026-10-08T10:05:00Z'
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/recovery')) return reply({ ...recovery, state: 'OfferArmed', votes: 1, required: 2,
        version: 3, versionHash: 'E'.repeat(64), candidateAddress: 'https://192.0.2.20:5131',
        candidateDeviceId: '22222222-2222-4222-8222-222222222222', proposalHash: 'D'.repeat(64) })
      if (url.endsWith('/handoff/restore')) return reply(restore)
      if (fail) throw new Error('offline')
      return reply({ ...copy, hostVersion: completed ? 3 : 4, state: completed ? 'Up to date when last checked' : phase,
        transferPhase: completed ? null : phase, receivedBytes: completed ? 0 : 32, totalBytes: completed ? 0 : 64,
        receivedUtc, receiptConfirmed: true })
    }))
    render(<FriendSharedWorlds profileId={profileId} available />)
    await act(async () => {})
    expect(screen.getByRole('region', { name: 'Save transfer progress' })).toBeInTheDocument()
    fail = true
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(screen.getByText(/Current save status: Unknown/)).toBeInTheDocument()
    expect(screen.getByText(/Save status refresh failed/)).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Save transfer progress' })).not.toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(screen.queryByText('Up to date when last checked')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Allow saves on this PC')).toBeChecked()
    expect(screen.getByText(/Verified save copy 3 on this PC/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Manage sharing' }))
    await act(async () => {})
    expect(screen.getByLabelText('Shared save age').querySelector(`time[datetime="${receivedUtc}"]`)).not.toBeNull()
    expect(screen.getByText('Acknowledged when last checked')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Recovery' }))
    fireEvent.click(screen.getByText('Choose the default Host after confirmed loss'))
    expect(screen.getByRole('button', { name: 'Prepare signed offer' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Prepare solo recovery' })).toBeEnabled()
    expect(screen.queryByRole('button', { name: 'Record recovery branch' })).not.toBeInTheDocument()
    expect(screen.getByText(/Last checked Host version: unknown \(stale observation\)/)).toBeInTheDocument()
    fail = false
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(screen.getByRole('region', { name: 'Save transfer progress' })).toBeInTheDocument()
    expect(screen.queryByText(/Save status refresh failed/)).not.toBeInTheDocument()
    completed = true
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(screen.getByText('Up to date when last checked')).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Save transfer progress' })).not.toBeInTheDocument()
    expect(mutations).toEqual([])
  })

  it('expires a stalled status read without losing the verified copy and recovers on a valid response', async () => {
    vi.useFakeTimers()
    let reads = 0
    let release!: (value: Response) => void
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/recovery')) return reply(recovery)
      if (url.endsWith('/handoff/restore')) return reply(restore)
      if (++reads === 1) return reply({ ...copy, state: 'Receiving', receivedBytes: 32, totalBytes: 64 })
      return new Promise<Response>(resolve => { release = resolve })
    }))
    render(<FriendSharedWorlds profileId={profileId} available />)
    await act(async () => {})
    await act(async () => { await vi.advanceTimersByTimeAsync(6000) })
    expect(reads).toBe(2)
    expect(screen.getByText(/Save status observation is stale/)).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(screen.getByText(/Verified save copy 3 on this PC/)).toBeInTheDocument()
    await act(async () => { release(reply(copy)) })
    expect(screen.queryByText(/Current save status: Unknown/)).not.toBeInTheDocument()
    expect(screen.getByText('Verified copy on this PC')).toBeInTheDocument()
  })

  it('uses the exact same current signed head to identify a local planned successor without promoting its copy to Ready', async () => {
    const route = 'https://192.0.2.20:5131'
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/recovery')) return reply({ ...recovery, authorityHeadHash: 'A'.repeat(64) })
      if (url.endsWith('/handoff/restore')) return reply({ ...restore, staged: true, recordHash: 'A'.repeat(64),
        controlRouteFingerprint: 'B'.repeat(64), controlRouteAddress: route })
      return reply(copy)
    }))
    render(<FriendSharedWorlds profileId={profileId} available serverState="Ready" serverStatusFresh observedHostAddress={route} />)
    expect(await screen.findByText('This PC · signed successor preparing hosting')).toBeInTheDocument()
    expect(screen.getByText('Current Host server: Unknown')).toBeInTheDocument()
    expect(screen.queryByText(/Connected server: Ready observed/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
  })
  it('keeps verified copy possession separate from the current signed Host and game observation', async () => {
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/handoff/restore')) return reply(restore)
      if (url.endsWith('/recovery')) return reply(recovery)
      return reply(copy)
    }))
    render(<FriendSharedWorlds profileId={profileId} available worldName="Alderheim" />)
    expect(screen.getByRole('heading', { name: 'Alderheim' })).toBeInTheDocument()
    expect(await screen.findByText(/Verified save copy 3 on this PC/)).toBeInTheDocument()
    expect(screen.getByLabelText('Current signed Host')).toHaveTextContent('Unknown')
    expect(screen.queryByRole('button', { name: /Join|Start server/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Check signed branches' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Host this save' }))
    const guide = screen.getByRole('dialog', { name: 'Host this save' })
    expect(within(guide).getByRole('button', { name: 'Prepare solo recovery' })).toBeEnabled()
    fireEvent.click(within(guide).getByText("Transfer the group's default hosting"))
    expect(within(guide).getByLabelText('Hosting steps')).toHaveTextContent('Get savePrepare hostingStart server')
    expect(within(guide).getByText('Prepare hosting', { selector: 'li' })).toHaveAttribute('aria-current', 'step')
    expect(within(guide).queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
    expect(within(guide).queryByRole('button', { name: 'Prepare signed offer' })).not.toBeInTheDocument()
    expect(within(guide).queryByRole('button', { name: 'Rehearse file restore' })).not.toBeInTheDocument()
    fireEvent.click(within(guide).getByRole('button', { name: 'Sharing checks and history' }))
    const management = screen.getByRole('dialog', { name: 'Manage sharing' })
    fireEvent.click(within(management).getByText('Hosting checks and file restore rehearsal'))
    expect(within(management).getByRole('button', { name: 'Rehearse file restore' })).toBeEnabled()
    expect(mutations).toEqual([])
  })

  it('expires game observations independently from signed Host identity', () => {
    const now = Date.parse('2026-10-10T12:00:00Z')
    const host: SharedSignedHost = { pcName: 'Next Host PC', state: 'ObservedReady', checkedUtc: '2026-10-10T11:59:59Z' }
    expect(sharedHostObservation(host, now)).toBe('Ready observed · game join unverified')
    expect(sharedHostObservation(host, now + 45001)).toMatch(/^Unknown/)
    for (const checkedUtc of [null, 'not a timestamp', '2026-10-10T12:00:01Z']) {
      expect(sharedHostObservation({ ...host, checkedUtc }, now)).toMatch(/^Unknown/)
    }
    for (const state of ['Preparing', 'Offline', 'Starting', 'Unknown'] as const) {
      expect(sharedHostObservation({ ...host, state }, now)).toBe(state)
    }
  })

  it('keeps consent and verified transfer mandatory inside the hosting guide', async () => {
    let consented = false
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        calls.push(url)
        if (url.endsWith('/consent')) consented = true
        return reply({ ok: true, code: 'Saved', message: 'Saved',
          status: { ...copy, consented, thisPcVersion: null, state: 'Waiting for save' } })
      }
      if (url.endsWith('/recovery')) return reply(recovery)
      return reply({ ...copy, consented, thisPcVersion: null, state: 'Consent off' })
    }))
    render(<FriendSharedWorlds profileId={profileId} available />)
    fireEvent.click(screen.getByRole('button', { name: 'Host this save' }))
    const guide = screen.getByRole('dialog', { name: 'Host this save' })
    fireEvent.click(within(guide).getByText("Transfer the group's default hosting"))
    expect(within(guide).getByRole('button', { name: 'Get hosting save' })).toBeDisabled()
    expect(within(guide).queryByRole('button', { name: 'Restore verified copy' })).not.toBeInTheDocument()
    expect(within(guide).queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
    fireEvent.click(within(guide).getByLabelText('Allow this PC to receive the hosting save'))
    await waitFor(() => expect(within(guide).getByRole('button', { name: 'Get hosting save' })).toBeEnabled())
    expect(calls).toEqual([`/api/local/friend/${profileId}/shared-world/consent`])
  })

  it('offers manual Start navigation only after the backend completes restore and checks', async () => {
    let ready = false
    const navigate = vi.fn()
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        calls.push(url); ready = true
        return reply({ ok: true, code: 'ReadyForManualStart', message: 'Checks passed.', pendingChecks: [] })
      }
      return reply({ ...restore, staged: true, restored: true, recordHash: 'A'.repeat(64), readyForManualStart: ready })
    }))
    render(<SharedWorldReadinessPanel profileId={profileId} expanded onOpenHostControls={navigate} />)
    expect(screen.queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Finish setup and checks' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Review Start in Host' }))
    expect(navigate).toHaveBeenCalledTimes(1)
    expect(calls).toEqual([`/api/local/friend/${profileId}/shared-world/handoff/finish`])
    expect(calls.some(url => url.endsWith('/start'))).toBe(false)
  })

  it('keeps lifecycle changes fenced while preserving transfer review and management', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) calls.push(url)
      if (url.endsWith('/handoff')) return reply({ pending: true, code: 'ReadyToComplete', message: 'Old transfer retained.',
        successorDeviceId: null, finalVersion: 3, receiptConfirmed: true, canComplete: true, canCancel: true })
      if (url.endsWith('/governance')) return reply({ revision: 1, ownerOverride: true })
      return reply({ enabled: true, latest: null, error: null, canManageSharing: true,
        authority: { state: 'ReviewRequired', message: 'History needs review.', head: null,
          competingHeads: [], exactManagedProcessRunning: false } })
    }))
    render(<HostSharedSaves profileId={profileId} devices={[]} rollingBackupEnabled onGrantChanged={async () => {}} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Review transfer' }))
    expect(screen.getByRole('button', { name: 'Complete pending handoff' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancel pending handoff' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Stop and prepare file copy' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Close Move hosting' }))
    fireEvent.click(screen.getByRole('button', { name: 'Manage sharing' }))
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Manage sharing' })).getByRole('button', { name: 'History & recovery' }))
    expect(screen.getByRole('button', { name: 'Check owner decisions' })).toBeEnabled()
    expect(calls).toEqual([])
  })
})

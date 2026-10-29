import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FriendSharedWorlds, parseFriendSharedWorldStatus,
  parseHostSharedWorldStatus, parseLiveOrphanReview,
  parseRecoveryOffer, parseSharingView } from './SharedWorldControls'
import type { Device } from './contracts'

const profile = '11111111-1111-4111-8111-111111111111'
const device: Device = { id: '22222222-2222-4222-8222-222222222222', profileId: profile,
  assignedProfileIds: [profile], name: 'Friend PC', canStart: false, canStop: false,
  canExtendTimer: false, canViewLogs: false, saveReceiveProfileIds: [], revoked: false,
  paired: true, approvalPending: false, credentialExpiresUtc: null, lastHeartbeatUtc: null,
  serverPermissions: [], accessExpiresUtc: null, accessExpired: false }

function reply(value: unknown): Response {
  return new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } })
}
const noRecovery = { state: 'NoOffer', votes: 0, required: 0, version: null, versionHash: null,
  candidateAddress: null, candidateDeviceId: null, majorityReached: false, separateCopies: 0,
  proposalHash: null, authorityHeadHash: null }
const offer = { proposal: { profileId: profile, candidateAddress: 'https://192.0.2.10:5131', candidatePublicKey: 'public-key' },
  version: { number: 3, versionHash: 'A'.repeat(64), captureKind: 'PostStopBackup' },
  candidateReceipt: { deviceId: device.id }, signed: 'signed-by-candidate' }

afterEach(() => { vi.useRealTimers() })

describe('Shared saves controls', () => {
  it('waits for this PC\'s saved consent before enabling the receiving choice', async () => {
    let release!: (response: Response) => void
    const pending = new Promise<Response>(resolve => { release = resolve })
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/handoff/restore')) return reply({ staged: false })
      if (url.endsWith('/shared-world')) return pending
      return reply([])
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    const consent = screen.getByRole('checkbox', { name: 'Allow saves on this PC' })
    expect(consent).toBeDisabled()
    expect(mutations).toEqual([])
    release(reply({ consented: true, hostVersion: null, thisPcVersion: null,
      state: 'Waiting for a completed save', error: null }))
    await waitFor(() => expect(consent).toBeEnabled())
    expect(consent).toBeChecked()
    expect(mutations).toEqual([])
  })

  it('keeps guarded recovery usable from a verified copy when Host support is unknown after restart', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/recovery/offer')) {
        calls.push(url)
        return reply({ ok: false, code: 'HostLossNotConfirmed', message: 'Wait for verified Host loss.' })
      }
      if (url.endsWith('/recovery/vote')) {
        calls.push(url)
        return reply({ ok: false, code: 'PermissionDenied', message: 'This PC cannot vote.', votes: 0, required: 0 })
      }
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/handoff/restore')) return reply({ staged: false })
      if (init?.method) throw new Error(`Unexpected mutation ${url}`)
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available={false} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    expect(await screen.findByText(/You can still recover your verified copy below/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Get latest save' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Recovery' }))
    fireEvent.click(screen.getByText('Older recovery flow: ask voters to move hosting'))
    const prepare = screen.getByRole('button', { name: 'Prepare recovery offer' })
    expect(prepare).toBeEnabled()
    fireEvent.click(prepare)
    expect(await screen.findByText('Wait for verified Host loss.')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Offer code from candidate PC'), { target: { value: JSON.stringify(offer) } })
    fireEvent.click(screen.getByRole('button', { name: 'Review offer code' }))
    const vote = screen.getByRole('button', { name: 'Check and vote for this offer' })
    expect(vote).toBeEnabled()
    fireEvent.click(vote)
    await waitFor(() => expect(calls).toContain(`/api/local/friend/${profile}/shared-world/recovery/vote`))
    expect(screen.getByText(/Changes since the Host's last completed copy may be missing/)).toBeInTheDocument()
    expect(calls).toHaveLength(2)
  })

  it('keeps receiver consent off after warning dismissal until this PC opts in and shows version lag', async () => {
    let consent = false
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/consent')) {
        consent = true
        return reply({ ok: true, code: 'ConsentSaved', message: 'Consent saved',
          status: { consented: true, hostVersion: null, thisPcVersion: 1, state: 'Ready', error: null } })
      }
      if (url.endsWith('/pull')) return reply({ ok: true, code: 'SaveReceived', message: 'Received',
        status: { consented: true, hostVersion: 3, thisPcVersion: 2, state: 'Behind', error: null } })
      if (!init?.method) return reply({ consented: consent, hostVersion: null,
        thisPcVersion: 1, state: 'Consent off', error: null })
      throw new Error(`Unexpected request ${url}`)
    }))
    const view = render(<FriendSharedWorlds profileId={profile} connectionId="first" available />)
    await screen.findByRole('region', { name: 'Shared worlds' })
    const toggle = await screen.findByLabelText('Allow saves on this PC')
    expect(toggle).not.toBeChecked()
    expect(screen.getByRole('button', { name: 'Get latest save' })).toBeDisabled()
    const notice = await screen.findByText('Turn on Allow saves on this PC before choosing Get latest save.')
    fireEvent.click(within(notice).getByRole('button', { name: 'Dismiss notification' }))
    expect(screen.queryByText('Turn on Allow saves on this PC before choosing Get latest save.')).not.toBeInTheDocument()
    expect(toggle).not.toBeChecked()
    expect(screen.getByRole('button', { name: 'Get latest save' })).toBeDisabled()
    expect(mutations).toHaveLength(0)
    view.rerender(<FriendSharedWorlds profileId={profile} connectionId="first" available />)
    expect(screen.queryByText('Turn on Allow saves on this PC before choosing Get latest save.')).not.toBeInTheDocument()
    view.rerender(<FriendSharedWorlds profileId={profile} connectionId="second" available />)
    expect(screen.getByText('Turn on Allow saves on this PC before choosing Get latest save.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Get latest save' })).toBeDisabled()
    expect(mutations).toHaveLength(0)
    fireEvent.click(toggle)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Get latest save' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Get latest save' }))
    expect(await screen.findByText('This PC is one save behind')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Shared worlds' })).toHaveTextContent('This PC is one save behind')
    expect(screen.getByText(/Hosting never takes over automatically/)).toBeInTheDocument()
  })

  it('lets a voter review and confirm signed membership without enabling save receipt', async () => {
    const ownerPublicKey = 'owner-public-key-for-voter-review'
    const calls: Array<{ url: string; body: unknown }> = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/history/review')) {
        const body = JSON.parse(String(init?.body)) as Record<string, unknown>
        calls.push({ url, body })
        return body.confirmGroupId === profile && body.confirmOwnerPublicKey === ownerPublicKey ?
          reply({ ok: true, code: 'HistoryReviewed', message: 'Verified hosting history was reviewed.',
            recordCount: 2, competingHeads: 2, groupId: null, ownerPublicKey: null }) :
          reply({ ok: false, code: 'GroupReviewRequired', message: 'Review signed membership.',
            recordCount: 0, competingHeads: 0, groupId: profile, ownerPublicKey })
      }
      if (url.endsWith('/resolution/heads')) return reply([])
      if (url.endsWith('/recovery')) return reply(noRecovery)
      return reply({ consented: false, hostVersion: null, thisPcVersion: null,
        state: 'Consent off', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    expect(await screen.findByLabelText('Allow saves on this PC')).not.toBeChecked()
    fireEvent.click(screen.getByRole('button', { name: 'Details' }))
    fireEvent.click(screen.getByRole('button', { name: 'Review hosting history' }))
    expect(await screen.findByRole('button', { name: 'Confirm group and owner' })).toBeEnabled()
    expect(screen.getByText(new RegExp(`Group ${profile}`))).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Confirm group and owner' }))
    expect(await screen.findByText('Verified hosting history was reviewed.')).toBeInTheDocument()
    expect(screen.getByLabelText('Allow saves on this PC')).not.toBeChecked()
    expect(calls).toHaveLength(2)
    expect(calls[1].body).toEqual({ confirmGroupId: profile, confirmOwnerPublicKey: ownerPublicKey })
    expect(screen.getByRole('button', { name: 'Get latest save' })).toBeDisabled()
  })

  it('joins a verified successor through an explicit action while retaining the old connection', async () => {
    const calls: { url: string; body?: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined })
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/successor-enrollment')) return reply({ ok: true,
        code: 'SuccessorEnrolled', message: 'This PC can now receive the new Host saves.' })
      return reply({ consented: true, hostVersion: 2, thisPcVersion: 2,
        state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    fireEvent.click(screen.getByRole('button', { name: 'Recovery' }))
    fireEvent.click(await screen.findByText('Join a new Host after takeover'))
    const action = screen.getByText('Check and join new Host')
    expect(action).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Hosting decision ID'),
      { target: { value: 'A'.repeat(64) } })
    fireEvent.change(screen.getByLabelText('New Host certificate fingerprint'),
      { target: { value: 'B'.repeat(64) } })
    fireEvent.click(action)
    await waitFor(() => expect(calls).toContainEqual({
      url: `/api/local/friend/${profile}/shared-world/successor-enrollment`,
      body: { recordHash: 'A'.repeat(64), tlsFingerprint: 'B'.repeat(64) }
    }))
    expect(await screen.findByText('This PC can now receive the new Host saves.')).toBeInTheDocument()
    expect(screen.getByText(/Your old connection and save history are kept/)).toBeInTheDocument()
  })

  it('rejects malformed status responses', () => {
    expect(() => parseHostSharedWorldStatus({ enabled: 'yes', latest: null, error: null })).toThrow()
    expect(() => parseFriendSharedWorldStatus({ consented: false, hostVersion: '3',
      thisPcVersion: null, state: 'Off', error: null })).toThrow()
  })

  it('accepts only the two signed save capture kinds in a recovery offer', () => {
    expect(parseRecoveryOffer(offer, profile).version.captureKind).toBe('PostStopBackup')
    expect(parseRecoveryOffer({ ...offer, version: { ...offer.version, captureKind: 'LiveSave' } }, profile)
      .version.captureKind).toBe('LiveSave')
    expect(() => parseRecoveryOffer({ ...offer, version: { ...offer.version, captureKind: 'Unknown' } }, profile))
      .toThrow('Save capture kind is invalid.')
  })

  it('keeps local staging measured and guarded before receiving or final verification', async () => {
    vi.useFakeTimers()
    let phase = 'Preparing'
    let receivedBytes = 128
    const fetcher = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(async url => {
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/handoff/restore')) return reply({ staged: false })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 2, state: phase,
        transferPhase: phase, error: null, receivedBytes, totalBytes: 1024 })
    })
    vi.stubGlobal('fetch', fetcher)
    render(<FriendSharedWorlds profileId={profile} available />)
    await act(async () => {})
    expect(screen.getByRole('heading', { name: 'Preparing local save files' })).toBeInTheDocument()
    expect(screen.getByRole('progressbar', { name: 'Local save bytes staged' })).toHaveAttribute('value', '128')
    expect(screen.getByRole('button', { name: 'Get latest save' })).toBeDisabled()
    receivedBytes = 768
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(screen.getByRole('progressbar', { name: 'Local save bytes staged' })).toHaveAttribute('value', '768')
    phase = 'Verifying'; receivedBytes = 1024
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(screen.getByRole('progressbar', { name: 'Checking save files and permission' })).not.toHaveAttribute('value')
    expect(screen.getByRole('button', { name: 'Get latest save' })).toBeDisabled()
    expect(fetcher.mock.calls.every(([, init]) => !init?.method)).toBe(true)
  })

  it('refreshes an open receiver panel as automatic catch-up advances', async () => {
    let reads = 0
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/handoff/restore')) return reply({ staged: false })
      reads++
      return reply({ consented: true, hostVersion: 3, thisPcVersion: reads > 1 ? 3 : 2,
        state: reads > 1 ? 'Up to date when last checked' : 'Receiving', error: null,
        receivedBytes: 32, totalBytes: 64 })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    await screen.findByRole('region', { name: 'Shared worlds' })
    expect(await screen.findByRole('heading', { name: 'Receiving the save copy' })).toHaveAttribute('aria-live', 'polite')
    await new Promise(resolve => setTimeout(resolve, 2200))
    expect(await screen.findByText('Up to date when last checked')).toBeInTheDocument()
  })

  it('waits for a slow save-status poll and ignores its response after changing worlds', async () => {
    let reads = 0
    let release: ((response: Response) => void) | undefined
    const nextProfile = '33333333-3333-4333-8333-333333333333'
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/handoff/restore')) return reply({ staged: false })
      if (url.includes(nextProfile)) return reply({ consented: true, hostVersion: 6,
        thisPcVersion: 5, state: 'Ready to pull', error: null })
      reads++
      if (reads > 1) return new Promise<Response>(resolve => { release = resolve })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3,
        state: 'Up to date when last checked', error: null })
    }))
    const view = render(<FriendSharedWorlds profileId={profile} available />)
    await screen.findByRole('region', { name: 'Shared worlds' })
    await waitFor(() => expect(reads).toBe(2), { timeout: 3500 })
    await new Promise(resolve => setTimeout(resolve, 2200))
    expect(reads).toBe(2)
    view.rerender(<FriendSharedWorlds profileId={nextProfile} available />)
    expect(await screen.findByText('This PC is one save behind')).toBeInTheDocument()
    release!(reply({ consented: true, hostVersion: 3, thisPcVersion: 3,
      state: 'Stalled', error: 'Stale poll from the previous world.' }))
    await new Promise(resolve => setTimeout(resolve, 100))
    expect(screen.getByText('This PC is one save behind')).toBeInTheDocument()
    expect(screen.queryByText('Stale poll from the previous world.')).not.toBeInTheDocument()
  }, 10000)

  it('shows a manual low-space failure beside the current action and clears it after a verified pull', async () => {
    let current = { consented: true, hostVersion: 3, thisPcVersion: 2,
      state: 'Ready to pull', error: null as string | null }
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/pull')) {
        if (current.state !== 'Low space') {
          current = { ...current, state: 'Low space', error: 'Keep at least 1 GiB free.' }
          return reply({ ok: false, code: 'InsufficientSpace', message: current.error })
        }
        current = { ...current, state: 'Up to date when last checked', thisPcVersion: 3, error: null }
        return reply({ ok: true, code: 'SaveReceived', message: 'Verified copy received.', status: current })
      }
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (init?.method) throw new Error(`Unexpected request ${url}`)
      return reply(current)
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    await screen.findByRole('region', { name: 'Shared worlds' })
    fireEvent.click(await screen.findByRole('button', { name: 'Get latest save' }))
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shared worlds' })!
      .querySelector('[role="alert"]')).toHaveTextContent('Low space — receiving paused'))
    // The shared-save workspace stays visible.
    expect(screen.getByRole('region', { name: 'Shared worlds' })).toBeVisible()
    // The shared-save workspace stays visible.
    fireEvent.click(screen.getByRole('button', { name: 'Get latest save' }))
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shared worlds' })!
      .querySelector('[role="alert"]')).not.toBeInTheDocument())
  })

  it('refreshes a stalled transfer alert in the visible workspace', async () => {
    let stalled = false
    vi.stubGlobal('fetch', vi.fn(async () => reply({ consented: true, hostVersion: 3,
      thisPcVersion: 2, state: stalled ? 'Stalled' : 'Ready to pull',
      error: stalled ? 'Receiving made no progress across repeated attempts.' : null })))
    render(<FriendSharedWorlds profileId={profile} available />)
    await screen.findByText('This PC is one save behind')
    stalled = true
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shared worlds' })!
      .querySelector('[role="alert"]')).toHaveTextContent('Transfer paused — retrying'),
    { timeout: 7500 })
    expect(screen.getByRole('region', { name: 'Shared worlds' })).toBeVisible()
  }, 10000)

  it('warns about the free-space reserve without hiding a save behind', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply({ consented: true, hostVersion: 6,
      thisPcVersion: 5, state: 'Ready to pull', error: null,
      capacityState: 'Low space',
      capacityNotice: 'Keep at least 1 GiB free to receive another signed save.' })))
    render(<FriendSharedWorlds profileId={profile} available />)
    const summary = await screen.findByRole('region', { name: 'Shared worlds' })
    await waitFor(() => expect(summary.querySelector('[role="alert"]'))
      .toHaveTextContent('Low space — receiving paused'))
    fireEvent.click(summary)
    expect(summary).toHaveTextContent('This PC is one save behind')
    expect(screen.getByText('Keep at least 1 GiB free to receive another signed save.'))
      .toHaveAttribute('role', 'alert')
  })

  it('shows manual Start readiness only after the restored setup checks confirm it', async () => {
    let ready = false
    let conflict = false
    let verificationError = false
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/handoff/finish')) {
        ready = true
        return reply({ ok: true, code: 'ReadyForManualStart', message: 'Setup checks passed.',
          pendingChecks: null })
      }
      if (url.endsWith('/handoff/restore')) return reply({ staged: true, restored: true,
        recordHash: 'A'.repeat(64), message: 'Setup checks passed.', pendingChecks: [],
        preparedServerRoot: null, readyForManualStart: ready, requiredAddOns: [],
        controlRouteFingerprint: 'B'.repeat(64) })
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/check')) {
        if (conflict) verificationError = true
        else conflict = true
        return reply({ ok: false, code: verificationError ? 'HistoryInvalid' : 'VersionConflict',
          message: verificationError ? 'The stored save failed verification.' : 'Competing saves need review.',
          status: { consented: true, hostVersion: 2, thisPcVersion: 2,
            state: verificationError ? 'Error' : 'Competing save histories. Review before receiving another save.',
            error: verificationError ? 'The stored save failed verification.' : null } })
      }
      return reply({ consented: true, hostVersion: 2, thisPcVersion: 2,
        state: verificationError ? 'Error' : conflict ?
          'Competing save histories. Review before receiving another save.' : 'Ready',
        error: verificationError ? 'The stored save failed verification.' : null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shared worlds' }))
      .toHaveTextContent('Verified copy on this PC'))
    expect(screen.getByText(/Verified save 2 is on this PC/)).toBeInTheDocument()
    expect(screen.queryByText(/Verified post-Stop file copy 2/)).not.toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Shared worlds' }))
      .not.toHaveTextContent('Ready for manual Start')
    fireEvent.click(await screen.findByRole('button', { name: 'Host this save' }))
    fireEvent.click(screen.getByText("Planned handoff and older recovery flow"))
    fireEvent.click(await screen.findByRole('button', { name: 'Finish setup and checks' }))
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shared worlds' }))
      .toHaveTextContent('Ready for manual Start; game load untested'))
    fireEvent.click(screen.getByRole('button', { name: 'Check latest' }))
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shared worlds' }))
      .toHaveTextContent('Competing save histories. Review before receiving another save.'))
    expect(screen.getByRole('region', { name: 'Shared worlds' }))
      .not.toHaveTextContent('Ready for manual Start')
    fireEvent.click(screen.getByRole('button', { name: 'Check latest' }))
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shared worlds' }))
      .toHaveTextContent('The stored save failed verification.'))
    expect(screen.getByRole('region', { name: 'Shared worlds' }))
      .not.toHaveTextContent('Ready for manual Start')
    expect(screen.getByText(/File checks do not prove the game can load or play/))
      .toBeInTheDocument()
  })

  it('opens the saved identity recovery flow for a changed Host address', async () => {
    const openRecovery = vi.fn()
    const fetch = vi.fn(async (url: string, init?: RequestInit) => {
      if (!url.includes('/shared-world') || init?.method === 'PUT') throw new Error('Unexpected request')
      return reply({ consented: false, hostVersion: null,
        thisPcVersion: null, state: 'Consent off', error: null })
    })
    vi.stubGlobal('fetch', fetch)
    render(<FriendSharedWorlds profileId={profile} available onAddressChange={openRecovery} />)
    await screen.findByRole('region', { name: 'Shared worlds' })
    fireEvent.click(screen.getByRole('button', { name: 'Host address changed?' }))
    expect(openRecovery).toHaveBeenCalledOnce()
    expect(fetch.mock.calls.every(([, init]) => init?.method !== 'PUT')).toBe(true)
  })

  it('lets a Manage sharing Friend edit another PC without Receive consent', async () => {
    const otherId = '33333333-3333-4333-8333-333333333333'
    const removedId = '44444444-4444-4444-8444-444444444444'
    const calls: { path: string; body?: unknown }[] = []
    const sharing = { available: true, canManage: true, selfDeviceId: device.id,
      revision: 5, code: 'SharingReady', message: 'Sharing verified.',
      checkedUtc: '2026-10-03T12:00:00Z', members: [
        { deviceId: device.id, isSelf: true, grants: { receive: false, eligibleHost: false,
          recoveryVoter: false, manageSharing: true }, revoked: false, accessExpiresUtc: null, name: null },
        { deviceId: otherId, isSelf: false, grants: { receive: true, eligibleHost: true,
          recoveryVoter: false, manageSharing: false }, revoked: false, accessExpiresUtc: null, name: null },
        { deviceId: removedId, isSelf: false, grants: { receive: false, eligibleHost: false,
          recoveryVoter: false, manageSharing: false }, revoked: true, accessExpiresUtc: null, name: null }
      ] }
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ path: url, body: init?.body ? JSON.parse(String(init.body)) : undefined })
      if (url.endsWith('/sharing/check')) return reply(sharing)
      if (url.endsWith('/sharing')) return reply({ ok: true, code: 'SharingChanged', message: 'Sharing updated.' })
      return reply({ consented: false, hostVersion: null, thisPcVersion: null,
        state: 'Consent off', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    fireEvent.click(screen.getByRole('button', { name: 'People' }))
    fireEvent.click(screen.getByText('Member permissions'))
    const vote = await screen.findByLabelText('Recovery voter for PC ending 333333')
    expect(screen.queryByText(/PC ending 444444/)).not.toBeInTheDocument()
    const technical = screen.getByText('Member permissions').closest('details')!.querySelector('details')!
    technical.open = true
    expect(screen.queryByText(new RegExp(removedId))).not.toBeInTheDocument()
    fireEvent.click(vote)
    await waitFor(() => expect(calls).toContainEqual({
      path: `/api/local/friend/${profile}/shared-world/sharing`,
      body: { deviceId: otherId, receive: true, eligibleHost: true,
        recoveryVoter: true, revoked: false }
    }))
    expect(calls.some(call => call.path.endsWith('/consent') || call.path.endsWith('/pull'))).toBe(false)
    expect(screen.queryByLabelText('Manage sharing for PC ending 333333')).not.toBeInTheDocument()
  })

  it('shows expired management access and does not offer member edits', async () => {
    const paths: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      paths.push(url)
      if (url.endsWith('/sharing/check')) return reply({ available: true, canManage: false,
        selfDeviceId: device.id, revision: null, code: 'AccessExpired',
        message: 'The owner ended this PC\'s sharing access.', members: [], checkedUtc: null })
      return reply({ consented: false, hostVersion: null, thisPcVersion: null,
        state: 'Consent off', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    fireEvent.click(screen.getByRole('button', { name: 'People' }))
    fireEvent.click(screen.getByText('Member permissions'))
    expect(await screen.findByRole('alert')).toHaveTextContent('owner ended this PC')
    expect(screen.queryByLabelText(/Receive for PC ending/)).not.toBeInTheDocument()
    expect(paths.some(path => path.endsWith('/sharing'))).toBe(false)
  })

  it('rejects malformed delegated sharing grants before showing controls', () => {
    expect(() => parseSharingView({ available: true, canManage: true,
      selfDeviceId: device.id, revision: 1, code: 'SharingReady', message: 'Ready', checkedUtc: null,
      members: [{ deviceId: device.id, isSelf: false, grants: { receive: 'yes' },
        revoked: false, accessExpiresUtc: null }] })).toThrow()
  })

  it('explains how to review a changed Host source without comparing group version numbers', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply({ consented: true, hostVersion: 1,
      thisPcVersion: 8, state: 'Host save source changed. Turn Allow saves off, then on to approve the new signed group. Earlier verified copies stay here.',
      error: null })))
    render(<FriendSharedWorlds profileId={profile} available />)
    await screen.findByRole('region', { name: 'Shared worlds' })
    expect(await screen.findByRole('alert')).toHaveTextContent('Earlier verified copies stay here')
    expect(screen.queryByText(/version\(s\) behind/)).not.toBeInTheDocument()
  })

  it('stages only a signed Friend offer and keeps hosting setup pending', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? 'GET'} ${url}`)
      if (url.endsWith('/stage')) return reply({ ok: true, code: 'StagedForSetup',
        message: 'The signed final copy is staged.' })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Host this save' }))
    fireEvent.click(screen.getByText("Planned handoff and older recovery flow"))
    expect(await screen.findByText('Verified copy on this PC')).toBeInTheDocument()
    expect(screen.queryByText('Ready to host')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Check handoff and prepare save' }))
    await waitFor(() => expect(calls).toContain(`POST /api/local/friend/${profile}/shared-world/handoff/stage`))
    expect(await screen.findByText(/Server setup, sharing identity and connections still need checking/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
  })

  it('restores an armed recovery offer, rejects malformed code, and submits the reviewed vote', async () => {
    const calls: { url: string; body?: unknown }[] = []
    let voteAttempts = 0
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/recovery/vote')) {
        calls.push({ url, body: JSON.parse(String(init?.body)) })
        voteAttempts++
        if (voteAttempts === 1) return reply({ ok: false, code: 'CandidateUnavailable',
          message: 'The candidate did not answer.', votes: 0, required: 0, decision: null })
        return reply({ ok: true, code: 'VoteRecorded', message: 'Your recovery vote was recorded.', votes: 1, required: 2, decision: null })
      }
      if (url.endsWith('/offer-code')) return reply({ proposalHash: 'B'.repeat(64), offer })
      if (url.endsWith('/recovery')) return reply({ ...noRecovery, state: 'OfferArmed', votes: 1, required: 2,
        version: 3, versionHash: 'A'.repeat(64), candidateAddress: offer.proposal.candidateAddress,
        candidateDeviceId: device.id, proposalHash: 'B'.repeat(64) })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    expect(await screen.findByText(/Votes 1\/2/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Recovery' }))
    fireEvent.click(screen.getByText('Older recovery flow: ask voters to move hosting'))
    fireEvent.click(screen.getByText('Older recovery flow: ask voters to move hosting').closest('details')!.querySelector('details > summary')!)
    fireEvent.click(screen.getByRole('button', { name: 'Show recovery offer code' }))
    expect(await screen.findByLabelText('Recovery offer code')).toHaveValue(JSON.stringify(offer))
    fireEvent.change(screen.getByLabelText('Offer code from candidate PC'), { target: { value: '{broken' } })
    fireEvent.click(screen.getByRole('button', { name: 'Review offer code' }))
    expect(await screen.findByText(/Invalid offer code/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Check and vote for this offer' })).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Offer code from candidate PC'), { target: { value: JSON.stringify(offer) } })
    fireEvent.click(screen.getByRole('button', { name: 'Review offer code' }))
    expect(screen.getByText(/save version 3 \(backup after Stop\)/)).toBeInTheDocument()
    const liveOffer = { ...offer, version: { ...offer.version, captureKind: 'LiveSave' } }
    fireEvent.change(screen.getByLabelText('Offer code from candidate PC'), { target: { value: JSON.stringify(liveOffer) } })
    fireEvent.click(screen.getByRole('button', { name: 'Review offer code' }))
    expect(screen.getByText(/save version 3 \(save copied while running\)/)).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Offer code from candidate PC'), { target: { value: JSON.stringify({
      ...offer, version: { ...offer.version, captureKind: 'Unknown' } }) } })
    fireEvent.click(screen.getByRole('button', { name: 'Review offer code' }))
    expect(screen.getByText(/Invalid offer code: Save capture kind is invalid/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Check and vote for this offer' })).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Offer code from candidate PC'), { target: { value: JSON.stringify(liveOffer) } })
    fireEvent.click(screen.getByRole('button', { name: 'Review offer code' }))
    expect(screen.getByText(/another Host may still be running/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Check and vote for this offer' }))
    expect(await screen.findByText('The candidate did not answer.')).toBeInTheDocument()
    expect(screen.queryByText(/Majority decision recorded/)).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Check and vote for this offer' }))
    await waitFor(() => expect(calls).toEqual([
      { url: `/api/local/friend/${profile}/shared-world/recovery/vote`, body: liveOffer },
      { url: `/api/local/friend/${profile}/shared-world/recovery/vote`, body: liveOffer }]))
    expect(await screen.findByText('Your recovery vote was recorded.')).toBeInTheDocument()
    expect(screen.queryByText('Ready to host')).not.toBeInTheDocument()
  })

  it('allows manual solo recovery while the original Host is Ready and requires its explicit branch warning', async () => {
    const calls: { url: string; body: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method === 'POST') {
        calls.push({ url, body: JSON.parse(String(init.body)) })
        if (url.endsWith('/solo/prepare')) return reply({ ok: true, code: 'SoloRecoveryPrepared',
          message: 'Review the complete recovery copy.', offer: { ...offer,
            version: { ...offer.version, createdUtc: '2020-01-01T00:00:00Z' } } })
        return reply({ ok: true, code: 'SeparateCopyRecorded', message: 'A separate history was recorded.' })
      }
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/handoff/restore')) return reply({ staged: false })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null,
        completedUtc: '2020-01-01T00:00:00Z' })
    }))
    render(<FriendSharedWorlds profileId={profile} available serverState="Ready" serverStatusFresh />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    fireEvent.click(screen.getByRole('button', { name: 'Recovery' }))
    const solo = screen.getByRole('region', { name: 'Manual solo recovery' })
    expect(within(solo).getByText(/Older complete copies can still be used/)).toBeInTheDocument()
    expect(within(solo).queryByRole('button', { name: 'Keep recovery history' })).not.toBeInTheDocument()
    fireEvent.click(within(solo).getByRole('button', { name: 'Prepare solo recovery' }))
    const separateButton = await within(solo).findByRole('button', { name: 'Keep recovery history' })
    expect(separateButton).toBeDisabled()
    expect(calls).toEqual([{ url: `/api/local/friend/${profile}/shared-world/recovery/solo/prepare`, body: { sourceBranchHash: null } }])
    fireEvent.click(within(solo).getByLabelText('I understand another server may still be running and both histories will be kept'))
    fireEvent.click(separateButton)
    await waitFor(() => expect(calls).toContainEqual({ url: `/api/local/friend/${profile}/shared-world/recovery/separate`, body: { acceptSplitWarning: true } }))
    expect(await screen.findByText('A separate history was recorded.')).toBeInTheDocument()
    expect(within(solo).queryByRole('button', { name: 'Keep recovery history' })).not.toBeInTheDocument()
    expect(calls).toHaveLength(2)
  })

  it('prepares the explicitly selected branch while offline without borrowing the default copy date', async () => {
    const bodies: unknown[] = []
    const selectedBranch = 'C'.repeat(64)
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/solo/prepare')) {
        bodies.push(JSON.parse(String(init?.body)))
        return reply({ ok: true, code: 'SoloRecoveryPrepared', message: 'Selected history verified.',
          offer: { ...offer, version: { ...offer.version, createdUtc: '2021-01-01T00:00:00Z' } } })
      }
      if (url.endsWith('/recovery')) return reply({ ...noRecovery, state: 'HistoryReviewRequired' })
      if (url.endsWith('/handoff/restore')) return reply({ staged: false })
      return reply({ consented: true, hostVersion: null, thisPcVersion: null, state: 'Host unavailable', error: null,
        completedUtc: '2026-01-01T00:00:00Z' })
    }))
    render(<FriendSharedWorlds profileId={profile} available={false} sourceBranchHash={selectedBranch} />)
    const solo = await screen.findByRole('region', { name: 'Manual solo recovery' })
    const prepare = within(solo).getByRole('button', { name: 'Prepare solo recovery' })
    await waitFor(() => expect(prepare).toBeEnabled())
    expect(solo.querySelector('time')).toBeNull()
    fireEvent.click(prepare)
    await within(solo).findByRole('button', { name: 'Keep recovery history' })
    expect(bodies).toEqual([{ sourceBranchHash: selectedBranch }])
    expect(solo.querySelector('time')).toHaveAttribute('datetime', '2021-01-01T00:00:00Z')
  })

  it('labels another PC’s current majority and superseded history without keeping an old offer code', async () => {
    let state: 'OfferArmed' | 'ObservedMajority' | 'HistoricalRecovery' | 'HistoryReviewRequired' = 'OfferArmed'
    const otherPc = '33333333-3333-4333-8333-333333333333'
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/offer-code')) return reply({ proposalHash: 'B'.repeat(64), offer })
      if (url.endsWith('/recovery')) return reply({ ...noRecovery, state,
        votes: state === 'ObservedMajority' ? 2 : 0,
        required: state === 'OfferArmed' || state === 'ObservedMajority' ? 2 : 0,
        version: state === 'HistoryReviewRequired' ? null : 3,
        versionHash: state === 'HistoryReviewRequired' ? null : 'A'.repeat(64),
        candidateAddress: state === 'HistoryReviewRequired' ? null : offer.proposal.candidateAddress,
        candidateDeviceId: state === 'HistoryReviewRequired' ? null : otherPc,
        majorityReached: state === 'ObservedMajority',
        proposalHash: state === 'OfferArmed' ? 'B'.repeat(64) : 'C'.repeat(64),
        authorityHeadHash: state === 'OfferArmed' ? null : 'D'.repeat(64) })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    fireEvent.click(screen.getByRole('button', { name: 'Recovery' }))
    fireEvent.click(await screen.findByText('Older recovery flow: ask voters to move hosting'))
    await screen.findByText(/Votes 0\/2/)
    fireEvent.click(screen.getByText('Older recovery flow: ask voters to move hosting').closest('details')!.querySelector('details > summary')!)
    fireEvent.click(await screen.findByRole('button', { name: 'Show recovery offer code' }))
    expect(await screen.findByLabelText('Recovery offer code')).toBeInTheDocument()
    state = 'ObservedMajority'
    fireEvent.click(screen.getByRole('button', { name: 'Refresh recovery' }))
    expect(await screen.findByText(/Another PC’s current majority decision/)).toBeInTheDocument()
    expect(screen.getByText(/Current candidate PC 33333333/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Recovery offer code')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Record separate history' })).not.toBeInTheDocument()
    state = 'HistoricalRecovery'
    fireEvent.click(screen.getByRole('button', { name: 'Refresh recovery' }))
    expect(await screen.findByText(/Earlier recovery history is preserved/)).toBeInTheDocument()
    expect(screen.getByText(/Earlier candidate PC 33333333/)).toBeInTheDocument()
    expect(screen.queryByText(/Majority decision pending/)).not.toBeInTheDocument()
    state = 'HistoryReviewRequired'
    fireEvent.click(screen.getByRole('button', { name: 'Refresh recovery' }))
    expect(await screen.findByText(/Conflicting hosting decisions need review/)).toBeInTheDocument()
    expect(screen.queryByText(/Current candidate PC/)).not.toBeInTheDocument()
  })

  it('drops an obsolete default offer code while retaining independent manual solo recovery', async () => {
    let changed = false
    let reads = 0
    let codeReads = 0
    let codeHash = 'C'.repeat(64)
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/offer-code')) { codeReads++; return reply({ proposalHash: codeHash, offer }) }
      if (url.endsWith('/recovery')) {
        reads++
        return reply(changed ? { ...noRecovery, state: 'HistoryReviewRequired',
          authorityHeadHash: 'D'.repeat(64) } : { ...noRecovery, state: 'OfferArmed',
          votes: 1, required: 2, version: 3, versionHash: 'A'.repeat(64),
          candidateAddress: offer.proposal.candidateAddress, candidateDeviceId: device.id,
          proposalHash: 'B'.repeat(64) })
      }
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    fireEvent.click(screen.getByRole('button', { name: 'Recovery' }))
    fireEvent.click(await screen.findByText('Older recovery flow: ask voters to move hosting'))
    // The section is available before its independent recovery poll commits.
    // Wait for the armed proposal before choosing its conditional details.
    await screen.findByText(/Votes 1\/2/)
    fireEvent.click(screen.getByText('Older recovery flow: ask voters to move hosting').closest('details')!.querySelector('details > summary')!)
    fireEvent.click(await screen.findByRole('button', { name: 'Show recovery offer code' }))
    await waitFor(() => expect(codeReads).toBe(1))
    expect(screen.queryByLabelText('Recovery offer code')).not.toBeInTheDocument()
    codeHash = 'B'.repeat(64)
    fireEvent.click(screen.getByRole('button', { name: 'Show recovery offer code' }))
    expect(await screen.findByLabelText('Recovery offer code')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Prepare solo recovery' })).toBeEnabled()
    const readsBeforeChange = reads
    changed = true
    await waitFor(() => expect(reads).toBeGreaterThan(readsBeforeChange), { timeout: 3500 })
    await waitFor(() => expect(screen.getByText(/Conflicting hosting decisions need review/)).toBeInTheDocument())
    expect(screen.queryByLabelText('Recovery offer code')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Prepare solo recovery' })).toBeEnabled()
    expect(screen.queryByText(/Votes 1\/2/)).not.toBeInTheDocument()
  })

  it('shares one in-flight recovery read between polling and manual refresh', async () => {
    let reads = 0
    let finishRead: ((response: Response) => void) | undefined
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/recovery')) {
        reads++
        return await new Promise<Response>(resolve => { finishRead = resolve })
      }
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    fireEvent.click(screen.getByRole('button', { name: 'Recovery' }))
    fireEvent.click(await screen.findByText('Older recovery flow: ask voters to move hosting'))
    fireEvent.click(screen.getByRole('button', { name: 'Refresh recovery' }))
    expect(reads).toBe(1)
    finishRead?.(reply(noRecovery))
    await waitFor(() => expect(screen.getByText('No recovery offer is active on this PC.')).toBeInTheDocument())
  })

  it('offers only a locally verified competing branch and reviews the voting invitation', async () => {
    const chosen = 'A'.repeat(64)
    const other = 'B'.repeat(64)
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? 'GET'} ${url}`)
      if (url.endsWith('/resolution/heads')) return reply([
        { recordHash: chosen, version: 2, availableHere: true },
        { recordHash: other, version: 3, availableHere: false }])
      if (url.endsWith(`/resolution/offer/${chosen}`)) return reply({ ok: true,
        code: 'ResolutionOfferArmed', message: 'Offer ready', offer: {
          proposal: { kind: 'ResolutionQuorum', competingHeadHashes: [chosen, other] },
          version: { number: 2 } } })
      if (url.endsWith('/resolution/invitations')) return reply({ ok: true, code: 'InvitationReviewed',
        message: 'Invitation reviewed', proposalHash: 'C'.repeat(64) })
      if (url.endsWith(`/resolution/vote/${'C'.repeat(64)}`))
        return reply({ ok: true, code: 'VoteRecorded', message: 'Vote recorded' })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 2, state: 'Competing save histories', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(await screen.findByRole('button', { name: 'Manage sharing' }))
    fireEvent.click(screen.getByRole('button', { name: 'Recovery' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Check verified save histories' }))
    expect(await screen.findByRole('button', { name: 'Ask recovery voters' })).toBeEnabled()
    expect(screen.getAllByText(/on another PC/)).toHaveLength(1)
    fireEvent.click(screen.getByRole('button', { name: 'Ask recovery voters' }))
    expect(await screen.findByText('Offer ready')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Vote on an invitation'))
    fireEvent.change(screen.getByLabelText('Paste voting invitation'), { target: { value: JSON.stringify({
      proposal: { kind: 'ResolutionQuorum', competingHeadHashes: [chosen, other] }, version: { number: 2 } }) } })
    expect(screen.queryByRole('button', { name: 'Approve this copy' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Review invitation' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Approve this copy' }))
    await waitFor(() => expect(calls).toContain(
      `POST /api/local/friend/${profile}/shared-world/resolution/vote/${'C'.repeat(64)}`))
  })

  it('accepts only a consistent exact-hash interrupted-copy status', () => {
    const versionHash = 'A'.repeat(64)
    expect(parseLiveOrphanReview({ code: 'Verified', versionHash,
      message: 'Signed file copy verified.' }).versionHash).toBe(versionHash)
    for (const invalid of [
      { code: 'Verified', versionHash: null, message: 'Missing hash' },
      { code: 'None', versionHash, message: 'Wrong state' },
      { code: 'Verified', versionHash: 'a'.repeat(64), message: 'Wrong casing' },
      { code: 'Verified', versionHash: 'G'.repeat(64), message: 'Invalid hex' },
      { code: 'ReviewRequired', versionHash: null, message: '' },
      { code: 'Verified', versionHash, message: 'Extra field', path: 'C:/world' }
    ]) expect(() => parseLiveOrphanReview(invalid)).toThrow()
  })
})

import { act, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FriendSharedWorlds, HostSharedSaves } from './SharedWorldControls'

const profileId = '11111111-1111-4111-8111-111111111111'
const copy = { consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null }
const recovery = { state: 'NoOffer', votes: 0, required: 0, version: null, versionHash: null,
  candidateAddress: null, candidateDeviceId: null, majorityReached: false, separateCopies: 0,
  proposalHash: null, authorityHeadHash: null }
const restore = { staged: false, restored: false, recordHash: null, message: 'No signed decision staged.',
  pendingChecks: [], preparedServerRoot: null, readyForManualStart: false, requiredAddOns: [],
  controlRouteFingerprint: null }
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

function primaryActions() {
  const workspace = screen.getByRole('region', { name: 'Shared worlds' })
  return within(workspace).getAllByRole('button').filter(button =>
    !button.closest('dialog, [role="dialog"]') && !button.classList.contains('secondary') &&
    !button.classList.contains('text-button'))
}

describe('Shared saves task presentation', () => {
  it('omits only the repeated world heading and keeps named identity, receiving consent and management', async () => {
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      return reply(url.endsWith('/recovery') ? recovery : url.endsWith('/handoff/restore') ? restore :
        { ...copy, consented: false, thisPcVersion: null, state: 'Consent off' })
    }))
    render(<FriendSharedWorlds profileId={profileId} available showWorldHeading={false} worldName="Alderheim"
      connectedHostName="Saved Weekend Host" currentSignedHost={{ pcName: 'Named signed PC', state: 'Unknown', checkedUtc: null }} />)
    await act(async () => {})
    expect(screen.queryByRole('heading', { name: 'Alderheim' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Shared saves' })).toBeInTheDocument()
    expect(screen.getByLabelText('Current signed Host')).toHaveTextContent('Signed Host: Named signed PC')
    expect(screen.getByText('Saved Host: Saved Weekend Host')).toBeInTheDocument()
    expect(screen.getByLabelText('Allow saves on this PC')).not.toBeChecked()
    expect(screen.getByText('Receiving consent is required before Get latest save.')).toBeInTheDocument()
    expect(primaryActions()).toEqual([screen.getByRole('button', { name: 'Host this save' })])
    expect(screen.getByRole('button', { name: 'Get latest save' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Get latest save' })).toHaveClass('secondary')
    fireEvent.click(screen.getByRole('button', { name: 'Host this save' }))
    expect(screen.getByRole('button', { name: 'Prepare solo recovery' })).toBeDisabled()
    fireEvent.click(screen.getByText("Transfer the group's default hosting"))
    expect(screen.getByRole('button', { name: 'Get hosting save' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Get hosting save' })).toHaveClass('secondary')
    expect(screen.getByLabelText('Allow this PC to receive the hosting save')).not.toBeChecked()
    expect(mutations).toEqual([])
  })

  it.each([
    { title: 'missing copy', save: { ...copy, thisPcVersion: null, state: 'Waiting for save' }, primary: 'Get latest save' },
    { title: 'behind copy', save: { ...copy, hostVersion: 4 }, primary: 'Get latest save' },
    { title: 'verified current copy', save: copy, primary: 'Host this save' }
  ])('has one next action for $title without executing a transfer or Start', async ({ save, primary }) => {
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      return reply(url.endsWith('/recovery') ? recovery : url.endsWith('/handoff/restore') ? restore : save)
    }))
    render(<FriendSharedWorlds profileId={profileId} available serverState="Ready" serverStatusFresh onOpenPlay={vi.fn()} />)
    await act(async () => {})
    expect(primaryActions()).toEqual([screen.getByRole('button', { name: primary })])
    expect(screen.getByRole('button', { name: 'Manage sharing' })).toHaveClass('secondary')
    expect(screen.getByRole('button', { name: 'Join connected Host' })).toHaveClass('secondary')
    expect(screen.getByRole('button', { name: 'Host this save' })).toBeEnabled()
    expect(mutations).toEqual([])
  })

  it('keeps receiving primary and fresh connected Ready joining secondary beside identity, then removes stale Join', async () => {
    const join = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string) => reply(url.endsWith('/recovery') ? recovery :
      url.endsWith('/handoff/restore') ? restore : { ...copy, thisPcVersion: null })))
    const props = { profileId, available: true, onOpenPlay: join, serverState: 'Ready', connectedHostName: 'Friendly name' }
    const view = render(<FriendSharedWorlds {...props} serverStatusFresh />)
    await act(async () => {})
    expect(primaryActions()).toEqual([screen.getByRole('button', { name: 'Get latest save' })])
    expect(screen.getByRole('button', { name: 'Join connected Host' })).toHaveClass('secondary')
    expect(screen.getByLabelText('Current signed Host')).toContainElement(screen.getByRole('button', { name: 'Join connected Host' }))
    expect(screen.getByText('Signed Host not checked')).toBeInTheDocument()
    expect(join).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Join connected Host' }))
    expect(join).toHaveBeenCalledTimes(1)
    view.rerender(<FriendSharedWorlds {...props} serverStatusFresh={false} />)
    expect(screen.queryByRole('button', { name: 'Join connected Host' })).not.toBeInTheDocument()
    expect(primaryActions()).toEqual([screen.getByRole('button', { name: 'Get latest save' })])
    expect(screen.getByText('Saved Host: Friendly name')).toBeInTheDocument()
  })

  it.each([true, false])('prioritizes known different signed Host pairing independently of receiving consent: %s', async consented => {
    const pair = vi.fn()
    const join = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string) => reply(url.endsWith('/recovery') ?
      { ...recovery, state: 'ObservedMajority', majorityReached: true, candidateAddress: 'https://192.0.2.20:5131',
        candidateDeviceId: profileId, authorityHeadHash: 'A'.repeat(64) } :
      url.endsWith('/handoff/restore') ? restore : { ...copy, consented })))
    render(<FriendSharedWorlds profileId={profileId} available serverState="Ready" serverStatusFresh
      observedHostAddress="https://192.0.2.10:5131" onPairHost={pair} onOpenPlay={join} connectedHostName="Same nice name" />)
    await act(async () => {})
    expect(primaryActions()).toEqual([screen.getByRole('button', { name: 'Pair with the new Host' })])
    expect(screen.getByRole('button', { name: 'Host this save' })).toHaveClass('secondary')
    expect(screen.queryByRole('button', { name: 'Join connected Host' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Pair with the new Host' }))
    expect(pair).toHaveBeenCalledTimes(1)
    expect(join).not.toHaveBeenCalled()
  })

  it('gives exact fresh manual Start review sole priority over connected Ready, using navigation only', async () => {
    const navigate = vi.fn()
    const join = vi.fn()
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      return reply(url.endsWith('/recovery') ? { ...recovery, authorityHeadHash: 'A'.repeat(64) } :
        url.endsWith('/handoff/restore') ? { ...restore, staged: true, restored: true, recordHash: 'A'.repeat(64), readyForManualStart: true } : copy)
    }))
    render(<FriendSharedWorlds profileId={profileId} available serverState="Ready" serverStatusFresh
      onOpenPlay={join} onOpenHostControls={navigate} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Host this save' }))
    fireEvent.click(screen.getByText("Transfer the group's default hosting"))
    await act(async () => {})
    expect(primaryActions()).toEqual([screen.getByRole('button', { name: 'Review Start server' })])
    expect(screen.getByRole('button', { name: 'Join connected Host' })).toHaveClass('secondary')
    fireEvent.click(screen.getByRole('button', { name: 'Review Start server' }))
    expect(navigate).toHaveBeenCalledTimes(1)
    expect(join).not.toHaveBeenCalled()
    expect(mutations).toEqual([])
  })

  it('forwards heading suppression on Host while preserving signed identity and management', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => reply(url.endsWith('/handoff') ?
      { pending: false, code: 'None', message: 'No pending transfer.', successorDeviceId: null, finalVersion: null,
        receiptConfirmed: false, canComplete: false, canCancel: false } : url.endsWith('/governance') ?
        { revision: 1, ownerOverride: true } : { enabled: true, latest: null, error: null,
          authority: { state: 'NoTakeover', message: 'No transfer.', head: null, competingHeads: [], exactManagedProcessRunning: false } })))
    const props = { profileId, devices: [], rollingBackupEnabled: true, onGrantChanged: async () => {}, worldName: 'Alderheim' }
    const view = render(<HostSharedSaves {...props} showWorldHeading={false} />)
    await act(async () => {})
    expect(screen.queryByRole('heading', { name: 'Alderheim' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Shared saves' })).toBeInTheDocument()
    expect(screen.getByLabelText('Current signed Host')).toHaveTextContent('Original Host PC')
    expect(screen.getByRole('button', { name: 'Manage sharing' })).toBeEnabled()
    view.rerender(<HostSharedSaves {...props} />)
    expect(screen.getByRole('heading', { name: 'Alderheim' })).toBeInTheDocument()
  })

  it('keeps conflicting authority visible in Manage sharing even without receiving consent or a local copy', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => reply(url.endsWith('/recovery') ?
      { ...recovery, state: 'HistoryReviewRequired' } : url.endsWith('/handoff/restore') ? restore :
        { ...copy, consented: false, hostVersion: null, thisPcVersion: null, state: 'Consent off' })))
    render(<FriendSharedWorlds profileId={profileId} available />)
    await act(async () => {})
    expect(screen.getByText('Competing hosting histories need a group decision.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Manage sharing' }))
    const warning = within(screen.getByRole('dialog', { name: 'Manage sharing' })).getByText(/Competing authority histories need review/)
    expect(warning).toHaveAttribute('role', 'alert')
    expect(warning.closest('details')).toBeNull()
    expect(screen.queryByText('Choose the default Host after confirmed loss')).not.toBeInTheDocument()
  })

  it.each([
    ['Receiving', 'Receiving the save copy'], ['Verifying', 'Verifying the save copy'],
    ['Receipt', 'Confirming the signed receipt']
  ])('leads active %s with progress and blocks another Get while keeping consent withdrawal available', async (phase, heading) => {
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      return reply(url.endsWith('/recovery') ? recovery : url.endsWith('/handoff/restore') ? restore :
        { ...copy, state: phase, transferPhase: phase, receivedBytes: 64, totalBytes: 64 })
    }))
    render(<FriendSharedWorlds profileId={profileId} available serverState="Ready" serverStatusFresh onOpenPlay={vi.fn()} />)
    await act(async () => {})
    expect(screen.getByRole('heading', { name: heading })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Save transfer progress' })).toBeInTheDocument()
    expect(screen.getByRole('progressbar')).toHaveAttribute('value', '64')
    expect(screen.getByRole('button', { name: 'Get latest save' })).toBeDisabled()
    expect(screen.getByText(/Get latest save is unavailable while this copy/)).toBeInTheDocument()
    expect(screen.getByText(/Keep this app open\. Wait for verification and the signed receipt/)).toBeInTheDocument()
    expect(screen.getByLabelText('Allow saves on this PC')).toBeEnabled()
    expect(primaryActions()).toEqual([])
    expect(mutations).toEqual([])
  })
})

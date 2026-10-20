import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { HostSharedSaves } from './SharedWorldControls'
import { FriendLocalHosting, parseLocalHostingStatus, type LocalHostingStatus } from './FriendLocalHosting'
import { parsePlannedHandoffStatus, type PlannedHandoffStatus } from './plannedHandoff'
import type { Device } from './contracts'

const profileId = '11111111-1111-4111-8111-111111111111'
const deviceId = '22222222-2222-4222-8222-222222222222'
const requestId = '33333333-3333-4333-8333-333333333333'
const oldHash = 'A'.repeat(64)
const finalHash = 'B'.repeat(64)
const hostBase = `/api/local/profiles/${profileId}/shared-world`
const friendBase = `/api/local/friend/${profileId}/shared-world`
const noRequest: PlannedHandoffStatus = { pending: false, code: 'NoPendingHandoff', message: 'No hosting request.',
  successorDeviceId: null, requestId: null, recipientState: null, preparedVersionHash: null, finalVersion: null,
  receiptConfirmed: false, canComplete: false, canCancel: false }
const requested: PlannedHandoffStatus = { ...noRequest, requestId, successorDeviceId: deviceId,
  recipientState: 'Requested', canCancel: true, code: 'WaitingForRecipient', message: 'Waiting for your friend to accept.' }
const device: Device = { id: deviceId, profileId, assignedProfileIds: [profileId], name: 'Morgan PC',
  canStart: false, canStop: false, canExtendTimer: false, canViewLogs: false, saveReceiveProfileIds: [],
  revoked: false, paired: true, approvalPending: false, credentialExpiresUtc: null, lastHeartbeatUtc: null,
  serverPermissions: [], accessExpiresUtc: null, accessExpired: false, sharedWorldKeyEnrolled: true,
  sharedWorldGrants: { [profileId]: { receive: true, eligibleHost: true, recoveryVoter: false, manageSharing: false } } }
const hostStatus = { enabled: true, canManageSharing: true, latest: null, error: null }
const local: LocalHostingStatus = { ok: true, code: 'WaitingForRecipient', message: 'Review the hosting request.',
  allowed: true, consented: false, game: 'Valheim', name: 'Shared world', needsServerFile: false,
  needsPassword: false, serverFile: 'C:/Games/server.jar', gameVersion: '1.21', gamePort: 25565,
  controlPort: 5131, localProfileId: null, branchHash: null, readyForManualStart: false, running: false,
  steps: [
    { key: 'permission', state: 'Ready', message: 'Approved for this PC.' },
    { key: 'server', state: 'Ready', message: 'Installed server found.' },
    { key: 'save', state: 'Unknown', message: 'Prepared after acceptance.' },
    { key: 'ports', state: 'Unknown', message: 'Checked during preparation.' },
    { key: 'connection', state: 'Unknown', message: 'Incoming connection must be reviewed.' }
  ], handoffRequest: requested }
const response = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
const hostProps = { profileId, devices: [device], rollingBackupEnabled: true, onGrantChanged: async () => {} }
const friendProps = { profileId, connectionId: 'first', worldName: 'Shared world', available: true, onOpenHost: vi.fn() }
afterEach(() => vi.useRealTimers())

describe('hosting request contracts', () => {
  it.each(['Requested', 'Accepted', 'Declined', 'Prepared', 'Completed', 'Canceled'] as const)('reads %s without inferring a game launch', recipientState => {
    expect(parsePlannedHandoffStatus({ ...requested, recipientState }).recipientState).toBe(recipientState)
  })
  it.each([{ requestId: 'bad' }, { recipientState: 'Maybe' }, { requestId: null }, { successorDeviceId: null },
    { preparedVersionHash: 'bad' }, { finalVersion: 0 }])('rejects malformed request metadata %j', patch => {
    expect(() => parsePlannedHandoffStatus({ ...requested, ...patch })).toThrow()
  })
  it('permits planned manual Start only for completed signed preparation', () => {
    const ready = { ...local, consented: true, readyForManualStart: true, localProfileId: profileId,
      steps: local.steps.map(step => ({ ...step, state: 'Ready' })),
      handoffRequest: { ...requested, recipientState: 'Completed', preparedVersionHash: finalHash } }
    expect(parseLocalHostingStatus(ready).branchHash).toBeNull()
    expect(() => parseLocalHostingStatus({ ...ready, handoffRequest: requested })).toThrow()
  })
})

describe('owner guided move', () => {
  it('requests one paired PC without an address and stops only after that PC accepts and prepares', async () => {
    vi.useFakeTimers()
    let handoff = noRequest
    const writes: { url: string; body: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        writes.push({ url, body: init.body ? JSON.parse(String(init.body)) : null })
        if (url.endsWith('/request')) handoff = requested
        if (url.endsWith('/continue')) handoff = { ...requested, pending: true, finalVersion: 4,
          recipientState: 'Accepted', message: 'Preparing the final save.' }
        return response({ ok: true, code: handoff.code, message: handoff.message })
      }
      return response(url.endsWith('/handoff') ? handoff : url.endsWith('/governance') ?
        { revision: 1, ownerOverride: true, members: [] } : hostStatus)
    }))
    render(<HostSharedSaves {...hostProps} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Move hosting' }))
    const dialog = screen.getByRole('dialog', { name: 'Move hosting' })
    expect(within(dialog).queryByRole('textbox')).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('list')).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Send hosting request' })).toBeDisabled()
    fireEvent.change(within(dialog).getByLabelText('Next host PC'), { target: { value: deviceId } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Send hosting request' }))
    await act(async () => {})
    expect(writes).toEqual([{ url: `${hostBase}/handoff/request`, body: { successorDeviceId: deviceId } }])
    handoff = { ...requested, recipientState: 'Accepted' }
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(writes).toHaveLength(1)
    handoff = { ...requested, recipientState: 'Prepared', preparedVersionHash: oldHash }
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(writes[1]).toEqual({ url: `${hostBase}/handoff/continue`, body: { requestId } })
    await act(async () => { await vi.advanceTimersByTimeAsync(4000) })
    expect(writes).toHaveLength(2)
    expect(writes.some(item => item.url.endsWith('/start'))).toBe(false)
  })

  it('requires Resume after reopening and pauses a failed continuation without retries', async () => {
    vi.useFakeTimers()
    let handoff = requested
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) { writes.push(url); return response({ ok: false, code: 'SaveFailed', message: 'The final save needs review.' }) }
      return response(url.endsWith('/handoff') ? handoff : url.endsWith('/governance') ?
        { revision: 1, ownerOverride: true, members: [] } : hostStatus)
    }))
    render(<HostSharedSaves {...hostProps} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Move hosting' }))
    fireEvent.click(screen.getByRole('button', { name: 'Close Move hosting' }))
    handoff = { ...requested, recipientState: 'Prepared', preparedVersionHash: oldHash }
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(writes).toEqual([])
    fireEvent.click(screen.getByRole('button', { name: 'Move hosting' }))
    fireEvent.click(screen.getByRole('button', { name: 'Resume move' }))
    await act(async () => {})
    expect(screen.getByText('The final save needs review.')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(6000) })
    expect(writes).toEqual([`${hostBase}/handoff/continue`])
    expect(screen.getByRole('button', { name: 'Resume move' })).toBeEnabled()
  })

  it('withdraws a selected PC immediately when its current hosting grant changes', async () => {
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) writes.push(url)
      return response(url.endsWith('/handoff') ? noRequest : url.endsWith('/governance') ?
        { revision: 1, ownerOverride: true, members: [] } : hostStatus)
    }))
    const view = render(<HostSharedSaves {...hostProps} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Move hosting' }))
    fireEvent.change(screen.getByLabelText('Next host PC'), { target: { value: deviceId } })
    view.rerender(<HostSharedSaves {...hostProps} devices={[{ ...device, revoked: true }]} />)
    expect(screen.getByLabelText('Next host PC')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Send hosting request' })).toBeDisabled()
    expect(writes).toEqual([])
  })

  it('releases a stalled continuation for review without claiming that its Stop was canceled', async () => {
    vi.useFakeTimers()
    const handoff = { ...requested, recipientState: 'Prepared', preparedVersionHash: oldHash }
    let release!: (value: Response) => void
    let signal: AbortSignal | null | undefined
    let continues = 0
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        ++continues; signal = init.signal
        return new Promise<Response>(resolve => { release = resolve })
      }
      return response(url.endsWith('/handoff') ? handoff : url.endsWith('/governance') ?
        { revision: 1, ownerOverride: true, members: [] } : hostStatus)
    }))
    render(<HostSharedSaves {...hostProps} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Move hosting' }))
    fireEvent.click(screen.getByRole('button', { name: 'Resume move' }))
    await act(async () => { await vi.advanceTimersByTimeAsync(120001) })
    expect(signal?.aborted).toBe(true)
    expect(screen.getByText(/The server may already be stopped/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Close Move hosting' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Resume move' })).toBeEnabled()
    await act(async () => { release(response({ ok: true, code: 'WaitingForSuccessorCopy', message: 'Late preparation result.' })) })
    expect(screen.queryByText('Late preparation result.')).not.toBeInTheDocument()
    expect(continues).toBe(1)
  })

  it.each(['unavailable', 'sharing paused', 'grant withdrawn'] as const)('pauses the active move when current access becomes %s', async condition => {
    vi.useFakeTimers()
    let handoff = noRequest
    let blocked = false
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        writes.push(url); handoff = requested
        return response({ ok: true, code: 'WaitingForRecipient', message: 'Request sent.' })
      }
      if (url.endsWith('/handoff')) return response(handoff)
      if (url.endsWith('/governance')) return response({ revision: 1, ownerOverride: true, members: [] })
      if (blocked && condition === 'unavailable') throw new Error('Status unavailable.')
      return response({ ...hostStatus, enabled: !blocked || condition !== 'sharing paused' })
    }))
    const view = render(<HostSharedSaves {...hostProps} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Move hosting' }))
    fireEvent.change(screen.getByLabelText('Next host PC'), { target: { value: deviceId } })
    fireEvent.click(screen.getByRole('button', { name: 'Send hosting request' }))
    await act(async () => {})
    blocked = true
    handoff = { ...requested, recipientState: 'Prepared', preparedVersionHash: oldHash }
    if (condition === 'grant withdrawn') view.rerender(<HostSharedSaves {...hostProps}
      devices={[{ ...device, sharedWorldGrants: {} }]} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(6000) })
    expect(writes).toEqual([`${hostBase}/handoff/request`])
    const dialog = screen.getByRole('dialog', { name: 'Move hosting' })
    expect(within(dialog).getByRole('button', { name: 'Recheck request' })).toBeEnabled()
    expect(dialog).not.toHaveAttribute('aria-busy', 'true')
    expect(within(dialog).queryByText('Keep this dialog open. The move continues after your friend prepares the save.')).not.toBeInTheDocument()
  })

  it('withdraws stale request actions and ignores a stalled read when it eventually returns', async () => {
    vi.useFakeTimers()
    let reads = 0
    let release!: (value: Response) => void
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/handoff')) return ++reads === 1 ? response(noRequest) :
        new Promise<Response>(resolve => { release = resolve })
      return response(url.endsWith('/governance') ? { revision: 1, ownerOverride: true, members: [] } : hostStatus)
    }))
    render(<HostSharedSaves {...hostProps} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Move hosting' }))
    fireEvent.change(screen.getByLabelText('Next host PC'), { target: { value: deviceId } })
    await act(async () => { await vi.advanceTimersByTimeAsync(6001) })
    expect(screen.queryByRole('button', { name: 'Send hosting request' })).not.toBeInTheDocument()
    await act(async () => { release(response(noRequest)) })
    expect(screen.queryByRole('button', { name: 'Send hosting request' })).not.toBeInTheDocument()
  })
})

describe('receiving a hosting request', () => {
  it.each(['Declined', 'Canceled'] as const)('keeps ordinary local recovery consent separate after a %s request', async recipientState => {
    const writes: { url: string; body: Record<string, unknown> }[] = []
    const status = { ...local, handoffRequest: { ...requested, recipientState } }
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        writes.push({ url, body: JSON.parse(String(init.body)) })
        return response({ ...local, handoffRequest: null, consented: true, readyForManualStart: true,
          localProfileId: deviceId, branchHash: oldHash, steps: local.steps.map(step => ({ ...step, state: 'Ready' })) })
      }
      return response(status)
    }))
    render(<FriendLocalHosting {...friendProps} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
    expect(screen.getByRole('button', { name: 'Continue and start' })).toBeDisabled()
    fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC and host my own copy/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Continue and start' }))
    await waitFor(() => expect(writes.map(item => item.url)).toEqual([`${friendBase}/hosting/prepare`, `${friendBase}/hosting/start`]))
  })

  it('requires separate Minecraft terms consent and an explicit preparation action', async () => {
    let status = { ...local, game: 'MinecraftJava' }
    const writes: { url: string; body: Record<string, unknown> }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        writes.push({ url, body: JSON.parse(String(init.body)) })
        if (url.endsWith('/respond')) {
          status = { ...status, consented: true, handoffRequest: { ...requested, recipientState: 'Accepted' } }
          return response({ ok: true, code: 'HostingAccepted', message: 'Accepted.' })
        }
        return response({ ...status, handoffRequest: { ...requested, recipientState: 'Prepared', preparedVersionHash: oldHash } })
      }
      return response(status)
    }))
    render(<FriendLocalHosting {...friendProps} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Review hosting request' }))
    fireEvent.click(screen.getByRole('button', { name: 'Accept hosting request' }))
    const terms = await screen.findByRole('checkbox', { name: /I have read and accept the terms/ })
    expect(terms).not.toBeChecked()
    expect(screen.getByRole('button', { name: 'Continue and start' })).toBeDisabled()
    expect(writes).toHaveLength(1)
    fireEvent.click(terms)
    expect(writes).toHaveLength(1)
    fireEvent.click(screen.getByRole('button', { name: 'Continue and start' }))
    await waitFor(() => expect(writes[1]).toEqual({ url: `${friendBase}/hosting/prepare`, body: {
      requestId, acceptedTerms: true, allowReceiving: true, acceptSplitWarning: false, requirePeerConnection: true, serverFile: null, gamePassword: null
    } }))
  })

  it('offers explicit decline without enabling consent or preparing a save', async () => {
    let status = local
    const writes: { url: string; body: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        writes.push({ url, body: JSON.parse(String(init.body)) })
        status = { ...local, handoffRequest: { ...requested, recipientState: 'Declined' } }
        return response({ ok: true, code: 'HostingDeclined', message: 'Declined. The current Host was kept.' })
      }
      return response(status)
    }))
    render(<FriendLocalHosting {...friendProps} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Review hosting request' }))
    const dialog = screen.getByRole('dialog', { name: 'Hosting request' })
    expect(within(dialog).queryByRole('list')).not.toBeInTheDocument()
    expect(writes).toEqual([])
    fireEvent.click(within(dialog).getByRole('button', { name: 'Decline' }))
    await waitFor(() => expect(writes).toEqual([{ url: `${friendBase}/handoff/respond`, body: { requestId, accept: false } }]))
    expect(screen.queryByRole('button', { name: 'Start server on this PC' })).not.toBeInTheDocument()
    expect(status.consented).toBe(false)
  })

  it('accepts and prepares each exact save, then requires an explicit Start instruction', async () => {
    vi.useFakeTimers()
    let status = local
    const writes: { url: string; body: Record<string, unknown> }[] = []
    const openHost = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        writes.push({ url, body: JSON.parse(String(init.body)) })
        if (url.endsWith('/respond')) {
          status = { ...local, consented: true, handoffRequest: { ...requested, recipientState: 'Accepted' } }
          return response({ ok: true, code: 'HostingAccepted', message: 'Accepted.' })
        }
        if (url.endsWith('/hosting/prepare')) {
          const completed = status.handoffRequest?.recipientState === 'Completed'
          status = { ...status, readyForManualStart: completed, localProfileId: completed ? profileId : null,
            steps: local.steps.map(step => ({ ...step, state: 'Ready' })),
            handoffRequest: { ...status.handoffRequest!, recipientState: completed ? 'Completed' : 'Prepared',
              preparedVersionHash: status.handoffRequest?.finalVersion ? finalHash : oldHash } }
          return response(status)
        }
        if (url.endsWith('/hosting/start')) return response({ ok: true, code: 'Starting', message: 'Start requested.' })
        throw new Error(`Unexpected mutation ${url}`)
      }
      return response(status)
    }))
    render(<FriendLocalHosting {...friendProps} onOpenHost={openHost} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Review hosting request' }))
    fireEvent.click(screen.getByRole('button', { name: 'Accept hosting request' }))
    await act(async () => {})
    expect(writes.map(item => item.url)).toEqual([`${friendBase}/handoff/respond`, `${friendBase}/hosting/prepare`])
    expect(writes[1].body).toEqual({ requestId, acceptedTerms: false, allowReceiving: true, acceptSplitWarning: false, requirePeerConnection: true, serverFile: null, gamePassword: null })
    status = { ...status, handoffRequest: { ...requested, pending: true, finalVersion: 4, recipientState: 'Accepted' } }
    await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
    expect(writes.filter(item => item.url.endsWith('/hosting/prepare'))).toHaveLength(2)
    status = { ...status, handoffRequest: { ...status.handoffRequest!, pending: false, recipientState: 'Completed' } }
    await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
    expect(writes.filter(item => item.url.endsWith('/hosting/prepare'))).toHaveLength(3)
    expect(screen.getByRole('button', { name: 'Continue and start' })).toBeEnabled()
    expect(openHost).not.toHaveBeenCalled()
    expect(writes.some(item => item.url.endsWith('/start'))).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Continue and start' }))
    await act(async () => {})
    expect(openHost).toHaveBeenCalledExactlyOnceWith(profileId)
    expect(writes.filter(item => item.url.endsWith('/start'))).toHaveLength(1)
  })

  it('does not apply an acceptance response from a previously selected connection', async () => {
    let release!: (value: Response) => void
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) { writes.push(url); return new Promise<Response>(resolve => { release = resolve }) }
      return response(local)
    }))
    const view = render(<FriendLocalHosting {...friendProps} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Review hosting request' }))
    fireEvent.click(screen.getByRole('button', { name: 'Accept hosting request' }))
    view.rerender(<FriendLocalHosting {...friendProps} connectionId="second" />)
    view.rerender(<FriendLocalHosting {...friendProps} />)
    await act(async () => { release(response({ ok: true, code: 'HostingAccepted', message: 'Old acceptance.' })) })
    expect(writes).toEqual([`${friendBase}/handoff/respond`])
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.queryByText('Old acceptance.')).not.toBeInTheDocument()
  })

  it('bounds planned preparation and ignores its late ready result while keeping recheck available', async () => {
    vi.useFakeTimers()
    const accepted = { ...local, consented: true, handoffRequest: { ...requested, recipientState: 'Accepted' as const } }
    let release!: (value: Response) => void
    let signal: AbortSignal | null | undefined
    let preparations = 0
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
      if (init?.method) {
        ++preparations; signal = init.signal
        return new Promise<Response>(resolve => { release = resolve })
      }
      return response(accepted)
    }))
    render(<FriendLocalHosting {...friendProps} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Host on this PC' }))
    await act(async () => { await vi.advanceTimersByTimeAsync(120001) })
    expect(signal?.aborted).toBe(true)
    expect(screen.getByText(/Preparation did not finish checking in time/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retry automatic checks' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Close Host on this PC' })).toBeEnabled()
    await act(async () => { release(response({ ...accepted, readyForManualStart: true, localProfileId: profileId,
      steps: local.steps.map(step => ({ ...step, state: 'Ready' })),
      handoffRequest: { ...requested, recipientState: 'Completed', preparedVersionHash: finalHash } })) })
    expect(screen.queryByRole('button', { name: 'Review Start server' })).not.toBeInTheDocument()
    expect(preparations).toBe(1)
  })
})

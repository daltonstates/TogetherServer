import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FriendLocalHosting, parseLocalHostingStatus, type LocalHostingStatus } from './FriendLocalHosting'
import { parsePlannedHandoffStatus, type PlannedHandoffStatus } from './plannedHandoff'

const profileId = '11111111-1111-4111-8111-111111111111'
const deviceId = '22222222-2222-4222-8222-222222222222'
const requestId = '33333333-3333-4333-8333-333333333333'
const oldHash = 'A'.repeat(64)
const finalHash = 'B'.repeat(64)
const friendBase = `/api/local/friend/${profileId}/shared-world`
const noRequest: PlannedHandoffStatus = { pending: false, code: 'NoPendingHandoff', message: 'No hosting request.',
  successorDeviceId: null, requestId: null, recipientState: null, preparedVersionHash: null, finalVersion: null,
  receiptConfirmed: false, canComplete: false, canCancel: false }
const requested: PlannedHandoffStatus = { ...noRequest, requestId, successorDeviceId: deviceId,
  recipientState: 'Requested', canCancel: true, code: 'WaitingForRecipient', message: 'Waiting for your friend to accept.' }
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

// Existing signed move invitations remain readable, but do not restore the old
// owner grant matrix or introduce a second consent/preparation wizard.
describe('existing hosting invitations', () => {
  it('does not accept or prepare an invitation from polling', async () => {
    vi.useFakeTimers()
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) writes.push(url)
      return response(local)
    }))
    render(<FriendLocalHosting {...friendProps} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
    expect(writes).toEqual([])
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it.each([false, true])('carries one network confirmation through the exact final save (close: %s)', async close => {
    vi.useFakeTimers()
    let current = local
    const writes: { url: string; body: Record<string, unknown> }[] = []
    const openHost = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (!init?.method) return response(current)
      const body = JSON.parse(String(init.body))
      writes.push({ url, body })
      if (url.endsWith('/respond')) {
        current = { ...current, consented: true, handoffRequest: { ...requested, recipientState: 'Accepted' } }
        return response({ ok: true, code: 'HostingAccepted', message: 'Accepted.' })
      }
      if (url.endsWith('/prepare')) {
        const completed = current.handoffRequest?.recipientState === 'Completed'
        current = { ...current, readyForManualStart: completed, localProfileId: completed ? profileId : null,
          steps: local.steps.map(step => ({ ...step, state: 'Ready' })),
          handoffRequest: { ...current.handoffRequest!, recipientState: completed ? 'Completed' : 'Prepared',
            preparedVersionHash: completed ? finalHash : oldHash } }
        return response(current)
      }
      return response({ ok: true, code: 'Starting', message: 'Start requested.' })
    }))
    render(<FriendLocalHosting {...friendProps} onOpenHost={openHost} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
    await act(async () => {})
    const dialog = within(screen.getByRole('dialog', { name: 'Host locally' }))
    expect(dialog.queryByRole('checkbox')).not.toBeInTheDocument()
    fireEvent.click(dialog.getByRole('button', { name: 'Host locally' }))
    await act(async () => {})
    expect(writes.map(item => item.url)).toEqual([`${friendBase}/handoff/respond`, `${friendBase}/hosting/prepare`])
    if (close) fireEvent.click(dialog.getByRole('button', { name: 'Close Host locally' }))
    current = { ...current, handoffRequest: { ...current.handoffRequest!, pending: false, finalVersion: 4,
      recipientState: 'Completed', preparedVersionHash: finalHash } }
    await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
    if (close) {
      expect(writes).toHaveLength(2)
      expect(openHost).not.toHaveBeenCalled()
    } else {
      expect(writes.at(-1)).toEqual({ url: `${friendBase}/hosting/start`, body: {
        localProfileId: profileId, branchHash: null, requestId, acceptSplitWarning: false
      } })
      expect(openHost).toHaveBeenCalledExactlyOnceWith(profileId)
    }
  })

  it('does not apply acceptance from a previously selected connection', async () => {
    let release!: (value: Response) => void
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) { writes.push(url); return new Promise<Response>(resolve => { release = resolve }) }
      return response(local)
    }))
    const view = render(<FriendLocalHosting {...friendProps} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Host locally' }))
    await waitFor(() => expect(screen.getByRole('dialog')).not.toHaveAttribute('aria-busy', 'true'))
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
    view.rerender(<FriendLocalHosting {...friendProps} connectionId="second" />)
    await act(async () => { release(response({ ok: true, code: 'HostingAccepted', message: 'Previous acceptance.' })) })
    expect(writes).toEqual([`${friendBase}/handoff/respond`])
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.queryByText('Previous acceptance.')).not.toBeInTheDocument()
  })
})

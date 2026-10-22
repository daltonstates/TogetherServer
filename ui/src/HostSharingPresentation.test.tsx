import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { HostSharedSaves } from './SharedWorldControls'
import { HostSharingPermissions } from './HostSharingPermissions'
import type { Device } from './contracts'

const profileId = '11111111-1111-4111-8111-111111111111'
const deviceId = '22222222-2222-4222-8222-222222222222'
const base = `/api/local/profiles/${profileId}/shared-world`
const device: Device = { id: deviceId, profileId, assignedProfileIds: [profileId], name: 'Mira PC', canStart: false,
  canStop: false, canExtendTimer: false, canViewLogs: false, saveReceiveProfileIds: [], revoked: false, paired: true,
  approvalPending: false, credentialExpiresUtc: null, lastHeartbeatUtc: null, serverPermissions: [],
  accessExpiresUtc: null, accessExpired: false, sharedWorldKeyEnrolled: false }
const initial = { enabled: false, latest: null, error: null, canManageSharing: true }
const unpublished = { ...initial, enabled: true }
const latest = { number: 1, versionHash: 'A'.repeat(64), createdUtc: '2026-10-22T12:00:00Z', captureKind: 'PostStopBackup' }
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
const props = { profileId, devices: [device], rollingBackupEnabled: false, worldName: 'Alderheim', onGrantChanged: vi.fn(async () => {}) }
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

function setup(status = initial) {
  let current = status
  const writes: { url: string; body: unknown }[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) {
      const body = JSON.parse(String(init.body))
      writes.push({ url, body })
      current = { ...current, enabled: body.enabled }
      return reply({ ok: true, code: 'SharingSaved', message: 'Hosting access saved.' })
    }
    return reply(url.endsWith('/governance') ? null : current)
  }))
  render(<HostSharedSaves {...props} />)
  return writes
}

async function toggle() {
  const checkbox = screen.getByRole('checkbox', { name: 'Allow friends to host this server' })
  await waitFor(() => expect(checkbox).toBeEnabled())
  return checkbox
}

function setupPublication(getStatus: () => unknown, publish: (init: RequestInit) => Promise<Response>) {
  const writes: { url: string; method: string; body: unknown }[] = []
  const reads: string[] = []
  const onGrantChanged = vi.fn(async () => {})
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) {
      writes.push({ url, method: init.method, body: JSON.parse(String(init.body)) })
      return publish(init)
    }
    reads.push(url)
    return reply(url.endsWith('/governance') ? null : getStatus())
  }))
  const view = render(<HostSharedSaves {...props} onGrantChanged={onGrantChanged} />)
  return { ...view, writes, reads, onGrantChanged }
}

describe('one shared-hosting setting', () => {
  it('shows exactly one switch and no per-PC grant, voter, forwarding or move controls', async () => {
    const writes = setup()
    expect(await toggle()).not.toBeChecked()
    expect(screen.queryByRole('button', { name: 'Share latest backup' })).not.toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Shared saves' }).firstElementChild).toBe(screen.getByRole('region', { name: 'Shared hosting' }))
    expect(screen.getAllByRole('checkbox')).toHaveLength(1)
    expect(screen.queryByRole('button', { name: /Manage sharing|Move hosting|Review transfer/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Sharing details', { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
    fireEvent.click(screen.getByText('Sharing details', { selector: 'summary' }))
    const people = screen.getByLabelText('Friends with hosting access')
    expect(within(people).getByText('Mira PC')).toBeVisible()
    expect(within(people).queryByRole('checkbox')).not.toBeInTheDocument()
    expect(screen.getByText(/Copies already received stay on those PCs/)).toBeVisible()
    expect(screen.queryByText(/A real game join and saved restart still need separate testing/)).not.toBeInTheDocument()
    expect(writes).toEqual([])
  })

  it('enables every approved assigned friend with one exact request', async () => {
    const writes = setup()
    fireEvent.click(await toggle())
    await waitFor(() => expect(screen.getByRole('checkbox')).toBeChecked())
    expect(writes).toEqual([{ url: `${base}/hosting`, body: { enabled: true } }])
    expect(props.onGrantChanged).toHaveBeenCalled()
    expect(screen.getAllByRole('checkbox')).toHaveLength(1)
    expect(screen.getByText(/They can choose Host locally beside Start server/)).toBeInTheDocument()
  })

  it('turns hosting off through the same single setting', async () => {
    const writes = setup({ ...initial, enabled: true })
    fireEvent.click(await toggle())
    await waitFor(() => expect(screen.getByRole('checkbox')).not.toBeChecked())
    expect(writes).toEqual([{ url: `${base}/hosting`, body: { enabled: false } }])
  })

  it.each(['OldHostFenced', 'CompetingHistories', 'ReviewRequired'])('keeps %s protected with one blocker', async state => {
    const head = { groupId: profileId, epoch: 1, recordHash: 'A'.repeat(64), versionHash: 'B'.repeat(64),
      hostDeviceId: deviceId, hostPublicKey: 'verified-key', hostAddress: 'https://192.0.2.10:5131' }
    vi.stubGlobal('fetch', vi.fn(async (url: string) => reply(url.endsWith('/governance') ? null : {
      ...initial, enabled: true, authority: { state, message: 'History needs review.', head: state === 'OldHostFenced' ? head : null,
        competingHeads: state === 'CompetingHistories' ? [head, { ...head, recordHash: 'C'.repeat(64) }] : [], exactManagedProcessRunning: false }
    })))
    render(<HostSharedSaves {...props} />)
    const blocker = await screen.findByText(/This server has conflicting hosting histories/)
    expect(blocker).toHaveAttribute('role', 'alert')
    expect(screen.getByRole('checkbox')).toBeDisabled()
    expect(screen.queryByRole('button', { name: /Choose|Merge|Approve|Move/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Share latest backup' })).not.toBeInTheDocument()
  })

  it('does not offer the setting from an inherited Host without owner permission', async () => {
    setup({ ...initial, enabled: true, canManageSharing: false })
    expect(await screen.findByText("Change this setting on the original owner's PC.")).toBeInTheDocument()
    expect(screen.getByRole('checkbox')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Share latest backup' })).not.toBeInTheDocument()
  })

  it('keeps a refused change visible and does not write grants as a fallback', async () => {
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) { writes.push(url); return reply({ ok: false, code: 'ReviewRequired', message: 'Review the selected world.' }) }
      return reply(url.endsWith('/governance') ? null : initial)
    }))
    render(<HostSharedSaves {...props} />)
    fireEvent.click(await toggle())
    expect(await screen.findByText('Review the selected world.')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('checkbox')).toBeEnabled())
    expect(screen.getByRole('checkbox')).not.toBeChecked()
    expect(writes).toEqual([`${base}/hosting`])
  })

  it('ignores a late sharing result from the previous server', async () => {
    let release!: (value: Response) => void
    const nextId = '33333333-3333-4333-8333-333333333333'
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) return new Promise<Response>(resolve => { release = resolve })
      return reply(url.endsWith('/governance') ? null : initial)
    }))
    const view = render(<HostSharedSaves {...props} />)
    fireEvent.click(await toggle())
    view.rerender(<HostSharedSaves {...props} profileId={nextId} />)
    await toggle()
    await act(async () => { release(reply({ ok: true, code: 'SharingSaved', message: 'Previous server saved.' })) })
    expect(screen.queryByText('Previous server saved.')).not.toBeInTheDocument()
    expect(screen.getByRole('checkbox')).not.toBeChecked()
  })

  it.each([true, false])('leads directly with the policy switch and no governance heading (legacy heading requested: %s)', async showWorldHeading => {
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) writes.push(url)
      return reply(url.endsWith('/governance') ? null : initial)
    }))
    render(<HostSharedSaves {...props} showWorldHeading={showWorldHeading} serverState="Unknown" />)
    await toggle()
    const workspace = screen.getByRole('region', { name: 'Shared saves' })
    expect(workspace.firstElementChild).toBe(screen.getByRole('region', { name: 'Shared hosting' }))
    expect(screen.queryByRole('heading', { name: 'Alderheim' })).not.toBeInTheDocument()
    expect(within(workspace).queryByRole('heading')).not.toBeInTheDocument()
    expect(within(workspace).queryByLabelText('Approved Host')).not.toBeInTheDocument()
    expect(workspace).not.toHaveTextContent(/Host decision|Current Host status/)
    expect(writes).toEqual([])
  })
})

describe('sharing the latest backup', () => {
  it('requires an explicit request and prevents duplicate publication while pending', async () => {
    let release!: (value: Response) => void
    const { writes, reads, onGrantChanged } = setupPublication(() => unpublished,
      () => new Promise<Response>(resolve => { release = resolve }))
    const button = await screen.findByRole('button', { name: 'Share latest backup' })
    expect(screen.getByText('No completed save has been shared yet. Friends cannot receive a save until a backup is shared.')).toBeVisible()
    expect(writes).toEqual([])
    fireEvent.click(button)
    fireEvent.click(button)
    expect(button).toBeDisabled()
    expect(screen.getByRole('checkbox')).toBeDisabled()
    expect(writes).toEqual([{ url: `${base}/publish`, method: 'POST', body: {} }])
    await act(async () => { release(reply({ ok: false, code: 'NoBackup', message: 'No completed backup is available.' })) })
    expect(await screen.findByText('No completed backup is available.')).toBeVisible()
    await waitFor(() => expect(button).toBeEnabled())
    expect(reads.filter(url => url === base)).toHaveLength(2)
    expect(onGrantChanged).not.toHaveBeenCalled()
  })

  it('keeps a failed publication visible and retryable without changing permissions', async () => {
    const { writes, onGrantChanged } = setupPublication(() => ({ ...unpublished, error: 'The backup could not be checked.' }),
      async () => reply({ ok: false, code: 'InvalidSetup', message: 'Check the server player list, then try again.' }))
    const button = await screen.findByRole('button', { name: 'Share latest backup' })
    fireEvent.click(button)
    expect(await screen.findByText('Check the server player list, then try again.')).toBeVisible()
    await waitFor(() => expect(button).toBeEnabled())
    expect(screen.queryByText(/will be sent|Latest save available/)).not.toBeInTheDocument()
    fireEvent.click(button)
    await waitFor(() => expect(button).toBeEnabled())
    expect(writes).toEqual([
      { url: `${base}/publish`, method: 'POST', body: {} },
      { url: `${base}/publish`, method: 'POST', body: {} }
    ])
    expect(screen.getByRole('checkbox')).toBeChecked()
    expect(onGrantChanged).not.toHaveBeenCalled()
  })

  it('rereads a successful publication and distinguishes availability from confirmed receipt', async () => {
    let current: unknown = { ...unpublished, error: 'The earlier backup could not be shared.' }
    const { writes, reads, onGrantChanged } = setupPublication(() => current, async () => {
      current = { ...unpublished, latest, confirmedCopies: 0 }
      return reply({ ok: true, code: 'Published', message: 'The backup was shared.' })
    })
    fireEvent.click(await screen.findByRole('button', { name: 'Share latest backup' }))
    expect(await screen.findByText('Latest save available. No PCs have confirmed receipt yet.')).toBeVisible()
    expect(screen.queryByText('The earlier backup could not be shared.')).not.toBeInTheDocument()
    expect(screen.queryByText('The backup was shared.')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Share latest backup' })).not.toBeInTheDocument()
    expect(screen.getByRole('checkbox')).toBeChecked()
    expect(reads.filter(url => url === base)).toHaveLength(2)
    expect(writes).toEqual([{ url: `${base}/publish`, method: 'POST', body: {} }])
    expect(onGrantChanged).not.toHaveBeenCalled()
  })

  it('offers recovery for a newer publication error without treating an older shared save as success', async () => {
    const { writes } = setupPublication(() => ({ ...unpublished, latest, confirmedCopies: 1, error: 'The newer backup could not be shared.' }),
      async () => reply({ ok: false, code: 'InvalidSetup', message: 'Review the setup.' }))
    expect(await screen.findByRole('button', { name: 'Share latest backup' })).toBeEnabled()
    expect(screen.getByText('The newer backup could not be shared.')).toBeVisible()
    expect(screen.getByText('Save sharing needs attention. Check the error and try sharing the latest backup again.')).toBeVisible()
    expect(screen.queryByText(/Latest save available|will be sent/)).not.toBeInTheDocument()
    expect(writes).toEqual([])
  })

  it('polls available status without publishing a backup automatically', async () => {
    vi.useFakeTimers()
    const { writes, reads } = setupPublication(() => unpublished,
      async () => reply({ ok: true, code: 'Published', message: 'The backup was shared.' }))
    await act(async () => {})
    expect(screen.getByRole('button', { name: 'Share latest backup' })).toBeEnabled()
    await act(async () => { await vi.advanceTimersByTimeAsync(20000) })
    expect(reads.filter(url => url === base)).toHaveLength(3)
    expect(writes).toEqual([])
  })

  it('aborts an unconfirmed publication after the timeout and leaves a visible retry', async () => {
    vi.useFakeTimers()
    let signal: AbortSignal | null | undefined
    const { writes, onGrantChanged } = setupPublication(() => unpublished, init => {
      signal = init.signal
      return new Promise<Response>((_resolve, reject) => {
        signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true })
      })
    })
    await act(async () => {})
    const button = screen.getByRole('button', { name: 'Share latest backup' })
    fireEvent.click(button)
    expect(button).toBeDisabled()
    await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
    expect(signal?.aborted).toBe(true)
    expect(screen.getByText('Sharing could not be checked. Refresh sharing and try again.')).toBeVisible()
    expect(button).toBeEnabled()
    expect(writes).toEqual([{ url: `${base}/publish`, method: 'POST', body: {} }])
    expect(onGrantChanged).not.toHaveBeenCalled()
  })

  it('ignores a late publication result and read for the previous server', async () => {
    let release!: (value: Response) => void
    let signal: AbortSignal | null | undefined
    const nextId = '33333333-3333-4333-8333-333333333333'
    const writes: string[] = []
    const reads: string[] = []
    const onGrantChanged = vi.fn(async () => {})
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        writes.push(url); signal = init.signal
        return new Promise<Response>(resolve => { release = resolve })
      }
      reads.push(url)
      return reply(url.endsWith('/governance') ? null : url === base ? unpublished : initial)
    }))
    const view = render(<HostSharedSaves {...props} onGrantChanged={onGrantChanged} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Share latest backup' }))
    view.rerender(<HostSharedSaves {...props} profileId={nextId} onGrantChanged={onGrantChanged} />)
    await toggle()
    expect(signal?.aborted).toBe(true)
    await act(async () => { release(reply({ ok: true, code: 'Published', message: 'Previous server backup shared.' })) })
    expect(screen.queryByText('Previous server backup shared.')).not.toBeInTheDocument()
    expect(screen.getByRole('checkbox')).not.toBeChecked()
    expect(screen.queryByRole('button', { name: 'Share latest backup' })).not.toBeInTheDocument()
    expect(reads.filter(url => url === base)).toHaveLength(1)
    expect(writes).toEqual([`${base}/publish`])
    expect(onGrantChanged).not.toHaveBeenCalled()
  })
})

it('lists only approved active assigned friends without exposing a second setting', () => {
  render(<HostSharingPermissions profileId={profileId} enabled members={[]} devices={[device,
    { ...device, id: 'revoked', name: 'Removed PC', revoked: true },
    { ...device, id: 'pending', name: 'Pending PC', approvalPending: true },
    { ...device, id: 'expired', name: 'Expired PC', accessExpired: true }]} />)
  expect(screen.getByText('Mira PC')).toBeVisible()
  expect(screen.getByText('Hosting allowed')).toBeVisible()
  expect(screen.queryByText(/Removed PC|Pending PC|Expired PC/)).not.toBeInTheDocument()
  expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
})

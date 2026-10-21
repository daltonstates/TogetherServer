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

describe('one shared-hosting setting', () => {
  it('shows exactly one switch and no per-PC grant, voter, forwarding or move controls', async () => {
    const writes = setup()
    expect(await toggle()).not.toBeChecked()
    expect(screen.getAllByRole('checkbox')).toHaveLength(1)
    expect(screen.queryByRole('button', { name: /Manage sharing|Move hosting|Review transfer/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByText('Sharing details', { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
    fireEvent.click(screen.getByText('Sharing details', { selector: 'summary' }))
    const people = screen.getByLabelText('Friends with hosting access')
    expect(within(people).getByText('Mira PC')).toBeVisible()
    expect(within(people).queryByRole('checkbox')).not.toBeInTheDocument()
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
  })

  it('does not offer the setting from an inherited Host without owner permission', async () => {
    setup({ ...initial, enabled: true, canManageSharing: false })
    expect(await screen.findByText("Change this setting on the original owner's PC.")).toBeInTheDocument()
    expect(screen.getByRole('checkbox')).toBeDisabled()
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

  it('preserves heading suppression and keeps automatic checks read-only', async () => {
    const writes: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) writes.push(url)
      return reply(url.endsWith('/governance') ? null : initial)
    }))
    render(<HostSharedSaves {...props} showWorldHeading={false} />)
    await toggle()
    expect(screen.queryByRole('heading', { name: 'Alderheim' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Shared saves' })).toBeInTheDocument()
    expect(writes).toEqual([])
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

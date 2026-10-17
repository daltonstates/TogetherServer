import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { HostSharedSaves } from './SharedWorldControls'
import { HostSharingPermissions } from './HostSharingPermissions'
import type { Device } from './contracts'

const profileId = '11111111-1111-4111-8111-111111111111'
const deviceId = '22222222-2222-4222-8222-222222222222'
const base = `/api/local/profiles/${profileId}/shared-world`
const grantsPath = `/api/local/devices/${deviceId}/shared-world/${profileId}/grants`
const emptyGrants = { receive: false, eligibleHost: false, recoveryVoter: false, manageSharing: false, reshare: false }
type Grants = typeof emptyGrants
const friend: Device = { id: deviceId, profileId, assignedProfileIds: [profileId], name: 'Mira PC',
  canStart: false, canStop: false, canExtendTimer: false, canViewLogs: false, revoked: false,
  paired: true, approvalPending: false, credentialExpiresUtc: null, lastHeartbeatUtc: null,
  serverPermissions: [], accessExpiresUtc: null, accessExpired: false, sharedWorldKeyEnrolled: true,
  sharedWorldGrants: { [profileId]: { ...emptyGrants } } }
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
const noHandoff = { pending: false, code: 'NoPendingHandoff', message: 'No pending handoff.',
  successorDeviceId: null, finalVersion: null, receiptConfirmed: false, canComplete: false, canCancel: false }

function setup({ enabled = true, canManage = true, authority = null, signedGrants = emptyGrants,
  device = friend, rollingBackupEnabled = true }:
  { enabled?: boolean; canManage?: boolean; authority?: unknown; signedGrants?: Grants;
    device?: Device; rollingBackupEnabled?: boolean } = {}) {
  const status = { enabled, canManageSharing: canManage, authority, confirmedCopies: 1, error: null,
    latest: { number: 3, versionHash: 'A'.repeat(64), createdUtc: '2020-01-01T00:00:00Z', captureKind: 'PostStopBackup' },
    liveSave: { available: false, message: 'This installation needs its private save test.', code: 'GameAcceptanceRequired', game: 'Valheim' } }
  const roster = { revision: 7, ownerOverride: true,
    members: [{ deviceId, grants: { ...signedGrants }, revoked: false, accessExpiresUtc: null }] }
  const mutations: { path: string; method: string; body: unknown }[] = []
  const reads: string[] = []
  const changed = vi.fn().mockResolvedValue(undefined)
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) {
      const body: unknown = init.body == null ? null : JSON.parse(String(init.body))
      mutations.push({ path: url, method: init.method, body })
      if (url === grantsPath) roster.members[0].grants = (body as { grants: Grants }).grants
      else if (url === base) status.enabled = (body as { enabled: boolean }).enabled
      else if (url === `${base}/governance`) {
        roster.ownerOverride = (body as { ownerOverride: boolean }).ownerOverride
        return reply(roster)
      } else if (url === `/api/local/devices/${deviceId}/shared-world/re-enroll`)
        roster.members[0].grants = { ...emptyGrants }
      else throw new Error(`Unexpected mutation ${init.method} ${url}`)
      return reply({ ok: true, code: 'SharingSaved', message: 'The selected sharing change was saved.' })
    }
    reads.push(url)
    if (url === `${base}/governance`) return reply(roster)
    if (url === `${base}/handoff`) return reply(noHandoff)
    if (url === `${base}/resolution/offers`) return reply([{ proposalHash: 'B'.repeat(64), selectedVersion: 3, competingBranches: 2 }])
    if (url === `${base}/live-orphan`) return reply({ code: 'None', versionHash: null, message: 'No interrupted live file copy is retained.' })
    if (url === base) return reply(status)
    throw new Error(`Unexpected read ${url}`)
  }))
  render(<HostSharedSaves profileId={profileId} devices={[device]} rollingBackupEnabled={rollingBackupEnabled}
    currentAddress="https://192.0.2.10:5131" worldName="Alderheim" onGrantChanged={changed} />)
  return { status, roster, mutations, reads, changed }
}

async function openSharing() {
  await waitFor(() => expect(screen.getByText('Sharing on')).toBeInTheDocument())
  const opener = screen.getByRole('button', { name: 'Manage sharing' })
  opener.focus()
  fireEvent.click(opener)
  return { dialog: screen.getByRole('dialog', { name: 'Manage sharing' }), opener }
}

afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('Host sharing presentation', () => {
  it('opens People first with separate everyday grants and keeps advanced controls out of the initial view', async () => {
    const { mutations } = setup()
    expect(screen.queryByRole('region', { name: 'Live save sharing' })).not.toBeInTheDocument()
    const { dialog, opener } = await openSharing()
    const navigation = within(dialog).getByRole('navigation', { name: 'Sharing sections' })
    expect(within(navigation).getByRole('button', { name: 'People' })).toHaveAttribute('aria-current', 'page')
    expect(within(navigation).getAllByRole('button')).toHaveLength(3)
    expect(within(dialog).getByRole('checkbox', { name: 'Share completed saves from this server' })).toBeChecked()
    const person = within(dialog).getByRole('article', { name: 'Mira PC sharing permissions' })
    expect(within(person).getByText('Signing identity enrolled')).toBeInTheDocument()
    expect(within(person).getByText('Receive saves')).toBeInTheDocument()
    expect(within(person).getByText('Host on this PC')).toBeInTheDocument()
    expect(within(person).getByRole('checkbox', { name: 'Receive for Mira PC' })).toBeEnabled()
    expect(within(person).getByRole('checkbox', { name: 'Eligible host for Mira PC' })).not.toBeChecked()
    expect(within(person).getByRole('checkbox', { name: 'Forward verified saves for Mira PC' })).not.toBeVisible()
    expect(within(person).getByText('More permissions for Mira PC').closest('details')).not.toHaveAttribute('open')
    expect(within(dialog).queryByRole('region', { name: 'Resolve competing copies' })).not.toBeInTheDocument()
    expect(mutations).toEqual([])
    fireEvent.click(within(dialog).getByRole('button', { name: 'Done' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(opener).toHaveFocus()
  })

  it('saves only the chosen Receive grant using the current signed permissions, without granting hosting or local consent', async () => {
    const signedGrants = { ...emptyGrants, recoveryVoter: true }
    const { mutations, changed } = setup({ signedGrants })
    const { dialog } = await openSharing()
    const receive = within(dialog).getByRole('checkbox', { name: 'Receive for Mira PC' })
    fireEvent.click(receive)
    expect(within(dialog).getByRole('button', { name: 'Done' })).toBeDisabled()
    expect(within(dialog).getByRole('checkbox', { name: 'Eligible host for Mira PC' })).toBeDisabled()
    await waitFor(() => expect(receive).toBeChecked())
    expect(mutations).toEqual([{ path: grantsPath, method: 'PUT', body: {
      grants: { ...signedGrants, receive: true },
    } }])
    expect(changed).toHaveBeenCalledTimes(1)
    expect(within(dialog).getByRole('checkbox', { name: 'Eligible host for Mira PC' })).not.toBeChecked()
    fireEvent.click(within(dialog).getByText('More permissions for Mira PC'))
    expect(within(dialog).getByRole('checkbox', { name: 'Recovery voter for Mira PC' })).toBeChecked()
    expect(within(dialog).getByRole('checkbox', { name: 'Forward verified saves for Mira PC' })).not.toBeChecked()
    expect(within(dialog).getByText(/Receiving stays off until that PC's owner opts in/)).toBeInTheDocument()
  })

  it('keeps completed-save enablement separate from every per-PC grant', async () => {
    const { mutations } = setup({ enabled: false })
    await screen.findByText('Sharing off')
    fireEvent.click(screen.getByRole('button', { name: 'Manage sharing' }))
    const dialog = screen.getByRole('dialog', { name: 'Manage sharing' })
    const sharing = within(dialog).getByRole('checkbox', { name: 'Share completed saves from this server' })
    expect(sharing).not.toBeChecked()
    expect(within(dialog).getByRole('checkbox', { name: 'Receive for Mira PC' })).toBeDisabled()
    fireEvent.click(sharing)
    await waitFor(() => expect(sharing).toBeChecked())
    expect(mutations).toEqual([{ path: base, method: 'PUT', body: { enabled: true } }])
    expect(within(dialog).getByRole('checkbox', { name: 'Receive for Mira PC' })).not.toBeChecked()
    expect(within(dialog).getByRole('checkbox', { name: 'Eligible host for Mira PC' })).not.toBeChecked()
  })

  it('keeps forwarding and owner decision changes explicit in their own advanced controls', async () => {
    const signedGrants = { ...emptyGrants, receive: true, recoveryVoter: true }
    const { mutations, roster } = setup({ signedGrants })
    const { dialog } = await openSharing()
    fireEvent.click(within(dialog).getByText('More permissions for Mira PC'))
    const forwarding = within(dialog).getByRole('checkbox', { name: 'Forward verified saves for Mira PC' })
    fireEvent.click(forwarding)
    await waitFor(() => expect(forwarding).toBeChecked())
    expect(mutations).toEqual([{ path: grantsPath, method: 'PUT', body: { grants: { ...signedGrants, reshare: true } } }])
    expect(within(dialog).getByRole('checkbox', { name: 'Eligible host for Mira PC' })).not.toBeChecked()
    fireEvent.click(within(dialog).getByText('Owner decision rules'))
    const override = within(dialog).getByRole('checkbox', { name: 'Owner has final say on save conflicts' })
    expect(override).toBeChecked()
    expect(roster.ownerOverride).toBe(true)
    fireEvent.click(override)
    await waitFor(() => expect(override).not.toBeChecked())
    expect(mutations[1]).toEqual({ path: `${base}/governance`, method: 'PUT', body: { ownerOverride: false } })
    expect(mutations).toHaveLength(2)
  })

  it.each(['expired PC', 'read-only Host', 'fenced Host', 'history review'] as const)(
    'preserves permission guards for an %s while keeping saved grants visible', async condition => {
      const head = { groupId: profileId, epoch: 2, recordHash: 'C'.repeat(64), versionHash: 'A'.repeat(64),
        hostDeviceId: deviceId, hostPublicKey: 'synthetic-host-key', hostAddress: 'https://192.0.2.10:5131' }
      const authority = condition === 'fenced Host' ? { state: 'OldHostFenced', message: 'Another PC has the signed hosting decision.',
        head, competingHeads: [], exactManagedProcessRunning: false } : condition === 'history review' ?
        { state: 'ReviewRequired', message: 'Review the signed histories.', head: null, competingHeads: [], exactManagedProcessRunning: false } : null
      const { mutations } = setup({ device: { ...friend, accessExpired: condition === 'expired PC' },
        canManage: condition !== 'read-only Host', authority, signedGrants: { ...emptyGrants, receive: true } })
      const { dialog } = await openSharing()
      expect(within(dialog).getByRole('checkbox', { name: 'Receive for Mira PC' })).toBeChecked()
      expect(within(dialog).getByRole('checkbox', { name: 'Receive for Mira PC' })).toBeDisabled()
      expect(within(dialog).getByRole('checkbox', { name: 'Eligible host for Mira PC' })).toBeDisabled()
      fireEvent.click(within(dialog).getByText('More permissions for Mira PC'))
      expect(within(dialog).getByRole('checkbox', { name: 'Forward verified saves for Mira PC' })).toBeDisabled()
      if (condition !== 'expired PC') expect(within(dialog).getByRole('checkbox', { name: 'Share completed saves from this server' })).toBeDisabled()
      expect(mutations).toEqual([])
    })

  it('mounts live controls only in Save sharing and leaves history browsing free of lifecycle changes', async () => {
    const { mutations, reads } = setup()
    const { dialog } = await openSharing()
    expect(within(dialog).queryByRole('region', { name: 'Live save sharing' })).not.toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save sharing' }))
    expect(within(dialog).getByRole('region', { name: 'Live save sharing' })).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Share current address' })).toBeEnabled()
    expect(within(dialog).queryByRole('article', { name: 'Mira PC sharing permissions' })).not.toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('button', { name: 'History & recovery' }))
    expect(within(dialog).queryByRole('region', { name: 'Live save sharing' })).not.toBeInTheDocument()
    expect(within(dialog).getByText(/Choose a group's default session separately in World groups/)).toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Check owner decisions' }))
    expect(await within(dialog).findByText(/2 signed branches/)).toBeInTheDocument()
    expect(reads).toContain(`${base}/resolution/offers`)
    expect(within(dialog).getByText('Receipts and diagnostics').closest('details')).not.toHaveAttribute('open')
    expect(mutations).toEqual([])
    fireEvent.click(within(dialog).getByRole('button', { name: 'Move hosting' }))
    expect(screen.queryByRole('dialog', { name: 'Manage sharing' })).not.toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Move hosting' })).toBeInTheDocument()
    expect(screen.getAllByRole('dialog')).toHaveLength(1)
    expect(mutations).toEqual([])
  })

  it('retains signing identity reset as a separate explicit action without widening grants', async () => {
    const { mutations, changed } = setup({ signedGrants: { ...emptyGrants, receive: true } })
    const { dialog } = await openSharing()
    fireEvent.click(within(dialog).getByText('Signing identity for Mira PC'))
    expect(mutations).toEqual([])
    fireEvent.click(within(dialog).getByRole('button', { name: "Reset Mira PC's signing identity and grants" }))
    await waitFor(() => expect(changed).toHaveBeenCalledTimes(1))
    expect(mutations).toEqual([{ path: `/api/local/devices/${deviceId}/shared-world/re-enroll`, method: 'POST', body: null }])
    await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: 'Receive for Mira PC' })).not.toBeChecked())
  })

  it('leads with current enrolled PCs and explains why saved grants for a pending PC are not active yet', () => {
    const pending = { ...friend, id: '33333333-3333-4333-8333-333333333333', name: 'Pending PC', sharedWorldKeyEnrolled: false }
    const expired = { ...friend, id: '44444444-4444-4444-8444-444444444444', name: 'Expired PC', accessExpired: true }
    const change = vi.fn().mockResolvedValue(undefined)
    render(<HostSharingPermissions profileId={profileId} devices={[expired, pending, friend]}
      members={[{ deviceId: pending.id, grants: { ...emptyGrants, receive: true, eligibleHost: true },
        revoked: false, accessExpiresUtc: null }]} busy={false} enabled canManage blocked={false}
      onChange={change} onResetKey={change} />)
    const people = screen.getAllByRole('article')
    expect(people.map(person => person.getAttribute('aria-label'))).toEqual([
      'Mira PC sharing permissions', 'Pending PC sharing permissions', 'Expired PC sharing permissions',
    ])
    const pendingCard = screen.getByRole('article', { name: 'Pending PC sharing permissions' })
    expect(within(pendingCard).getByText('Signing identity pending')).toBeInTheDocument()
    expect(within(pendingCard).getByRole('checkbox', { name: 'Receive for Pending PC' })).toBeChecked()
    expect(within(pendingCard).getByRole('checkbox', { name: 'Eligible host for Pending PC' })).toBeChecked()
    expect(within(pendingCard).getByText(/Checked permissions are saved choices, awaiting your friend's signing enrollment/)).toHaveTextContent('open TogetherServer and connect to this Host')
    expect(within(pendingCard).getByText(/Receiving also needs their local consent/)).toBeInTheDocument()
    expect(change).not.toHaveBeenCalled()
  })
})

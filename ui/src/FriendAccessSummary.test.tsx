import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { Device } from './contracts'
import { permissionPresetRequest } from './permissionState'
import {
  FriendAccessReview, FriendAccessSummary, FriendPcFilters, defaultFriendPcFilters,
  effectiveDeviceAccess, filterFriendPcs, reviewFriendAccess
} from './FriendAccessSummary'

const nowMs = Date.parse('2026-10-08T12:00:00Z')
const servers = [{ id: 'one', name: 'Valheim evening' }, { id: 'two', name: 'Minecraft weekend' }, { id: 'three', name: 'New world' }]
const device: Device = {
  id: 'pc-1', profileId: 'one', assignedProfileIds: ['one', 'two'], name: 'Living room PC',
  canStart: true, canStop: false, canExtendTimer: false, canViewLogs: false,
  revoked: false, paired: true, approvalPending: false, credentialExpiresUtc: '2026-11-08T12:00:00Z',
  accessExpiresUtc: null, accessExpired: false, lastHeartbeatUtc: '2026-10-08T11:59:55Z',
  serverPermissions: [{ profileId: 'two', canStart: false, canStop: true, canExtendTimer: true, canViewLogs: true }]
}

describe('Friend access summary and review', () => {
  it('finds a Friend by their shared person name without losing PC label search', () => {
    const named = { ...device, profileName: 'Sam Rivera' }
    for (const search of ['sam rivera', ' LIVING ROOM '])
      expect(filterFriendPcs([named], servers, { ...defaultFriendPcFilters, search }, nowMs)).toEqual([named])
  })
  it('preserves mixed exact-server grants instead of treating Start as a log or timer grant', () => {
    const view = effectiveDeviceAccess(device, servers, nowMs)
    expect(view.rows[0]).toMatchObject({ exception: false, effectivePermissions: { canStart: true, canStop: false, canViewLogs: false, canExtendTimer: false } })
    expect(view.rows[1]).toMatchObject({ exception: true, effectivePermissions: { canStart: false, canStop: true, canViewLogs: true, canExtendTimer: true } })
    render(<FriendAccessSummary device={device} servers={servers} nowMs={nowMs} />)
    expect(screen.getByRole('status')).toHaveTextContent(/Start: 1\/2.*Guarded Stop: 1\/2.*Restart: 0\/2.*Add time: 1\/2.*Logs: 1\/2/)
    expect(screen.getByText(/No owner access deadline/)).toBeInTheDocument()
  })

  it('keeps an active helper overlay scoped to actual assignments and actual returned grants', () => {
    const view = effectiveDeviceAccess({ ...device, temporaryHelperActive: true, temporaryHelperUntilUtc: '2026-10-08T13:00:00Z' }, servers, nowMs)
    expect(view.helper).toBe('Active')
    expect(view.rows.map(row => row.profileId)).toEqual(['one', 'two'])
    expect(view.rows[0].effectivePermissions?.canViewLogs).toBe(false)
    expect(view.rows.every(row => !row.exception)).toBe(true)
  })

  it('does not reconstruct saved permissions after a projected helper expires at the exact deadline', () => {
    const helperDevice = { ...device, canStart: true, canStop: true, canExtendTimer: true, canViewLogs: true,
      temporaryHelperActive: true, temporaryHelperUntilUtc: '2026-10-08T12:00:00Z' }
    const view = effectiveDeviceAccess(helperDevice, servers, nowMs)
    expect(view.state).toBe('RefreshNeeded')
    expect(view.rows.every(row => row.effectivePermissions === null)).toBe(true)
    render(<FriendAccessSummary device={helperDevice} servers={servers} nowMs={nowMs} />)
    expect(screen.getByText(/cannot infer them/)).toBeInTheDocument()
    expect(screen.queryByText(/Start: 2\/2/)).not.toBeInTheDocument()
  })

  it('does not let a still-projected helper overlay bypass owner-ended access', () => {
    const expired = { ...device, temporaryHelperActive: true, temporaryHelperUntilUtc: '2026-10-08T13:00:00Z', accessExpired: true }
    const view = effectiveDeviceAccess(expired, servers, nowMs)
    expect(view.state).toBe('AccessExpired')
    expect(view.rows[0].effectivePermissions?.canStart).toBe(false)
    expect(filterFriendPcs([expired], servers, { ...defaultFriendPcFilters, status: 'helper' }, nowMs)).toEqual([])
    render(<FriendAccessSummary device={expired} servers={servers} nowMs={nowMs} />)
    expect(screen.getByText(/device access currently blocks its use/)).toBeInTheDocument()
  })

  it.each([
    [{ revoked: true }, 'Revoked'], [{ approvalPending: true }, 'ApprovalPending'], [{ paired: false }, 'Unpaired'],
    [{ accessExpiresUtc: '2026-10-08T12:00:00Z' }, 'AccessExpired'],
    [{ credentialExpiresUtc: '2026-10-08T12:00:00Z' }, 'CredentialExpired']
  ] as const)('blocks displayed effective grants for %j while preserving assignments', (changed, expected) => {
    const view = effectiveDeviceAccess({ ...device, ...changed }, servers, nowMs)
    expect(view.state).toBe(expected)
    expect(view.rows).toHaveLength(2)
    expect(view.rows.every(row => row.effectivePermissions && Object.values(row.effectivePermissions).every(value => value === false))).toBe(true)
    expect(view.rows[0].observedPermissions.canStart).toBe(true)
  })

  it('keeps malformed expiry Unknown and stale heartbeat separate from configured access', () => {
    const unknown = effectiveDeviceAccess({ ...device, accessExpiresUtc: 'invalid' }, servers, nowMs)
    expect(unknown.state).toBe('Unknown')
    expect(unknown.rows.every(row => row.effectivePermissions === null)).toBe(true)
    const stale = effectiveDeviceAccess({ ...device, lastHeartbeatUtc: '2026-10-08T11:59:00Z' }, servers, nowMs)
    expect(stale.connected).toBe(false)
    expect(stale.state).toBe('Available')
  })

  it('reviews assignment removals/additions and exact permission changes without mutating either input', () => {
    const before = structuredClone(device)
    const after = { ...device, assignedProfileIds: ['two', 'three'], serverPermissions: [], ...permissionPresetRequest('start') }
    const review = reviewFriendAccess(device, after, servers)
    expect(review.rows.map(row => [row.profileId, row.change])).toEqual([['one', 'Removed'], ['two', 'Changed'], ['three', 'Added']])
    expect(review.rows[0].afterAssigned).toBe(false)
    expect(review.rows[1].afterPermissions).toEqual({ canStart: true, canStop: false, canExtendTimer: false, canViewLogs: false })
    expect(device).toEqual(before)
    render(<FriendAccessReview before={device} after={after} servers={servers} clearsExceptions />)
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Server access before and after' })).toBeInTheDocument()
    const removed = screen.getByRole('group', { name: 'Valheim evening access change' })
    expect(within(removed).getByText('Removed')).toBeInTheDocument()
    expect(within(removed).getByRole('group', { name: 'After permissions' })).toHaveTextContent('Not assigned')
    const changed = screen.getByRole('group', { name: 'Minecraft weekend access change' })
    const logs = within(changed).getByRole('listitem', { name: 'View logs permission change' })
    expect(within(logs).getByLabelText('Before: Allowed')).toBeInTheDocument()
    expect(within(logs).getByLabelText('After: Off')).toBeInTheDocument()
    const added = screen.getByRole('group', { name: 'New world access change' })
    expect(within(added).getByText('Added')).toBeInTheDocument()
    expect(within(added).getByRole('group', { name: 'Before permissions' })).toHaveTextContent('Not assigned')
    expect(within(added).getByRole('group', { name: 'After permissions' })).toHaveTextContent('Start')
    expect(screen.getByText(/clears per-server exceptions/)).toBeInTheDocument()
  })

  it('shows identical defaults once and groups unchanged servers away from the changes', () => {
    const before = { ...device, serverPermissions: [] }
    const after = { ...before, assignedProfileIds: ['one', 'two', 'three'] }
    render(<FriendAccessReview before={before} after={after} servers={servers} />)
    const defaults = screen.getByRole('group', { name: 'Device defaults' })
    expect(within(defaults).getByText('Unchanged')).toBeInTheDocument()
    expect(within(defaults).getAllByText('Start')).toHaveLength(1)
    expect(within(defaults).queryByText('Before')).not.toBeInTheDocument()
    expect(within(defaults).queryByText('After')).not.toBeInTheDocument()
    expect(screen.getByText('2 unchanged servers')).toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Server access before and after' }).children).toHaveLength(1)
    expect(screen.queryByRole('group', { name: 'Valheim evening access change' })).not.toBeInTheDocument()
  })

  it('derives Restart changes from Start and Stop while keeping log and timer permissions independent', () => {
    const before = { ...device, serverPermissions: [] }
    const after = { ...before, canStop: true, canViewLogs: true }
    render(<FriendAccessReview before={before} after={after} servers={servers} />)
    const changes = within(screen.getByRole('group', { name: 'Device defaults' })).getByRole('list', { name: 'Device default permission changes' })
    expect(within(changes).getAllByRole('listitem').map(row => row.getAttribute('aria-label'))).toEqual([
      'Guarded Stop permission change', 'Restart permission change', 'View logs permission change'
    ])
    const restart = within(changes).getByRole('listitem', { name: 'Restart permission change' })
    expect(within(restart).getByLabelText('Before: Off')).toBeInTheDocument()
    expect(within(restart).getByLabelText('After: Allowed')).toBeInTheDocument()
    expect(within(changes).queryByText('Add time')).not.toBeInTheDocument()
    expect(screen.getByText(/owner access deadlines stay unchanged/)).toBeInTheDocument()
  })

  it('refuses to call projected helper permissions saved defaults in a change review', () => {
    const review = reviewFriendAccess({ ...device, temporaryHelperActive: true }, device, servers)
    expect(review.helperProjection).toBe(true)
    expect(review.beforeDefaults).toBeNull()
    expect(review.rows.every(row => row.beforePermissions === null && row.change === 'ReviewNeeded')).toBe(true)
    render(<FriendAccessReview before={{ ...device, temporaryHelperActive: true }} after={device} servers={servers} />)
    const defaults = screen.getByRole('group', { name: 'Device defaults' })
    const before = within(defaults).getByRole('group', { name: 'Before permissions' })
    expect(before).toHaveTextContent('Saved defaults unavailable')
    expect(within(before).queryByRole('list', { name: 'Allowed permissions' })).not.toBeInTheDocument()
    expect(screen.getByText(/End temporary helper access and refresh/)).toBeInTheDocument()
    expect(screen.getAllByText('Refresh needed')).toHaveLength(2)
  })

  it('does not grant unassigned servers through device defaults', () => {
    expect(effectiveDeviceAccess({ ...device, assignedProfileIds: [] }, servers, nowMs).rows).toEqual([])
    expect(reviewFriendAccess({ ...device, assignedProfileIds: [] }, { ...device, assignedProfileIds: [] }, servers).rows).toEqual([])
    render(<FriendAccessReview before={{ ...device, assignedProfileIds: [] }} after={{ ...device, assignedProfileIds: [] }} servers={servers} />)
    expect(screen.getByText(/Defaults alone give no server access/)).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Server access before and after' })).not.toBeInTheDocument()
  })

  it('filters PCs by name, assigned server and actual expiry/connection state', () => {
    const expired = { ...device, id: 'pc-2', name: 'Spare PC', accessExpiresUtc: '2026-10-08T12:00:00Z' }
    const unknown = { ...device, id: 'pc-3', name: 'Laptop', lastHeartbeatUtc: null, assignedProfileIds: ['one'] }
    const all = [device, expired, unknown]
    expect(filterFriendPcs(all, servers, { ...defaultFriendPcFilters, search: ' MINECRAFT ' }, nowMs).map(pc => pc.id)).toEqual(['pc-1', 'pc-2'])
    expect(filterFriendPcs(all, servers, { ...defaultFriendPcFilters, status: 'expired' }, nowMs).map(pc => pc.id)).toEqual(['pc-2'])
    expect(filterFriendPcs(all, servers, { ...defaultFriendPcFilters, status: 'unknown', serverId: 'one' }, nowMs).map(pc => pc.id)).toEqual(['pc-3'])
    expect(filterFriendPcs(all, servers, { ...defaultFriendPcFilters, status: 'connected' }, nowMs).map(pc => pc.id)).toEqual(['pc-1'])
  })

  it('hides removed PCs across ordinary statuses and searches until explicitly requested', () => {
    const removed = { ...device, revoked: true, name: 'Removed laptop', lastHeartbeatUtc: null, assignedProfileIds: [] }
    const statuses = ['all', 'connected', 'unknown', 'approval', 'expired', 'helper', 'unassigned'] as const
    for (const status of statuses) {
      for (const search of ['', 'Removed', 'Access removed']) {
        expect(filterFriendPcs([removed], servers, { ...defaultFriendPcFilters, status, search }, nowMs)).toEqual([])
      }
    }
    expect(filterFriendPcs([removed, device], servers, { ...defaultFriendPcFilters, status: 'revoked', search: 'Removed' }, nowMs)).toEqual([removed])
  })

  it('orders usable connected PCs first, usable Unknown next, and preserves ties and source order', () => {
    const expired = { ...device, id: 'expired', accessExpired: true }
    const unknown = { ...device, id: 'unknown', lastHeartbeatUtc: null }
    const connected = { ...device, id: 'connected' }
    const connectedSecond = { ...device, id: 'connected-second' }
    const pending = { ...device, id: 'pending', approvalPending: true }
    const source = [expired, unknown, connected, pending, connectedSecond]
    expect(filterFriendPcs(source, servers, defaultFriendPcFilters, nowMs).map(pc => pc.id))
      .toEqual(['connected', 'connected-second', 'unknown', 'expired', 'pending'])
    expect(source.map(pc => pc.id)).toEqual(['expired', 'unknown', 'connected', 'pending', 'connected-second'])
  })

  it('offers controlled search/filter input and announces result counts politely', () => {
    const change = vi.fn()
    render(<FriendPcFilters value={defaultFriendPcFilters} servers={servers} resultCount={1} totalCount={3} onChange={change} />)
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search Friend PCs' }), { target: { value: 'Laptop' } })
    expect(change).toHaveBeenCalledWith({ ...defaultFriendPcFilters, search: 'Laptop' })
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite')
    expect(screen.getByRole('status')).toHaveTextContent('1 of 3 Friend PCs shown.')
  })

  it('announces actual grant changes while keeping deadline display ticks quiet', () => {
    const { rerender } = render(<FriendAccessSummary device={device} servers={servers} nowMs={nowMs} />)
    const announcement = screen.getByRole('status').textContent
    rerender(<FriendAccessSummary device={device} servers={servers} nowMs={nowMs + 1000} />)
    expect(screen.getByRole('status').textContent).toBe(announcement)
    rerender(<FriendAccessSummary device={{ ...device, canViewLogs: true }} servers={servers} nowMs={nowMs + 1000} />)
    expect(screen.getByRole('status')).toHaveTextContent('Logs: 2/2')
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite')
  })
})

import { fireEvent, render, screen } from '@testing-library/react'
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
    expect(screen.getByRole('table', { name: 'Server access before and after' })).toBeInTheDocument()
    expect(screen.getByText(/clears per-server exceptions/)).toBeInTheDocument()
  })

  it('refuses to call projected helper permissions saved defaults in a change review', () => {
    const review = reviewFriendAccess({ ...device, temporaryHelperActive: true }, device, servers)
    expect(review.helperProjection).toBe(true)
    expect(review.beforeDefaults).toBeNull()
    expect(review.rows.every(row => row.beforePermissions === null && row.change === 'ReviewNeeded')).toBe(true)
  })

  it('does not grant unassigned servers through device defaults', () => {
    expect(effectiveDeviceAccess({ ...device, assignedProfileIds: [] }, servers, nowMs).rows).toEqual([])
    expect(reviewFriendAccess({ ...device, assignedProfileIds: [] }, { ...device, assignedProfileIds: [] }, servers).rows).toEqual([])
  })

  it('filters PCs by name, assigned server and actual expiry/connection state without reordering them', () => {
    const expired = { ...device, id: 'pc-2', name: 'Spare PC', accessExpiresUtc: '2026-10-08T12:00:00Z' }
    const unknown = { ...device, id: 'pc-3', name: 'Laptop', lastHeartbeatUtc: null, assignedProfileIds: ['one'] }
    const all = [device, expired, unknown]
    expect(filterFriendPcs(all, servers, { ...defaultFriendPcFilters, search: ' MINECRAFT ' }, nowMs).map(pc => pc.id)).toEqual(['pc-1', 'pc-2'])
    expect(filterFriendPcs(all, servers, { ...defaultFriendPcFilters, status: 'expired' }, nowMs).map(pc => pc.id)).toEqual(['pc-2'])
    expect(filterFriendPcs(all, servers, { ...defaultFriendPcFilters, status: 'unknown', serverId: 'one' }, nowMs).map(pc => pc.id)).toEqual(['pc-3'])
    expect(filterFriendPcs(all, servers, { ...defaultFriendPcFilters, status: 'connected' }, nowMs).map(pc => pc.id)).toEqual(['pc-1'])
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

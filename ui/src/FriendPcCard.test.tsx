import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { Device } from './contracts'
import { FriendPcCard } from './FriendPcCard'
import friendAccessCss from './friend-access.css?inline'
import themeCss from './theme.css?inline'

const nowMs = Date.parse('2026-10-10T12:00:00Z')
const servers = [
  { id: 'one', name: 'Valheim evening' }, { id: 'two', name: 'Minecraft weekend' },
  { id: 'three', name: 'New world' }, { id: 'four', name: 'Fourth server' }
]
const device: Device = {
  id: 'pc-1', profileId: 'one', assignedProfileIds: ['one', 'two'], name: 'Living room PC',
  canStart: true, canStop: false, canExtendTimer: false, canViewLogs: false,
  revoked: false, paired: true, approvalPending: false,
  credentialExpiresUtc: '2026-11-10T12:00:00Z', accessExpiresUtc: null, accessExpired: false,
  lastHeartbeatUtc: '2026-10-10T11:59:55Z',
  serverPermissions: [{ profileId: 'two', canStart: false, canStop: true, canExtendTimer: true, canViewLogs: true }]
}
const callbacks = () => ({ onManage: vi.fn(), onApprove: vi.fn() })

describe('Friend PC overview card', () => {
  it('shows a shared person name with the PC label secondary and falls back for older or cleared profiles', () => {
    const input = { device: { ...device, profileName: 'Sam' }, servers, nowMs, busy: false, ...callbacks() }
    const { rerender } = render(<FriendPcCard {...input} />)
    expect(screen.getByRole('heading', { name: 'Sam' })).toBeInTheDocument()
    expect(screen.getByText('PC: Living room PC')).toBeInTheDocument()
    for (const profileName of [undefined, null, '', '  ']) {
      rerender(<FriendPcCard {...input} device={{ ...device, profileName }} />)
      expect(screen.getByRole('heading', { name: 'Living room PC' })).toBeInTheDocument()
    }
  })
  it('separates identity, heartbeat, assigned servers and exact grants without overview editors', () => {
    const actions = callbacks()
    const { container } = render(<FriendPcCard device={device} servers={servers} nowMs={nowMs} busy={false} {...actions} />)
    expect(screen.getByRole('article', { name: 'Living room PC' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 3, name: 'Living room PC' })).toBeInTheDocument()
    expect(screen.getByText('Friend PC')).toBeInTheDocument()
    expect(screen.getByText('Recently connected')).toBeInTheDocument()
    expect(screen.getByText('Last connected 5s ago')).toBeInTheDocument()
    const assigned = screen.getByRole('group', { name: 'Assigned servers' })
    expect(assigned).toHaveTextContent('2 assigned servers')
    expect(within(assigned).getByText('Valheim evening')).toBeInTheDocument()
    expect(within(assigned).getByText('Minecraft weekend')).toBeInTheDocument()
    expect(screen.getByText('Custom permissions')).toBeInTheDocument()
    const grants = screen.getByRole('list', { name: 'Permissions by server' })
    expect(within(grants).getByRole('listitem', { name: 'Start: 1 of 2 assigned servers' })).toBeInTheDocument()
    expect(within(grants).getByRole('listitem', { name: 'Stop when empty: 1 of 2 assigned servers' })).toBeInTheDocument()
    expect(within(grants).getByRole('listitem', { name: 'View logs: 1 of 2 assigned servers' })).toBeInTheDocument()
    expect(within(grants).getAllByRole('listitem')).toHaveLength(6)
    expect(within(grants).getByRole('listitem', { name: 'Start: 1 of 2 assigned servers' })).toHaveAttribute('data-permission-state', 'granted')
    expect(within(grants).getByRole('listitem', { name: 'Restart: 0 of 2 assigned servers' })).toHaveAttribute('data-permission-state', 'unassigned')
    expect(container.querySelector('input, select, textarea, table, details')).toBeNull()
    expect(screen.getAllByRole('button')).toHaveLength(1)
    fireEvent.click(screen.getByRole('button', { name: 'Manage access' }))
    expect(actions.onManage).toHaveBeenCalledOnce()
    expect(actions.onApprove).not.toHaveBeenCalled()
  })

  it('shows every permission for status-only and fully granted PCs with distinct readable styles', () => {
    const statusOnly = { ...device, canStart: false, canStop: false, serverPermissions: [] }
    const props = { servers, nowMs, busy: false, ...callbacks() }
    const { rerender } = render(<><style>{themeCss + friendAccessCss}</style><FriendPcCard {...props} device={statusOnly} /></>)
    const list = screen.getByRole('list', { name: 'Permissions by server' })
    const unassigned = within(list).getAllByRole('listitem')
    expect(unassigned).toHaveLength(6)
    for (const chip of unassigned) {
      expect(chip).toHaveAttribute('data-permission-state', 'unassigned')
      expect(chip).toHaveAccessibleName(/0 of 2 assigned servers$/)
      expect(getComputedStyle(chip).opacity).not.toBe('0')
    }
    expect(screen.getByText('Status and connection details only.')).toBeInTheDocument()
    const mutedColor = getComputedStyle(unassigned[0]).color
    const mutedBackground = getComputedStyle(unassigned[0]).backgroundColor

    rerender(<><style>{themeCss + friendAccessCss}</style><FriendPcCard {...props} device={{ ...statusOnly,
      canStart: true, canStop: true, canExtendTimer: true, canViewLogs: true }} /></>)
    for (const chip of within(screen.getByRole('list', { name: 'Permissions by server' })).getAllByRole('listitem')) {
      if (chip.getAttribute('aria-label')?.startsWith('Game password:')) {
        expect(chip).toHaveAttribute('data-permission-state', 'unassigned')
        continue
      }
      expect(chip).toHaveAttribute('data-permission-state', 'granted')
      expect(chip).toHaveAccessibleName(/2 of 2 assigned servers$/)
      expect(getComputedStyle(chip).color).not.toBe(mutedColor)
      expect(getComputedStyle(chip).backgroundColor).not.toBe(mutedBackground)
    }
  })

  it('keeps password access separate from helper actions and shows explicit Valheim grants', () => {
    const input = { ...device, canStart: true, canStop: true, canExtendTimer: true, canViewLogs: true,
      temporaryHelperActive: true, temporaryHelperUntilUtc: '2026-10-10T13:00:00Z' }
    const games = servers.map((server, index) => ({ ...server, kind: index === 0 ? 'Valheim' : 'MinecraftJava' }))
    const view = render(<FriendPcCard device={input} servers={games} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByRole('listitem', { name: 'Game password: 0 of 2 assigned servers' })).toHaveAttribute('data-permission-state', 'unassigned')
    view.rerender(<FriendPcCard device={{ ...input, canViewPassword: true }} servers={games} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByRole('listitem', { name: 'Game password: 1 of 2 assigned servers' })).toHaveAttribute('data-permission-state', 'granted')
  })

  it('keeps stale heartbeat Unknown separately from active permission settings', () => {
    const { rerender } = render(<FriendPcCard device={device} servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    rerender(<FriendPcCard device={device} servers={servers} nowMs={nowMs + 46_000} busy={false} {...callbacks()} />)
    expect(screen.getByText('Connection unavailable')).toBeInTheDocument()
    expect(screen.getByText('Last connected 51s ago')).toBeInTheDocument()
    expect(screen.getByText('Access active')).toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Permissions by server' })).toBeInTheDocument()
    expect(screen.queryByText(/Offline/)).not.toBeInTheDocument()
  })

  it('does not classify a missing, malformed or future heartbeat as connected', () => {
    const { rerender } = render(<FriendPcCard device={{ ...device, lastHeartbeatUtc: null }} servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByText('Not connected yet')).toBeInTheDocument()
    for (const heartbeat of ['invalid', '2026-10-10T12:01:00Z']) {
      rerender(<FriendPcCard device={{ ...device, lastHeartbeatUtc: heartbeat }} servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
      expect(screen.getByText('Connection unavailable')).toBeInTheDocument()
      expect(screen.getByText('Last connection time unavailable')).toBeInTheDocument()
      expect(screen.queryByText('Recently connected')).not.toBeInTheDocument()
    }
  })

  it('offers approval only for a pending PC and blocks both actions while busy', () => {
    const actions = callbacks()
    const pendingDevice = { ...device, approvalPending: true }
    const { rerender } = render(<FriendPcCard device={pendingDevice} servers={servers} nowMs={nowMs} busy={false} {...actions} />)
    expect(screen.getByText('Waiting for owner approval')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Approve this PC' }))
    expect(actions.onApprove).toHaveBeenCalledOnce()
    rerender(<FriendPcCard device={pendingDevice} servers={servers} nowMs={nowMs} busy {...actions} />)
    expect(screen.getByRole('button', { name: 'Manage access' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Approve this PC' })).toBeDisabled()
    rerender(<FriendPcCard device={{ ...pendingDevice, revoked: true }} servers={servers} nowMs={nowMs} busy={false} {...actions} />)
    expect(screen.getByText('Access removed')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Manage access' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Approve this PC' })).not.toBeInTheDocument()
  })

  it('offers deletion only for removed PCs, hides former grants and blocks deletion while busy', () => {
    const onDelete = vi.fn()
    const input = { device: { ...device, revoked: true }, servers, nowMs, busy: false, onDelete, ...callbacks() }
    const { rerender } = render(<FriendPcCard {...input} />)
    expect(screen.queryByRole('group', { name: 'Assigned servers' })).not.toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Configured permissions' })).not.toBeInTheDocument()
    const removedPermissions = screen.getByRole('list', { name: 'Unavailable permissions' })
    expect(within(removedPermissions).getAllByRole('listitem')).toHaveLength(6)
    for (const chip of within(removedPermissions).getAllByRole('listitem')) {
      expect(chip).toHaveAttribute('data-permission-state', 'unavailable')
      expect(chip).toHaveAccessibleName(/unavailable, access removed$/)
      expect(chip).not.toHaveTextContent(/\d\/\d/)
    }
    expect(screen.queryByText('Recently connected')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Manage access' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Delete PC' }))
    expect(onDelete).toHaveBeenCalledOnce()
    rerender(<FriendPcCard {...input} busy />)
    fireEvent.click(screen.getByRole('button', { name: 'Delete PC' }))
    expect(screen.getByRole('button', { name: 'Delete PC' })).toBeDisabled()
    expect(onDelete).toHaveBeenCalledOnce()
    rerender(<FriendPcCard {...input} device={device} />)
    expect(screen.queryByRole('button', { name: 'Delete PC' })).not.toBeInTheDocument()
  })

  it('shows blocked grants as configured access and preserves the exact owner deadline boundary', () => {
    const expiring = { ...device, accessExpiresUtc: '2026-10-10T12:00:01Z' }
    const { rerender } = render(<FriendPcCard device={expiring} servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByText(/^Access ends:/)).toBeInTheDocument()
    rerender(<FriendPcCard device={expiring} servers={servers} nowMs={nowMs + 1000} busy={false} {...callbacks()} />)
    expect(screen.getByText('Access time ended')).toBeInTheDocument()
    expect(screen.getByText(/^Access ended:/)).toBeInTheDocument()
    expect(screen.getByRole('group', { name: 'Configured permissions' })).toBeInTheDocument()
    expect(screen.getByRole('listitem', { name: 'Start: 1 of 2 assigned servers, configured' })).toBeInTheDocument()
    expect(screen.getByRole('listitem', { name: 'Start: 1 of 2 assigned servers, configured' })).toHaveAttribute('data-permission-state', 'configured')
    expect(screen.getByRole('listitem', { name: 'Restart: 0 of 2 assigned servers, configured' })).toHaveAttribute('data-permission-state', 'unassigned')
    expect(screen.getByRole('article')).not.toContainHTML('data-permission-state="granted"')
    expect(screen.getByText('Actions and logs are currently blocked.')).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Permissions by server' })).not.toBeInTheDocument()
    expect(screen.queryByText('Saved access expired')).not.toBeInTheDocument()
  })

  it('keeps renewable credential expiry distinct from an owner deadline', () => {
    render(<FriendPcCard device={{ ...device, credentialExpiresUtc: '2026-10-10T12:00:00Z' }} servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByText('Saved access expired')).toBeInTheDocument()
    expect(screen.queryByText(/Access ended:/)).not.toBeInTheDocument()
    expect(screen.getByText('Actions and logs are currently blocked.')).toBeInTheDocument()
  })

  it('shows only the returned helper grants and labels their saved permissions unavailable', () => {
    render(<FriendPcCard device={{ ...device, temporaryHelperActive: true, temporaryHelperUntilUtc: '2026-10-10T13:00:00Z' }}
      servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByText('Temporary helper')).toBeInTheDocument()
    expect(screen.getByRole('listitem', { name: 'Start: 1 of 2 assigned servers' })).toBeInTheDocument()
    expect(screen.queryByRole('listitem', { name: 'Start: 2 of 2 assigned servers' })).not.toBeInTheDocument()
    expect(screen.getByText(/Saved permissions are hidden while it is active/)).toBeInTheDocument()
    expect(screen.queryByText('Trusted helper')).not.toBeInTheDocument()
    expect(screen.queryByText(/server has custom permissions/)).not.toBeInTheDocument()
  })

  it('does not infer saved grants after a helper projection expires at the exact deadline', () => {
    const helper = { ...device, canStart: true, canStop: true, canExtendTimer: true, canViewLogs: true,
      serverPermissions: [], temporaryHelperActive: true, temporaryHelperUntilUtc: '2026-10-10T12:00:01Z' }
    const { rerender } = render(<FriendPcCard device={helper} servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByRole('listitem', { name: 'Restart: 2 of 2 assigned servers' })).toBeInTheDocument()
    rerender(<FriendPcCard device={helper} servers={servers} nowMs={nowMs + 1000} busy={false} {...callbacks()} />)
    expect(screen.getByText('Refresh needed after temporary helper access')).toBeInTheDocument()
    expect(screen.getByText(/Refresh to see saved permissions after temporary access ends/)).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Permissions by server' })).not.toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Saved permissions by server' })).not.toBeInTheDocument()
    const unknownPermissions = screen.getByRole('list', { name: 'Permissions need refresh' })
    expect(within(unknownPermissions).getAllByRole('listitem')).toHaveLength(6)
    for (const chip of within(unknownPermissions).getAllByRole('listitem')) {
      expect(chip).toHaveAttribute('data-permission-state', 'unknown')
      expect(chip).not.toHaveTextContent(/\d\/\d/)
    }
    expect(screen.queryByText('Trusted helper')).not.toBeInTheDocument()
  })

  it('does not display a still-active helper overlay as usable after owner access ends', () => {
    render(<FriendPcCard device={{ ...device, accessExpired: true, temporaryHelperActive: true, temporaryHelperUntilUtc: '2026-10-10T13:00:00Z' }}
      servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByText('Access time ended')).toBeInTheDocument()
    expect(screen.getByText(/Temporary access is blocked/)).toBeInTheDocument()
    expect(screen.getByText(/^Access ended; time unavailable/)).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Permissions by server' })).not.toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Permissions need refresh' })).getAllByRole('listitem')).toHaveLength(6)
    expect(screen.getByRole('article')).not.toContainHTML('data-permission-state="granted"')
    expect(screen.queryByRole('list', { name: 'Saved permissions by server' })).not.toBeInTheDocument()
  })

  it('keeps unknown expiry fail-closed while leaving access management available', () => {
    render(<FriendPcCard device={{ ...device, accessExpiresUtc: 'invalid' }} servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByText('Access time unavailable')).toBeInTheDocument()
    expect(screen.getByText('Refresh to check current access.')).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Permissions by server' })).not.toBeInTheDocument()
    for (const chip of within(screen.getByRole('list', { name: 'Permissions need refresh' })).getAllByRole('listitem')) {
      expect(chip).toHaveAttribute('data-permission-state', 'unknown')
      expect(chip).toHaveAccessibleName(/unavailable until access is refreshed$/)
    }
    expect(screen.getByRole('button', { name: 'Manage access' })).toBeEnabled()
  })

  it('does not turn device defaults into grants for a PC without assigned servers', () => {
    render(<FriendPcCard device={{ ...device, assignedProfileIds: [] }} servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    expect(screen.getByText('0 assigned servers')).toBeInTheDocument()
    expect(screen.getByText(/No server access. Assign a server in Manage access/)).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Current permissions' })).not.toBeInTheDocument()
    const emptyPermissions = within(screen.getByRole('list', { name: 'Permissions by server' })).getAllByRole('listitem')
    expect(emptyPermissions).toHaveLength(6)
    for (const chip of emptyPermissions) {
      expect(chip).toHaveAttribute('data-permission-state', 'unassigned')
      expect(chip).toHaveAccessibleName(/no assigned servers$/)
    }
  })

  it('bounds server tags while retaining the actual unique assignment count and manage action', () => {
    render(<FriendPcCard device={{ ...device, assignedProfileIds: ['one', 'two', 'three', 'four', 'four'] }}
      servers={servers} nowMs={nowMs} busy={false} {...callbacks()} />)
    const assigned = screen.getByRole('group', { name: 'Assigned servers' })
    expect(assigned).toHaveTextContent('4 assigned servers')
    expect(within(assigned).getAllByRole('listitem')).toHaveLength(3)
    expect(within(assigned).getByText('+2 more')).toBeInTheDocument()
    expect(within(assigned).queryByText('New world')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Manage access' })).toBeEnabled()
  })

  it('keeps feedback with its own PC and announces permission changes without announcing every clock tick', () => {
    const props = { device, servers, nowMs, busy: false, ...callbacks() }
    const { rerender } = render(<FriendPcCard {...props} feedback={<p>Saved this PC.</p>} />)
    expect(within(screen.getByRole('article', { name: 'Living room PC' })).getByText('Saved this PC.')).toBeInTheDocument()
    const announcement = screen.getByRole('status').textContent
    rerender(<FriendPcCard {...props} nowMs={nowMs + 1000} />)
    expect(screen.getByRole('status').textContent).toBe(announcement)
    rerender(<FriendPcCard {...props} device={{ ...device, canViewLogs: true }} nowMs={nowMs + 1000} />)
    expect(screen.getByRole('status')).toHaveTextContent('View logs: 2/2')
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite')
  })
})

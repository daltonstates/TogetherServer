import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { TemporaryHelperAccess } from './TemporaryHelperAccess'
import type { Device } from './contracts'

const device: Device = { id: 'pc', profileId: 'server', assignedProfileIds: ['server'], name: 'Friend PC',
  canStart: false, canStop: false, canExtendTimer: false, canViewLogs: false, revoked: false,
  paired: true, approvalPending: false, credentialExpiresUtc: null, lastHeartbeatUtc: null,
  serverPermissions: [], accessExpiresUtc: null, accessExpired: false }

describe('TemporaryHelperAccess', () => {
  it('offers bounded grants and a separate End now action', () => {
    const onGrant = vi.fn()
    const onEnd = vi.fn()
    const nowMs = Date.parse('2026-09-30T12:00:00Z')
    const { rerender } = render(<TemporaryHelperAccess device={device} busy={false} nowMs={nowMs}
      onGrant={onGrant} onEnd={onEnd} />)
    fireEvent.click(screen.getByRole('button', { name: 'Grant 1 hour' }))
    expect(onGrant).toHaveBeenCalledWith('OneHour')
    rerender(<TemporaryHelperAccess device={{ ...device, temporaryHelperUntilUtc: '2026-09-30T13:00:00Z',
      temporaryHelperActive: true }} busy={false} nowMs={nowMs} onGrant={onGrant} onEnd={onEnd} />)
    expect(screen.getByText(/usual permissions return automatically/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'End now' }))
    expect(onEnd).toHaveBeenCalledOnce()
  })
})

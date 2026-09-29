import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import {
  accessDurationChoices,
  FriendAccessExpiredNotice,
  OwnerAccessDeadlineEditor,
  putDeviceAccessExpiry,
  validateCustomUtcDateTime
} from './AccessExpiry'
import type { Device, DeviceAccessExpiryResult } from './contracts'

const now = () => new Date('2026-09-28T12:00:00.000Z')
const success: DeviceAccessExpiryResult = {
  ok: true,
  code: 'AccessExpirySet',
  message: 'This Friend PC\'s access end date was saved.',
  accessExpiresUtc: '2026-09-29T12:00:00Z',
  accessExpired: false
}

function device(overrides: Partial<Device> = {}): Device {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    profileId: '22222222-2222-2222-2222-222222222222',
    assignedProfileIds: ['22222222-2222-2222-2222-222222222222'],
    name: 'Friend PC 111111',
    canStart: true,
    canStop: false,
    canExtendTimer: false,
    canViewLogs: false,
    revoked: false,
    paired: true,
    approvalPending: false,
    credentialExpiresUtc: '2026-12-01T12:00:00Z',
    lastHeartbeatUtc: null,
    serverPermissions: [],
    accessExpiresUtc: null,
    accessExpired: false,
    ...overrides
  }
}

function renderEditor(overrides: {
  current?: Device
  onSave?: (request: Parameters<typeof putDeviceAccessExpiry>[1]) => Promise<DeviceAccessExpiryResult>
  onRefresh?: () => Promise<void>
} = {}) {
  const onSave = vi.fn(overrides.onSave ?? (async () => success))
  const onRefresh = vi.fn(overrides.onRefresh ?? (async () => undefined))
  render(<OwnerAccessDeadlineEditor device={overrides.current ?? device()} now={now}
    formatLocal={date => `LOCAL ${date.toISOString()}`} onSave={onSave} onRefresh={onRefresh} />)
  return { onSave, onRefresh }
}

describe('OwnerAccessDeadlineEditor', () => {
  it('maps every reviewed preset exactly and refreshes canonical device state after each save', async () => {
    const { onSave, onRefresh } = renderEditor()

    expect(accessDurationChoices.map(choice => choice.label)).toEqual(['1 hour', '8 hours', '1 day', '7 days', '30 days', '90 days'])
    for (const choice of accessDurationChoices) expect(screen.getByLabelText(choice.label)).toBeInTheDocument()

    for (const [index, choice] of accessDurationChoices.entries()) {
      fireEvent.click(screen.getByLabelText(choice.label))
      fireEvent.click(screen.getByRole('button', { name: 'Save deadline' }))
      await waitFor(() => expect(onSave).toHaveBeenCalledTimes(index + 1))
      expect(onSave).toHaveBeenNthCalledWith(index + 1, { duration: choice.value })
      await waitFor(() => expect(screen.getByRole('button', { name: 'Save deadline' })).toBeDisabled())
    }

    expect(onRefresh).toHaveBeenCalledTimes(accessDurationChoices.length)
    expect(onRefresh.mock.invocationCallOrder[0]).toBeGreaterThan(onSave.mock.invocationCallOrder[0])
    expect(await screen.findByRole('status')).toHaveTextContent('access end date was saved')
  })

  it('sends the dedicated clear request without changing another access setting', async () => {
    const cleared: DeviceAccessExpiryResult = {
      ok: true, code: 'AccessExpiryCleared', message: 'This Friend PC no longer has an access end date.',
      accessExpiresUtc: null, accessExpired: false
    }
    const onSave = vi.fn(async () => cleared)
    renderEditor({ current: device({ accessExpiresUtc: '2026-09-29T12:00:00Z' }), onSave })

    fireEvent.click(screen.getByLabelText('Clear deadline'))
    fireEvent.click(screen.getByRole('button', { name: 'Save deadline' }))

    await waitFor(() => expect(onSave).toHaveBeenCalledWith({ clear: true }))
  })

  it('treats the advanced field as explicit UTC and previews the exact local conversion before saving', async () => {
    const onSave = vi.fn(async () => ({ ...success, accessExpiresUtc: '2026-09-28T18:30:00.000Z' }))
    renderEditor({ onSave })

    fireEvent.click(screen.getByText('Change deadline'))
    fireEvent.click(screen.getByText('Advanced: custom UTC date and time'))
    fireEvent.change(screen.getByLabelText('UTC date and time'), { target: { value: '2026-09-28T18:30' } })

    expect(screen.getByText('2026-09-28T18:30:00.000Z')).toBeInTheDocument()
    expect(screen.getByText('LOCAL 2026-09-28T18:30:00.000Z')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Save deadline' }))
    await waitFor(() => expect(onSave).toHaveBeenCalledWith({ accessExpiresUtc: '2026-09-28T18:30:00.000Z' }))
  })

  it('rejects malformed, past, and more-than-365-day custom values before any request', () => {
    expect(validateCustomUtcDateTime('09/28/2026 18:30', now().getTime())).toEqual({
      ok: false, message: 'Use the UTC date and time fields shown.'
    })
    expect(validateCustomUtcDateTime('2026-09-28T11:59', now().getTime())).toEqual({
      ok: false, message: 'Choose a UTC time in the future.'
    })
    expect(validateCustomUtcDateTime('2027-09-29T12:01', now().getTime())).toEqual({
      ok: false, message: 'Choose a UTC time no more than 365 days away.'
    })
  })

  it('shows an expired state independently from credential expiry and leaves editing available', () => {
    renderEditor({ current: device({ accessExpiresUtc: '2026-09-28T10:00:00Z', accessExpired: true }) })

    expect(screen.getByText('Access expired')).toBeInTheDocument()
    expect(screen.getByText(/Ended LOCAL 2026-09-28T10:00:00.000Z/)).toHaveTextContent('2 hours ago')
    expect(screen.getByText('Change deadline')).toBeInTheDocument()
  })

  it('shows an active deadline as local date/time with useful relative text', () => {
    renderEditor({ current: device({ accessExpiresUtc: '2026-09-28T18:00:00Z' }) })

    expect(screen.getByText('Access ends LOCAL 2026-09-28T18:00:00.000Z')).toBeInTheDocument()
    expect(screen.getByText(/in 6 hours/)).toHaveTextContent("saved access and servers stay unchanged")
  })

  it('reports a request failure and does not refresh stale state', async () => {
    const onSave = vi.fn(async () => { throw new Error('Host rejected the deadline.') })
    const onRefresh = vi.fn(async () => undefined)
    renderEditor({ onSave, onRefresh })

    fireEvent.click(screen.getByLabelText('1 hour'))
    fireEvent.click(screen.getByRole('button', { name: 'Save deadline' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Host rejected the deadline.')
    expect(onRefresh).not.toHaveBeenCalled()
  })

  it('keeps the single save action visibly pending until the request finishes', async () => {
    let finish: (result: DeviceAccessExpiryResult) => void = () => undefined
    const onSave = vi.fn(() => new Promise<DeviceAccessExpiryResult>(resolve => { finish = resolve }))
    renderEditor({ onSave })

    fireEvent.click(screen.getByLabelText('1 day'))
    fireEvent.click(screen.getByRole('button', { name: 'Save deadline' }))

    expect(screen.getByRole('button', { name: 'Saving deadline…' })).toBeDisabled()
    finish(success)
    expect(await screen.findByRole('status')).toHaveTextContent('access end date was saved')
  })
})

describe('access-expiry API', () => {
  it('uses only the loopback owner PUT route and strictly decodes its response', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(success), {
      status: 200, headers: { 'Content-Type': 'application/json' }
    }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(putDeviceAccessExpiry(device().id, { duration: 'OneDay' })).resolves.toEqual(success)
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/local/devices/11111111-1111-1111-1111-111111111111/access-expiry',
      expect.objectContaining({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' },
        body: JSON.stringify({ duration: 'OneDay' })
      })
    )

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ ...success, accessExpired: 'no' }), {
      status: 200, headers: { 'Content-Type': 'application/json' }
    }))
    await expect(putDeviceAccessExpiry(device().id, { clear: true })).rejects.toMatchObject({ code: 'InvalidResponse' })
  })
})

describe('FriendAccessExpiredNotice', () => {
  it('presents typed AccessExpired as owner-ended access while preserving the saved connection', () => {
    const { rerender } = render(<FriendAccessExpiredNotice connectionCode="AccessExpired" />)

    expect(screen.getByText('Access expired')).toBeInTheDocument()
    expect(screen.getByText(/extend or clear the end date/i)).toBeInTheDocument()
    expect(screen.getByText(/saved connection remains here/i)).toBeInTheDocument()

    rerender(<FriendAccessExpiredNotice connectionCode="CredentialExpired" />)
    expect(screen.queryByText(/saved connection remains here/i)).not.toBeInTheDocument()
  })
})

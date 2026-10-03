import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { FriendSharedWorlds, HostSharedSaves, parseFriendSharedWorldStatus,
  parseHostSharedWorldStatus } from './SharedWorldControls'
import type { Device } from './contracts'

const profile = '11111111-1111-4111-8111-111111111111'
const device: Device = { id: '22222222-2222-4222-8222-222222222222', profileId: profile,
  assignedProfileIds: [profile], name: 'Friend PC', canStart: false, canStop: false,
  canExtendTimer: false, canViewLogs: false, saveReceiveProfileIds: [], revoked: false,
  paired: true, approvalPending: false, credentialExpiresUtc: null, lastHeartbeatUtc: null,
  serverPermissions: [], accessExpiresUtc: null, accessExpired: false }

function reply(value: unknown): Response {
  return new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

describe('Shared saves controls', () => {
  it('requires explicit Host enablement and a separate per-PC grant', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? 'GET'} ${url}`)
      if (url.endsWith('/governance')) return reply({ revision: calls.length, ownerOverride: true })
      if (url.endsWith('/shared-world') && !init?.method)
        return reply({ enabled: calls.some(call => call.startsWith('PUT')), latest: null, error: null })
      return reply({ ok: true, code: 'Saved', message: 'Saved' })
    }))
    render(<HostSharedSaves profileId={profile} devices={[device]} rollingBackupEnabled
      onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    const sharing = await screen.findByLabelText('Share completed saves from this server')
    expect(sharing).not.toBeChecked()
    fireEvent.click(sharing)
    await waitFor(() => expect(calls).toContain(`PUT /api/local/profiles/${profile}/shared-world`))
    const grant = await screen.findByLabelText('Receive for Friend PC')
    fireEvent.click(grant)
    await waitFor(() => expect(calls).toContain(
      `PUT /api/local/devices/${device.id}/shared-world/${profile}/grants`))
    expect(screen.getByLabelText('Eligible host for Friend PC')).not.toBeChecked()
    expect(screen.getByLabelText('Recovery voter for Friend PC')).not.toBeChecked()
    expect(screen.getByLabelText('Manage sharing for Friend PC')).not.toBeChecked()
    expect(screen.getByText(/Live save capture and takeover are not available yet/)).toBeInTheDocument()
  })

  it('keeps receiver consent off until this PC opts in and shows version lag', async () => {
    let consent = false
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/consent')) {
        consent = true
        return reply({ ok: true, code: 'ConsentSaved', message: 'Consent saved',
          status: { consented: true, hostVersion: null, thisPcVersion: 1, state: 'Ready', error: null } })
      }
      if (url.endsWith('/pull')) return reply({ ok: true, code: 'SaveReceived', message: 'Received',
        status: { consented: true, hostVersion: 3, thisPcVersion: 2, state: 'Behind', error: null } })
      if (!init?.method) return reply({ consented: consent, hostVersion: null,
        thisPcVersion: 1, state: 'Consent off', error: null })
      throw new Error(`Unexpected request ${url}`)
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    const toggle = await screen.findByLabelText('Allow saves on this PC')
    expect(toggle).not.toBeChecked()
    expect(screen.getByRole('button', { name: 'Receive latest save' })).toBeDisabled()
    fireEvent.click(toggle)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Receive latest save' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Receive latest save' }))
    expect(await screen.findByText(/1 version\(s\) behind/)).toBeInTheDocument()
    expect(screen.getByText(/takeover are not available yet/)).toBeInTheDocument()
  })

  it('rejects malformed status responses', () => {
    expect(() => parseHostSharedWorldStatus({ enabled: 'yes', latest: null, error: null })).toThrow()
    expect(() => parseFriendSharedWorldStatus({ consented: false, hostVersion: '3',
      thisPcVersion: null, state: 'Off', error: null })).toThrow()
  })

  it('sends owner override changes through the signed governance route', async () => {
    const bodies: unknown[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/governance')) {
        if (init?.method === 'PUT') bodies.push(JSON.parse(String(init.body)))
        return reply({ revision: init?.method === 'PUT' ? 2 : 1,
          ownerOverride: init?.method !== 'PUT' })
      }
      return reply({ enabled: true, latest: null, error: null })
    }))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled
      onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    const toggle = await screen.findByLabelText('Owner recovery override (future recovery only)')
    await waitFor(() => expect(toggle).toBeEnabled())
    fireEvent.click(toggle)
    await waitFor(() => expect(bodies).toEqual([{ ownerOverride: false }]))
    expect(toggle).not.toBeChecked()
  })

  it('explains how to review a changed Host source without comparing group version numbers', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply({ consented: true, hostVersion: 1,
      thisPcVersion: 8, state: 'Host save source changed. Turn Allow saves off, then on to approve the new signed group. Earlier verified copies stay here.',
      error: null })))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    expect(await screen.findByRole('alert')).toHaveTextContent('Earlier verified copies stay here')
    expect(screen.queryByText(/version\(s\) behind/)).not.toBeInTheDocument()
  })
})

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
        return reply({ enabled: calls.some(call => call.startsWith('PUT')), latest: null, error: null,
          liveSave: { available: false, message: 'Live save sharing is unavailable for this game. Use its verified post-Stop copy.' } })
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
    expect(screen.getByText(/Use its verified post-Stop copy/)).toBeInTheDocument()
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

  it('refreshes an open receiver panel as automatic catch-up advances', async () => {
    let reads = 0
    vi.stubGlobal('fetch', vi.fn(async () => {
      reads++
      return reply({ consented: true, hostVersion: 3, thisPcVersion: reads > 1 ? 3 : 2,
        state: reads > 1 ? 'Up to date when last checked' : 'Receiving', error: null,
        receivedBytes: 32, totalBytes: 64 })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    expect(await screen.findByText('Receiving completed save')).toBeInTheDocument()
    await new Promise(resolve => setTimeout(resolve, 2200))
    expect(await screen.findByText('Up to date when last checked')).toBeInTheDocument()
  })

  it('shows only the confirmed copy count returned for the latest version', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply({ enabled: true, error: null,
      confirmedCopies: 2, latest: { number: 4, versionHash: 'A'.repeat(64),
        createdUtc: '2026-10-03T12:00:00Z' } })))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled
      onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText(/Copied to 2 PCs/)).toBeInTheDocument()
    expect(screen.getByText(/app cannot prove its current availability/)).toBeInTheDocument()
    expect(() => parseHostSharedWorldStatus({ enabled: true, latest: null,
      error: null, confirmedCopies: -1 })).toThrow()
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

  it('lets the owner review a changed source without changing the override choice', async () => {
    const bodies: unknown[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/governance')) {
        if (init?.method === 'PUT') bodies.push(JSON.parse(String(init.body)))
        return reply({ revision: 4, ownerOverride: false })
      }
      return reply({ enabled: true, latest: null, error: null })
    }))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled
      onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    fireEvent.click(screen.getByText('Technical details'))
    fireEvent.click(await screen.findByRole('button', { name: 'Review changed world source' }))
    await waitFor(() => expect(bodies).toEqual([{ reviewSourceChange: true }]))
    expect(screen.getByLabelText('Owner recovery override (future recovery only)')).not.toBeChecked()
  })

  it('keeps source review available after a settings change disables sharing', async () => {
    const bodies: unknown[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/governance')) {
        if (init?.method === 'PUT') bodies.push(JSON.parse(String(init.body)))
        return reply({ revision: 5, ownerOverride: true })
      }
      return reply({ enabled: false, latest: null, error: null })
    }))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled
      onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    fireEvent.click(screen.getByText('Technical details'))
    fireEvent.click(await screen.findByRole('button', { name: 'Review changed world source' }))
    await waitFor(() => expect(bodies).toEqual([{ reviewSourceChange: true }]))
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

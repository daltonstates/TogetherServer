import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { FriendSharedWorlds, HostSharedSaves, parseFriendSharedWorldStatus,
  parseHostSharedWorldStatus, parseSharingView } from './SharedWorldControls'
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
    const toggle = await screen.findByLabelText('Owner has final say on save conflicts')
    await waitFor(() => expect(toggle).toBeEnabled())
    fireEvent.click(toggle)
    await waitFor(() => expect(bodies).toEqual([{ ownerOverride: false }]))
    expect(toggle).not.toBeChecked()
  })

  it('shows successor sharing permissions as read only', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/governance')
      ? reply({ revision: 3, ownerOverride: true })
      : reply({ enabled: true, latest: null, error: null, canManageSharing: false })))
    render(<HostSharedSaves profileId={profile} devices={[device]} rollingBackupEnabled
      onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText(/Owner controls stay with the original owner/)).toBeInTheDocument()
    expect(screen.getByLabelText('Share completed saves from this server')).toBeDisabled()
    expect(screen.getByLabelText('Owner has final say on save conflicts')).toBeDisabled()
    expect(screen.getByLabelText('Receive for Friend PC')).toBeDisabled()
    fireEvent.click(screen.getByText('Technical details'))
    expect(screen.queryByRole('button', { name: 'Retry signed permissions' })).not.toBeInTheDocument()
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
    expect(screen.getByLabelText('Owner has final say on save conflicts')).not.toBeChecked()
  })

  it('uses the current signed roster before the owner changes one grant', async () => {
    const bodies: unknown[] = []
    const signedGrants = { receive: true, eligibleHost: true, recoveryVoter: false, manageSharing: false }
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/governance')) return reply({ revision: 7, ownerOverride: true,
        members: [{ deviceId: device.id, grants: signedGrants, revoked: false, accessExpiresUtc: null }] })
      if (url.endsWith('/grants')) {
        bodies.push(JSON.parse(String(init?.body)))
        return reply({ ok: true, code: 'Saved', message: 'Saved' })
      }
      return reply({ enabled: true, latest: null, error: null })
    }))
    render(<HostSharedSaves profileId={profile} devices={[{ ...device, sharedWorldGrants: {
      [profile]: { receive: false, eligibleHost: false, recoveryVoter: false, manageSharing: false }
    } }]} rollingBackupEnabled onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText(/Verified sharing list: 1 active PCs, revision 7/)).toBeInTheDocument()
    expect(screen.getByLabelText('Receive for Friend PC')).toBeChecked()
    expect(screen.getByLabelText('Eligible host for Friend PC')).toBeChecked()
    fireEvent.click(screen.getByLabelText('Manage sharing for Friend PC'))
    await waitFor(() => expect(bodies).toEqual([{ grants: {
      receive: true, eligibleHost: true, recoveryVoter: false, manageSharing: true
    } }]))
  })

  it('lets a Manage sharing Friend edit another PC without Receive consent', async () => {
    const otherId = '33333333-3333-4333-8333-333333333333'
    const calls: { path: string; body?: unknown }[] = []
    const sharing = { available: true, canManage: true, selfDeviceId: device.id,
      revision: 5, code: 'SharingReady', message: 'Sharing verified.',
      checkedUtc: '2026-10-03T12:00:00Z', members: [
        { deviceId: device.id, isSelf: true, grants: { receive: false, eligibleHost: false,
          recoveryVoter: false, manageSharing: true }, revoked: false, accessExpiresUtc: null, name: null },
        { deviceId: otherId, isSelf: false, grants: { receive: true, eligibleHost: true,
          recoveryVoter: false, manageSharing: false }, revoked: false, accessExpiresUtc: null, name: null }
      ] }
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ path: url, body: init?.body ? JSON.parse(String(init.body)) : undefined })
      if (url.endsWith('/sharing/check')) return reply(sharing)
      if (url.endsWith('/sharing')) return reply({ ok: true, code: 'SharingChanged', message: 'Sharing updated.' })
      return reply({ consented: false, hostVersion: null, thisPcVersion: null,
        state: 'Consent off', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(screen.getByText('Manage sharing'))
    const vote = await screen.findByLabelText('Recovery voter for PC ending 333333')
    fireEvent.click(vote)
    await waitFor(() => expect(calls).toContainEqual({
      path: `/api/local/friend/${profile}/shared-world/sharing`,
      body: { deviceId: otherId, receive: true, eligibleHost: true,
        recoveryVoter: true, revoked: false }
    }))
    expect(calls.some(call => call.path.endsWith('/consent') || call.path.endsWith('/pull'))).toBe(false)
    expect(screen.queryByLabelText('Manage sharing for PC ending 333333')).not.toBeInTheDocument()
  })

  it('shows expired management access and does not offer member edits', async () => {
    const paths: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      paths.push(url)
      if (url.endsWith('/sharing/check')) return reply({ available: true, canManage: false,
        selfDeviceId: device.id, revision: null, code: 'AccessExpired',
        message: 'The owner ended this PC\'s sharing access.', members: [], checkedUtc: null })
      return reply({ consented: false, hostVersion: null, thisPcVersion: null,
        state: 'Consent off', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(screen.getByText('Manage sharing'))
    expect(await screen.findByRole('alert')).toHaveTextContent('owner ended this PC')
    expect(screen.queryByLabelText(/Receive for PC ending/)).not.toBeInTheDocument()
    expect(paths.some(path => path.endsWith('/sharing'))).toBe(false)
  })

  it('rejects malformed delegated sharing grants before showing controls', () => {
    expect(() => parseSharingView({ available: true, canManage: true,
      selfDeviceId: device.id, revision: 1, code: 'SharingReady', message: 'Ready', checkedUtc: null,
      members: [{ deviceId: device.id, isSelf: false, grants: { receive: 'yes' },
        revoked: false, accessExpiresUtc: null }] })).toThrow()
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

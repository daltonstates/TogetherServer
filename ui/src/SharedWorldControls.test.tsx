import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { FriendSharedWorlds, HostSharedSaves, parseFriendSharedWorldStatus,
  parseHostSharedWorldStatus, normalizedDirectIpHttpsEndpoint, parseSharingView } from './SharedWorldControls'
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
const noRecovery = { state: 'NoOffer', votes: 0, required: 0, version: null, versionHash: null,
  candidateAddress: null, candidateDeviceId: null, majorityReached: false, separateCopies: 0,
  proposalHash: null, authorityHeadHash: null }
const noHandoff = { pending: false, code: 'NoPendingHandoff', message: 'No pending handoff.',
  successorDeviceId: null, finalVersion: null, receiptConfirmed: false, canComplete: false, canCancel: false }
const offer = { proposal: { profileId: profile, candidateAddress: 'https://192.0.2.10:5131', candidatePublicKey: 'public-key' },
  version: { number: 3, versionHash: 'A'.repeat(64), captureKind: 'PostStopBackup' },
  candidateReceipt: { deviceId: device.id }, signed: 'signed-by-candidate' }

describe('Shared saves controls', () => {
  it('requires explicit Host enablement and a separate per-PC grant', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? 'GET'} ${url}`)
      if (url.endsWith('/governance')) return reply({ revision: calls.length, ownerOverride: true })
      if (url.endsWith('/shared-world') && !init?.method)
        return reply({ enabled: calls.some(call => call.startsWith('PUT')), latest: null, error: null,
          liveSave: { available: false, message: 'Live save sharing is unavailable for this game. Use its hash-verified post-Stop file copy. Game load has not been checked.' } })
      return reply({ ok: true, code: 'Saved', message: 'Saved' })
    }))
    render(<HostSharedSaves profileId={profile} devices={[device]} rollingBackupEnabled
      onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    const sharing = await screen.findByLabelText('Share completed saves from this server')
    expect(sharing).not.toBeChecked()
    fireEvent.click(sharing)
    await waitFor(() => expect(calls).toContain(`PUT /api/local/profiles/${profile}/shared-world`))
    fireEvent.click(screen.getByText('Technical details and PC permissions'))
    const grant = await screen.findByLabelText('Receive for Friend PC')
    fireEvent.click(grant)
    await waitFor(() => expect(calls).toContain(
      `PUT /api/local/devices/${device.id}/shared-world/${profile}/grants`))
    expect(screen.getByLabelText('Eligible host for Friend PC')).not.toBeChecked()
    expect(screen.getByLabelText('Recovery voter for Friend PC')).not.toBeChecked()
    expect(screen.getByLabelText('Manage sharing for Friend PC')).not.toBeChecked()
    expect(screen.getByText(/Live save capture and automatic takeover are unavailable/)).toBeInTheDocument()
    expect(screen.getByText(/Use its hash-verified post-Stop file copy/)).toBeInTheDocument()
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
    expect(screen.getByText(/automatic takeover are unavailable/)).toBeInTheDocument()
  })

  it('lets a voter review and confirm signed membership without enabling save receipt', async () => {
    const ownerPublicKey = 'owner-public-key-for-voter-review'
    const calls: Array<{ url: string; body: unknown }> = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/history/review')) {
        const body = JSON.parse(String(init?.body)) as Record<string, unknown>
        calls.push({ url, body })
        return body.confirmGroupId === profile && body.confirmOwnerPublicKey === ownerPublicKey ?
          reply({ ok: true, code: 'HistoryReviewed', message: 'Signed hosting history was reviewed.',
            recordCount: 2, competingHeads: 2, groupId: null, ownerPublicKey: null }) :
          reply({ ok: false, code: 'GroupReviewRequired', message: 'Review signed membership.',
            recordCount: 0, competingHeads: 0, groupId: profile, ownerPublicKey })
      }
      if (url.endsWith('/resolution/heads')) return reply([])
      if (url.endsWith('/recovery')) return reply(noRecovery)
      return reply({ consented: false, hostVersion: null, thisPcVersion: null,
        state: 'Consent off', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    expect(await screen.findByLabelText('Allow saves on this PC')).not.toBeChecked()
    fireEvent.click(screen.getByRole('button', { name: 'Review signed history' }))
    expect(await screen.findByRole('button', { name: 'Confirm group and owner' })).toBeEnabled()
    expect(screen.getByText(new RegExp(`Group ${profile}`))).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Confirm group and owner' }))
    expect(await screen.findByText('Signed hosting history was reviewed.')).toBeInTheDocument()
    expect(screen.getByLabelText('Allow saves on this PC')).not.toBeChecked()
    expect(calls).toHaveLength(2)
    expect(calls[1].body).toEqual({ confirmGroupId: profile, confirmOwnerPublicKey: ownerPublicKey })
    expect(screen.getByRole('button', { name: 'Receive latest save' })).toBeDisabled()
  })

  it('rejects malformed status responses', () => {
    expect(() => parseHostSharedWorldStatus({ enabled: 'yes', latest: null, error: null })).toThrow()
    expect(() => parseFriendSharedWorldStatus({ consented: false, hostVersion: '3',
      thisPcVersion: null, state: 'Off', error: null })).toThrow()
  })

  it('refreshes an open receiver panel as automatic catch-up advances', async () => {
    let reads = 0
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/recovery')) return reply(noRecovery)
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

  it('shows a manual low-space failure in the closed summary and clears it after a verified pull', async () => {
    let current = { consented: true, hostVersion: 3, thisPcVersion: 2,
      state: 'Ready to pull', error: null as string | null }
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/pull')) {
        if (current.state !== 'Low space') {
          current = { ...current, state: 'Low space', error: 'Keep at least 1 GiB free.' }
          return reply({ ok: false, code: 'InsufficientSpace', message: current.error })
        }
        current = { ...current, state: 'Up to date when last checked', thisPcVersion: 3, error: null }
        return reply({ ok: true, code: 'SaveReceived', message: 'Verified copy received.', status: current })
      }
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (init?.method) throw new Error(`Unexpected request ${url}`)
      return reply(current)
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(await screen.findByRole('button', { name: 'Receive latest save' }))
    await waitFor(() => expect(screen.getByText(/Shared worlds/, { selector: 'summary' })
      .querySelector('[role="alert"]')).toHaveTextContent('Low space — receiving paused'))
    fireEvent.click(screen.getByText(/Shared worlds/, { selector: 'summary' }))
    expect(screen.getByText(/Shared worlds/, { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
    fireEvent.click(screen.getByText(/Shared worlds/, { selector: 'summary' }))
    fireEvent.click(screen.getByRole('button', { name: 'Receive latest save' }))
    await waitFor(() => expect(screen.getByText(/Shared worlds/, { selector: 'summary' })
      .querySelector('[role="alert"]')).not.toBeInTheDocument())
  })

  it('refreshes a stalled transfer alert while Shared worlds stays closed', async () => {
    let stalled = false
    vi.stubGlobal('fetch', vi.fn(async () => reply({ consented: true, hostVersion: 3,
      thisPcVersion: 2, state: stalled ? 'Stalled' : 'Ready to pull',
      error: stalled ? 'Receiving made no progress across repeated attempts.' : null })))
    render(<FriendSharedWorlds profileId={profile} available />)
    await screen.findByText('1 version(s) behind')
    stalled = true
    await waitFor(() => expect(screen.getByText(/Shared worlds/, { selector: 'summary' })
      .querySelector('[role="alert"]')).toHaveTextContent('Receiving stalled — retrying'),
    { timeout: 7500 })
    expect(screen.getByText(/Shared worlds/, { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
  }, 10000)

  it('shows only the confirmed copy count returned for the latest version', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply({ enabled: true, error: null,
      confirmedCopies: 2, latest: { number: 4, versionHash: 'A'.repeat(64),
        createdUtc: '2026-10-03T12:00:00Z' } })))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled
      onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText(/Copied to 2 PCs.*latest post-Stop file copy 4/)).toBeInTheDocument()
    expect(screen.getByText(/Game load and playability have not been checked/)).toBeInTheDocument()
    expect(screen.getByText(/app cannot prove its current availability/)).toBeInTheDocument()
    expect(() => parseHostSharedWorldStatus({ enabled: true, latest: null,
      error: null, confirmedCopies: -1 })).toThrow()
  })

  it('copies only a normalized direct IP address for the current shared Host', async () => {
    expect(normalizedDirectIpHttpsEndpoint('https://192.0.2.10:5131/')).toBe('https://192.0.2.10:5131')
    expect(normalizedDirectIpHttpsEndpoint('https://[2001:db8::1]:5131/')).toBe('https://[2001:db8::1]:5131')
    for (const invalid of ['https://example.test:5131', 'http://192.0.2.10:5131',
      'https://127.0.0.1:5131', 'https://192.0.2.10:5131/path',
      'https://user@192.0.2.10:5131'])
      expect(normalizedDirectIpHttpsEndpoint(invalid)).toBeNull()
    const writeText = vi.fn(async () => {})
    vi.stubGlobal('navigator', { clipboard: { writeText } })
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/handoff') ? reply(noHandoff) :
      url.endsWith('/governance') ? reply({ revision: 1, ownerOverride: true }) :
        reply({ enabled: true, latest: null, error: null })))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled
      currentAddress="https://192.0.2.10:5131/" onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    fireEvent.click(await screen.findByRole('button', { name: 'Share current address' }))
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('https://192.0.2.10:5131'))
    expect(screen.getByText(/Friends will check the saved Host identity/)).toBeInTheDocument()
  })

  it('opens the saved identity recovery flow for a changed Host address', async () => {
    const openRecovery = vi.fn()
    const fetch = vi.fn(async (url: string, init?: RequestInit) => {
      if (!url.includes('/shared-world') || init?.method === 'PUT') throw new Error('Unexpected request')
      return reply({ consented: false, hostVersion: null,
        thisPcVersion: null, state: 'Consent off', error: null })
    })
    vi.stubGlobal('fetch', fetch)
    render(<FriendSharedWorlds profileId={profile} available onAddressChange={openRecovery} />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(screen.getByRole('button', { name: 'Host address changed?' }))
    expect(openRecovery).toHaveBeenCalledOnce()
    expect(fetch.mock.calls.every(([, init]) => init?.method !== 'PUT')).toBe(true)
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
    fireEvent.click(screen.getAllByText('Technical details').at(-1)!)
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
    fireEvent.click(screen.getAllByText('Technical details').at(-1)!)
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

  it('prepares only for an eligible PC and waits for its exact signed receipt', async () => {
    const requests: { url: string; body?: unknown }[] = []
    let pending = false
    const successor = { ...device, sharedWorldKeyEnrolled: true,
      sharedWorldGrants: { [profile]: { receive: true, eligibleHost: true, recoveryVoter: false, manageSharing: false } } }
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method === 'POST') {
        requests.push({ url, body: init.body ? JSON.parse(String(init.body)) : undefined })
        if (url.endsWith('/prepare')) { pending = true; return reply({ ok: true, code: 'WaitingForSuccessorCopy', message: 'Final save ready.' }) }
        if (url.endsWith('/complete')) return reply({ ok: false, code: 'WaitingForSuccessorCopy', message: 'Exact receipt missing.' })
        if (url.endsWith('/cancel')) { pending = false; return reply({ ok: true, code: 'HandoffCanceled', message: 'Canceled safely.' }) }
      }
      if (url.endsWith('/governance')) return reply({ revision: 1, ownerOverride: true })
      if (url.endsWith('/handoff')) return reply(pending ? { ...noHandoff, pending: true,
        code: 'WaitingForSuccessorCopy', message: 'Signed receipt pending from the chosen PC.',
        successorDeviceId: device.id, finalVersion: 3, canCancel: true } : noHandoff)
      return reply({ enabled: true, latest: null, error: null })
    }))
    render(<HostSharedSaves profileId={profile} devices={[successor]} rollingBackupEnabled onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    await screen.findByText('Move hosting to another PC')
    const prepare = screen.getByRole('button', { name: 'Stop and prepare file copy' })
    expect(prepare).toBeDisabled()
    await waitFor(() => expect(screen.getByLabelText('Next host PC')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Next host PC'), { target: { value: device.id } })
    fireEvent.click(screen.getAllByText('Technical details')[0])
    fireEvent.change(screen.getByLabelText('Next PC direct HTTPS IP address and port'),
      { target: { value: 'https://192.0.2.10:5131' } })
    fireEvent.click(prepare)
    await waitFor(() => expect(requests[0]).toEqual({ url: `/api/local/profiles/${profile}/shared-world/handoff/prepare`,
      body: { successorDeviceId: device.id, successorAddress: 'https://192.0.2.10:5131' } }))
    expect(await screen.findByText(/Signed receipt pending from the chosen PC/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Complete pending handoff' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Cancel pending handoff' }))
    expect(await screen.findByText('Canceled safely.')).toBeInTheDocument()
  })

  it('stages only a signed Friend offer and keeps hosting setup pending', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? 'GET'} ${url}`)
      if (url.endsWith('/stage')) return reply({ ok: true, code: 'StagedForSetup',
        message: 'The signed final copy is staged.' })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    expect(await screen.findByText('Verified copy on this PC')).toBeInTheDocument()
    expect(screen.queryByText('Ready to host')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Check signed offer and stage copy' }))
    await waitFor(() => expect(calls).toContain(`POST /api/local/friend/${profile}/shared-world/handoff/stage`))
    expect(await screen.findByText(/Local server setup, save signing, and direct routes still need checking/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
  })

  it('restores pending receipt state after reopening and keeps completion gated', async () => {
    let receipt = false
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/governance')) return reply({ revision: 1, ownerOverride: true })
      if (url.endsWith('/handoff')) return reply({ ...noHandoff, pending: true,
        code: receipt ? 'ReadyToComplete' : 'WaitingForSuccessorCopy',
        message: receipt ? 'The exact copy is confirmed.' : 'The successor has not confirmed the exact final save yet.',
        successorDeviceId: device.id, finalVersion: 7, receiptConfirmed: receipt,
        canComplete: receipt, canCancel: true })
      return reply({ enabled: true, latest: null, error: null })
    }))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText(/successor has not confirmed/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Complete pending handoff' })).toBeDisabled()
    receipt = true
    fireEvent.click(screen.getByRole('button', { name: 'Refresh shared save' }))
    expect(await screen.findByText(/Signed receipt confirmed/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Complete pending handoff' })).toBeEnabled()
  })

  it('does not infer a signed fence from NoPendingHandoff after reopen', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => reply(url.endsWith('/handoff') ? noHandoff :
      url.endsWith('/governance') ? { revision: 1, ownerOverride: true } :
        { enabled: true, latest: null, error: null })))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText(/If this PC was fenced, review its shared-world status/)).toBeInTheDocument()
    expect(screen.queryByText(/Handoff signed/)).not.toBeInTheDocument()
  })

  it('shows the verified old-Host fence and labels the retained save as this PC\'s copy', async () => {
    const head = { groupId: profile, epoch: 2, recordHash: 'B'.repeat(64),
      versionHash: 'C'.repeat(64), hostDeviceId: device.id, hostPublicKey: 'hosting-key',
      hostAddress: 'https://192.0.2.10:5131' }
    let state = 'NoTakeover'
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/handoff') ? reply(noHandoff) :
      url.endsWith('/governance') ? reply({ revision: 1, ownerOverride: true }) :
        reply({ enabled: true, canManageSharing: state === 'NoTakeover', confirmedCopies: 1,
          latest: { number: 3, versionHash: 'A'.repeat(64), createdUtc: '2026-10-03T12:00:00Z' },
          error: null, authority: { state, message: state === 'OldHostFenced'
            ? 'Another PC now hosts this world. This PC cannot start or share this world.' :
              'No takeover is recorded for this world.',
          head: state === 'OldHostFenced' ? head : null, competingHeads: null,
          exactManagedProcessRunning: state === 'OldHostFenced' } })))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled
      currentAddress="https://192.0.2.10:5131" onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    await screen.findByText(/No pending handoff/)
    state = 'OldHostFenced'
    fireEvent.click(screen.getByRole('button', { name: 'Refresh shared save' }))
    expect(await screen.findByRole('heading', { name: 'Another PC now hosts this world' })).toBeInTheDocument()
    expect(screen.getByText(/Preserved version 3 on this PC/)).toBeInTheDocument()
    expect(screen.queryByText(/latest saved version 3/)).not.toBeInTheDocument()
    expect(screen.getByText(/exact game process managed by this PC is still running/)).toBeInTheDocument()
    expect(screen.getByLabelText('Share completed saves from this server')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Share current address' })).not.toBeInTheDocument()
    expect(screen.queryByText('Ready to host')).not.toBeInTheDocument()
    const technical = screen.getAllByText('Technical details').at(-1)!.closest('details')!
    expect(technical).not.toHaveAttribute('open')
    expect(technical).toHaveTextContent(head.hostAddress)
    expect(technical).toHaveTextContent(head.versionHash)
  })

  it('reserves latest for the verified current local Host', async () => {
    const head = { groupId: profile, epoch: 2, recordHash: 'B'.repeat(64),
      versionHash: 'A'.repeat(64), hostDeviceId: device.id, hostPublicKey: 'hosting-key',
      hostAddress: 'https://192.0.2.10:5131' }
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/handoff') ? reply(noHandoff) :
      url.endsWith('/governance') ? reply({ revision: 1, ownerOverride: true }) :
        reply({ enabled: true, latest: { number: 4, versionHash: head.versionHash,
          createdUtc: '2026-10-03T12:00:00Z' }, confirmedCopies: 2,
          canManageSharing: false, error: null, authority: { state: 'ThisPcHost',
            message: 'This PC holds hosting authority for this world.', head,
            competingHeads: null, exactManagedProcessRunning: false } })))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText(/Copied to 2 PCs · latest post-Stop file copy 4/)).toBeInTheDocument()
    expect(screen.getByText('This PC holds the verified current hosting decision.')).toBeInTheDocument()
    expect(screen.queryByText(/Preserved version/)).not.toBeInTheDocument()
  })

  it('keeps competing verified heads and damaged history in review', async () => {
    const head = { groupId: profile, epoch: 1, recordHash: 'B'.repeat(64),
      versionHash: 'A'.repeat(64), hostDeviceId: device.id, hostPublicKey: 'hosting-key',
      hostAddress: 'https://192.0.2.10:5131' }
    let state: 'CompetingHistories' | 'ReviewRequired' = 'CompetingHistories'
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/handoff') ? reply(noHandoff) :
      url.endsWith('/governance') ? reply({ revision: 1, ownerOverride: true }) :
        reply({ enabled: true, latest: null, canManageSharing: false, error: null,
          authority: { state, message: 'Keep this world offline.', head: null,
            competingHeads: state === 'CompetingHistories' ? [head, { ...head, recordHash: 'C'.repeat(64) }] : null,
            exactManagedProcessRunning: false } })))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText('Competing hosting histories need review')).toBeInTheDocument()
    expect(screen.getByText(/Preserved local copy on this PC/)).toBeInTheDocument()
    expect(screen.getByLabelText('Share completed saves from this server')).toBeDisabled()
    state = 'ReviewRequired'
    fireEvent.click(screen.getByRole('button', { name: 'Refresh shared save' }))
    expect(await screen.findByText('Hosting decision needs review')).toBeInTheDocument()
    expect(screen.queryByText('Ready to host')).not.toBeInTheDocument()
  })

  it('rechecks authority while the Host panel stays open', async () => {
    let reads = 0
    const head = { groupId: profile, epoch: 1, recordHash: 'B'.repeat(64),
      versionHash: 'A'.repeat(64), hostDeviceId: device.id, hostPublicKey: 'hosting-key',
      hostAddress: 'https://192.0.2.10:5131' }
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/handoff') ? reply(noHandoff) :
      url.endsWith('/governance') ? reply({ revision: 1, ownerOverride: true }) : (() => {
        reads++
        const fenced = reads > 2
        return reply({ enabled: true, latest: null, canManageSharing: !fenced, error: null,
          authority: { state: fenced ? 'OldHostFenced' : 'NoTakeover',
            message: fenced ? 'Another PC now hosts this world.' : 'No takeover is recorded.',
            head: fenced ? head : null, competingHeads: null, exactManagedProcessRunning: false } })
      })()))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByRole('heading', { name: 'Another PC now hosts this world' },
      { timeout: 4000 })).toBeInTheDocument()
    expect(screen.getByLabelText('Share completed saves from this server')).toBeDisabled()
  })

  it('restores an armed recovery offer, rejects malformed code, and submits the reviewed vote', async () => {
    const calls: { url: string; body?: unknown }[] = []
    let voteAttempts = 0
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith('/recovery/vote')) {
        calls.push({ url, body: JSON.parse(String(init?.body)) })
        voteAttempts++
        if (voteAttempts === 1) return reply({ ok: false, code: 'CandidateUnavailable',
          message: 'The candidate did not answer.', votes: 0, required: 0, decision: null })
        return reply({ ok: true, code: 'VoteRecorded', message: 'Your recovery vote was recorded.', votes: 1, required: 2, decision: null })
      }
      if (url.endsWith('/offer-code')) return reply({ proposalHash: 'B'.repeat(64), offer })
      if (url.endsWith('/recovery')) return reply({ ...noRecovery, state: 'OfferArmed', votes: 1, required: 2,
        version: 3, versionHash: 'A'.repeat(64), candidateAddress: offer.proposal.candidateAddress,
        candidateDeviceId: device.id, proposalHash: 'B'.repeat(64) })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    expect(await screen.findByText(/Votes 1\/2/)).toBeInTheDocument()
    fireEvent.click(screen.getByText('Recover after Host loss'))
    fireEvent.click(screen.getAllByText('Technical details')[0])
    fireEvent.click(screen.getByRole('button', { name: 'Show signed offer code' }))
    expect(await screen.findByLabelText('Signed offer code')).toHaveValue(JSON.stringify(offer))
    fireEvent.change(screen.getByLabelText('Offer code from candidate PC'), { target: { value: '{broken' } })
    fireEvent.click(screen.getByRole('button', { name: 'Review offer code' }))
    expect(await screen.findByText(/Invalid offer code/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Check and vote for this offer' })).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Offer code from candidate PC'), { target: { value: JSON.stringify(offer) } })
    fireEvent.click(screen.getByRole('button', { name: 'Review offer code' }))
    expect(screen.getByText(/another Host may still be running/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Check and vote for this offer' }))
    expect(await screen.findByText('The candidate did not answer.')).toBeInTheDocument()
    expect(screen.queryByText(/Majority decision recorded/)).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Check and vote for this offer' }))
    await waitFor(() => expect(calls).toEqual([
      { url: `/api/local/friend/${profile}/shared-world/recovery/vote`, body: offer },
      { url: `/api/local/friend/${profile}/shared-world/recovery/vote`, body: offer }]))
    expect(await screen.findByText('Your recovery vote was recorded.')).toBeInTheDocument()
    expect(screen.queryByText('Ready to host')).not.toBeInTheDocument()
  })

  it('shows recovery errors and requires an explicit split warning before recording a separate history', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method === 'POST') {
        calls.push(url)
        if (url.endsWith('/offer')) return reply({ ok: false, code: 'HostLossNotConfirmed',
          message: 'Wait for two minutes of failed secure Host checks.' })
        return reply({ ok: true, code: 'SeparateCopyRecorded', message: 'A separate history was recorded.' })
      }
      if (url.endsWith('/recovery')) return reply({ ...noRecovery, state: 'OfferArmed', votes: 0, required: 2,
        version: 3, versionHash: 'A'.repeat(64), candidateAddress: offer.proposal.candidateAddress,
        candidateDeviceId: device.id, proposalHash: 'B'.repeat(64) })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(await screen.findByText('Recover after Host loss'))
    const separateButton = await screen.findByRole('button', { name: 'Record separate history' })
    expect(separateButton).toBeDisabled()
    expect(screen.getByText(/another game server may still be running/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Prepare signed offer' }))
    expect(await screen.findByText(/Wait for two minutes/)).toBeInTheDocument()
    expect(calls).not.toContain(`/api/local/friend/${profile}/shared-world/recovery/separate`)
    fireEvent.click(screen.getByLabelText('I understand this world may split into separate histories'))
    fireEvent.click(separateButton)
    await waitFor(() => expect(calls).toContain(`/api/local/friend/${profile}/shared-world/recovery/separate`))
    expect(await screen.findByText('A separate history was recorded.')).toBeInTheDocument()
    expect(separateButton).toBeDisabled()
  })

  it('labels another PC’s current majority and superseded history without keeping an old offer code', async () => {
    let state: 'OfferArmed' | 'ObservedMajority' | 'HistoricalRecovery' | 'HistoryReviewRequired' = 'OfferArmed'
    const otherPc = '33333333-3333-4333-8333-333333333333'
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/offer-code')) return reply({ proposalHash: 'B'.repeat(64), offer })
      if (url.endsWith('/recovery')) return reply({ ...noRecovery, state,
        votes: state === 'ObservedMajority' ? 2 : 0,
        required: state === 'OfferArmed' || state === 'ObservedMajority' ? 2 : 0,
        version: state === 'HistoryReviewRequired' ? null : 3,
        versionHash: state === 'HistoryReviewRequired' ? null : 'A'.repeat(64),
        candidateAddress: state === 'HistoryReviewRequired' ? null : offer.proposal.candidateAddress,
        candidateDeviceId: state === 'HistoryReviewRequired' ? null : otherPc,
        majorityReached: state === 'ObservedMajority',
        proposalHash: state === 'OfferArmed' ? 'B'.repeat(64) : 'C'.repeat(64),
        authorityHeadHash: state === 'OfferArmed' ? null : 'D'.repeat(64) })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(await screen.findByText('Recover after Host loss'))
    fireEvent.click(screen.getAllByText('Technical details')[0])
    fireEvent.click(screen.getByRole('button', { name: 'Show signed offer code' }))
    expect(await screen.findByLabelText('Signed offer code')).toBeInTheDocument()
    state = 'ObservedMajority'
    fireEvent.click(screen.getByRole('button', { name: 'Refresh recovery' }))
    expect(await screen.findByText(/Another PC’s current majority decision/)).toBeInTheDocument()
    expect(screen.getByText(/Current candidate PC 33333333/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Signed offer code')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Record separate history' })).not.toBeInTheDocument()
    state = 'HistoricalRecovery'
    fireEvent.click(screen.getByRole('button', { name: 'Refresh recovery' }))
    expect(await screen.findByText(/Earlier recovery history is preserved/)).toBeInTheDocument()
    expect(screen.getByText(/Earlier candidate PC 33333333/)).toBeInTheDocument()
    expect(screen.queryByText(/Majority decision pending/)).not.toBeInTheDocument()
    state = 'HistoryReviewRequired'
    fireEvent.click(screen.getByRole('button', { name: 'Refresh recovery' }))
    expect(await screen.findByText(/Competing authority histories need review/)).toBeInTheDocument()
    expect(screen.queryByText(/Current candidate PC/)).not.toBeInTheDocument()
  })

  it('drops an armed code and split action when the authority head changes while open', async () => {
    let changed = false
    let reads = 0
    let codeReads = 0
    let codeHash = 'C'.repeat(64)
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/offer-code')) { codeReads++; return reply({ proposalHash: codeHash, offer }) }
      if (url.endsWith('/recovery')) {
        reads++
        return reply(changed ? { ...noRecovery, state: 'HistoryReviewRequired',
          authorityHeadHash: 'D'.repeat(64) } : { ...noRecovery, state: 'OfferArmed',
          votes: 1, required: 2, version: 3, versionHash: 'A'.repeat(64),
          candidateAddress: offer.proposal.candidateAddress, candidateDeviceId: device.id,
          proposalHash: 'B'.repeat(64) })
      }
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(await screen.findByText('Recover after Host loss'))
    fireEvent.click(screen.getAllByText('Technical details')[0])
    fireEvent.click(screen.getByRole('button', { name: 'Show signed offer code' }))
    await waitFor(() => expect(codeReads).toBe(1))
    expect(screen.queryByLabelText('Signed offer code')).not.toBeInTheDocument()
    codeHash = 'B'.repeat(64)
    fireEvent.click(screen.getByRole('button', { name: 'Show signed offer code' }))
    expect(await screen.findByLabelText('Signed offer code')).toBeInTheDocument()
    fireEvent.click(screen.getByLabelText('I understand this world may split into separate histories'))
    expect(screen.getByRole('button', { name: 'Record separate history' })).toBeEnabled()
    changed = true
    await waitFor(() => expect(reads).toBeGreaterThan(1), { timeout: 3500 })
    await waitFor(() => expect(screen.getByText(/Competing authority histories need review/)).toBeInTheDocument())
    expect(screen.queryByLabelText('Signed offer code')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Record separate history' })).not.toBeInTheDocument()
    expect(screen.queryByText(/Votes 1\/2/)).not.toBeInTheDocument()
  })

  it('shares one in-flight recovery read between polling and manual refresh', async () => {
    let reads = 0
    let finishRead: ((response: Response) => void) | undefined
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/recovery')) {
        reads++
        return await new Promise<Response>(resolve => { finishRead = resolve })
      }
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 3, state: 'Ready', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(await screen.findByText('Recover after Host loss'))
    fireEvent.click(screen.getByRole('button', { name: 'Refresh recovery' }))
    expect(reads).toBe(1)
    finishRead?.(reply(noRecovery))
    await waitFor(() => expect(screen.getByText('No candidate offer is armed on this PC.')).toBeInTheDocument())
  })

  it('offers only a locally verified competing branch and reviews the voting invitation', async () => {
    const chosen = 'A'.repeat(64)
    const other = 'B'.repeat(64)
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      calls.push(`${init?.method ?? 'GET'} ${url}`)
      if (url.endsWith('/resolution/heads')) return reply([
        { recordHash: chosen, version: 2, availableHere: true },
        { recordHash: other, version: 3, availableHere: false }])
      if (url.endsWith(`/resolution/offer/${chosen}`)) return reply({ ok: true,
        code: 'ResolutionOfferArmed', message: 'Offer ready', offer: {
          proposal: { kind: 'ResolutionQuorum', competingHeadHashes: [chosen, other] },
          version: { number: 2 } } })
      if (url.endsWith('/resolution/invitations')) return reply({ ok: true, code: 'InvitationReviewed',
        message: 'Invitation reviewed', proposalHash: 'C'.repeat(64) })
      if (url.endsWith(`/resolution/vote/${'C'.repeat(64)}`))
        return reply({ ok: true, code: 'VoteRecorded', message: 'Vote recorded' })
      return reply({ consented: true, hostVersion: 3, thisPcVersion: 2, state: 'Competing save histories', error: null })
    }))
    render(<FriendSharedWorlds profileId={profile} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(await screen.findByRole('button', { name: 'Check signed branches' }))
    expect(await screen.findByRole('button', { name: 'Ask recovery voters' })).toBeEnabled()
    expect(screen.getAllByText(/on another PC/)).toHaveLength(1)
    fireEvent.click(screen.getByRole('button', { name: 'Ask recovery voters' }))
    expect(await screen.findByText('Offer ready')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Vote on an invitation'))
    fireEvent.change(screen.getByLabelText('Paste signed invitation'), { target: { value: JSON.stringify({
      proposal: { kind: 'ResolutionQuorum', competingHeadHashes: [chosen, other] }, version: { number: 2 } }) } })
    expect(screen.queryByRole('button', { name: 'Approve this copy' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Review invitation' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Approve this copy' }))
    await waitFor(() => expect(calls).toContain(
      `POST /api/local/friend/${profile}/shared-world/resolution/vote/${'C'.repeat(64)}`))
  })

  it('keeps owner resolution approval disabled when the signed roster turns it off', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/governance')) return reply({ revision: 3, ownerOverride: false })
      if (url.endsWith('/resolution/offers')) return reply([{
        proposalHash: 'A'.repeat(64), selectedVersion: 2, competingBranches: 2 }])
      return reply({ enabled: true, latest: null, error: null })
    }))
    render(<HostSharedSaves profileId={profile} devices={[]} rollingBackupEnabled
      onGrantChanged={async () => {}} />)
    fireEvent.click(screen.getByText('Shared saves'))
    fireEvent.click(await screen.findByRole('button', { name: 'Check owner decisions' }))
    expect(await screen.findByRole('button', { name: 'Approve selected copy' })).toBeDisabled()
  })
})

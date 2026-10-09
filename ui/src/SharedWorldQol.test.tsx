import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FriendSharedWorlds, HostSharedSaves, parseFriendSharedWorldStatus } from './SharedWorldControls'
import { SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'

const profileId = '11111111-1111-4111-8111-111111111111'
const deviceId = '22222222-2222-4222-8222-222222222222'
const noRecovery = { state: 'NoOffer', votes: 0, required: 0, version: null, versionHash: null,
  candidateAddress: null, candidateDeviceId: null, majorityReached: false, separateCopies: 0,
  proposalHash: null, authorityHeadHash: null }
const restoreStatus = { staged: false, restored: false, recordHash: null, message: 'No signed decision staged.',
  pendingChecks: [], preparedServerRoot: null, readyForManualStart: false, requiredAddOns: [], controlRouteFingerprint: null }
const receivedStatus = { consented: true, hostVersion: 4, thisPcVersion: 3, state: 'Receiving', error: null,
  receivedBytes: 2 * 1024 * 1024, totalBytes: 2 * 1024 * 1024 }
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
afterEach(() => vi.unstubAllGlobals())

describe('shared-save UX evidence', () => {
  it('keeps incomplete byte progress separate from verification and receipt, with canonical copy dates', async () => {
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/handoff/restore')) return reply(restoreStatus)
      return reply({ ...receivedStatus, transferPhase: 'Receipt', receiptConfirmed: false,
        completedUtc: '2026-10-08T10:00:00Z', receivedUtc: '2026-10-08T10:05:00Z' })
    }))
    render(<FriendSharedWorlds profileId={profileId} available game="Valheim" />)
    fireEvent.click(screen.getByText('Shared worlds'))
    const receiving = await screen.findByRole('region', { name: 'Receive save copies' })
    expect(within(receiving).queryByRole('button', { name: 'Prepare signed offer' })).not.toBeInTheDocument()
    expect(await screen.findByText('Confirming signed receipt', { selector: 'summary span' })).toBeInTheDocument()
    const progress = screen.getByRole('region', { name: 'Save transfer progress' })
    expect(progress).toHaveTextContent('2 MiB of 2 MiB')
    expect(within(progress).getByText('Confirm signed receipt')).toHaveAttribute('aria-current', 'step')
    expect(screen.getByText('Not acknowledged yet')).toBeInTheDocument()
    const age = screen.getByLabelText('Shared save age')
    expect(age.querySelector('time[datetime="2026-10-08T10:00:00Z"]')).not.toBeNull()
    expect(age.querySelector('time[datetime="2026-10-08T10:05:00Z"]')).not.toBeNull()
    expect(mutations).toEqual([])
  })

  it('keeps missing timestamp and receipt evidence unavailable and rejects malformed projections', () => {
    const legacy = parseFriendSharedWorldStatus(receivedStatus)
    expect(legacy.completedUtc).toBeNull()
    expect(legacy.receivedUtc).toBeNull()
    expect(legacy.receiptConfirmed).toBeNull()
    for (const extra of [{ completedUtc: 'yesterday' }, { receivedUtc: '2026-10-08T10:00:00-04:00' },
      { receiptConfirmed: 'true' }, { transferPhase: 'Passed' }, { receivedBytes: 3 * 1024 * 1024 }]) {
      expect(() => parseFriendSharedWorldStatus({ ...receivedStatus, ...extra })).toThrow()
    }
  })

  it('copies the current signed recovery offer beside the offer without casting a vote or starting', async () => {
    const offer = { proposal: { profileId, candidateAddress: 'https://192.0.2.10:5131', candidatePublicKey: 'synthetic-key' },
      version: { number: 3, versionHash: 'B'.repeat(64), captureKind: 'PostStopBackup' }, candidateReceipt: { deviceId } }
    const proposalHash = 'A'.repeat(64)
    const armed = { ...noRecovery, state: 'OfferArmed', version: 3, versionHash: offer.version.versionHash,
      candidateAddress: offer.proposal.candidateAddress, candidateDeviceId: deviceId, proposalHash }
    const mutations: string[] = []
    const clipboard = vi.fn().mockResolvedValue(undefined)
    vi.stubGlobal('navigator', { clipboard: { writeText: clipboard } })
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/recovery/offer-code')) return reply({ proposalHash, offer })
      if (url.endsWith('/recovery')) return reply(armed)
      if (url.endsWith('/handoff/restore')) return reply(restoreStatus)
      return reply({ ...receivedStatus, state: 'Ready', hostVersion: 3 })
    }))
    render(<FriendSharedWorlds profileId={profileId} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(await screen.findByRole('button', { name: 'Review hosting on this PC' }))
    fireEvent.click(await screen.findByText('Recover after Host loss'))
    fireEvent.click(await screen.findByRole('button', { name: 'Copy signed offer code' }))
    await waitFor(() => expect(clipboard).toHaveBeenCalledWith(JSON.stringify(offer)))
    expect(mutations).toEqual([])
  })

  it('resumes a pending handoff from the signed receipt after reopening, without running its completion action', async () => {
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/handoff')) return reply({ pending: true, code: 'ReadyToComplete', message: 'Exact copy confirmed.',
        successorDeviceId: deviceId, finalVersion: 4, receiptConfirmed: true, canComplete: true, canCancel: true })
      if (url.endsWith('/governance')) return reply({ revision: 1, ownerOverride: true })
      return reply({ enabled: true, latest: null, error: null })
    }))
    const props = { profileId, devices: [], rollingBackupEnabled: true, onGrantChanged: async () => {} }
    const first = render(<HostSharedSaves {...props} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText('Next · Current Host PC')).toBeInTheDocument()
    first.unmount()
    render(<HostSharedSaves {...props} />)
    fireEvent.click(screen.getByText('Shared saves'))
    expect(await screen.findByText('Next · Current Host PC')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Complete pending handoff' })).toBeEnabled()
    expect(mutations).toEqual([])
  })

  it('compares every signed save without requesting a group or owner decision until clicked', async () => {
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      if (url.endsWith('/resolution/heads')) return reply([
        { recordHash: 'A'.repeat(64), version: 3, availableHere: true },
        { recordHash: 'B'.repeat(64), version: 99, availableHere: false }])
      if (url.endsWith('/recovery')) return reply(noRecovery)
      if (url.endsWith('/handoff/restore')) return reply(restoreStatus)
      return reply({ ...receivedStatus, state: 'Competing save histories' })
    }))
    render(<FriendSharedWorlds profileId={profileId} available />)
    fireEvent.click(screen.getByText('Shared worlds'))
    fireEvent.click(await screen.findByRole('button', { name: 'Review hosting on this PC' }))
    fireEvent.click(screen.getByRole('button', { name: 'Check signed branches' }))
    const comparison = await screen.findByRole('table', { name: 'Compare competing save histories' })
    expect(comparison).toHaveTextContent('Saved version 3')
    expect(comparison).toHaveTextContent('Saved version 99')
    expect(comparison).toHaveTextContent('No copy is selected automatically')
    expect(screen.getByText(/Both histories remain preserved/)).toBeInTheDocument()
    expect(mutations).toEqual([])
  })
})

it('opens the one missing control-route step as navigation without probing or starting', async () => {
  const mutations: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) mutations.push(url)
    return reply({ ...restoreStatus, staged: true, restored: true, recordHash: 'A'.repeat(64),
      pendingChecks: ["Test the future Host's direct-IP Friend control route from another PC."] })
  }))
  render(<SharedWorldReadinessPanel profileId={profileId} game="MinecraftJava" />)
  fireEvent.click(screen.getByText('Check this PC for future hosting'))
  fireEvent.click(await screen.findByRole('button', { name: 'Review the control route' }))
  expect(screen.getByText("Test successor's control route from another Friend PC").closest('details')).toHaveAttribute('open')
  expect(screen.getByLabelText('Managed Minecraft Java server.jar')).toBeInTheDocument()
  expect(screen.queryByLabelText('Factorio local RCON port, if using Factorio')).not.toBeInTheDocument()
  expect(screen.queryByLabelText('New game password, if this game uses one')).not.toBeInTheDocument()
  expect(mutations).toEqual([])
})

it('copies the signed route together and submits only the existing proof fields after paste review', async () => {
  const clipboard = vi.fn().mockResolvedValue(undefined)
  const sent: unknown[] = []
  vi.stubGlobal('navigator', { clipboard: { writeText: clipboard } })
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
    if (init?.method === 'POST') {
      sent.push(JSON.parse(String(init.body)) as unknown)
      return reply({ controlRouteObserved: true, code: 'ControlRouteObserved', message: 'Pinned control route checked.' })
    }
    return reply({ ...restoreStatus, staged: true, restored: true, recordHash: 'A'.repeat(64),
      controlRouteFingerprint: 'B'.repeat(64), controlRouteAddress: 'https://192.0.2.10:5131' })
  }))
  render(<SharedWorldReadinessPanel profileId={profileId} />)
  fireEvent.click(screen.getByText('Check this PC for future hosting'))
  fireEvent.click(screen.getByText("Test successor's control route from another Friend PC"))
  fireEvent.click(await screen.findByRole('button', { name: 'Copy route details' }))
  await waitFor(() => expect(clipboard).toHaveBeenCalledTimes(1))
  const copied = String(clipboard.mock.calls[0][0])
  expect(JSON.parse(copied)).toEqual({ kind: 'TogetherServerRoute', schema: 1, profileId,
    recordHash: 'A'.repeat(64), tlsFingerprint: 'B'.repeat(64), endpoint: 'https://192.0.2.10:5131' })
  const probe = screen.getByRole('button', { name: 'Check direct-IP control route' })
  fireEvent.change(screen.getByLabelText('Route details from the new Host'), { target: { value: copied } })
  expect(probe).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Review pasted route details' }))
  expect(probe).toBeEnabled()
  expect(sent).toEqual([])
  fireEvent.click(probe)
  await waitFor(() => expect(sent).toEqual([{ recordHash: 'A'.repeat(64), tlsFingerprint: 'B'.repeat(64) }]))
})

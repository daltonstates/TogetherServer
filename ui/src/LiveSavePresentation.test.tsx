import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LiveSaveControls, parseLiveSaveStatus } from './LiveSaveControls'
import { LiveSaveAcceptancePanel } from './LiveSaveAcceptancePanel'

const profileId = '11111111-1111-4111-8111-111111111111'
const testId = '22222222-2222-4222-8222-222222222222'
const versionHash = 'A'.repeat(64)
const payloadSha256 = 'B'.repeat(64)
const ids = ['completion', 'snapshot', 'transfer', 'load', 'change', 'restart']
const sources: Record<string, string> = { completion: 'ObservedExactRun', snapshot: 'VerifiedImmutableSnapshot',
  transfer: 'SignedSeparateDeviceReceipt', load: 'OwnerReported', change: 'OwnerReported', restart: 'OwnerReported' }
const response = (value: unknown) => new Response(JSON.stringify(value))
const mockFetch = () => vi.fn<(url: string, init?: RequestInit) => Promise<Response>>()
const status = () => ({ available: false, game: 'Valheim', code: 'GameAcceptanceRequired',
  message: 'Detailed backend explanation of this installation and its independent checks.',
  stages: ids.map(id => ({ id, state: 'Unverified', detail: `${id} diagnostic evidence.` })) })
const stages = (passed: string[]) => ids.map(id => ({ id, state: passed.includes(id) ? 'Passed' : 'Unverified',
  detail: `${id} details for this exact private test.`, source: sources[id] }))
const acceptance = (passed = ['completion', 'snapshot']) => ({ accepted: false, code: 'GameAcceptanceRequired',
  message: 'Detailed backend installation evidence.', stages: stages(passed), tests: [{ testId, state: 'ArtifactReady', reviewed: false,
    artifact: { testId, testOnly: true, version: { versionHash }, payloadSha256 }, stages: stages(passed) }] })

afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('Compact live-save presentation', () => {
  it('offers a useful requirements disclosure without a disabled save call to action', () => {
    const fetch = vi.fn(); vi.stubGlobal('fetch', fetch)
    render(<LiveSaveControls profileId={profileId} status={parseLiveSaveStatus(status())} onUpdated={vi.fn()} />)
    const card = screen.getByRole('region', { name: 'Live save sharing' })
    expect(within(card).getByText('Needs setup')).toBeInTheDocument()
    expect(within(card).getByText('Test this exact installation before enabling live saves.')).toBeInTheDocument()
    expect(within(card).queryByRole('button', { name: 'Save and share now' })).not.toBeInTheDocument()
    const details = within(card).getByText('Game acceptance checks and save details').closest('details')!
    expect(details).not.toHaveAttribute('open')
    fireEvent.click(within(card).getByRole('button', { name: 'Review requirements' }))
    expect(details).toHaveAttribute('open')
    expect(within(details).getAllByRole('listitem')).toHaveLength(6)
    expect(within(details).getByText(status().message)).toBeInTheDocument()
    expect(fetch).not.toHaveBeenCalled()
  })

  it('keeps resume and withdrawal outside closed diagnostics and uses the fixed resume request', async () => {
    const fetch = mockFetch().mockResolvedValue(response({ ok: true, code: 'BedrockResumeConfirmed', message: 'Writes resumed.', attempt: null }))
    vi.stubGlobal('fetch', fetch)
    const updated = vi.fn().mockResolvedValue(undefined)
    render(<LiveSaveControls profileId={profileId} status={parseLiveSaveStatus({ ...status(), game: 'MinecraftBedrock',
      code: 'BedrockResumePending', resumePending: true, lastAttempt: { requestId: testId, state: 'Failed',
        code: 'ResumeRequired', message: 'Held writes need review.', versionHash: null, versionNumber: null } })} onUpdated={updated} />)
    expect(screen.getByText('Resume needed')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Withdraw this attempt' })).toBeVisible()
    expect(screen.getByText('Game acceptance checks and save details').closest('details')).not.toHaveAttribute('open')
    fireEvent.click(screen.getByRole('button', { name: 'Retry save resume' }))
    await waitFor(() => expect(updated).toHaveBeenCalledOnce())
    expect(fetch.mock.calls[0][0]).toBe(`/api/local/profiles/${profileId}/shared-world/live/resume`)
    expect(fetch.mock.calls[0][1]?.body).toBeUndefined()
  })

  it('shows a saving state and keeps an exact pending-attempt withdrawal available', async () => {
    const fetch = mockFetch().mockResolvedValue(response({ ok: true, code: 'LiveSaveWithdrawn', message: 'Private attempt withdrawn.', attempt: null }))
    vi.stubGlobal('fetch', fetch)
    render(<LiveSaveControls profileId={profileId} status={parseLiveSaveStatus({ ...status(),
      lastAttempt: { requestId: testId, state: 'Capturing', code: 'LiveSavePending', message: 'Waiting for completion.',
        versionHash: null, versionNumber: null } })} onUpdated={vi.fn().mockResolvedValue(undefined)} />)
    expect(screen.getByText('Saving')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Withdraw this attempt' }))
    await waitFor(() => expect(fetch).toHaveBeenCalledOnce())
    expect(fetch.mock.calls[0][0]).toBe(`/api/local/profiles/${profileId}/shared-world/live/withdraw`)
    expect(JSON.parse(String(fetch.mock.calls[0][1]?.body))).toEqual({ requestId: testId })
  })

  it('keeps a synthetic ready action explicit and reuses its request identity after a lost reply', async () => {
    const fetch = mockFetch().mockRejectedValueOnce(new Error('lost reply'))
      .mockResolvedValueOnce(response({ ok: true, code: 'LiveSavePublished', message: 'Synthetic copy published.', attempt: null }))
    vi.stubGlobal('fetch', fetch)
    render(<LiveSaveControls profileId={profileId} status={parseLiveSaveStatus({ ...status(), available: true,
      game: 'Fixture', code: 'StagingFixtureReady' })} onUpdated={vi.fn().mockResolvedValue(undefined)} />)
    expect(screen.getByText('Ready')).toBeInTheDocument()
    expect(screen.getByText('Development fixture')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Save and share now' }))
    await screen.findByRole('status')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save and share now' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Save and share now' }))
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2))
    expect(fetch.mock.calls[0][1]?.body).toBe(fetch.mock.calls[1][1]?.body)
    expect(Object.keys(JSON.parse(String(fetch.mock.calls[1][1]?.body)))).toEqual(['requestId'])
  })
})

describe('Private installation test presentation', () => {
  it('groups the six checks into three steps and keeps detailed evidence in a disclosure', async () => {
    const fetch = mockFetch().mockImplementation(async () => response(acceptance())); vi.stubGlobal('fetch', fetch)
    render(<LiveSaveAcceptancePanel profileId={profileId} />)
    await screen.findByText('Needs setup')
    fireEvent.click(screen.getByText('Automatic recovery copies'))
    const steps = screen.getByRole('list', { name: 'Installation test steps' })
    expect(within(steps).getAllByRole('listitem')).toHaveLength(3)
    expect(within(steps).getByText('2 of 2 · App checks')).toBeInTheDocument()
    expect(within(steps).getByText('0 of 3 · Your observations')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /I verified/ })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Check transfer' })).toBeInTheDocument()
    const details = screen.getByText('Evidence and installation details').closest('details')!
    expect(details).not.toHaveAttribute('open')
    fireEvent.click(screen.getByText('Evidence and installation details'))
    expect(within(details).getAllByRole('listitem')).toHaveLength(6)
    expect(within(details).getAllByText('OwnerReported')).toHaveLength(3)
    expect(fetch.mock.calls.every(call => !call[1]?.method)).toBe(true)
  })

  it('confirms only the next owner report against the current ready test and exact hashes', async () => {
    let current = acceptance(['completion', 'snapshot', 'transfer'])
    current.tests.push({ ...current.tests[0], testId: '44444444-4444-4444-8444-444444444444', state: 'Stale',
      artifact: { ...current.tests[0].artifact, testId: '44444444-4444-4444-8444-444444444444',
        version: { versionHash: 'E'.repeat(64) }, payloadSha256: 'F'.repeat(64) } })
    const mutations: { path: string; body: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, body: JSON.parse(String(init.body)) })
        current = acceptance(['completion', 'snapshot', 'transfer', 'load'])
        return response({ ok: true, code: 'LiveSaveOwnerReportSaved', message: 'Your observation was recorded.' })
      }
      return response(current)
    }))
    render(<LiveSaveAcceptancePanel profileId={profileId} />)
    await screen.findByText('Needs setup')
    fireEvent.click(screen.getByText('Automatic recovery copies'))
    fireEvent.click(screen.getByRole('button', { name: 'I verified load in the game' }))
    const confirmation = screen.getByRole('dialog', { name: 'Confirm actual game load?' })
    expect(confirmation).toHaveAccessibleDescription(/owner report, not an automatic game check/)
    expect(mutations).toEqual([])
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Record my observation' }))
    await waitFor(() => expect(mutations).toEqual([{ path: `/api/local/profiles/${profileId}/live-save-acceptance/tests/${testId}/confirm`,
      body: { versionHash, payloadSha256, stage: 'load', confirmed: true } }]))
    expect(await screen.findByRole('button', { name: 'I verified change in the game' })).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /I verified/ })).toHaveLength(1)
  })

  it('keeps synthetic evidence from presenting ready acceptance or owner-report actions', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response({ ...acceptance(['completion', 'snapshot', 'transfer']),
      code: 'SyntheticEvidenceOnly', accepted: true })))
    render(<LiveSaveAcceptancePanel profileId={profileId} />)
    await screen.findByText('Needs setup')
    fireEvent.click(screen.getByText('Automatic recovery copies'))
    expect(screen.queryByText('accepted for this installation')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Capture private test save' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: /I verified/ })).not.toBeInTheDocument()
    expect(screen.getByText('Development evidence cannot accept a real game installation.')).toBeInTheDocument()
  })
})

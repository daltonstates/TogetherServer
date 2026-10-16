import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LiveSaveAcceptancePanel, parseLiveSaveAcceptance } from './LiveSaveAcceptancePanel'

const profileId = '11111111-1111-4111-8111-111111111111'
const testId = '22222222-2222-4222-8222-222222222222'
const requestId = '33333333-3333-4333-8333-333333333333'
const versionHash = 'A'.repeat(64)
const payloadSha256 = 'B'.repeat(64)
const base = `/api/local/profiles/${profileId}/live-save-acceptance`
const stageIds = ['completion', 'snapshot', 'transfer', 'load', 'change', 'restart'] as const
type StageId = typeof stageIds[number]
const sources: Record<StageId, string> = {
  completion: 'ObservedExactRun', snapshot: 'VerifiedImmutableSnapshot', transfer: 'SignedSeparateDeviceReceipt',
  load: 'OwnerReported', change: 'OwnerReported', restart: 'OwnerReported',
}
const scope = { contractVersion: 1, profileId, game: 'Valheim', installationId: 'synthetic-installation-id',
  binarySha256: 'C'.repeat(64), configurationSha256: 'D'.repeat(64), synthetic: false, binaryVersion: 'test' }
function stages(passed: readonly StageId[]) {
  return stageIds.map(id => ({ id, state: passed.includes(id) ? 'Passed' : 'Unverified',
    detail: `${id} evidence for this exact private test.`, source: sources[id], observedUtc: null }))
}
function acceptance(passed: readonly StageId[] = ['completion', 'snapshot']) {
  return { accepted: false, code: 'GameAcceptanceRequired', message: 'This installation still needs its private test.',
    scope, stages: stages(passed), tests: [{ testId, state: 'ArtifactReady', code: 'LiveSaveTestReady',
      requestedUtc: '2026-10-16T12:00:00Z', scope, reviewed: false, stages: stages(passed),
      artifact: { schema: 1, testOnly: true, testId, scope, operationId: requestId,
        sourceDeviceId: profileId, completionUtc: '2026-10-16T12:00:01Z', snapshotUtc: '2026-10-16T12:00:02Z',
        version: { versionHash }, payloadSha256, payloadBytes: 32, chunkBytes: 1048576, chunkCount: 1,
        signature: 'synthetic-ui-response' } }] }
}
const reply = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
type Mutation = { path: string; method: string; body: unknown }

afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('Installed live-save acceptance contracts', () => {
  it('keeps private artifact identity and the six different evidence stages together', () => {
    const parsed = parseLiveSaveAcceptance(acceptance(['completion', 'snapshot', 'transfer', 'load']))
    expect(parsed.accepted).toBe(false)
    expect(parsed.tests[0].artifact).toEqual({ testId, version: { versionHash }, payloadSha256 })
    expect(parsed.stages.map(stage => stage.id)).toEqual(stageIds)
    expect(parsed.tests[0].stages.map(stage => stage.source)).toEqual([
      'ObservedExactRun', 'VerifiedImmutableSnapshot', 'SignedSeparateDeviceReceipt',
      'OwnerReported', 'OwnerReported', 'OwnerReported',
    ])
    expect(parsed.tests[0].stages.find(stage => stage.id === 'change')?.state).toBe('Unverified')
  })

  it.each([
    ['playable artifact', { testOnly: false }],
    ['missing private marker', { testOnly: undefined }],
    ['different test', { testId: requestId }],
    ['invalid test identity', { testId: 'not-a-test-id' }],
    ['invalid payload hash', { payloadSha256: 'not-a-hash' }],
    ['invalid version hash', { version: { versionHash: 'not-a-hash' } }],
  ] as const)('rejects a %s before offering confirmation actions', (_label, changedArtifact) => {
    const value = acceptance()
    expect(() => parseLiveSaveAcceptance({ ...value, tests: [{ ...value.tests[0],
      artifact: { ...value.tests[0].artifact, ...changedArtifact } }] })).toThrow()
  })

  it('requires all six stages in both the installation and each individual test', () => {
    const value = acceptance()
    expect(() => parseLiveSaveAcceptance({ ...value, stages: value.stages.slice(0, 5) })).toThrow('six live-save stages')
    expect(() => parseLiveSaveAcceptance({ ...value, tests: [{ ...value.tests[0], stages: value.stages.slice(1) }] })).toThrow('six live-save stages')
    expect(() => parseLiveSaveAcceptance({ ...value, accepted: 'true' })).toThrow()
    expect(() => parseLiveSaveAcceptance({ ...value, tests: [{ ...value.tests[0], reviewed: 'true' }] })).toThrow()
  })
})

describe('Installed live-save acceptance controls', () => {
  it('only reads while polling and exposes owner reports separately from observed evidence', async () => {
    vi.useFakeTimers()
    const mutations: Mutation[] = []
    let reads = 0
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push({ path: url, method: init.method, body: JSON.parse(String(init.body)) })
      else reads++
      return reply(acceptance())
    }))
    await act(async () => { render(<LiveSaveAcceptancePanel profileId={profileId} />) })
    fireEvent.click(screen.getByText(/Automatic recovery copies/))
    expect(screen.getByText('This installation still needs its private test.')).toBeInTheDocument()
    expect(screen.getAllByText(/OwnerReported/)).toHaveLength(3)
    expect(screen.getByText(/transfer: Unverified/)).toBeInTheDocument()
    expect(screen.getByText(/Test copies stay private and separate/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /I verified/ })).not.toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(29999) })
    expect(reads).toBe(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(30001) })
    expect(reads).toBe(3)
    expect(mutations).toEqual([])
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('requires a private-capture confirmation and sends only a generated request UUID', async () => {
    const mutations: Mutation[] = []
    const randomUUID = vi.spyOn(crypto, 'randomUUID').mockReturnValue(requestId)
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, method: init.method, body: JSON.parse(String(init.body)) })
        return reply({ ok: true, code: 'LiveSaveTestCaptured', message: 'The private test copy was captured.' })
      }
      return reply(acceptance())
    }))
    render(<LiveSaveAcceptancePanel profileId={profileId} />)
    await screen.findByText('This installation still needs its private test.')
    fireEvent.click(screen.getByText(/Automatic recovery copies/))
    fireEvent.click(screen.getByRole('button', { name: 'Capture private test save' }))
    const firstReview = screen.getByRole('dialog', { name: 'Capture a private test save?' })
    expect(firstReview).toHaveAccessibleDescription(/distinct approved receiving PC must verify the transfer/)
    expect(mutations).toEqual([])
    expect(randomUUID).not.toHaveBeenCalled()
    fireEvent.click(within(firstReview).getByRole('button', { name: 'Cancel' }))
    await act(async () => {})
    expect(mutations).toEqual([])
    fireEvent.click(screen.getByRole('button', { name: 'Capture private test save' }))
    const review = screen.getByRole('dialog', { name: 'Capture a private test save?' })
    fireEvent.click(within(review).getByRole('button', { name: 'Capture test save' }))
    await waitFor(() => expect(mutations).toHaveLength(1))
    expect(mutations).toEqual([{ path: `${base}/tests`, method: 'POST', body: { requestId } }])
    expect(randomUUID).toHaveBeenCalledTimes(1)
    expect(await screen.findByRole('status')).toHaveTextContent('The private test copy was captured.')
    expect(screen.queryByRole('button', { name: /I verified/ })).not.toBeInTheDocument()
  })

  it('unlocks ordered owner observations only after transfer and binds each POST to the exact private bytes', async () => {
    vi.useFakeTimers()
    let current = acceptance()
    const mutations: Mutation[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        const body = JSON.parse(String(init.body)) as { stage: StageId }
        mutations.push({ path: url, method: init.method, body })
        const passed = current.tests[0].stages.filter(stage => stage.state === 'Passed').map(stage => stage.id)
        current = acceptance([...passed, body.stage])
        if (body.stage === 'restart') { current.accepted = true; current.tests[0].state = 'Accepted' }
        return reply({ ok: true, code: 'LiveSaveOwnerReportSaved', message: 'Your observation was recorded as an owner report.' })
      }
      return reply(current)
    }))
    await act(async () => { render(<LiveSaveAcceptancePanel profileId={profileId} />) })
    fireEvent.click(screen.getByText(/Automatic recovery copies/))
    expect(screen.queryByRole('button', { name: /I verified/ })).not.toBeInTheDocument()
    current = acceptance(['completion', 'snapshot', 'transfer'])
    await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
    expect(mutations).toEqual([])
    for (const stage of ['load', 'change', 'restart'] as const) {
      expect(screen.getAllByRole('button', { name: /I verified/ })).toHaveLength(1)
      fireEvent.click(screen.getByRole('button', { name: `I verified ${stage} in the game` }))
      const review = screen.getByRole('dialog', { name: `Confirm actual game ${stage}?` })
      expect(review).toHaveAccessibleDescription(/this exact transferred test copy in a disposable world/)
      expect(review).toHaveAccessibleDescription(/owner report, not an automatic game check/)
      expect(mutations).toHaveLength(['load', 'change', 'restart'].indexOf(stage))
      await act(async () => { fireEvent.click(within(review).getByRole('button', { name: 'Record my observation' })) })
    }
    expect(mutations).toEqual(['load', 'change', 'restart'].map(stage => ({
      path: `${base}/tests/${testId}/confirm`, method: 'POST',
      body: { versionHash, payloadSha256, stage, confirmed: true },
    })))
    expect(screen.getAllByText(/OwnerReported/)).toHaveLength(3)
    expect(screen.getByText(/Automatic recovery copies/)).toHaveTextContent('accepted for this installation')
    expect(screen.queryByRole('button', { name: /I verified/ })).not.toBeInTheDocument()
  })

  it('uses a current ready artifact instead of a newer stale artifact and keeps both in history', async () => {
    const current = acceptance(['completion', 'snapshot', 'transfer'])
    const staleTestId = '44444444-4444-4444-8444-444444444444'
    const staleVersionHash = 'E'.repeat(64)
    current.tests.push({ ...current.tests[0], testId: staleTestId, state: 'Stale',
      artifact: { ...current.tests[0].artifact, testId: staleTestId,
        version: { versionHash: staleVersionHash }, payloadSha256: 'F'.repeat(64) } })
    const mutations: Mutation[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) {
        mutations.push({ path: url, method: init.method, body: JSON.parse(String(init.body)) })
        current.tests[0].stages = stages(['completion', 'snapshot', 'transfer', 'load'])
        return reply({ ok: true, code: 'LiveSaveOwnerReportSaved', message: 'Your observation was recorded as an owner report.' })
      }
      return reply(current)
    }))
    render(<LiveSaveAcceptancePanel profileId={profileId} />)
    await screen.findByText('This installation still needs its private test.')
    fireEvent.click(screen.getByText(/Automatic recovery copies/))
    expect(screen.getByRole('button', { name: 'I verified load in the game' })).toBeInTheDocument()
    expect(mutations).toEqual([])
    const history = screen.getByText('Private test history').closest('details')!
    expect(history).not.toHaveAttribute('open')
    fireEvent.click(within(history).getByText('Private test history'))
    expect(within(history).getByText(/Test 22222222 · ArtifactReady/)).toHaveTextContent(versionHash.slice(0, 12))
    expect(within(history).getByText(/Test 44444444 · Stale/)).toHaveTextContent(staleVersionHash.slice(0, 12))
    expect(within(history).getByText(/Retained tests do not establish acceptance/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'I verified load in the game' }))
    const review = screen.getByRole('dialog', { name: 'Confirm actual game load?' })
    fireEvent.click(within(review).getByRole('button', { name: 'Record my observation' }))
    await waitFor(() => expect(mutations).toHaveLength(1))
    expect(mutations).toEqual([{ path: `${base}/tests/${testId}/confirm`, method: 'POST',
      body: { versionHash, payloadSha256, stage: 'load', confirmed: true } }])
    expect(await screen.findByRole('button', { name: 'I verified change in the game' })).toBeInTheDocument()
    expect(screen.getByText(/Automatic recovery copies/)).toHaveTextContent('installation test required')
  })

  it.each(['Stale', 'Failed', 'Interrupted'] as const)(
    'retains a %s artifact without offering owner confirmations or claiming current acceptance', async state => {
      const current = acceptance(['completion', 'snapshot', 'transfer'])
      current.tests[0].state = state
      const mutations: string[] = []
      vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
        if (init?.method) mutations.push(url)
        return reply(current)
      }))
      render(<LiveSaveAcceptancePanel profileId={profileId} />)
      await screen.findByText('This installation still needs its private test.')
      fireEvent.click(screen.getByText(/Automatic recovery copies/))
      expect(screen.queryByRole('button', { name: /I verified/ })).not.toBeInTheDocument()
      expect(screen.getByText(/Automatic recovery copies/)).toHaveTextContent('installation test required')
      fireEvent.click(screen.getByText('Private test history'))
      const history = screen.getByText('Private test history').closest('details')!
      expect(within(history).getByText(`Test 22222222 · ${state}`)).toHaveTextContent(versionHash.slice(0, 12))
      expect(mutations).toEqual([])
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })

  it('withdraws an owner report action when polling marks its artifact stale', async () => {
    vi.useFakeTimers()
    const current = acceptance(['completion', 'snapshot', 'transfer'])
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      return reply(current)
    }))
    await act(async () => { render(<LiveSaveAcceptancePanel profileId={profileId} />) })
    fireEvent.click(screen.getByText(/Automatic recovery copies/))
    expect(screen.getByRole('button', { name: 'I verified load in the game' })).toBeInTheDocument()
    current.tests[0].state = 'Stale'
    await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
    expect(screen.queryByRole('button', { name: /I verified/ })).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Private test history'))
    const history = screen.getByText('Private test history').closest('details')!
    expect(within(history).getByText('Test 22222222 · Stale')).toBeInTheDocument()
    expect(screen.getByText(/Automatic recovery copies/)).toHaveTextContent('installation test required')
    expect(mutations).toEqual([])
  })

  it('does not offer capture for synthetic installation evidence', async () => {
    const value = acceptance()
    value.code = 'SyntheticEvidenceOnly'
    const mutations: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) mutations.push(url)
      return reply(value)
    }))
    render(<LiveSaveAcceptancePanel profileId={profileId} />)
    await screen.findByText('This installation still needs its private test.')
    fireEvent.click(screen.getByText(/Automatic recovery copies/))
    expect(screen.getByRole('button', { name: 'Capture private test save' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: /I verified/ })).not.toBeInTheDocument()
    expect(mutations).toEqual([])
  })
})

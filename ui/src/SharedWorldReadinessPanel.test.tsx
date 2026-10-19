import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { parseTakeoverReadiness, SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'

afterEach(() => vi.useRealTimers())
const profileId = '11111111-1111-4111-8111-111111111111'
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
const handoff = { staged: true, restored: true, recordHash: 'A'.repeat(64), message: 'Verified current stage.',
  pendingChecks: [], preparedServerRoot: null, readyForManualStart: true, requiredAddOns: [],
  controlRouteFingerprint: 'C'.repeat(64) }

it('withdraws manual Start on head changes, ignores deferred old reads, and preserves unsaved setup', async () => {
  vi.useFakeTimers()
  let reads = 0
  let oldRead!: (value: Response) => void
  let newRead!: (value: Response) => void
  const requests: { url: string; signal?: AbortSignal | null }[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    requests.push({ url, signal: init?.signal })
    if (++reads === 1) return reply(handoff)
    return new Promise<Response>(resolve => { if (reads === 2) oldRead = resolve; else newRead = resolve })
  }))
  const ready = vi.fn()
  const navigate = vi.fn()
  const props = { profileId, expanded: true, onHostingSetupChange: ready, onOpenHostControls: navigate }
  const view = render(<SharedWorldReadinessPanel {...props} authorityHeadHash={handoff.recordHash} />)
  await act(async () => {})
  expect(screen.getByRole('button', { name: 'Review Start in Host' })).toBeEnabled()
  const values = [
    ['Installed server file', 'C:\\draft\\server.exe'], ['Installed game version', 'unsaved-version'],
    ['TogetherServer connection port', '5141'], ['Game port', '2567']
  ]
  // Unknown-game setup deliberately exposes the generic local server field.
  const serverField = screen.getByPlaceholderText('Installed server file location')
  fireEvent.change(serverField, { target: { value: values[0][1] } })
  for (const [label, value] of values.slice(1)) fireEvent.change(screen.getByLabelText(label), { target: { value } })
  const access = screen.getByLabelText('I set a new game password on this PC')
  fireEvent.click(access)
  await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
  expect(ready).toHaveBeenLastCalledWith(false)
  expect(screen.queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
  view.rerender(<SharedWorldReadinessPanel {...props} authorityHeadHash={'B'.repeat(64)} />)
  expect(requests[1].signal?.aborted).toBe(true)
  await act(async () => { oldRead(reply(handoff)) })
  expect(screen.queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
  expect(ready).toHaveBeenLastCalledWith(false)
  expect(screen.getByText(/Checking the hosting decision again/)).toBeInTheDocument()
  await act(async () => { newRead(reply({ ...handoff, recordHash: 'B'.repeat(64) })) })
  expect(ready).toHaveBeenLastCalledWith(true)
  expect(serverField).toHaveValue(values[0][1])
  for (const [label, value] of values.slice(1)) expect(screen.getByLabelText(label)).toHaveValue(value)
  expect(access).toBeChecked()
  expect(navigate).not.toHaveBeenCalled()
  expect(requests.every(request => request.url.endsWith('/handoff/restore'))).toBe(true)
})

it('keeps a slow stage check single flight and readiness Unknown until its valid response', async () => {
  vi.useFakeTimers()
  let reads = 0
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const ready = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
    if (++reads === 1) return reply(handoff)
    signal = init?.signal
    return new Promise<Response>(resolve => { release = resolve })
  }))
  render(<SharedWorldReadinessPanel profileId={profileId} expanded onHostingSetupChange={ready}
    onOpenHostControls={vi.fn()} authorityHeadHash={null} />)
  await act(async () => {})
  expect(ready).toHaveBeenLastCalledWith(true)
  await act(async () => { await vi.advanceTimersByTimeAsync(20000) })
  expect(reads).toBe(2)
  expect(signal?.aborted).toBe(false)
  expect(ready).toHaveBeenLastCalledWith(false)
  expect(screen.queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
  await act(async () => { release(reply(handoff)) })
  expect(ready).toHaveBeenLastCalledWith(true)
  await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
  expect(reads).toBe(3)
  expect(ready).toHaveBeenLastCalledWith(false)
})

it('preserves every restore draft field through revision refresh and failed metadata checks', async () => {
  vi.useFakeTimers()
  let fail = false
  vi.stubGlobal('fetch', vi.fn(async () => {
    if (fail) throw new Error('offline')
    return reply({ ...handoff, restored: false, readyForManualStart: false, preparedServerRoot: 'C:\\initial' })
  }))
  const view = render(<SharedWorldReadinessPanel profileId={profileId} expanded authorityRevision={1} />)
  await act(async () => {})
  const values = [
    ['New server name', 'Draft name'], ['Game program, if different from the server file', 'C:\\draft.exe'],
    ['New Minecraft server folder, if used', 'C:\\draft-root'],
    ['Factorio RCON control port, if used', '28015'], ['New game password, if used', 'unsaved-password']
  ]
  for (const [label, value] of values) fireEvent.change(screen.getByLabelText(label), { target: { value } })
  fail = true
  view.rerender(<SharedWorldReadinessPanel profileId={profileId} expanded authorityRevision={2} />)
  await act(async () => {})
  expect(screen.getByText(/Hosting readiness is Unknown: refresh failed/)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Restore verified copy' })).toBeDisabled()
  for (const [label, value] of values) expect(screen.getByLabelText(label)).toHaveValue(value)
  fail = false
  await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
  expect(screen.getByRole('button', { name: 'Restore verified copy' })).toBeEnabled()
  for (const [label, value] of values) expect(screen.getByLabelText(label)).toHaveValue(value)
})

it('requires a fresh exact staged head, including with no parent head, and recovers after failure', async () => {
  vi.useFakeTimers()
  let status = handoff
  let fail = false
  const ready = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async () => { if (fail) throw new Error('offline'); return reply(status) }))
  const view = render(<SharedWorldReadinessPanel profileId={profileId} expanded onHostingSetupChange={ready}
    onOpenHostControls={vi.fn()} authorityHeadHash={'B'.repeat(64)} />)
  await act(async () => {})
  expect(screen.getByText(/does not match the current hosting decision/)).toBeInTheDocument()
  expect(ready).toHaveBeenLastCalledWith(false)
  view.rerender(<SharedWorldReadinessPanel profileId={profileId} expanded onHostingSetupChange={ready}
    onOpenHostControls={vi.fn()} authorityHeadHash={null} />)
  await act(async () => {})
  expect(ready).toHaveBeenLastCalledWith(true)
  fail = true
  await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
  expect(ready).toHaveBeenLastCalledWith(false)
  expect(screen.queryByRole('button', { name: 'Review Start in Host' })).not.toBeInTheDocument()
  expect(screen.getByText(/refresh failed/)).toBeInTheDocument()
  fail = false
  status = { ...handoff, staged: false }
  await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
  expect(ready).toHaveBeenLastCalledWith(false)
  status = handoff
  await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
  expect(ready).toHaveBeenLastCalledWith(true)
})

it('ignores a deferred restore check after changing the profile', async () => {
  let release!: (value: Response) => void
  vi.stubGlobal('fetch', vi.fn(async (url: string) => url.includes(profileId) ?
    new Promise<Response>(resolve => { release = resolve }) : reply({ ...handoff, staged: false })))
  const ready = vi.fn()
  const view = render(<SharedWorldReadinessPanel profileId={profileId} expanded onHostingSetupChange={ready} />)
  view.rerender(<SharedWorldReadinessPanel profileId="22222222-2222-4222-8222-222222222222" expanded onHostingSetupChange={ready} />)
  await act(async () => { release(reply(handoff)) })
  expect(ready).toHaveBeenLastCalledWith(false)
  expect(screen.queryByText('Before the first Start on this PC')).not.toBeInTheDocument()
})

it('shows rehearsal limits and keeps takeover unavailable', async () => {
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    calls.push(url)
    return new Response(JSON.stringify({ ready: false,
      reasons: ['The owner has not granted this PC takeover permission.',
        'Disposable file copy passed hash checks. Fixture process rehearsal is unavailable because this app cannot verify the selected executable\'s provenance. A real game load, join, and save still need testing.'],
      version: 2, versionHash: 'A'.repeat(64), rehearsalPassed: true,
      managedProcessRehearsalPassed: false }),
    { status: 200, headers: { 'Content-Type': 'application/json' } })
  }))
  render(<SharedWorldReadinessPanel profileId="11111111-1111-4111-8111-111111111111" />)
  fireEvent.click(screen.getByText('Check this PC for hosting'))
  fireEvent.click(screen.getByRole('button', { name: 'Test restoring files' }))
  await waitFor(() => expect(calls.some(url => url.endsWith('/rehearse'))).toBe(true))
  expect(await screen.findByText('Test files restored, checked and removed. No real game was tested.')).toBeInTheDocument()
  expect(screen.getByText(/real game load, join, and save still need testing/)).toBeInTheDocument()
  expect(screen.getByText(/Fixture process rehearsal is unavailable/)).toBeInTheDocument()
  expect(screen.queryByText('Ready to host')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Test app connection' })).toBeDisabled()
})

it('rejects malformed readiness data', () => {
  expect(() => parseTakeoverReadiness({ ready: true, reasons: [], version: 1,
    versionHash: 'A'.repeat(64), rehearsalPassed: 'yes' })).toThrow()
})

it('finishes a verified majority with signed add-ons before showing manual Start', async () => {
  const hash = 'A'.repeat(64)
  const fingerprint = 'B'.repeat(64)
  const addOn = { name: 'Reviewed mod', version: '1.0', requiredGameVersion: '2.0',
    type: 'Factorio mod', id: null }
  let ready = false
  const hostingSetupChange = vi.fn()
  let finishBody: Record<string, unknown> | null = null
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/handoff/finish')) {
      finishBody = JSON.parse(String(init?.body)) as Record<string, unknown>
      ready = true
      return new Response(JSON.stringify({ ok: true, code: 'ReadyForManualStart',
        message: 'Pre-Start checks passed.', pendingChecks: null }), { status: 200 })
    }
    return new Response(JSON.stringify({ staged: true, restored: true,
      recordHash: hash, message: 'Verified copy restored.', pendingChecks: [],
      preparedServerRoot: null, readyForManualStart: ready, requiredAddOns: [addOn],
      controlRouteFingerprint: fingerprint }),
    { status: 200 })
  }))
  render(<SharedWorldReadinessPanel profileId="11111111-1111-4111-8111-111111111111"
    onHostingSetupChange={hostingSetupChange} />)
  fireEvent.click(screen.getByText('Check this PC for hosting'))
  expect(await screen.findByRole('button', { name: 'Finish setup and checks' })).toBeInTheDocument()
  fireEvent.click(screen.getByText('Required add-ons'))
  expect(screen.getByText(/Reviewed mod 1.0/)).toBeInTheDocument()
  expect(screen.getByText(fingerprint)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Finish setup and checks' }))
  await waitFor(() => expect(finishBody).not.toBeNull())
  const sent = finishBody as Record<string, unknown> | null
  expect(sent && sent.recordHash).toBe(hash)
  expect((sent?.setup as { enabledAddOns: unknown[] }).enabledAddOns).toEqual([addOn])
  expect(await screen.findByText(/Before the first Start on this PC/)).toBeInTheDocument()
  await waitFor(() => expect(hostingSetupChange).toHaveBeenLastCalledWith(true))
  expect(screen.getByText(/The game connection still needs testing/)).toBeInTheDocument()
})

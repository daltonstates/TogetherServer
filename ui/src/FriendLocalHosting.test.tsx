import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { useLayoutEffect } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FriendLocalHosting, localHostingAttention, parseLocalHostingStatus, type LocalHostingStatus } from './FriendLocalHosting'
import type { HostingProgress } from './LocalHostingProgress'

const profileId = '11111111-1111-4111-8111-111111111111'
const localProfileId = '22222222-2222-4222-8222-222222222222'
const branchHash = 'A'.repeat(64)
const base = `/api/local/friend/${profileId}/shared-world`
const initial: LocalHostingStatus = {
  ok: true, code: 'NeedsPreparation', message: 'Prepare the local copy.', allowed: true, consented: false,
  game: 'Valheim', name: 'Shared world', needsServerFile: false, needsPassword: false,
  serverFile: 'C:\\Games\\valheim_server.exe', gameVersion: '0.222.1', gamePort: 2456, controlPort: 5131,
  localProfileId: null, branchHash: null, readyForManualStart: false, running: false,
  steps: [
    { key: 'permission', state: 'Ready', message: 'The owner allows this PC to host.' },
    { key: 'server', state: 'Ready', message: 'Installed server found.' },
    { key: 'save', state: 'ActionRequired', message: 'Allow a verified local copy.' },
    { key: 'ports', state: 'Unknown', message: 'Checked during preparation.' },
    { key: 'connection', state: 'Unknown', message: 'Incoming Friend access is unverified. Local hosting is available.' }
  ]
}
const prepared: LocalHostingStatus = {
  ...initial, code: 'LocalHostingPrepared', message: 'Verified copy prepared without starting.', consented: true,
  localProfileId, branchHash, readyForManualStart: true,
  steps: initial.steps.map(step => step.key === 'connection' ? step : { ...step, state: 'Ready' })
}
const stoppedProgress: LocalHostingStatus = {
  ...prepared, ok: false, code: 'LocalStoppedProgressNeedsSharing', message: 'The last stopped save is still local.', readyForManualStart: false,
  remedy: { kind: 'ShareStoppedProgress', title: 'Share stopped progress', message: 'Review the stopped save before sharing.' },
  steps: prepared.steps.map(step => step.key === 'save' ? { ...step, state: 'ActionRequired', message: 'Share the last stopped save before hosting.' } : step)
}
const response = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
// Existing hosting tests keep their access/check fixtures independent of the
// separate cached receiver read. Transfer behavior has explicit tests below.
const idleTransfer = { state: 'Ready', transferPhase: null, receivedBytes: 0, totalBytes: 0 }
function stubFetch(fetcher: (url: string, init?: RequestInit) => Promise<Response>) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) =>
    url.endsWith('/shared-world') ? response(idleTransfer) : fetcher(url, init)))
}
const props = { profileId, connectionId: 'first', worldName: 'Shared world', available: true, onOpenHost: vi.fn() }
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('local hosting response trust', () => {
  it('accepts verified local preparation while incoming connectivity remains unverified', () => {
    expect(parseLocalHostingStatus(prepared)).toBe(prepared)
  })
  it.each([
    { allowed: false }, { consented: false }, { ok: false }, { running: true }, { needsServerFile: true },
    { localProfileId: null }, { branchHash: null }, { branchHash: 'bad' }, { gamePort: 0 }, { controlPort: 65536 },
    { steps: prepared.steps.slice(1) },
    { steps: prepared.steps.map(step => step.key === 'save' ? { ...step, state: 'Unknown' } : step) },
    { steps: prepared.steps.map(step => step.key === 'server' ? { ...step, state: 'Passed' } : step) }
  ])('refuses inconsistent preparation %j', patch => {
    expect(() => parseLocalHostingStatus({ ...prepared, ...patch })).toThrow()
  })
})

async function openHosting() {
  const entry = await screen.findByRole('button', { name: 'Host locally' })
  await waitFor(() => expect(entry).toBeEnabled())
  fireEvent.click(entry)
  const dialog = screen.getByRole('dialog', { name: 'Host locally' })
  await waitFor(() => expect(dialog).not.toHaveAttribute('aria-busy', 'true'))
  return within(dialog)
}
async function confirmHosting() {
  const dialog = within(screen.getByRole('dialog', { name: 'Host locally' }))
  const button = await dialog.findByRole('button', { name: /^(Host locally|Retry and start|Start hosting)$/ })
  await waitFor(() => expect(button).toBeEnabled())
  fireEvent.click(button)
}

async function pendingPreparation(progressAtPoll: () => HostingProgress) {
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/progress')) return response(progressAtPoll())
    if (init?.method) {
      writes.push(url)
      if (url.endsWith('/prepare')) { signal = init.signal; return new Promise<Response>(resolve => { release = resolve }) }
      return response({ ok: true, code: 'Starting', message: 'Start requested.' })
    }
    return response(initial)
  }))
  const view = render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  return { view, writes, open, signal: () => signal, finish: (value: Response) => release(value) }
}

async function pendingStart(progressAtPoll: () => HostingProgress | null, statusAtRead: () => LocalHostingStatus = () => prepared) {
  const releases: ((value: Response) => void)[] = []
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/progress')) return response(progressAtPoll())
    if (init?.method) {
      writes.push(url)
      if (url.endsWith('/start')) { signal = init.signal; return new Promise<Response>(resolve => { releases.push(resolve) }) }
      throw new Error('Unexpected synthetic hosting mutation.')
    }
    return response(statusAtRead())
  }))
  const view = render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Start hosting' }))
  await act(async () => {})
  return { view, writes, open, signal: () => signal, finish: (value: Response, attempt = 0) => releases[attempt](value) }
}

it.each(['bytes', 'zero-byte files'] as const)('keeps one Start active beyond 120 seconds while its current copy %s advance', async mode => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-26T12:00:00Z')
  const began = Date.now()
  const flight = await pendingStart(() => ({ phase: 'PreparingCopy', state: 'Working', message: 'Preparing the protected hosting copy.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
    receivedBytes: mode === 'bytes' ? Date.now() - began : 0, totalBytes: mode === 'bytes' ? 1_000_000 : 0,
    completedFiles: Math.floor((Date.now() - began) / 1000), totalFiles: 315, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(181000) })
  expect(flight.signal()?.aborted).toBe(false)
  expect(screen.getByRole('button', { name: 'Requesting Start...' })).toBeDisabled()
  expect(screen.getByRole('region', { name: 'Hosting progress' })).toHaveTextContent('Preparing the hosting copy on this PC')
  expect(flight.writes).toEqual([`${base}/hosting/start`])
  await act(async () => { flight.finish(response({ ok: true, code: 'Starting', message: 'Start requested.' })) })
  expect(flight.open).toHaveBeenCalledExactlyOnceWith(localProfileId)
  expect(flight.writes).toEqual([`${base}/hosting/start`])
})

it('preserves an unconfirmed Start when an already-pending access read cancels it', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-26T12:00:00Z')
  let accessReads = 0
  let finishAccess!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  let began = Date.now()
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/access')) return ++accessReads === 1 ? response(prepared) : new Promise<Response>(resolve => { finishAccess = resolve })
    if (url.endsWith('/progress')) return response({ phase: 'Verifying', state: 'Working', message: 'Checking this attempt.',
      startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(), receivedBytes: 0, totalBytes: 0, percent: null })
    if (init?.method) { writes.push(url); signal = init.signal; began = Date.now(); return new Promise<Response>(() => {}) }
    return response(prepared)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  fireEvent.click(dialog.getByRole('button', { name: 'Start hosting' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(250) })
  await act(async () => { finishAccess(response({ allowed: false, code: 'HostingPermissionRequired', message: 'Hosting permission was removed.' })) })
  expect(signal?.aborted).toBe(true)
  expect(dialog.getByRole('status', { name: 'Earlier Start result' })).toBeInTheDocument()
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent('Hosting access changed')
  fireEvent.click(dialog.getByRole('button', { name: 'Check Start result' }))
  await act(async () => {})
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  expect(writes).toEqual([`${base}/hosting/start`])
})

it.each(['heartbeat', 'unchanged', 'changed-counts-with-old-timestamp', 'earlier-attempt', 'different-operation'] as const)(
  'does not extend Start authorization for %s progress', async mode => {
    vi.useFakeTimers(); vi.setSystemTime('2026-10-26T12:00:00Z')
    const began = Date.now()
    const flight = await pendingStart(() => ({ phase: 'Verifying', state: 'Working', message: 'Checking the current files.',
      startedUtc: new Date(mode === 'earlier-attempt' ? began - 10000 : mode === 'different-operation' && Date.now() - began > 1000 ? began + 1000 : began).toISOString(),
      updatedUtc: new Date(mode === 'unchanged' || mode === 'changed-counts-with-old-timestamp' ? began : Date.now()).toISOString(),
      receivedBytes: mode === 'changed-counts-with-old-timestamp' ? Date.now() - began : 0,
      totalBytes: mode === 'changed-counts-with-old-timestamp' ? 1_000_000 : 0,
      completedFiles: 0, totalFiles: 0, percent: null }))
    await act(async () => { await vi.advanceTimersByTimeAsync(120251) })
    expect(flight.signal()?.aborted).toBe(true)
    expect(screen.getByRole('status', { name: 'Earlier Start result' })).toHaveTextContent('unconfirmed')
    expect(screen.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
    expect(flight.writes).toEqual([`${base}/hosting/start`])
  })

it('keeps Start bounded by its original ten-minute intent despite continuously advancing work', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-26T12:00:00Z')
  const began = Date.now()
  const flight = await pendingStart(() => ({ phase: 'Verifying', state: 'Working', message: 'Checking the current files.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
    receivedBytes: Date.now() - began, totalBytes: 1_000_000, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(599999) })
  expect(flight.signal()?.aborted).toBe(false)
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(flight.signal()?.aborted).toBe(true)
  expect(screen.getByRole('alert')).toHaveTextContent('authorized time limit')
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(screen.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  expect(flight.writes).toEqual([`${base}/hosting/start`])
})

it('keeps a timed-out Start blocked through generic Prepared polls and reconciles only its matching terminal result with a fresh status', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-26T12:00:00Z')
  const began = Date.now()
  let progress: HostingProgress = { phase: 'Verifying', state: 'Working', message: 'Checking the current files.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(began).toISOString(), receivedBytes: 0, totalBytes: 0, percent: null }
  const flight = await pendingStart(() => progress)
  await act(async () => { await vi.advanceTimersByTimeAsync(130000) })
  const dialog = within(screen.getByRole('dialog'))
  expect(flight.signal()?.aborted).toBe(true)
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Check Start result' }))
  await act(async () => {})
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  progress = { ...progress, state: 'Failed', message: 'The earlier Start was canceled before launching.', updatedUtc: new Date(Date.now()).toISOString() }
  await act(async () => { await vi.advanceTimersByTimeAsync(1250) })
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Check Start result' }))
  await act(async () => {})
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeEnabled()
  expect(dialog.queryByRole('status', { name: 'Earlier Start result' })).not.toBeInTheDocument()
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(progress.message)
  expect(flight.writes).toEqual([`${base}/hosting/start`])
  expect(flight.open).not.toHaveBeenCalled()
  await act(async () => { flight.finish(response({ ok: true, code: 'Starting', message: 'Late result.' })) })
  expect(flight.open).not.toHaveBeenCalled()
  expect(flight.writes).toEqual([`${base}/hosting/start`])
})

it('does not reconcile a prior Start with another operation or a changed local-copy identity', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-26T12:00:00Z')
  const began = Date.now()
  let status = prepared
  let progress: HostingProgress = { phase: 'Verifying', state: 'Working', message: 'Checking the current files.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(began).toISOString(), receivedBytes: 0, totalBytes: 0, percent: null }
  const flight = await pendingStart(() => progress, () => status)
  await act(async () => { await vi.advanceTimersByTimeAsync(130000) })
  const dialog = within(screen.getByRole('dialog'))
  progress = { ...progress, state: 'Failed', startedUtc: new Date(began + 1000).toISOString(),
    updatedUtc: new Date(Date.now()).toISOString(), message: 'Different operation finished.' }
  await act(async () => { await vi.advanceTimersByTimeAsync(1250) })
  fireEvent.click(dialog.getByRole('button', { name: 'Check Start result' }))
  await act(async () => {})
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  expect(dialog.queryByText(progress.message)).not.toBeInTheDocument()
  status = { ...prepared, branchHash: 'B'.repeat(64) }
  progress = { ...progress, startedUtc: new Date(began).toISOString(), message: 'The original attempt finished.' }
  await act(async () => { await vi.advanceTimersByTimeAsync(1250) })
  fireEvent.click(dialog.getByRole('button', { name: 'Check Start result' }))
  await act(async () => {})
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  expect(flight.writes).toEqual([`${base}/hosting/start`])
  fireEvent.click(dialog.getByRole('button', { name: 'Earlier Start server details' }))
  expect(flight.open).toHaveBeenCalledExactlyOnceWith(localProfileId)
  expect(flight.writes).toEqual([`${base}/hosting/start`])
})

it('clears the old Start intent on a connection change and never adopts its progress or late completion for a new Start', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-26T12:00:00Z')
  const began = Date.now()
  const flight = await pendingStart(() => ({ phase: 'PreparingCopy', state: 'Working', message: 'Old connection copy work.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
    receivedBytes: Date.now() - began, totalBytes: 1_000_000, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  const oldSignal = flight.signal()
  flight.view.rerender(<FriendLocalHosting {...props} connectionId="second" onOpenHost={flight.open} />)
  expect(oldSignal?.aborted).toBe(true)
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  expect(flight.writes).toHaveLength(1)
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Start hosting' }))
  await act(async () => { flight.finish(response({ ok: true, code: 'Starting', message: 'Late old-connection result.' })) })
  expect(flight.open).not.toHaveBeenCalled()
  await act(async () => { await vi.advanceTimersByTimeAsync(120001) })
  expect(flight.signal()?.aborted).toBe(true)
  expect(screen.queryByText('Old connection copy work.')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  expect(flight.writes).toEqual([`${base}/hosting/start`, `${base}/hosting/start`])
})

it('does not treat a status read begun before terminal progress as fresh Start reconciliation', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-26T12:00:00Z')
  const began = Date.now()
  let progress: HostingProgress = { phase: 'Verifying', state: 'Working', message: 'Checking the current files.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(began).toISOString(), receivedBytes: 0, totalBytes: 0, percent: null }
  const flight = await pendingStart(() => progress)
  await act(async () => { await vi.advanceTimersByTimeAsync(130000) })
  const dialog = within(screen.getByRole('dialog'))
  let completeOldStatus!: (value: Response) => void
  vi.mocked(fetch).mockImplementationOnce(async url => {
    expect(String(url)).toBe(`${base}/hosting`)
    return new Promise<Response>(resolve => { completeOldStatus = resolve })
  })
  fireEvent.click(dialog.getByRole('button', { name: 'Check Start result' }))
  progress = { ...progress, state: 'Failed', message: 'This attempt finished.', updatedUtc: new Date(Date.now()).toISOString() }
  await act(async () => { await vi.advanceTimersByTimeAsync(1250) })
  await act(async () => { completeOldStatus(response(prepared)) })
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Check Start result' }))
  await act(async () => {})
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeEnabled()
  expect(flight.writes).toEqual([`${base}/hosting/start`])
})

it('does not extend the original Start intent after eight minutes of advancing preparation', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-26T12:00:00Z')
  const intentBegan = Date.now()
  let startBegan: number | null = null
  let finishPreparation!: (value: Response) => void
  let startSignal: AbortSignal | null | undefined
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/progress')) return response({ phase: 'Verifying', state: 'Working', message: 'Checking this attempt.',
      startedUtc: new Date(startBegan ?? intentBegan).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
      receivedBytes: Date.now() - (startBegan ?? intentBegan), totalBytes: 1_000_000, percent: null })
    if (init?.method) writes.push(url)
    if (url.endsWith('/prepare')) return new Promise<Response>(resolve => { finishPreparation = resolve })
    if (url.endsWith('/start')) { startBegan = Date.now(); startSignal = init?.signal; return new Promise<Response>(() => {}) }
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(480000) })
  await act(async () => { finishPreparation(response(prepared)) })
  expect(writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
  await act(async () => { await vi.advanceTimersByTimeAsync(119999) })
  expect(startSignal?.aborted).toBe(false)
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(startSignal?.aborted).toBe(true)
  expect(screen.getByRole('alert')).toHaveTextContent('authorized time limit')
  expect(writes).toHaveLength(2)
})

it('shares stopped progress only on an explicit click, then requires a separate Start without preparation', async () => {
  let latest = stoppedProgress
  let finishSharing!: (value: Response) => void
  const writes: { url: string; body: unknown }[] = []
  const opened = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push({ url, body: JSON.parse(String(init.body)) })
    if (url.endsWith('/share-stopped-progress')) return new Promise<Response>(resolve => { finishSharing = resolve })
    if (url.endsWith('/start')) return response({ ok: true, code: 'Starting', message: 'Start requested.' })
    return response(latest)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={opened} />)
  let dialog = await openHosting()
  expect(parseLocalHostingStatus(stoppedProgress)).toBe(stoppedProgress)
  expect(dialog.getByRole('button', { name: 'Share stopped progress' })).toBeEnabled()
  expect(dialog.queryByRole('button', { name: 'Start hosting' })).not.toBeInTheDocument()
  expect(writes).toEqual([])
  fireEvent.click(dialog.getByRole('button', { name: 'Share stopped progress' }))
  expect(dialog.getByRole('button', { name: 'Sharing stopped progress...' })).toBeDisabled()
  expect(dialog.getByRole('region', { name: 'Hosting progress' })).toHaveTextContent('This action does not start the server.')
  fireEvent.click(dialog.getByRole('button', { name: 'Sharing stopped progress...' }))
  expect(writes).toEqual([{ url: `${base}/hosting/share-stopped-progress`, body: { localProfileId, branchHash } }])
  latest = prepared
  await act(async () => { finishSharing(response({ ok: true, code: 'StoppedProgressShared', message: 'This stopped save is now shared within its signed history.' })) })
  await waitFor(() => expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeEnabled())
  expect(opened).not.toHaveBeenCalled()
  expect(writes).toHaveLength(1)
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await waitFor(() => expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeEnabled())
  fireEvent.click(dialog.getByRole('button', { name: 'Close Host locally' }))
  dialog = await openHosting()
  expect(writes).toHaveLength(1)
  fireEvent.click(dialog.getByRole('button', { name: 'Start hosting' }))
  await waitFor(() => expect(opened).toHaveBeenCalledWith(localProfileId))
  expect(writes.map(write => write.url)).toEqual([`${base}/hosting/share-stopped-progress`, `${base}/hosting/start`])
})

it.each(['StoppedProgressProofRequired', 'StoppedProgressSetupChanged', 'StoppedProgressBusy', 'StoppedProgressSharingChanged', 'StoppedProgressPublicationFailed'])(
  'keeps a real %s result and the sharing action across a generic prepared refresh', async code => {
    let latest = stoppedProgress
    const writes: string[] = []
    const failure = 'The stopped save needs review. Existing worlds and backups were kept.'
    stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method) { writes.push(url); return response({ ok: false, code, message: failure }) }
      return response(latest)
    }))
    render(<FriendLocalHosting {...props} onReviewSharedSave={vi.fn()} />)
    const dialog = await openHosting()
    fireEvent.click(dialog.getByRole('button', { name: 'Share stopped progress' }))
    await waitFor(() => expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(failure))
    expect(dialog.getByRole('button', { name: 'Share stopped progress' })).toBeDisabled()
    latest = prepared
    fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Share stopped progress' })).toBeEnabled())
    expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(failure)
    expect(dialog.getByRole('status', { name: 'Last check result' })).toHaveTextContent('the last stopped save still needs sharing')
    expect(dialog.getByRole('status', { name: 'Last check result' })).not.toHaveTextContent('this PC is prepared')
    expect(dialog.queryByRole('button', { name: 'Start hosting' })).not.toBeInTheDocument()
    expect(dialog.getByRole('heading', { name: 'Share this PC\'s stopped progress' })).toBeInTheDocument()
    expect(writes).toEqual([`${base}/hosting/share-stopped-progress`])
  })

it.each(['StoppedProgressProofRequired', 'UnreviewedFailure'])('retains safe sharing failure guidance for HTTP refusal %s', async code => {
  stubFetch(vi.fn(async (_url: string, init?: RequestInit) => init?.method
    ? new Response(JSON.stringify({ ok: false, code, message: 'Synthetic private path C:/private/backup/detail.txt' }), { status: 409 })
    : response(stoppedProgress)))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  fireEvent.click(dialog.getByRole('button', { name: 'Share stopped progress' }))
  await waitFor(() => expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(code === 'StoppedProgressProofRequired'
    ? 'could not be matched to its latest completed backup' : 'Sharing stopped progress could not be confirmed'))
  expect(dialog.queryByText(/Synthetic private path/)).not.toBeInTheDocument()
  expect(dialog.queryByRole('button', { name: 'Start hosting' })).not.toBeInTheDocument()
})

it('times out one pending sharing request without replay after a late success, refresh or navigation', async () => {
  vi.useFakeTimers()
  let finishSharing!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const opened = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); signal = init.signal; return new Promise<Response>(resolve => { finishSharing = resolve }) }
    return response(stoppedProgress)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={opened} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  let dialog = within(screen.getByRole('dialog'))
  fireEvent.click(dialog.getByRole('button', { name: 'Share stopped progress' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(119999) })
  expect(signal?.aborted).toBe(false)
  expect(dialog.getByRole('button', { name: 'Sharing stopped progress...' })).toBeDisabled()
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(signal?.aborted).toBe(true)
  expect(dialog.getByRole('alert')).toHaveTextContent('did not return a confirmed result within 2 minutes')
  await act(async () => { finishSharing(response({ ok: true, code: 'StoppedProgressShared', message: 'Late result.' })) })
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await act(async () => {})
  fireEvent.click(dialog.getByRole('button', { name: 'Close Host locally' }))
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
  dialog = within(screen.getByRole('dialog'))
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent('within 2 minutes')
  expect(writes).toEqual([`${base}/hosting/share-stopped-progress`])
  expect(opened).not.toHaveBeenCalled()
})

it('aborts sharing on a connection change and ignores its late result without replay', async () => {
  let finishSharing!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const opened = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); signal = init.signal; return new Promise<Response>(resolve => { finishSharing = resolve }) }
    return response(stoppedProgress)
  }))
  const view = render(<FriendLocalHosting {...props} onOpenHost={opened} />)
  const dialog = await openHosting()
  fireEvent.click(dialog.getByRole('button', { name: 'Share stopped progress' }))
  expect(signal?.aborted).toBe(false)
  view.rerender(<FriendLocalHosting {...props} connectionId="second" onOpenHost={opened} />)
  expect(signal?.aborted).toBe(true)
  await act(async () => { finishSharing(response({ ok: true, code: 'StoppedProgressShared', message: 'Late result.' })) })
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(writes).toEqual([`${base}/hosting/share-stopped-progress`])
  expect(opened).not.toHaveBeenCalled()
})

it('lets the user pause sharing and keeps its unconfirmed result after reopening without replay', async () => {
  let finishSharing!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); signal = init.signal; return new Promise<Response>(resolve => { finishSharing = resolve }) }
    return response(stoppedProgress)
  }))
  render(<FriendLocalHosting {...props} />)
  let dialog = await openHosting()
  fireEvent.click(dialog.getByRole('button', { name: 'Share stopped progress' }))
  fireEvent.click(dialog.getByRole('button', { name: 'Pause sharing' }))
  expect(signal?.aborted).toBe(true)
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  await act(async () => { finishSharing(response({ ok: true, code: 'StoppedProgressShared', message: 'Late result.' })) })
  dialog = await openHosting()
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent('paused before its result was confirmed')
  expect(writes).toEqual([`${base}/hosting/share-stopped-progress`])
})

it('retains the typed sharing remedy and failed Start result when later status polling says prepared', async () => {
  vi.useFakeTimers()
  let latest = prepared
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); latest = stoppedProgress; return response({ ok: false, code: stoppedProgress.code, message: stoppedProgress.message }) }
    return response(latest)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  fireEvent.click(dialog.getByRole('button', { name: 'Start hosting' }))
  await act(async () => {})
  expect(dialog.getByRole('button', { name: 'Share stopped progress' })).toBeEnabled()
  latest = prepared
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(stoppedProgress.message)
  expect(dialog.getByRole('button', { name: 'Share stopped progress' })).toBeEnabled()
  expect(dialog.queryByRole('button', { name: 'Start hosting' })).not.toBeInTheDocument()
  expect(writes).toEqual([`${base}/hosting/start`])
})

it.each([
  { allowed: false, code: 'HostingPermissionRequired', remedy: null },
  { code: 'HostingHistoryChoiceRequired', remedy: { kind: 'ChooseHistory' as const, title: 'Review the save histories', message: 'Keep all existing saves.' } },
  { running: true, code: 'LocalHostingRunning', remedy: null }
])('gives current permission, history and running checks priority over a retained stopped-sharing result: $code', async patch => {
  let latest = stoppedProgress
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(latest)
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  expect(dialog.getByRole('button', { name: 'Share stopped progress' })).toBeEnabled()
  latest = { ...stoppedProgress, ...patch }
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await waitFor(() => expect(screen.getByRole('dialog')).not.toHaveAttribute('aria-busy', 'true'))
  expect(dialog.queryByRole('button', { name: 'Share stopped progress' })).not.toBeInTheDocument()
  if (!patch.running) expect(dialog.getByRole('button', { name: /^(Host locally|Retry and start)$/ })).toBeDisabled()
  else expect(dialog.getByRole('button', { name: 'Open local server' })).toBeEnabled()
  expect(writes).toEqual([])
})

it.each([{ branchHash: 'B'.repeat(64) }, { localProfileId: '33333333-3333-4333-8333-333333333333' }])(
  'does not use a retained sharing result to authorize a changed checked hosting identity %j', async changedIdentity => {
    vi.useFakeTimers()
    let latest = stoppedProgress
    const writes: string[] = []
    stubFetch(vi.fn(async (url: string, init?: RequestInit) => { if (init?.method) writes.push(url); return response(latest) }))
    render(<FriendLocalHosting {...props} />)
    await act(async () => {})
    fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
    await act(async () => {})
    const dialog = within(screen.getByRole('dialog'))
    expect(dialog.getByRole('button', { name: 'Share stopped progress' })).toBeEnabled()
    latest = { ...prepared, ...changedIdentity }
    await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
    expect(dialog.getByRole('button', { name: 'Share stopped progress' })).toBeDisabled()
    expect(dialog.getByRole('heading', { name: 'Recheck the selected hosting copy' })).toBeInTheDocument()
    expect(dialog.getByRole('status', { name: 'Last check result' })).toHaveTextContent('selected hosting copy changed')
    fireEvent.click(dialog.getByRole('button', { name: 'Share stopped progress' }))
    expect(writes).toEqual([])
  })

it('shows current withdrawn copy consent instead of a retained sharing remedy and sends no write', async () => {
  vi.useFakeTimers()
  let latest = stoppedProgress
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => { if (init?.method) writes.push(url); return response(latest) }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  expect(dialog.getByRole('button', { name: 'Share stopped progress' })).toBeEnabled()
  latest = { ...stoppedProgress, consented: false, code: 'LocalSaveConsentRequired', message: 'Review current hosting-copy permission.',
    remedy: { kind: 'ConfirmCopy', title: 'Review current hosting-copy permission', message: 'Current copy permission is no longer confirmed.' } }
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(dialog.queryByRole('button', { name: 'Share stopped progress' })).not.toBeInTheDocument()
  expect(dialog.getByRole('heading', { name: 'Review current hosting-copy permission' })).toBeInTheDocument()
  expect(dialog.queryByRole('heading', { name: 'Share this PC\'s stopped progress' })).not.toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Host locally' }))
  expect(writes).toEqual([])
})

it.each(['action', 'card'] as const)('waits for confirmed permission before offering the %s entry', async presentation => {
  const calls: string[] = []
  const writes: string[] = []
  let finishPermission!: (value: Response) => void
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    if (url.endsWith('/access')) return new Promise<Response>(resolve => { finishPermission = resolve })
    return response({ ...initial, needsServerFile: true, serverFile: null })
  })
  stubFetch(fetcher)
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Checking hosting access...')
  expect(screen.queryByRole('button', { name: 'Check hosting access again' })).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`])
  await act(async () => { finishPermission(response({ allowed: true, code: 'HostingAllowed', message: 'The owner allows hosting.' })) })
  const entry = screen.getByRole('button', { name: 'Host locally' })
  expect(entry).toHaveTextContent('Host locally')
  expect(entry).toBeEnabled()
  expect(calls).toEqual([`${base}/hosting/access`])
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  fireEvent.click(entry)
  const dialog = await screen.findByRole('dialog', { name: 'Host locally' })
  expect(await within(dialog).findByRole('heading', { name: 'Install or find the game server' })).toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting`])
  expect(calls).toHaveLength(2)
  expect(writes).toEqual([])
})

it.each(['action', 'card'] as const)('hides the %s entry after a failed permission lookup', async presentation => {
  const calls: string[] = []
  stubFetch(vi.fn(async (url: string) => {
    calls.push(url)
    if (url.endsWith('/access')) throw new Error('Permission lookup unavailable.')
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Hosting access could not be checked.')
  expect(screen.getByRole('button', { name: 'Check hosting access again' })).toBeEnabled()
  expect(calls).toEqual([`${base}/hosting/access`])
})

it('shows elapsed indeterminate feedback for a stalled status check without polling mutation progress', async () => {
  vi.useFakeTimers()
  const calls: { url: string; method?: string }[] = []
  let finishChecks!: (value: Response) => void
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method })
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'The owner allows hosting.' })
    return new Promise<Response>(resolve => { finishChecks = resolve })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
  const progress = within(screen.getByRole('region', { name: 'Hosting progress' }))
  expect(progress.getByRole('progressbar', { name: 'Checking this PC...' })).not.toHaveAttribute('aria-valuenow')
  expect(progress.getByText('2s elapsed')).toBeInTheDocument()
  expect(calls).toEqual([{ url: `${base}/hosting/access`, method: undefined }, { url: `${base}/hosting`, method: undefined }])
  await act(async () => { finishChecks(response(initial)) })
  expect(screen.queryByRole('region', { name: 'Hosting progress' })).not.toBeInTheDocument()
})

it('shows a current progress failure while the preparation POST is pending without starting or unlocking another request', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-24T12:00:00Z')
  const began = new Date(Date.now()).toISOString()
  let progressState = { phase: 'Permission', state: 'Working', message: 'Checking current hosting access.',
    startedUtc: began, updatedUtc: began, receivedBytes: 0, totalBytes: 0, percent: null }
  const writes: string[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/progress')) return response(progressState)
    if (init?.method) { writes.push(url); return new Promise<Response>(() => {}) }
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(250) })
  progressState = { ...progressState, state: 'Failed', message: 'The current save check failed. Existing saves were kept.',
    updatedUtc: new Date(Date.now()).toISOString() }
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  const live = within(screen.getByRole('region', { name: 'Hosting progress' }))
  expect(live.getByText(progressState.message)).toBeInTheDocument()
  expect(live.queryByRole('progressbar')).not.toBeInTheDocument()
  expect(screen.getByRole('dialog')).toHaveAttribute('aria-busy', 'true')
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Preparing this PC...' })).toBeDisabled()
  await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
  expect(writes).toEqual([`${base}/hosting/prepare`])
  expect(open).not.toHaveBeenCalled()
})

it('retains an interrupted latest-save attempt and its phase after refreshing prerequisites without retrying preparation', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-24T12:00:00Z')
  const began = new Date(Date.now()).toISOString()
  const canceled = 'Hosting was canceled or interrupted. Existing saves were kept.'
  const writes: string[] = []
  let finishPreparation!: (value: Response) => void
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/access')) return response(initial)
    if (url.endsWith('/progress')) return response({ phase: 'LatestSave', state: 'Failed', message: canceled,
      startedUtc: began, updatedUtc: began, receivedBytes: 0, totalBytes: 0, percent: null })
    if (init?.method) { writes.push(url); return new Promise<Response>(resolve => { finishPreparation = resolve }) }
    return response({ ...initial, code: 'LocalHostingPreparationRequired' })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  fireEvent.click(dialog.getByRole('button', { name: 'Host locally' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(250) })
  expect(dialog.getByRole('button', { name: 'Retry automatic checks' })).toBeDisabled()
  await act(async () => { finishPreparation(new Response('', { status: 503 })) })
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(canceled)
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent('Last step: Finding the latest completed save.')
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  expect(guide.getByRole('button', { name: '3. Save' })).toHaveAttribute('aria-current', 'step')
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await act(async () => {})
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(canceled)
  expect(guide.getByRole('button', { name: '3. Save' })).toHaveAttribute('aria-current', 'step')
  expect(dialog.queryByText(/two different|different progress|history needs review/i)).not.toBeInTheDocument()
  expect(writes).toEqual([`${base}/hosting/prepare`])
})

it.each([
  { code: 'InstalledServerRequired', step: '2. Server app', heading: 'Install or select the dedicated server', needsServerFile: true },
  { code: 'LocalConnectionSetupRequired', step: '5. Connection', heading: 'Set up connections to this PC', needsServerFile: false }
])('recommends the typed $code repair ahead of the last verification phase and unprepared save', async ({ code, step, heading, needsServerFile }) => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-24T12:00:00Z')
  const began = new Date(Date.now()).toISOString()
  const flight = await pendingPreparation(() => ({ phase: 'Verifying', state: 'Working', message: 'Checking the prepared files.',
    startedUtc: began, updatedUtc: began, receivedBytes: 1024, totalBytes: 1024, completedFiles: 4, totalFiles: 4, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(250) })
  const dialog = within(screen.getByRole('dialog'))
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  const focusedControl = guide.getByRole('button', { name: '3. Save' })
  focusedControl.focus()
  const failed: LocalHostingStatus = { ...initial, ok: false, code, message: `Review the ${needsServerFile ? 'server app' : 'connection'} setup before continuing.`,
    needsServerFile, serverFile: needsServerFile ? null : initial.serverFile,
    steps: initial.steps.map(check => check.key === 'save' ? { ...check, state: 'Unknown' } :
      check.key === (needsServerFile ? 'server' : 'connection') ? { ...check, state: 'ActionRequired' } : check) }
  await act(async () => { flight.finish(response(failed)) })
  expect(guide.getByRole('button', { name: step })).toHaveAttribute('aria-current', 'step')
  expect(guide.getByRole('heading', { name: heading })).toBeInTheDocument()
  expect(focusedControl).toHaveFocus()
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent('Last step: Verifying the save files.')
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(failed.message)
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await act(async () => {})
  expect(guide.getByRole('button', { name: step })).toHaveAttribute('aria-current', 'step')
  expect(guide.getByRole('heading', { name: heading })).toBeInTheDocument()
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(failed.message)
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
  expect(flight.open).not.toHaveBeenCalled()
})

it.each(['network', 'http', 'malformed'] as const)('checks %s permission failures only on the explicit access action', async failure => {
  let accessReads = 0
  const calls: { url: string; method?: string; headers?: HeadersInit }[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method, headers: init?.headers })
    if (++accessReads > 1) return response({ allowed: true, code: 'HostingAllowed', message: 'The owner allows hosting.' })
    if (failure === 'network') throw new Error('Unavailable.')
    return failure === 'http' ? new Response(JSON.stringify({ code: 'AppBusy', message: 'Wait for this operation.' }), { status: 409 })
      : response({ allowed: 'true', code: 'HostingAllowed', message: 'Invalid permission.' })
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Hosting access could not be checked.')
  fireEvent.click(screen.getByRole('button', { name: 'Check hosting access again' }))
  await act(async () => {})
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  expect(screen.queryByRole('status', { name: 'Hosting access' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual([
    { url: `${base}/hosting/access`, method: undefined, headers: { 'X-TogetherServer-Local': '1' } },
    { url: `${base}/hosting/access/check`, method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' } }
  ])
  expect(open).not.toHaveBeenCalled()
})

it.each(['action', 'card'] as const)('keeps a timed-out %s entry hidden when the old permission result arrives late', async presentation => {
  vi.useFakeTimers()
  let finishPermission!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const calls: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url); signal = init?.signal
    return new Promise<Response>(resolve => { finishPermission = resolve })
  }))
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(15001) })
  expect(signal?.aborted).toBe(true)
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Hosting access check timed out.')
  expect(screen.getByRole('button', { name: 'Check hosting access again' })).toBeEnabled()
  await act(async () => { finishPermission(response({ allowed: true, code: 'HostingAllowed', message: 'Sharing was on.' })) })
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`])
})

it.each(['Unknown', 'RosterUnavailable', 'SharingReviewRequired'])('waits for %s hosting access to recover before offering the entry', async code => {
  vi.useFakeTimers()
  let restored = false
  const calls: string[] = []
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    return response(restored ? { allowed: true, code: 'HostingAllowed', message: 'The owner allows hosting.' }
      : { allowed: false, code, message: 'The saved hosting information needs checking.' })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('The saved hosting information needs checking.')
  expect(screen.getByRole('button', { name: 'Check hosting access again' })).toBeEnabled()
  restored = true
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting/access`])
  expect(writes).toEqual([])
})

it('keeps a retry result when a timed-out access read returns allowed later', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let accessReads = 0
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return ++accessReads === 1 ? new Promise<Response>(resolve => { release = resolve })
      : response({ allowed: false, code: 'HostingPermissionRequired', message: 'The owner has not allowed hosting.' })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(15000) })
  fireEvent.click(screen.getByRole('button', { name: 'Check hosting access again' }))
  await act(async () => {})
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('The owner has not allowed hosting.')
  await act(async () => { release(response({ allowed: true, code: 'HostingAllowed', message: 'Old grant.' })) })
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('The owner has not allowed hosting.')
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(accessReads).toBe(2)
  expect(writes).toEqual([`${base}/hosting/access/check`])
})

it.each(['profile', 'connection'] as const)('cancels a manual access retry when the selected %s changes', async change => {
  let release!: (value: Response) => void
  let retrySignal: AbortSignal | null | undefined
  let accessReads = 0
  const calls: string[] = []
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    if (++accessReads === 2) {
      retrySignal = init?.signal
      return new Promise<Response>(resolve => { release = resolve })
    }
    return response({ allowed: false, code: accessReads === 1 ? 'RosterUnavailable' : 'HostingPermissionRequired',
      message: accessReads === 1 ? 'Keep the Host reachable.' : 'This server is not shared with this PC.' })
  }))
  const view = render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Check hosting access again' }))
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Checking hosting access...')
  expect(screen.queryByRole('button', { name: 'Check hosting access again' })).not.toBeInTheDocument()
  const nextProfile = change === 'profile' ? '33333333-3333-4333-8333-333333333333' : profileId
  view.rerender(<FriendLocalHosting {...props} profileId={nextProfile} connectionId={change === 'connection' ? 'second' : 'first'} />)
  await act(async () => {})
  expect(retrySignal?.aborted).toBe(true)
  await act(async () => { release(response({ allowed: true, code: 'HostingAllowed', message: 'Old server grant.' })) })
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('This server is not shared with this PC.')
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting/access/check`,
    `/api/local/friend/${nextProfile}/shared-world/hosting/access`])
  expect(writes).toEqual([`${base}/hosting/access/check`])
})

it('returns to read-only permission polling after one explicit access check', async () => {
  vi.useFakeTimers()
  const calls: { url: string; method?: string; body?: BodyInit | null }[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method, body: init?.body })
    return response({ allowed: false, code: 'SharingPermissionUnknown', message: 'Check signed permission.' })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Check hosting access again' }))
  await act(async () => {})
  await act(async () => { await vi.advanceTimersByTimeAsync(20001) })
  expect(calls.map(call => call.method)).toEqual([undefined, 'POST', undefined, undefined])
  expect(calls.filter(call => call.method)).toEqual([{ url: `${base}/hosting/access/check`, method: 'POST', body: '{}' }])
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
})

it.each(['unavailable', 'blocked'] as const)('keeps hosting access hidden and cancels its lookup when %s', async guard => {
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const fetcher = vi.fn(async (_url: string, init?: RequestInit) => {
    signal = init?.signal
    return new Promise<Response>(resolve => { release = resolve })
  })
  stubFetch(fetcher)
  const view = render(<FriendLocalHosting {...props} available={false} />)
  expect(view.container).toBeEmptyDOMElement()
  expect(fetcher).not.toHaveBeenCalled()
  view.rerender(<FriendLocalHosting {...props} />)
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Checking hosting access...')
  view.rerender(<FriendLocalHosting {...props} available={guard !== 'unavailable'} accessBlocked={guard === 'blocked'} />)
  expect(signal?.aborted).toBe(true)
  await act(async () => { release(response({ allowed: true, code: 'HostingAllowed', message: 'Late grant.' })) })
  expect(view.container).toBeEmptyDOMElement()
  expect(fetcher).toHaveBeenCalledOnce()
})

it.each(['RosterRollback', 'HostingPermissionRequired', 'RosterUnavailable'])('marks checks blocked by %s as not run instead of failed', async code => {
  const writes: string[] = []
  const blocked: LocalHostingStatus = { ...initial, ok: false, allowed: false, code,
    message: 'Current hosting access needs review.',
    steps: initial.steps.map(step => ({ ...step, state: 'ActionRequired' })) }
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(url.endsWith('/access') ? { allowed: true, code: 'HostingAllowed', message: 'Sharing was allowed.' } : blocked)
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const rows = within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')
  expect(within(rows[0]).getByText('Needs attention')).toBeInTheDocument()
  for (const row of rows.slice(1)) {
    expect(within(row).getByText('Not checked')).toBeInTheDocument()
    expect(within(row).getByText(/This check has not run in this attempt/)).toBeInTheDocument()
    expect(within(row).queryByText('Needs attention')).not.toBeInTheDocument()
  }
  fireEvent.click(within(rows[3]).getByRole('button'))
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  expect(guide.getByText(/The ports check has not run in this attempt/)).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  expect(writes).toEqual([])
})

it('reports an unchanged history failure after a pending retry, then shows the actual failed port check after access recovers', async () => {
  let release!: (value: Response) => void
  let pending = false
  const writes: string[] = []
  const blocked: LocalHostingStatus = { ...initial, ok: false, allowed: false, code: 'RosterRollback',
    message: 'The saved sharing history changed unexpectedly.',
    steps: initial.steps.map(step => ({ ...step, state: 'ActionRequired' })) }
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    return pending ? new Promise<Response>(resolve => { release = resolve }) : response(blocked)
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  expect(guide.getByRole('status')).toHaveTextContent('Check finished: sharing history needs review.')
  pending = true
  fireEvent.click(guide.getByRole('button', { name: 'Check hosting access again' }))
  expect(guide.getByRole('status')).toHaveTextContent('Checking hosting access and this PC.')
  expect(guide.getByRole('status')).toHaveTextContent('This check does not start the server.')
  expect(dialog.getByRole('button', { name: 'Checking this PC...' })).toBeDisabled()
  await act(async () => { release(response(blocked)) })
  expect(guide.getByRole('status')).toHaveTextContent('Check finished: sharing history needs review.')
  expect(dialog.getByRole('status', { name: 'Last check result' })).toHaveTextContent('Refresh finished: results unchanged')
  expect(dialog.getByRole('status', { name: 'Last check result' })).toHaveTextContent('sharing history needs review')
  expect(guide.getByRole('button', { name: 'Check hosting access again' })).toBeEnabled()
  fireEvent.click(guide.getByRole('button', { name: 'Check hosting access again' }))
  await act(async () => { release(response({ ...prepared, ok: false, code: 'PortInUse', readyForManualStart: false,
    message: 'The required game port is in use.',
    steps: prepared.steps.map(step => step.key === 'ports' ? { ...step, state: 'ActionRequired', message: 'The required game port is in use.' } : step) })) })
  expect(guide.getByRole('status')).toHaveTextContent('Check finished. Next: Local game ports.')
  expect(dialog.getByRole('status', { name: 'Last check result' })).toHaveTextContent('Refresh finished: results updated')
  const rows = within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')
  expect(within(rows[3]).getByText('Needs attention')).toBeInTheDocument()
  expect(within(rows[3]).getByText('The required game port is in use.')).toBeInTheDocument()
  expect(within(rows[4]).getByText('Unverified')).toBeInTheDocument()
  expect(dialog.queryByText('Not checked')).not.toBeInTheDocument()
  expect(writes).toEqual([])
})

it('cancels preparation when the pending permission lookup returns an explicit denial', async () => {
  vi.useFakeTimers()
  let accessReads = 0
  let finishPermission!: (value: Response) => void
  let finishPreparation!: (value: Response) => void
  let preparationSignal: AbortSignal | null | undefined
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/access')) return ++accessReads === 1 ? response(initial)
      : new Promise<Response>(resolve => { finishPermission = resolve })
    if (init?.method) {
      writes.push(url); preparationSignal = init.signal
      return new Promise<Response>(resolve => { finishPreparation = resolve })
    }
    return response(initial)
  }))
  const open = vi.fn()
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  await act(async () => { finishPermission(response({ allowed: false, code: 'HostingPermissionRequired', message: 'The owner removed hosting access.' })) })
  expect(preparationSignal?.aborted).toBe(true)
  expect(screen.getByRole('heading', { name: 'Ask the owner to allow hosting' })).toBeInTheDocument()
  const guide = within(screen.getByRole('region', { name: 'Local hosting setup guide' }))
  fireEvent.click(guide.getByRole('button', { name: '1. Permission' }))
  expect(guide.getByRole('heading', { name: 'Ask the owner to allow shared hosting' })).toBeInTheDocument()
  expect(guide.queryByText('This PC is allowed to host')).not.toBeInTheDocument()
  await act(async () => { finishPreparation(response(prepared)) })
  expect(writes).toEqual([`${base}/hosting/prepare`])
  expect(open).not.toHaveBeenCalled()
})

it('uses one network confirmation to prepare and Start the exact checked save', async () => {
  const calls: { url: string; body?: Record<string, unknown> }[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, ...(init?.method ? { body: JSON.parse(String(init.body)) } : {}) })
    return response(url.endsWith('/prepare') ? prepared : url.endsWith('/start') ?
      { ok: true, code: 'Starting', message: 'Start requested.' } : initial)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  const dialog = await openHosting()
  expect(dialog.getByText(/Hosting lets other players connect to this PC through your network/)).toBeInTheDocument()
  expect(dialog.queryByRole('checkbox')).not.toBeInTheDocument()
  expect(dialog.queryByText(/Receive|Reshare|voter|choose a history/i)).not.toBeInTheDocument()
  expect(calls).toEqual([{ url: `${base}/hosting/access` }, { url: `${base}/hosting` }])
  await confirmHosting()
  await waitFor(() => expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId))
  expect(calls.slice(2)).toEqual([{ url: `${base}/hosting/prepare`, body: {
    allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null
  } }, { url: `${base}/hosting/start`, body: { localProfileId, branchHash, requestId: null, acceptSplitWarning: true } }])
})

it('starts the freshly checked prepared copy once after confirmation without preparing it again', async () => {
  const writes: { url: string; body: Record<string, unknown> }[] = []
  let finishStart!: (value: Response) => void
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push({ url, body: JSON.parse(String(init.body)) })
    if (url.endsWith('/start')) return new Promise<Response>(resolve => { finishStart = resolve })
    return response(prepared)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  const dialog = await openHosting()
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeEnabled()
  expect(dialog.queryByRole('button', { name: 'Retry and start' })).not.toBeInTheDocument()
  expect(dialog.getByText(/Start checks this PC and the prepared save again/)).toBeInTheDocument()
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await waitFor(() => expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeEnabled())
  expect(writes).toEqual([])
  await confirmHosting()
  fireEvent.click(dialog.getByRole('button', { name: 'Requesting Start...' }))
  expect(writes).toEqual([{ url: `${base}/hosting/start`, body: { localProfileId, branchHash, requestId: null, acceptSplitWarning: true } }])
  await act(async () => { finishStart(response({ ok: true, code: 'Starting', message: 'Start requested.' })) })
  await waitFor(() => expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId))
  expect(writes).toHaveLength(1)
})

it('starts an ordinary prepared branch once while its connection warning remains advisory', async () => {
  vi.useFakeTimers()
  const warning = 'This PC still needs an incoming connection check.'
  const ready: LocalHostingStatus = { ...prepared,
    steps: prepared.steps.map(step => step.key === 'connection' ? { ...step, state: 'ActionRequired', message: warning } : step) }
  const calls: { url: string; method?: string; body?: unknown }[] = []
  let finishStart!: (value: Response) => void
  const open = vi.fn()
  const connectionHelp = vi.fn()
  const gameSetup = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method, body: init?.body ? JSON.parse(String(init.body)) : undefined })
    if (url.endsWith('/start')) return new Promise<Response>(resolve => { finishStart = resolve })
    return response(url.endsWith('/progress') ? null : ready)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} onOpenConnectionHelp={connectionHelp} onOpenGameSetup={gameSetup} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog', { name: 'Host locally' }))
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeEnabled()
  const checks = within(dialog.getByRole('list', { name: 'Local hosting checks' }))
  expect(checks.getByText(warning).closest('li')).toHaveAttribute('data-state', 'ActionRequired')
  expect(dialog.getAllByRole('button', { name: 'Connection help' }).length).toBeGreaterThan(0)
  const initialReads = calls.length
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(calls.length).toBeGreaterThan(initialReads)
  expect(calls.every(call => call.method === undefined)).toBe(true)
  expect(connectionHelp).not.toHaveBeenCalled()
  expect(gameSetup).not.toHaveBeenCalled()
  fireEvent.click(dialog.getByRole('button', { name: 'Start hosting' }))
  await act(async () => {})
  fireEvent.click(dialog.getByRole('button', { name: 'Requesting Start...' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(calls.filter(call => call.method)).toEqual([{ url: `${base}/hosting/start`, method: 'POST',
    body: { localProfileId, branchHash, requestId: null, acceptSplitWarning: true } }])
  expect(open).not.toHaveBeenCalled()
  await act(async () => { finishStart(response({ ok: true, code: 'Starting', message: 'Start requested.' })) })
  expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId)
  expect(calls.filter(call => call.method)).toHaveLength(1)
})

it.each(['Prepared', 'Completed'] as const)('keeps an active %s planned move blocked by its connection warning', async recipientState => {
  vi.useFakeTimers()
  const blocked: LocalHostingStatus = { ...prepared,
    branchHash: recipientState === 'Completed' ? null : branchHash,
    handoffRequest: { pending: recipientState !== 'Completed', code: 'RecipientPrepared', message: 'Review this planned move.',
      successorDeviceId: '33333333-3333-4333-8333-333333333333', requestId: '44444444-4444-4444-8444-444444444444',
      recipientState, preparedVersionHash: branchHash, finalVersion: recipientState === 'Completed' ? 4 : null,
      receiptConfirmed: recipientState === 'Completed', canComplete: false, canCancel: recipientState !== 'Completed' },
    steps: prepared.steps.map(step => step.key === 'connection' ? { ...step, state: 'ActionRequired' } : step) }
  const calls: { url: string; method?: string }[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method })
    return response(blocked)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog', { name: 'Host locally' }))
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Start hosting' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(calls.every(call => call.method === undefined)).toBe(true)
})

it.each(['SharingPermissionUnknown', 'HostingPermissionRequired', 'SharingReviewRequired'])(
  'does not expose a prepared branch through %s hosting access', async code => {
    vi.useFakeTimers()
    const calls: { url: string; method?: string }[] = []
    stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method })
      return response(url.endsWith('/access') ? { allowed: false, code, message: 'Current signed hosting access is not confirmed.' }
        : { ...prepared, steps: prepared.steps.map(step => step.key === 'connection' ? { ...step, state: 'ActionRequired' } : step) })
    }))
    render(<FriendLocalHosting {...props} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(20000) })
    expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start hosting' })).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(calls.length).toBeGreaterThan(1)
    expect(calls.every(call => call.url === `${base}/hosting/access` && call.method === undefined)).toBe(true)
  })

it('starts once after a five-minute active preparation returns ready without requiring another click', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-25T12:00:00Z')
  const began = Date.now()
  const flight = await pendingPreparation(() => ({ phase: 'PreparingCopy', state: 'Working', message: 'Preparing the hosting copy.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
    receivedBytes: Date.now() - began, totalBytes: 1_000_000, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(301_000) })
  expect(flight.signal()?.aborted).toBe(false)
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
  await act(async () => { flight.finish(response(prepared)) })
  expect(flight.writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
  expect(flight.open).toHaveBeenCalledExactlyOnceWith(localProfileId)
  await act(async () => { await vi.advanceTimersByTimeAsync(30_000) })
  expect(flight.writes).toHaveLength(2)
})

it('omits unmeasured last-attempt counters after the final connection phase resets completed copy progress', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-25T12:00:00Z')
  const began = new Date(Date.now()).toISOString()
  let current: HostingProgress = { phase: 'PreparingCopy', state: 'Working', message: 'Preparing the hosting copy.',
    startedUtc: began, updatedUtc: began, receivedBytes: 114 * 1024 * 1024, totalBytes: 114 * 1024 * 1024,
    completedFiles: 315, totalFiles: 315, percent: 100 }
  const flight = await pendingPreparation(() => current)
  await act(async () => { await vi.advanceTimersByTimeAsync(250) })
  current = { ...current, phase: 'Connection', message: 'Checking this PC’s connection.',
    updatedUtc: new Date(Date.now()).toISOString(), receivedBytes: 0, totalBytes: 0, completedFiles: 0, totalFiles: 0, percent: null }
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  flight.view.rerender(<FriendLocalHosting {...props} disabled onOpenHost={flight.open} />)
  await act(async () => { flight.finish(response(prepared)) })
  const result = within(screen.getByRole('status', { name: 'Last hosting attempt' }))
  expect(result.getByText(prepared.message)).toBeInTheDocument()
  expect(result.getByText('Last step: Checking this PC’s connection.')).toBeInTheDocument()
  expect(result.queryByText(/save bytes|copy bytes|files (prepared|checked|reported)/i)).not.toBeInTheDocument()
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
})

it('keeps measured zero-of-known-total bytes and files visible in the last hosting attempt', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-25T12:00:00Z')
  const began = new Date(Date.now()).toISOString()
  const flight = await pendingPreparation(() => ({ phase: 'PreparingCopy', state: 'Working', message: 'Preparing the hosting copy.',
    startedUtc: began, updatedUtc: began, receivedBytes: 0, totalBytes: 1024, completedFiles: 0, totalFiles: 315, percent: 0 }))
  await act(async () => { await vi.advanceTimersByTimeAsync(250) })
  flight.view.rerender(<FriendLocalHosting {...props} disabled onOpenHost={flight.open} />)
  await act(async () => { flight.finish(response(prepared)) })
  const result = within(screen.getByRole('status', { name: 'Last hosting attempt' }))
  expect(result.getByText('Hosting copy bytes prepared: 0 B of 1 KiB.')).toBeInTheDocument()
  expect(result.getByText('0 of 315 files prepared for verification.')).toBeInTheDocument()
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
})

it('starts an already completed planned move with its exact request instead of preparing it again', async () => {
  const requestId = '44444444-4444-4444-8444-444444444444'
  const moved: LocalHostingStatus = { ...prepared, branchHash: null, handoffRequest: {
    pending: false, code: 'OldHostFenced', message: 'The hosting move completed.',
    successorDeviceId: '33333333-3333-4333-8333-333333333333', requestId, recipientState: 'Completed',
    preparedVersionHash: branchHash, finalVersion: 4, receiptConfirmed: true, canComplete: false, canCancel: false
  } }
  const writes: { url: string; body: Record<string, unknown> }[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push({ url, body: JSON.parse(String(init.body)) })
    return response(url.endsWith('/start') ? { ok: true, code: 'Starting', message: 'Start requested.' } : moved)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await openHosting()
  expect(writes).toEqual([])
  await confirmHosting()
  await waitFor(() => expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId))
  expect(writes).toEqual([{ url: `${base}/hosting/start`, body: { branchHash: null, requestId, localProfileId, acceptSplitWarning: false } }])
})

it('cancels an explicit prepared-copy Start on navigation and ignores its late result without preparing or retrying', async () => {
  let finishStart!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/start')) { signal = init?.signal; return new Promise<Response>(resolve => { finishStart = resolve }) }
    return response(prepared)
  }))
  const view = render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await openHosting(); await confirmHosting()
  expect(writes).toEqual([`${base}/hosting/start`])
  expect(signal?.aborted).toBe(false)
  view.rerender(<FriendLocalHosting {...props} connectionId="second" onOpenHost={open} />)
  expect(signal?.aborted).toBe(true)
  await act(async () => { finishStart(response({ ok: true, code: 'Starting', message: 'Late result.' })) })
  expect(open).not.toHaveBeenCalled()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(writes).toEqual([`${base}/hosting/start`])
})

it.each([
  { kind: 'Connection' as const, code: 'LocalConnectionSetupRequired' },
  { kind: 'ChooseHistory' as const, code: 'HostingHistoryChoiceRequired' },
  { kind: 'Review' as const, code: 'LocalHostingReviewRequired' }
])('does not repeat preparation or start a ready copy with a typed $kind blocker', async ({ kind, code }) => {
  const writes: string[] = []
  const blocked: LocalHostingStatus = { ...prepared, code,
    remedy: { kind, title: 'Review before hosting', message: 'Resolve the current hosting check before starting.' } }
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => { if (init?.method) writes.push(url); return response(blocked) }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Start hosting' }))
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await waitFor(() => expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled())
  expect(writes).toEqual([])
})

it('explains preparation before confirmation, then preserves an actual save failure without starting', async () => {
  const unprepared: LocalHostingStatus = { ...initial, ok: false, code: 'LocalHostingPreparationRequired', consented: true,
    message: 'Choose Host locally to get the latest completed save and prepare this PC.',
    steps: initial.steps.map(step => step.key === 'save' ? { ...step, state: 'Unknown' } : step) }
  const failed: LocalHostingStatus = { ...unprepared, code: 'SharedChunkUnavailable',
    message: 'The source no longer has a required part of this save. Ask it to share a completed backup again.',
    remedy: { kind: 'GetSave', title: 'Get a completed save', message: 'Generic save advice.' } }
  let latest = unprepared
  const calls: { url: string; method?: string }[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method })
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'The owner allows hosting.' })
    if (url.endsWith('/prepare')) latest = failed
    return response(latest)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} onReviewSharedSave={vi.fn()} />)
  const dialog = await openHosting()
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  expect(guide.getByRole('heading', { name: 'Prepare this PC\'s hosting copy' })).toBeInTheDocument()
  expect(guide.getByText(/receives it if needed and prepares a separate local copy/)).toBeInTheDocument()
  expect(dialog.queryByText(/interrupted transfer|stop the game server safely/i)).not.toBeInTheDocument()
  expect(calls).toEqual([{ url: `${base}/hosting/access`, method: undefined }, { url: `${base}/hosting`, method: undefined }])
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  expect(open).not.toHaveBeenCalled()
  await confirmHosting()
  await waitFor(() => expect(guide.getByRole('heading', { name: 'Get a completed verified save' })).toBeInTheDocument())
  expect(within(dialog.getByRole('region', { name: 'Next hosting step' })).getByText(failed.message)).toBeInTheDocument()
  expect(guide.getByText(failed.message)).toBeInTheDocument()
  expect(guide.getByRole('button', { name: 'Review the shared saves' })).toBeEnabled()
  expect(guide.getByRole('button', { name: 'Recheck the save' })).toBeEnabled()
  latest = unprepared // A status read reports prerequisites, not the last preparation outcome.
  fireEvent.click(guide.getByRole('button', { name: 'Recheck the save' }))
  await act(async () => {})
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent(failed.message)
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent('Refreshing checks does not retry this hosting attempt.')
  expect(guide.getByRole('heading', { name: 'Get a completed verified save' })).toBeInTheDocument()
  expect(guide.getByText(failed.message)).toBeInTheDocument()
  expect(calls.filter(call => call.method)).toEqual([{ url: `${base}/hosting/prepare`, method: 'POST' }])
  expect(calls.at(-1)).toEqual({ url: `${base}/hosting`, method: undefined })
  expect(open).not.toHaveBeenCalled()
})

it.each(['action', 'card'] as const)('hides the %s affordance when permission is denied', async presentation => {
  const fetcher = vi.fn(async () => response({ ...initial, allowed: false, code: 'HostingPermissionRequired' }))
  stubFetch(fetcher)
  const view = render(<FriendLocalHosting {...props} presentation={presentation} />)
  await waitFor(() => expect(fetcher).toHaveBeenCalledOnce())
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  await waitFor(() => expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent(initial.message))
  expect(screen.queryByRole('button', { name: 'Check hosting access again' })).not.toBeInTheDocument()
  view.rerender(<FriendLocalHosting {...props} presentation={presentation} accessBlocked />)
  expect(fetcher).toHaveBeenCalledOnce()
})

it.each(['action', 'card'] as const)('updates the %s entry when the owner enables then disables hosting without making writes', async presentation => {
  vi.useFakeTimers()
  let enabled = false
  const calls: string[] = []
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    return response({ allowed: enabled, code: enabled ? 'HostingAllowed' : 'HostingPermissionRequired',
      message: enabled ? 'The owner allows hosting.' : 'Shared hosting is off.' })
  }))
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  enabled = true
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  enabled = false
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Shared hosting is off.')
  expect(screen.queryByRole('button', { name: 'Check hosting access again' })).not.toBeInTheDocument()
  expect(calls).toEqual(Array<string>(3).fill(`${base}/hosting/access`))
  expect(writes).toEqual([])
})

it.each(['profile', 'connection'] as const)('does not carry confirmed permission across a %s change before reset effects run', async change => {
  vi.useFakeTimers()
  let finishPermission!: (value: Response) => void
  let accessReads = 0
  const calls: string[] = []
  const writes: string[] = []
  const entryAtCommit: boolean[] = []
  const continueAtCommit: (boolean | null)[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    if (!url.endsWith('/access')) return response(initial)
    return ++accessReads === 1 ? response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
      : new Promise<Response>(resolve => { finishPermission = resolve })
  }))
  function ScopeProbe({ nextProfile = profileId, nextConnection = 'first' }: { nextProfile?: string; nextConnection?: string }) {
    useLayoutEffect(() => {
      const buttons = screen.queryAllByRole('button', { name: 'Host locally' })
      entryAtCommit.push(buttons.some(button => !button.closest('dialog, [role="dialog"]')))
      const confirm = buttons.find(button => button.closest('dialog, [role="dialog"]'))
      continueAtCommit.push(confirm ? !(confirm as HTMLButtonElement).disabled : null)
    }, [nextProfile, nextConnection])
    return <FriendLocalHosting {...props} profileId={nextProfile} connectionId={nextConnection} />
  }
  const view = render(<ScopeProbe />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' })).toBeEnabled()
  const nextProfile = change === 'profile' ? '33333333-3333-4333-8333-333333333333' : profileId
  view.rerender(<ScopeProbe nextProfile={nextProfile} nextConnection={change === 'connection' ? 'second' : 'first'} />)
  expect(entryAtCommit).toEqual([false, false])
  expect(continueAtCommit).toEqual([null, false])
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  await act(async () => { finishPermission(response({ allowed: false, code: 'HostingPermissionRequired', message: 'Shared hosting is off.' })) })
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting`, `/api/local/friend/${nextProfile}/shared-world/hosting/access`])
  expect(writes).toEqual([])
})

it.each(['action', 'card'] as const)('keeps an open guide recoverable after the %s entry loses permission', async presentation => {
  vi.useFakeTimers()
  let enabled = true
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(url.endsWith('/access') ? { allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' }
      : enabled ? initial : { ...initial, ok: false, allowed: false, code: 'HostingPermissionRequired', message: 'The owner disabled shared hosting.' })
  }))
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog', { name: 'Host locally' }))
  enabled = false
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  const confirm = dialog.getByRole('button', { name: 'Host locally' })
  expect(confirm).toBeDisabled()
  expect(screen.getAllByRole('button', { name: 'Host locally' })).toEqual([confirm])
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(dialog.getByRole('heading', { name: 'Ask the owner to allow hosting' })).toBeInTheDocument()
  expect(dialog.getByRole('region', { name: 'Local hosting setup guide' })).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Retry automatic checks' })).toBeEnabled()
  enabled = true
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await act(async () => {})
  expect(confirm).toBeEnabled()
  expect(writes).toEqual([])
})

it('shows the missing install and continues with the selected file without a separate save permission', async () => {
  const missing: LocalHostingStatus = { ...initial, ok: false, code: 'InstalledServerRequired', needsServerFile: true, serverFile: null }
  const writes: { url: string; body: Record<string, unknown> }[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push({ url, body: JSON.parse(String(init.body)) })
    return response(missing)
  }))
  const browse = vi.fn(async () => 'C:/Selected/valheim_server.exe')
  render(<FriendLocalHosting {...props} onBrowseServerFile={browse} />)
  const dialog = await openHosting()
  expect(dialog.getByRole('heading', { name: 'Install or find the game server' })).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Browse' }))
  await confirmHosting()
  await waitFor(() => expect(writes).toHaveLength(1))
  expect(writes[0].body.serverFile).toBe('C:/Selected/valheim_server.exe')
  expect(dialog.getByRole('button', { name: 'Retry and start' })).toBeEnabled()
})

it.each([true, false])('opens the server picker from the guide and reports its result without starting (selected: %s)', async selected => {
  let finishBrowse!: (value: string | null) => void
  const writes: string[] = []
  const missing: LocalHostingStatus = { ...initial, needsServerFile: true, serverFile: null,
    steps: initial.steps.map(step => step.key === 'server' ? { ...step, state: 'ActionRequired' } : step) }
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(missing)
  }))
  const browse = vi.fn(() => new Promise<string | null>(resolve => { finishBrowse = resolve }))
  render(<FriendLocalHosting {...props} onBrowseServerFile={browse} />)
  const dialog = await openHosting()
  fireEvent.change(dialog.getByRole('textbox', { name: 'Installed server file' }), { target: { value: 'C:/Previous/valheim_server.exe' } })
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  fireEvent.click(guide.getByRole('button', { name: 'Choose the installed server file' }))
  expect(browse).toHaveBeenCalledExactlyOnceWith('Valheim')
  expect(guide.getByRole('status')).toHaveTextContent('Choose the installed dedicated server file in the file picker.')
  expect(guide.getByRole('button', { name: 'Choose the installed server file' })).toBeDisabled()
  expect(dialog.getByRole('button', { name: 'Choosing server file...' })).toBeDisabled()
  await act(async () => { finishBrowse(selected ? 'C:/Selected/valheim_server.exe' : null) })
  expect(dialog.getByRole('textbox', { name: 'Installed server file' })).toHaveValue(selected ? 'C:/Selected/valheim_server.exe' : 'C:/Previous/valheim_server.exe')
  expect(guide.getByRole('status')).toHaveTextContent(selected ? 'Server file selected. Choose Host locally to verify it' : 'No server file was selected. Your previous choice is kept.')
  expect(guide.getByRole('button', { name: 'Choose the installed server file' })).toBeEnabled()
  expect(writes).toEqual([])
})

it('explains manual file entry when the server picker is unavailable', async () => {
  stubFetch(vi.fn(async () => response({ ...initial, needsServerFile: true, serverFile: null,
    steps: initial.steps.map(step => step.key === 'server' ? { ...step, state: 'ActionRequired' } : step) })))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  fireEvent.click(guide.getByRole('button', { name: 'Choose the installed server file' }))
  expect(dialog.getByRole('textbox', { name: 'Installed server file' })).toHaveFocus()
  expect(guide.getByRole('status')).toHaveTextContent('Enter the installed server file above, then choose Host locally.')
  expect(guide.getByRole('status')).toHaveTextContent('The file is checked before Start.')
})

it('requires Minecraft terms to be accepted locally before the network confirmation can proceed', async () => {
  const writes: { url: string; body: Record<string, unknown> }[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push({ url, body: JSON.parse(String(init.body)) })
    return response({ ...initial, game: 'MinecraftJava' })
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const terms = dialog.getByRole('checkbox', { name: /I have read and accept the terms/ })
  expect(terms).not.toBeChecked()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  fireEvent.click(terms)
  expect(writes).toEqual([])
  await confirmHosting()
  await waitFor(() => expect(writes).toEqual([{ url: `${base}/hosting/prepare`, body: {
    allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null, acceptedTerms: true
  } }]))
})

it('shows acceptance progress, then a retryable timeout without accepting or starting again from a late reply', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const invitation: LocalHostingStatus = { ...initial, handoffRequest: {
    pending: true, code: 'WaitingForRecipient', message: 'Waiting for this PC.',
    successorDeviceId: '33333333-3333-4333-8333-333333333333', requestId: '44444444-4444-4444-8444-444444444444',
    recipientState: 'Requested', preparedVersionHash: null, finalVersion: null,
    receiptConfirmed: false, canComplete: false, canCancel: true
  } }
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) {
      writes.push(url); signal = init.signal
      return new Promise<Response>(resolve => { release = resolve })
    }
    return response(invitation)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  fireEvent.click(dialog.getByRole('button', { name: 'Host locally' }))
  expect(dialog.getByRole('button', { name: 'Accepting move...' })).toBeDisabled()
  expect(within(dialog.getByRole('region', { name: 'Local hosting setup guide' })).getByRole('status')).toHaveTextContent('Accepting the hosting move')
  await act(async () => { await vi.advanceTimersByTimeAsync(15000) })
  expect(signal?.aborted).toBe(true)
  expect(dialog.getByRole('alert')).toHaveTextContent('Retry the checks to see whether it was accepted before responding again.')
  expect(dialog.getByRole('button', { name: 'Retry automatic checks' })).toBeEnabled()
  await act(async () => {
    release(response({ ok: true, code: 'HostingAccepted', message: 'Late acceptance.' }))
    await vi.advanceTimersByTimeAsync(10000)
  })
  expect(writes).toEqual([`${base}/handoff/respond`])
  expect(dialog.queryByText('Late acceptance.')).not.toBeInTheDocument()
})

it('offers Connection help before a profile exists without changing the network from a status read', async () => {
  const help = vi.fn()
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response({ ...initial, remedy: { kind: 'Connection', title: 'Let friends connect to this PC', message: 'Review the incoming address.' } })
  }))
  render(<FriendLocalHosting {...props} onOpenConnectionHelp={help} />)
  const dialog = await openHosting()
  fireEvent.click(dialog.getByRole('button', { name: 'Connection help' }))
  expect(help).toHaveBeenCalledExactlyOnceWith(profileId)
  expect(writes).toEqual([])
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it.each([true, false])('offers port setup only for an existing local profile (%s)', async hasProfile => {
  const setup = vi.fn()
  stubFetch(vi.fn(async () => response({ ...initial, consented: true, branchHash,
    localProfileId: hasProfile ? localProfileId : null, code: 'PortInUse' })))
  render(<FriendLocalHosting {...props} onOpenGameSetup={setup} />)
  const dialog = await openHosting()
  if (hasProfile) {
    fireEvent.click(dialog.getByRole('button', { name: 'Open game setup' }))
    expect(setup).toHaveBeenCalledExactlyOnceWith('Valheim', localProfileId)
  } else expect(dialog.queryByRole('button', { name: 'Open game setup' })).not.toBeInTheDocument()
})

it.each(['HostingHistoryChoiceRequired', 'WorldHistoryReviewRequired'])('keeps %s protected behind one comprehensible blocker', async code => {
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response({ ...initial, code })
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  expect(dialog.getByRole('heading', { name: 'This server has two different save histories' })).toBeInTheDocument()
  expect(dialog.getByText(/All existing saves are kept/)).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  expect(dialog.queryByRole('combobox')).not.toBeInTheDocument()
  expect(writes).toEqual([])
})

it('uses general save review for SharedSaveHistoryNeedsReview without asserting a history conflict or corruption', async () => {
  const writes: string[] = []
  const status: LocalHostingStatus = { ...initial, ok: false, code: 'SharedSaveHistoryNeedsReview',
    message: 'The available shared save could not be selected and verified. Keep its source PC reachable and retry.' }
  expect(localHostingAttention(status)?.kind).toBe('Review')
  stubFetch(vi.fn(async (_url: string, init?: RequestInit) => { if (init?.method) writes.push('POST'); return response(status) }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  expect(guide.getByRole('heading', { name: 'Review the available shared save' })).toBeInTheDocument()
  expect(guide.getByText(status.message)).toBeInTheDocument()
  expect(screen.getByRole('dialog').textContent).not.toMatch(/two different|different progress|conflicting saves|damaged|corrupt/i)
  fireEvent.click(guide.getByRole('button', { name: 'Recheck the save' }))
  await act(async () => {})
  expect(writes).toEqual([])
})

it.each(['connection changed', 'actions disabled', 'access removed'] as const)('cancels the pending Start when %s', async reason => {
  let release!: (value: Response) => void
  const writes: string[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); return new Promise<Response>(resolve => { release = resolve }) }
    return response(initial)
  }))
  const view = render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await openHosting(); await confirmHosting()
  if (reason === 'connection changed') view.rerender(<FriendLocalHosting {...props} connectionId="second" onOpenHost={open} />)
  else if (reason === 'actions disabled') view.rerender(<FriendLocalHosting {...props} onOpenHost={open} disabled />)
  else view.rerender(<FriendLocalHosting {...props} onOpenHost={open} accessBlocked />)
  await act(async () => { release(response(prepared)) })
  expect(writes).toEqual([`${base}/hosting/prepare`])
  expect(open).not.toHaveBeenCalled()
})

it('never starts or accepts terms or installs anything from polling', async () => {
  vi.useFakeTimers()
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => { if (init?.method) writes.push(url); return response(prepared) }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(40000) })
  expect(writes).toEqual([])
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it('does not repeat Start after a failure and a later ready poll', async () => {
  vi.useFakeTimers()
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(url.endsWith('/start') ? { ok: false, code: 'PortInUse', message: 'A game port is busy.' } : prepared)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Start hosting' }))
  await act(async () => {})
  expect(screen.getByRole('heading', { name: 'Free the game ports on this PC' })).toBeInTheDocument()
  await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
  expect(writes).toEqual([`${base}/hosting/start`])
})

it('keeps Start busy beyond 30 seconds and accepts a delayed reply exactly once', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/start')) {
      signal = init?.signal
      return new Promise<Response>(resolve => { release = resolve })
    }
    return response(prepared)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Start hosting' }))
  await act(async () => {})
  expect(writes).toEqual([`${base}/hosting/start`])
  await act(async () => { await vi.advanceTimersByTimeAsync(31000) })
  expect(signal?.aborted).toBe(false)
  expect(screen.getByRole('dialog')).toHaveAttribute('aria-busy', 'true')
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Requesting Start...' })).toBeDisabled()
  expect(within(screen.getByRole('region', { name: 'Local hosting setup guide' })).getByRole('status')).toHaveTextContent('Requesting Start for the checked local save.')
  expect(open).not.toHaveBeenCalled()
  expect(writes).toEqual([`${base}/hosting/start`])
  await act(async () => { release(response({ ok: true, code: 'Starting', message: 'Start requested.' })) })
  expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId)
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  await act(async () => { await vi.advanceTimersByTimeAsync(120001) })
  expect(signal?.aborted).toBe(false)
  expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId)
  expect(writes).toEqual([`${base}/hosting/start`])
})

it.each([true, false])('expires inactive Start at exactly 120 seconds and ignores a late reply without retrying (success: %s)', async ok => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const open = vi.fn()
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/start')) {
      signal = init?.signal
      return new Promise<Response>(resolve => { release = resolve })
    }
    return response(prepared)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Start hosting' }))
  await act(async () => {})
  expect(writes).toEqual([`${base}/hosting/start`])
  await act(async () => { await vi.advanceTimersByTimeAsync(119999) })
  expect(signal?.aborted).toBe(false)
  expect(screen.getByRole('dialog')).toHaveAttribute('aria-busy', 'true')
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(signal?.aborted).toBe(true)
  expect(screen.getByRole('dialog')).not.toHaveAttribute('aria-busy', 'true')
  const timeoutMessage = /No new Start progress was reported for 2 minutes/
  expect(screen.getByRole('alert')).toHaveTextContent(timeoutMessage)
  expect(screen.getByRole('button', { name: 'Check Start result' })).toBeEnabled()
  await act(async () => {
    release(response({ ok, code: ok ? 'Starting' : 'PortInUse', message: 'Late result.' }))
    await vi.advanceTimersByTimeAsync(30000)
  })
  expect(screen.getByRole('alert')).toHaveTextContent(timeoutMessage)
  expect(screen.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  const lastAttempt = screen.getByRole('status', { name: 'Last hosting attempt' })
  expect(lastAttempt).toHaveTextContent('Wait for the matching result from this Start attempt. Choose Check Start result to read current checks, or Earlier Start server details to review the server. Checking does not retry Start.')
  expect(lastAttempt).not.toHaveTextContent('choose Host locally to try again')
  expect(open).not.toHaveBeenCalled()
  expect(writes).toEqual([`${base}/hosting/start`])
})

it('bounds preparation and ignores a late result without retrying Start', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); signal = init.signal; return new Promise<Response>(resolve => { release = resolve }) }
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Preparing this PC...' })).toBeDisabled()
  expect(within(screen.getByRole('region', { name: 'Local hosting setup guide' })).getByRole('status')).toHaveTextContent('Getting the latest completed save')
  await act(async () => { await vi.advanceTimersByTimeAsync(120001) })
  expect(signal?.aborted).toBe(true)
  expect(screen.getByRole('button', { name: 'Retry automatic checks' })).toBeEnabled()
  await act(async () => { release(response(prepared)); await vi.advanceTimersByTimeAsync(10000) })
  expect(writes).toEqual([`${base}/hosting/prepare`])
})

it('keeps preparation active beyond 120 seconds while new copy-byte progress is observed, then accepts its result once', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-24T12:00:00Z')
  const began = Date.now()
  const flight = await pendingPreparation(() => ({ phase: 'PreparingCopy', state: 'Working', message: 'Preparing the hosting copy.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
    receivedBytes: Date.now() - began, totalBytes: 1_000_000, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(131_000) })
  expect(flight.signal()?.aborted).toBe(false)
  expect(screen.getByRole('dialog')).toHaveAttribute('aria-busy', 'true')
  expect(screen.getByRole('progressbar', { name: 'Hosting copy bytes prepared' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Retry automatic checks' })).toBeDisabled()
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
  await act(async () => { flight.finish(response(prepared)) })
  expect(flight.open).toHaveBeenCalledExactlyOnceWith(localProfileId)
  expect(flight.writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
  await act(async () => { await vi.advanceTimersByTimeAsync(120_001) })
  expect(flight.signal()?.aborted).toBe(false)
  expect(flight.open).toHaveBeenCalledOnce()
})

it.each(['unchanged reply', 'timestamp only', 'counters without a newer timestamp'] as const)('expires preparation after 120 seconds without work progress despite %s polls', async mode => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-24T12:00:00Z')
  const began = Date.now()
  const flight = await pendingPreparation(() => ({ phase: 'PreparingCopy', state: 'Working',
    message: `Waiting for progress at ${Date.now()}.`, startedUtc: new Date(began).toISOString(),
    updatedUtc: new Date(mode === 'timestamp only' ? Date.now() : began).toISOString(),
    receivedBytes: mode === 'counters without a newer timestamp' ? Date.now() - began : 256,
    totalBytes: 1_000_000, completedFiles: 1, totalFiles: 4, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(120_249) })
  expect(flight.signal()?.aborted).toBe(false)
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(flight.signal()?.aborted).toBe(true)
  const dialog = within(screen.getByRole('dialog'))
  expect(dialog.getByRole('alert')).toHaveTextContent('No new preparation progress was reported for 2 minutes.')
  expect(dialog.getByRole('alert')).toHaveTextContent('Last step: Preparing the hosting copy on this PC.')
  expect(dialog.getByRole('status', { name: 'Last hosting attempt' })).toHaveTextContent('1 of 4 files prepared for verification.')
  expect(dialog.queryByText(/history conflict|different progress/i)).not.toBeInTheDocument()
  await act(async () => { flight.finish(response(prepared)); await vi.advanceTimersByTimeAsync(30_000) })
  expect(flight.open).not.toHaveBeenCalled()
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
})

it('renews inactivity when a newer phase reports work without measured bytes', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-24T12:00:00Z')
  const began = Date.now()
  const flight = await pendingPreparation(() => ({ phase: Date.now() - began < 90_000 ? 'PeerSync' : 'LatestSave',
    state: 'Working', message: 'Checking approved shared-save information.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
    receivedBytes: 0, totalBytes: 0, completedFiles: 0, totalFiles: 0, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(131_000) })
  expect(flight.signal()?.aborted).toBe(false)
  expect(screen.getByRole('progressbar', { name: 'Finding the latest completed save' })).not.toHaveAttribute('value')
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
  flight.view.unmount()
  expect(flight.signal()?.aborted).toBe(true)
})

it('treats advancing file counts for zero-byte files as work progress', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-24T12:00:00Z')
  const began = Date.now()
  const flight = await pendingPreparation(() => ({ phase: 'PreparingCopy', state: 'Working', message: 'Preparing files for verification.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
    receivedBytes: 0, totalBytes: 0, completedFiles: Math.floor((Date.now() - began) / 1000), totalFiles: 1000, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(131_000) })
  expect(flight.signal()?.aborted).toBe(false)
  expect(screen.getByRole('dialog')).toHaveAttribute('aria-busy', 'true')
  expect(screen.getByText('130 of 1000 files prepared for verification.')).toBeInTheDocument()
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
  flight.view.unmount()
  expect(flight.signal()?.aborted).toBe(true)
})

it('caps preparation at ten minutes despite continuous byte progress and ignores its late result', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-24T12:00:00Z')
  const began = Date.now()
  const flight = await pendingPreparation(() => ({ phase: 'PreparingCopy', state: 'Working', message: 'Preparing the hosting copy.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
    receivedBytes: Date.now() - began, totalBytes: 1_000_000, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(599_999) })
  expect(flight.signal()?.aborted).toBe(false)
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(flight.signal()?.aborted).toBe(true)
  expect(within(screen.getByRole('dialog')).getByRole('alert')).toHaveTextContent('Preparation reached its 10-minute time limit.')
  await act(async () => { flight.finish(response(prepared)); await vi.advanceTimersByTimeAsync(30_000) })
  expect(flight.open).not.toHaveBeenCalled()
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
})

it.each(['connection change', 'unmount'] as const)('aborts extended preparation on %s and clears its watchdog without retrying', async navigation => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-24T12:00:00Z')
  const began = Date.now()
  const flight = await pendingPreparation(() => ({ phase: 'PeerSync', state: 'Working', message: 'Checking approved PCs.',
    startedUtc: new Date(began).toISOString(), updatedUtc: new Date(Date.now()).toISOString(),
    receivedBytes: Date.now() - began, totalBytes: 1_000_000, percent: null }))
  await act(async () => { await vi.advanceTimersByTimeAsync(131_000) })
  expect(flight.signal()?.aborted).toBe(false)
  if (navigation === 'unmount') flight.view.unmount()
  else flight.view.rerender(<FriendLocalHosting {...props} connectionId="second" onOpenHost={flight.open} />)
  expect(flight.signal()?.aborted).toBe(true)
  await act(async () => { flight.finish(response(prepared)); await vi.advanceTimersByTimeAsync(600_001) })
  expect(flight.open).not.toHaveBeenCalled()
  expect(flight.writes).toEqual([`${base}/hosting/prepare`])
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it('withdraws stale permission when an automatic check stalls', async () => {
  vi.useFakeTimers()
  let reads = 0
  let release!: (value: Response) => void
  stubFetch(vi.fn(async () => ++reads <= 2 ? response(initial) : new Promise<Response>(resolve => { release = resolve })))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(25001) })
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' })).toBeDisabled()
  await act(async () => { release(response(prepared)) })
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' })).toBeDisabled()
})

it('always shows all automatic check results and rejects unreviewed action destinations', async () => {
  expect(() => parseLocalHostingStatus({ ...prepared, remedy: { kind: 'https://untrusted', title: 'Click', message: 'Click' } })).toThrow()
  expect(localHostingAttention(initial)).toBeNull()
  expect(localHostingAttention(prepared)).toBeNull()
  stubFetch(vi.fn(async () => response(initial)))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  expect(dialog.getByRole('heading', { name: 'Automatically checked results' }).closest('details')).toBeNull()
  expect(within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')).toHaveLength(5)
  expect(dialog.getByText('Last checked 0s ago')).toBeInTheDocument()
})

it('retains the same check rows and timestamp through a failed retry, then recovers on reopen without waiting for a permission poll', async () => {
  let result: 'ready' | 'waiting' | 'error' = 'ready'
  let release!: (value: Response) => void
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    if (result === 'error') return new Response(JSON.stringify({ code: 'HostUnavailable', message: 'The Host could not be reached.' }), { status: 503 })
    if (result === 'waiting') return new Promise<Response>(resolve => { release = resolve })
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const list = dialog.getByRole('list', { name: 'Local hosting checks' })
  const rows = within(list).getAllByRole('listitem')
  const checked = dialog.getByText(/Last checked/).getAttribute('datetime')
  result = 'waiting'
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  expect(within(list).getAllByRole('listitem')).toEqual(rows)
  expect(dialog.getByText(/Checking again/)).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Checking this PC...' })).toBeDisabled()
  await act(async () => { release(response(initial)) })
  result = 'error'
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await waitFor(() => expect(dialog.getByRole('alert')).toHaveTextContent('The Host could not be reached.'))
  expect(within(list).getAllByRole('listitem')).toEqual(rows)
  expect(dialog.getByText(/Showing the last results/)).toBeInTheDocument()
  expect(dialog.getByRole('status', { name: 'Last check result' })).toHaveTextContent('Refresh failed')
  expect(within(dialog.getByRole('region', { name: 'Local hosting setup guide' })).getByRole('status')).toHaveTextContent('The checks could not finish.')
  expect(dialog.getByText(/Last checked/)).toHaveAttribute('datetime')
  expect(checked).toBeTruthy()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Close Host locally' }))
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  result = 'ready'
  const reopened = await openHosting()
  expect(reopened.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  expect(writes).toEqual([])
})

it('keeps the list visible on the first pending read and updates a last-checked timer without hiding it', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  stubFetch(vi.fn(async (url: string) => url.endsWith('/access')
    ? response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    : new Promise<Response>(resolve => { release = resolve })))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  const dialog = within(screen.getByRole('dialog'))
  const rows = within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')
  expect(rows).toHaveLength(5)
  for (const row of rows) expect(within(row).getByText('Checking…')).toBeInTheDocument()
  expect(dialog.getByText(/Waiting for the first check/)).toBeInTheDocument()
  await act(async () => { release(response(initial)) })
  await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
  expect(dialog.getByText('Last checked 3s ago')).toBeInTheDocument()
  expect(within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')).toEqual(rows)
})

it('preserves displayed checks after an automatic timeout and ignores a late result', async () => {
  vi.useFakeTimers()
  let reads = 0
  let release!: (value: Response) => void
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    return ++reads === 1 ? response(initial) : new Promise<Response>(resolve => { release = resolve })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  const rows = within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')
  await act(async () => { await vi.advanceTimersByTimeAsync(25001) })
  expect(within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')).toEqual(rows)
  expect(dialog.getByText(/Showing the last results/)).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  await act(async () => { release(response(prepared)) })
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  expect(writes).toEqual([])
})

it('explains unprepared checks without claiming a save failure or changing stable refresh text', async () => {
  const status: LocalHostingStatus = { ...initial, code: 'LocalHostingPreparationRequired',
    steps: initial.steps.map(step => step.key === 'save' ? { ...step, state: 'Unknown' } : step) }
  const writes: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(status)
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const rows = within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')
  for (const row of [rows[2], rows[3]]) {
    expect(within(row).getByText('Not checked')).toBeInTheDocument()
    expect(within(row).getAllByText(/Host locally/).length).toBeGreaterThan(0)
  }
  expect(within(rows[2]).getByText(/Receiving and file verification run after/)).toBeInTheDocument()
  expect(within(rows[3]).getByText(/waiting for this PC’s hosting copy and game setup/)).toBeInTheDocument()
  const refresh = dialog.getByRole('button', { name: 'Retry automatic checks' })
  fireEvent.click(refresh)
  await waitFor(() => expect(dialog.getByRole('status', { name: 'Last check result' })).toHaveTextContent('results unchanged'))
  expect(refresh).toHaveTextContent('Retry automatic checks')
  expect(writes).toEqual([])
})

it('shows a safe retryable refresh failure without reflecting an unexpected error, path or identifier', async () => {
  let fail = false
  const privateError = 'InternalLatestSelectionException C:\\Private\\save 33333333-3333-4333-8333-333333333333'
  stubFetch(vi.fn(async () => fail
    ? new Response(JSON.stringify({ code: 'UnexpectedInternalFailure', message: privateError }), { status: 500 })
    : response(initial)))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  fail = true
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await waitFor(() => expect(dialog.getByRole('status', { name: 'Last check result' })).toHaveTextContent('Refresh failed'))
  expect(dialog.getByRole('alert')).toHaveTextContent('The hosting request could not be checked.')
  expect(dialog.queryByText(/InternalLatestSelectionException|Private|33333333/)).not.toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
})

it.each(['failed', 'timed out'])('keeps the entry hidden when a later permission read %s after an earlier denial', async failure => {
  vi.useFakeTimers()
  let accessReads = 0
  const calls: string[] = []
  stubFetch(vi.fn(async (url: string) => {
    calls.push(url)
    if (!url.endsWith('/access')) return response(initial)
    if (++accessReads === 1) return response({ allowed: false, code: 'HostingPermissionRequired', message: 'Sharing was off.' })
    if (failure === 'timed out') return new Promise<Response>(() => {})
    throw new Error('The current permission is unavailable.')
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  await act(async () => { await vi.advanceTimersByTimeAsync(failure === 'timed out' ? 25001 : 10001) })
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting/access`])
})

it('waits for an in-flight automatic check before letting Browse change the selected server', async () => {
  vi.useFakeTimers()
  let statusReads = 0
  let release!: (value: Response) => void
  const missing = { ...initial, needsServerFile: true, serverFile: null }
  const browse = vi.fn(async () => 'C:/Selected/valheim_server.exe')
  stubFetch(vi.fn(async (url: string) => {
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    if (++statusReads === 1) return response(missing)
    return new Promise<Response>(resolve => { release = resolve })
  }))
  render(<FriendLocalHosting {...props} onBrowseServerFile={browse} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(dialog.getByRole('button', { name: 'Browse' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Browse' }))
  expect(browse).not.toHaveBeenCalled()
  await act(async () => { release(response(missing)) })
  expect(dialog.getByRole('button', { name: 'Browse' })).toBeEnabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Browse' }))
  await act(async () => {})
  expect(browse).toHaveBeenCalledOnce()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeEnabled()
})

it.each(['action', 'card'] as const)('disables the %s entry through receive, verification and receipt without dispatching any action', async presentation => {
  vi.useFakeTimers()
  let phase: 'Receiving' | 'Verifying' | 'Receipt' | null = 'Receiving'
  const calls: { url: string; method?: string; headers?: HeadersInit }[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method, headers: init?.headers })
    if (url === base) return response({ state: phase ?? 'Ready', transferPhase: phase, receivedBytes: 100, totalBytes: 100 })
    return response(initial)
  }))
  const open = vi.fn()
  render(<FriendLocalHosting {...props} presentation={presentation} onOpenHost={open} />)
  await act(async () => {})
  const entry = screen.getByRole('button', { name: 'Host locally' })
  expect(entry).toBeDisabled()
  const progress = within(screen.getByRole('region', { name: 'Save transfer progress' }))
  expect(progress.getByText('100% received')).toBeInTheDocument()
  expect(progress.getByRole('list', { name: 'Save transfer stages' }).querySelector('[aria-current="step"]')).toHaveTextContent('Receive save')
  fireEvent.click(entry)
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  phase = 'Verifying'
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(entry).toBeDisabled()
  expect(progress.getByText('Verifying the shared save')).toBeInTheDocument()
  expect(progress.queryByText('100% received')).not.toBeInTheDocument()
  expect(progress.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
  phase = 'Receipt'
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(entry).toBeDisabled()
  expect(progress.getByText('Confirming save receipt')).toBeInTheDocument()
  phase = null
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(entry).toBeEnabled()
  expect(screen.queryByRole('region', { name: 'Save transfer progress' })).not.toBeInTheDocument()
  expect(calls.every(call => call.method === undefined)).toBe(true)
  expect(calls.filter(call => call.url === base)).toHaveLength(4)
  expect(calls.filter(call => call.url === base).every(call => JSON.stringify(call.headers) === JSON.stringify({ 'X-TogetherServer-Local': '1' }))).toBe(true)
  expect(open).not.toHaveBeenCalled()
})

it('disables dialog confirmation when a background transfer starts after the hosting checks', async () => {
  vi.useFakeTimers()
  let transferPhase: 'Verifying' | 'Receipt' | null = null
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(url === base ? { ...idleTransfer, transferPhase } : prepared)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog', { name: 'Host locally' }))
  const confirm = dialog.getByRole('button', { name: 'Start hosting' })
  expect(confirm).toBeEnabled()
  transferPhase = 'Verifying'
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(confirm).toBeDisabled()
  expect(dialog.getByRole('region', { name: 'Save transfer progress' })).toHaveTextContent('Verifying the shared save')
  fireEvent.click(confirm)
  transferPhase = 'Receipt'
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(confirm).toBeDisabled()
  transferPhase = null
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(confirm).toBeEnabled()
  expect(writes).toEqual([])
})

it('shows one preparation progress panel and waits for the background receipt before its single authorized Start', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-28T12:00:00Z')
  const began = new Date(Date.now()).toISOString()
  let transferPhase: 'Receiving' | 'Receipt' | null = null
  let finishPreparation!: (value: Response) => void
  const writes: string[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (url === base) return response({ ...idleTransfer, transferPhase })
    if (url.endsWith('/progress')) return response({ phase: 'Receiving', state: 'Working', message: 'Receiving the save.',
      startedUtc: began, updatedUtc: new Date(Date.now()).toISOString(), receivedBytes: 25, totalBytes: 100, percent: 25 })
    if (init?.method) {
      writes.push(url)
      if (url.endsWith('/prepare')) return new Promise<Response>(resolve => { finishPreparation = resolve })
      return response({ ok: true, code: 'Starting', message: 'Start requested.' })
    }
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog', { name: 'Host locally' }))
  fireEvent.click(dialog.getByRole('button', { name: 'Host locally' }))
  transferPhase = 'Receiving'
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(dialog.getByRole('region', { name: 'Hosting progress' })).toHaveTextContent('Receiving the save')
  expect(dialog.queryByRole('region', { name: 'Save transfer progress' })).not.toBeInTheDocument()
  transferPhase = 'Receipt'
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  await act(async () => { finishPreparation(response(prepared)) })
  expect(dialog.getByRole('region', { name: 'Save transfer progress' })).toHaveTextContent('Confirming save receipt')
  expect(dialog.getByRole('button', { name: 'Start hosting' })).toBeDisabled()
  expect(writes).toEqual([`${base}/hosting/prepare`])
  transferPhase = null
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
  expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId)
})

it.each(['network', 'malformed', 'timeout'] as const)('keeps hosting disabled during %s background status failure and recovers only from a fresh read', async failure => {
  vi.useFakeTimers()
  let state: 'pending' | 'failed' | 'ready' = 'pending'
  let finish!: (value: Response) => void
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url !== base) return response(initial)
    if (state === 'ready') return response(idleTransfer)
    if (failure === 'network') throw new Error('Unavailable.')
    if (failure === 'malformed') return response({ ...idleTransfer, transferPhase: 'Finished' })
    return new Promise<Response>(resolve => { finish = resolve })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  if (failure === 'timeout') await act(async () => { await vi.advanceTimersByTimeAsync(4000) })
  expect(screen.getByRole('region', { name: 'Save transfer progress' })).toHaveTextContent('Save transfer status unavailable')
  state = 'ready'
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  if (failure === 'timeout') {
    await act(async () => { finish(response({ ...idleTransfer, transferPhase: 'Receiving' })) })
    expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  }
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(writes).toEqual([])
})

const approvedPort = { id: '44444444-4444-4444-8444-444444444444', game: 'Valheim' as const, gamePort: 2460, factorioRconPort: null }
const portApprovalRequired: LocalHostingStatus = { ...initial, code: 'LocalPortApprovalRequired', needsPortApproval: true,
  selectedApprovedPortId: null, approvedPorts: [], factorioRconPort: null }

it.each([
  { needsPortApproval: 'yes' }, { approvedPorts: null }, { selectedApprovedPortId: 'bad' },
  { selectedApprovedPortId: approvedPort.id }, { approvedPorts: [{ ...approvedPort, game: 'MinecraftJava' }] },
  { approvedPorts: [{ ...approvedPort, gamePort: 65535 }] }
])('refuses invalid local port approvals %j', patch => {
  expect(() => parseLocalHostingStatus({ ...portApprovalRequired, ...patch })).toThrow()
})

it('keeps a suggested game port unapproved until the local checkbox is chosen, then remembers only that deliberate choice', async () => {
  const bodies: unknown[] = []
  const calls: string[] = []
  stubFetch(vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (url.endsWith('/start')) return response({ ok: true, code: 'Started', message: 'Started.' })
    if (init?.method === 'POST') {
      bodies.push(JSON.parse(String(init.body)))
      return response({ ...prepared, gamePort: 2460, needsPortApproval: false, selectedApprovedPortId: approvedPort.id,
        approvedPorts: [approvedPort], factorioRconPort: null })
    }
    return response(portApprovalRequired)
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const confirm = dialog.getByRole('button', { name: 'Host locally' })
  expect(confirm).toBeDisabled()
  expect(dialog.getByRole('spinbutton', { name: 'Local hosting game port' })).toHaveValue(2456)
  fireEvent.change(dialog.getByRole('spinbutton', { name: 'Local hosting game port' }), { target: { value: '2460' } })
  expect(confirm).toBeDisabled()
  expect(bodies).toEqual([])
  fireEvent.click(dialog.getByRole('checkbox', { name: 'Approve these game ports for reuse on this PC.' }))
  expect(confirm).toBeEnabled()
  fireEvent.click(confirm)
  await waitFor(() => expect(calls).toContain(`${base}/hosting/start`))
  expect(bodies).toEqual([expect.objectContaining({ gamePort: 2460, factorioRconPort: null, approvePorts: true })])
  expect(bodies[0]).not.toHaveProperty('approvedPortId')
})

it('offers approved plans for reuse without another approval and keeps an existing prepared server’s selector fixed', async () => {
  const writes: unknown[] = []
  stubFetch(vi.fn(async (_url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(JSON.parse(String(init.body))); return new Promise<Response>(() => {}) }
    return response({ ...portApprovalRequired, approvedPorts: [approvedPort] })
  }))
  const view = render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  fireEvent.change(dialog.getByRole('combobox', { name: 'Approved local game ports' }), { target: { value: approvedPort.id } })
  expect(dialog.queryByRole('checkbox', { name: 'Approve these game ports for reuse on this PC.' })).not.toBeInTheDocument()
  fireEvent.click(dialog.getByRole('button', { name: 'Host locally' }))
  await waitFor(() => expect(writes).toHaveLength(1))
  expect(writes[0]).toHaveProperty('approvedPortId', approvedPort.id)
  expect(writes[0]).not.toHaveProperty('gamePort')
  view.unmount()
  stubFetch(vi.fn(async () => response({ ...prepared, gamePort: 2460, needsPortApproval: false,
    selectedApprovedPortId: approvedPort.id, approvedPorts: [approvedPort], factorioRconPort: null })))
  render(<FriendLocalHosting {...props} />)
  const existing = await openHosting()
  expect(existing.getByRole('combobox', { name: 'Approved local game ports' })).toBeDisabled()
  expect(existing.getByText('This prepared server keeps its saved ports. Open its local Setup to change them.')).toBeInTheDocument()
})

it('clears a draft game-port approval when the saved Host connection changes', async () => {
  stubFetch(vi.fn(async () => response(portApprovalRequired)))
  const view = render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  fireEvent.click(dialog.getByRole('checkbox', { name: 'Approve these game ports for reuse on this PC.' }))
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  view.rerender(<FriendLocalHosting {...props} connectionId="second" />)
  const next = await openHosting()
  expect(next.getByRole('checkbox', { name: 'Approve these game ports for reuse on this PC.' })).not.toBeChecked()
  expect(next.getByRole('button', { name: 'Host locally' })).toBeDisabled()
})

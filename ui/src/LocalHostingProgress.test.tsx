import { act, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LocalHostingProgress, parseHostingProgress, type HostingProgress } from './LocalHostingProgress'

const instant = '2026-10-24T12:00:00Z'
const endpoint = '/api/local/friend/11111111-1111-4111-8111-111111111111/shared-world/hosting/progress'
const progress = (patch: Partial<HostingProgress> = {}): HostingProgress => ({ phase: 'Receiving', state: 'Working',
  message: 'Receiving the latest completed save from an approved PC.', startedUtc: instant, updatedUtc: instant,
  receivedBytes: 256, totalBytes: 1024, percent: 25, ...patch })
const response = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

describe('honest hosting progress', () => {
  it('rejects malformed counters, states, timestamps and invented percentages', () => {
    expect(parseHostingProgress(null)).toBeNull()
    expect(parseHostingProgress(progress())).toEqual(progress())
    for (const patch of [{ receivedBytes: -1 }, { receivedBytes: 1025 }, { totalBytes: 1.5 }, { percent: 101 },
      { phase: '' }, { message: 3 }, { state: 'Ready' }, { startedUtc: 'later' }, { updatedUtc: '2026-10-23T12:00:00Z' },
      { completedFiles: 1 }, { completedFiles: -1, totalFiles: 3 }, { completedFiles: 4, totalFiles: 3 },
      { completedFiles: 1.5, totalFiles: 3 }, { completedFiles: 0, totalFiles: '3' }])
      expect(() => parseHostingProgress({ ...progress(), ...patch })).toThrow(/progress/)
  })

  it('shows measured copy bytes and file counts as preparation still awaiting verification', async () => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    let current = progress({ phase: 'PreparingCopy', message: 'Preparing the protected hosting copy.',
      completedFiles: 75, totalFiles: 315 })
    vi.stubGlobal('fetch', vi.fn(async () => response(current)))
    const update = vi.fn()
    render(<LocalHostingProgress endpoint={endpoint} title="Preparing this PC" message="Checking the save." onUpdate={update} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(250) })
    expect(screen.getByRole('progressbar', { name: 'Hosting copy bytes prepared' })).toHaveAttribute('value', '256')
    expect(screen.getByText('25% of copy bytes prepared')).toBeInTheDocument()
    expect(screen.getByText('75 of 315 files prepared for verification.')).toBeInTheDocument()
    expect(screen.getByText(/These bytes still need final file verification/)).toBeInTheDocument()
    expect(screen.queryByText('25% received')).not.toBeInTheDocument()
    expect(update).toHaveBeenCalledExactlyOnceWith(current)
    current = progress({ phase: 'Verifying', receivedBytes: 1024, completedFiles: 315, totalFiles: 315,
      message: 'Verifying the prepared files.', updatedUtc: new Date(Date.now()).toISOString() })
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByRole('progressbar', { name: 'Verifying the save files' })).not.toHaveAttribute('value')
    expect(screen.getByText('315 of 315 files checked.')).toBeInTheDocument()
    expect(screen.queryByText(/100%/)).not.toBeInTheDocument()
    expect(update).toHaveBeenLastCalledWith(current)
  })

  it('hides default zero file counters and keeps non-copy file evidence neutral', async () => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    let current = progress({ phase: 'Permission', receivedBytes: 0, totalBytes: 0, completedFiles: 0, totalFiles: 0 })
    vi.stubGlobal('fetch', vi.fn(async () => response(current)))
    render(<LocalHostingProgress endpoint={endpoint} title="Preparing this PC" message="Checking the save." />)
    await act(async () => { await vi.advanceTimersByTimeAsync(250) })
    expect(screen.queryByText(/0 of 0 files/)).not.toBeInTheDocument()
    current = progress({ phase: 'PeerSync', receivedBytes: 0, totalBytes: 0, completedFiles: 1, totalFiles: 4,
      updatedUtc: new Date(Date.now()).toISOString() })
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByText('1 of 4 files reported.')).toBeInTheDocument()
    expect(screen.queryByText(/files prepared for verification|files checked/)).not.toBeInTheDocument()
  })

  it('updates real received bytes and then shows verification as an indeterminate stage', async () => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    let current = progress()
    const fetcher = vi.fn(async () => response(current))
    vi.stubGlobal('fetch', fetcher)
    render(<LocalHostingProgress endpoint={endpoint} title="Preparing this PC" message="Checking the save." />)
    expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
    await act(async () => { await vi.advanceTimersByTimeAsync(250) })
    expect(screen.getByRole('progressbar', { name: 'Shared save bytes received' })).toHaveAttribute('value', '256')
    expect(screen.getByText('25% received')).toBeInTheDocument()
    expect(screen.getByText('256 B of 1 KiB')).toBeInTheDocument()
    current = progress({ receivedBytes: 768, percent: 75, updatedUtc: new Date(Date.now()).toISOString() })
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByText('75% received')).toBeInTheDocument()
    current = progress({ phase: 'Verifying', receivedBytes: 1024, percent: null,
      message: 'Checking every received file against its signed hash.', updatedUtc: new Date(Date.now()).toISOString() })
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByRole('progressbar', { name: 'Verifying the save files' })).not.toHaveAttribute('aria-valuenow')
    expect(screen.queryByText(/100%/)).not.toBeInTheDocument()
    expect(screen.getByText('2s elapsed')).toBeInTheDocument()
    expect(screen.getByText('Earlier: Receiving the save')).toBeInTheDocument()
    expect(fetcher).toHaveBeenLastCalledWith(endpoint, expect.objectContaining({ headers: { 'X-TogetherServer-Local': '1' } }))
  })

  it('keeps elapsed indeterminate feedback when progress is unavailable and never estimates completion', async () => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('No progress reply.') }))
    render(<LocalHostingProgress endpoint={endpoint} title="Checking hosting access" message="Waiting for the approved PC." />)
    await act(async () => { await vi.advanceTimersByTimeAsync(31_000) })
    expect(screen.getByRole('progressbar', { name: 'Checking hosting access' })).not.toHaveAttribute('aria-valuenow')
    expect(screen.getByText('31s elapsed')).toBeInTheDocument()
    expect(screen.getByText(/Waiting for a fresh progress update/)).toBeInTheDocument()
    expect(screen.queryByText(/\d+%/)).not.toBeInTheDocument()
  })

  it('ignores earlier completed attempts and aborts the scoped read when the dialog closes', async () => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    let signal: AbortSignal | undefined | null
    const fetcher = vi.fn(async (_url: string, init?: RequestInit) => {
      signal = init?.signal
      return response(progress({ state: 'Completed', startedUtc: '2026-10-24T11:59:59Z', receivedBytes: 1024, percent: 100 }))
    })
    vi.stubGlobal('fetch', fetcher)
    const view = render(<LocalHostingProgress endpoint={endpoint} title="Preparing this PC" message="Checking the save." />)
    await act(async () => { await vi.advanceTimersByTimeAsync(250) })
    expect(screen.queryByText(/100%/)).not.toBeInTheDocument()
    expect(screen.getByRole('progressbar', { name: 'Preparing this PC' })).toBeInTheDocument()
    view.unmount()
    expect(signal?.aborted).toBe(true)
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(fetcher).toHaveBeenCalledOnce()
  })

  it('shows the initial backend phase when the request starts before React mounts the progress view', async () => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    const startedAt = Date.now()
    vi.setSystemTime(startedAt + 50)
    vi.stubGlobal('fetch', vi.fn(async () => response(progress({ phase: 'Permission', receivedBytes: 0, totalBytes: 0, percent: null,
      message: 'Checking the owner’s current permission.' }))))
    render(<LocalHostingProgress endpoint={endpoint} startedAt={startedAt} title="Preparing this PC" message="Checking the save." />)
    await act(async () => { await vi.advanceTimersByTimeAsync(250) })
    expect(screen.getByRole('progressbar', { name: 'Checking hosting access' })).toBeInTheDocument()
    expect(screen.getByText('Checking the owner’s current permission.')).toBeInTheDocument()
  })

  it('ignores an earlier still-Working attempt even when that attempt updates after the new dispatch', async () => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    vi.stubGlobal('fetch', vi.fn(async () => response(progress({ startedUtc: '2026-10-24T11:59:59Z',
      updatedUtc: '2026-10-24T12:00:01Z', message: 'Earlier attempt is still receiving.' }))))
    render(<LocalHostingProgress endpoint={endpoint} title="Preparing this PC" message="Waiting for this attempt." />)
    await act(async () => { await vi.advanceTimersByTimeAsync(2250) })
    expect(screen.getByRole('progressbar', { name: 'Preparing this PC' })).toBeInTheDocument()
    expect(screen.queryByText('Earlier attempt is still receiving.')).not.toBeInTheDocument()
    expect(screen.queryByText('25% received')).not.toBeInTheDocument()
  })

  it.each(['Failed', 'Completed'] as const)('shows a current %s result without reviving the spinner or claiming the next action', async state => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    let current = progress()
    const requests: RequestInit[] = []
    const result = vi.fn()
    const fetcher = vi.fn(async (_url: string, init?: RequestInit) => { requests.push(init ?? {}); return response(current) })
    vi.stubGlobal('fetch', fetcher)
    const view = render(<LocalHostingProgress endpoint={endpoint} title="Preparing this PC" message="Checking the save." onResult={result} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(250) })
    expect(screen.getByRole('progressbar')).toBeInTheDocument()
    current = progress({ state, message: state === 'Failed' ? 'The save check failed. Existing copies were kept.' : 'The local copy passed this preparation step.',
      updatedUtc: new Date(Date.now()).toISOString() })
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByRole('region', { name: 'Hosting progress' })).toHaveAttribute('aria-busy', 'false')
    expect(screen.getByText(current.message)).toBeInTheDocument()
    expect(screen.getByText(/Last step: Receiving the save/)).toHaveTextContent('Updated 0s ago')
    expect(result).toHaveBeenCalledExactlyOnceWith(current)
    expect(screen.getByText('Waiting for the operation result before continuing.')).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(view.container.querySelector('.icon-spin')).toBeNull()
    const terminalMessage = current.message
    current = progress({ updatedUtc: '2026-10-24T12:00:03Z', message: 'A delayed Working update.' })
    await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
    expect(screen.getByText(terminalMessage)).toBeInTheDocument()
    expect(screen.queryByText('A delayed Working update.')).not.toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(fetcher).toHaveBeenCalledTimes(2)
    expect(result).toHaveBeenCalledOnce()
    expect(requests.every(init => init.method === undefined)).toBe(true)
  })

  it('uses a fixed label for an unrecognized phase instead of exposing an internal phase name', async () => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    vi.stubGlobal('fetch', vi.fn(async () => response(progress({ phase: 'InternalSelectionStep_33333333',
      message: 'InternalSelectionException C:\\Private\\save', totalBytes: 0, receivedBytes: 0, percent: null }))))
    render(<LocalHostingProgress endpoint={endpoint} title="Preparing this PC" message="Checking the save." />)
    await act(async () => { await vi.advanceTimersByTimeAsync(250) })
    expect(screen.getByRole('progressbar', { name: 'Preparing this PC' })).toBeInTheDocument()
    expect(screen.queryByText(/InternalSelection|Private|33333333/)).not.toBeInTheDocument()
    expect(screen.getByText(/Current step: Preparing this PC/)).toHaveTextContent('Updated 0s ago')
  })

  it('does not overlap stalled progress reads or mutate the hosting operation', async () => {
    vi.useFakeTimers(); vi.setSystemTime(instant)
    const calls: RequestInit[] = []
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      calls.push(init ?? {})
      return new Promise<Response>(() => {})
    }))
    render(<LocalHostingProgress endpoint={endpoint} title="Preparing this PC" message="Checking the save." />)
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000) })
    expect(calls).toHaveLength(1)
    expect(calls[0].signal?.aborted).toBe(true)
    expect(calls[0].method).toBeUndefined()
    expect(screen.getByText(/Waiting for a fresh progress update/)).toBeInTheDocument()
  })
})

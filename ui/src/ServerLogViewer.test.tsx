import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ServerLogViewer, friendLogAvailability, type ServerLogLoader } from './ServerLogViewer'
import type { ServerLogRecord, ServerLogResult, ServerLogSourceState } from './contracts'

function record(message: string): ServerLogRecord {
  return { timestampUtc: '2026-09-28T12:00:00Z', severity: 'Info', category: 'Server', stream: 'Server', message }
}

function result(runId: string | null, records: ServerLogRecord[], cursor: string | null,
  sourceState: ServerLogSourceState = 'Active', code = 'LogAvailable', ok = true): ServerLogResult {
  return { ok, code, message: `${sourceState} log state.`, sourceState, runId, records, cursor, hasMore: false }
}

async function flush() {
  await act(async () => { await Promise.resolve(); await Promise.resolve() })
}

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
})

describe('ServerLogViewer', () => {
  it('polls only while its surface is visible and supports pause/resume', async () => {
    vi.useFakeTimers()
    const loader = vi.fn(async () => result('a'.repeat(32), [], 'cursor'))
    const view = render(<ServerLogViewer endpoint="/logs" visible={false} loader={loader} pollIntervalMs={1000} />)
    await flush()
    expect(loader).not.toHaveBeenCalled()

    view.rerender(<ServerLogViewer endpoint="/logs" visible loader={loader} pollIntervalMs={1000} />)
    await flush()
    expect(loader).toHaveBeenCalledTimes(1)

    fireEvent.click(screen.getByRole('button', { name: /pause/i }))
    await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
    expect(loader).toHaveBeenCalledTimes(1)

    fireEvent.click(screen.getByRole('button', { name: /resume/i }))
    await flush()
    expect(loader).toHaveBeenCalledTimes(2)

    view.rerender(<ServerLogViewer endpoint="/logs" visible={false} loader={loader} pollIntervalMs={1000} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
    expect(loader).toHaveBeenCalledTimes(2)
  })

  it('bounds client history and replaces it when the exact run changes', async () => {
    vi.useFakeTimers()
    const loader = vi.fn<ServerLogLoader>()
      .mockResolvedValueOnce(result('a'.repeat(32), [record('one'), record('two')], 'cursor-a'))
      .mockResolvedValueOnce(result('a'.repeat(32), [record('three'), record('four')], 'cursor-b'))
      .mockResolvedValueOnce(result('b'.repeat(32), [record('new run')], 'cursor-c'))

    render(<ServerLogViewer endpoint="/logs" visible loader={loader} pollIntervalMs={1000} historyLimit={3} />)
    await flush()
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.queryByText('one')).not.toBeInTheDocument()
    expect(screen.getByText('two')).toBeInTheDocument()
    expect(screen.getByText('three')).toBeInTheDocument()
    expect(screen.getByText('four')).toBeInTheDocument()

    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByText('new run')).toBeInTheDocument()
    expect(screen.queryByText('two')).not.toBeInTheDocument()
    expect(screen.getByText(/1 of 3 client-held entries/i)).toBeInTheDocument()
  })

  it('drops a stale cursor and immediately tails the replacement run', async () => {
    vi.useFakeTimers()
    const loader = vi.fn<ServerLogLoader>()
      .mockResolvedValueOnce(result('a'.repeat(32), [record('old run')], 'cursor-a'))
      .mockResolvedValueOnce(result('b'.repeat(32), [], null, 'Unavailable', 'InvalidLogCursor', false))
      .mockResolvedValueOnce(result('b'.repeat(32), [record('replacement run')], 'cursor-b'))

    render(<ServerLogViewer endpoint="/logs" visible loader={loader} pollIntervalMs={1000} />)
    await flush()
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })

    expect(loader).toHaveBeenCalledTimes(3)
    expect(loader.mock.calls[0][1].cursor).toBeNull()
    expect(loader.mock.calls[1][1].cursor).toBe('cursor-a')
    expect(loader.mock.calls[2][1].cursor).toBeNull()
    expect(screen.queryByText('old run')).not.toBeInTheDocument()
    expect(screen.getByText('replacement run')).toBeInTheDocument()
  })

  it('resets and sends bounded severity, category, and contains filters', async () => {
    const loader = vi.fn<ServerLogLoader>().mockResolvedValue(result('a'.repeat(32), [], 'cursor'))
    render(<ServerLogViewer endpoint="/logs" visible loader={loader} />)
    await waitFor(() => expect(loader).toHaveBeenCalledTimes(1))

    fireEvent.change(screen.getByLabelText('Log severity'), { target: { value: 'Warning' } })
    fireEvent.change(screen.getByLabelText('Log category'), { target: { value: 'Lifecycle' } })
    fireEvent.change(screen.getByLabelText('Log contains'), { target: { value: ' started ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }))

    await waitFor(() => expect(loader).toHaveBeenCalledTimes(2))
    expect(loader.mock.calls[1][1]).toMatchObject({
      cursor: null, severity: 'Warning', category: 'Lifecycle', contains: 'started', limit: 100
    })
  })

  it('shows typed ended, missing, and unsupported states without widening Friend access', async () => {
    const endedLoader = vi.fn<ServerLogLoader>().mockResolvedValue(result('a'.repeat(32), [], 'cursor', 'Ended', 'EndedLog'))
    const ended = render(<ServerLogViewer endpoint="/ended" visible loader={endedLoader} />)
    expect(await screen.findByText('Ended log state.')).toBeInTheDocument()
    expect(screen.getAllByText('Ended run').length).toBeGreaterThan(0)
    ended.unmount()

    const missingLoader = vi.fn<ServerLogLoader>().mockResolvedValue(result(null, [], null, 'Missing', 'NoManagedRunLog', false))
    const missing = render(<ServerLogViewer endpoint="/missing" visible loader={missingLoader} />)
    expect(await screen.findByText('Missing log state.')).toBeInTheDocument()
    expect(screen.getAllByText('No run log').length).toBeGreaterThan(0)
    missing.unmount()

    const errorLoader = vi.fn<ServerLogLoader>().mockRejectedValue(new Error('Host log request failed.'))
    const failed = render(<ServerLogViewer endpoint="/error" visible loader={errorLoader} />)
    expect(await screen.findByText('Host log request failed.')).toBeInTheDocument()
    expect(screen.getAllByText('Log unavailable').length).toBeGreaterThan(0)
    failed.unmount()

    expect(friendLogAvailability(false, ['server-logs-v1']).visible).toBe(false)
    const availability = friendLogAvailability(true, ['activity-feed'])
    const unsupportedLoader = vi.fn<ServerLogLoader>()
    render(<ServerLogViewer endpoint="/unsupported" visible loader={unsupportedLoader}
      unsupported={availability.unsupported} />)
    expect(screen.getByText('Update the Host app before viewing server logs from this PC.')).toBeInTheDocument()
    expect(unsupportedLoader).not.toHaveBeenCalled()
  })
})

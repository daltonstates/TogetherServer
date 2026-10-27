import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ServerLogViewer, friendLogAvailability, readLogPreferences, saveLogPreferences, serverLogProblem, type ServerLogLoader } from './ServerLogViewer'
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
  localStorage.clear()
})

describe('ServerLogViewer', () => {
  it('shows bounded unfiltered startup output as text without changing saved full-log filters', async () => {
    saveLogPreferences({ severity: 'Error', category: 'Lifecycle', contains: '' })
    const write = vi.spyOn(Storage.prototype, 'setItem')
    const loader = vi.fn<ServerLogLoader>().mockResolvedValue(result('a'.repeat(32), [
      record('old line'), record('<img src=x onerror=run()>'), record('Current startup line')
    ], 'cursor'))
    render(<ServerLogViewer endpoint="/logs" visible compact expectedRunId={'a'.repeat(32)} loader={loader} historyLimit={2} />)
    expect(await screen.findByText('Current startup line')).toBeVisible()
    expect(screen.getByRole('log')).toHaveTextContent('<img src=x onerror=run()>')
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    expect(screen.queryByText('old line')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Filters' })).not.toBeInTheDocument()
    expect(loader.mock.calls[0][1]).toMatchObject({ severity: '', category: '', contains: '' })
    expect(write).not.toHaveBeenCalled()
    expect(readLogPreferences().severity).toBe('Error')
  })

  it('refuses another run or an archived log in a startup console, then accepts the expected active run', async () => {
    vi.useFakeTimers()
    const run = '22222222-2222-4222-8222-222222222222'
    const runId = run.replaceAll('-', '')
    const loader = vi.fn<ServerLogLoader>()
      .mockResolvedValueOnce(result('a'.repeat(32), [record('Other run output')], 'other', 'Ended'))
      .mockResolvedValueOnce(result(runId, [record('Archived output')], 'ended', 'Ended'))
      .mockResolvedValueOnce(result(runId, [record('Current run output')], 'current'))
      .mockResolvedValueOnce(result('b'.repeat(32), [record('Replacement output')], 'replacement'))
    render(<ServerLogViewer endpoint="/logs" visible compact expectedRunId={run} loader={loader} pollIntervalMs={1000} />)
    await flush()
    expect(screen.getByText('Waiting for console output from this server start.')).toBeInTheDocument()
    expect(screen.queryByText('Other run output')).not.toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.queryByText('Archived output')).not.toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.getByText('Current run output')).toBeInTheDocument()
    expect(loader.mock.calls[2][1].cursor).toBeNull()
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(screen.queryByText('Current run output')).not.toBeInTheDocument()
    expect(screen.queryByText('Replacement output')).not.toBeInTheDocument()
  })

  it('keeps unfinished filters when their compact controls are collapsed without issuing a request', async () => {
    const loader = vi.fn<ServerLogLoader>().mockResolvedValue(result('a'.repeat(32), [record('latest line')], 'cursor'))
    render(<ServerLogViewer endpoint="/logs" visible loader={loader} />)
    await screen.findByText('latest line')
    const toggle = screen.getByRole('button', { name: 'Filters' })
    const form = document.getElementById(toggle.getAttribute('aria-controls')!)!
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(toggle)
    fireEvent.change(screen.getByRole('textbox', { name: 'Log contains' }), { target: { value: 'unfinished search' } })
    fireEvent.click(toggle)
    expect(form).toHaveAttribute('data-expanded', 'false')
    expect(screen.getByRole('log')).toHaveTextContent('latest line')
    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('textbox', { name: 'Log contains' })).toHaveValue('unfinished search')
    expect(loader).toHaveBeenCalledTimes(1)
  })

  it('dismisses a read error while retaining log controls and allows a later endpoint error', async () => {
    const loader = vi.fn<ServerLogLoader>().mockRejectedValue(new Error('Host log request failed.'))
    const view = render(<ServerLogViewer endpoint="/first" visible loader={loader} />)
    await screen.findByText('Host log request failed.')
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
    expect(screen.queryByText('Host log request failed.')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled()
    expect(screen.getByRole('log')).toBeInTheDocument()
    view.rerender(<ServerLogViewer endpoint="/second" visible loader={loader} />)
    expect(await screen.findByText('Host log request failed.')).toBeVisible()
  })

  it('ignores a late failed read after the endpoint changes', async () => {
    let rejectOld!: (error: Error) => void
    const loader = vi.fn<ServerLogLoader>(endpoint => endpoint === '/old' ? new Promise((_resolve, reject) => {
      rejectOld = reject
    }) : Promise.resolve(result('b'.repeat(32), [record('current run')], 'current')))
    const display = render(<ServerLogViewer endpoint="/old" visible loader={loader} />)
    await flush()
    display.rerender(<ServerLogViewer endpoint="/current" visible loader={loader} />)
    expect(loader.mock.calls[0][2].aborted).toBe(true)
    expect(loader).toHaveBeenCalledTimes(1)
    expect(screen.queryByText('current run')).not.toBeInTheDocument()
    // This loader deliberately ignores abort: a new scope waits instead of
    // opening a concurrent read, and the old failure cannot reach that scope.
    await act(async () => rejectOld(new Error('PRIVATE_OLD_FAILURE')))
    await screen.findByText('current run')
    expect(loader).toHaveBeenCalledTimes(2)
    expect(loader.mock.calls[1][0]).toBe('/current')
    expect(screen.queryByText('PRIVATE_OLD_FAILURE')).not.toBeInTheDocument()
    expect(screen.getByText('current run')).toBeInTheDocument()
  })

  it('clears old content immediately and coalesces manual refresh behind an aborted old scope', async () => {
    vi.useFakeTimers()
    let finishOld!: (value: ServerLogResult) => void
    const loader = vi.fn<ServerLogLoader>()
      .mockResolvedValueOnce(result('a'.repeat(32), [record('Prior scope content')], 'old'))
      .mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve }))
      .mockResolvedValue(result('b'.repeat(32), [record('Current scope content')], 'current'))
    const display = render(<ServerLogViewer endpoint="/old" visible loader={loader} pollIntervalMs={1000} />)
    await flush()
    expect(screen.getByText('Prior scope content')).toBeVisible()
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(loader).toHaveBeenCalledTimes(2)
    display.rerender(<ServerLogViewer endpoint="/current" visible loader={loader} pollIntervalMs={1000} />)
    expect(loader.mock.calls[1][2].aborted).toBe(true)
    expect(screen.queryByText('Prior scope content')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    await flush()
    expect(loader).toHaveBeenCalledTimes(2)
    await act(async () => finishOld(result('a'.repeat(32), [record('PRIVATE_LATE_OLD_CONTENT')], 'late-old')))
    await flush()
    expect(loader).toHaveBeenCalledTimes(3)
    expect(loader.mock.calls[2][0]).toBe('/current')
    expect(loader.mock.calls[2][1].cursor).toBeNull()
    expect(screen.getByText('Current scope content')).toBeVisible()
    expect(screen.queryByText('PRIVATE_LATE_OLD_CONTENT')).not.toBeInTheDocument()
    expect(screen.queryByText('Prior scope content')).not.toBeInTheDocument()
  })

  it('keeps the visible reading anchor when bounded history drops earlier lines, then jumps on request', async () => {
    vi.useFakeTimers()
    const loader = vi.fn<ServerLogLoader>()
      .mockResolvedValueOnce(result('a'.repeat(32), Array.from({ length: 30 }, (_, index) => record(`line ${index}`)), 'one'))
      .mockResolvedValueOnce(result('a'.repeat(32), [record('line 30'), record('line 31')], 'two'))
    render(<ServerLogViewer endpoint="/logs" visible loader={loader} pollIntervalMs={1000} historyLimit={30} />)
    await flush()
    const console = screen.getByRole('log')
    Object.defineProperties(console, { scrollHeight: { configurable: true, get: () => 600 },
      clientHeight: { configurable: true, get: () => 100 } })
    vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
      if (this === console) return new DOMRect(0, 0, 500, 100)
      const index = Array.from(console.querySelectorAll('[data-log-sequence]')).indexOf(this)
      return new DOMRect(0, index * 20 - console.scrollTop, 500, 20)
    })
    console.scrollTop = 100
    console.scrollLeft = 42
    fireEvent.scroll(console)
    expect(screen.getByRole('button', { name: 'Follow latest' })).toHaveAttribute('aria-pressed', 'false')
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(console.scrollTop).toBe(60)
    expect(console.scrollLeft).toBe(42)
    expect(screen.getByText('2 new lines')).toBeInTheDocument()
    expect(screen.queryByText('line 0')).not.toBeInTheDocument()
    expect(screen.getByText('line 5')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Jump to latest' }))
    expect(console.scrollTop).toBe(600)
    expect(screen.getByRole('button', { name: 'Follow latest' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.queryByText('2 new lines')).not.toBeInTheDocument()
  })

  it('pauses following independently of polling and restores it when the user reaches the bottom', async () => {
    vi.useFakeTimers()
    const loader = vi.fn(async () => result('a'.repeat(32), [record('a line')], 'cursor'))
    render(<ServerLogViewer endpoint="/logs" visible loader={loader} pollIntervalMs={1000} />)
    await flush()
    fireEvent.click(screen.getByRole('button', { name: 'Follow latest' }))
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(loader).toHaveBeenCalledTimes(2)
    expect(screen.getByText('1 new line')).toBeInTheDocument()
    const console = screen.getByRole('log')
    Object.defineProperties(console, { scrollHeight: { configurable: true, value: 1000 },
      clientHeight: { configurable: true, value: 200 } })
    console.scrollTop = 800
    fireEvent.scroll(console)
    expect(screen.getByRole('button', { name: 'Follow latest' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.queryByRole('button', { name: 'Jump to latest' })).not.toBeInTheDocument()
  })

  it('routes issue labels to fixed destinations without passing log text or using a log-supplied route', async () => {
    const navigate = vi.fn()
    const loader = vi.fn(async () => result('a'.repeat(32), [
      record('Invalid server.properties; https://untrusted.example/run'),
      record('Missing mod dependency; open C:\\private'),
      { ...record('Failed to bind port; javascript:run()'), severity: 'Error' as const }
    ], 'cursor'))
    render(<ServerLogViewer endpoint="/logs" visible loader={loader} onNavigateIssue={navigate} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Settings issue · Open Files' }))
    fireEvent.click(screen.getByRole('button', { name: 'Add-on issue · Review add-ons' }))
    fireEvent.click(screen.getByRole('button', { name: 'Server problem · Check health' }))
    expect(navigate.mock.calls).toEqual([['files'], ['addons'], ['health']])
    expect(loader).toHaveBeenCalledTimes(1)
  })

  it('remembers only validated severity and category, with no initial overwrite or persisted search text', async () => {
    saveLogPreferences({ severity: 'Warning', category: 'Lifecycle', contains: 'password=PRIVATE_SEARCH' })
    const write = vi.spyOn(Storage.prototype, 'setItem')
    const loader = vi.fn<ServerLogLoader>().mockResolvedValue(result('a'.repeat(32), [], 'cursor'))
    render(<ServerLogViewer endpoint="/private-endpoint" visible loader={loader} />)
    await waitFor(() => expect(loader).toHaveBeenCalledOnce())
    expect(loader.mock.calls[0][1]).toMatchObject({ severity: 'Warning', category: 'Lifecycle', contains: '' })
    expect(write).not.toHaveBeenCalled()
    fireEvent.change(screen.getByLabelText('Log contains'), { target: { value: 'PRIVATE_SEARCH' } })
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }))
    expect(write.mock.calls).toEqual([['togetherserver.log-filters.v1', '{"severity":"Warning","category":"Lifecycle"}']])
    expect(localStorage.getItem('togetherserver.log-filters.v1')).not.toContain('PRIVATE')
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }))
    expect(readLogPreferences()).toEqual({ severity: '', category: '', contains: '' })
  })

  it('ignores malformed or secret-bearing preferences and continues when storage is blocked', () => {
    for (const unsafe of ['{"severity":"password=private","category":"Server"}',
      '{"severity":"Error","category":"Server","contains":"private"}', 'null', '[]', '{']) {
      localStorage.setItem('togetherserver.log-filters.v1', unsafe)
      expect(readLogPreferences()).toEqual({ severity: '', category: '', contains: '' })
    }
    const write = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
    expect(() => saveLogPreferences({ severity: 'Error', category: 'Server', contains: 'private' })).not.toThrow()
    write.mockClear()
    saveLogPreferences({ severity: 'private' as 'Error', category: 'Server', contains: '' })
    expect(write).not.toHaveBeenCalled()
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
    expect(readLogPreferences()).toEqual({ severity: '', category: '', contains: '' })
  })

  it('calls out likely add-on and settings failures in loaded logs', () => {
    expect(serverLogProblem(record('Mod dependency missing for server launch'))).toBe('Add-on issue')
    expect(serverLogProblem(record('Invalid server.properties configuration'))).toBe('Settings issue')
    expect(serverLogProblem(record('Player joined'))).toBeNull()
  })
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
    expect(screen.getByText(/1 of 3 lines shown/i)).toBeInTheDocument()
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

  it('preserves the last good cursor and records across a transient same-run failure', async () => {
    vi.useFakeTimers()
    const runId = 'a'.repeat(32)
    const loader = vi.fn<ServerLogLoader>()
      .mockResolvedValueOnce(result(runId, [record('first record')], 'cursor-a'))
      .mockResolvedValueOnce(result(runId, [], null, 'Unavailable', 'LogReadUnavailable', false))
      .mockResolvedValueOnce(result(runId, [record('second record')], 'cursor-b'))

    render(<ServerLogViewer endpoint="/logs" visible loader={loader} pollIntervalMs={1000} />)
    await flush()
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })

    expect(screen.getByText('first record')).toBeInTheDocument()
    expect(loader.mock.calls[1][1].cursor).toBe('cursor-a')

    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(loader.mock.calls[2][1].cursor).toBe('cursor-a')
    expect(screen.getAllByText('first record')).toHaveLength(1)
    expect(screen.getByText('second record')).toBeInTheDocument()
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
    expect(screen.getAllByText('Past session').length).toBeGreaterThan(0)
    ended.unmount()

    const missingLoader = vi.fn<ServerLogLoader>().mockResolvedValue(result(null, [], null, 'Missing', 'NoManagedRunLog', false))
    const missing = render(<ServerLogViewer endpoint="/missing" visible loader={missingLoader} />)
    expect(await screen.findByText('Missing log state.')).toBeInTheDocument()
    expect(screen.getAllByText('No session log').length).toBeGreaterThan(0)
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

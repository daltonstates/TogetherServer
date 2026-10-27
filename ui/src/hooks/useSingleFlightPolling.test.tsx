import { act, cleanup, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useSingleFlightPolling } from './useSingleFlightPolling'

function deferred() {
  let resolve!: () => void
  const promise = new Promise<void>(done => { resolve = done })
  return { promise, resolve }
}

function PollingProbe({ task, intervalMs = 1000, restartKey = undefined, enabled = true }: {
  task: (signal: AbortSignal) => Promise<void>
  intervalMs?: number
  restartKey?: unknown
  enabled?: boolean
}) {
  useSingleFlightPolling(task, intervalMs, undefined, enabled, restartKey)
  return <span>mounted</span>
}

let visibility: DocumentVisibilityState = 'visible'
beforeEach(() => {
  visibility = 'visible'
  vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility)
})
afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks() })

function setVisibility(value: DocumentVisibilityState) {
  act(() => { visibility = value; document.dispatchEvent(new Event('visibilitychange')) })
}

describe('useSingleFlightPolling', () => {
  it('waits for a request to finish before scheduling the next one', async () => {
    vi.useFakeTimers()
    const first = deferred()
    const task = vi.fn(() => first.promise)
    render(<PollingProbe task={task} />)

    expect(task).toHaveBeenCalledTimes(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(task).toHaveBeenCalledTimes(1)

    await act(async () => { first.resolve(); await first.promise })
    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(task).toHaveBeenCalledTimes(2)
  })

  it('aborts the active request when the component unmounts', () => {
    const signals: AbortSignal[] = []
    const task = vi.fn((signal: AbortSignal) => {
      signals.push(signal)
      return new Promise<void>(resolve => signal.addEventListener('abort', () => resolve(), { once: true }))
    })
    const view = render(<PollingProbe task={task} />)

    view.unmount()
    expect(signals[0].aborted).toBe(true)
  })

  it('aborts its completed response signal when authorization disables the scope', async () => {
    const task = vi.fn<(signal: AbortSignal) => Promise<void>>().mockResolvedValue(undefined)
    const view = render(<PollingProbe task={task} />)
    await act(async () => { await Promise.resolve() })
    const signal = task.mock.calls[0][0]
    expect(signal.aborted).toBe(false)
    view.rerender(<PollingProbe task={task} enabled={false} />)
    expect(signal.aborted).toBe(true)
    expect(task).toHaveBeenCalledTimes(1)
  })

  it('reports a failed poll without leaving an unhandled rejection', async () => {
    vi.useFakeTimers()
    const error = new Error('local service unavailable')
    const onError = vi.fn()
    const task = vi.fn(async () => { throw error })

    function FailingProbe() {
      useSingleFlightPolling(task, 1000, onError)
      return null
    }

    render(<FailingProbe />)
    await act(async () => { await Promise.resolve() })
    expect(onError).toHaveBeenCalledWith(error)

    await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
    expect(task).toHaveBeenCalledTimes(2)
  })

  it('starts once when an initially hidden document becomes visible', async () => {
    vi.useFakeTimers()
    visibility = 'hidden'
    const task = vi.fn(async () => {})
    render(<PollingProbe task={task} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(task).not.toHaveBeenCalled()

    setVisibility('visible')
    setVisibility('visible')
    await act(async () => { await Promise.resolve() })
    expect(task).toHaveBeenCalledTimes(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(999) })
    expect(task).toHaveBeenCalledTimes(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(task).toHaveBeenCalledTimes(2)
  })

  it('aborts on hiding, discards late completion and stays idle until visible', async () => {
    vi.useFakeTimers()
    const first = deferred()
    const task = vi.fn<(signal: AbortSignal) => Promise<void>>().mockImplementationOnce(() => first.promise).mockResolvedValue(undefined)
    render(<PollingProbe task={task} />)
    const signal = task.mock.calls[0][0]
    setVisibility('hidden')
    expect(signal.aborted).toBe(true)
    await act(async () => { first.resolve(); await first.promise; await vi.advanceTimersByTimeAsync(5000) })
    expect(task).toHaveBeenCalledTimes(1)
    setVisibility('visible')
    await act(async () => { await Promise.resolve() })
    expect(task).toHaveBeenCalledTimes(2)
  })

  it('waits for a cancelled flight that ignores abort before resuming once', async () => {
    vi.useFakeTimers()
    const first = deferred()
    const second = deferred()
    const task = vi.fn<(signal: AbortSignal) => Promise<void>>()
      .mockImplementationOnce(() => first.promise).mockImplementationOnce(() => second.promise).mockResolvedValue(undefined)
    render(<PollingProbe task={task} />)
    setVisibility('hidden')
    setVisibility('visible')
    setVisibility('visible')
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(task).toHaveBeenCalledTimes(1)
    await act(async () => { first.resolve(); await first.promise })
    expect(task).toHaveBeenCalledTimes(2)
    expect(task.mock.calls[1][0].aborted).toBe(false)
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(task).toHaveBeenCalledTimes(2)
    await act(async () => { second.resolve(); await second.promise })
    await act(async () => { await vi.advanceTimersByTimeAsync(999) })
    expect(task).toHaveBeenCalledTimes(2)
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(task).toHaveBeenCalledTimes(3)
  })

  it('waits across scope changes and cannot schedule after unmount', async () => {
    vi.useFakeTimers()
    const first = deferred()
    const oldTask = vi.fn<(signal: AbortSignal) => Promise<void>>(() => first.promise)
    const currentTask = vi.fn(async () => {})
    const view = render(<PollingProbe task={oldTask} restartKey="old" />)
    view.rerender(<PollingProbe task={currentTask} restartKey="current" />)
    expect(oldTask.mock.calls[0][0].aborted).toBe(true)
    expect(currentTask).not.toHaveBeenCalled()
    await act(async () => { first.resolve(); await first.promise })
    expect(currentTask).toHaveBeenCalledTimes(1)
    view.unmount()
    setVisibility('hidden')
    setVisibility('visible')
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(currentTask).toHaveBeenCalledTimes(1)
  })

  it('clears an idle timer while hidden and never polls a disabled surface', async () => {
    vi.useFakeTimers()
    const task = vi.fn(async () => {})
    const view = render(<PollingProbe task={task} />)
    await act(async () => { await Promise.resolve() })
    setVisibility('hidden')
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(task).toHaveBeenCalledTimes(1)
    view.rerender(<PollingProbe task={task} enabled={false} />)
    setVisibility('visible')
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(task).toHaveBeenCalledTimes(1)
    view.rerender(<PollingProbe task={task} enabled />)
    await act(async () => { await Promise.resolve() })
    expect(task).toHaveBeenCalledTimes(2)
  })
})

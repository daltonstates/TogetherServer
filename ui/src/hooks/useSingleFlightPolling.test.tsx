import { act, render } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useSingleFlightPolling } from './useSingleFlightPolling'

function deferred() {
  let resolve!: () => void
  const promise = new Promise<void>(done => { resolve = done })
  return { promise, resolve }
}

function PollingProbe({ task, intervalMs = 1000 }: {
  task: (signal: AbortSignal) => Promise<void>
  intervalMs?: number
}) {
  useSingleFlightPolling(task, intervalMs)
  return <span>mounted</span>
}

afterEach(() => vi.useRealTimers())

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
})

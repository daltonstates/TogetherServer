import { useEffect, useRef } from 'react'

export function useSingleFlightPolling(task: (signal: AbortSignal) => Promise<void>, intervalMs: number,
  onError?: (error: unknown) => void, enabled = true, restartKey: unknown = undefined) {
  const taskRef = useRef(task)
  const errorRef = useRef(onError)
  const flightRef = useRef<Promise<void> | null>(null)
  useEffect(() => { taskRef.current = task }, [task])
  useEffect(() => { errorRef.current = onError }, [onError])

  useEffect(() => {
    if (!enabled) return
    let active = true
    let hidden = document.visibilityState === 'hidden'
    let waiting = false
    let resume = false
    let timer: number | undefined
    let controller: AbortController | undefined

    const clearTimer = () => {
      if (timer !== undefined) window.clearTimeout(timer)
      timer = undefined
    }

    const poll = async () => {
      if (!active || hidden || waiting) return
      waiting = true
      // An aborted request can ignore cancellation. A new scope or visible
      // document waits for its completion instead of opening a second flight.
      if (flightRef.current) {
        await flightRef.current
        waiting = false
        if (active && !hidden) { resume = false; void poll() }
        return
      }
      resume = false
      const current = new AbortController()
      controller = current
      const flight = (async () => {
        try { await taskRef.current(current.signal) }
        catch (error) {
          if (!current.signal.aborted) errorRef.current?.(error)
        }
      })()
      flightRef.current = flight
      try { await flight }
      finally {
        if (flightRef.current === flight) flightRef.current = null
        // Keep the last signal owned by this scope, even after completion.
        // Authorization/scope removal still aborts every retained consumer of
        // that latest response; the next flight replaces its controller.
        waiting = false
        if (active && !hidden) {
          if (resume) { resume = false; void poll() }
          else timer = window.setTimeout(() => void poll(), intervalMs)
        }
      }
    }

    const visibilityChanged = () => {
      const nextHidden = document.visibilityState === 'hidden'
      if (hidden === nextHidden) return
      hidden = nextHidden
      clearTimer()
      if (hidden) { resume = false; controller?.abort() }
      else if (waiting) resume = true
      else void poll()
    }

    document.addEventListener('visibilitychange', visibilityChanged)
    if (!hidden) void poll()
    return () => {
      active = false
      clearTimer()
      document.removeEventListener('visibilitychange', visibilityChanged)
      controller?.abort()
    }
  }, [enabled, intervalMs, restartKey])
}

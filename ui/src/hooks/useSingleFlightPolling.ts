import { useEffect, useRef } from 'react'

export function useSingleFlightPolling(task: (signal: AbortSignal) => Promise<void>, intervalMs: number,
  onError?: (error: unknown) => void) {
  const taskRef = useRef(task)
  const errorRef = useRef(onError)
  useEffect(() => { taskRef.current = task }, [task])
  useEffect(() => { errorRef.current = onError }, [onError])

  useEffect(() => {
    let active = true
    let timer: number | undefined
    const controller = new AbortController()

    const poll = async () => {
      try { await taskRef.current(controller.signal) }
      catch (error) {
        if (!controller.signal.aborted) errorRef.current?.(error)
      }
      finally {
        if (active && !controller.signal.aborted) timer = window.setTimeout(() => void poll(), intervalMs)
      }
    }

    void poll()
    return () => {
      active = false
      if (timer !== undefined) window.clearTimeout(timer)
      controller.abort()
    }
  }, [intervalMs])
}

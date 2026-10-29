import { act, renderHook } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { parseFriendSaveTransferStatus, useFriendSaveTransfer } from './FriendSaveTransfer'

const idle = { state: 'Ready', transferPhase: null, receivedBytes: 0, totalBytes: 0 }
const response = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

it.each([
  null, {}, { ...idle, transferPhase: undefined }, { ...idle, transferPhase: 'Done' }, { ...idle, state: '' },
  { ...idle, state: 'Ready\u0001' }, { ...idle, receivedBytes: -1 }, { ...idle, receivedBytes: 1 },
  { ...idle, totalBytes: 1.5 }, { ...idle, totalBytes: Number.MAX_SAFE_INTEGER + 1 }
])('rejects incomplete or inconsistent background receiver evidence %j', value => {
  expect(() => parseFriendSaveTransferStatus(value)).toThrow()
})

it.each(['Preparing', 'Receiving', 'Verifying', 'Receipt'] as const)('retains the active %s phase when every byte has arrived', transferPhase => {
  expect(parseFriendSaveTransferStatus({ ...idle, transferPhase, receivedBytes: 100, totalBytes: 100 }).transferPhase).toBe(transferPhase)
})

it('uses only bounded single-flight local reads and blocks immediately when evidence ages', async () => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-28T12:00:00Z')
  let finish!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  let reads = 0
  const fetcher = vi.fn(async (_url: string, init?: RequestInit) => {
    signal = init?.signal
    return ++reads === 1 ? response(idle) : new Promise<Response>(resolve => { finish = resolve })
  })
  vi.stubGlobal('fetch', fetcher)
  const { result, unmount } = renderHook(() => useFriendSaveTransfer({ endpoint: '/cached-receiver', scope: 'first', enabled: true }))
  expect(result.current.blocked).toBe(true)
  await act(async () => {})
  expect(result.current.canHost()).toBe(true)
  expect(fetcher).toHaveBeenLastCalledWith('/cached-receiver', expect.objectContaining({
    cache: 'no-store', headers: { 'X-TogetherServer-Local': '1' }
  }))
  await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
  expect(fetcher).toHaveBeenCalledTimes(2)
  vi.setSystemTime('2026-10-28T12:00:10Z')
  expect(result.current.canHost()).toBe(false)
  unmount()
  expect(signal?.aborted).toBe(true)
  await act(async () => { finish(response(idle)) })
  expect(fetcher).toHaveBeenCalledTimes(2)
})

it('retries a timed-out read and ignores its late idle result', async () => {
  vi.useFakeTimers()
  let finish!: (value: Response) => void
  let oldSignal: AbortSignal | null | undefined
  let reads = 0
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
    if (++reads === 1) { oldSignal = init?.signal; return new Promise<Response>(resolve => { finish = resolve }) }
    return response({ ...idle, transferPhase: 'Receipt' })
  }))
  const { result } = renderHook(() => useFriendSaveTransfer({ endpoint: '/cached-receiver', scope: 'first', enabled: true }))
  await act(async () => { await vi.advanceTimersByTimeAsync(4000) })
  expect(oldSignal?.aborted).toBe(true)
  expect(result.current.unavailable).toBe(true)
  expect(result.current.canHost()).toBe(false)
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(result.current.status?.transferPhase).toBe('Receipt')
  await act(async () => { finish(response(idle)) })
  expect(result.current.status?.transferPhase).toBe('Receipt')
  expect(result.current.canHost()).toBe(false)
})

it.each(['profile', 'connection'] as const)('cancels a background read when the %s scope changes', async change => {
  let finish!: (value: Response) => void
  let oldSignal: AbortSignal | null | undefined
  let reads = 0
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
    if (++reads === 1) { oldSignal = init?.signal; return new Promise<Response>(resolve => { finish = resolve }) }
    return response({ ...idle, transferPhase: 'Receiving', receivedBytes: 30, totalBytes: 100 })
  }))
  const { result, rerender } = renderHook(({ scope }) => useFriendSaveTransfer({ endpoint: '/cached-receiver', scope, enabled: true }),
    { initialProps: { scope: '["connection-one","profile-one"]' } })
  rerender({ scope: change === 'profile' ? '["connection-one","profile-two"]' : '["connection-two","profile-one"]' })
  expect(oldSignal?.aborted).toBe(true)
  expect(result.current.canHost()).toBe(false)
  await act(async () => {})
  expect(result.current.status?.transferPhase).toBe('Receiving')
  await act(async () => { finish(response(idle)) })
  expect(result.current.status?.transferPhase).toBe('Receiving')
  expect(result.current.canHost()).toBe(false)
})

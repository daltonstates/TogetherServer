import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { parseGettingStarted, useGettingStarted } from './useGettingStarted'
import { changeJson, getLocalJson } from './api'

vi.mock('./api', () => ({ getLocalJson: vi.fn(), changeJson: vi.fn(), errorMessage: () => 'Could not save the guide preference.' }))
const quiet = { offered: false, intent: 'Choose' as const, dismissed: false }
beforeEach(() => {
  localStorage.clear(); vi.resetAllMocks()
  vi.mocked(getLocalJson).mockResolvedValue(quiet)
  vi.mocked(changeJson).mockResolvedValue({ ...quiet, offered: true, dismissed: true })
})

describe('getting started handoff', () => {
  it('ignores an old first-run reply after the owner has started or dismissed a guide', async () => {
    let resolveRead!: (value: unknown) => void
    vi.mocked(getLocalJson).mockReturnValue(new Promise(resolve => { resolveRead = resolve }))
    const { result } = renderHook(() => useGettingStarted(true))
    act(() => result.current.open())
    await act(async () => { await result.current.start('host') })
    await act(async () => { resolveRead({ offered: true, intent: 'Join', dismissed: false }) })
    expect(result.current.active).toBe('host')
    expect(result.current.welcome).toBe(false)
    expect(result.current.view?.dismissed).toBe(true)
  })
  it('waits for required profile setup and only auto-offers a requested guide', async () => {
    const { result, rerender } = renderHook(({ enabled }) => useGettingStarted(enabled), { initialProps: { enabled: false } })
    expect(getLocalJson).not.toHaveBeenCalled()
    rerender({ enabled: true })
    await waitFor(() => expect(result.current.view).toEqual(quiet))
    expect(result.current.welcome).toBe(false)
  })
  it('opens the installer choice, persists acknowledgement, and resumes optional progress', async () => {
    localStorage.setItem('togetherserver.guide-progress.v1', JSON.stringify({ kind: 'host', step: 2 }))
    vi.mocked(getLocalJson).mockResolvedValue({ offered: true, intent: 'Host', dismissed: false })
    const { result } = renderHook(() => useGettingStarted(true))
    await waitFor(() => expect(result.current.welcome).toBe(true))
    expect(result.current.intent).toBe('host')
    await act(async () => { expect(await result.current.start('host')).toBe(true) })
    expect(changeJson).toHaveBeenCalledWith('/api/local/getting-started', 'PUT', parseGettingStarted, { intent: 'Host', dismissed: true })
    expect(result.current.active).toBe('host')
    expect(result.current.step).toBe(2)
    act(() => result.current.pause())
    expect(result.current.active).toBeNull()
    expect(result.current.resumeStep('host')).toBe(2)
    await act(async () => { await result.current.start('host') })
    act(() => result.current.finish())
    expect(localStorage.getItem('togetherserver.guide-progress.v1')).toBeNull()
  })
  it('keeps setup available after a read failure and leaves the welcome screen open on a failed start', async () => {
    vi.mocked(getLocalJson).mockRejectedValue(new Error('Unavailable'))
    vi.mocked(changeJson).mockRejectedValue(new Error('Disk unavailable'))
    const { result } = renderHook(() => useGettingStarted(true))
    act(() => result.current.open())
    await act(async () => { expect(await result.current.start('join')).toBe(false) })
    expect(result.current.welcome).toBe(true)
    expect(result.current.active).toBeNull()
    expect(result.current.error).toBe('Could not save the guide preference.')
    await act(async () => { await result.current.dismiss() })
    expect(result.current.welcome).toBe(false)
  })
  it('rejects malformed handoffs and ignores corrupt or out-of-range optional progress', () => {
    for (const value of [null, {}, { ...quiet, offered: 'yes' }, { ...quiet, intent: 'Other' }]) expect(() => parseGettingStarted(value)).toThrow()
    localStorage.setItem('togetherserver.guide-progress.v1', '{bad json')
    const view = renderHook(() => useGettingStarted(false))
    expect(view.result.current.resumeStep('host')).toBe(0)
    view.unmount()
    localStorage.setItem('togetherserver.guide-progress.v1', JSON.stringify({ kind: 'join', step: 4 }))
    const other = renderHook(() => useGettingStarted(false))
    expect(other.result.current.resumeStep('join')).toBe(0)
  })
})

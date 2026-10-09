import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useEditorDraftGuard, useEditorProtectedDraft, type EditorDraftGuard } from './editorProtectedDraft'
import type { DraftIdentity, ProtectedDraftResult } from './protectedUiDrafts'

const draftApi = vi.hoisted(() => ({ read: vi.fn(), save: vi.fn(), clear: vi.fn() }))
vi.mock('./protectedUiDrafts', () => ({ readProtectedDraft: draftApi.read, saveProtectedDraft: draftApi.save, clearProtectedDraft: draftApi.clear }))
const identity: DraftIdentity = { purpose: 'file', profileId: '11111111-1111-4111-8111-111111111111', key: 'file:admin-list', connectionId: null }
const absent = { ok: true, text: null, revision: 41, message: '' }
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}
beforeEach(() => {
  draftApi.read.mockReset().mockResolvedValue(absent)
  draftApi.save.mockReset().mockImplementation(async (_identity: DraftIdentity, text: string, revision: number) => ({ ok: true, text, revision: revision + 1, message: '' }))
  draftApi.clear.mockReset().mockImplementation(async (_identity: DraftIdentity, revision: number) => ({ ok: true, text: null, revision: revision + 1, message: '' }))
})
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks() })

describe('protected editor drafts', () => {
  it('never overwrites an unread recovered draft with initial or newly edited state', async () => {
    const read = deferred<ProtectedDraftResult>()
    draftApi.read.mockReturnValueOnce(read.promise)
    const hook = renderHook(({ value }) => useEditorProtectedDraft(identity, value), { initialProps: { value: null as string | null } })
    hook.rerender({ value: 'a current edit' })
    await act(async () => { read.resolve({ ...absent, text: 'an earlier draft' }); await read.promise })
    expect(hook.result.current.recovered).toBe('an earlier draft')
    await act(async () => { expect(await hook.result.current.persistNow()).toBe(false) })
    expect(draftApi.save).not.toHaveBeenCalled()
    act(() => hook.result.current.acceptRecovery())
    await act(async () => { expect(await hook.result.current.persistNow()).toBe(true) })
    expect(draftApi.save).toHaveBeenCalledWith(identity, 'a current edit', 41)
  })

  it('uses the absent tombstone revision and flushes the last edit before the debounce on unmount', async () => {
    const hook = renderHook(({ value }) => useEditorProtectedDraft(identity, value), { initialProps: { value: null as string | null } })
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    expect(draftApi.save).not.toHaveBeenCalled()
    hook.rerender({ value: 'last keystroke' })
    hook.unmount()
    await waitFor(() => expect(draftApi.save).toHaveBeenCalledWith(identity, 'last keystroke', 41))
  })

  it('waits for the initial read before Finish later flushes, rather than assuming revision zero', async () => {
    const read = deferred<ProtectedDraftResult>()
    draftApi.read.mockReturnValueOnce(read.promise)
    const hook = renderHook(() => useEditorProtectedDraft(identity, 'unfinished setup'))
    let completed = false
    let persisted: Promise<boolean>
    act(() => { persisted = hook.result.current.persistNow().then(value => { completed = true; return value }) })
    expect(completed).toBe(false)
    expect(draftApi.save).not.toHaveBeenCalled()
    await act(async () => { read.resolve(absent); await persisted! })
    expect(draftApi.save).toHaveBeenCalledWith(identity, 'unfinished setup', 41)
  })

  it('ignores a late read after the selected profile changes and keeps writes in their captured scope', async () => {
    const read = deferred<ProtectedDraftResult>()
    draftApi.read.mockReturnValueOnce(read.promise)
    const other = { ...identity, profileId: '22222222-2222-4222-8222-222222222222' }
    const hook = renderHook(({ target, value }) => useEditorProtectedDraft(target, value),
      { initialProps: { target: identity, value: 'first profile edit' as string | null } })
    hook.rerender({ target: other, value: null })
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    await act(async () => { read.resolve({ ...absent, text: 'first profile recovered' }); await read.promise })
    expect(hook.result.current.recovered).toBeNull()
    expect(draftApi.save).not.toHaveBeenCalled()
    hook.rerender({ target: other, value: 'second profile edit' })
    await act(async () => { await hook.result.current.persistNow() })
    expect(draftApi.save).toHaveBeenCalledWith(other, 'second profile edit', 41)
  })

  it('serializes CAS writes and presents a concurrent change for explicit recovery', async () => {
    const write = deferred<ProtectedDraftResult>()
    draftApi.save.mockReturnValueOnce(write.promise).mockResolvedValueOnce({ ok: false, text: 'other window draft', revision: 45, message: 'Draft changed in another window.' })
    const hook = renderHook(({ value }) => useEditorProtectedDraft(identity, value), { initialProps: { value: null as string | null } })
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    hook.rerender({ value: 'first edit' })
    let first: Promise<boolean>
    act(() => { first = hook.result.current.persistNow() })
    await waitFor(() => expect(draftApi.save).toHaveBeenCalledTimes(1))
    hook.rerender({ value: 'second edit' })
    let second: Promise<boolean>
    act(() => { second = hook.result.current.persistNow() })
    expect(draftApi.save).toHaveBeenCalledTimes(1)
    await act(async () => { write.resolve({ ok: true, text: 'first edit', revision: 42, message: '' }); await first!; await second! })
    expect(draftApi.save).toHaveBeenLastCalledWith(identity, 'second edit', 42)
    expect(hook.result.current.status).toBe('conflict')
    expect(hook.result.current.recovered).toBe('other window draft')
    await act(async () => { expect(await hook.result.current.persistNow()).toBe(false) })
    expect(draftApi.save).toHaveBeenCalledTimes(2)
  })

  it('clears the resulting CAS revision and prevents autosave from resurrecting an accepted draft', async () => {
    const hook = renderHook(({ value }) => useEditorProtectedDraft(identity, value), { initialProps: { value: null as string | null } })
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    hook.rerender({ value: 'accepted edit' })
    await act(async () => { await hook.result.current.persistNow(); expect(await hook.result.current.clear()).toBe(true) })
    expect(draftApi.clear).toHaveBeenCalledWith(identity, 42)
    await act(async () => { await hook.result.current.persistNow() })
    expect(draftApi.save).toHaveBeenCalledTimes(1)
    hook.rerender({ value: 'new edit after save' })
    await act(async () => { await hook.result.current.persistNow() })
    expect(draftApi.save).toHaveBeenLastCalledWith(identity, 'new edit after save', 43)
  })

  it('registers a navigation guard that awaits the read and latest edit, then removes the old scope on unmount', async () => {
    const read = deferred<ProtectedDraftResult>()
    draftApi.read.mockReturnValueOnce(read.promise)
    const change = vi.fn()
    const hook = renderHook(({ value }) => {
      const draft = useEditorProtectedDraft(identity, value)
      useEditorDraftGuard('file:first', value !== null, draft, change)
      return draft
    }, { initialProps: { value: null as string | null } })
    hook.rerender({ value: 'latest edit before navigation' })
    const guard = change.mock.calls.at(-1)?.[1] as EditorDraftGuard
    expect(guard.dirty).toBe(true)
    expect(guard.saving).toBe(true)
    let flushed: Promise<boolean>
    act(() => { flushed = guard.flush() })
    expect(draftApi.save).not.toHaveBeenCalled()
    await act(async () => { read.resolve(absent); expect(await flushed!).toBe(true) })
    expect(draftApi.save).toHaveBeenCalledWith(identity, 'latest edit before navigation', 41)
    hook.unmount()
    expect(change).toHaveBeenLastCalledWith('file:first', null)
  })

  it('keeps navigation guarded when typing continues during a held save of the earlier value', async () => {
    const write = deferred<ProtectedDraftResult>()
    draftApi.save.mockReturnValueOnce(write.promise)
    const change = vi.fn()
    const hook = renderHook(({ value }) => {
      const draft = useEditorProtectedDraft(identity, value)
      useEditorDraftGuard('file:first', value !== null, draft, change)
      return draft
    }, { initialProps: { value: null as string | null } })
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    hook.rerender({ value: 'earlier edit A' })
    const guard = change.mock.calls.at(-1)?.[1] as EditorDraftGuard
    let flushed: Promise<boolean>
    act(() => { flushed = guard.flush() })
    await waitFor(() => expect(draftApi.save).toHaveBeenCalledWith(identity, 'earlier edit A', 41))
    hook.rerender({ value: 'newer edit B' })
    await act(async () => {
      write.resolve({ ok: true, text: 'earlier edit A', revision: 42, message: '' })
      expect(await flushed!).toBe(false)
    })
    const latestGuard = change.mock.calls.at(-1)?.[1] as EditorDraftGuard
    expect(latestGuard.dirty).toBe(true)
    await act(async () => { expect(await latestGuard.flush()).toBe(true) })
    expect(draftApi.save).toHaveBeenLastCalledWith(identity, 'newer edit B', 42)
  })

  it('does not let completion of an old scope authorize navigation in the new scope', async () => {
    const write = deferred<ProtectedDraftResult>()
    draftApi.save.mockReturnValueOnce(write.promise)
    const other = { ...identity, profileId: '22222222-2222-4222-8222-222222222222' }
    const hook = renderHook(({ target, value }) => useEditorProtectedDraft(target, value),
      { initialProps: { target: identity, value: null as string | null } })
    await waitFor(() => expect(hook.result.current.status).toBe('ready'))
    hook.rerender({ target: identity, value: 'same text in different scopes' })
    let flushed: Promise<boolean>
    act(() => { flushed = hook.result.current.persistNow() })
    await waitFor(() => expect(draftApi.save).toHaveBeenCalledTimes(1))
    hook.rerender({ target: other, value: 'same text in different scopes' })
    await act(async () => {
      write.resolve({ ok: true, text: 'same text in different scopes', revision: 42, message: '' })
      expect(await flushed!).toBe(false)
    })
    await waitFor(() => expect(draftApi.save).toHaveBeenCalledWith(other, 'same text in different scopes', 41))
  })

  it('can discard an earlier recovered record while retaining a different current edit', async () => {
    draftApi.read.mockResolvedValueOnce({ ...absent, text: 'earlier record' })
    const hook = renderHook(() => useEditorProtectedDraft(identity, 'current edit'))
    await waitFor(() => expect(hook.result.current.recovered).toBe('earlier record'))
    expect(draftApi.save).not.toHaveBeenCalled()
    await act(async () => { expect(await hook.result.current.clear({ keepCurrent: true })).toBe(true); await hook.result.current.persistNow() })
    expect(draftApi.clear).toHaveBeenCalledWith(identity, 41)
    expect(draftApi.save).toHaveBeenCalledWith(identity, 'current edit', 42)
  })
})

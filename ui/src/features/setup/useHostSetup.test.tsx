import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useHostSetup } from './useHostSetup'
import type { HostSnapshot, Settings } from '../../contracts'
import type { Profile } from '../../GameProfile'
import { readProtectedDraft, clearProtectedDraft, saveProtectedDraft } from '../../protectedUiDrafts'
import { serializeProtectedSetupDraft } from '../../setupDraft'

vi.mock('../../protectedUiDrafts', () => ({ readProtectedDraft: vi.fn(), clearProtectedDraft: vi.fn(), saveProtectedDraft: vi.fn() }))
const id = '11111111-1111-4111-8111-111111111111', nextId = '22222222-2222-4222-8222-222222222222'
const profile: Profile = { id, kind: 'Valheim', name: 'Friends', serverName: 'Friends', crossplay: false, publicListing: false,
  worldId: 'world', worldSource: 'Existing', worldDirectory: 'C:\\synthetic\\old', gamePort: 2456, executablePath: 'C:\\synthetic\\valheim_server.exe' }
const settings: Settings = { maxConcurrentServers: 1, idleMinutes: 15, friendTimerExtensionMinutes: 15, friendTimerExtensionMaximumMinutes: 60,
  autoShutdownEnabled: false, keepAwakeWhileHosting: false, remoteControlsEnabled: false, companionListeningEnabled: false,
  companionBindAddress: '127.0.0.1', companionEndpoint: 'https://127.0.0.1:5131', companionPort: 5131,
  connectionRoute: { mode: 'DirectInternet', address: '' }, publicGameIp: '', publicGameIpCheckedUtc: null, profiles: [profile] }
const host = (profiles = [profile]) => ({ mode: 'Host', settings: { ...settings, profiles }, passwordConfigured: { [id]: true },
  managedWorldsRoot: 'C:\\synthetic\\managed' }) as unknown as HostSnapshot
const options = (snapshot: HostSnapshot) => ({ snapshot, pending: '', setPending: vi.fn(), setNotice: vi.fn(), applySnapshot: vi.fn(), dataRecoveryBlocked: false, instance: null })
const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } })
beforeEach(() => {
  localStorage.clear()
  vi.mocked(readProtectedDraft).mockReset().mockResolvedValue({ ok: true, text: null, revision: 30, message: '' })
  vi.mocked(saveProtectedDraft).mockReset().mockImplementation(async (_identity, text, revision) => ({ ok: true, text, revision: revision + 1, message: '' }))
  vi.mocked(clearProtectedDraft).mockReset().mockImplementation(async (_identity, revision) => ({ ok: true, text: null, revision: revision + 1, message: '' }))
  vi.stubGlobal('fetch', vi.fn(async () => json({ installations: [], worlds: [] })))
})
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('Host setup continuity and imports', () => {
  it('restores a protected draft only after owner review and resumes the recorded step without secrets or policy', async () => {
    const recovered = { ...profile, id: nextId, worldId: 'unfinished' }
    vi.mocked(readProtectedDraft).mockResolvedValue({ ok: true, revision: 31, message: '', text: serializeProtectedSetupDraft([recovered], 'server', nextId, 'C:\\synthetic\\source') })
    const snapshot = host([])
    const opts = options(snapshot)
    const hook = renderHook(() => useHostSetup(opts))
    act(() => hook.result.current.synchronizeHostSnapshot(snapshot))
    await waitFor(() => expect(hook.result.current.setupDraftRecovery.recovered).not.toBeNull())
    expect(hook.result.current.draft?.profiles).toEqual([])
    act(() => hook.result.current.recoverSetupDraft())
    expect(hook.result.current.setupStep).toBe('server')
    expect(hook.result.current.sourceRoots[nextId]).toBe('C:\\synthetic\\source')
    expect(hook.result.current.passwords).toEqual({})
    expect(hook.result.current.customScripts).toEqual({})
    expect(hook.result.current.minecraftTerms).toEqual({})
    expect(hook.result.current.draft?.remoteControlsEnabled).toBe(false)
    expect(hook.result.current.showSetup).toBe(true)
  })

  it('reuses setup in the blank new-server slot instead of leaving an invalid extra profile', async () => {
    vi.spyOn(crypto, 'randomUUID').mockReturnValue(nextId)
    const snapshot = host()
    const hook = renderHook(() => useHostSetup(options(snapshot)))
    act(() => hook.result.current.synchronizeHostSnapshot(snapshot))
    act(() => hook.result.current.addProfile())
    expect(hook.result.current.draft?.profiles).toHaveLength(2)
    act(() => hook.result.current.reuseProfile(id))
    expect(hook.result.current.draft?.profiles).toHaveLength(2)
    expect(hook.result.current.editedProfile).toMatchObject({ id: nextId, worldId: '', gamePort: 2458, worldSource: 'New' })
    expect(hook.result.current.setupStep).toBe('world')
    await waitFor(() => expect(hook.result.current.discovery).not.toBeNull())
  })

  it('prepares a native save selection without copying and confirms only its fixed opaque identity', async () => {
    const factorio: Profile = { ...profile, kind: 'Factorio', worldId: '', worldDirectory: '', gamePort: 34197, factorio: { rconPort: 27015 } }
    const snapshot = host([factorio])
    const selectionId = '33333333-3333-4333-8333-333333333333'
    const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/preview-save') ? {
      ok: true, code: 'ImportPreviewReady', message: 'Review first.', preview: { selectionId, profileId: id, kind: 'Factorio', worldId: 'new-factory',
        sourceFiles: [{ name: 'new-factory.zip', bytes: 50, modifiedUtc: null }], totalBytes: 50, modifiedUtc: null, expiresUtc: '2099-01-01T00:00:00Z' } } :
      { ok: true, code: 'FactorioImported', message: 'Copied.', worldId: 'new-factory', worldDirectory: 'C:\\synthetic\\managed\\factory' }))
    vi.stubGlobal('fetch', fetcher)
    const hook = renderHook(() => useHostSetup(options(snapshot)))
    act(() => hook.result.current.synchronizeHostSnapshot(snapshot))
    await act(async () => { await hook.result.current.browseFactorio(factorio, 'save') })
    expect(hook.result.current.importReview?.selectionId).toBe(selectionId)
    expect(hook.result.current.editedProfile?.worldId).toBe('')
    expect(fetcher).toHaveBeenCalledTimes(1)
    await act(async () => { await hook.result.current.confirmImport() })
    expect(fetcher).toHaveBeenLastCalledWith('/api/local/setup/import-confirm', expect.objectContaining({ method: 'POST',
      body: JSON.stringify({ selectionId, profileId: id, kind: 'Factorio' }) }))
    expect(hook.result.current.editedProfile?.worldId).toBe('new-factory')
    expect(hook.result.current.importReview).toBeNull()
  })

  it('refuses an expired native selection before dispatching import and leaves a Valheim source unchanged until confirmation', async () => {
    const factorio: Profile = { ...profile, kind: 'Factorio', worldId: '', worldDirectory: '', gamePort: 34197, factorio: { rconPort: 27015 } }
    const snapshot = host([factorio])
    const fetcher = vi.fn(async () => json({ ok: true, code: 'ImportPreviewReady', message: 'Review.', preview: {
      selectionId: nextId, profileId: id, kind: 'Factorio', worldId: 'factory', sourceFiles: [{ name: 'factory.zip', bytes: 1, modifiedUtc: null }],
      totalBytes: 1, modifiedUtc: null, expiresUtc: '2000-01-01T00:00:00Z' } }))
    vi.stubGlobal('fetch', fetcher)
    const opts = options(snapshot)
    const hook = renderHook(() => useHostSetup(opts))
    act(() => hook.result.current.synchronizeHostSnapshot(snapshot))
    await act(async () => { await hook.result.current.browseFactorio(factorio, 'save'); await hook.result.current.confirmImport() })
    await act(async () => { await hook.result.current.confirmImport() })
    expect(fetcher).toHaveBeenCalledTimes(1)
    expect(opts.setNotice).toHaveBeenCalledWith(expect.objectContaining({ good: false, text: expect.stringContaining('expired') }))
    act(() => hook.result.current.importWorld(profile, 'C:\\synthetic\\source', 'selected-world'))
    expect(hook.result.current.importReview?.worldId).toBe('selected-world')
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})

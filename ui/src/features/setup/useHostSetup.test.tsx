import { act, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useHostSetup } from './useHostSetup'
import type { HostSnapshot, Settings } from '../../contracts'
import type { Profile } from '../../GameProfile'
import { readProtectedDraft, clearProtectedDraft, saveProtectedDraft } from '../../protectedUiDrafts'
import { serializeProtectedSetupDraft } from '../../setupDraft'
import type { ConfirmationOptions } from '../../Confirmation'

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
const options = (snapshot: HostSnapshot) => ({ snapshot, confirm: vi.fn<(options: ConfirmationOptions) => Promise<boolean>>().mockResolvedValue(true),
  pending: '', setPending: vi.fn(), setNotice: vi.fn(), applySnapshot: vi.fn(), dataRecoveryBlocked: false, instance: null })
const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } })
const originalShowModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal')
const originalClose = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close')
beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', '') } })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value(this: HTMLDialogElement) { this.removeAttribute('open') } })
  localStorage.clear()
  vi.mocked(readProtectedDraft).mockReset().mockResolvedValue({ ok: true, text: null, revision: 30, message: '' })
  vi.mocked(saveProtectedDraft).mockReset().mockImplementation(async (_identity, text, revision) => ({ ok: true, text, revision: revision + 1, message: '' }))
  vi.mocked(clearProtectedDraft).mockReset().mockImplementation(async (_identity, revision) => ({ ok: true, text: null, revision: revision + 1, message: '' }))
  vi.stubGlobal('fetch', vi.fn(async () => json({ installations: [], worlds: [] })))
})
afterEach(() => {
  vi.unstubAllGlobals(); vi.restoreAllMocks()
  if (originalShowModal) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', originalShowModal)
  else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal')
  if (originalClose) Object.defineProperty(HTMLDialogElement.prototype, 'close', originalClose)
  else Reflect.deleteProperty(HTMLDialogElement.prototype, 'close')
})

describe('Host setup continuity and imports', () => {
  it.each(['field', 'action'])('opens setup on a meaningful %s and returns focus to its opener', async target => {
    const opts = options(host())
    vi.spyOn(HTMLDialogElement.prototype, 'showModal').mockImplementation(function (this: HTMLDialogElement) {
      this.setAttribute('open', '')
      this.querySelector<HTMLElement>('[autofocus]')?.focus()
    })
    vi.spyOn(HTMLDialogElement.prototype, 'close').mockImplementation(function (this: HTMLDialogElement) { this.removeAttribute('open') })
    function Harness() {
      const setup = useHostSetup(opts)
      return <><button onClick={() => setup.openSetup(id)}>Open server setup</button>
        {setup.showSetup && <dialog ref={setup.setupRef} aria-label="Server setup">
          <button onClick={() => void setup.cancelSetup()}>Close setup</button>
          <details><summary>Advanced paths</summary><input aria-label="Hidden advanced path" /></details>
          <input disabled aria-label="Unavailable field" />
          {target === 'field' ? <input aria-label="Server name" /> :
            <div className="setup-stage"><button aria-pressed="true">Choose game</button></div>}
          <div className="wizard-footer"><div className="actions"><button>Cancel</button></div>
            <div className="actions"><button>Continue</button></div></div>
        </dialog>}</>
    }
    const view = render(<Harness />)
    const opener = screen.getByRole('button', { name: 'Open server setup' })
    opener.focus()
    fireEvent.click(opener)
    const meaningful = target === 'field' ? screen.getByRole('textbox', { name: 'Server name' }) : screen.getByRole('button', { name: 'Choose game' })
    expect(meaningful).toHaveFocus()
    expect(meaningful).not.toHaveAttribute('autofocus')
    fireEvent.click(screen.getByRole('button', { name: 'Close setup' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(opener).toHaveFocus())
    view.unmount()
  })
  it('declines removal, Finish later and cached-world copying without a mutation or losing review state', async () => {
    const snapshot = host()
    const opts = options(snapshot)
    opts.confirm.mockResolvedValue(false)
    const hook = renderHook(() => useHostSetup(opts))
    act(() => hook.result.current.synchronizeHostSnapshot(snapshot))
    await waitFor(() => expect(readProtectedDraft).toHaveBeenCalled())
    act(() => hook.result.current.setPassword(id, 'synthetic-secret'))
    await act(async () => { await hook.result.current.removeProfile(profile); await hook.result.current.finishSetupLater() })
    expect(clearProtectedDraft).not.toHaveBeenCalled()
    expect(hook.result.current.passwords[id]).toBe('synthetic-secret')
    act(() => hook.result.current.importWorld(profile, 'C:\\synthetic\\source', 'cached-world', 'worlds'))
    const review = hook.result.current.importReview
    await act(async () => { await hook.result.current.confirmImport() })
    expect(hook.result.current.importReview).toBe(review)
    expect(hook.result.current.sourceRoots).toEqual({})
    expect(opts.confirm.mock.calls.map(([options]) => options.description)).toEqual([
      'Remove Friends from TogetherServer? Its world files are left in place.',
      'Finish later clears the entered game password and unsaved custom scripts from this window. Continue?',
      'Close Valheim and wait for Steam Cloud to finish syncing before copying this cached world folder. Continue?'
    ])
    expect(vi.mocked(fetch).mock.calls.filter(([, request]) => request?.method === 'PUT' || request?.method === 'POST')).toEqual([])
  })

  it('keeps a changed draft and blocks removal when process guards change during confirmation', async () => {
    const snapshot = host()
    const opts = options(snapshot)
    let answer!: (accepted: boolean) => void
    opts.confirm.mockImplementation(() => new Promise(resolve => { answer = resolve }))
    const hook = renderHook(() => useHostSetup(opts))
    act(() => hook.result.current.synchronizeHostSnapshot(snapshot))
    await waitFor(() => expect(readProtectedDraft).toHaveBeenCalled())
    act(() => hook.result.current.updateProfile(id, { name: 'Reviewed name' }))
    let cancel!: Promise<void>
    act(() => { cancel = hook.result.current.cancelSetup() })
    act(() => hook.result.current.updateProfile(id, { name: 'New name' }))
    await act(async () => { answer(true); await cancel })
    expect(hook.result.current.draft?.profiles[0].name).toBe('New name')
    expect(clearProtectedDraft).not.toHaveBeenCalled()
    let removal!: Promise<void>
    act(() => { removal = hook.result.current.removeProfile(hook.result.current.draft!.profiles[0]) })
    opts.snapshot = { ...snapshot, runs: [{ profileId: id, state: 'Unknown', processId: 42, onlinePlayers: null }] } as HostSnapshot
    hook.rerender()
    await act(async () => { answer(true); await removal })
    expect(vi.mocked(fetch).mock.calls.filter(([, request]) => request?.method === 'PUT')).toEqual([])
    expect(opts.applySnapshot).not.toHaveBeenCalled()
  })

  it('rejects a cached-world decision for a replaced source selection and after unmount', async () => {
    const snapshot = host()
    const opts = options(snapshot)
    let answer!: (accepted: boolean) => void
    opts.confirm.mockImplementation(() => new Promise(resolve => { answer = resolve }))
    const hook = renderHook(() => useHostSetup(opts))
    act(() => hook.result.current.synchronizeHostSnapshot(snapshot))
    await waitFor(() => expect(readProtectedDraft).toHaveBeenCalled())
    act(() => hook.result.current.importWorld(profile, 'C:\\synthetic\\source', 'old', 'worlds'))
    let copying!: Promise<void>
    act(() => { copying = hook.result.current.confirmImport() })
    act(() => hook.result.current.importWorld(profile, 'C:\\synthetic\\other', 'new', 'worlds'))
    await act(async () => { answer(true); await copying })
    expect(hook.result.current.importReview?.worldId).toBe('new')
    expect(hook.result.current.sourceRoots).toEqual({})
    let removal!: Promise<void>
    act(() => { removal = hook.result.current.removeProfile(profile) })
    hook.unmount()
    await act(async () => { answer(true); await removal })
    expect(vi.mocked(fetch).mock.calls.filter(([, request]) => request?.method === 'PUT' || request?.method === 'POST')).toEqual([])
  })

  it('keeps first-server setup, secret and terms when cancellation is declined, then clears them on accepted Cancel without saving or starting', async () => {
    vi.spyOn(crypto, 'randomUUID').mockReturnValue(nextId)
    const snapshot = host([])
    const opts = options(snapshot)
    const confirm = opts.confirm.mockResolvedValue(false)
    const hook = renderHook(() => useHostSetup(opts))
    act(() => hook.result.current.synchronizeHostSnapshot(snapshot))
    act(() => hook.result.current.addProfile())
    await waitFor(() => expect(readProtectedDraft).toHaveBeenCalled())
    act(() => {
      hook.result.current.setPassword(nextId, 'synthetic-secret')
      hook.result.current.setMinecraftTermsFor(nextId, true)
    })
    await act(async () => { await hook.result.current.cancelSetup() })
    expect(confirm).toHaveBeenCalledOnce()
    expect(hook.result.current.showSetup).toBe(true)
    expect(hook.result.current.passwords[nextId]).toBe('synthetic-secret')
    expect(hook.result.current.minecraftTerms[nextId]).toBe(true)
    expect(clearProtectedDraft).not.toHaveBeenCalled()
    confirm.mockResolvedValue(true)
    await act(async () => { await hook.result.current.cancelSetup() })
    expect(hook.result.current.showSetup).toBe(false)
    expect(hook.result.current.passwords).toEqual({})
    expect(hook.result.current.minecraftTerms).toEqual({})
    expect(hook.result.current.draft?.profiles).toEqual([])
    expect(clearProtectedDraft).toHaveBeenCalled()
    for (const [url, request] of vi.mocked(fetch).mock.calls) {
      expect(String(url)).not.toMatch(/\/settings$|\/start$/u)
      expect(request?.method ?? 'GET').toBe('GET')
    }
  })

  it('persists a nonsecret first-server draft before Finish later closes setup without a game action', async () => {
    vi.spyOn(crypto, 'randomUUID').mockReturnValue(nextId)
    const snapshot = host([])
    const hook = renderHook(() => useHostSetup(options(snapshot)))
    act(() => hook.result.current.synchronizeHostSnapshot(snapshot))
    act(() => hook.result.current.addProfile())
    await waitFor(() => expect(readProtectedDraft).toHaveBeenCalled())
    act(() => {
      hook.result.current.updateProfile(nextId, { name: 'Unfinished synthetic server' })
      hook.result.current.setSetupStep('world')
      hook.result.current.setPassword(nextId, 'synthetic-secret')
    })
    let complete!: () => void
    vi.mocked(saveProtectedDraft).mockClear().mockImplementation((_identity, text, revision) => new Promise(resolve => {
      complete = () => resolve({ ok: true, text, revision: revision + 1, message: '' })
    }))
    let finishing!: Promise<void>
    act(() => { finishing = hook.result.current.finishSetupLater() })
    await waitFor(() => expect(saveProtectedDraft).toHaveBeenCalled())
    expect(hook.result.current.showSetup).toBe(true)
    const savedText = vi.mocked(saveProtectedDraft).mock.calls.at(-1)![1]
    expect(savedText).toContain('Unfinished synthetic server')
    expect(savedText).not.toContain('synthetic-secret')
    await act(async () => { complete(); await finishing })
    expect(hook.result.current.showSetup).toBe(false)
    expect(hook.result.current.passwords).toEqual({})
    expect(hook.result.current.pausedSetupProfileId).toBe(nextId)
    expect(hook.result.current.setupStep).toBe('world')
    for (const [url, request] of vi.mocked(fetch).mock.calls) {
      expect(String(url)).not.toMatch(/\/settings$|\/start$/u)
      expect(request?.method ?? 'GET').toBe('GET')
    }
  })

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

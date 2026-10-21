import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ServerFilesPanel } from './ServerFilesPanel'
import { readProtectedDraft, saveProtectedDraft, clearProtectedDraft } from './protectedUiDrafts'
import type { EditorDraftGuard } from './editorProtectedDraft'

vi.mock('./protectedUiDrafts', () => ({ readProtectedDraft: vi.fn(), saveProtectedDraft: vi.fn(), clearProtectedDraft: vi.fn() }))
beforeEach(() => {
  vi.mocked(readProtectedDraft).mockReset().mockResolvedValue({ ok: true, text: null, revision: 7, message: '' })
  vi.mocked(saveProtectedDraft).mockReset().mockImplementation(async (_identity, text, revision) => ({ ok: true, text, revision: revision + 1, message: '' }))
  vi.mocked(clearProtectedDraft).mockReset().mockImplementation(async (_identity, revision) => ({ ok: true, text: null, revision: revision + 1, message: '' }))
})

const profileId = '11111111-1111-4111-8111-111111111111'

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('ServerFilesPanel', () => {
  it('shows real folders and keeps file saving behind maintenance and Offline state', async () => {
    const prepare = vi.fn()
    let content = 'Steam_111\n'
    let sha = 'before'
    const fetcher = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input)
      const payload = path.endsWith('/files')
        ? { ok: true, code: 'ServerFilesReady', message: 'Ready',
          locations: [{ key: 'save', label: 'World and saves', path: 'C:\\worlds', available: true }],
          files: [{ key: 'admin-list', label: 'adminlist.txt', path: 'C:\\worlds\\adminlist.txt', available: true }] }
        : init?.method === 'PUT'
          ? (() => { content = 'Steam_222\n'; sha = 'after'; return { ok: true, code: 'FileSaved', message: 'Saved',
            key: 'admin-list', sha256: sha, canUndo: true } })()
          : { ok: true, code: 'ServerFileReady', message: 'Ready', key: 'admin-list',
            content, sha256: sha, canUndo: sha === 'after' }
      return new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } })
    })
    vi.stubGlobal('fetch', fetcher)
    const props = { profileId, state: 'Ready', maintenance: false, busy: false, recoveryBlocked: false,
      onPrepareMaintenance: prepare, onStart: vi.fn(), onOpenDoctor: vi.fn() }
    const view = render(<ServerFilesPanel {...props} />)
    expect(await screen.findByText('World and saves')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Edit file' }))
    const editor = await screen.findByRole('textbox', { name: 'File contents' })
    fireEvent.change(editor, { target: { value: 'Steam_222\n' } })
    fireEvent.click(screen.getByRole('button', { name: 'Review file changes' }))
    expect(screen.getByRole('region', { name: 'Review raw file changes' })).toHaveTextContent('Steam_111')
    expect(screen.getByRole('region', { name: 'Review raw file changes' })).toHaveTextContent('Steam_222')
    const comparison = screen.getByRole('region', { name: 'File change comparison' })
    expect(comparison).toHaveAttribute('tabindex', '0')
    expect(within(comparison).getByRole('columnheader', { name: 'Exact text' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Back up and save' })).toBeDisabled()
    const warning = screen.getByText('The server must report 0 players before Prepare changes can stop it. Players online or an unknown count block Stop.')
    fireEvent.click(within(warning).getByRole('button', { name: 'Dismiss notification' }))
    expect(screen.queryByText(warning.textContent!)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Back up and save' })).toBeDisabled()
    expect(editor).toHaveValue('Steam_222\n')
    expect(fetcher).not.toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ method: 'PUT' }))
    fireEvent.click(screen.getByRole('button', { name: 'Open maintenance guide' }))
    expect(prepare).toHaveBeenCalledOnce()
    view.rerender(<ServerFilesPanel {...props} state="Offline" maintenance />)
    fireEvent.click(screen.getByRole('button', { name: 'Back up and save' }))
    await waitFor(() => expect(screen.getByText('Saved')).toBeInTheDocument())
    expect(fetcher).toHaveBeenCalledWith(`/api/local/profiles/${profileId}/files/admin-list`,
      expect.objectContaining({ method: 'PUT' }))
    expect(screen.getByRole('button', { name: 'Undo last change' })).toBeEnabled()
  })

  it('requires a fresh file review after each edit and keeps dirty reload cancelable', async () => {
    const fetcher = vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input)
      return new Response(JSON.stringify(path.endsWith('/files') ? { ok: true, code: 'ServerFilesReady', message: 'Ready', locations: [],
        files: [{ key: 'admin-list', label: 'adminlist.txt', path: 'C:\\synthetic\\adminlist.txt', available: true }] } : path.endsWith('/backups') ? { backups: [] } :
        { ok: true, code: 'ServerFileReady', message: 'Ready', key: 'admin-list', content: 'Steam_111\n', sha256: 'current', canUndo: false }),
      { status: 200, headers: { 'Content-Type': 'application/json' } })
    })
    vi.stubGlobal('fetch', fetcher)
    const drafts = vi.fn()
    render(<ServerFilesPanel profileId={profileId} state="Offline" maintenance busy={false} recoveryBlocked={false}
      onPrepareMaintenance={vi.fn()} onStart={vi.fn()} onOpenDoctor={vi.fn()} onDraftStateChange={drafts} />)
    fireEvent.click((await screen.findAllByRole('button', { name: 'Edit file' }))[0])
    fireEvent.change(await screen.findByLabelText('File contents'), { target: { value: 'Steam_222\n' } })
    expect(screen.getByRole('button', { name: 'Back up and save' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Review file changes' }))
    expect(screen.getByRole('button', { name: 'Back up and save' })).toBeEnabled()
    fireEvent.change(screen.getByLabelText('File contents'), { target: { value: 'Steam_333\n' } })
    expect(screen.getByRole('button', { name: 'Back up and save' })).toBeDisabled()
    const reads = fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list')).length
    fireEvent.click(screen.getByRole('button', { name: 'Reload file' }))
    const dialog = await screen.findByRole('dialog', { name: 'Reload server file?' })
    expect(dialog).toHaveTextContent('Save a draft of your edits, then reload this file?')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByLabelText('File contents')).toHaveValue('Steam_333\n')
    expect(fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list'))).toHaveLength(reads)
    expect(drafts).toHaveBeenCalledWith(`file:${profileId}`, true)
    const unload = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(unload)
    expect(unload.defaultPrevented).toBe(true)
  })

  it('recovers only on explicit review and saves against the current hash after a stale source', async () => {
    vi.mocked(readProtectedDraft).mockResolvedValue({ ok: true, revision: 9, message: '',
      text: JSON.stringify({ version: 1, key: 'admin-list', sha256: 'old-file-hash', content: 'Steam_333\n' }) })
    let saved = false
    const fetcher = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input)
      const payload = path.endsWith('/files') ? { ok: true, code: 'ServerFilesReady', message: 'Ready', locations: [],
        files: [{ key: 'admin-list', label: 'adminlist.txt', path: 'C:\\synthetic\\adminlist.txt', available: true }] } : path.endsWith('/backups') ? { backups: [] } :
        init?.method === 'PUT' ? (() => { saved = true; return { ok: true, code: 'FileSaved', message: 'Saved', key: 'admin-list', sha256: 'new', canUndo: true } })() :
          { ok: true, code: 'ServerFileReady', message: 'Ready', key: 'admin-list', content: saved ? 'Steam_333\n' : 'Steam_111\n', sha256: saved ? 'new' : 'current-file-hash', canUndo: saved }
      return new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } })
    })
    vi.stubGlobal('fetch', fetcher)
    render(<ServerFilesPanel profileId={profileId} state="Offline" maintenance busy={false} recoveryBlocked={false}
      onPrepareMaintenance={vi.fn()} onStart={vi.fn()} onOpenDoctor={vi.fn()} />)
    fireEvent.click((await screen.findAllByRole('button', { name: 'Edit file' }))[0])
    await screen.findByRole('button', { name: 'Review recovered draft' })
    expect(screen.getByLabelText('File contents')).toHaveValue('Steam_111\n')
    expect(screen.getByLabelText('File contents')).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Review recovered draft' }))
    expect(screen.getByLabelText('File contents')).toHaveValue('Steam_333\n')
    expect(screen.getByText(/file changed since this draft/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Review file changes' }))
    fireEvent.click(screen.getByRole('button', { name: 'Back up and save' }))
    await waitFor(() => expect(fetcher).toHaveBeenCalledWith(expect.stringContaining('/files/admin-list'), expect.objectContaining({
      method: 'PUT', body: JSON.stringify({ expectedSha256: 'current-file-hash', content: 'Steam_333\n' }) })))
    await waitFor(() => expect(clearProtectedDraft).toHaveBeenCalledWith(expect.objectContaining({ purpose: 'file', profileId, key: 'file:admin-list' }), 9))
  })
})

const confirmationProps = { profileId, state: 'Offline', maintenance: true, busy: false, recoveryBlocked: false,
  onPrepareMaintenance: vi.fn(), onStart: vi.fn(), onOpenDoctor: vi.fn() }
const fileListing = { ok: true, code: 'ServerFilesReady', message: 'Ready', locations: [], files: [
  { key: 'admin-list', label: 'adminlist.txt', path: 'C:\\synthetic\\adminlist.txt', available: true },
  { key: 'factorio-settings', label: 'server-settings.json', path: 'C:\\synthetic\\server-settings.json', available: false } ] }
const currentFile = { ok: true, code: 'ServerFileReady', message: 'Ready', key: 'admin-list', content: 'Steam_111\n',
  sha256: 'current-file-hash', canUndo: true }
const completeBackups = { backups: [{ id: 'checkpoint', createdUtc: '2026-10-10T12:00:00Z', setupIncluded: true, backupKind: 'Manual' }] }
function confirmationFetcher() {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input)
    const payload = init?.method === 'POST'
      ? { ok: false, code: 'ReviewRequired', message: 'Backend guard retained.', key: 'admin-list', sha256: 'current-file-hash', canUndo: true }
      : path.endsWith('/files') ? fileListing : path.endsWith('/backups') ? completeBackups : currentFile
    return new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } })
  })
}
const guardedActions = [
  { action: 'Undo last change', title: 'Restore previous file version?', accept: 'Restore previous version', route: '/files/admin-list/undo',
    description: 'Restore the previous file version? The stopped world will be backed up first.', body: { expectedSha256: 'current-file-hash' }, state: 'Offline' },
  { action: 'Create config', title: 'Create game configuration?', accept: 'Create configuration', route: '/files/factorio-settings/create',
    description: 'Create this game configuration? The stopped world will be backed up first.', body: undefined, state: 'Offline' },
  { action: 'Restore setup', title: 'Restore complete setup?', accept: 'Restore setup', route: '/backups/checkpoint/restore-setup',
    description: 'Restore this backup? It replaces the world, supported settings, and add-ons while the server is stopped. A backup of the current setup will be kept first.', body: undefined, state: 'Offline' },
  { action: 'Prepare changes', title: 'Prepare server changes?', accept: 'Prepare changes', route: '/prepare-change',
    description: 'Prepare to edit this server? Friend controls will pause. TogetherServer will check for 0 players before stopping, then back up the world and supported setup.', body: undefined, state: 'Offline' },
  { action: 'Finish maintenance', title: 'Finish maintenance?', accept: 'Confirm game join and finish', route: '/finish-change',
    description: 'Have you joined this server from a real game client and checked the changed setup? Confirm to end maintenance and allow Friend controls again.', body: { confirmedGameJoin: true }, state: 'Ready' },
]

it.each(guardedActions)('requires a dialog decision for $action and preserves the original request', async item => {
  const fetcher = confirmationFetcher()
  vi.stubGlobal('fetch', fetcher)
  render(<ServerFilesPanel {...confirmationProps} state={item.state} />)
  fireEvent.click((await screen.findAllByRole('button', { name: 'Edit file' }))[0])
  await screen.findByLabelText('File contents')
  if (item.action === 'Restore setup') fireEvent.click(screen.getByRole('button', { name: 'Setup backups' }))
  fireEvent.click(screen.getByRole('button', { name: item.action }))
  let dialog = await screen.findByRole('dialog', { name: item.title })
  expect(dialog).toHaveTextContent(item.description)
  expect(fetcher.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(fetcher.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  fireEvent.click(screen.getByRole('button', { name: item.action }))
  dialog = await screen.findByRole('dialog', { name: item.title })
  fireEvent.click(within(dialog).getByRole('button', { name: item.accept }))
  await waitFor(() => expect(fetcher).toHaveBeenCalledWith(`/api/local/profiles/${profileId}${item.route}`,
    expect.objectContaining({ method: 'POST', body: item.body === undefined ? undefined : JSON.stringify(item.body),
      headers: expect.objectContaining({ 'X-TogetherServer-Local': '1' }) })))
})

it.each(guardedActions)('refuses $action when the current lifecycle guard changes during confirmation', async item => {
  const fetcher = confirmationFetcher()
  vi.stubGlobal('fetch', fetcher)
  const rendered = render(<ServerFilesPanel {...confirmationProps} state={item.state} />)
  fireEvent.click((await screen.findAllByRole('button', { name: 'Edit file' }))[0])
  await screen.findByLabelText('File contents')
  if (item.action === 'Restore setup') fireEvent.click(screen.getByRole('button', { name: 'Setup backups' }))
  fireEvent.click(screen.getByRole('button', { name: item.action }))
  const dialog = await screen.findByRole('dialog', { name: item.title })
  rendered.rerender(<ServerFilesPanel {...confirmationProps} state="Unknown" />)
  expect(screen.getByRole('dialog')).toBe(dialog)
  rendered.rerender(<ServerFilesPanel {...confirmationProps} state={item.state} />)
  fireEvent.click(within(dialog).getByRole('button', { name: item.accept }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(fetcher.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
})

it('cancels the previous profile decision when the selected server changes', async () => {
  const fetcher = confirmationFetcher()
  vi.stubGlobal('fetch', fetcher)
  const rendered = render(<ServerFilesPanel {...confirmationProps} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Setup backups' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Restore setup' }))
  await screen.findByRole('dialog', { name: 'Restore complete setup?' })
  rendered.rerender(<ServerFilesPanel {...confirmationProps} profileId="22222222-2222-4222-8222-222222222222" />)
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(fetcher.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
})

it('keeps file edits on decline and clears them only after accepted discard', async () => {
  const fetcher = confirmationFetcher()
  vi.stubGlobal('fetch', fetcher)
  render(<ServerFilesPanel {...confirmationProps} />)
  fireEvent.click((await screen.findAllByRole('button', { name: 'Edit file' }))[0])
  fireEvent.change(await screen.findByLabelText('File contents'), { target: { value: 'Steam_222\n' } })
  fireEvent.click(screen.getByRole('button', { name: 'Discard unsaved file edits' }))
  let dialog = await screen.findByRole('dialog', { name: 'Discard file edits?' })
  expect(dialog).toHaveTextContent('Discard the unsaved contents of this editor? The current saved file stays unchanged.')
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(screen.getByLabelText('File contents')).toHaveValue('Steam_222\n')
  expect(clearProtectedDraft).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: 'Discard unsaved file edits' }))
  dialog = await screen.findByRole('dialog', { name: 'Discard file edits?' })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Discard file edits' }))
  await waitFor(() => expect(screen.getByLabelText('File contents')).toHaveValue('Steam_111\n'))
  await waitFor(() => expect(clearProtectedDraft).toHaveBeenCalledOnce())
  expect(fetcher.mock.calls.some(([, init]) => init?.method === 'POST' || init?.method === 'PUT')).toBe(false)
})

it('keeps the current editor when its contents change during a reload decision', async () => {
  const fetcher = confirmationFetcher()
  vi.stubGlobal('fetch', fetcher)
  render(<ServerFilesPanel {...confirmationProps} />)
  fireEvent.click((await screen.findAllByRole('button', { name: 'Edit file' }))[0])
  fireEvent.change(await screen.findByLabelText('File contents'), { target: { value: 'Steam_222\n' } })
  fireEvent.click(screen.getByRole('button', { name: 'Reload file' }))
  const dialog = await screen.findByRole('dialog', { name: 'Reload server file?' })
  const reads = fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list')).length
  fireEvent.change(screen.getByLabelText('File contents'), { target: { value: 'Steam_333\n' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Keep draft and reload' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(screen.getByLabelText('File contents')).toHaveValue('Steam_333\n')
  expect(fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list'))).toHaveLength(reads)
})

it('waits for protected draft persistence before an accepted file reload', async () => {
  let finishSave: (() => void) | undefined
  vi.mocked(saveProtectedDraft).mockImplementationOnce(async (_identity, text, revision) => {
    await new Promise<void>(resolve => { finishSave = resolve })
    return { ok: true, text, revision: revision + 1, message: '' }
  })
  const fetcher = confirmationFetcher()
  vi.stubGlobal('fetch', fetcher)
  render(<ServerFilesPanel {...confirmationProps} />)
  fireEvent.click((await screen.findAllByRole('button', { name: 'Edit file' }))[0])
  fireEvent.change(await screen.findByLabelText('File contents'), { target: { value: 'Steam_222\n' } })
  const reads = fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list')).length
  fireEvent.click(screen.getByRole('button', { name: 'Reload file' }))
  fireEvent.click(within(await screen.findByRole('dialog', { name: 'Reload server file?' })).getByRole('button', { name: 'Keep draft and reload' }))
  await waitFor(() => expect(finishSave).toBeDefined())
  expect(fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list'))).toHaveLength(reads)
  expect(screen.getByLabelText('File contents')).toHaveValue('Steam_222\n')
  finishSave!()
  await waitFor(() => expect(fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list'))).toHaveLength(reads + 1))
  await waitFor(() => expect(screen.getByLabelText('File contents')).toHaveValue('Steam_111\n'))
})

it('preserves a reviewed dirty file across supporting sections and blocks conflicting changes', async () => {
  const addOns = { ok: true, code: 'AddOnsReady', message: 'Ready', gameVersion: '2.0',
    requiredOnFriendPc: 'Match enabled add-ons.', stateToken: 'before', canImport: true, canUndo: false,
    warning: null, importType: 'FactorioMod', items: [] }
  const fetcher = vi.fn(async (input: RequestInfo | URL) => {
    const path = String(input)
    const payload = path.endsWith('/files') ? fileListing : path.endsWith('/backups') ? completeBackups :
      path.endsWith('/addons') ? addOns : currentFile
    return new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } })
  })
  vi.stubGlobal('fetch', fetcher)
  const draftState = vi.fn()
  render(<ServerFilesPanel {...confirmationProps} onDraftStateChange={draftState} />)
  fireEvent.click((await screen.findAllByRole('button', { name: 'Edit file' }))[0])
  const editor = await screen.findByLabelText('File contents')
  fireEvent.change(editor, { target: { value: 'Steam_333\n' } })
  fireEvent.click(screen.getByRole('button', { name: 'Review file changes' }))
  expect(screen.getByRole('button', { name: 'Back up and save' })).toBeEnabled()
  const fileReads = fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list')).length
  const navigation = within(screen.getByRole('navigation', { name: 'Server file sections' }))

  fireEvent.click(navigation.getByRole('button', { name: 'Add-ons' }))
  expect(editor).not.toBeVisible()
  expect(await screen.findByRole('button', { name: 'Import mod ZIP' })).toBeDisabled()
  expect(screen.getByText('Save or discard your file edits before changing add-ons.')).toBeVisible()
  fireEvent.click(navigation.getByRole('button', { name: 'Setup backups' }))
  expect(screen.getByRole('button', { name: 'Restore setup' })).toBeDisabled()
  expect(editor).toHaveValue('Steam_333\n')
  fireEvent.click(navigation.getByRole('button', { name: /^Files/ }))

  expect(screen.getByLabelText('File contents')).toBe(editor)
  expect(editor).toBeVisible()
  expect(editor).toHaveValue('Steam_333\n')
  expect(screen.getByRole('button', { name: 'Back up and save' })).toBeEnabled()
  expect(screen.getByRole('region', { name: 'Review raw file changes' })).toHaveTextContent('Steam_333')
  expect(fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list'))).toHaveLength(fileReads)
  expect(clearProtectedDraft).not.toHaveBeenCalled()
  expect(draftState).toHaveBeenLastCalledWith(`file:${profileId}`, true)
})

it('reveals a hidden file editor when its protected draft cannot be kept before navigation', async () => {
  // Fail storage before editing so an autosave cannot keep this exact draft
  // successfully before the navigation flush checks whether it is durable.
  vi.mocked(saveProtectedDraft).mockRejectedValue(new Error('Protected draft unavailable'))
  const fetcher = vi.fn(async (input: RequestInfo | URL) => {
    const path = String(input)
    return new Response(JSON.stringify(path.endsWith('/files') ? fileListing : path.endsWith('/backups') ? completeBackups : currentFile),
      { status: 200, headers: { 'Content-Type': 'application/json' } })
  })
  vi.stubGlobal('fetch', fetcher)
  let guard: EditorDraftGuard | null = null
  render(<ServerFilesPanel {...confirmationProps} onDraftGuardChange={(_source, next) => { guard = next }} />)
  fireEvent.click((await screen.findAllByRole('button', { name: 'Edit file' }))[0])
  const editor = await screen.findByLabelText('File contents')
  fireEvent.change(editor, { target: { value: 'Steam_333\n' } })
  await waitFor(() => expect(guard?.dirty).toBe(true))
  fireEvent.click(within(screen.getByRole('navigation', { name: 'Server file sections' })).getByRole('button', { name: 'Setup backups' }))
  expect(editor).not.toBeVisible()
  await act(async () => { expect(await guard!.flush()).toBe(false) })
  expect(saveProtectedDraft).toHaveBeenCalledWith(expect.objectContaining({ purpose: 'file', profileId, key: 'file:admin-list' }),
    JSON.stringify({ version: 1, key: 'admin-list', sha256: 'current-file-hash', content: 'Steam_333\n' }), 7)
  await waitFor(() => expect(guard?.dirty).toBe(true))
  expect(editor).toBeVisible()
  expect(editor).toHaveValue('Steam_333\n')
  expect(screen.getByText('Protected draft unavailable')).toBeVisible()
})

it('keeps unopened file tools from blocking server actions without any draft to save', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => new Response(JSON.stringify(
    String(input).endsWith('/files') ? fileListing : completeBackups),
    { status: 200, headers: { 'Content-Type': 'application/json' } })))
  let guard: EditorDraftGuard | null = null
  render(<ServerFilesPanel {...confirmationProps} onDraftGuardChange={(_source, next) => { guard = next }} />)
  await screen.findAllByRole('button', { name: 'Edit file' })
  expect(guard).toMatchObject({ dirty: false, saving: false })
  fireEvent.click(within(screen.getByRole('navigation', { name: 'Server file sections' })).getByRole('button', { name: 'Setup backups' }))
  expect(guard).toMatchObject({ dirty: false, saving: false })
  expect(readProtectedDraft).not.toHaveBeenCalled()
})

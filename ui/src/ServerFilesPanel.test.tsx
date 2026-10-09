import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ServerFilesPanel } from './ServerFilesPanel'
import { readProtectedDraft, saveProtectedDraft, clearProtectedDraft } from './protectedUiDrafts'

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
    vi.spyOn(window, 'confirm').mockReturnValue(true)
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
    expect(screen.getByRole('button', { name: 'Save with checkpoint' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Open maintenance guide' }))
    expect(prepare).toHaveBeenCalledOnce()
    view.rerender(<ServerFilesPanel {...props} state="Offline" maintenance />)
    fireEvent.click(screen.getByRole('button', { name: 'Save with checkpoint' }))
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
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    const drafts = vi.fn()
    render(<ServerFilesPanel profileId={profileId} state="Offline" maintenance busy={false} recoveryBlocked={false}
      onPrepareMaintenance={vi.fn()} onStart={vi.fn()} onOpenDoctor={vi.fn()} onDraftStateChange={drafts} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Edit file' }))
    fireEvent.change(await screen.findByLabelText('File contents'), { target: { value: 'Steam_222\n' } })
    expect(screen.getByRole('button', { name: 'Save with checkpoint' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Review file changes' }))
    expect(screen.getByRole('button', { name: 'Save with checkpoint' })).toBeEnabled()
    fireEvent.change(screen.getByLabelText('File contents'), { target: { value: 'Steam_333\n' } })
    expect(screen.getByRole('button', { name: 'Save with checkpoint' })).toBeDisabled()
    const reads = fetcher.mock.calls.filter(([input]) => String(input).endsWith('/files/admin-list')).length
    fireEvent.click(screen.getByRole('button', { name: 'Reload file' }))
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
    fireEvent.click(await screen.findByRole('button', { name: 'Edit file' }))
    await screen.findByRole('button', { name: 'Review recovered draft' })
    expect(screen.getByLabelText('File contents')).toHaveValue('Steam_111\n')
    expect(screen.getByLabelText('File contents')).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Review recovered draft' }))
    expect(screen.getByLabelText('File contents')).toHaveValue('Steam_333\n')
    expect(screen.getByText(/file changed since this draft/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Review file changes' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save with checkpoint' }))
    await waitFor(() => expect(fetcher).toHaveBeenCalledWith(expect.stringContaining('/files/admin-list'), expect.objectContaining({
      method: 'PUT', body: JSON.stringify({ expectedSha256: 'current-file-hash', content: 'Steam_333\n' }) })))
    await waitFor(() => expect(clearProtectedDraft).toHaveBeenCalledWith(expect.objectContaining({ purpose: 'file', profileId, key: 'file:admin-list' }), 9))
  })
})

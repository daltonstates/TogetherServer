import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ServerFilesPanel } from './ServerFilesPanel'

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
})

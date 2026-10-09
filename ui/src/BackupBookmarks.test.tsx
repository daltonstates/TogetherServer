import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from './api'
import { BackupBookmarks } from './BackupBookmarks'
import { parseBackupBookmarkResult, parseBackupBookmarks, type BackupBookmarksResult } from './backupBookmarksWire'

const profileId = '11111111-1111-4111-8111-111111111111'
const backupId = '22222222-2222-4222-8222-222222222222'
const empty: BackupBookmarksResult = {
  ok: true, code: 'BackupBookmarks', message: 'Completed backups kept on this Host.', profileId, backups: [],
  pinnedCount: 0, pinnedSizeBytes: 0, maximumLabelLength: 64, maximumPinnedCount: 20,
  maximumPinnedSizeBytes: 50 * 1024 ** 3, moreBackupsAvailable: false
}
const named: BackupBookmarksResult = { ...empty, pinnedCount: 1, pinnedSizeBytes: 1024, backups: [{
  profileId, backupId, createdUtc: '2026-10-08T12:00:00Z', backupKind: 'Manual', sizeBytes: 1024,
  label: 'Before a game update', pinned: true
}] }

describe('BackupBookmarks', () => {
  it('loads only while visible and distinguishes loading from no completed backups', async () => {
    let resolve!: (value: BackupBookmarksResult) => void
    const loader = vi.fn(() => new Promise<BackupBookmarksResult>(done => { resolve = done }))
    const { rerender } = render(<BackupBookmarks profileId={profileId} visible={false} loader={loader} />)
    expect(loader).not.toHaveBeenCalled()
    rerender(<BackupBookmarks profileId={profileId} visible loader={loader} />)
    expect(screen.getByText('Loading backup names and pins…')).toBeInTheDocument()
    resolve(empty)
    expect(await screen.findByText(/No completed backups yet/)).toBeInTheDocument()
  })

  it('saves a deliberate name/unpin change for the exact backup and explains later retention', async () => {
    const onChanged = vi.fn()
    const updater = vi.fn(async (_profile: string, _backup: string, change: { label: string; pinned: boolean }) => ({
      ok: true, code: 'BackupBookmarkUpdated', message: 'Saved.', profileId, backupId,
      backup: { ...named.backups[0], ...change }
    }))
    const { container } = render(<BackupBookmarks profileId={profileId} visible loader={async () => named} updater={updater} onChanged={onChanged} />)
    expect(await screen.findByText('Before a game update')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Name and retention'))
    fireEvent.change(screen.getByRole('textbox', { name: 'Backup name' }), { target: { value: '  Before new mods  ' } })
    fireEvent.click(screen.getByRole('checkbox', { name: 'Pin this backup' }))
    expect(screen.getByText(/a later backup can remove this one/)).toBeInTheDocument()
    expect(updater).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Save name and pin' }))
    expect(await screen.findByText('Saved. This backup follows automatic retention.')).toBeInTheDocument()
    expect(updater).toHaveBeenCalledWith(profileId, backupId, { label: 'Before new mods', pinned: false }, expect.any(AbortSignal))
    expect(onChanged).toHaveBeenCalledOnce()
    expect(container.querySelector('.good')).not.toBeInTheDocument()
    expect(screen.getByText(/Use Copy to vault/)).toBeInTheDocument()
  })

  it('rejects unsafe names locally and keeps a pin visible after a storage or capacity denial', async () => {
    const updater = vi.fn(async () => { throw new ApiError(400, 'PinnedBackupCapacityReached', 'C:\\private\\world password=secret') })
    render(<BackupBookmarks profileId={profileId} visible loader={async () => named} updater={updater} />)
    await screen.findByText('Before a game update')
    fireEvent.click(screen.getByText('Name and retention'))
    const input = screen.getByRole('textbox', { name: 'Backup name' })
    fireEvent.change(input, { target: { value: '../private' } })
    expect(screen.getByRole('button', { name: 'Save name and pin' })).toBeDisabled()
    expect(updater).not.toHaveBeenCalled()
    fireEvent.change(input, { target: { value: 'New safe name' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save name and pin' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('at most 50 GB')
    expect(screen.getByText('Pinned')).toBeInTheDocument()
    expect(screen.queryByText(/password=secret|C:\\private/)).not.toBeInTheDocument()
  })

  it('resets obsolete drafts when Refresh returns a newly pinned and renamed canonical backup', async () => {
    let canonical: BackupBookmarksResult = { ...named, pinnedCount: 0, pinnedSizeBytes: 0,
      backups: [{ ...named.backups[0], pinned: false }] }
    const loader = vi.fn(async () => canonical)
    const updater = vi.fn(async (_profile: string, _backup: string, change: { label: string; pinned: boolean }) => ({
      ok: true, code: 'BackupBookmarkUpdated', message: 'Saved.', profileId, backupId,
      backup: { ...canonical.backups[0], ...change }
    }))
    render(<BackupBookmarks profileId={profileId} visible loader={loader} updater={updater} />)
    await screen.findByText('Before a game update')
    fireEvent.click(screen.getByText('Name and retention'))
    expect(screen.getByRole('checkbox', { name: 'Pin this backup' })).not.toBeChecked()
    fireEvent.change(screen.getByRole('textbox', { name: 'Backup name' }), { target: { value: 'Obsolete unsaved name' } })

    canonical = { ...named, backups: [{ ...named.backups[0], label: 'Named elsewhere' }] }
    fireEvent.click(screen.getByRole('button', { name: 'Refresh names and pins' }))
    await screen.findByText('Named elsewhere')
    fireEvent.click(screen.getByText('Name and retention'))
    expect(screen.getByRole('checkbox', { name: 'Pin this backup' })).toBeChecked()
    expect(screen.getByRole('textbox', { name: 'Backup name' })).toHaveValue('Named elsewhere')
    expect(screen.getByRole('button', { name: 'Save name and pin' })).toBeDisabled()

    fireEvent.change(screen.getByRole('textbox', { name: 'Backup name' }), { target: { value: 'Name-only edit' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save name and pin' }))
    expect(await screen.findByText('Saved. This backup is protected from automatic retention.')).toBeInTheDocument()
    expect(updater).toHaveBeenCalledWith(profileId, backupId, { label: 'Name-only edit', pinned: true }, expect.any(AbortSignal))
    expect(screen.getByText('Pinned')).toBeInTheDocument()
  })

  it('rejects another backup in a successful mutation and aborts an obsolete profile load', async () => {
    const updater = vi.fn(async () => ({ ok: true, code: 'BackupBookmarkUpdated', message: 'Saved.', profileId,
      backupId: '33333333-3333-4333-8333-333333333333', backup: named.backups[0] }))
    const onChanged = vi.fn()
    const { rerender } = render(<BackupBookmarks profileId={profileId} visible loader={async () => named} updater={updater} onChanged={onChanged} />)
    await screen.findByText('Before a game update')
    fireEvent.click(screen.getByText('Name and retention'))
    fireEvent.change(screen.getByRole('textbox', { name: 'Backup name' }), { target: { value: 'Changed' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save name and pin' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('another backup')
    expect(onChanged).not.toHaveBeenCalled()
    let signal: AbortSignal | undefined
    const loader = vi.fn((_profile: string, current?: AbortSignal) => { signal = current; return new Promise<BackupBookmarksResult>(() => {}) })
    rerender(<BackupBookmarks profileId={profileId} visible loader={loader} />)
    expect(signal?.aborted).toBe(false)
    rerender(<BackupBookmarks profileId="33333333-3333-4333-8333-333333333333" visible={false} loader={loader} />)
    await waitFor(() => expect(signal?.aborted).toBe(true))
  })

  it('uses a fixed generic load error without exposing failure details', async () => {
    render(<BackupBookmarks profileId={profileId} visible loader={async () => { throw new Error('C:\\private\\secret') }} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Backup names and pins unavailable')
    expect(screen.queryByText(/private\\secret/)).not.toBeInTheDocument()
  })
})

describe('backup bookmark wire boundary', () => {
  it('accepts completed bounded records and exact successful identities', () => {
    expect(parseBackupBookmarks(named)).toEqual(named)
    expect(parseBackupBookmarkResult({ ok: true, code: 'BackupBookmarkUpdated', message: 'Saved.', profileId, backupId, backup: named.backups[0] }).backup?.pinned).toBe(true)
  })

  it('rejects identity swaps, duplicates, unsafe labels, unbounded sizes and inconsistent totals', () => {
    for (const bad of [
      { ...named, backups: [named.backups[0], named.backups[0]] },
      { ...named, pinnedCount: 0 },
      { ...named, pinnedSizeBytes: 1 },
      { ...named, maximumPinnedCount: 10000 },
      { ...named, backups: [{ ...named.backups[0], profileId: '33333333-3333-4333-8333-333333333333' }] },
      { ...named, backups: [{ ...named.backups[0], label: 'bad\u202Ename' }] },
      { ...named, backups: [{ ...named.backups[0], sizeBytes: Number.POSITIVE_INFINITY }] },
      { ...named, rawWorldPath: 'C:\\private' }
    ]) expect(() => parseBackupBookmarks(bad)).toThrow()
    expect(() => parseBackupBookmarkResult({ ok: true, code: 'BackupBookmarkUpdated', message: 'Saved.', profileId,
      backupId: '33333333-3333-4333-8333-333333333333', backup: named.backups[0] })).toThrow()
  })
})

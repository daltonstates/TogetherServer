import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { BackupCatalog } from './BackupCatalog'
import { BackupBookmarks } from './BackupBookmarks'
import type { BackupBookmarkChange, BackupBookmarkResult, BackupBookmarksResult } from './backupBookmarksWire'
import type { BackupCatalogResult, BackupSummary } from './backupCatalogModel'

const profileId = '11111111-1111-4111-8111-111111111111'
const pinned: BackupSummary = {
  profileId, backupId: '22222222-2222-4222-8222-222222222222', createdUtc: '2026-10-07T12:00:00Z',
  backupKind: 'Manual', sizeBytes: 1024, label: 'Before the update', pinned: true,
  gameKind: 'Valheim', worldId: 'FixtureWorld', fileCount: 2, setupIncluded: false,
  payloadSha256: 'A'.repeat(64), setupSha256: null, metadataAvailable: true, evidence: []
}
const newest: BackupSummary = {
  ...pinned, backupId: '33333333-3333-4333-8333-333333333333', createdUtc: '2026-10-10T12:00:00Z',
  backupKind: 'Rolling', label: 'After the weekend', pinned: false
}
const catalog: BackupCatalogResult = {
  ok: true, code: 'BackupCatalog', message: 'Completed local copies.', profileId, backups: [pinned, newest],
  pinnedCount: 1, pinnedSizeBytes: 1024, maximumLabelLength: 64, maximumPinnedCount: 20,
  maximumPinnedSizeBytes: 50 * 1024 ** 3, moreBackupsAvailable: false,
  retention: { retentionCount: 3, minimumFreeSpaceBytes: 1024 ** 3, rollingEnabled: true },
  retainedSizeBytes: 2048, availableSpaceBytes: null, evidenceAvailable: true
}
const loader = async () => catalog

async function backupView(name = pinned.label) {
  return within((await screen.findByText(name)).closest('article')!)
}

describe('Backup catalog presentation', () => {
  it('summarizes the newest completion independently of pin-first rows and keeps unknown drive space honest', async () => {
    render(<BackupCatalog profileId={profileId} visible loader={loader} />)
    await backupView()
    expect(screen.getByRole('heading', { name: 'Backup library' })).toBeVisible()
    const summary = within(screen.getByLabelText('Protection summary'))
    const latest = within(summary.getByText('Latest completed').closest('div')!)
    expect(latest.getByText(new Date(newest.createdUtc).toLocaleDateString())).toHaveAttribute('dateTime', newest.createdUtc)
    expect(summary.getByText('Completed copies').closest('div')).toHaveTextContent('2')
    expect(summary.getByText('Backup storage').closest('div')).toHaveTextContent('2 KiB')
    expect(summary.getByText('Backup drive free space unknown')).toBeVisible()
    expect(summary.getByText('Pinned copies').closest('div')).toHaveTextContent('1 / 20')
    expect(screen.getAllByRole('article')[0]).toHaveTextContent(pinned.label)
    expect(screen.getByRole('search', { name: 'Find backups' })).toBeVisible()
  })

  it('labels a partial catalog as loaded copies and leaves the saved retention policy unchanged', async () => {
    const partial = { ...catalog, moreBackupsAvailable: true }
    const load = vi.fn(async () => partial)
    render(<BackupCatalog profileId={profileId} visible loader={load} />)
    await backupView()
    const summary = within(screen.getByLabelText('Protection summary'))
    expect(summary.getByText('Latest loaded copy')).toBeVisible()
    expect(summary.getByText('Loaded copies')).toBeVisible()
    expect(summary.queryByText('Latest completed')).not.toBeInTheDocument()
    expect(screen.getByText(/Search, filters and comparisons cover only these backups/)).toBeVisible()
    const retentionToggle = screen.getByText('Preview which backups are kept')
    fireEvent.click(retentionToggle)
    expect(screen.getByText(/Load the full backup list and saved settings/)).toBeVisible()
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Preview unpinned copies to keep' }), { target: { value: '1' } })
    expect(screen.getByText(/Saved settings: keep 3 unpinned copies/)).toBeVisible()
    expect(partial.retention?.retentionCount).toBe(3)
    expect(load).toHaveBeenCalledOnce()
  })

  it('keeps failures and incomplete game evidence visible alongside a measured integrity pass', async () => {
    const checkedUtc = '2026-10-11T12:00:00Z'
    const measured: BackupSummary = { ...pinned, evidence: [
      { backupId: pinned.backupId, kind: 'Integrity', outcome: 'Passed', checkedUtc, code: 'BackupVerified' },
      { backupId: pinned.backupId, kind: 'Vault', outcome: 'Failed', checkedUtc, code: 'VaultCopyFailed' },
      { backupId: pinned.backupId, kind: 'OwnerGameRehearsal', outcome: 'Incomplete', checkedUtc, code: 'GameCheckIncomplete' }
    ] }
    const verify = vi.fn()
    const vault = vi.fn()
    render(<BackupCatalog profileId={profileId} visible loader={async () => ({ ...catalog, backups: [measured, newest] })}
      onVerify={verify} onVault={vault} />)
    const copy = await backupView()
    expect(copy.getByText('File check passed')).toBeVisible()
    expect(copy.getByText('Recorded check failed: Vault copy.')).toBeVisible()
    expect(copy.getByText('Incomplete: Game load, change and restart.')).toBeVisible()
    expect(copy.getByText('Files matched when checked. The world still needs testing in the game.')).toBeVisible()
    expect(copy.queryByRole('button', { name: 'Verify' })).not.toBeInTheDocument()
    fireEvent.click(copy.getByRole('button', { name: 'Review backup' }))
    expect(copy.getByText(`Owner reported: Incomplete · ${new Date(checkedUtc).toLocaleString()}`)).toBeVisible()
    expect(copy.getByText('Unknown — no saved result for this backup')).toBeVisible()
    expect(verify).not.toHaveBeenCalled()
    expect(vault).not.toHaveBeenCalled()
  })

  it('keeps legacy bookmark loaders and custom review actions available without inventing storage or integrity evidence', async () => {
    const bookmarks: BackupBookmarksResult = {
      ok: true, code: 'BackupBookmarks', message: 'Completed copies.', profileId,
      backups: [pinned], pinnedCount: 1, pinnedSizeBytes: 1024, maximumLabelLength: 64,
      maximumPinnedCount: 20, maximumPinnedSizeBytes: 50 * 1024 ** 3, moreBackupsAvailable: false
    }
    const extraAction = vi.fn()
    render(<BackupBookmarks profileId={profileId} visible loader={async () => bookmarks}
      renderActions={() => <button type="button" onClick={extraAction}>Existing backup action</button>} />)
    const copy = await backupView()
    expect(screen.getByRole('heading', { name: 'Named and pinned backups' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Refresh names and pins' })).toBeVisible()
    const storage = within(screen.getByText('Backup storage').closest('div')!)
    expect(storage.getByText('Unknown')).toBeVisible()
    expect(copy.getByText('Saved results unavailable.')).toBeVisible()
    expect(copy.getByText('Completion details unavailable')).toBeVisible()
    expect(copy.queryByRole('button', { name: 'Existing backup action' })).not.toBeInTheDocument()
    fireEvent.click(copy.getByRole('button', { name: 'Review backup' }))
    expect(copy.getByRole('button', { name: 'Existing backup action' })).toBeVisible()
    expect(extraAction).not.toHaveBeenCalled()
    fireEvent.click(copy.getByRole('button', { name: 'Existing backup action' }))
    expect(extraAction).toHaveBeenCalledOnce()
  })

  it('preserves the mounted name draft and in-flight save when either disclosure closes', async () => {
    let finish!: (value: BackupBookmarkResult) => void
    let signal: AbortSignal | undefined
    const updater = vi.fn((_profile: string, _backup: string, _change: BackupBookmarkChange, current?: AbortSignal) => {
      signal = current
      return new Promise<BackupBookmarkResult>(resolve => { finish = resolve })
    })
    render(<BackupCatalog profileId={profileId} visible loader={loader} updater={updater} />)
    const copy = await backupView()
    const nameToggle = copy.getByText('Name and pin backup')
    const namePanel = nameToggle.closest('details')!
    fireEvent.click(nameToggle)
    expect(namePanel).toHaveAttribute('open')
    const nameInput = copy.getByRole('textbox', { name: 'Backup name' })
    fireEvent.change(nameInput, { target: { value: 'Keep this draft' } })
    fireEvent.click(nameToggle)
    expect(namePanel).not.toHaveAttribute('open')
    expect(nameInput).toHaveValue('Keep this draft')
    const reviewToggle = copy.getByRole('button', { name: 'Review backup' })
    fireEvent.click(reviewToggle)
    fireEvent.click(reviewToggle)
    fireEvent.click(nameToggle)
    expect(copy.getByRole('textbox', { name: 'Backup name' })).toBe(nameInput)
    fireEvent.click(copy.getByRole('button', { name: 'Save name and pin' }))
    expect(updater).toHaveBeenCalledOnce()
    fireEvent.click(nameToggle)
    fireEvent.click(reviewToggle)
    fireEvent.click(reviewToggle)
    expect(signal?.aborted).toBe(false)
    fireEvent.click(nameToggle)
    expect(nameInput).toHaveValue('Keep this draft')
    expect(nameInput).toBeDisabled()
    await act(async () => { finish({ ok: true, code: 'BackupBookmarkUpdated', message: 'Saved.', profileId,
      backupId: pinned.backupId, backup: { ...pinned, label: 'Keep this draft' } }) })
    expect(await screen.findByText('Saved. The app will keep this backup when deleting older copies.')).toBeVisible()
  })

  it('shows pending protection work outside a closed review and keeps competing actions disabled', async () => {
    let finish!: () => void
    const verify = vi.fn(() => new Promise<void>(resolve => { finish = resolve }))
    const vault = vi.fn()
    render(<BackupCatalog profileId={profileId} visible loader={loader} onVerify={verify} onVault={vault} />)
    const copy = await backupView()
    const reviewToggle = copy.getByRole('button', { name: 'Review backup' })
    fireEvent.click(reviewToggle)
    fireEvent.click(copy.getByRole('button', { name: 'Verify' }))
    fireEvent.click(reviewToggle)
    expect(reviewToggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getByRole('status')).toHaveTextContent('Verify is in progress for the selected backup.')
    fireEvent.click(reviewToggle)
    expect(copy.getByRole('button', { name: 'Verify…' })).toBeDisabled()
    const competingAction = copy.getByRole('button', { name: 'Copy to vault' })
    expect(competingAction).toBeDisabled()
    fireEvent.click(competingAction)
    expect(verify).toHaveBeenCalledOnce()
    expect(vault).not.toHaveBeenCalled()
    await act(async () => { finish() })
    await waitFor(() => expect(copy.getByRole('button', { name: 'Verify' })).toBeEnabled())
    expect(screen.queryByText('Verify is in progress for the selected backup.')).not.toBeInTheDocument()
  })
})

import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { BackupCatalog, type BackupCatalogLoader } from './BackupCatalog'
import type { BackupCatalogResult, BackupSummary } from './backupCatalogModel'

const profileId = '11111111-1111-4111-8111-111111111111'
const firstId = '22222222-2222-4222-8222-222222222222'
const secondId = '33333333-3333-4333-8333-333333333333'
const row: BackupSummary = { backupId: firstId, profileId, createdUtc: '2026-10-08T12:00:00Z', backupKind: 'Manual', sizeBytes: 1024,
  label: 'Before update', pinned: true, gameKind: 'Valheim', worldId: 'SyntheticWorld', fileCount: 2, setupIncluded: false,
  payloadSha256: 'A'.repeat(64), setupSha256: null, metadataAvailable: true, evidence: [] }
const catalog: BackupCatalogResult = { ok: true, code: 'BackupCatalog', message: 'Completed metadata.', profileId,
  backups: [row, { ...row, backupId: secondId, createdUtc: '2026-10-07T12:00:00Z', backupKind: 'Rolling', label: 'Weekly copy', pinned: false }],
  pinnedCount: 1, pinnedSizeBytes: 1024, maximumLabelLength: 64, maximumPinnedCount: 20, maximumPinnedSizeBytes: 50 * 1024 ** 3,
  moreBackupsAvailable: false, retention: { retentionCount: 2, minimumFreeSpaceBytes: 1024 ** 3, rollingEnabled: false },
  retainedSizeBytes: 2048, availableSpaceBytes: null, evidenceAvailable: true }
const loader: BackupCatalogLoader = async () => catalog

describe('BackupCatalog', () => {
  it('keeps failed checks and unknown integrity visible while review tools are closed', async () => {
    render(<BackupCatalog profileId={profileId} visible loader={async () => ({ ...catalog, backups: [
      { ...row, evidence: [{ backupId: firstId, kind: 'Integrity', outcome: 'Failed', checkedUtc: '2026-10-08T13:00:00Z', code: 'BackupIntegrityFailed' }] },
      catalog.backups[1]
    ] })} />)
    const failed = within((await screen.findByText('Before update')).closest('article')!)
    expect(failed.getByText('Recorded check failed: Local integrity.')).toBeVisible()
    expect(failed.getByRole('button', { name: 'Review backup' })).toHaveAttribute('aria-expanded', 'false')
    const unknown = within(screen.getByText('Weekly copy').closest('article')!)
    expect(unknown.getByText('Integrity not checked.')).toBeVisible()
    expect(unknown.getByRole('button', { name: 'Review backup' })).toHaveAttribute('aria-expanded', 'false')
  })

  it('keeps saved evidence unavailable honest without suggesting an unchecked backup is healthy', async () => {
    render(<BackupCatalog profileId={profileId} visible loader={async () => ({ ...catalog, evidenceAvailable: false })} />)
    const article = (await screen.findByText('Before update')).closest('article')!
    expect(within(article).getByText('Saved evidence unavailable.')).toBeVisible()
    expect(within(article).queryByText(/Recorded integrity passed/)).not.toBeInTheDocument()
  })

  it('keeps name drafts, pending tools and Restore review when a backup panel closes', async () => {
    let fail!: (reason: Error) => void
    const verify = vi.fn(() => new Promise<void>((_resolve, reject) => { fail = reject }))
    render(<BackupCatalog profileId={profileId} visible loader={loader} onVerify={verify} onRestore={vi.fn()} offline currentWorld="SyntheticWorld" currentGame="Valheim" />)
    const article = (await screen.findByText('Before update')).closest('article')!
    const view = within(article)
    fireEvent.change(view.getByRole('textbox', { name: 'Backup name' }), { target: { value: 'Unsaved name' } })
    const toggle = view.getByRole('button', { name: 'Review backup' })
    fireEvent.click(toggle)
    fireEvent.click(view.getByRole('button', { name: 'Review Restore' }))
    const review = screen.getByRole('region', { name: 'Review Restore' })
    fireEvent.click(within(review).getByRole('checkbox'))
    fireEvent.click(view.getByRole('button', { name: 'Verify' }))
    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(within(review).getByRole('checkbox')).toBeChecked()
    expect(view.getByRole('textbox', { name: 'Backup name' })).toHaveValue('Unsaved name')
    await act(async () => { fail(new Error('private internal failure')) })
    expect(screen.getByRole('status')).toHaveTextContent('Verify could not complete')
    expect(screen.queryByText(/private internal failure/)).not.toBeInTheDocument()
    fireEvent.click(toggle)
    expect(view.getByRole('button', { name: 'Verify' })).toBeEnabled()
    expect(view.getByRole('textbox', { name: 'Backup name' })).toHaveValue('Unsaved name')
  })

  it('puts name/pin, measured results and optional action callbacks in the same exact row', async () => {
    const verify = vi.fn()
    const vault = vi.fn()
    const hash = vi.fn()
    render(<BackupCatalog profileId={profileId} visible loader={loader} onVerify={verify} onVault={vault} onHashRehearsal={hash} />)
    const article = (await screen.findByText('Before update')).closest('article')!
    expect(within(article).queryByRole('button', { name: 'Verify' })).not.toBeInTheDocument()
    fireEvent.click(within(article).getByRole('button', { name: 'Review backup' }))
    expect(within(article).getByText('Name and retention')).toBeInTheDocument()
    expect(within(article).getByText('Protection results')).toBeInTheDocument()
    expect(within(article).getByRole('button', { name: 'Copy to vault' })).toBeInTheDocument()
    expect(within(article).getByRole('button', { name: 'Test restore hashes' })).toBeInTheDocument()
    expect(verify).not.toHaveBeenCalled()
    expect(vault).not.toHaveBeenCalled()
    expect(hash).not.toHaveBeenCalled()
    fireEvent.click(within(article).getByRole('button', { name: 'Verify' }))
    await waitFor(() => expect(verify).toHaveBeenCalledWith(row, expect.any(AbortSignal)))
    expect(vault).not.toHaveBeenCalled()
  })

  it('searches/filter rows while preserving selected comparison identities', async () => {
    render(<BackupCatalog profileId={profileId} visible loader={loader} />)
    await screen.findByText('Before update')
    fireEvent.click(screen.getByRole('checkbox', { name: 'Compare Before update' }))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Compare Weekly copy' }))
    expect(screen.getByRole('region', { name: 'Backup comparison' })).toBeInTheDocument()
    fireEvent.change(screen.getByRole('textbox', { name: 'Search backups' }), { target: { value: 'Weekly' } })
    expect(screen.queryByRole('checkbox', { name: 'Compare Before update' })).not.toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Backup comparison' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Filter backups' }))
    fireEvent.change(screen.getByRole('combobox', { name: 'Pins' }), { target: { value: 'pinned' } })
    expect(screen.getByRole('button', { name: 'Filter backups (1 active)' })).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(screen.getByRole('button', { name: 'Filter backups (1 active)' }))
    expect(screen.queryByRole('combobox', { name: 'Pins' })).not.toBeInTheDocument()
    expect(screen.getByText('No backups match these filters.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(screen.getByRole('checkbox', { name: 'Compare Before update' })).toBeChecked()
    fireEvent.click(screen.getByRole('button', { name: 'Clear comparison' }))
    expect(screen.queryByRole('region', { name: 'Backup comparison' })).not.toBeInTheDocument()
  })

  it('keeps integrity/vault/hash/game evidence dated and separate in the protection summary', async () => {
    const measured: BackupCatalogResult = { ...catalog, backups: [{ ...row, evidence: [
      { backupId: firstId, kind: 'Integrity', outcome: 'Failed', checkedUtc: '2026-10-08T13:00:00Z', code: 'BackupIntegrityFailed' },
      { backupId: firstId, kind: 'HashRehearsal', outcome: 'Passed', checkedUtc: '2026-10-08T14:00:00Z', code: 'RestoreRehearsalCompleted' }
    ] }, catalog.backups[1]] }
    render(<BackupCatalog profileId={profileId} visible loader={async () => measured} />)
    const article = (await screen.findByText('Before update')).closest('article')!
    fireEvent.click(within(article).getByRole('button', { name: 'Review backup' }))
    expect(within(article).getByText(`Failed · ${new Date('2026-10-08T13:00:00Z').toLocaleString()}`)).toBeInTheDocument()
    expect(within(article).getByText(`Passed · ${new Date('2026-10-08T14:00:00Z').toLocaleString()}`)).toBeInTheDocument()
    expect(screen.getByLabelText('Protection summary')).toHaveTextContent('0 local integrity, 0 vault transfers, 1 hash restore tests, 0 owner-reported game rehearsals')
    expect(within(article).getByText(/does not establish game save health/)).toBeInTheDocument()
  })

  it('requires an explicit exact Restore review and acknowledgement before the callback', async () => {
    const restore = vi.fn()
    render(<BackupCatalog profileId={profileId} visible loader={loader} offline currentWorld="SyntheticWorld" currentGame="Valheim" onRestore={restore} />)
    await screen.findByText('Before update')
    fireEvent.click(screen.getAllByRole('button', { name: 'Review backup' })[0])
    fireEvent.click(screen.getAllByRole('button', { name: 'Review Restore' })[0])
    const review = screen.getByRole('region', { name: 'Review Restore' })
    expect(review).toHaveTextContent(firstId)
    expect(review).toHaveTextContent('World only')
    expect(review).toHaveTextContent('A new pre-restore checkpoint')
    expect(within(review).getByRole('button', { name: 'Restore reviewed backup' })).toBeDisabled()
    expect(restore).not.toHaveBeenCalled()
    fireEvent.click(within(review).getByRole('checkbox'))
    fireEvent.click(within(review).getByRole('button', { name: 'Restore reviewed backup' }))
    await waitFor(() => expect(restore).toHaveBeenCalledWith(expect.objectContaining({ backup: row, canRestore: true }), expect.any(AbortSignal)))
  })

  it('rechecks current world and Offline props even after acknowledgement', async () => {
    const restore = vi.fn()
    const { rerender } = render(<BackupCatalog profileId={profileId} visible loader={loader} offline currentWorld="SyntheticWorld" currentGame="Valheim" onRestore={restore} />)
    await screen.findByText('Before update')
    fireEvent.click(screen.getAllByRole('button', { name: 'Review backup' })[0])
    fireEvent.click(screen.getAllByRole('button', { name: 'Review Restore' })[0])
    fireEvent.click(within(screen.getByRole('region', { name: 'Review Restore' })).getByRole('checkbox'))
    rerender(<BackupCatalog profileId={profileId} visible loader={loader} offline={false} currentWorld="SyntheticWorld" currentGame="Valheim" onRestore={restore} />)
    expect(screen.getByRole('button', { name: 'Restore reviewed backup' })).toBeDisabled()
    rerender(<BackupCatalog profileId={profileId} visible loader={loader} offline currentWorld="OtherWorld" currentGame="Valheim" onRestore={restore} />)
    expect(within(screen.getByRole('region', { name: 'Review Restore' })).getByRole('checkbox')).not.toBeChecked()
    expect(screen.getByRole('button', { name: 'Restore reviewed backup' })).toBeDisabled()
    expect(restore).not.toHaveBeenCalled()
  })

  it('withholds Restore when backup completion metadata is unknown and explains unavailable controls inline', async () => {
    render(<BackupCatalog profileId={profileId} visible loader={async () => ({ ...catalog, backups: [{ ...row, metadataAvailable: false, worldId: null }] })}
      offline currentWorld="SyntheticWorld" currentGame="Valheim" onRestore={vi.fn()} onVault={vi.fn()} unavailableReasons={{ vault: 'Wait for the current vault transfer.' }} />)
    await screen.findByText('Before update')
    fireEvent.click(screen.getByRole('button', { name: 'Review backup' }))
    expect(screen.getByRole('button', { name: 'Copy to vault' })).toBeDisabled()
    expect(screen.getByText('Wait for the current vault transfer.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Review Restore' }))
    const review = screen.getByRole('region', { name: 'Review Restore' })
    fireEvent.click(within(review).getByRole('checkbox'))
    expect(within(review).getByRole('button', { name: 'Restore reviewed backup' })).toBeDisabled()
  })

  it('uses suggestions only after a deliberate choice and explains projected pin quota before Save', async () => {
    const updater = vi.fn()
    render(<BackupCatalog profileId={profileId} visible loader={loader} updater={updater} />)
    const article = (await screen.findByText('Weekly copy')).closest('article')!
    const view = within(article)
    expect(view.getByRole('textbox', { name: 'Backup name' })).toHaveValue('Weekly copy')
    fireEvent.change(view.getByRole('combobox', { name: 'Suggested name' }), { target: { value: 'After Stop 2026-10-07' } })
    expect(view.getByRole('textbox', { name: 'Backup name' })).toHaveValue('After Stop 2026-10-07')
    fireEvent.click(view.getByRole('checkbox', { name: 'Pin this backup' }))
    expect(view.getByText(/After Save: 2 of 20 pins/)).toBeInTheDocument()
    expect(updater).not.toHaveBeenCalled()
  })

  it('blocks adding a pin at the global quota but allows a name-only change on an existing pin', async () => {
    const updater = vi.fn()
    render(<BackupCatalog profileId={profileId} visible loader={async () => ({ ...catalog, pinnedCount: 20, moreBackupsAvailable: true })} updater={updater} />)
    await screen.findByText('Weekly copy')
    const unpinned = within(screen.getByText('Weekly copy').closest('article')!)
    fireEvent.click(unpinned.getByRole('checkbox', { name: 'Pin this backup' }))
    expect(unpinned.getByRole('button', { name: 'Save name and pin' })).toBeDisabled()
    expect(unpinned.getByText(/Unpin another backup/)).toBeInTheDocument()
    const pinned = within(screen.getByText('Before update').closest('article')!)
    fireEvent.change(pinned.getByRole('textbox', { name: 'Backup name' }), { target: { value: 'New name' } })
    expect(pinned.getByRole('button', { name: 'Save name and pin' })).toBeEnabled()
    expect(updater).not.toHaveBeenCalled()
  })

  it('shows retention preview as an estimate and withholds candidates for a partial catalog', async () => {
    render(<BackupCatalog profileId={profileId} visible loader={async () => ({ ...catalog, moreBackupsAvailable: true })} />)
    await screen.findByText('Before update')
    expect(screen.getByText(/Exact retention candidates are unknown/)).toBeInTheDocument()
    expect(screen.getByText(/Filters and comparisons cover these rows/)).toBeInTheDocument()
    expect(screen.queryByText(/current copies could become eligible/)).not.toBeInTheDocument()
  })

  it('aborts an obsolete action/load and ignores late completion when the profile changes', async () => {
    let finish!: () => void
    let actionSignal: AbortSignal | undefined
    const changed = vi.fn()
    const verify = vi.fn((_backup: BackupSummary, signal?: AbortSignal) => { actionSignal = signal; return new Promise<void>(resolve => { finish = resolve }) })
    const { rerender } = render(<BackupCatalog profileId={profileId} visible loader={loader} onVerify={verify} onChanged={changed} />)
    await screen.findByText('Before update')
    fireEvent.click(screen.getAllByRole('button', { name: 'Review backup' })[0])
    fireEvent.click(screen.getAllByRole('button', { name: 'Verify' })[0])
    expect(actionSignal?.aborted).toBe(false)
    rerender(<BackupCatalog profileId="44444444-4444-4444-8444-444444444444" visible={false} loader={loader} onVerify={verify} onChanged={changed} />)
    expect(actionSignal?.aborted).toBe(true)
    await act(async () => { finish() })
    expect(changed).not.toHaveBeenCalled()
    expect(screen.queryByText('Before update')).not.toBeInTheDocument()
  })

  it('keeps failures generic and loading distinct from an empty catalog without activating anything', async () => {
    const verify = vi.fn()
    const { rerender } = render(<BackupCatalog profileId={profileId} visible loader={async () => { throw new Error('C:\\private password=secret') }} onVerify={verify} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Backups unavailable')
    expect(screen.queryByText(/password=secret/)).not.toBeInTheDocument()
    rerender(<BackupCatalog profileId={profileId} visible loader={async () => ({ ...catalog, backups: [], pinnedCount: 0, pinnedSizeBytes: 0, retainedSizeBytes: 0 })} onVerify={verify} />)
    expect(await screen.findByText(/No completed backups yet/)).toBeInTheDocument()
    expect(verify).not.toHaveBeenCalled()
  })
})

import { describe, expect, it } from 'vitest'
import { validBackupLabel } from './backupBookmarksWire'
import { backupBytes, bookmarksAsCatalog, compareBackups, emptyBackupFilters, filterBackups, latestBackupEvidence,
  pinPreview, protectionSummary, restoreReview, retentionPreview, suggestedBackupNames,
  type BackupCatalogResult, type BackupSummary } from './backupCatalogModel'
import { parseBackupCatalog } from './backupCatalogWire'

const profileId = '11111111-1111-4111-8111-111111111111'
const firstId = '22222222-2222-4222-8222-222222222222'
const secondId = '33333333-3333-4333-8333-333333333333'
const row: BackupSummary = { backupId: firstId, profileId, createdUtc: '2026-10-08T12:00:00Z', backupKind: 'Manual', sizeBytes: 1024,
  label: 'Before update', pinned: true, gameKind: 'Valheim', worldId: 'SyntheticWorld', fileCount: 2, setupIncluded: false,
  payloadSha256: 'A'.repeat(64), setupSha256: null, metadataAvailable: true, evidence: [] }
const second: BackupSummary = { ...row, backupId: secondId, createdUtc: '2026-10-07T12:00:00Z', backupKind: 'Rolling', label: 'Weekly copy', pinned: false }
const catalog: BackupCatalogResult = { ok: true, code: 'BackupCatalog', message: 'Completed metadata.', profileId,
  backups: [row, second], pinnedCount: 1, pinnedSizeBytes: 1024, maximumLabelLength: 64,
  maximumPinnedCount: 20, maximumPinnedSizeBytes: 50 * 1024 ** 3, moreBackupsAvailable: false,
  retention: { retentionCount: 2, minimumFreeSpaceBytes: 1024 ** 3, rollingEnabled: false },
  retainedSizeBytes: 2048, availableSpaceBytes: null, evidenceAvailable: true }

describe('backup catalog projections', () => {
  it('filters names, pins, kinds and inclusive local dates together without mutating the catalog', () => {
    expect(filterBackups(catalog.backups, { ...emptyBackupFilters, query: 'UPDATE', pin: 'pinned', kind: 'Manual',
      after: '2026-10-08', before: '2026-10-08' })).toEqual([row])
    expect(filterBackups(catalog.backups, { ...emptyBackupFilters, query: 'after stop', pin: 'unpinned' })).toEqual([second])
    expect(filterBackups(catalog.backups, { ...emptyBackupFilters, query: 'pinned' })).toEqual([row])
    expect(filterBackups(catalog.backups, { ...emptyBackupFilters, query: 'unpinned' })).toEqual([second])
    expect(filterBackups(catalog.backups, { ...emptyBackupFilters, after: '2026-10-09', before: '2026-10-01' })).toEqual([])
    expect(catalog.backups).toEqual([row, second])
  })

  it('reserves a slot for the next ordinary copy and keeps pins outside the unpinned retention count', () => {
    const third = { ...second, backupId: '44444444-4444-4444-8444-444444444444', createdUtc: '2026-10-06T12:00:00Z' }
    const preview = retentionPreview({ ...catalog, backups: [row, second, third] })
    expect(preview.known).toBe(true)
    expect(preview.candidates).toEqual([third])
    expect(preview.message).toContain('Restore also protects its selected source')
    expect(retentionPreview({ ...catalog, backups: [row, second, third] }, 1).candidates).toEqual([second, third])
    expect(retentionPreview(catalog, 50).candidates).toEqual([])
  })

  it('withholds exact retention candidates for unknown policy, partial metadata, tied dates and partial lists', () => {
    for (const result of [
      { ...catalog, retention: null }, { ...catalog, moreBackupsAvailable: true },
      { ...catalog, backups: [{ ...row, metadataAvailable: false }] },
      { ...catalog, backups: [second, { ...second, backupId: firstId }] }
    ]) expect(retentionPreview(result)).toMatchObject({ known: false, candidates: [] })
    for (const count of [0, 51, Number.NaN, 1.5]) expect(retentionPreview(catalog, count).known).toBe(false)
  })

  it('previews both independent pin limits and permits renaming an existing pin at capacity', () => {
    expect(pinPreview(second, { ...catalog, pinnedCount: 20 }, true).blocked).toBe(true)
    expect(pinPreview(second, { ...catalog, pinnedSizeBytes: 50 * 1024 ** 3 }, true).blocked).toBe(true)
    expect(pinPreview(second, { ...catalog, pinnedSizeBytes: 50 * 1024 ** 3 - second.sizeBytes }, true).blocked).toBe(false)
    expect(pinPreview(row, { ...catalog, pinnedCount: 20, pinnedSizeBytes: 50 * 1024 ** 3 }, true).blocked).toBe(false)
    expect(pinPreview(row, catalog, false)).toMatchObject({ count: 0, bytes: 0, blocked: false })
  })

  it('suggests bounded names using known purpose and date without changing stored names', () => {
    for (const backup of [row, second, { ...row, backupKind: 'PreRestore' as const }]) {
      const suggestions = suggestedBackupNames(backup)
      expect(suggestions).toHaveLength(3)
      expect(suggestions.every(validBackupLabel)).toBe(true)
      expect(suggestions[0]).toContain('2026-10-')
    }
    expect(row.label).toBe('Before update')
    expect(backupBytes(null)).toBe('Unknown')
    expect(backupBytes(50 * 1024 ** 3)).toBe('50 GiB')
  })

  it('compares recorded hashes and setup identities, keeping missing evidence unknown', () => {
    expect(compareBackups(row, second)).toMatchObject({ payload: 'Same', world: 'Same', setup: 'World only in both', sizeChange: 0 })
    expect(compareBackups(row, { ...second, worldId: 'AnotherWorld', payloadSha256: 'B'.repeat(64), sizeBytes: 512 })).toMatchObject({
      world: 'Different', payload: 'Different', sizeChange: -512 })
    expect(compareBackups(row, { ...second, payloadSha256: null, setupIncluded: null, setupSha256: null, fileCount: null })).toMatchObject({
      payload: 'Unknown', setup: 'Unknown', filesChange: null })
    expect(compareBackups({ ...row, setupIncluded: true, setupSha256: 'C'.repeat(64) },
      { ...second, setupIncluded: true, setupSha256: 'D'.repeat(64) }).setup).toBe('Different')
  })

  it('requires definite Offline plus exact game/world identity for Restore review', () => {
    expect(restoreReview(row, 'SyntheticWorld', 'Valheim', true).canRestore).toBe(true)
    for (const review of [restoreReview(row, 'SyntheticWorld', 'Valheim', false),
      restoreReview(row, 'AnotherWorld', 'Valheim', true), restoreReview(row, 'SyntheticWorld', 'Terraria', true),
      restoreReview(row, null, 'Valheim', true), restoreReview({ ...row, metadataAvailable: false }, 'SyntheticWorld', 'Valheim', true)]) {
      expect(review.canRestore).toBe(false)
      expect(review.checkpoint).toContain('If it fails, Restore does not begin')
    }
  })

  it('uses only the latest exact-backup stage and does not turn a hash pass into another kind of protection', () => {
    const evidence: BackupSummary['evidence'] = [
      { backupId: firstId, kind: 'Integrity', outcome: 'Passed', checkedUtc: '2026-10-08T12:00:00Z', code: 'BackupVerified' },
      { backupId: firstId, kind: 'Integrity', outcome: 'Failed', checkedUtc: '2026-10-08T13:00:00Z', code: 'BackupIntegrityFailed' },
      { backupId: firstId, kind: 'HashRehearsal', outcome: 'Passed', checkedUtc: '2026-10-08T14:00:00Z', code: 'RestoreRehearsalCompleted' },
      { backupId: secondId, kind: 'Vault', outcome: 'Passed', checkedUtc: '2026-10-08T14:00:00Z', code: 'VaultCopyVerified' }
    ]
    expect(latestBackupEvidence({ ...row, evidence }, 'Integrity')?.outcome).toBe('Failed')
    expect(protectionSummary({ ...catalog, backups: [{ ...row, evidence }, second] })).toMatchObject({
      integrity: 0, vault: 0, hashRehearsal: 1, ownerGame: 0 })
  })

  it('marks legacy bookmark metadata and evidence unavailable instead of synthesizing protection', () => {
    const legacy = bookmarksAsCatalog({ ...catalog, backups: [row] })
    expect(legacy.backups[0]).toMatchObject({ metadataAvailable: false, payloadSha256: null, worldId: null, evidence: [] })
    expect(legacy.retention).toBeNull()
    expect(protectionSummary(legacy).incomplete).toBe(true)
    expect(restoreReview(legacy.backups[0], 'SyntheticWorld', 'Valheim', true).canRestore).toBe(false)
  })
})

describe('backup catalog response boundary', () => {
  it('decodes bounded catalog summaries and measured results without exposing manifest paths', () => {
    expect(parseBackupCatalog(catalog)).toEqual(catalog)
    const measured = { ...catalog, backups: [{ ...row, evidence: [{ backupId: firstId, kind: 'Integrity', outcome: 'Failed',
      checkedUtc: '2026-10-08T12:00:00Z', code: 'BackupIntegrityFailed' }] }, second] }
    expect(parseBackupCatalog(measured).backups[0].evidence[0].outcome).toBe('Failed')
  })

  it('rejects identity swaps, unknown fields, missing metadata, unsafe world text and unbounded evidence', () => {
    const fact = { backupId: firstId, kind: 'Integrity', outcome: 'Passed', checkedUtc: '2026-10-08T12:00:00Z', code: 'BackupVerified' }
    for (const value of [
      { ...catalog, rawWorldPath: 'C:\\private' },
      { ...catalog, backups: [{ ...row, payloadSha256: null }, second] },
      { ...catalog, backups: [{ ...row, worldId: 'hidden\u202Eworld' }, second] },
      { ...catalog, backups: [{ ...row, worldId: 'C:\\private\\world' }, second] },
      { ...catalog, backups: [{ ...row, fileCount: -1 }, second] },
      { ...catalog, backups: [{ ...row, setupIncluded: true, setupSha256: null }, second] },
      { ...catalog, backups: [{ ...row, evidence: [{ ...fact, backupId: secondId }] }, second] },
      { ...catalog, backups: [{ ...row, evidence: [fact, fact] }, second] },
      { ...catalog, backups: [{ ...row, evidence: [{ ...fact, kind: 'GameReady' }] }, second] },
      { ...catalog, backups: [{ ...row, evidence: [{ ...fact, checkedUtc: 'yesterday' }] }, second] },
      { ...catalog, backups: [{ ...row, evidence: [{ ...fact, path: 'C:\\private' }] }, second] },
      { ...catalog, retention: { ...catalog.retention, retentionCount: 51 } },
      { ...catalog, retainedSizeBytes: 1 }, { ...catalog, availableSpaceBytes: Number.POSITIVE_INFINITY }
    ]) expect(() => parseBackupCatalog(value)).toThrow()
  })
})

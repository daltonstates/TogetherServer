import type { BackupBookmark, BackupBookmarksResult } from './backupBookmarksWire'

export type BackupEvidenceKind = 'Integrity' | 'Vault' | 'HashRehearsal' | 'OwnerGameRehearsal'
export type BackupEvidence = {
  backupId: string
  kind: BackupEvidenceKind
  outcome: 'Passed' | 'Failed' | 'Incomplete'
  checkedUtc: string
  code: string
}
export type BackupSummary = BackupBookmark & {
  gameKind: string | null
  worldId: string | null
  fileCount: number | null
  setupIncluded: boolean | null
  payloadSha256: string | null
  setupSha256: string | null
  metadataAvailable: boolean
  evidence: BackupEvidence[]
}
export type BackupRetentionPolicy = { retentionCount: number; minimumFreeSpaceBytes: number; rollingEnabled: boolean }
export type BackupCatalogResult = Omit<BackupBookmarksResult, 'backups'> & {
  backups: BackupSummary[]
  retention: BackupRetentionPolicy | null
  retainedSizeBytes: number | null
  availableSpaceBytes: number | null
  evidenceAvailable: boolean
}
export type BackupFilters = { query: string; pin: 'all' | 'pinned' | 'unpinned'; kind: string; after: string; before: string }
export const emptyBackupFilters: BackupFilters = { query: '', pin: 'all', kind: 'all', after: '', before: '' }

export function bookmarksAsCatalog(result: BackupBookmarksResult): BackupCatalogResult {
  return { ...result, backups: result.backups.map(backup => ({ ...backup, gameKind: null, worldId: null,
    fileCount: null, setupIncluded: null, payloadSha256: null, setupSha256: null, metadataAvailable: false, evidence: [] })),
    retention: null, retainedSizeBytes: null, availableSpaceBytes: null, evidenceAvailable: false }
}

export function backupKindName(kind: string) {
  return ({ Rolling: 'After Stop', Manual: 'Manual', PreRestore: 'Before restore' } as Record<string, string>)[kind] ?? 'Kind unknown'
}
export function backupBytes(value: number | null) {
  if (value === null || !Number.isSafeInteger(value) || value < 0) return 'Unknown'
  if (value < 1024) return `${value} B`
  const unit = value >= 1024 ** 3 ? 'GiB' : value >= 1024 ** 2 ? 'MiB' : 'KiB'
  const divisor = unit === 'GiB' ? 1024 ** 3 : unit === 'MiB' ? 1024 ** 2 : 1024
  return `${(value / divisor).toLocaleString(undefined, { maximumFractionDigits: 2 })} ${unit}`
}
export function filterBackups(backups: BackupSummary[], filters: BackupFilters) {
  const query = filters.query.trim().toLowerCase()
  const after = filters.after ? new Date(`${filters.after}T00:00:00`).getTime() : null
  const before = filters.before ? new Date(`${filters.before}T00:00:00`).getTime() : null
  return backups.filter(backup => {
    const date = new Date(backup.createdUtc)
    const localDay = new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime()
    return (filters.pin === 'all' || backup.pinned === (filters.pin === 'pinned')) &&
      (filters.kind === 'all' || backup.backupKind === filters.kind) &&
      (after === null || Number.isFinite(after) && localDay >= after) &&
      (before === null || Number.isFinite(before) && localDay <= before) &&
      (!query || (query === 'pinned' ? backup.pinned : query === 'unpinned' ? !backup.pinned :
        [backup.label, backup.backupKind, backupKindName(backup.backupKind), date.toLocaleDateString(), backup.createdUtc]
          .some(value => value.toLowerCase().includes(query))))
  }).sort((left, right) => Number(right.pinned) - Number(left.pinned) || Date.parse(right.createdUtc) - Date.parse(left.createdUtc))
}

export function suggestedBackupNames(backup: BackupBookmark) {
  const date = new Date(backup.createdUtc)
  const day = Number.isFinite(date.getTime()) ? date.toISOString().slice(0, 10) : 'Saved copy'
  const purpose = backup.backupKind === 'PreRestore' ? 'Before restore' : backup.backupKind === 'Rolling' ? 'After Stop' : 'Manual checkpoint'
  return [`${purpose} ${day}`, `Before game update ${day}`, `Before setup changes ${day}`]
}

export function pinPreview(backup: BackupBookmark, result: Pick<BackupBookmarksResult, 'pinnedCount' | 'pinnedSizeBytes' | 'maximumPinnedCount' | 'maximumPinnedSizeBytes'>, pinned: boolean) {
  const count = result.pinnedCount + Number(pinned) - Number(backup.pinned)
  const bytes = result.pinnedSizeBytes + (Number(pinned) - Number(backup.pinned)) * backup.sizeBytes
  const blocked = pinned && !backup.pinned && (count > result.maximumPinnedCount || bytes > result.maximumPinnedSizeBytes || !Number.isSafeInteger(bytes))
  return { count, bytes, blocked, message: blocked ? 'Unpin another backup before adding this pin. The Host checks capacity again when saving.' :
    `After Save: ${count} of ${result.maximumPinnedCount} pins, ${backupBytes(bytes)} of ${backupBytes(result.maximumPinnedSizeBytes)}.` }
}

export function retentionPreview(result: BackupCatalogResult, count = result.retention?.retentionCount) {
  if (!result.retention || count === undefined || !Number.isInteger(count) || count < 1 || count > 50 || result.moreBackupsAvailable ||
      result.backups.some(backup => !backup.metadataAvailable) ||
      new Set(result.backups.filter(backup => !backup.pinned).map(backup => Date.parse(backup.createdUtc))).size !== result.backups.filter(backup => !backup.pinned).length) {
    return { known: false, candidates: [] as BackupSummary[], message: 'Exact retention candidates are unknown until the complete catalog and saved policy are available.' }
  }
  // Create always protects its new copy, so it consumes one of the unpinned slots.
  // Pre-restore also protects the selected source. No row is promised to be deleted.
  const unpinned = result.backups.filter(backup => !backup.pinned).sort((a, b) => Date.parse(b.createdUtc) - Date.parse(a.createdUtc))
  const candidates = unpinned.slice(Math.max(0, count - 1))
  return { known: true, candidates, message: `The next ordinary completed backup keeps its new copy and ${Math.max(0, count - 1)} newest unpinned copies, plus every pin. ` +
    'Older unpinned copies may be removed then. Restore also protects its selected source; space or copy failures can prevent a new backup. Changing this preview removes nothing.' }
}

export function latestBackupEvidence(backup: BackupSummary, kind: BackupEvidenceKind) {
  return backup.evidence.filter(item => item.backupId.toLowerCase() === backup.backupId.toLowerCase() && item.kind === kind)
    .sort((a, b) => Date.parse(b.checkedUtc) - Date.parse(a.checkedUtc))[0] ?? null
}

export function compareBackups(left: BackupSummary, right: BackupSummary) {
  const same = (a: string | null, b: string | null) => a === null || b === null ? 'Unknown' : a === b ? 'Same' : 'Different'
  const payload = same(left.payloadSha256, right.payloadSha256)
  const setup = left.setupIncluded === false && right.setupIncluded === false ? 'World only in both' : same(left.setupSha256, right.setupSha256)
  return { world: same(left.worldId, right.worldId), game: same(left.gameKind, right.gameKind), payload, setup,
    sizeChange: right.sizeBytes - left.sizeBytes, filesChange: left.fileCount === null || right.fileCount === null ? null : right.fileCount - left.fileCount }
}

export type BackupRestoreReview = { backup: BackupSummary; currentWorld: string | null; canRestore: boolean; reason: string; checkpoint: string }
export function restoreReview(backup: BackupSummary, currentWorld: string | null, currentGame: string | null, offline: boolean): BackupRestoreReview {
  const sameWorld = backup.worldId !== null && currentWorld !== null && backup.worldId === currentWorld
  const sameGame = backup.gameKind !== null && currentGame !== null && backup.gameKind === currentGame
  const canRestore = offline && backup.metadataAvailable && sameWorld && sameGame
  return { backup, currentWorld, canRestore, reason: !offline ? 'Stop or resolve the server before Restore.' :
    !backup.metadataAvailable || backup.worldId === null || currentWorld === null || backup.gameKind === null || currentGame === null ?
      'World and game identity must be available before Restore.' : !sameWorld || !sameGame ? 'This backup belongs to another game or world.' :
        'The Host rechecks Offline state, exact process identity and the selected backup before changing the world.',
    checkpoint: 'A new pre-restore checkpoint of the current world must succeed before replacement. If it fails, Restore does not begin.' }
}

export function protectionSummary(result: BackupCatalogResult) {
  const newest = [...result.backups].sort((a, b) => Date.parse(b.createdUtc) - Date.parse(a.createdUtc))[0] ?? null
  const passed = (kind: BackupEvidenceKind) => result.backups.filter(backup => latestBackupEvidence(backup, kind)?.outcome === 'Passed').length
  return { newest, integrity: passed('Integrity'), vault: passed('Vault'), hashRehearsal: passed('HashRehearsal'), ownerGame: passed('OwnerGameRehearsal'),
    incomplete: result.moreBackupsAvailable || !result.evidenceAvailable || result.backups.some(backup => !backup.metadataAvailable ||
      (['Integrity', 'Vault', 'HashRehearsal', 'OwnerGameRehearsal'] as BackupEvidenceKind[]).some(kind => !latestBackupEvidence(backup, kind))) }
}

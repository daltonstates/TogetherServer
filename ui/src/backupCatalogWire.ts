import { ContractError, type Decoder } from './contracts'
import { parseBackupBookmarks } from './backupBookmarksWire'
import type { BackupCatalogResult, BackupEvidence, BackupSummary } from './backupCatalogModel'

const summaryKeys = ['backupId', 'profileId', 'createdUtc', 'backupKind', 'sizeBytes', 'label', 'pinned',
  'gameKind', 'worldId', 'fileCount', 'setupIncluded', 'payloadSha256', 'setupSha256', 'metadataAvailable', 'evidence']
const resultKeys = ['ok', 'code', 'message', 'profileId', 'backups', 'pinnedCount', 'pinnedSizeBytes', 'maximumLabelLength',
  'maximumPinnedCount', 'maximumPinnedSizeBytes', 'moreBackupsAvailable', 'retention', 'retainedSizeBytes', 'availableSpaceBytes', 'evidenceAvailable']
function fail(context: string): never { throw new ContractError(`${context}: invalid backup catalog response`) }
function record(value: unknown, context: string, keys: string[]): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return fail(context)
  const source = value as Record<string, unknown>
  if (Object.keys(source).some(key => !keys.includes(key)) || keys.some(key => !(key in source))) return fail(context)
  return source
}
function flag(value: unknown, context: string) { if (typeof value !== 'boolean') return fail(context); return value }
function text(value: unknown, maximum: number, context: string) {
  if (typeof value !== 'string' || value.length > maximum || /[\p{Cc}\p{Cf}]/u.test(value)) return fail(context)
  return value
}
function number(value: unknown, context: string, maximum = Number.MAX_SAFE_INTEGER) {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0 || value > maximum) return fail(context)
  return value
}
function nullableNumber(value: unknown, context: string) { return value === null ? null : number(value, context) }
function hash(value: unknown, context: string) {
  if (value === null) return null
  if (typeof value !== 'string' || !/^[a-f0-9]{64}$/iu.test(value)) return fail(context)
  return value.toUpperCase()
}
function evidence(value: unknown, backupId: string, context: string): BackupEvidence {
  const source = record(value, context, ['backupId', 'kind', 'outcome', 'checkedUtc', 'code'])
  if (typeof source.backupId !== 'string' || source.backupId.toLowerCase() !== backupId.toLowerCase() ||
      !['Integrity', 'Vault', 'HashRehearsal', 'OwnerGameRehearsal'].includes(String(source.kind)) ||
      !['Passed', 'Failed', 'Incomplete'].includes(String(source.outcome))) return fail(context)
  const checkedUtc = text(source.checkedUtc, 64, context)
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/u.test(checkedUtc) || !Number.isFinite(Date.parse(checkedUtc))) return fail(context)
  const code = text(source.code, 80, context)
  if (!/^[A-Za-z][A-Za-z0-9]{0,79}$/u.test(code)) return fail(context)
  return { backupId, kind: source.kind as BackupEvidence['kind'], outcome: source.outcome as BackupEvidence['outcome'], checkedUtc, code }
}

export const parseBackupCatalog: Decoder<BackupCatalogResult> = (value, context = 'backup catalog') => {
  const source = record(value, context, resultKeys)
  if (!Array.isArray(source.backups) || source.backups.length > 100) return fail(context)
  const rows = source.backups.map(item => record(item, context, summaryKeys))
  const base = parseBackupBookmarks({ ok: source.ok, code: source.code, message: source.message, profileId: source.profileId,
    backups: rows.map(row => ({ backupId: row.backupId, profileId: row.profileId, createdUtc: row.createdUtc,
      backupKind: row.backupKind, sizeBytes: row.sizeBytes, label: row.label, pinned: row.pinned })),
    pinnedCount: source.pinnedCount, pinnedSizeBytes: source.pinnedSizeBytes, maximumLabelLength: source.maximumLabelLength,
    maximumPinnedCount: source.maximumPinnedCount, maximumPinnedSizeBytes: source.maximumPinnedSizeBytes,
    moreBackupsAvailable: source.moreBackupsAvailable }, context)
  const backups: BackupSummary[] = rows.map((row, index) => {
    const metadataAvailable = flag(row.metadataAvailable, context)
    const gameKind = row.gameKind === null ? null : text(row.gameKind, 40, context)
    if (gameKind !== null && !['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria', 'Fixture'].includes(gameKind)) return fail(context)
    const worldId = row.worldId === null ? null : text(row.worldId, 128, context)
    if (worldId !== null && /[\\/:]/u.test(worldId)) return fail(context)
    const fileCount = nullableNumber(row.fileCount, context)
    const setupIncluded = row.setupIncluded === null ? null : flag(row.setupIncluded, context)
    const payloadSha256 = hash(row.payloadSha256, context)
    const setupSha256 = hash(row.setupSha256, context)
    if (metadataAvailable && (!gameKind || !worldId || fileCount === null || setupIncluded === null || !payloadSha256 || setupIncluded !== (setupSha256 !== null)) ||
        !metadataAvailable && [fileCount, setupIncluded, payloadSha256, setupSha256].some(item => item !== null)) return fail(context)
    if (!Array.isArray(row.evidence) || row.evidence.length > 4) return fail(context)
    const facts = row.evidence.map(item => evidence(item, base.backups[index].backupId, context))
    if (new Set(facts.map(item => item.kind)).size !== facts.length || !metadataAvailable && facts.length > 0) return fail(context)
    return { ...base.backups[index], gameKind, worldId, fileCount, setupIncluded, payloadSha256, setupSha256, metadataAvailable, evidence: facts }
  })
  let retention: BackupCatalogResult['retention'] = null
  if (source.retention !== null) {
    const policy = record(source.retention, context, ['retentionCount', 'minimumFreeSpaceBytes', 'rollingEnabled'])
    const retentionCount = number(policy.retentionCount, context, 50)
    if (retentionCount < 1) return fail(context)
    retention = { retentionCount, minimumFreeSpaceBytes: number(policy.minimumFreeSpaceBytes, context), rollingEnabled: flag(policy.rollingEnabled, context) }
  }
  const retainedSizeBytes = nullableNumber(source.retainedSizeBytes, context)
  const availableSpaceBytes = nullableNumber(source.availableSpaceBytes, context)
  const shownBytes = backups.reduce((sum, backup) => sum + backup.sizeBytes, 0)
  if (!Number.isSafeInteger(shownBytes) || retainedSizeBytes !== null && (retainedSizeBytes < shownBytes || !base.moreBackupsAvailable && retainedSizeBytes !== shownBytes)) return fail(context)
  const evidenceAvailable = flag(source.evidenceAvailable, context)
  if (!evidenceAvailable && backups.some(backup => backup.evidence.length > 0)) return fail(context)
  return { ...base, backups, retention, retainedSizeBytes, availableSpaceBytes, evidenceAvailable }
}

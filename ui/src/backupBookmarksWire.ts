import { ContractError, type Decoder } from './contracts'

export type BackupBookmark = {
  backupId: string
  profileId: string
  createdUtc: string
  backupKind: 'Rolling' | 'Manual' | 'PreRestore' | 'Unavailable'
  sizeBytes: number
  label: string
  pinned: boolean
}

export type BackupBookmarksResult = {
  ok: boolean
  code: string
  message: string
  profileId: string
  backups: BackupBookmark[]
  pinnedCount: number
  pinnedSizeBytes: number
  maximumLabelLength: number
  maximumPinnedCount: number
  maximumPinnedSizeBytes: number
  moreBackupsAvailable: boolean
}

export type BackupBookmarkResult = {
  ok: boolean
  code: string
  message: string
  profileId: string
  backupId: string
  backup: BackupBookmark | null
}

export type BackupBookmarkChange = { label: string; pinned: boolean }

function fail(context: string): never { throw new ContractError(`${context}: invalid backup bookmark response`) }
function object(value: unknown, context: string, keys: readonly string[]) {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return fail(context)
  const result = value as Record<string, unknown>
  if (Object.keys(result).some(key => !keys.includes(key))) return fail(context)
  return result
}
function text(value: unknown, context: string, maximum = 400) {
  if (typeof value !== 'string' || value.length > maximum || /[\p{Cc}\u202a-\u202e\u2066-\u2069]/u.test(value)) return fail(context)
  return value
}
function integer(value: unknown, context: string, maximum = Number.MAX_SAFE_INTEGER) {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0 || value > maximum) return fail(context)
  return value
}
function boolean(value: unknown, context: string) {
  if (typeof value !== 'boolean') return fail(context)
  return value
}
function id(value: unknown, context: string) {
  const result = text(value, context, 36)
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(result) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/u.test(result)) return fail(context)
  return result
}
function timestamp(value: unknown, context: string) {
  const result = text(value, context, 64)
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/u.test(result) || !Number.isFinite(Date.parse(result))) return fail(context)
  return result
}
export function validBackupLabel(label: string) {
  return label.length <= 64 && /^[A-Za-z0-9 .,'()_!-]*$/u.test(label) && (label.trim() === '' || /[A-Za-z0-9]/u.test(label))
}
function bookmark(value: unknown, context: string): BackupBookmark {
  const source = object(value, context, ['backupId', 'profileId', 'createdUtc', 'backupKind', 'sizeBytes', 'label', 'pinned'])
  const kind = text(source.backupKind, context, 20)
  if (!['Rolling', 'Manual', 'PreRestore', 'Unavailable'].includes(kind)) return fail(context)
  const label = text(source.label, context, 64)
  if (!validBackupLabel(label)) return fail(context)
  return {
    backupId: id(source.backupId, context), profileId: id(source.profileId, context),
    createdUtc: timestamp(source.createdUtc, context), backupKind: kind as BackupBookmark['backupKind'],
    sizeBytes: integer(source.sizeBytes, context), label, pinned: boolean(source.pinned, context)
  }
}
function common(source: Record<string, unknown>, context: string) {
  const code = text(source.code, context, 80)
  if (!/^[A-Za-z][A-Za-z0-9]{0,79}$/u.test(code)) return fail(context)
  return { ok: boolean(source.ok, context), code, message: text(source.message, context), profileId: id(source.profileId, context) }
}

export const parseBackupBookmarks: Decoder<BackupBookmarksResult> = (value, context = 'backup bookmarks') => {
  const source = object(value, context, ['ok', 'code', 'message', 'profileId', 'backups', 'pinnedCount', 'pinnedSizeBytes',
    'maximumLabelLength', 'maximumPinnedCount', 'maximumPinnedSizeBytes', 'moreBackupsAvailable'])
  const result = common(source, context)
  if (!Array.isArray(source.backups) || source.backups.length > 100 || source.maximumLabelLength !== 64 ||
      source.maximumPinnedCount !== 20 || source.maximumPinnedSizeBytes !== 50 * 1024 ** 3) return fail(context)
  const backups = source.backups.map(item => bookmark(item, context))
  if (backups.some(item => item.profileId.toLowerCase() !== result.profileId.toLowerCase()) ||
      new Set(backups.map(item => item.backupId.toLowerCase())).size !== backups.length) return fail(context)
  const pinnedCount = integer(source.pinnedCount, context)
  const pinnedSizeBytes = integer(source.pinnedSizeBytes, context)
  const moreBackupsAvailable = boolean(source.moreBackupsAvailable, context)
  const pinned = backups.filter(item => item.pinned)
  const shownBytes = pinned.reduce((total, item) => total + item.sizeBytes, 0)
  if (!Number.isSafeInteger(shownBytes) || pinnedCount < pinned.length || pinnedSizeBytes < shownBytes ||
      (!moreBackupsAvailable && (pinnedCount !== pinned.length || pinnedSizeBytes !== shownBytes)) ||
      (!result.ok && backups.length !== 0)) return fail(context)
  return { ...result, backups, pinnedCount, pinnedSizeBytes, maximumLabelLength: 64, maximumPinnedCount: 20,
    maximumPinnedSizeBytes: 50 * 1024 ** 3, moreBackupsAvailable }
}

export const parseBackupBookmarkResult: Decoder<BackupBookmarkResult> = (value, context = 'backup bookmark') => {
  const source = object(value, context, ['ok', 'code', 'message', 'profileId', 'backupId', 'backup'])
  const result = common(source, context)
  const backupId = id(source.backupId, context)
  const backup = source.backup === null ? null : bookmark(source.backup, context)
  if (result.ok !== (backup !== null) || backup && (backup.profileId.toLowerCase() !== result.profileId.toLowerCase() ||
      backup.backupId.toLowerCase() !== backupId.toLowerCase())) return fail(context)
  return { ...result, backupId, backup }
}

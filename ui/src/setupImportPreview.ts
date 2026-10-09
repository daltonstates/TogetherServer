import { ContractError, parseBasicResult, type BasicResult, type Decoder } from './contracts'

export type SetupSourceFile = { name: string; bytes: number | null; modifiedUtc: string | null }
export type SetupImportPreview = { selectionId: string; profileId: string; kind: 'Factorio' | 'Terraria'; worldId: string;
  sourceFiles: SetupSourceFile[]; totalBytes: number | null; modifiedUtc: string | null; expiresUtc: string }
export type SetupImportPreviewResult = BasicResult & { preview: SetupImportPreview | null }
export type SetupImportReview = { profileId: string; kind: 'Valheim' | 'Factorio' | 'Terraria'; worldId: string;
  sourceSaveRoot?: string; sourceFolder?: string; format?: string; sourceFiles?: SetupSourceFile[] | null;
  totalBytes?: number | null; modifiedUtc?: string | null; selectionId?: string; expiresUtc?: string }

function object(value: unknown, label: string, fields?: string[]): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError(`${label} must be an object.`)
  const source = value as Record<string, unknown>
  if (fields && (Object.keys(source).length !== fields.length || fields.some(field => !Object.hasOwn(source, field))))
    throw new ContractError(`${label} has unsupported or missing fields.`)
  return source
}
function bounded(value: unknown, maximum: number, label: string): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > maximum || /[\p{Cc}\p{Cf}]/u.test(value))
    throw new ContractError(`${label} must be bounded text.`)
  return value
}
function utc(value: unknown, label: string): string | null {
  if (value === null) return null
  if (typeof value !== 'string' || value.length > 40 || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/u.test(value) || !Number.isFinite(Date.parse(value)))
    throw new ContractError(`${label} must be a UTC timestamp.`)
  return value
}
function bytes(value: unknown, label: string): number | null {
  if (value === null) return null
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) throw new ContractError(`${label} must be a bounded byte count.`)
  return value
}
export function setupSourceFiles(value: unknown, label = 'source files'): SetupSourceFile[] | null {
  if (value === null || value === undefined) return null
  if (!Array.isArray(value) || value.length > 512) throw new ContractError(`${label} must be a bounded array.`)
  return value.map((item, index) => {
    const source = object(item, `${label} ${index}`, ['name', 'bytes', 'modifiedUtc'])
    const name = bounded(source.name, 160, `${label}.name`)
    if (name.includes('/') || name.includes('\\') || name === '.' || name === '..') throw new ContractError(`${label} must contain basenames only.`)
    return { name, bytes: bytes(source.bytes, `${label}.bytes`), modifiedUtc: utc(source.modifiedUtc, `${label}.modifiedUtc`) }
  })
}
export const parseSetupImportPreview: Decoder<SetupImportPreviewResult> = (value, label = 'setup import preview') => {
  const result = parseBasicResult(value, label)
  const source = object(value, label)
  if (Object.keys(source).some(key => !['ok', 'code', 'message', 'preview'].includes(key)) || result.code.length > 80 || result.message.length > 2000)
    throw new ContractError(`${label} has unsupported or unbounded fields.`)
  if (!result.ok) {
    if (source.preview !== null && source.preview !== undefined) throw new ContractError(`${label} cannot expose a failed selection.`)
    return { ...result, preview: null }
  }
  const preview = object(source.preview, `${label}.preview`, ['selectionId', 'profileId', 'kind', 'worldId', 'sourceFiles', 'totalBytes', 'modifiedUtc', 'expiresUtc'])
  const guid = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/iu
  const selectionId = bounded(preview.selectionId, 36, `${label}.selectionId`), profileId = bounded(preview.profileId, 36, `${label}.profileId`)
  if (!guid.test(selectionId) || !guid.test(profileId) || selectionId === '00000000-0000-0000-0000-000000000000' || profileId === '00000000-0000-0000-0000-000000000000')
    throw new ContractError(`${label} has an invalid selection identity.`)
  if (preview.kind !== 'Factorio' && preview.kind !== 'Terraria') throw new ContractError(`${label} has an unsupported game.`)
  const sourceFiles = setupSourceFiles(preview.sourceFiles, `${label}.sourceFiles`)
  const expiresUtc = utc(preview.expiresUtc, `${label}.expiresUtc`)
  if (!sourceFiles?.length || !expiresUtc) throw new ContractError(`${label} has an incomplete selection.`)
  return { ...result, preview: { selectionId, profileId, kind: preview.kind, worldId: bounded(preview.worldId, 64, `${label}.worldId`),
    sourceFiles, totalBytes: bytes(preview.totalBytes, `${label}.totalBytes`), modifiedUtc: utc(preview.modifiedUtc, `${label}.modifiedUtc`), expiresUtc } }
}

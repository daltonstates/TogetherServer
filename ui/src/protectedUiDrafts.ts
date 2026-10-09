import { changeJson } from './api'
import { ContractError } from './contracts'

export type DraftIdentity = {
  purpose: 'file' | 'settings' | 'list' | 'chat'
  profileId: string
  connectionId?: string | null
  key: string
}

export type ProtectedDraftResult = { ok: boolean; text: string | null; revision: number; message: string }

export const maximumProtectedDraftBytes = 64 * 1024
export const maximumProtectedFileDraftBytes = 2 * 1024 * 1024
export const maxProtectedDraftBytesFor = (identity: Pick<DraftIdentity, 'purpose'>): number =>
  identity.purpose === 'file' ? maximumProtectedFileDraftBytes : maximumProtectedDraftBytes

export function parseProtectedDraftResult(value: unknown, context = 'protected draft'): ProtectedDraftResult {
  if (typeof value !== 'object' || value === null || Array.isArray(value))
    throw new ContractError(`${context} must be an object.`)
  const source = value as Record<string, unknown>
  if (typeof source.ok !== 'boolean' ||
    (source.text !== null && typeof source.text !== 'string') ||
    typeof source.revision !== 'number' || !Number.isSafeInteger(source.revision) || source.revision < 0 ||
    typeof source.message !== 'string' || source.message.length > 600)
    throw new ContractError(`${context} has invalid draft fields.`)
  if (typeof source.text === 'string' && new TextEncoder().encode(source.text).byteLength > maximumProtectedFileDraftBytes)
    throw new ContractError(`${context} exceeds the protected draft limit.`)
  return { ok: source.ok, text: source.text, revision: source.revision, message: source.message }
}

function boundedIdentity(identity: DraftIdentity): DraftIdentity {
  const guid = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i
  const empty = '00000000-0000-0000-0000-000000000000'
  if (!['file', 'settings', 'list', 'chat'].includes(identity.purpose) ||
    !guid.test(identity.profileId) || !/^[a-z\d_:-]{1,96}$/i.test(identity.key) ||
    (identity.connectionId != null && (!guid.test(identity.connectionId) ||
      (identity.connectionId === empty && (identity.purpose !== 'chat' || identity.key !== 'compose')))) ||
    (identity.profileId === empty &&
      (identity.purpose !== 'settings' || identity.key !== 'host-setup' || identity.connectionId != null)))
    throw new ContractError('The draft must belong to a saved server or the local setup guide.')
  return { purpose: identity.purpose, profileId: identity.profileId.toLowerCase(),
    connectionId: identity.connectionId?.toLowerCase() ?? null, key: identity.key }
}

function checkedRevision(revision: number): number {
  if (!Number.isSafeInteger(revision) || revision < 0)
    throw new ContractError('The draft revision is invalid. Read the current draft again.')
  return revision
}

export function readProtectedDraft(identity: DraftIdentity, signal?: AbortSignal): Promise<ProtectedDraftResult> {
  return changeJson('/api/local/ui-drafts/read', 'POST', resultFor(identity), boundedIdentity(identity), signal)
}

function resultFor(identity: DraftIdentity) {
  return (value: unknown, context?: string): ProtectedDraftResult => {
    const result = parseProtectedDraftResult(value, context)
    if (result.text !== null && new TextEncoder().encode(result.text).byteLength > maxProtectedDraftBytesFor(identity))
      throw new ContractError('The recovered draft exceeds this editor’s protected storage limit.')
    return result
  }
}

export function saveProtectedDraft(identity: DraftIdentity, text: string, expectedRevision: number,
  signal?: AbortSignal): Promise<ProtectedDraftResult> {
  if (new TextEncoder().encode(text).byteLength > maxProtectedDraftBytesFor(identity))
    throw new ContractError('This draft exceeds this editor’s protected storage limit. Keep a smaller edit.')
  return changeJson('/api/local/ui-drafts', 'PUT', resultFor(identity),
    { ...boundedIdentity(identity), text, expectedRevision: checkedRevision(expectedRevision) }, signal)
}

export function clearProtectedDraft(identity: DraftIdentity, expectedRevision: number,
  signal?: AbortSignal): Promise<ProtectedDraftResult> {
  return changeJson('/api/local/ui-drafts/clear', 'POST', resultFor(identity),
    { ...boundedIdentity(identity), expectedRevision: checkedRevision(expectedRevision) }, signal)
}

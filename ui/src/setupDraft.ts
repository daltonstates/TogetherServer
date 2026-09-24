import { ContractError, parseProfiles, type ActionResult, type CustomScriptBundle, type Settings } from './contracts'
import type { Profile } from './GameProfile'

type DraftStorage = Pick<Storage, 'getItem' | 'removeItem'>
type StorageProvider<T> = () => T

export function removeSetupDraft(storage: Pick<Storage, 'removeItem'>, key: string): void {
  try { storage.removeItem(key) }
  catch { /* Browser storage is optional; an unavailable store must not break setup. */ }
}

export function readSetupDraft(storage: DraftStorage, key: string): Profile[] | null {
  try {
    const saved = storage.getItem(key)
    if (!saved) return null
    const value: unknown = JSON.parse(saved)
    if (typeof value !== 'object' || value === null || Array.isArray(value))
      throw new ContractError('saved setup draft must be an object.')
    const source = value as Record<string, unknown>
    if (source.version !== 2) throw new ContractError('saved setup draft has an unsupported version.')
    return parseProfiles(source.profiles, 'saved setup draft profiles')
  } catch {
    removeSetupDraft(storage, key)
    return null
  }
}

export function serializeSetupDraft(profiles: Profile[]): string {
  return JSON.stringify({ version: 2, profiles })
}

export function readSetupDraftFrom(provider: StorageProvider<DraftStorage>, key: string): Profile[] | null {
  try { return readSetupDraft(provider(), key) }
  catch { return null }
}

export function writeSetupDraftTo(provider: StorageProvider<Pick<Storage, 'setItem'>>, key: string,
  profiles: Profile[]): void {
  try { provider().setItem(key, serializeSetupDraft(profiles)) }
  catch { /* Browser storage is optional; an unavailable store must not break setup. */ }
}

export function removeSetupDraftFrom(provider: StorageProvider<Pick<Storage, 'removeItem'>>, key: string): void {
  try { removeSetupDraft(provider(), key) }
  catch { /* Accessing browser storage can itself throw when storage is disabled. */ }
}

export function reconcileProfileRemoval(current: Settings, result: Pick<ActionResult, 'ok' | 'snapshot'>):
  { committed: boolean; settings: Settings } {
  return result.ok
    ? { committed: true, settings: result.snapshot.settings }
    : { committed: false, settings: current }
}

export function hasSensitiveSetupDraft(passwords: Record<string, string>, scripts: Record<string, CustomScriptBundle>,
  scriptsSaved: Record<string, boolean>): boolean {
  return Object.values(passwords).some(password => password.length > 0) ||
    Object.entries(scripts).some(([profileId, bundle]) => !scriptsSaved[profileId] &&
      Object.values(bundle).some(script => script.trim().length > 0))
}

export function customPortKey(profileId: string, index: number): string {
  return `${profileId}-additional-port-${index}`
}

export function safeFileName(value: string): string {
  return value.split(/[\\/]/).filter(Boolean).at(-1) ?? 'unknown file'
}

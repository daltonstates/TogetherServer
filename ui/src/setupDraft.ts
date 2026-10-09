import { ContractError, parseProfiles, type ActionResult, type CustomScriptBundle, type Settings } from './contracts'
import type { Profile } from './GameProfile'
import type { SetupStep } from './features/setup/HostSetupDialog'

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
  _profiles: Profile[]): void {
  void _profiles
  // Kept for compatibility with older callers. Private setup fields now use the
  // Windows-protected local draft API; browser storage may retain only step metadata.
  try { provider().setItem(key, JSON.stringify({ version: 3, step: 'world' })) }
  catch { /* Browser storage is optional; an unavailable store must not break setup. */ }
}

export type ProtectedSetupDraft = { version: 3; profiles: Profile[]; activeProfileId: string; step: SetupStep; sourceRoot: string }
export function serializeProtectedSetupDraft(profiles: Profile[], step: SetupStep, activeProfileId: string, sourceRoot = ''): string {
  // Explicit projection drops runtime extras, including any accidentally attached secret.
  const selected = profiles.find(profile => profile.id === activeProfileId) ?? profiles[0]
  const safeProfiles = selected ? [{ id: selected.id, kind: selected.kind, name: selected.name, serverName: selected.serverName,
    crossplay: selected.crossplay, publicListing: selected.publicListing, worldId: selected.worldId, worldSource: selected.worldSource,
    worldDirectory: selected.worldDirectory, gamePort: selected.gamePort, executablePath: selected.executablePath,
    minecraft: selected.minecraft ? { serverJarPath: selected.minecraft.serverJarPath } : null,
    factorio: selected.factorio ? { rconPort: selected.factorio.rconPort } : null,
    custom: selected.custom ? { gameName: selected.custom.gameName, primaryProtocol: selected.custom.primaryProtocol,
      shareJoinAddress: selected.custom.shareJoinAddress, additionalPorts: selected.custom.additionalPorts.map(port => ({ protocol: port.protocol,
        port: port.port, label: port.label, family: port.family })) } : null,
    backups: selected.backups ? { enabled: selected.backups.enabled, retentionCount: selected.backups.retentionCount, minimumFreeSpaceMb: selected.backups.minimumFreeSpaceMb } : null,
    crashRecovery: selected.crashRecovery ? { enabled: selected.crashRecovery.enabled } : null,
    maintenance: selected.maintenance ? { enabled: selected.maintenance.enabled, message: selected.maintenance.message } : null,
    sharedSavesEnabled: selected.sharedSavesEnabled ?? false, worldLoadRehearsalId: selected.worldLoadRehearsalId ?? null }] : []
  return JSON.stringify({ version: 3, profiles: safeProfiles, activeProfileId: selected?.id ?? '', step, sourceRoot })
}

export function parseProtectedSetupDraft(text: string): ProtectedSetupDraft {
  if (text.length > 128 * 1024) throw new ContractError('The setup draft is too large.')
  const value: unknown = JSON.parse(text)
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError('The setup draft must be an object.')
  const source = value as Record<string, unknown>
  if (source.version !== 3 || !['game', 'world', 'server', 'review'].includes(String(source.step)) || typeof source.activeProfileId !== 'string')
    throw new ContractError('The setup draft has an unsupported version or step.')
  const profiles = parseProfiles(source.profiles, 'protected setup draft profiles')
  if (profiles.length !== 1 || profiles[0].id !== source.activeProfileId ||
      !/^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/iu.test(source.activeProfileId) || source.activeProfileId === '00000000-0000-0000-0000-000000000000')
    throw new ContractError('The setup draft must name one selected server.')
  const sourceRoot = source.sourceRoot ?? ''
  if (typeof sourceRoot !== 'string' || sourceRoot.length > 4096 || /[\p{Cc}\p{Cf}]/u.test(sourceRoot)) throw new ContractError('The setup draft source root is invalid.')
  return { version: 3, profiles, activeProfileId: source.activeProfileId, step: source.step as SetupStep, sourceRoot }
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

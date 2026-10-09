import { ContractError, type Decoder } from './contracts'

export type GameSettingsValues = { difficulty: string; maximumPlayers: number; gameMode: string;
  allowListEnabled: boolean; forceGameMode: boolean }
export type GameAccessListSummary = { key: string; label: string; available: boolean }
export type GameSettingsView = { ok: boolean; code: string; message: string; kind: string;
  sha256: string | null; settings: GameSettingsValues | null; canUndo: boolean; lists: GameAccessListSummary[] }
export type GameSettingsRequest = { expectedSha256: string; settings: GameSettingsValues }
export type GameSettingChange = { key: string; label: string; before: string | null; after: string | null }
export type GameSettingsPreview = { ok: boolean; code: string; message: string; key: string;
  expectedSha256: string | null; proposedSha256: string | null; changes: GameSettingChange[] }
export type GameAccessEntry = { identity: string | null; name: string | null; ignoresPlayerLimit: boolean | null }
export type GameAccessListView = { ok: boolean; code: string; message: string; kind: string; key: string;
  label: string; sha256: string | null; entries: GameAccessEntry[]; canUndo: boolean }
export type GameAccessListRequest = { expectedSha256: string; entries: GameAccessEntry[] }
export type GameSettingsChangeResult = { ok: boolean; code: string; message: string; key: string;
  sha256: string | null; canUndo: boolean }

const difficulties = ['peaceful', 'easy', 'normal', 'hard']
const gameKinds = ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria', 'Custom', 'Fixture', 'Unknown']
const listKeys = ['allow-list', 'admin-list', 'ban-list', 'permit-list']
const propertyKeys = ['difficulty', 'max-players', 'gamemode', 'white-list', 'allow-list', 'force-gamemode']
const fileKeys = ['server-properties', ...listKeys]
export const maximumGamePlayers = 200
export const maximumAccessEntries = 128

function object(value: unknown, fields: string[], label: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError(`${label} must be an object.`)
  const source = value as Record<string, unknown>
  if (Object.keys(source).length !== fields.length || fields.some(field => !Object.hasOwn(source, field)) ||
      Object.keys(source).some(field => !fields.includes(field))) throw new ContractError(`${label} has unsupported or missing fields.`)
  return source
}
function text(value: unknown, maximum: number, label: string): string {
  if (typeof value !== 'string' || value.length > maximum || Array.from(value).some(character => {
    const code = character.codePointAt(0)!
    return code <= 8 || code === 11 || code === 12 || (code >= 14 && code <= 31) || code === 127 ||
      (code >= 0xd800 && code <= 0xdfff)
  }))
    throw new ContractError(`${label} must be bounded text.`)
  return value
}
function nullableText(value: unknown, maximum: number, label: string): string | null {
  return value === null ? null : text(value, maximum, label)
}
function flag(value: unknown, label: string): boolean {
  if (typeof value !== 'boolean') throw new ContractError(`${label} must be true or false.`)
  return value
}
function choice(value: unknown, choices: string[], label: string): string {
  if (typeof value !== 'string' || !choices.includes(value)) throw new ContractError(`${label} is unsupported.`)
  return value
}
function hash(value: unknown, label: string): string | null {
  if (value === null) return null
  if (typeof value !== 'string' || !/^[0-9a-f]{64}$/iu.test(value)) throw new ContractError(`${label} must be a SHA-256 hash.`)
  return value
}
function basic(source: Record<string, unknown>, label: string) {
  return { ok: flag(source.ok, `${label}.ok`), code: text(source.code, 80, `${label}.code`),
    message: text(source.message, 2000, `${label}.message`) }
}
export function gameModes(kind: string): string[] {
  return kind === 'MinecraftJava' ? ['survival', 'creative', 'adventure', 'spectator'] : ['survival', 'creative', 'adventure']
}
export function settingsIssue(kind: string, values: GameSettingsValues): string | null {
  if (!['MinecraftJava', 'MinecraftBedrock'].includes(kind) || !difficulties.includes(values.difficulty) ||
      !gameModes(kind).includes(values.gameMode)) return 'Choose a supported difficulty and game mode for this edition.'
  return !Number.isInteger(values.maximumPlayers) || values.maximumPlayers < 1 || values.maximumPlayers > maximumGamePlayers
    ? 'Maximum players must be a whole number from 1 to 200.' : null
}
function supportsList(kind: string, key: string): boolean {
  return ['MinecraftJava', 'MinecraftBedrock'].includes(kind) ? key === 'allow-list' :
    kind === 'Valheim' && ['admin-list', 'ban-list', 'permit-list'].includes(key)
}

export const parseGameSettings: Decoder<GameSettingsView> = (value, label = 'game settings') => {
  const source = object(value, ['ok', 'code', 'message', 'kind', 'sha256', 'settings', 'canUndo', 'lists'], label)
  const result = basic(source, label)
  const kind = choice(source.kind, gameKinds, `${label}.kind`)
  const sha256 = hash(source.sha256, `${label}.sha256`)
  const canUndo = flag(source.canUndo, `${label}.canUndo`)
  let settings: GameSettingsValues | null = null
  if (source.settings !== null) {
    const fields = object(source.settings, ['difficulty', 'maximumPlayers', 'gameMode', 'allowListEnabled', 'forceGameMode'], `${label}.settings`)
    if (typeof fields.maximumPlayers !== 'number') throw new ContractError(`${label}.maximumPlayers must be an integer.`)
    settings = { difficulty: choice(fields.difficulty, difficulties, `${label}.difficulty`),
      maximumPlayers: fields.maximumPlayers, gameMode: choice(fields.gameMode, gameModes(kind), `${label}.gameMode`),
      allowListEnabled: flag(fields.allowListEnabled, `${label}.allowListEnabled`),
      forceGameMode: flag(fields.forceGameMode, `${label}.forceGameMode`) }
    if (settingsIssue(kind, settings)) throw new ContractError(`${label} has invalid settings for this edition.`)
  }
  if (!Array.isArray(source.lists) || source.lists.length > 3) throw new ContractError(`${label}.lists must be a bounded array.`)
  const lists = source.lists.map((item, index) => {
    const row = object(item, ['key', 'label', 'available'], `${label}.list ${index}`)
    const key = choice(row.key, listKeys, `${label}.list.key`)
    if (!supportsList(kind, key)) throw new ContractError(`${label} has an unsupported list for this game.`)
    return { key, label: text(row.label, 80, `${label}.list.label`), available: flag(row.available, `${label}.list.available`) }
  })
  if (new Set(lists.map(list => list.key)).size !== lists.length ||
      (settings !== null && (!result.ok || sha256 === null)) ||
      (settings === null && (sha256 !== null || canUndo)) ||
      (result.ok && ['MinecraftJava', 'MinecraftBedrock'].includes(kind) && settings === null))
    throw new ContractError(`${label} has inconsistent file state.`)
  return { ...result, kind, sha256, settings, canUndo, lists }
}

export const parseGameSettingsPreview: Decoder<GameSettingsPreview> = (value, label = 'settings preview') => {
  const source = object(value, ['ok', 'code', 'message', 'key', 'expectedSha256', 'proposedSha256', 'changes'], label)
  const result = basic(source, label)
  const key = choice(source.key, fileKeys, `${label}.key`)
  const expectedSha256 = hash(source.expectedSha256, `${label}.expectedSha256`)
  const proposedSha256 = hash(source.proposedSha256, `${label}.proposedSha256`)
  if (!Array.isArray(source.changes) || source.changes.length > (key === 'server-properties' ? 5 : 256))
    throw new ContractError(`${label}.changes must be a bounded array.`)
  const changes = source.changes.map((item, index) => {
    const row = object(item, ['key', 'label', 'before', 'after'], `${label}.change ${index}`)
    const field = choice(row.key, key === 'server-properties' ? propertyKeys : [key], `${label}.change.key`)
    const before = nullableText(row.before, 512, `${label}.before`)
    const after = nullableText(row.after, 512, `${label}.after`)
    if ((before === null && after === null) || (before !== null && /[\r\n]/u.test(before)) ||
        (after !== null && /[\r\n]/u.test(after))) throw new ContractError(`${label} has invalid preview lines.`)
    return { key: field, label: text(row.label, 80, `${label}.change.label`), before, after }
  })
  if ((result.ok && (expectedSha256 === null || proposedSha256 === null)) ||
      (!result.ok && (expectedSha256 !== null || proposedSha256 !== null || changes.length !== 0)) ||
      (key === 'server-properties' && new Set(changes.map(change => change.key)).size !== changes.length))
    throw new ContractError(`${label} has inconsistent review state.`)
  return { ...result, key, expectedSha256, proposedSha256, changes }
}

export function accessEntryIssue(kind: string, entry: GameAccessEntry): string | null {
  if (kind === 'Valheim') return entry.name !== null || entry.ignoresPlayerLimit !== null || entry.identity === null ||
    !/^[A-Za-z][A-Za-z0-9]{0,19}_[A-Za-z0-9-]{1,100}$/u.test(entry.identity)
    ? 'Enter the case-sensitive Platform_UserID shown in the game F2 panel.' : null
  if (kind === 'MinecraftJava') return entry.ignoresPlayerLimit !== null || entry.identity === null ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(entry.identity) ||
    entry.identity === '00000000-0000-0000-0000-000000000000' || entry.name === null || !/^[A-Za-z0-9_]{1,16}$/u.test(entry.name)
    ? 'Enter the player’s Java username and a valid, non-empty UUID.' : null
  if (kind === 'MinecraftBedrock') return entry.name === null || entry.name.length < 1 || entry.name.length > 32 ||
    entry.name.trim() !== entry.name || /[\p{Cc}\p{Cf}\uD800-\uDFFF]/u.test(entry.name) || entry.ignoresPlayerLimit === null ||
    (entry.identity !== null && !/^[0-9]{1,20}$/u.test(entry.identity))
    ? 'Enter an Xbox gamertag and, if known, its numeric XUID.' : null
  return 'This game has no reviewed access-list editor.'
}
export function accessListIssue(kind: string, entries: GameAccessEntry[]): string | null {
  if (entries.length > maximumAccessEntries) return 'Use at most 128 players in this editor.'
  const identities = new Set<string>()
  const names = new Set<string>()
  for (const entry of entries) {
    const issue = accessEntryIssue(kind, entry)
    if (issue) return issue
    if (entry.identity !== null) {
      const identity = kind === 'Valheim' ? entry.identity : entry.identity.toLowerCase()
      if (identities.has(identity)) return 'Remove the repeated player identity.'
      identities.add(identity)
    }
    if (entry.name !== null) {
      const name = entry.name.toLowerCase()
      if (names.has(name)) return 'Remove the repeated player name.'
      names.add(name)
    }
  }
  return null
}

export const parseGameAccessList: Decoder<GameAccessListView> = (value, label = 'game access list') => {
  const source = object(value, ['ok', 'code', 'message', 'kind', 'key', 'label', 'sha256', 'entries', 'canUndo'], label)
  const result = basic(source, label)
  const kind = choice(source.kind, gameKinds, `${label}.kind`)
  const key = choice(source.key, listKeys, `${label}.key`)
  const sha256 = hash(source.sha256, `${label}.sha256`)
  const canUndo = flag(source.canUndo, `${label}.canUndo`)
  if (!Array.isArray(source.entries) || source.entries.length > maximumAccessEntries)
    throw new ContractError(`${label}.entries must be a bounded array.`)
  const entries = source.entries.map((item, index) => {
    const row = object(item, ['identity', 'name', 'ignoresPlayerLimit'], `${label}.entry ${index}`)
    return { identity: nullableText(row.identity, 128, `${label}.identity`), name: nullableText(row.name, 32, `${label}.name`),
      ignoresPlayerLimit: row.ignoresPlayerLimit === null ? null : flag(row.ignoresPlayerLimit, `${label}.ignoresPlayerLimit`) }
  })
  if ((result.ok && (!supportsList(kind, key) || sha256 === null || accessListIssue(kind, entries))) ||
      (!result.ok && (sha256 !== null || canUndo || entries.length !== 0)))
    throw new ContractError(`${label} has inconsistent or invalid entries.`)
  return { ...result, kind, key, label: text(source.label, 80, `${label}.label`), sha256, entries, canUndo }
}

export const parseGameSettingsChange: Decoder<GameSettingsChangeResult> = (value, label = 'settings change') => {
  const source = object(value, ['ok', 'code', 'message', 'key', 'sha256', 'canUndo'], label)
  const result = basic(source, label)
  const sha256 = hash(source.sha256, `${label}.sha256`)
  const canUndo = flag(source.canUndo, `${label}.canUndo`)
  if ((result.ok && sha256 === null) || (!result.ok && (sha256 !== null || canUndo)))
    throw new ContractError(`${label} has inconsistent saved-file state.`)
  return { ...result, key: choice(source.key, fileKeys, `${label}.key`), sha256, canUndo }
}

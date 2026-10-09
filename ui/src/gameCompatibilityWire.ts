import { ContractError, parseBasicResult, type Decoder } from './contracts'

export type RequiredGameAddOn = { id: string; name: string; version: string; type: 'FactorioMod' | 'BedrockBehaviorPack' | 'BedrockResourcePack' }
export type GameRequirements = {
  profileId: string; kind: string; gameName: string; requiredVersion: string | null
  versionSource: 'Observed' | 'OwnerReported' | 'Unknown'; addOnState: 'Known' | 'Unknown' | 'NotReviewed'
  addOns: RequiredGameAddOn[]; checkedUtc: string; guidance: string
}
export type GameRequirementsResult = { ok: boolean; code: string; message: string; requirements: GameRequirements | null }
export type GameCompatibilityResult = GameRequirementsResult & {
  clientVersion: string | null; clientVersionSource: 'Observed' | 'Manual' | 'Unknown'
  versionComparison: 'Match' | 'Mismatch' | 'Unknown'; addOnComparison: 'Match' | 'Mismatch' | 'Unknown'
}
export const supportedRequirementKinds = ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria']
export function validManualGameVersion(value: string): boolean { return /^[0-9][A-Za-z0-9._+-]{0,47}$/.test(value) }
function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError('Invalid game requirements object.')
  return value as Record<string, unknown>
}
function bounded(value: unknown, maximum: number): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > maximum || /[\p{Cc}\p{Cf}]/u.test(value))
    throw new ContractError('Game requirements text exceeds its bounds.')
  return value
}
function choice<T extends string>(value: unknown, values: readonly T[]): T {
  if (typeof value !== 'string' || !values.includes(value as T)) throw new ContractError('Unsupported game requirements state.')
  return value as T
}
function version(value: unknown): string | null {
  if (value === null) return null
  if (typeof value !== 'string' || !validManualGameVersion(value)) throw new ContractError('Invalid game version.')
  return value
}
function decodeRequirements(value: unknown): GameRequirements | null {
  if (value === null || value === undefined) return null
  const source = record(value)
  const profileId = bounded(source.profileId, 36)
  if (!/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(profileId)) throw new ContractError('Invalid requirement profile.')
  const kind = choice(source.kind, supportedRequirementKinds)
  const gameName = bounded(source.gameName, 40)
  const requiredVersion = version(source.requiredVersion)
  const versionSource = choice(source.versionSource, ['Observed', 'OwnerReported', 'Unknown'] as const)
  if ((requiredVersion === null) !== (versionSource === 'Unknown')) throw new ContractError('Inconsistent required version source.')
  const addOnState = choice(source.addOnState, ['Known', 'Unknown', 'NotReviewed'] as const)
  if (!Array.isArray(source.addOns) || source.addOns.length > 64) throw new ContractError('Too many game add-ons.')
  const addOns: RequiredGameAddOn[] = source.addOns.map(value => {
    const item = record(value)
    const id = bounded(item.id, 80), name = bounded(item.name, 100), addonVersion = version(item.version)
    if (!/^[A-Za-z0-9_-]+$/.test(id) || /[\\/:@]/.test(name) || addonVersion === null) throw new ContractError('Invalid add-on metadata.')
    return { id, name, version: addonVersion, type: choice(item.type, ['FactorioMod', 'BedrockBehaviorPack', 'BedrockResourcePack'] as const) }
  })
  if (new Set(addOns.map(item => `${item.type}:${item.id}`)).size !== addOns.length || addOnState !== 'Known' && addOns.length)
    throw new ContractError('Inconsistent add-on inventory.')
  const checkedUtc = bounded(source.checkedUtc, 40)
  if (!/(Z|\+00:00)$/.test(checkedUtc) || !Number.isFinite(Date.parse(checkedUtc))) throw new ContractError('Invalid requirements timestamp.')
  return { profileId, kind, gameName, requiredVersion, versionSource, addOnState, addOns, checkedUtc, guidance: bounded(source.guidance, 400) }
}
export const parseGameRequirements: Decoder<GameRequirementsResult> = (value, context) => {
  const source = record(value), basic = parseBasicResult(value, context)
  const requirements = decodeRequirements(source.requirements)
  if (basic.ok && !requirements) throw new ContractError('Successful requirements omitted their data.')
  return { ok: basic.ok, code: basic.code, message: basic.message, requirements }
}
export const parseGameCompatibility: Decoder<GameCompatibilityResult> = (value, context) => {
  const source = record(value), basic = parseGameRequirements(value, context)
  const clientVersion = version(source.clientVersion)
  const clientVersionSource = choice(source.clientVersionSource, ['Observed', 'Manual', 'Unknown'] as const)
  if ((clientVersion === null) !== (clientVersionSource === 'Unknown')) throw new ContractError('Inconsistent installed version source.')
  const versionComparison = choice(source.versionComparison, ['Match', 'Mismatch', 'Unknown'] as const)
  const addOnComparison = choice(source.addOnComparison, ['Match', 'Mismatch', 'Unknown'] as const)
  const required = basic.requirements?.requiredVersion
  const expected = !required || !clientVersion ? 'Unknown' : required.toLowerCase() === clientVersion.toLowerCase() ? 'Match' : 'Mismatch'
  if (versionComparison !== expected || addOnComparison === 'Match' && basic.requirements?.addOnState !== 'Known')
    throw new ContractError('Inconsistent compatibility comparison.')
  return { ...basic, clientVersion, clientVersionSource, versionComparison, addOnComparison }
}

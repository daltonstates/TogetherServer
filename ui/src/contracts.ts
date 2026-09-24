import type { Profile } from './GameProfile'
import type { InternetRouteCheck, PortDiagnostics } from './ServerReadiness'
import type { MinecraftDiscovery, MinecraftInstallation } from './MinecraftSetup'

export type Settings = {
  maxConcurrentServers: number
  idleMinutes: number
  friendTimerExtensionMinutes: number
  friendTimerExtensionMaximumMinutes: number
  autoShutdownEnabled: boolean
  remoteControlsEnabled: boolean
  companionListeningEnabled: boolean
  companionBindAddress: string
  companionEndpoint: string
  companionPort: number
  connectionRoute: { mode: 'DirectInternet' | 'PrivateMesh' | 'AdvancedAddress'; address: string }
  publicGameIp: string
  publicGameIpCheckedUtc: string | null
  profiles: Profile[]
}

export type Run = {
  profileId: string
  state: string
  detail: string
  processId: number | null
  onlinePlayers: number | null
  maxPlayers: number | null
  autoShutdownAtUtc: string | null
  autoShutdownReason: string | null
  hostAddedTime: boolean
  playerNames: string[] | null
  playerCountTrusted: boolean
  friendAddedMinutes: number
}

export type CustomCertificationState = {
  profileId: string
  stage: string
  message: string
  inProgress: boolean
  certified: boolean
  certifiedUtc: string | null
  onlinePlayers: number | null
  blockReason: string | null
}

export type CrashRecoveryState = {
  profileId: string
  cycleId: string
  state: 'Pending' | 'Starting' | 'Recovered' | 'Suspended'
  attempts: number
  crashDetectedUtc: string
  nextAttemptUtc: string | null
  readinessDeadlineUtc: string | null
  recoveredUtc: string | null
  lastFailure: string | null
}

export type WorldBackupStatus = {
  profileId: string
  lastSuccessfulUtc: string | null
  lastFailureUtc: string | null
  lastFailure: string | null
  completedCount: number
}

export type WorldBackupRecord = {
  id: string
  profileId: string
  kind: string
  worldId: string
  backupKind: 'Rolling' | 'PreRestore'
  createdUtc: string
  sizeBytes: number
  fileCount: number
}

export type WorldBackupList = { backups: WorldBackupRecord[]; status: WorldBackupStatus }

export type ActivityEvent = {
  id: string
  occurredUtc: string
  category: string
  action: string
  message: string
  severity: 'Info' | 'Important' | 'Warning'
  profileId: string | null
  deviceId: string | null
  visibility: string
}

export type HostSnapshot = {
  mode: 'Host'
  evidence: string
  settings: Settings
  runs: Run[]
  passwordConfigured: Record<string, boolean>
  managedWorldsRoot: string
  customCertifications?: Record<string, CustomCertificationState>
  crashRecovery?: Record<string, CrashRecoveryState>
  backups?: Record<string, WorldBackupStatus>
  activity?: ActivityEvent[]
  recovery?: DataRecoveryView | null
}

export type RemoteOperation = {
  id: string
  action: string
  state: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Interrupted'
  ok: boolean | null
  code: string
  message: string
  requestedUtc: string
  completedUtc: string | null
  portConflicts?: PortConflict[] | null
}

export type PublicProfile = {
  id: string
  name: string
  state: string
  joinAddress: string | null
  canStopNow: boolean
  stopReason: string | null
  kind: string
  onlinePlayers: number | null
  maxPlayers: number | null
  autoShutdownAtUtc: string | null
  autoShutdownReason: string | null
  canStart: boolean
  canStop: boolean
  canRestartNow: boolean
  restartReason: string | null
  operation?: RemoteOperation | null
  maintenanceEnabled: boolean
  maintenanceMessage: string | null
  canExtendTimer: boolean
  timerExtensionMinutes: number
  timerExtensionRemainingMinutes: number
}

export type ServerPermission = { profileId: string; canStart: boolean; canStop: boolean; canExtendTimer: boolean }

export type Device = {
  id: string
  profileId: string
  assignedProfileIds: string[]
  name: string
  canStart: boolean
  canStop: boolean
  canExtendTimer: boolean
  revoked: boolean
  paired: boolean
  approvalPending: boolean
  credentialExpiresUtc: string | null
  lastHeartbeatUtc: string | null
  serverPermissions: ServerPermission[]
}

export type HostCertificateState = {
  hostId: string
  activeFingerprint: string
  activeExpiresUtc: string
  nextFingerprint: string | null
  nextExpiresUtc: string | null
  previousFingerprint: string | null
  previousAcceptedUntilUtc: string | null
}

export type CompanionInfo = {
  listenerActive: boolean
  listenerWarning: string | null
  endpoint: string
  fingerprint: string | null
  certificates: HostCertificateState | null
  route: { mode: string; address: string }
  devices: Device[]
  stopSafety: Record<string, { available: boolean; reason: string }>
}

export type FriendSnapshot = {
  mode: 'Friend'
  state: string
  detail: string
  endpoint: string
  lastConnectedUtc: string | null
  remoteControlsEnabled: boolean
  canStart: boolean
  canStop: boolean
  profiles: PublicProfile[]
  connectionId: string
  connections: FriendSnapshot[] | null
  connectionCode?: string | null
  hostVersion?: string | null
  friendVersion?: string
  hostProtocolVersion?: number | null
  protocolCompatible?: boolean
  credentialExpiresUtc?: string | null
  certificateExpiresUtc?: string | null
  expiryWarning?: string | null
  routeMode?: string
  routeAddress?: string | null
  hostId?: string
  connectionName?: string | null
  activity?: ActivityEvent[]
}

export type Snapshot = HostSnapshot | FriendSnapshot
export type DataRecoveryNotice = {
  stateFile: string
  quarantinedFile: string
  reason: string
  detectedUtc: string
  blocksLifecycle: boolean
}
export type DataRecoveryView = { lifecycleBlocked: boolean; notices: DataRecoveryNotice[] }
export type GamePort = { protocol: string; port: number; label: string; family: string }
export type PortConflict = { profileId: string; profileName: string; sharedPorts: GamePort[]; canReplace: boolean; blockReason: string | null }
export type BasicResult = { ok: boolean; code: string; message: string; portConflicts?: PortConflict[] | null; operationId?: string | null; operationState?: RemoteOperation['state'] | null }
export type ActionResult = BasicResult & { snapshot: HostSnapshot }
export type PublicIpDetection = BasicResult & { address: string | null; snapshot?: HostSnapshot }
export type Discovery = { installations: { executablePath: string; source: string }[]; worlds: { name: string; saveRoot: string; sourceFolder: string; format: string }[] }
export type ImportResult = BasicResult & { worldDirectory: string | null }
export type ServerBrowseResult = BasicResult & { executablePath?: string }
export type MinecraftBrowseResult = BasicResult & { path?: string }
export type MinecraftInstallResult = BasicResult & { installation?: MinecraftInstallation }
export type WorldBrowseResult = BasicResult & { worldId: string | null; sourceSaveRoot: string | null; sourceFolder: string }
export type UpdateView = { state: 'Checking' | 'Current' | 'Available' | 'NoRelease' | 'Unavailable' | 'Unsupported'; currentVersion: string; latestVersion: string | null; message: string }
export type DesktopPreferences = { available: boolean; launchAtLogin: boolean; closeToTray: boolean; startupAvailable: boolean }
export type DesktopPreferenceResult = BasicResult & { preferences: DesktopPreferences }
export type FriendIssue = { code: string; message: string }
export type BrowseResult = BasicResult & { path?: string | null }
export type CustomScriptBundle = { start: string; status: string; stop: string }
export type CustomScriptResult = BasicResult & { scripts: CustomScriptBundle }
export type CustomCertificationResult = BasicResult & { snapshot: HostSnapshot; certification: CustomCertificationState }
export type RouteDiscovery = { privateMeshCandidates: { provider: string; interfaceName: string; address: string }[]; advancedCandidates: { provider: string; interfaceName: string; address: string }[] }
export type GameEndpointResult = { answered: boolean; code: string; message: string; checkedUtc: string; onlinePlayers: number | null; maxPlayers: number | null }
export type InviteState = { exists: boolean; open: boolean; canStart: boolean; durationMinutes: number; deviceLimit: number; requireApproval: boolean }
export type InviteResult = BasicResult & { password?: string; expiresUtc?: string; listenerActive?: boolean; listenerWarning?: string }
export type PasswordResult = BasicResult & { password?: string }

export type Decoder<T> = (value: unknown, context?: string) => T

export class ContractError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'ContractError'
  }
}

type JsonRecord = Record<string, unknown>

function object(value: unknown, context: string): JsonRecord {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new ContractError(`${context} must be an object.`)
  return value as JsonRecord
}

function text(value: unknown, context = 'value'): string {
  if (typeof value !== 'string') throw new ContractError(`${context} must be text.`)
  return value
}

function flag(value: unknown, context = 'value'): boolean {
  if (typeof value !== 'boolean') throw new ContractError(`${context} must be true or false.`)
  return value
}

function numeric(value: unknown, context = 'value'): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) throw new ContractError(`${context} must be a finite number.`)
  return value
}

function nullableText(value: unknown, context: string): string | null {
  return value === null ? null : text(value, context)
}

function nullableNumber(value: unknown, context: string): number | null {
  return value === null ? null : numeric(value, context)
}

function optionalText(value: unknown, context: string): string | undefined {
  return value === undefined ? undefined : text(value, context)
}

function optionalNullableText(value: unknown, context: string): string | null | undefined {
  return value === undefined ? undefined : nullableText(value, context)
}

function optionalNullableNumber(value: unknown, context: string): number | null | undefined {
  return value === undefined ? undefined : nullableNumber(value, context)
}

function list<T>(value: unknown, context: string, decode: Decoder<T>): T[] {
  if (!Array.isArray(value)) throw new ContractError(`${context} must be a list.`)
  return value.map((item, index) => decode(item, `${context}[${index}]`))
}

function textList(value: unknown, context: string): string[] {
  return list(value, context, text)
}

function literal<T extends string>(value: unknown, allowed: readonly T[], context: string): T {
  if (typeof value !== 'string' || !allowed.includes(value as T))
    throw new ContractError(`${context} has an unsupported value.`)
  return value as T
}

function optionalObject<T>(value: unknown, context: string, decode: Decoder<T>): T | undefined {
  return value === undefined ? undefined : decode(value, context)
}

function nullableObject<T>(value: unknown, context: string, decode: Decoder<T>): T | null {
  return value === null ? null : decode(value, context)
}

function dictionary<T>(value: unknown, context: string, decode: Decoder<T>): Record<string, T> {
  const source = object(value, context)
  return Object.fromEntries(Object.entries(source).map(([key, item]) => [key, decode(item, `${context}.${key}`)]))
}

export function parseProfile(value: unknown, context = 'profile'): Profile {
  const source = object(value, context)
  const kind = literal(source.kind, ['Fixture', 'Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Custom'] as const, `${context}.kind`)
  const worldSource = literal(source.worldSource, ['Existing', 'New'] as const, `${context}.worldSource`)
  const minecraft = source.minecraft === undefined ? undefined : source.minecraft === null ? null : (() => {
    const item = object(source.minecraft, `${context}.minecraft`)
    return { serverJarPath: text(item.serverJarPath, `${context}.minecraft.serverJarPath`) }
  })()
  const custom = source.custom === undefined ? undefined : source.custom === null ? null : (() => {
    const item = object(source.custom, `${context}.custom`)
    return {
      gameName: text(item.gameName, `${context}.custom.gameName`),
      primaryProtocol: literal(item.primaryProtocol, ['TCP', 'UDP'] as const, `${context}.custom.primaryProtocol`),
      shareJoinAddress: flag(item.shareJoinAddress, `${context}.custom.shareJoinAddress`),
      additionalPorts: list(item.additionalPorts, `${context}.custom.additionalPorts`, (port, portContext) => {
        const parsed = object(port, portContext ?? `${context}.custom.additionalPorts`)
        return {
          protocol: literal(parsed.protocol, ['TCP', 'UDP'] as const, `${portContext}.protocol`),
          port: numeric(parsed.port, `${portContext}.port`),
          label: text(parsed.label, `${portContext}.label`),
          family: literal(parsed.family, ['Any', 'IPv4', 'IPv6'] as const, `${portContext}.family`)
        }
      })
    }
  })()
  const crashRecovery = source.crashRecovery === undefined ? undefined : source.crashRecovery === null ? null : (() => {
    const item = object(source.crashRecovery, `${context}.crashRecovery`)
    return { enabled: flag(item.enabled, `${context}.crashRecovery.enabled`) }
  })()
  const backups = source.backups === undefined ? undefined : source.backups === null ? null : (() => {
    const item = object(source.backups, `${context}.backups`)
    return {
      enabled: flag(item.enabled, `${context}.backups.enabled`),
      retentionCount: numeric(item.retentionCount, `${context}.backups.retentionCount`),
      minimumFreeSpaceMb: numeric(item.minimumFreeSpaceMb, `${context}.backups.minimumFreeSpaceMb`)
    }
  })()
  const maintenance = source.maintenance === undefined ? undefined : source.maintenance === null ? null : (() => {
    const item = object(source.maintenance, `${context}.maintenance`)
    return { enabled: flag(item.enabled, `${context}.maintenance.enabled`), message: text(item.message, `${context}.maintenance.message`) }
  })()
  return {
    id: text(source.id, `${context}.id`), kind, name: text(source.name, `${context}.name`),
    serverName: text(source.serverName, `${context}.serverName`), crossplay: flag(source.crossplay, `${context}.crossplay`),
    publicListing: flag(source.publicListing, `${context}.publicListing`), worldId: text(source.worldId, `${context}.worldId`),
    worldSource, worldDirectory: text(source.worldDirectory, `${context}.worldDirectory`),
    gamePort: numeric(source.gamePort, `${context}.gamePort`), executablePath: text(source.executablePath, `${context}.executablePath`),
    minecraft, custom, crashRecovery, backups, maintenance
  }
}

export const parseProfiles: Decoder<Profile[]> = (value, context = 'profiles') => list(value, context, parseProfile)

export const parseSettings: Decoder<Settings> = (value, context = 'settings') => {
  const source = object(value, context)
  const route = object(source.connectionRoute, `${context}.connectionRoute`)
  return {
    maxConcurrentServers: numeric(source.maxConcurrentServers, `${context}.maxConcurrentServers`),
    idleMinutes: numeric(source.idleMinutes, `${context}.idleMinutes`),
    friendTimerExtensionMinutes: numeric(source.friendTimerExtensionMinutes, `${context}.friendTimerExtensionMinutes`),
    friendTimerExtensionMaximumMinutes: numeric(source.friendTimerExtensionMaximumMinutes, `${context}.friendTimerExtensionMaximumMinutes`),
    autoShutdownEnabled: flag(source.autoShutdownEnabled, `${context}.autoShutdownEnabled`),
    remoteControlsEnabled: flag(source.remoteControlsEnabled, `${context}.remoteControlsEnabled`),
    companionListeningEnabled: flag(source.companionListeningEnabled, `${context}.companionListeningEnabled`),
    companionBindAddress: text(source.companionBindAddress, `${context}.companionBindAddress`),
    companionEndpoint: text(source.companionEndpoint, `${context}.companionEndpoint`),
    companionPort: numeric(source.companionPort, `${context}.companionPort`),
    connectionRoute: {
      mode: literal(route.mode, ['DirectInternet', 'PrivateMesh', 'AdvancedAddress'] as const, `${context}.connectionRoute.mode`),
      address: text(route.address, `${context}.connectionRoute.address`)
    },
    publicGameIp: text(source.publicGameIp, `${context}.publicGameIp`),
    publicGameIpCheckedUtc: nullableText(source.publicGameIpCheckedUtc, `${context}.publicGameIpCheckedUtc`),
    profiles: parseProfiles(source.profiles, `${context}.profiles`)
  }
}

const parseRun: Decoder<Run> = (value, context = 'run') => {
  const source = object(value, context)
  return {
    profileId: text(source.profileId, `${context}.profileId`), state: text(source.state, `${context}.state`),
    detail: text(source.detail, `${context}.detail`), processId: nullableNumber(source.processId, `${context}.processId`),
    onlinePlayers: nullableNumber(source.onlinePlayers, `${context}.onlinePlayers`),
    maxPlayers: nullableNumber(source.maxPlayers, `${context}.maxPlayers`),
    autoShutdownAtUtc: nullableText(source.autoShutdownAtUtc, `${context}.autoShutdownAtUtc`),
    autoShutdownReason: nullableText(source.autoShutdownReason, `${context}.autoShutdownReason`),
    hostAddedTime: flag(source.hostAddedTime, `${context}.hostAddedTime`),
    playerNames: source.playerNames === null ? null : textList(source.playerNames, `${context}.playerNames`),
    playerCountTrusted: flag(source.playerCountTrusted, `${context}.playerCountTrusted`),
    friendAddedMinutes: numeric(source.friendAddedMinutes, `${context}.friendAddedMinutes`)
  }
}

const parseActivity: Decoder<ActivityEvent> = (value, context = 'activity') => {
  const source = object(value, context)
  return {
    id: text(source.id, `${context}.id`), occurredUtc: text(source.occurredUtc, `${context}.occurredUtc`),
    category: text(source.category, `${context}.category`), action: text(source.action, `${context}.action`),
    message: text(source.message, `${context}.message`),
    severity: literal(source.severity, ['Info', 'Important', 'Warning'] as const, `${context}.severity`),
    profileId: nullableText(source.profileId, `${context}.profileId`),
    deviceId: nullableText(source.deviceId, `${context}.deviceId`), visibility: text(source.visibility, `${context}.visibility`)
  }
}

const parseCustomCertification: Decoder<CustomCertificationState> = (value, context = 'custom certification') => {
  const source = object(value, context)
  return {
    profileId: text(source.profileId, `${context}.profileId`), stage: text(source.stage, `${context}.stage`),
    message: text(source.message, `${context}.message`), inProgress: flag(source.inProgress, `${context}.inProgress`),
    certified: flag(source.certified, `${context}.certified`), certifiedUtc: nullableText(source.certifiedUtc, `${context}.certifiedUtc`),
    onlinePlayers: nullableNumber(source.onlinePlayers, `${context}.onlinePlayers`), blockReason: nullableText(source.blockReason, `${context}.blockReason`)
  }
}

const parseCrashRecovery: Decoder<CrashRecoveryState> = (value, context = 'crash recovery') => {
  const source = object(value, context)
  return {
    profileId: text(source.profileId, `${context}.profileId`), cycleId: text(source.cycleId, `${context}.cycleId`),
    state: literal(source.state, ['Pending', 'Starting', 'Recovered', 'Suspended'] as const, `${context}.state`),
    attempts: numeric(source.attempts, `${context}.attempts`), crashDetectedUtc: text(source.crashDetectedUtc, `${context}.crashDetectedUtc`),
    nextAttemptUtc: nullableText(source.nextAttemptUtc, `${context}.nextAttemptUtc`),
    readinessDeadlineUtc: nullableText(source.readinessDeadlineUtc, `${context}.readinessDeadlineUtc`),
    recoveredUtc: nullableText(source.recoveredUtc, `${context}.recoveredUtc`), lastFailure: nullableText(source.lastFailure, `${context}.lastFailure`)
  }
}

const parseBackupStatus: Decoder<WorldBackupStatus> = (value, context = 'backup status') => {
  const source = object(value, context)
  return {
    profileId: text(source.profileId, `${context}.profileId`), lastSuccessfulUtc: nullableText(source.lastSuccessfulUtc, `${context}.lastSuccessfulUtc`),
    lastFailureUtc: nullableText(source.lastFailureUtc, `${context}.lastFailureUtc`), lastFailure: nullableText(source.lastFailure, `${context}.lastFailure`),
    completedCount: numeric(source.completedCount, `${context}.completedCount`)
  }
}

const parseHostSnapshot: Decoder<HostSnapshot> = (value, context = 'host snapshot') => {
  const source = object(value, context)
  if (source.mode !== 'Host') throw new ContractError(`${context}.mode must be Host.`)
  return {
    mode: 'Host', evidence: text(source.evidence, `${context}.evidence`), settings: parseSettings(source.settings, `${context}.settings`),
    runs: list(source.runs, `${context}.runs`, parseRun),
    passwordConfigured: dictionary(source.passwordConfigured, `${context}.passwordConfigured`, flag),
    managedWorldsRoot: text(source.managedWorldsRoot, `${context}.managedWorldsRoot`),
    customCertifications: optionalObject(source.customCertifications, `${context}.customCertifications`, (item, itemContext) => dictionary(item, itemContext ?? '', parseCustomCertification)),
    crashRecovery: optionalObject(source.crashRecovery, `${context}.crashRecovery`, (item, itemContext) => dictionary(item, itemContext ?? '', parseCrashRecovery)),
    backups: optionalObject(source.backups, `${context}.backups`, (item, itemContext) => dictionary(item, itemContext ?? '', parseBackupStatus)),
    activity: source.activity === undefined || source.activity === null ? undefined : list(source.activity, `${context}.activity`, parseActivity),
    recovery: source.recovery === undefined ? undefined : source.recovery === null ? null : parseDataRecoveryView(source.recovery, `${context}.recovery`)
  }
}

const parsePortConflict: Decoder<PortConflict> = (value, context = 'port conflict') => {
  const source = object(value, context)
  return {
    profileId: text(source.profileId, `${context}.profileId`), profileName: text(source.profileName, `${context}.profileName`),
    sharedPorts: list(source.sharedPorts, `${context}.sharedPorts`, (port, portContext) => {
      const item = object(port, portContext ?? `${context}.sharedPorts`)
      return { protocol: text(item.protocol, `${portContext}.protocol`), port: numeric(item.port, `${portContext}.port`),
        label: text(item.label, `${portContext}.label`), family: text(item.family, `${portContext}.family`) }
    }),
    canReplace: flag(source.canReplace, `${context}.canReplace`), blockReason: nullableText(source.blockReason, `${context}.blockReason`)
  }
}

const parseRemoteOperation: Decoder<RemoteOperation> = (value, context = 'remote operation') => {
  const source = object(value, context)
  return {
    id: text(source.id, `${context}.id`), action: text(source.action, `${context}.action`),
    state: literal(source.state, ['Pending', 'Running', 'Succeeded', 'Failed', 'Interrupted'] as const, `${context}.state`),
    ok: source.ok === null ? null : flag(source.ok, `${context}.ok`), code: text(source.code, `${context}.code`),
    message: text(source.message, `${context}.message`), requestedUtc: text(source.requestedUtc, `${context}.requestedUtc`),
    completedUtc: nullableText(source.completedUtc, `${context}.completedUtc`),
    portConflicts: source.portConflicts === undefined ? undefined : source.portConflicts === null ? null : list(source.portConflicts, `${context}.portConflicts`, parsePortConflict)
  }
}

const parsePublicProfile: Decoder<PublicProfile> = (value, context = 'public profile') => {
  const source = object(value, context)
  return {
    id: text(source.id, `${context}.id`), name: text(source.name, `${context}.name`), state: text(source.state, `${context}.state`),
    joinAddress: nullableText(source.joinAddress, `${context}.joinAddress`), canStopNow: flag(source.canStopNow, `${context}.canStopNow`),
    stopReason: nullableText(source.stopReason, `${context}.stopReason`), kind: text(source.kind, `${context}.kind`),
    onlinePlayers: nullableNumber(source.onlinePlayers, `${context}.onlinePlayers`), maxPlayers: nullableNumber(source.maxPlayers, `${context}.maxPlayers`),
    autoShutdownAtUtc: nullableText(source.autoShutdownAtUtc, `${context}.autoShutdownAtUtc`),
    autoShutdownReason: nullableText(source.autoShutdownReason, `${context}.autoShutdownReason`),
    canStart: flag(source.canStart, `${context}.canStart`), canStop: flag(source.canStop, `${context}.canStop`),
    canRestartNow: flag(source.canRestartNow, `${context}.canRestartNow`), restartReason: nullableText(source.restartReason, `${context}.restartReason`),
    operation: source.operation === undefined ? undefined : nullableObject(source.operation, `${context}.operation`, parseRemoteOperation),
    maintenanceEnabled: flag(source.maintenanceEnabled, `${context}.maintenanceEnabled`),
    maintenanceMessage: nullableText(source.maintenanceMessage, `${context}.maintenanceMessage`),
    canExtendTimer: flag(source.canExtendTimer, `${context}.canExtendTimer`),
    timerExtensionMinutes: numeric(source.timerExtensionMinutes, `${context}.timerExtensionMinutes`),
    timerExtensionRemainingMinutes: numeric(source.timerExtensionRemainingMinutes, `${context}.timerExtensionRemainingMinutes`)
  }
}

const parseFriendSnapshotInternal = (value: unknown, context: string, depth: number): FriendSnapshot => {
  const source = object(value, context)
  if (source.mode !== 'Friend') throw new ContractError(`${context}.mode must be Friend.`)
  if (depth > 4) throw new ContractError(`${context}.connections is nested too deeply.`)
  return {
    mode: 'Friend', state: text(source.state, `${context}.state`), detail: text(source.detail, `${context}.detail`),
    endpoint: text(source.endpoint, `${context}.endpoint`), lastConnectedUtc: nullableText(source.lastConnectedUtc, `${context}.lastConnectedUtc`),
    remoteControlsEnabled: flag(source.remoteControlsEnabled, `${context}.remoteControlsEnabled`),
    canStart: flag(source.canStart, `${context}.canStart`), canStop: flag(source.canStop, `${context}.canStop`),
    profiles: list(source.profiles, `${context}.profiles`, parsePublicProfile), connectionId: text(source.connectionId, `${context}.connectionId`),
    connections: source.connections === null ? null : list(source.connections, `${context}.connections`, (item, itemContext) => parseFriendSnapshotInternal(item, itemContext ?? `${context}.connections`, depth + 1)),
    connectionCode: optionalNullableText(source.connectionCode, `${context}.connectionCode`), hostVersion: optionalNullableText(source.hostVersion, `${context}.hostVersion`),
    friendVersion: optionalText(source.friendVersion, `${context}.friendVersion`), hostProtocolVersion: optionalNullableNumber(source.hostProtocolVersion, `${context}.hostProtocolVersion`),
    protocolCompatible: source.protocolCompatible === undefined ? undefined : flag(source.protocolCompatible, `${context}.protocolCompatible`),
    credentialExpiresUtc: optionalNullableText(source.credentialExpiresUtc, `${context}.credentialExpiresUtc`),
    certificateExpiresUtc: optionalNullableText(source.certificateExpiresUtc, `${context}.certificateExpiresUtc`),
    expiryWarning: optionalNullableText(source.expiryWarning, `${context}.expiryWarning`), routeMode: optionalText(source.routeMode, `${context}.routeMode`),
    routeAddress: optionalNullableText(source.routeAddress, `${context}.routeAddress`), hostId: optionalText(source.hostId, `${context}.hostId`),
    connectionName: optionalNullableText(source.connectionName, `${context}.connectionName`),
    activity: source.activity === undefined || source.activity === null ? undefined : list(source.activity, `${context}.activity`, parseActivity)
  }
}

export const parseSnapshot: Decoder<Snapshot> = (value, context = 'snapshot') => {
  const source = object(value, context)
  if (source.mode === 'Host') return parseHostSnapshot(value, context)
  if (source.mode === 'Friend') return parseFriendSnapshotInternal(value, context, 0)
  throw new ContractError(`${context}.mode must be Host or Friend.`)
}

export const parseFriendSnapshot: Decoder<FriendSnapshot> = (value, context = 'friend snapshot') => parseFriendSnapshotInternal(value, context, 0)

export const parseDataRecoveryView: Decoder<DataRecoveryView> = (value, context = 'data recovery') => {
  const source = object(value, context)
  return {
    lifecycleBlocked: flag(source.lifecycleBlocked, `${context}.lifecycleBlocked`),
    notices: list(source.notices, `${context}.notices`, (item, itemContext) => {
      const entry = object(item, itemContext ?? `${context}.notices`)
      return {
        stateFile: text(entry.stateFile, `${itemContext}.stateFile`),
        quarantinedFile: text(entry.quarantinedFile, `${itemContext}.quarantinedFile`),
        reason: text(entry.reason, `${itemContext}.reason`),
        detectedUtc: text(entry.detectedUtc, `${itemContext}.detectedUtc`),
        blocksLifecycle: flag(entry.blocksLifecycle, `${itemContext}.blocksLifecycle`)
      }
    })
  }
}

export const parseBasicResult: Decoder<BasicResult> = (value, context = 'result') => {
  const source = object(value, context)
  return {
    ok: flag(source.ok, `${context}.ok`), code: text(source.code, `${context}.code`), message: text(source.message, `${context}.message`),
    portConflicts: source.portConflicts === undefined ? undefined : source.portConflicts === null ? null : list(source.portConflicts, `${context}.portConflicts`, parsePortConflict),
    operationId: optionalNullableText(source.operationId, `${context}.operationId`),
    operationState: source.operationState === undefined || source.operationState === null ? source.operationState :
      literal(source.operationState, ['Pending', 'Running', 'Succeeded', 'Failed', 'Interrupted'] as const, `${context}.operationState`)
  }
}

function withBasicResult(value: unknown, context: string): { source: JsonRecord; basic: BasicResult } {
  const source = object(value, context)
  return { source, basic: parseBasicResult(source, context) }
}

export const parseActionResult: Decoder<ActionResult> = (value, context = 'action result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, snapshot: parseHostSnapshot(source.snapshot, `${context}.snapshot`) }
}

export const parsePublicIpDetection: Decoder<PublicIpDetection> = (value, context = 'public IP result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, address: nullableText(source.address, `${context}.address`),
    snapshot: source.snapshot === undefined ? undefined : parseHostSnapshot(source.snapshot, `${context}.snapshot`) }
}

export const parseDesktopPreferences: Decoder<DesktopPreferences> = (value, context = 'desktop preferences') => {
  const source = object(value, context)
  return { available: flag(source.available, `${context}.available`), launchAtLogin: flag(source.launchAtLogin, `${context}.launchAtLogin`),
    closeToTray: flag(source.closeToTray, `${context}.closeToTray`), startupAvailable: flag(source.startupAvailable, `${context}.startupAvailable`) }
}

export const parseDesktopPreferenceResult: Decoder<DesktopPreferenceResult> = (value, context = 'desktop preference result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, preferences: parseDesktopPreferences(source.preferences, `${context}.preferences`) }
}

export const parseUpdateView: Decoder<UpdateView> = (value, context = 'update status') => {
  const source = object(value, context)
  return { state: literal(source.state, ['Checking', 'Current', 'Available', 'NoRelease', 'Unavailable', 'Unsupported'] as const, `${context}.state`),
    currentVersion: text(source.currentVersion, `${context}.currentVersion`), latestVersion: nullableText(source.latestVersion, `${context}.latestVersion`),
    message: text(source.message, `${context}.message`) }
}

export const parseCustomScriptResult: Decoder<CustomScriptResult> = (value, context = 'custom script result') => {
  const { source, basic } = withBasicResult(value, context)
  if (!basic.ok && (source.scripts === undefined || source.scripts === null))
    return { ...basic, scripts: { start: '', status: '', stop: '' } }
  const scripts = object(source.scripts, `${context}.scripts`)
  return { ...basic, scripts: { start: text(scripts.start, `${context}.scripts.start`), status: text(scripts.status, `${context}.scripts.status`),
    stop: text(scripts.stop, `${context}.scripts.stop`) } }
}

export const parseCustomCertificationResult: Decoder<CustomCertificationResult> = (value, context = 'custom certification result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, snapshot: parseHostSnapshot(source.snapshot, `${context}.snapshot`),
    certification: parseCustomCertification(source.certification, `${context}.certification`) }
}

export const parseDiscovery: Decoder<Discovery> = (value, context = 'discovery') => {
  const source = object(value, context)
  return {
    installations: list(source.installations, `${context}.installations`, (item, itemContext) => {
      const entry = object(item, itemContext ?? `${context}.installations`)
      return { executablePath: text(entry.executablePath, `${itemContext}.executablePath`), source: text(entry.source, `${itemContext}.source`) }
    }),
    worlds: list(source.worlds, `${context}.worlds`, (item, itemContext) => {
      const entry = object(item, itemContext ?? `${context}.worlds`)
      return { name: text(entry.name, `${itemContext}.name`), saveRoot: text(entry.saveRoot, `${itemContext}.saveRoot`),
        sourceFolder: text(entry.sourceFolder, `${itemContext}.sourceFolder`), format: text(entry.format, `${itemContext}.format`) }
    })
  }
}

function parseMinecraftInstallation(value: unknown, context = 'Minecraft installation'): MinecraftInstallation {
  const source = object(value, context)
  return {
    kind: literal(source.kind, ['MinecraftJava', 'MinecraftBedrock'] as const, `${context}.kind`),
    serverDirectory: text(source.serverDirectory, `${context}.serverDirectory`), artifactPath: text(source.artifactPath, `${context}.artifactPath`),
    executablePath: text(source.executablePath, `${context}.executablePath`), worldName: text(source.worldName, `${context}.worldName`),
    gamePort: numeric(source.gamePort, `${context}.gamePort`), source: text(source.source, `${context}.source`), note: text(source.note, `${context}.note`)
  }
}

export const parseMinecraftDiscovery: Decoder<MinecraftDiscovery> = (value, context = 'Minecraft discovery') => {
  const source = object(value, context)
  return { installations: list(source.installations, `${context}.installations`, parseMinecraftInstallation),
    javaRuntimePath: text(source.javaRuntimePath, `${context}.javaRuntimePath`) }
}

export const parseMinecraftInstallResult: Decoder<MinecraftInstallResult> = (value, context = 'Minecraft install result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, installation: source.installation === undefined || source.installation === null
    ? undefined : parseMinecraftInstallation(source.installation, `${context}.installation`) }
}

export const parsePortDiagnostics: Decoder<PortDiagnostics> = (value, context = 'port diagnostics') => {
  const source = object(value, context)
  const control = object(source.control, `${context}.control`)
  return {
    checkedUtc: text(source.checkedUtc, `${context}.checkedUtc`),
    games: list(source.games, `${context}.games`, (item, itemContext) => {
      const entry = object(item, itemContext ?? `${context}.games`)
      return { profileId: text(entry.profileId, `${itemContext}.profileId`), label: text(entry.label, `${itemContext}.label`),
        ports: list(entry.ports, `${itemContext}.ports`, numeric), protocol: text(entry.protocol, `${itemContext}.protocol`),
        state: text(entry.state, `${itemContext}.state`), detail: text(entry.detail, `${itemContext}.detail`),
        routeKind: entry.routeKind === undefined ? undefined : literal(entry.routeKind, ['Direct', 'Relay', 'Not applicable', 'Unknown'] as const, `${itemContext}.routeKind`),
        kind: optionalText(entry.kind, `${itemContext}.kind`) }
    }),
    control: {
      port: numeric(control.port, `${context}.control.port`), state: text(control.state, `${context}.control.state`),
      detail: text(control.detail, `${context}.control.detail`), remoteState: text(control.remoteState, `${context}.control.remoteState`),
      remoteDetail: text(control.remoteDetail, `${context}.control.remoteDetail`), bindAddress: optionalText(control.bindAddress, `${context}.control.bindAddress`),
      bindScope: optionalText(control.bindScope, `${context}.control.bindScope`), endpoint: optionalText(control.endpoint, `${context}.control.endpoint`),
      endpointState: optionalText(control.endpointState, `${context}.control.endpointState`), endpointDetail: optionalText(control.endpointDetail, `${context}.control.endpointDetail`),
      lanAddresses: control.lanAddresses === undefined ? undefined : list(control.lanAddresses, `${context}.control.lanAddresses`, (item, itemContext) => {
        const entry = object(item, itemContext ?? `${context}.control.lanAddresses`)
        return { address: text(entry.address, `${itemContext}.address`), interfaceName: text(entry.interfaceName, `${itemContext}.interfaceName`), gateway: text(entry.gateway, `${itemContext}.gateway`) }
      }),
      lanForwardDetail: optionalText(control.lanForwardDetail, `${context}.control.lanForwardDetail`)
    }
  }
}

export const parseInternetRouteCheck: Decoder<InternetRouteCheck> = (value, context = 'internet route check') => {
  const source = object(value, context)
  return { state: literal(source.state, ['Reachable', 'Not reachable', 'Inconclusive', 'Unavailable'] as const, `${context}.state`),
    detail: text(source.detail, `${context}.detail`), port: numeric(source.port, `${context}.port`), checkedUtc: text(source.checkedUtc, `${context}.checkedUtc`),
    endpoint: optionalNullableText(source.endpoint, `${context}.endpoint`) }
}

const parseServerPermission: Decoder<ServerPermission> = (value, context = 'server permission') => {
  const source = object(value, context)
  return { profileId: text(source.profileId, `${context}.profileId`), canStart: flag(source.canStart, `${context}.canStart`),
    canStop: flag(source.canStop, `${context}.canStop`), canExtendTimer: flag(source.canExtendTimer, `${context}.canExtendTimer`) }
}

const parseDevice: Decoder<Device> = (value, context = 'device') => {
  const source = object(value, context)
  return { id: text(source.id, `${context}.id`), profileId: text(source.profileId, `${context}.profileId`),
    assignedProfileIds: textList(source.assignedProfileIds, `${context}.assignedProfileIds`), name: text(source.name, `${context}.name`),
    canStart: flag(source.canStart, `${context}.canStart`), canStop: flag(source.canStop, `${context}.canStop`),
    canExtendTimer: flag(source.canExtendTimer, `${context}.canExtendTimer`), revoked: flag(source.revoked, `${context}.revoked`),
    paired: flag(source.paired, `${context}.paired`), approvalPending: flag(source.approvalPending, `${context}.approvalPending`),
    credentialExpiresUtc: nullableText(source.credentialExpiresUtc, `${context}.credentialExpiresUtc`),
    lastHeartbeatUtc: nullableText(source.lastHeartbeatUtc, `${context}.lastHeartbeatUtc`),
    serverPermissions: list(source.serverPermissions, `${context}.serverPermissions`, parseServerPermission) }
}

const parseCertificateState: Decoder<HostCertificateState> = (value, context = 'certificate state') => {
  const source = object(value, context)
  return { hostId: text(source.hostId, `${context}.hostId`), activeFingerprint: text(source.activeFingerprint, `${context}.activeFingerprint`),
    activeExpiresUtc: text(source.activeExpiresUtc, `${context}.activeExpiresUtc`), nextFingerprint: nullableText(source.nextFingerprint, `${context}.nextFingerprint`),
    nextExpiresUtc: nullableText(source.nextExpiresUtc, `${context}.nextExpiresUtc`), previousFingerprint: nullableText(source.previousFingerprint, `${context}.previousFingerprint`),
    previousAcceptedUntilUtc: nullableText(source.previousAcceptedUntilUtc, `${context}.previousAcceptedUntilUtc`) }
}

export const parseCompanionInfo: Decoder<CompanionInfo> = (value, context = 'companion information') => {
  const source = object(value, context)
  const route = object(source.route, `${context}.route`)
  return { listenerActive: flag(source.listenerActive, `${context}.listenerActive`), listenerWarning: nullableText(source.listenerWarning, `${context}.listenerWarning`),
    endpoint: text(source.endpoint, `${context}.endpoint`), fingerprint: nullableText(source.fingerprint, `${context}.fingerprint`),
    certificates: nullableObject(source.certificates, `${context}.certificates`, parseCertificateState),
    route: { mode: text(route.mode, `${context}.route.mode`), address: text(route.address, `${context}.route.address`) },
    devices: list(source.devices, `${context}.devices`, parseDevice),
    stopSafety: dictionary(source.stopSafety, `${context}.stopSafety`, (item, itemContext) => {
      const entry = object(item, itemContext ?? `${context}.stopSafety`)
      return { available: flag(entry.available, `${itemContext}.available`), reason: text(entry.reason, `${itemContext}.reason`) }
    }) }
}

export const parseGameEndpointResult: Decoder<GameEndpointResult> = (value, context = 'game endpoint result') => {
  const source = object(value, context)
  return { answered: flag(source.answered, `${context}.answered`), code: text(source.code, `${context}.code`), message: text(source.message, `${context}.message`),
    checkedUtc: text(source.checkedUtc, `${context}.checkedUtc`), onlinePlayers: nullableNumber(source.onlinePlayers, `${context}.onlinePlayers`),
    maxPlayers: nullableNumber(source.maxPlayers, `${context}.maxPlayers`) }
}

export const parseInviteState: Decoder<InviteState> = (value, context = 'invite state') => {
  const source = object(value, context)
  return { exists: flag(source.exists, `${context}.exists`), open: flag(source.open, `${context}.open`), canStart: flag(source.canStart, `${context}.canStart`),
    durationMinutes: numeric(source.durationMinutes, `${context}.durationMinutes`), deviceLimit: numeric(source.deviceLimit, `${context}.deviceLimit`),
    requireApproval: flag(source.requireApproval, `${context}.requireApproval`) }
}

export const parseInviteResult: Decoder<InviteResult> = (value, context = 'invite result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, password: optionalText(source.password, `${context}.password`), expiresUtc: optionalText(source.expiresUtc, `${context}.expiresUtc`),
    listenerActive: source.listenerActive === undefined ? undefined : flag(source.listenerActive, `${context}.listenerActive`),
    listenerWarning: optionalText(source.listenerWarning, `${context}.listenerWarning`) }
}

export const parsePasswordResult: Decoder<PasswordResult> = (value, context = 'password result') => {
  const source = object(value, context)
  const ok = flag(source.ok, `${context}.ok`)
  const password = optionalText(source.password, `${context}.password`)
  if (ok && password) return {
    ok: true,
    code: typeof source.code === 'string' ? source.code : 'PasswordRevealed',
    message: typeof source.message === 'string' ? source.message : 'Game password revealed.',
    password
  }
  const basic = parseBasicResult(source, context)
  return { ...basic, password }
}

export const parseWorldBackupList: Decoder<WorldBackupList> = (value, context = 'backup list') => {
  const source = object(value, context)
  return { backups: list(source.backups, `${context}.backups`, (item, itemContext) => {
    const entry = object(item, itemContext ?? `${context}.backups`)
    return { id: text(entry.id, `${itemContext}.id`), profileId: text(entry.profileId, `${itemContext}.profileId`), kind: text(entry.kind, `${itemContext}.kind`),
      worldId: text(entry.worldId, `${itemContext}.worldId`), backupKind: literal(entry.backupKind, ['Rolling', 'PreRestore'] as const, `${itemContext}.backupKind`),
      createdUtc: text(entry.createdUtc, `${itemContext}.createdUtc`), sizeBytes: numeric(entry.sizeBytes, `${itemContext}.sizeBytes`), fileCount: numeric(entry.fileCount, `${itemContext}.fileCount`) }
  }), status: parseBackupStatus(source.status, `${context}.status`) }
}

export const parseRouteDiscovery: Decoder<RouteDiscovery> = (value, context = 'route discovery') => {
  const source = object(value, context)
  const candidate: Decoder<RouteDiscovery['privateMeshCandidates'][number]> = (item, itemContext = 'route candidate') => {
    const entry = object(item, itemContext)
    return { provider: text(entry.provider, `${itemContext}.provider`), interfaceName: text(entry.interfaceName, `${itemContext}.interfaceName`),
      address: text(entry.address, `${itemContext}.address`) }
  }
  return { privateMeshCandidates: list(source.privateMeshCandidates, `${context}.privateMeshCandidates`, candidate),
    advancedCandidates: list(source.advancedCandidates, `${context}.advancedCandidates`, candidate) }
}

export const parseBrowseResult: Decoder<BrowseResult> = (value, context = 'browse result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, path: optionalNullableText(source.path, `${context}.path`) }
}

export const parseServerBrowseResult: Decoder<ServerBrowseResult> = (value, context = 'server browse result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, executablePath: optionalText(source.executablePath, `${context}.executablePath`) }
}

export const parseMinecraftBrowseResult: Decoder<MinecraftBrowseResult> = (value, context = 'Minecraft browse result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, path: optionalText(source.path, `${context}.path`) }
}

export const parseImportResult: Decoder<ImportResult> = (value, context = 'import result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, worldDirectory: nullableText(source.worldDirectory, `${context}.worldDirectory`) }
}

export const parseWorldBrowseResult: Decoder<WorldBrowseResult> = (value, context = 'world browse result') => {
  const { source, basic } = withBasicResult(value, context)
  return { ...basic, worldId: nullableText(source.worldId, `${context}.worldId`), sourceSaveRoot: nullableText(source.sourceSaveRoot, `${context}.sourceSaveRoot`),
    sourceFolder: text(source.sourceFolder, `${context}.sourceFolder`) }
}

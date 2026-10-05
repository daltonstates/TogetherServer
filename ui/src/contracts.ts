import type { Profile } from './GameProfile'
import type { InternetRouteCheck, PortDiagnostics } from './ServerReadiness'
import type { MinecraftDiscovery, MinecraftInstallation } from './MinecraftSetup'

export type Settings = {
  maxConcurrentServers: number
  idleMinutes: number
  friendTimerExtensionMinutes: number
  friendTimerExtensionMaximumMinutes: number
  autoShutdownEnabled: boolean
  keepAwakeWhileHosting: boolean
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
  addedShutdownMinutes: number
  playerObservationSource: string | null
  playerCountObservedUtc: string | null
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
  retainedSizeBytes: number
  availableSpaceBytes: number | null
}

export type WorldBackupRecord = {
  id: string
  profileId: string
  kind: string
  worldId: string
  backupKind: 'Rolling' | 'Manual' | 'PreRestore'
  createdUtc: string
  sizeBytes: number
  fileCount: number
}

export type WorldBackupList = { backups: WorldBackupRecord[]; status: WorldBackupStatus }
export type WorldBackupVerificationResult = BasicResult & { backupId: string; checkedUtc: string }

export type ServerSessionEndReason = 'GracefulStop' | 'ProcessExited' |
  'RecoveryProcessExitedBeforeReady' | 'OwnerArchivedExitedRun' | 'ProcessExitedBeforeRestore'
export type ServerSessionOutcome = 'GracefulStop' | 'UnexpectedExit' | 'FailedBeforeReady' |
  'RecoveryFailedBeforeReady' | 'OwnerArchivedExited' | 'ExitedBeforeRestore' | 'StopUnconfirmed'
export type ServerSessionBackupResult = 'Completed' | 'Failed' | 'NotConfigured' |
  'Unsupported' | 'NotAttempted'
export type RecentServerSession = {
  profileId: string
  operationId: string
  gameKind: 'Fixture' | 'Valheim' | 'MinecraftJava' | 'MinecraftBedrock' | 'Factorio' | 'Terraria' | 'Custom' | 'Unavailable'
  startedUtc: string | null
  endedUtc: string | null
  durationSeconds: number | null
  readyEverObserved: boolean | null
  endReason: ServerSessionEndReason | null
  outcome: ServerSessionOutcome | null
  crashRecoveryScheduled: boolean | null
  lastTrustedOnlinePlayers: number | null
  maximumTrustedOnlinePlayers: number | null
  backupResult: ServerSessionBackupResult | null
}
export type RecentServerSessionsResult = {
  ok: boolean
  code: string
  message: string
  profileId: string
  sessions: RecentServerSession[]
}

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

export type HostingPowerView = {
  settingEnabled: boolean
  managedServerRunning: boolean
  requestActive: boolean
  state: 'Disabled' | 'Waiting' | 'Active' | 'Unavailable'
  message: string
}

export type StorageLocationHealth = {
  id: string
  label: string
  state: 'Available' | 'Attention' | 'Unknown'
  detail: string
  availableSpaceBytes: number | null
  usedBytes: number
  measurementTruncated: boolean
}

export type StorageHealthView = {
  checkedUtc: string
  locations: StorageLocationHealth[]
  warnings: string[]
  resources: {
    logicalProcessors: number
    processWorkingSetBytes: number
    managedMemoryBytes: number
    totalAvailableMemoryBytes: number | null
    evidenceBoundary: string
  }
}

export type StartupRecoveryView = {
  previousSessionInterrupted: boolean
  previousStartedUtc: string | null
  items: { profileId: string; state: string; detail: string; canResume: boolean }[]
  message: string
  windowsRestartRegistered: boolean
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
  hostingPower?: HostingPowerView | null
  storageHealth?: StorageHealthView | null
  startupRecovery?: StartupRecoveryView | null
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
  canViewLogs: boolean
}

export type ServerPermission = { profileId: string; canStart: boolean; canStop: boolean; canExtendTimer: boolean; canViewLogs: boolean }

export type Device = {
  id: string
  profileId: string
  assignedProfileIds: string[]
  name: string
  canStart: boolean
  canStop: boolean
  canExtendTimer: boolean
  canViewLogs: boolean
  saveReceiveProfileIds?: string[]
  sharedWorldGrants?: Record<string, { receive: boolean; eligibleHost: boolean; recoveryVoter: boolean; manageSharing: boolean }>
  sharedWorldKeyEnrolled?: boolean
  revoked: boolean
  paired: boolean
  approvalPending: boolean
  credentialExpiresUtc: string | null
  lastHeartbeatUtc: string | null
  serverPermissions: ServerPermission[]
  accessExpiresUtc: string | null
  accessExpired: boolean
  temporaryHelperUntilUtc?: string | null
  temporaryHelperActive?: boolean
}

export type DeviceAccessDuration = 'OneHour' | 'EightHours' | 'OneDay' | 'SevenDays' | 'ThirtyDays' | 'NinetyDays'
export type DeviceAccessExpiryRequest =
  | { clear: true; accessExpiresUtc?: never; duration?: never }
  | { clear?: never; accessExpiresUtc: string; duration?: never }
  | { clear?: never; accessExpiresUtc?: never; duration: DeviceAccessDuration }
export type DeviceAccessExpiryResult = {
  ok: boolean
  code: string
  message: string
  accessExpiresUtc: string | null
  accessExpired: boolean
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
  listenerState: 'Off' | 'Idle' | 'Listening' | 'Error'
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
  hostCapabilities: string[]
  protocolCompatible?: boolean
  credentialExpiresUtc?: string | null
  certificateExpiresUtc?: string | null
  expiryWarning?: string | null
  routeMode?: string
  routeAddress?: string | null
  hostId?: string
  connectionName?: string | null
  activity?: ActivityEvent[]
  chatProfiles?: { id: string; name: string; supported: boolean }[]
}

export type Snapshot = HostSnapshot | FriendSnapshot
export type ChatEntry = { id: string; hostId: string; profileId: string; authorId: string; author: string; sentUtc: string; text: string; signature: string }
export type ChatDraft = { id: string; text: string }
export type ChatMember = { deviceId: string; name: string; allowed: boolean }
export type ChatRoomView = { ok: boolean; code: string; message: string; hostId: string; profileId: string; entries: ChatEntry[]; pending: ChatDraft[]; members: ChatMember[] | null }
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
export type MinecraftBrowseResult = BasicResult & { path?: string | null }
export type MinecraftInstallResult = BasicResult & { installation?: MinecraftInstallation }
export type WorldBrowseResult = BasicResult & { worldId: string | null; sourceSaveRoot: string | null; sourceFolder: string }
export type AppInstanceView = {
  kind: 'Production' | 'Staging'
  displayName: string
  isStaging: boolean
  freshWorldsOnly: boolean
  startupAvailable: boolean
  updatesAvailable: boolean
  localPort: number
  companionPort: number
  valheimPort: number
  minecraftJavaPort: number
  minecraftBedrockPort: number
  dataRoot: string
  dataIsolation: string
}
export type UpdateView = { state: 'Checking' | 'Current' | 'Available' | 'NoRelease' | 'Unavailable' | 'Unsupported'; currentVersion: string; latestVersion: string | null; message: string; publisherTrust: string }
export type DesktopPreferences = { available: boolean; launchAtLogin: boolean; closeToTray: boolean; startupAvailable: boolean }
export type DesktopPreferenceResult = BasicResult & { preferences: DesktopPreferences }
export type FriendIssue = { code: string; message: string }
export type BrowseResult = BasicResult & { path?: string | null }
export type CustomScriptBundle = { start: string; status: string; stop: string }
export type CustomScriptResult = BasicResult & { scripts: CustomScriptBundle }
export type CustomCertificationResult = BasicResult & { snapshot: HostSnapshot; certification: CustomCertificationState }
export type RouteDiscovery = { privateMeshCandidates: { provider: string; interfaceName: string; address: string }[]; advancedCandidates: { provider: string; interfaceName: string; address: string }[] }
export type GameEndpointResult = { answered: boolean; code: string; message: string; checkedUtc: string; onlinePlayers: number | null; maxPlayers: number | null }
export type InviteState = { exists: boolean; open: boolean; canStart: boolean; requireApproval: boolean }
export type InviteResult = BasicResult & { password?: string; expiresUtc?: string | null; listenerActive?: boolean; listenerWarning?: string | null }
export type PasswordResult = BasicResult & { password?: string }
export type BackupSafetyResult = BasicResult & { backupId: string; completedUtc: string; fileCount: number; sizeBytes: number }
export type HostMoveKitResult = BasicResult & { kit: { version: number; kind: string; name: string; worldId: string; gamePort: number; backupId: string; backupCreatedUtc: string } | null; fileCount: number; sizeBytes: number }
export type FactorioImportResult = BasicResult & { worldId: string | null; worldDirectory: string | null }
export type TerrariaImportResult = BasicResult & { worldId: string | null; worldDirectory: string | null }
export type AcceptanceCheckView = { id: string; label: string; evidence: string; confirmed: boolean; confirmedUtc: string | null }
export type AcceptanceView = { profileId: string; stale: boolean; updatedUtc: string | null; checks: AcceptanceCheckView[]; evidenceBoundary: string; gameFilesAvailable: boolean; gameFilesChanged: boolean }
export type AcceptanceResult = BasicResult & { view: AcceptanceView }

export type OwnerDiagnosticTone = 'Neutral' | 'Attention' | 'Error'
export type OwnerDiagnosticCheck = {
  id: string
  label: string
  state: string
  detail: string
  nextAction: string
  location: string
  tone: OwnerDiagnosticTone
  observedUtc: string | null
}
export type OwnerServerDiagnostics = {
  profileId: string
  label: string
  kind: string
  checks: OwnerDiagnosticCheck[]
}
export type OwnerDiagnosticsView = {
  generatedUtc: string
  evidenceBoundary: string
  servers: OwnerServerDiagnostics[]
  sharedChecks: OwnerDiagnosticCheck[]
  truncated: boolean
}
export type SupportReportExport = {
  fileName: 'TogetherServer-support-report.json'
  contentType: 'application/json; charset=utf-8'
  content: string
  sizeBytes: number
}

export type ServerLogSourceState = 'Active' | 'Ended' | 'Missing' | 'Unsupported' | 'Unavailable'
export type ServerLogRecord = {
  timestampUtc: string | null
  severity: 'Info' | 'Warning' | 'Error'
  category: string
  stream: string
  message: string
}
export type ServerLogResult = {
  ok: boolean
  code: string
  message: string
  sourceState: ServerLogSourceState
  runId: string | null
  records: ServerLogRecord[]
  cursor: string | null
  hasMore: boolean
}

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

function boundedText(value: unknown, context: string, maximum: number, allowEmpty = false): string {
  const parsed = text(value, context)
  if ((!allowEmpty && parsed.length === 0) || parsed.length > maximum || [...parsed].some(character => character < ' ' && character !== '\t'))
    throw new ContractError(`${context} is outside its supported bounds.`)
  return parsed
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

function utcTimestamp(value: unknown, context: string): string {
  const parsed = text(value, context)
  if (!/(?:Z|\+00:00)$/i.test(parsed) || !Number.isFinite(Date.parse(parsed)))
    throw new ContractError(`${context} must be an explicit UTC timestamp.`)
  return parsed
}

function nullableUtcTimestamp(value: unknown, context: string): string | null {
  return value === null ? null : utcTimestamp(value, context)
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
  const kind = literal(source.kind, ['Fixture', 'Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria', 'Custom'] as const, `${context}.kind`)
  const worldSource = literal(source.worldSource, ['Existing', 'New'] as const, `${context}.worldSource`)
  const minecraft = source.minecraft === undefined ? undefined : source.minecraft === null ? null : (() => {
    const item = object(source.minecraft, `${context}.minecraft`)
    return { serverJarPath: text(item.serverJarPath, `${context}.minecraft.serverJarPath`) }
  })()
  const factorio = source.factorio === undefined ? undefined : source.factorio === null ? null : (() => {
    const item = object(source.factorio, `${context}.factorio`)
    return { rconPort: numeric(item.rconPort, `${context}.factorio.rconPort`) }
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
    sharedSavesEnabled: source.sharedSavesEnabled === undefined ? false : flag(source.sharedSavesEnabled, `${context}.sharedSavesEnabled`),
    worldLoadRehearsalId: source.worldLoadRehearsalId == null ? null : text(source.worldLoadRehearsalId, `${context}.worldLoadRehearsalId`),
    worldSource, worldDirectory: text(source.worldDirectory, `${context}.worldDirectory`),
    gamePort: numeric(source.gamePort, `${context}.gamePort`), executablePath: text(source.executablePath, `${context}.executablePath`),
    minecraft, factorio, custom, crashRecovery, backups, maintenance
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
    keepAwakeWhileHosting: source.keepAwakeWhileHosting === undefined ? false : flag(source.keepAwakeWhileHosting, `${context}.keepAwakeWhileHosting`),
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
    friendAddedMinutes: numeric(source.friendAddedMinutes, `${context}.friendAddedMinutes`),
    addedShutdownMinutes: source.addedShutdownMinutes === undefined ? 0 : numeric(source.addedShutdownMinutes, `${context}.addedShutdownMinutes`),
    playerObservationSource: source.playerObservationSource === undefined ? null : nullableText(source.playerObservationSource, `${context}.playerObservationSource`),
    playerCountObservedUtc: source.playerCountObservedUtc === undefined ? null : nullableText(source.playerCountObservedUtc, `${context}.playerCountObservedUtc`)
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
    completedCount: numeric(source.completedCount, `${context}.completedCount`),
    retainedSizeBytes: numeric(source.retainedSizeBytes, `${context}.retainedSizeBytes`),
    availableSpaceBytes: nullableNumber(source.availableSpaceBytes, `${context}.availableSpaceBytes`)
  }
}

const parseHostingPower: Decoder<HostingPowerView> = (value, context = 'hosting power') => {
  const source = object(value, context)
  return {
    settingEnabled: flag(source.settingEnabled, `${context}.settingEnabled`),
    managedServerRunning: flag(source.managedServerRunning, `${context}.managedServerRunning`),
    requestActive: flag(source.requestActive, `${context}.requestActive`),
    state: literal(source.state, ['Disabled', 'Waiting', 'Active', 'Unavailable'] as const, `${context}.state`),
    message: text(source.message, `${context}.message`)
  }
}

const parseStorageHealth: Decoder<StorageHealthView> = (value, context = 'storage health') => {
  const source = object(value, context)
  const resources = object(source.resources, `${context}.resources`)
  return {
    checkedUtc: utcTimestamp(source.checkedUtc, `${context}.checkedUtc`),
    locations: list(source.locations, `${context}.locations`, (item, itemContext) => {
      const location = object(item, itemContext ?? `${context}.locations`)
      return {
        id: text(location.id, `${itemContext}.id`), label: text(location.label, `${itemContext}.label`),
        state: literal(location.state, ['Available', 'Attention', 'Unknown'] as const, `${itemContext}.state`),
        detail: text(location.detail, `${itemContext}.detail`),
        availableSpaceBytes: nullableNumber(location.availableSpaceBytes, `${itemContext}.availableSpaceBytes`),
        usedBytes: numeric(location.usedBytes, `${itemContext}.usedBytes`),
        measurementTruncated: flag(location.measurementTruncated, `${itemContext}.measurementTruncated`)
      }
    }),
    warnings: textList(source.warnings, `${context}.warnings`),
    resources: {
      logicalProcessors: numeric(resources.logicalProcessors, `${context}.resources.logicalProcessors`),
      processWorkingSetBytes: numeric(resources.processWorkingSetBytes, `${context}.resources.processWorkingSetBytes`),
      managedMemoryBytes: numeric(resources.managedMemoryBytes, `${context}.resources.managedMemoryBytes`),
      totalAvailableMemoryBytes: nullableNumber(resources.totalAvailableMemoryBytes, `${context}.resources.totalAvailableMemoryBytes`),
      evidenceBoundary: text(resources.evidenceBoundary, `${context}.resources.evidenceBoundary`)
    }
  }
}

const parseStartupRecovery: Decoder<StartupRecoveryView> = (value, context = 'startup recovery') => {
  const source = object(value, context)
  return {
    previousSessionInterrupted: flag(source.previousSessionInterrupted, `${context}.previousSessionInterrupted`),
    previousStartedUtc: nullableUtcTimestamp(source.previousStartedUtc, `${context}.previousStartedUtc`),
    items: list(source.items, `${context}.items`, (item, itemContext) => {
      const recovery = object(item, itemContext ?? `${context}.items`)
      return { profileId: text(recovery.profileId, `${itemContext}.profileId`),
        state: text(recovery.state, `${itemContext}.state`), detail: text(recovery.detail, `${itemContext}.detail`),
        canResume: flag(recovery.canResume, `${itemContext}.canResume`) }
    }),
    message: text(source.message, `${context}.message`),
    windowsRestartRegistered: flag(source.windowsRestartRegistered, `${context}.windowsRestartRegistered`)
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
    recovery: source.recovery === undefined ? undefined : source.recovery === null ? null : parseDataRecoveryView(source.recovery, `${context}.recovery`),
    hostingPower: source.hostingPower === undefined ? undefined : source.hostingPower === null ? null : parseHostingPower(source.hostingPower, `${context}.hostingPower`),
    storageHealth: source.storageHealth === undefined ? undefined : source.storageHealth === null ? null : parseStorageHealth(source.storageHealth, `${context}.storageHealth`),
    startupRecovery: source.startupRecovery === undefined ? undefined : source.startupRecovery === null ? null : parseStartupRecovery(source.startupRecovery, `${context}.startupRecovery`)
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
    timerExtensionRemainingMinutes: numeric(source.timerExtensionRemainingMinutes, `${context}.timerExtensionRemainingMinutes`),
    canViewLogs: flag(source.canViewLogs, `${context}.canViewLogs`)
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
    hostCapabilities: source.hostCapabilities === undefined || source.hostCapabilities === null
      ? [] : textList(source.hostCapabilities, `${context}.hostCapabilities`),
    protocolCompatible: source.protocolCompatible === undefined ? undefined : flag(source.protocolCompatible, `${context}.protocolCompatible`),
    credentialExpiresUtc: optionalNullableText(source.credentialExpiresUtc, `${context}.credentialExpiresUtc`),
    certificateExpiresUtc: optionalNullableText(source.certificateExpiresUtc, `${context}.certificateExpiresUtc`),
    expiryWarning: optionalNullableText(source.expiryWarning, `${context}.expiryWarning`), routeMode: optionalText(source.routeMode, `${context}.routeMode`),
    routeAddress: optionalNullableText(source.routeAddress, `${context}.routeAddress`), hostId: optionalText(source.hostId, `${context}.hostId`),
    connectionName: optionalNullableText(source.connectionName, `${context}.connectionName`),
    activity: source.activity === undefined || source.activity === null ? undefined : list(source.activity, `${context}.activity`, parseActivity),
    chatProfiles: source.chatProfiles === undefined || source.chatProfiles === null ? undefined :
      list(source.chatProfiles, `${context}.chatProfiles`, (item, itemContext) => {
        const profile = object(item, itemContext ?? `${context}.chatProfiles`)
        return { id: text(profile.id, `${itemContext}.id`), name: text(profile.name, `${itemContext}.name`),
          supported: flag(profile.supported, `${itemContext}.supported`) }
      })
  }
}

export const parseSnapshot: Decoder<Snapshot> = (value, context = 'snapshot') => {
  const source = object(value, context)
  if (source.mode === 'Host') return parseHostSnapshot(value, context)
  if (source.mode === 'Friend') return parseFriendSnapshotInternal(value, context, 0)
  throw new ContractError(`${context}.mode must be Host or Friend.`)
}

export const parseFriendSnapshot: Decoder<FriendSnapshot> = (value, context = 'friend snapshot') => parseFriendSnapshotInternal(value, context, 0)

export const parseChatRoomView: Decoder<ChatRoomView> = (value, context = 'chat room') => {
  const source = object(value, context)
  const entries = list(source.entries, `${context}.entries`, (item, itemContext) => {
    const entry = object(item, itemContext ?? `${context}.entries`)
    return {
      id: text(entry.id, `${itemContext}.id`), hostId: text(entry.hostId, `${itemContext}.hostId`),
      profileId: text(entry.profileId, `${itemContext}.profileId`), authorId: text(entry.authorId, `${itemContext}.authorId`),
      author: text(entry.author, `${itemContext}.author`), sentUtc: text(entry.sentUtc, `${itemContext}.sentUtc`),
      text: text(entry.text, `${itemContext}.text`), signature: text(entry.signature, `${itemContext}.signature`)
    }
  })
  const pending = list(source.pending, `${context}.pending`, (item, itemContext) => {
    const draft = object(item, itemContext ?? `${context}.pending`)
    return { id: text(draft.id, `${itemContext}.id`), text: text(draft.text, `${itemContext}.text`) }
  })
  const members = source.members === null || source.members === undefined ? null :
    list(source.members, `${context}.members`, (item, itemContext) => {
      const member = object(item, itemContext ?? `${context}.members`)
      return { deviceId: text(member.deviceId, `${itemContext}.deviceId`),
        name: text(member.name, `${itemContext}.name`), allowed: flag(member.allowed, `${itemContext}.allowed`) }
    })
  if (entries.length > 200 || pending.length > 20 || entries.some(entry => entry.text.length > 500) ||
      pending.some(draft => draft.text.length > 500))
    throw new ContractError(`${context} exceeded chat limits.`)
  return { ok: flag(source.ok, `${context}.ok`), code: text(source.code, `${context}.code`),
    message: text(source.message, `${context}.message`), hostId: text(source.hostId, `${context}.hostId`),
    profileId: text(source.profileId, `${context}.profileId`), entries, pending, members }
}

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

export const parseServerLogResult: Decoder<ServerLogResult> = (value, context = 'server log result') => {
  const source = object(value, context)
  if (!Array.isArray(source.records)) throw new ContractError(`${context}.records must be a list.`)
  if (source.records.length > 200) throw new ContractError(`${context}.records has too many entries.`)
  const records = list(source.records, `${context}.records`, (item, itemContext = `${context}.records`) => {
    const record = object(item, itemContext)
    const rawTimestamp = nullableText(record.timestampUtc, `${itemContext}.timestampUtc`)
    const timestampUtc = rawTimestamp === null ? null :
      boundedText(rawTimestamp, `${itemContext}.timestampUtc`, 40)
    if (timestampUtc !== null && !Number.isFinite(Date.parse(timestampUtc)))
      throw new ContractError(`${itemContext}.timestampUtc must be a timestamp or null.`)
    return {
      timestampUtc,
      severity: literal(record.severity, ['Info', 'Warning', 'Error'] as const, `${itemContext}.severity`),
      category: boundedText(record.category, `${itemContext}.category`, 32),
      stream: boundedText(record.stream, `${itemContext}.stream`, 16),
      message: boundedText(record.message, `${itemContext}.message`, 2048, true)
    }
  })
  const runId = nullableText(source.runId, `${context}.runId`)
  if (runId !== null && (runId.length !== 32 || !/^[0-9a-f]{32}$/i.test(runId)))
    throw new ContractError(`${context}.runId is invalid.`)
  const cursor = nullableText(source.cursor, `${context}.cursor`)
  if (cursor !== null && (cursor.length === 0 || cursor.length > 160)) throw new ContractError(`${context}.cursor is invalid.`)
  return {
    ok: flag(source.ok, `${context}.ok`),
    code: boundedText(source.code, `${context}.code`, 80),
    message: boundedText(source.message, `${context}.message`, 600),
    sourceState: literal(source.sourceState, ['Active', 'Ended', 'Missing', 'Unsupported', 'Unavailable'] as const, `${context}.sourceState`),
    runId,
    records,
    cursor,
    hasMore: flag(source.hasMore, `${context}.hasMore`)
  }
}

export const parseRecentServerSessions: Decoder<RecentServerSessionsResult> = (value, context = 'recent sessions') => {
  const source = object(value, context)
  const profileId = boundedText(source.profileId, `${context}.profileId`, 36)
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(profileId))
    throw new ContractError(`${context}.profileId is invalid.`)
  if (!Array.isArray(source.sessions) || source.sessions.length > 20)
    throw new ContractError(`${context}.sessions is outside its supported bounds.`)

  const boundedInteger = (entry: unknown, entryContext: string, maximum: number): number | null => {
    if (entry === null) return null
    const parsed = numeric(entry, entryContext)
    if (!Number.isSafeInteger(parsed) || parsed < 0 || parsed > maximum)
      throw new ContractError(`${entryContext} is outside its supported bounds.`)
    return parsed
  }
  const nullableFlag = (entry: unknown, entryContext: string): boolean | null =>
    entry === null ? null : flag(entry, entryContext)

  const sessions = list(source.sessions, `${context}.sessions`, (value, itemContext = `${context}.sessions`) => {
    const item = object(value, itemContext)
    const itemProfileId = boundedText(item.profileId, `${itemContext}.profileId`, 36)
    const operationId = boundedText(item.operationId, `${itemContext}.operationId`, 36)
    if (itemProfileId !== profileId ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(operationId))
      throw new ContractError(`${itemContext} has an invalid run identity.`)
    const startedUtc = nullableUtcTimestamp(item.startedUtc, `${itemContext}.startedUtc`)
    const endedUtc = nullableUtcTimestamp(item.endedUtc, `${itemContext}.endedUtc`)
    const durationSeconds = boundedInteger(item.durationSeconds, `${itemContext}.durationSeconds`, 3_155_760_000)
    const readyEverObserved = nullableFlag(item.readyEverObserved, `${itemContext}.readyEverObserved`)
    const crashRecoveryScheduled = nullableFlag(item.crashRecoveryScheduled, `${itemContext}.crashRecoveryScheduled`)
    const lastTrustedOnlinePlayers = boundedInteger(item.lastTrustedOnlinePlayers, `${itemContext}.lastTrustedOnlinePlayers`, 1_000_000)
    const maximumTrustedOnlinePlayers = boundedInteger(item.maximumTrustedOnlinePlayers, `${itemContext}.maximumTrustedOnlinePlayers`, 1_000_000)
    if ((lastTrustedOnlinePlayers === null) !== (maximumTrustedOnlinePlayers === null) ||
        (lastTrustedOnlinePlayers !== null && maximumTrustedOnlinePlayers! < lastTrustedOnlinePlayers))
      throw new ContractError(`${itemContext} has inconsistent trusted player observations.`)
    const endReason = item.endReason === null ? null : literal(item.endReason,
      ['GracefulStop', 'ProcessExited', 'RecoveryProcessExitedBeforeReady', 'OwnerArchivedExitedRun', 'ProcessExitedBeforeRestore'] as const,
      `${itemContext}.endReason`)
    const outcome = item.outcome === null ? null : literal(item.outcome,
      ['GracefulStop', 'UnexpectedExit', 'FailedBeforeReady', 'RecoveryFailedBeforeReady', 'OwnerArchivedExited', 'ExitedBeforeRestore', 'StopUnconfirmed'] as const,
      `${itemContext}.outcome`)
    const backupResult = item.backupResult === null ? null : literal(item.backupResult,
      ['Completed', 'Failed', 'NotConfigured', 'Unsupported', 'NotAttempted'] as const,
      `${itemContext}.backupResult`)
    const legacy = outcome === null
    const incompleteLegacy = legacy && (startedUtc !== null || endedUtc !== null ||
      durationSeconds !== null || lastTrustedOnlinePlayers !== null)
    const incompleteCurrent = !legacy && (startedUtc === null || endedUtc === null || durationSeconds === null)
    const invalidDuration = durationSeconds !== null && (startedUtc === null || endedUtc === null ||
      Date.parse(endedUtc) < Date.parse(startedUtc) ||
      Math.abs(durationSeconds - Math.floor((Date.parse(endedUtc) - Date.parse(startedUtc)) / 1000)) > 1)
    const expectedReason: Record<ServerSessionOutcome, ServerSessionEndReason> = {
      GracefulStop: 'GracefulStop',
      UnexpectedExit: 'ProcessExited',
      FailedBeforeReady: 'ProcessExited',
      RecoveryFailedBeforeReady: 'RecoveryProcessExitedBeforeReady',
      OwnerArchivedExited: 'OwnerArchivedExitedRun',
      ExitedBeforeRestore: 'ProcessExitedBeforeRestore',
      StopUnconfirmed: 'ProcessExited'
    }
    if ((endReason === null) !== legacy || (backupResult === null) !== legacy ||
        (readyEverObserved === null) !== legacy || (crashRecoveryScheduled === null) !== legacy ||
        incompleteLegacy || incompleteCurrent || invalidDuration ||
        outcome !== null && endReason !== expectedReason[outcome])
      throw new ContractError(`${itemContext} has an incomplete summary contract.`)
    return {
      profileId: itemProfileId,
      operationId,
      gameKind: literal(item.gameKind,
        ['Fixture', 'Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria', 'Custom', 'Unavailable'] as const,
        `${itemContext}.gameKind`),
      startedUtc,
      endedUtc,
      durationSeconds,
      readyEverObserved,
      endReason,
      outcome,
      crashRecoveryScheduled,
      lastTrustedOnlinePlayers,
      maximumTrustedOnlinePlayers,
      backupResult
    }
  })
  return {
    ok: flag(source.ok, `${context}.ok`),
    code: boundedText(source.code, `${context}.code`, 80),
    message: boundedText(source.message, `${context}.message`, 600),
    profileId,
    sessions
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

export const parseAppInstance: Decoder<AppInstanceView> = (value, context = 'app instance') => {
  const source = object(value, context)
  return {
    kind: literal(source.kind, ['Production', 'Staging'] as const, `${context}.kind`),
    displayName: text(source.displayName, `${context}.displayName`),
    isStaging: flag(source.isStaging, `${context}.isStaging`),
    freshWorldsOnly: flag(source.freshWorldsOnly, `${context}.freshWorldsOnly`),
    startupAvailable: flag(source.startupAvailable, `${context}.startupAvailable`),
    updatesAvailable: flag(source.updatesAvailable, `${context}.updatesAvailable`),
    localPort: numeric(source.localPort, `${context}.localPort`),
    companionPort: numeric(source.companionPort, `${context}.companionPort`),
    valheimPort: numeric(source.valheimPort, `${context}.valheimPort`),
    minecraftJavaPort: numeric(source.minecraftJavaPort, `${context}.minecraftJavaPort`),
    minecraftBedrockPort: numeric(source.minecraftBedrockPort, `${context}.minecraftBedrockPort`),
    dataRoot: text(source.dataRoot, `${context}.dataRoot`),
    dataIsolation: text(source.dataIsolation, `${context}.dataIsolation`)
  }
}

export const parseUpdateView: Decoder<UpdateView> = (value, context = 'update status') => {
  const source = object(value, context)
  return { state: literal(source.state, ['Checking', 'Current', 'Available', 'NoRelease', 'Unavailable', 'Unsupported'] as const, `${context}.state`),
    currentVersion: text(source.currentVersion, `${context}.currentVersion`), latestVersion: nullableText(source.latestVersion, `${context}.latestVersion`),
    message: text(source.message, `${context}.message`),
    publisherTrust: source.publisherTrust === undefined ? 'Unknown' : text(source.publisherTrust, `${context}.publisherTrust`) }
}

const parseOwnerDiagnosticCheck: Decoder<OwnerDiagnosticCheck> = (value, context = 'diagnostic check') => {
  const source = object(value, context)
  const observedUtc = nullableText(source.observedUtc, `${context}.observedUtc`)
  if (observedUtc !== null && !Number.isFinite(Date.parse(observedUtc)))
    throw new ContractError(`${context}.observedUtc must be a timestamp or null.`)
  return {
    id: boundedText(source.id, `${context}.id`, 64),
    label: boundedText(source.label, `${context}.label`, 120),
    state: boundedText(source.state, `${context}.state`, 120),
    detail: boundedText(source.detail, `${context}.detail`, 500),
    nextAction: boundedText(source.nextAction, `${context}.nextAction`, 400),
    location: boundedText(source.location, `${context}.location`, 120),
    tone: literal(source.tone, ['Neutral', 'Attention', 'Error'] as const, `${context}.tone`),
    observedUtc
  }
}

export const parseOwnerDiagnostics: Decoder<OwnerDiagnosticsView> = (value, context = 'owner diagnostics') => {
  const source = object(value, context)
  const generatedUtc = boundedText(source.generatedUtc, `${context}.generatedUtc`, 64)
  if (!Number.isFinite(Date.parse(generatedUtc))) throw new ContractError(`${context}.generatedUtc must be a timestamp.`)
  const servers = list(source.servers, `${context}.servers`, (item, itemContext = `${context}.servers`) => {
    const server = object(item, itemContext)
    const checks = list(server.checks, `${itemContext}.checks`, parseOwnerDiagnosticCheck)
    if (checks.length > 8) throw new ContractError(`${itemContext}.checks has too many entries.`)
    return {
      profileId: boundedText(server.profileId, `${itemContext}.profileId`, 64),
      label: boundedText(server.label, `${itemContext}.label`, 100),
      kind: boundedText(server.kind, `${itemContext}.kind`, 40), checks
    }
  })
  if (servers.length > 64) throw new ContractError(`${context}.servers has too many entries.`)
  const sharedChecks = list(source.sharedChecks, `${context}.sharedChecks`, parseOwnerDiagnosticCheck)
  if (sharedChecks.length > 12) throw new ContractError(`${context}.sharedChecks has too many entries.`)
  return {
    generatedUtc,
    evidenceBoundary: boundedText(source.evidenceBoundary, `${context}.evidenceBoundary`, 600),
    servers, sharedChecks, truncated: flag(source.truncated, `${context}.truncated`)
  }
}

export const parseSupportReportExport: Decoder<SupportReportExport> = (value, context = 'support report') => {
  const source = object(value, context)
  if (source.fileName !== 'TogetherServer-support-report.json')
    throw new ContractError(`${context}.fileName is not the fixed safe filename.`)
  if (source.contentType !== 'application/json; charset=utf-8')
    throw new ContractError(`${context}.contentType is unsupported.`)
  const content = text(source.content, `${context}.content`)
  if (content.length === 0 || content.length > 128 * 1024 ||
      [...content].some(character => character < ' ' && character !== '\r' && character !== '\n' && character !== '\t'))
    throw new ContractError(`${context}.content is outside its supported bounds.`)
  const sizeBytes = numeric(source.sizeBytes, `${context}.sizeBytes`)
  if (!Number.isSafeInteger(sizeBytes) || sizeBytes < 1 || sizeBytes > 128 * 1024)
    throw new ContractError(`${context}.sizeBytes is outside its supported bounds.`)
  if (new TextEncoder().encode(content).byteLength !== sizeBytes)
    throw new ContractError(`${context}.sizeBytes does not match its UTF-8 content.`)
  return { fileName: source.fileName, contentType: source.contentType, content, sizeBytes }
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
      bindScope: optionalText(control.bindScope, `${context}.control.bindScope`), endpoint: optionalNullableText(control.endpoint, `${context}.control.endpoint`),
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
    canStop: flag(source.canStop, `${context}.canStop`), canExtendTimer: flag(source.canExtendTimer, `${context}.canExtendTimer`),
    canViewLogs: flag(source.canViewLogs, `${context}.canViewLogs`) }
}

const parseDevice: Decoder<Device> = (value, context = 'device') => {
  const source = object(value, context)
  const accessExpiresUtc = nullableUtcTimestamp(source.accessExpiresUtc, `${context}.accessExpiresUtc`)
  const accessExpired = flag(source.accessExpired, `${context}.accessExpired`)
  const temporaryHelperUntilUtc = source.temporaryHelperUntilUtc === undefined ? null :
    nullableUtcTimestamp(source.temporaryHelperUntilUtc, `${context}.temporaryHelperUntilUtc`)
  const temporaryHelperActive = source.temporaryHelperActive === undefined ? false :
    flag(source.temporaryHelperActive, `${context}.temporaryHelperActive`)
  if (accessExpired && accessExpiresUtc === null)
    throw new ContractError(`${context}.accessExpired requires an access deadline.`)
  if (temporaryHelperActive && temporaryHelperUntilUtc === null)
    throw new ContractError(`${context}.temporaryHelperActive requires a deadline.`)
  return { id: text(source.id, `${context}.id`), profileId: text(source.profileId, `${context}.profileId`),
    assignedProfileIds: textList(source.assignedProfileIds, `${context}.assignedProfileIds`), name: text(source.name, `${context}.name`),
    canStart: flag(source.canStart, `${context}.canStart`), canStop: flag(source.canStop, `${context}.canStop`),
    canExtendTimer: flag(source.canExtendTimer, `${context}.canExtendTimer`), canViewLogs: flag(source.canViewLogs, `${context}.canViewLogs`),
    saveReceiveProfileIds: source.saveReceiveProfileIds === undefined || source.saveReceiveProfileIds === null ? [] :
      textList(source.saveReceiveProfileIds, `${context}.saveReceiveProfileIds`),
    sharedWorldGrants: source.sharedWorldGrants === undefined || source.sharedWorldGrants === null ? {} :
      Object.fromEntries(Object.entries(object(source.sharedWorldGrants, `${context}.sharedWorldGrants`)).map(([id, value]) => {
        const grants = object(value, `${context}.sharedWorldGrants.${id}`)
        return [id, { receive: flag(grants.receive, 'Receive'), eligibleHost: flag(grants.eligibleHost, 'Eligible host'),
          recoveryVoter: flag(grants.recoveryVoter, 'Recovery voter'), manageSharing: flag(grants.manageSharing, 'Manage sharing') }]
      })),
    sharedWorldKeyEnrolled: source.sharedWorldKeyEnrolled === undefined ? false :
      flag(source.sharedWorldKeyEnrolled, `${context}.sharedWorldKeyEnrolled`),
    revoked: flag(source.revoked, `${context}.revoked`),
    paired: flag(source.paired, `${context}.paired`), approvalPending: flag(source.approvalPending, `${context}.approvalPending`),
    credentialExpiresUtc: nullableText(source.credentialExpiresUtc, `${context}.credentialExpiresUtc`),
    lastHeartbeatUtc: nullableText(source.lastHeartbeatUtc, `${context}.lastHeartbeatUtc`),
    serverPermissions: list(source.serverPermissions, `${context}.serverPermissions`, parseServerPermission),
    accessExpiresUtc, accessExpired, temporaryHelperUntilUtc, temporaryHelperActive }
}

export const parseDeviceAccessExpiryResult: Decoder<DeviceAccessExpiryResult> = (value, context = 'device access expiry result') => {
  const source = object(value, context)
  const accessExpiresUtc = nullableUtcTimestamp(source.accessExpiresUtc, `${context}.accessExpiresUtc`)
  const accessExpired = flag(source.accessExpired, `${context}.accessExpired`)
  if (accessExpired && accessExpiresUtc === null)
    throw new ContractError(`${context}.accessExpired requires an access deadline.`)
  return {
    ok: flag(source.ok, `${context}.ok`),
    code: boundedText(source.code, `${context}.code`, 80),
    message: boundedText(source.message, `${context}.message`, 600),
    accessExpiresUtc,
    accessExpired
  }
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
  return { listenerActive: flag(source.listenerActive, `${context}.listenerActive`),
    listenerState: literal(source.listenerState, ['Off', 'Idle', 'Listening', 'Error'] as const,
      `${context}.listenerState`),
    listenerWarning: nullableText(source.listenerWarning, `${context}.listenerWarning`),
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
    requireApproval: flag(source.requireApproval, `${context}.requireApproval`) }
}

export const parseInviteResult: Decoder<InviteResult> = (value, context = 'invite result') => {
  const { source, basic } = withBasicResult(value, context)
  const password = optionalText(source.password, `${context}.password`)
  if (basic.ok && !password) throw new ContractError(`${context}.password must be a non-empty string when the server code is ready`)
  return { ...basic, password, expiresUtc: optionalNullableText(source.expiresUtc, `${context}.expiresUtc`),
    listenerActive: source.listenerActive === undefined ? undefined : flag(source.listenerActive, `${context}.listenerActive`),
    listenerWarning: optionalNullableText(source.listenerWarning, `${context}.listenerWarning`) }
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
      worldId: text(entry.worldId, `${itemContext}.worldId`), backupKind: literal(entry.backupKind, ['Rolling', 'Manual', 'PreRestore'] as const, `${itemContext}.backupKind`),
      createdUtc: text(entry.createdUtc, `${itemContext}.createdUtc`), sizeBytes: numeric(entry.sizeBytes, `${itemContext}.sizeBytes`), fileCount: numeric(entry.fileCount, `${itemContext}.fileCount`) }
  }), status: parseBackupStatus(source.status, `${context}.status`) }
}

export const parseWorldBackupVerificationResult: Decoder<WorldBackupVerificationResult> = (value, context = 'backup verification') => {
  const source = object(value, context)
  return { ...parseBasicResult(source, context), backupId: text(source.backupId, `${context}.backupId`),
    checkedUtc: text(source.checkedUtc, `${context}.checkedUtc`) }
}

export const parseBackupSafetyResult: Decoder<BackupSafetyResult> = (value, context = 'backup safety result') => {
  const source = object(value, context)
  return { ...parseBasicResult(source, context), backupId: text(source.backupId, `${context}.backupId`),
    completedUtc: utcTimestamp(source.completedUtc, `${context}.completedUtc`),
    fileCount: numeric(source.fileCount, `${context}.fileCount`), sizeBytes: numeric(source.sizeBytes, `${context}.sizeBytes`) }
}

export const parseHostMoveKitResult: Decoder<HostMoveKitResult> = (value, context = 'Host move kit') => {
  const source = object(value, context)
  const kit = source.kit == null ? null : object(source.kit, `${context}.kit`)
  return { ...parseBasicResult(source, context), kit: kit && {
    version: numeric(kit.version, `${context}.kit.version`), kind: text(kit.kind, `${context}.kit.kind`),
    name: text(kit.name, `${context}.kit.name`), worldId: text(kit.worldId, `${context}.kit.worldId`),
    gamePort: numeric(kit.gamePort, `${context}.kit.gamePort`), backupId: text(kit.backupId, `${context}.kit.backupId`),
    backupCreatedUtc: utcTimestamp(kit.backupCreatedUtc, `${context}.kit.backupCreatedUtc`)
  }, fileCount: numeric(source.fileCount, `${context}.fileCount`), sizeBytes: numeric(source.sizeBytes, `${context}.sizeBytes`) }
}

export const parseFactorioImportResult: Decoder<FactorioImportResult> = (value, context = 'Factorio import result') => {
  const source = object(value, context)
  return { ...parseBasicResult(source, context), worldId: nullableText(source.worldId, `${context}.worldId`),
    worldDirectory: nullableText(source.worldDirectory, `${context}.worldDirectory`) }
}
export const parseTerrariaImportResult: Decoder<TerrariaImportResult> = (value, context = 'Terraria import result') => {
  const source = object(value, context)
  return { ...parseBasicResult(source, context), worldId: nullableText(source.worldId, `${context}.worldId`),
    worldDirectory: nullableText(source.worldDirectory, `${context}.worldDirectory`) }
}

export const parseAcceptanceView: Decoder<AcceptanceView> = (value, context = 'acceptance view') => {
  const source = object(value, context)
  return { profileId: text(source.profileId, `${context}.profileId`), stale: flag(source.stale, `${context}.stale`),
    gameFilesAvailable: flag(source.gameFilesAvailable, `${context}.gameFilesAvailable`),
    gameFilesChanged: flag(source.gameFilesChanged, `${context}.gameFilesChanged`),
    updatedUtc: nullableUtcTimestamp(source.updatedUtc, `${context}.updatedUtc`),
    checks: list(source.checks, `${context}.checks`, (item, itemContext) => {
      const check = object(item, itemContext ?? `${context}.checks`)
      return { id: text(check.id, `${itemContext}.id`), label: text(check.label, `${itemContext}.label`),
        evidence: text(check.evidence, `${itemContext}.evidence`), confirmed: flag(check.confirmed, `${itemContext}.confirmed`),
        confirmedUtc: nullableUtcTimestamp(check.confirmedUtc, `${itemContext}.confirmedUtc`) }
    }), evidenceBoundary: text(source.evidenceBoundary, `${context}.evidenceBoundary`) }
}

export const parseAcceptanceResult: Decoder<AcceptanceResult> = (value, context = 'acceptance result') => {
  const source = object(value, context)
  return { ...parseBasicResult(source, context), view: parseAcceptanceView(source.view, `${context}.view`) }
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
  return { ...basic, path: optionalNullableText(source.path, `${context}.path`) }
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

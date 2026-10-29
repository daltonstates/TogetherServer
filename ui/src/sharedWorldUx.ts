export type SharedGame = 'Valheim' | 'MinecraftJava' | 'MinecraftBedrock' | 'Factorio' | 'Terraria' | 'Fixture' | string

// A projection supplied by the owning workspace from a verified signed decision
// and a separate current game observation. Save receipt is never this evidence.
export type SharedSignedHost = {
  pcName: string
  state: 'Preparing' | 'Offline' | 'Starting' | 'ObservedReady' | 'Unknown'
  checkedUtc: string | null
}

export function sharedHostObservation(host?: SharedSignedHost, now = Date.now()): string {
  if (!host) return 'Unknown · Host not checked'
  const checked = host.checkedUtc === null ? NaN : Date.parse(host.checkedUtc)
  if (!Number.isFinite(checked) || checked > now || now - checked > 45000) return 'Unknown · no recent check'
  return host.state === 'ObservedReady' ? 'Ready · game join not tested' : host.state
}

export type FutureHostFields = {
  gameName: string
  serverFileLabel: string
  serverFileHint: string
  gamePort: string
  password: boolean
  minecraft: boolean
  factorio: boolean
}

export function futureHostFields(game?: SharedGame | null): FutureHostFields {
  switch (game) {
    case 'Valheim': return { gameName: 'Valheim', serverFileLabel: 'Valheim dedicated server',
      serverFileHint: 'Choose valheim_server.exe installed on this PC.', gamePort: '2456', password: true, minecraft: false, factorio: false }
    case 'MinecraftJava': return { gameName: 'Minecraft Java', serverFileLabel: 'Minecraft Java server.jar',
      serverFileHint: 'Use an official server installed by TogetherServer on this PC. Use the same game version as the shared save.', gamePort: '25565', password: false, minecraft: true, factorio: false }
    case 'MinecraftBedrock': return { gameName: 'Minecraft Bedrock', serverFileLabel: 'Minecraft Bedrock server',
      serverFileHint: 'Choose bedrock_server.exe in the new server folder.', gamePort: '19132', password: false, minecraft: true, factorio: false }
    case 'Factorio': return { gameName: 'Factorio preview', serverFileLabel: 'Factorio dedicated server',
      serverFileHint: 'Choose factorio.exe installed on this PC. Never forward the RCON control port.', gamePort: '34197', password: false, minecraft: false, factorio: true }
    case 'Terraria': return { gameName: 'Terraria preview', serverFileLabel: 'Terraria dedicated server',
      serverFileHint: 'Choose TerrariaServer.exe installed on this PC. Player count is unavailable.', gamePort: '7777', password: false, minecraft: false, factorio: false }
    default: return { gameName: game === 'Fixture' ? 'Test server' : 'Game server', serverFileLabel: 'Installed game server file',
      serverFileHint: 'Choose the matching server installed on this PC. Check the file location.', gamePort: '', password: true, minecraft: false, factorio: false }
  }
}

export function formatSharedBytes(bytes: number): string {
  if (!Number.isSafeInteger(bytes) || bytes < 0) return 'Unavailable'
  if (bytes < 1024) return `${bytes} B`
  const units = ['KiB', 'MiB', 'GiB', 'TiB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++ }
  return `${value.toLocaleString(undefined, { maximumFractionDigits: value < 10 ? 1 : 0 })} ${units[unit]}`
}

export function sharedSaveAge(utc: string | null | undefined, now = Date.now()): string {
  if (!utc || !Number.isFinite(Date.parse(utc))) return 'Unavailable'
  const seconds = Math.floor((now - Date.parse(utc)) / 1000)
  if (seconds < 0) return 'Time is ahead of this PC'
  if (seconds < 60) return 'Less than a minute ago'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} hr ago`
  const days = Math.floor(seconds / 86400)
  return `${days} ${days === 1 ? 'day' : 'days'} ago`
}

export type TransferPhase = 'Preparing' | 'Receiving' | 'Verifying' | 'Receipt'
export function sharedTransferPhase(state: string, phase?: TransferPhase | null): TransferPhase | null {
  return phase ?? (state === 'Preparing' ? 'Preparing' : state === 'Receiving' ? 'Receiving' : state === 'Verifying' ? 'Verifying' :
    state === 'Receipt' || state === 'Confirming receipt' ? 'Receipt' : null)
}

export function normalizedDirectIpHttpsEndpoint(value: string): string | null {
  try {
    const url = new URL(value)
    if (url.protocol !== 'https:' || !url.port || url.pathname !== '/' || url.search || url.hash ||
      url.username || url.password) return null
    const host = url.hostname.toLowerCase()
    const ipv4 = /^(\d{1,3}\.){3}\d{1,3}$/.test(host)
    const ipv6 = /^\[[0-9a-f:.]+\]$/.test(host) && host.includes(':')
    if ((!ipv4 && !ipv6) || host === '0.0.0.0' || host.startsWith('127.') ||
      host === '[::]' || host === '[::1]' || host.includes('ffff:')) return null
    return url.origin
  } catch { return null }
}

export type SharedRouteDetails = { recordHash: string; tlsFingerprint: string; endpoint: string | null }
const hashPattern = /^[0-9a-f]{64}$/i
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function createSharedRouteDetails(profileId: string, details: SharedRouteDetails): string {
  return JSON.stringify({ kind: 'TogetherServerRoute', schema: 1, profileId,
    recordHash: details.recordHash, tlsFingerprint: details.tlsFingerprint, endpoint: details.endpoint })
}

export function parseSharedRouteDetails(text: string, profileId: string): SharedRouteDetails {
  if (!text.trim() || text.length > 4096) throw new Error('Paste the complete connection details, up to 4 KiB.')
  let recordHash: unknown, tlsFingerprint: unknown, endpoint: unknown
  if (text.trim().startsWith('{')) {
    let value: unknown
    try { value = JSON.parse(text) as unknown } catch { throw new Error('Could not read these connection details. Copy them from the new Host again.') }
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('These connection details are invalid. Copy them from the new Host again.')
    const source = value as Record<string, unknown>
    if (Object.keys(source).sort().join(',') !== 'endpoint,kind,profileId,recordHash,schema,tlsFingerprint' ||
      source.kind !== 'TogetherServerRoute' || source.schema !== 1 ||
      typeof source.profileId !== 'string' || !guidPattern.test(source.profileId) ||
      source.profileId.toLowerCase() !== profileId.toLowerCase())
      throw new Error('These connection details are for a different shared world.')
    recordHash = source.recordHash; tlsFingerprint = source.tlsFingerprint; endpoint = source.endpoint
  } else {
    const lines = text.trim().split(/\r?\n/).map(line => line.trim())
    if (lines.length < 2 || lines.length > 3) throw new Error('Paste the decision ID and certificate fingerprint together, one per line.')
    recordHash = lines[0]; tlsFingerprint = lines[1]
    endpoint = lines[2] ?? null
  }
  if (typeof recordHash !== 'string' || !hashPattern.test(recordHash) ||
    typeof tlsFingerprint !== 'string' || !hashPattern.test(tlsFingerprint))
    throw new Error('The decision ID and certificate fingerprint must each contain 64 letters and numbers from 0–9 and A–F.')
  const normalized = endpoint === null ? null : typeof endpoint === 'string' ? normalizedDirectIpHttpsEndpoint(endpoint) : null
  if (endpoint !== null && normalized === null) throw new Error('Use an HTTPS IP address and port without a page path or sign-in details.')
  return { recordHash: recordHash.toUpperCase(), tlsFingerprint: tlsFingerprint.toUpperCase(), endpoint: normalized }
}

export type HandoffChecklistStep = { id: string; pc: string; text: string; done: boolean; current: boolean }
export function plannedHandoffChecklist(status: { pending: boolean; code: string; finalVersion: number | null;
  receiptConfirmed: boolean; canComplete: boolean }, successorName: string): HandoffChecklistStep[] {
  const prepared = status.pending && status.finalVersion !== null
  const receipt = prepared && status.receiptConfirmed
  const steps = [
    { id: 'prepare', pc: 'Current Host PC', text: 'Stop the server safely and share the final verified save.', done: prepared, current: false },
    { id: 'receive', pc: successorName, text: 'Receive and verify the final save, then confirm receipt.', done: receipt, current: false },
    { id: 'complete', pc: 'Current Host PC', text: 'Check that the other PC received the save, then finish the handoff.', done: false, current: false },
    { id: 'setup', pc: successorName, text: 'Restore into a new world folder, finish setup and connection checks, then choose Start in Host.', done: false, current: false }
  ]
  if (status.pending && (status.code === 'HandoffReviewRequired' || status.code === 'SuccessorAccessChanged')) {
    return [{ id: 'review', pc: 'Current Host PC', text: 'Check the save, sharing permissions and running server before continuing.', done: false, current: true }, ...steps]
  }
  const next = steps.find(step => !step.done)
  if (next) next.current = true
  return steps
}

export function liveSaveDisabledReason(game: string | null): string {
  switch (game) {
    case 'Valheim': return 'Test saving and copying with this Valheim installation before sharing while it runs.'
    case 'MinecraftJava': return 'Test saving and copying with this Minecraft Java installation before sharing while it runs.'
    case 'MinecraftBedrock': return 'Test pausing saves, copying and resuming with this Minecraft Bedrock installation before sharing while it runs.'
    case 'Factorio': return 'Test saving and copying with this Factorio installation before sharing while it runs.'
    case 'Terraria': return 'Test saving and copying with this Terraria installation before sharing while it runs.'
    case 'Fixture': return 'This test save action is available only in the development app.'
    default: return 'Sharing while this game runs has not been tested. Stop the server safely to share a completed save.'
  }
}

export type HostingStepDestination = 'receive' | 'server' | 'version' | 'access' | 'ports' | 'folder' | 'route' | 'game' | 'permissions' | 'review'
export function nextHostingStep(reasons: string[]): { reason: string; destination: HostingStepDestination; action: string } | null {
  if (!reasons.length) return null
  const reason = reasons[0]
  const text = reason.toLowerCase()
  const destination: HostingStepDestination = text.includes('permission') || text.includes('eligible') ? 'permissions' :
    text.includes('receive') || text.includes('portable setup') ? 'receive' :
    text.includes('server file') || text.includes('server jar') || text.includes('game executable') ? 'server' :
    text.includes('version') ? 'version' : text.includes('password') ? 'access' :
    text.includes('port') && !text.includes('route') ? 'ports' :
    text.includes('location') || text.includes('folder') ? 'folder' :
    text.includes('control route') || text.includes('friend control route') ? 'route' :
    text.includes('game route') || text.includes('real game') || text.includes('restart') ? 'game' : 'review'
  const actions: Record<HostingStepDestination, string> = { receive: 'Review receiving', server: 'Choose the server file',
    version: 'Review the game version', access: 'Review local game access', ports: 'Review ports',
    folder: 'Review the new world folder', route: 'Review the app connection', game: 'Review the game test steps',
    permissions: 'Review hosting permission help', review: 'Review local setup' }
  return { reason, destination, action: actions[destination] }
}

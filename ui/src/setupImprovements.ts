import type { Profile } from './GameProfile'

export type SetupPort = { protocol: 'TCP' | 'UDP'; port: number; family: 'Any' | 'IPv4' | 'IPv6' }
export function setupPlannedPorts(profile: Profile, basePort = profile.gamePort): SetupPort[] {
  if (profile.kind === 'MinecraftJava' || profile.kind === 'Terraria') return [{ protocol: 'TCP', port: basePort, family: 'Any' }]
  if (profile.kind === 'MinecraftBedrock') return [{ protocol: 'UDP', port: basePort, family: 'IPv4' }]
  if (profile.kind === 'Factorio') return [{ protocol: 'UDP', port: basePort, family: 'Any' }, { protocol: 'TCP', port: profile.factorio?.rconPort ?? 27015, family: 'IPv4' }]
  if (profile.kind === 'Custom') return [{ protocol: profile.custom?.primaryProtocol ?? 'UDP', port: basePort, family: 'Any' }, ...(profile.custom?.additionalPorts ?? [])]
  return [{ protocol: 'UDP', port: basePort, family: 'Any' }, { protocol: 'UDP', port: basePort + 1, family: 'Any' }]
}
function overlaps(left: SetupPort, right: SetupPort) {
  return left.protocol === right.protocol && left.port === right.port && (left.family === 'Any' || right.family === 'Any' || left.family === right.family)
}
export function setupPortSuggestion(profile: Profile, profiles: Profile[]): { conflicts: Profile[]; patch: Partial<Profile> | null } {
  const others = profiles.filter(other => other.id !== profile.id)
  const requested = setupPlannedPorts(profile)
  const conflicts = others.filter(other => setupPlannedPorts(other).some(owned => requested.some(port => overlaps(owned, port))))
  if (conflicts.length === 0) return { conflicts, patch: null }
  const occupied = others.flatMap(other => setupPlannedPorts(other))
  const used = (port: SetupPort) => occupied.some(other => overlaps(port, other))
  const maximum = profile.kind === 'Valheim' || profile.kind === 'Fixture' ? 65534 : 65535
  const first = Number.isInteger(profile.gamePort) ? Math.min(maximum, Math.max(1024, profile.gamePort + 1)) : 1024
  let gamePort = -1
  for (let offset = 0; offset <= maximum - 1024; offset++) {
    const candidate = 1024 + ((first - 1024 + offset) % (maximum - 1024 + 1))
    const gamePorts = setupPlannedPorts(profile, candidate).filter(port => profile.kind !== 'Factorio' || port.protocol === 'UDP')
    // A suggested primary port cannot fix custom secondary conflicts; preserve those explicit settings.
    if (gamePorts.every(port => !used(port))) { gamePort = candidate; break }
  }
  if (gamePort === -1) return { conflicts, patch: null }
  let factorio = profile.factorio
  if (profile.kind === 'Factorio') {
    let rconPort = profile.factorio?.rconPort ?? 27015
    if (rconPort === gamePort || used({ protocol: 'TCP', port: rconPort, family: 'IPv4' })) {
      for (rconPort = 27015; rconPort <= 65535; rconPort++)
        if (rconPort !== gamePort && !used({ protocol: 'TCP', port: rconPort, family: 'IPv4' })) break
      if (rconPort > 65535) return { conflicts, patch: null }
    }
    factorio = { rconPort }
  }
  return { conflicts, patch: { gamePort, ...(profile.kind === 'Factorio' ? { factorio } : {}) } }
}

export function setupTemplate(source: Profile, id: string, managedRoot: string, profiles: Profile[]): Profile {
  const next: Profile = { id, kind: source.kind, name: source.name ? `${source.name} copy` : '', serverName: source.serverName ? `${source.serverName} copy` : '',
    crossplay: source.crossplay, publicListing: source.publicListing, worldId: '', worldSource: source.kind === 'Valheim' ? 'New' : 'Existing',
    worldDirectory: source.kind === 'Valheim' ? `${managedRoot}\\${id.replaceAll('-', '')}` : '', gamePort: source.gamePort,
    executablePath: source.kind === 'MinecraftBedrock' || source.kind === 'Custom' ? '' : source.executablePath,
    minecraft: source.kind === 'MinecraftJava' ? { serverJarPath: '' } : null,
    factorio: source.kind === 'Factorio' ? { rconPort: source.factorio?.rconPort ?? 27015 } : null,
    custom: source.kind === 'Custom' ? { gameName: source.custom?.gameName ?? '', primaryProtocol: source.custom?.primaryProtocol ?? 'UDP',
      shareJoinAddress: source.custom?.shareJoinAddress ?? true, additionalPorts: [] } : null,
    backups: source.backups ? { ...source.backups } : { enabled: false, retentionCount: 5, minimumFreeSpaceMb: 1024 },
    crashRecovery: { enabled: false }, maintenance: { enabled: false, message: '' }, sharedSavesEnabled: false, worldLoadRehearsalId: null }
  const suggestion = setupPortSuggestion(next, profiles)
  return suggestion.patch ? { ...next, ...suggestion.patch } : next
}

export type SetupChange = { label: string; before: string; after: string }
export function setupProfileChanges(before: Profile, after: Profile): SetupChange[] {
  const fields: [string, (profile: Profile) => string][] = [
    ['Game', profile => profile.kind], ['Server name', profile => profile.name], ['Listing name', profile => profile.serverName],
    ['World', profile => profile.worldId], ['World source', profile => profile.worldSource], ['Save location', profile => profile.worldDirectory],
    ['Server application', profile => profile.executablePath], ['Game ports', profile => setupPlannedPorts(profile).map(port => `${port.protocol} ${port.port} ${port.family}`).join(', ')],
    ['Java server JAR', profile => profile.minecraft?.serverJarPath ?? ''], ['Crossplay', profile => profile.crossplay ? 'On' : 'Off'],
    ['Public listing', profile => profile.publicListing ? 'On' : 'Off'], ['Automatic crash restart', profile => profile.crashRecovery?.enabled ? 'On' : 'Off'],
    ['Backups', profile => profile.backups?.enabled ? `On · keep ${profile.backups.retentionCount} · reserve ${profile.backups.minimumFreeSpaceMb} MB` : 'Off'],
    ['Custom game', profile => profile.custom?.gameName ?? ''], ['Share game address', profile => profile.custom?.shareJoinAddress ? 'On' : 'Off']
  ]
  return fields.flatMap(([label, read]) => read(before) === read(after) ? [] : [{ label, before: read(before) || '(empty)', after: read(after) || '(empty)' }])
}

export function setupSupport(kind: Profile['kind']): { label: string; actions: string; limits: string } {
  if (kind === 'Valheim') return { label: 'Valheim', actions: 'Built-in Start, server-reported players, graceful Stop, backups, and direct Friend controls.',
    limits: 'Use your Steam-installed dedicated server. Check a real Friend join and recognizable saved change after Stop and restart before relying on automatic Stop for a valued world.' }
  if (kind === 'MinecraftJava' || kind === 'MinecraftBedrock') return { label: 'Minecraft · fixture-tested support',
    actions: 'Official server setup, built-in Start, player queries, graceful Stop, reviewed settings and player lists.',
    limits: `${kind === 'MinecraftJava' ? 'Official vanilla Java only; Paper and modded JARs are unsupported.' : 'Bedrock packs must use the reviewed managed-world flow.'} Real owner-installed join, player transitions, save, and restart acceptance remains required.` }
  if (kind === 'Factorio') return { label: 'Factorio preview', actions: 'Owner-installed server, verified copied save, local authenticated player query, graceful /quit and backups.',
    limits: 'No automatic crash restart. Never forward the RCON port. Real join and save/restart acceptance is pending.' }
  if (kind === 'Terraria') return { label: 'Terraria preview', actions: 'Owner-installed server, verified copied world, fixed Start, graceful exit, and backups.',
    limits: 'A TCP listener leaves players Unknown. Friend Stop and automatic Stop remain blocked; real join/save/restart acceptance is pending.' }
  if (kind === 'Custom') return { label: 'Custom game · advanced', actions: 'Owner-reviewed local Start, Status/players and Stop scripts. Friends request fixed saved actions only.',
    limits: 'Scripts can act with your Windows account. Trusted occupancy needs the reviewed contract and live test; missing or invalid player counts are Unknown and block Friend or automatic Stop.' }
  return { label: 'Synthetic fixture', actions: 'Disposable development checks only.', limits: 'A fixture cannot prove a real game join, world save, or network route.' }
}

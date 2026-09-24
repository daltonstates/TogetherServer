export type GameKind = 'Fixture' | 'Valheim' | 'MinecraftJava' | 'MinecraftBedrock' | 'Custom'

export type CustomPort = {
  protocol: 'TCP' | 'UDP'
  port: number
  label: string
  family: 'Any' | 'IPv4' | 'IPv6'
}

export type CustomGameOptions = {
  gameName: string
  primaryProtocol: 'TCP' | 'UDP'
  shareJoinAddress: boolean
  additionalPorts: CustomPort[]
}

export type Profile = {
  id: string
  kind: GameKind
  name: string
  serverName: string
  crossplay: boolean
  publicListing: boolean
  worldId: string
  worldSource: 'Existing' | 'New'
  worldDirectory: string
  gamePort: number
  executablePath: string
  minecraft?: { serverJarPath: string } | null
  custom?: CustomGameOptions | null
  crashRecovery?: { enabled: boolean } | null
  backups?: { enabled: boolean; retentionCount: number; minimumFreeSpaceMb: number } | null
}

export function gameLabel(kind: string): string {
  return kind === 'MinecraftJava' ? 'Minecraft Java Edition' :
    kind === 'MinecraftBedrock' ? 'Minecraft Bedrock Edition' : kind
}

export function profileGameLabel(profile: Pick<Profile, 'kind' | 'custom'>): string {
  return profile.kind === 'Custom' ? profile.custom?.gameName?.trim() || 'Custom game' : gameLabel(profile.kind)
}

export type GameKind = 'Fixture' | 'Valheim' | 'MinecraftJava' | 'MinecraftBedrock'

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
}

export function gameLabel(kind: string): string {
  return kind === 'MinecraftJava' ? 'Minecraft Java Edition' :
    kind === 'MinecraftBedrock' ? 'Minecraft Bedrock Edition' : kind
}

import { gameLabel } from './GameProfile'

const steps: Record<string, string[]> = {
  Valheim: [
    'Open Valheim, choose your character, then choose Join Game and Join IP.',
    'Copy the Server IP above and enter it in Valheim.',
    'Enter the game password shared separately by the Host.'
  ],
  MinecraftJava: [
    'Open Minecraft: Java Edition and choose Multiplayer.',
    'Choose Add Server or Direct Connection, then paste the Server IP above.',
    'Join the server. If Minecraft says your version differs, ask the Host which game version is running.'
  ],
  MinecraftBedrock: [
    'Open Minecraft: Bedrock Edition, choose Play, then Servers and Add Server.',
    'Use the address before the final colon in Server IP above as Server Address, and the number after it as Port.',
    'Save the entry and choose Join. An Outdated Client or Outdated Server message means the game versions need to match.'
  ],
  Factorio: [
    'Open Factorio and choose Multiplayer, then Connect to address.',
    'Copy the Server IP above and paste it into the address field.'
  ],
  Terraria: [
    'Open Terraria and choose Multiplayer, then Join via IP.',
    'Use the address before the final colon in Server IP above, then enter the port after it.',
    'Choose your character and join the server. The Host should check a real saved change after play.'
  ]
}

export function JoinGuide({ kind }: { kind: string }) {
  const gameSteps = steps[kind]
  if (!gameSteps) return null
  return <details className="join-guide">
    <summary>How to join in {gameLabel(kind)}</summary>
    <ol>{gameSteps.map(step => <li key={step}>{step}</li>)}</ol>
    <small>If the app connection works but the game cannot join, use Connection Doctor to check the game route from this PC.</small>
  </details>
}

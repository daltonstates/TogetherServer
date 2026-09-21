import type { Profile } from './GameProfile'

type Props = {
  profile: Profile
  busy: boolean
  onChange: (patch: Partial<Profile>) => void
  onBrowse: (target: 'folder' | 'executable' | 'jar') => void
}

export function minecraftSetupIssues(profile: Profile): string[] {
  const issues: string[] = []
  if (!profile.name.trim()) issues.push('Name this server.')
  if (!profile.worldId.trim()) issues.push('Enter the world name from server.properties.')
  if (!profile.worldDirectory.trim()) issues.push('Choose the prepared Minecraft server folder.')
  if (!profile.executablePath.trim()) issues.push(profile.kind === 'MinecraftJava' ? 'Choose java.exe.' : 'Choose bedrock_server.exe.')
  if (profile.kind === 'MinecraftJava' && !profile.minecraft?.serverJarPath?.trim()) issues.push('Choose the Java server JAR.')
  return issues
}

export function MinecraftWorldSetup({ profile, busy, onChange, onBrowse }: Props) {
  return <>
    <p className="helper-text">Use a server folder you prepared outside this app. TogetherServer reads server.properties and never changes the EULA or copies a world.</p>
    <div className="settings-grid"><label>Server name<input value={profile.name} onChange={event => onChange({ name: event.target.value })} placeholder="My Minecraft server" /></label>
      <label>World name in server.properties<input value={profile.worldId} onChange={event => onChange({ worldId: event.target.value })} placeholder="level-name" /></label>
      <label className="wide">Prepared server folder<input value={profile.worldDirectory} onChange={event => onChange({ worldDirectory: event.target.value })} placeholder="C:\\MinecraftServer" /></label>
      <label>Game port in server.properties<input type="number" min="1024" max="65535" value={profile.gamePort} onChange={event => onChange({ gamePort: Number(event.target.value) })} /></label></div>
    <div className="setup-tools"><button className="secondary" disabled={busy} onClick={() => onBrowse('folder')}>Browse server folder</button></div>
    {profile.kind === 'MinecraftJava' && <p className="helper-text">Review Minecraft's EULA yourself. Start requires eula=true in this folder.</p>}
    {profile.kind === 'MinecraftBedrock' && <p className="helper-text">Bedrock also uses server-portv6. With LAN visibility on, it binds the default discovery ports too. TogetherServer checks them for conflicts.</p>}
  </>
}

export function MinecraftServerSetup({ profile, busy, onChange, onBrowse }: Props) {
  return <div className="settings-grid">
    <label className="wide">{profile.kind === 'MinecraftJava' ? 'Installed java.exe' : 'Installed bedrock_server.exe'}<input value={profile.executablePath} onChange={event => onChange({ executablePath: event.target.value })} placeholder={profile.kind === 'MinecraftJava' ? 'C:\\Program Files\\Java\\bin\\java.exe' : 'C:\\MinecraftServer\\bedrock_server.exe'} /></label>
    {profile.kind === 'MinecraftJava' && <label className="wide">Server JAR in that folder<input value={profile.minecraft?.serverJarPath ?? ''} onChange={event => onChange({ minecraft: { serverJarPath: event.target.value } })} placeholder="C:\\MinecraftServer\\server.jar" /></label>}
    <div className="setup-tools wide"><button className="secondary" disabled={busy} onClick={() => onBrowse('executable')}>Browse executable</button>{profile.kind === 'MinecraftJava' && <button className="secondary" disabled={busy} onClick={() => onBrowse('jar')}>Browse server JAR</button>}</div>
  </div>
}

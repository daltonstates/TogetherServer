import type { Profile } from './GameProfile'

export type MinecraftInstallation = {
  kind: 'MinecraftJava' | 'MinecraftBedrock'
  serverDirectory: string
  artifactPath: string
  executablePath: string
  worldName: string
  gamePort: number
  source: string
  note: string
}
export type MinecraftDiscovery = { installations: MinecraftInstallation[]; javaRuntimePath: string }

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
    <p className="helper-text">Find a server already on this PC, or install a fresh one in the next step. An existing world is never changed by setup.</p>
    <div className="settings-grid"><label>Server name<input disabled={busy} value={profile.name} onChange={event => onChange({ name: event.target.value })} placeholder="My Minecraft server" /></label>
      <label>World name<input disabled={busy} value={profile.worldId} onChange={event => onChange({ worldId: event.target.value })} placeholder="world" /></label>
      <label className="wide">Server folder<input disabled={busy} value={profile.worldDirectory} onChange={event => onChange({ worldDirectory: event.target.value })} placeholder="C:\\MinecraftServer" /></label>
      <label>Game port<input disabled={busy} type="number" min="1024" max="65535" value={profile.gamePort} onChange={event => onChange({ gamePort: Number(event.target.value) })} /></label></div>
    <div className="setup-tools"><button className="secondary" disabled={busy} onClick={() => onBrowse('folder')}>Browse server folder</button></div>
    {profile.kind === 'MinecraftJava' && <p className="helper-text">For an existing Java server, Start requires eula=true in its folder. Installing a new server asks for your consent first.</p>}
    {profile.kind === 'MinecraftBedrock' && <p className="helper-text">Bedrock also uses server-portv6. With LAN visibility on, it binds the default discovery ports too. TogetherServer checks them for conflicts.</p>}
  </>
}

export function MinecraftServerSetup({ profile, busy, onChange, onBrowse, discovery, onSelect, onScan, onInstall,
  acceptedTerms, onTermsChange, installBusy }: Props & {
  discovery: MinecraftDiscovery | null
  onSelect: (item: MinecraftInstallation) => void
  onScan: () => void
  onInstall: () => void
  acceptedTerms: boolean
  onTermsChange: (accepted: boolean) => void
  installBusy: boolean
}) {
  const found = discovery?.installations.filter(item => item.kind === profile.kind) ?? []
  return <div className="settings-grid">
    <div className="wide choices"><strong>Servers found on this PC</strong>
      {discovery && found.length === 0 && <p>No {profile.kind === 'MinecraftJava' ? 'Java server JAR' : 'Bedrock server'} found in common folders. Browse to another folder or install below.</p>}
      {found.map(item => <div className="choice" key={item.artifactPath}><span>{item.artifactPath}<small>{item.source} · {item.note}</small></span><button className="secondary" disabled={busy} onClick={() => onSelect(item)}>Use this server</button></div>)}
      <div className="setup-tools"><button className="secondary" disabled={busy} onClick={onScan}>Search this PC again</button></div>
    </div>
    <div className="wide minecraft-install"><strong>Install a new {profile.kind === 'MinecraftJava' ? 'Java' : 'Bedrock'} server</strong>
      <p className="helper-text">Installs the latest official server in a new TogetherServer folder. Java also installs its required runtime. This may take a few minutes. Existing folders and worlds are left alone.</p>
      <label className="minecraft-terms"><input type="checkbox" checked={acceptedTerms} disabled={busy} onChange={event => onTermsChange(event.target.checked)} /> I have read and accept the <a href="https://www.minecraft.net/en-us/eula" target="_blank" rel="noreferrer">Minecraft EULA</a> and <a href="https://www.microsoft.com/en-us/privacy/privacystatement" target="_blank" rel="noreferrer">Microsoft Privacy Statement</a> for this download.</label>
      <div className="setup-tools"><button disabled={busy || !acceptedTerms} onClick={onInstall}>{installBusy ? 'Installing…' : profile.kind === 'MinecraftJava' ? 'Install Java server' : 'Install Bedrock server'}</button></div>
    </div>
    <p className="helper-text wide">Using an existing server? Its world name and port must match server.properties. You can choose a JAR below if there are several in one folder.</p>
    <label className="wide">{profile.kind === 'MinecraftJava' ? 'Installed java.exe' : 'Installed bedrock_server.exe'}<input disabled={busy} value={profile.executablePath} onChange={event => onChange({ executablePath: event.target.value })} placeholder={profile.kind === 'MinecraftJava' ? 'C:\\Program Files\\Java\\bin\\java.exe' : 'C:\\MinecraftServer\\bedrock_server.exe'} /></label>
    {profile.kind === 'MinecraftJava' && <label className="wide">Server JAR in that folder<input disabled={busy} value={profile.minecraft?.serverJarPath ?? ''} onChange={event => onChange({ minecraft: { serverJarPath: event.target.value } })} placeholder="C:\\MinecraftServer\\server.jar" /></label>}
    <div className="setup-tools wide"><button className="secondary" disabled={busy} onClick={() => onBrowse('executable')}>Browse executable</button>{profile.kind === 'MinecraftJava' && <button className="secondary" disabled={busy} onClick={() => onBrowse('jar')}>Browse server JAR</button>}</div>
  </div>
}

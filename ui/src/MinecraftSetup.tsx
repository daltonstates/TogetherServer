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

export type MinecraftDiscovery = {
  installations: MinecraftInstallation[]
  javaRuntimePath: string
}

export type MinecraftSetupMode = 'existing' | 'install'

type ProfileProps = {
  profile: Profile
  busy: boolean
  onChange: (patch: Partial<Profile>) => void
}

type BrowseTarget = 'folder' | 'executable' | 'jar'

type MinecraftServerSetupProps = ProfileProps & {
  mode: MinecraftSetupMode
  onBrowse: (target: BrowseTarget) => void
  discovery: MinecraftDiscovery | null
  onSelect: (item: MinecraftInstallation) => void
  onScan: () => void
  onInstall: () => void
  acceptedTerms: boolean
  onTermsChange: (accepted: boolean) => void
  installBusy: boolean
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

export function MinecraftWorldSetup({ profile, busy, onChange }: ProfileProps) {
  return <>
    <p className="helper-text">Give this server a friendly name. A new install uses the world and port below; choosing an existing server fills them from server.properties.</p>
    <div className="settings-grid">
      <label>Server name
        <input disabled={busy} value={profile.name} onChange={event => onChange({ name: event.target.value })} placeholder="My Minecraft server" />
      </label>
      <label>World name
        <input disabled={busy} value={profile.worldId} onChange={event => onChange({ worldId: event.target.value })} placeholder="world" />
      </label>
      <label>Game port
        <input disabled={busy} type="number" min="1024" max="65535" value={profile.gamePort} onChange={event => onChange({ gamePort: Number(event.target.value) })} />
      </label>
    </div>
  </>
}

function InstallationChoice({ item, busy, selected, onSelect }: {
  item: MinecraftInstallation
  busy: boolean
  selected: boolean
  onSelect: (item: MinecraftInstallation) => void
}) {
  return <div className="choice">
    <span>{item.artifactPath}<small>{item.source} · {item.note}</small></span>
    <button type="button" className="secondary" disabled={busy || selected} onClick={() => onSelect(item)}>
      {selected ? 'Selected' : 'Use this server'}
    </button>
  </div>
}

export function MinecraftServerSetup({ profile, busy, onChange, onBrowse, discovery, onSelect, onScan, onInstall,
  acceptedTerms, onTermsChange, installBusy, mode }: MinecraftServerSetupProps) {
  const edition = profile.kind === 'MinecraftJava' ? 'Java' : 'Bedrock'

  if (mode === 'install') {
    return <div className="settings-grid">
      <div className="wide minecraft-install">
        <strong>Install a new official {edition} server</strong>
        <p className="helper-text">TogetherServer installs the latest official server in a new private folder. {profile.kind === 'MinecraftJava' ? 'It also installs the required Java runtime. ' : ''}This may take a few minutes. Existing folders and worlds are left alone.</p>
        <label className="minecraft-terms">
          <input type="checkbox" checked={acceptedTerms} disabled={busy || installBusy} onChange={event => onTermsChange(event.target.checked)} />
          <span>I have read and accept the <a href="https://www.minecraft.net/en-us/eula" target="_blank" rel="noreferrer">Minecraft EULA</a> and <a href="https://www.microsoft.com/en-us/privacy/privacystatement" target="_blank" rel="noreferrer">Microsoft Privacy Statement</a> for this download.</span>
        </label>
        <div className="setup-tools">
          <button type="button" disabled={busy || installBusy || !acceptedTerms} onClick={onInstall}>
            {installBusy ? 'Installing…' : `Install ${edition} server`}
          </button>
        </div>
      </div>
    </div>
  }

  const found = discovery?.installations.filter(item => item.kind === profile.kind) ?? []
  const selectedPath = profile.kind === 'MinecraftJava' ? profile.minecraft?.serverJarPath : profile.executablePath

  return <>
    <p className="helper-text">Choose a server TogetherServer found, or browse to its folder. Setup reads its world name and port without changing the existing server or world.</p>
    {profile.worldDirectory && profile.executablePath && <p className="selection-summary">
      Selected: <strong>{profile.worldId || `${edition} server`}</strong>
      <small>{profile.worldDirectory} · port {profile.gamePort}</small>
    </p>}
    <div className="choices">
      <strong>Servers found on this PC</strong>
      {!discovery && <p>Search the common folders on this PC, or browse directly to the server folder.</p>}
      {discovery && found.length === 0 && <p>No {edition} server was found in the common folders. Browse to its folder or use Manual setup.</p>}
      {found.map(item => <InstallationChoice key={item.artifactPath} item={item} busy={busy}
        selected={item.artifactPath === selectedPath} onSelect={onSelect} />)}
    </div>
    <div className="setup-tools">
      <button type="button" className="secondary" disabled={busy} onClick={onScan}>{discovery ? 'Search this PC again' : 'Search this PC'}</button>
      <button type="button" className="secondary" disabled={busy} onClick={() => onBrowse('folder')}>Browse server folder</button>
    </div>
    {profile.kind === 'MinecraftJava' && <p className="helper-text">Before starting an existing Java server, review Minecraft's terms and set eula=true in that server folder.</p>}
    {profile.kind === 'MinecraftBedrock' && <p className="helper-text">TogetherServer also checks Bedrock's IPv6 and LAN-discovery ports for conflicts when those options are enabled.</p>}
    <details className="advanced-block">
      <summary>Manual setup</summary>
      <p className="helper-text">Use these fields only when folder discovery cannot identify the server correctly.</p>
      <div className="settings-grid">
        <label className="wide">Server folder
          <input disabled={busy} value={profile.worldDirectory} onChange={event => onChange({ worldDirectory: event.target.value })} placeholder="C:\\MinecraftServer" />
        </label>
        <label className="wide">{profile.kind === 'MinecraftJava' ? 'Installed java.exe' : 'Installed bedrock_server.exe'}
          <input disabled={busy} value={profile.executablePath} onChange={event => onChange({ executablePath: event.target.value })}
            placeholder={profile.kind === 'MinecraftJava' ? 'C:\\Program Files\\Java\\bin\\java.exe' : 'C:\\MinecraftServer\\bedrock_server.exe'} />
        </label>
        {profile.kind === 'MinecraftJava' && <label className="wide">Server JAR
          <input disabled={busy} value={profile.minecraft?.serverJarPath ?? ''} onChange={event => onChange({ minecraft: { serverJarPath: event.target.value } })} placeholder="C:\\MinecraftServer\\server.jar" />
        </label>}
      </div>
      <div className="setup-tools">
        <button type="button" className="secondary" disabled={busy} onClick={() => onBrowse('folder')}>Browse folder</button>
        <button type="button" className="secondary" disabled={busy} onClick={() => onBrowse('executable')}>Browse executable</button>
        {profile.kind === 'MinecraftJava' && <button type="button" className="secondary" disabled={busy} onClick={() => onBrowse('jar')}>Browse server JAR</button>}
      </div>
    </details>
  </>
}

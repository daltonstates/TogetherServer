import type { RefObject } from 'react'
import { Button, Input, Select, TextArea } from '../../Controls'
import type { CustomScriptBundle, Discovery, HostSnapshot, Settings } from '../../contracts'
import type { CustomPort, Profile } from '../../GameProfile'
import { profileGameLabel } from '../../GameProfile'
import { Icon } from '../../Icon'
import {
  MinecraftServerSetup,
  MinecraftWorldSetup,
  minecraftSetupIssues,
  type MinecraftDiscovery,
  type MinecraftInstallation
} from '../../MinecraftSetup'
import { customPortKey } from '../../setupDraft'

export type SetupStep = 'game' | 'world' | 'server' | 'review'

export const setupSteps: SetupStep[] = ['game', 'world', 'server', 'review']

type PlannedPort = { protocol: 'TCP' | 'UDP'; port: number; family: 'Any' | 'IPv4' | 'IPv6' }

function plannedPorts(profile: Profile, basePort = profile.gamePort): PlannedPort[] {
  if (profile.kind === 'MinecraftJava') return [{ protocol: 'TCP', port: basePort, family: 'Any' }]
  if (profile.kind === 'MinecraftBedrock') return [{ protocol: 'UDP', port: basePort, family: 'IPv4' }]
  if (profile.kind === 'Custom') return [
    { protocol: profile.custom?.primaryProtocol ?? 'UDP', port: basePort, family: 'Any' },
    ...(profile.custom?.additionalPorts ?? []).map(port => ({ protocol: port.protocol, port: port.port, family: port.family }))
  ]
  return [
    { protocol: 'UDP', port: basePort, family: 'Any' },
    { protocol: 'UDP', port: basePort + 1, family: 'Any' }
  ]
}

function plannedPortOverlap(left: PlannedPort, right: PlannedPort) {
  return left.protocol === right.protocol && left.port === right.port &&
    (left.family === 'Any' || right.family === 'Any' || left.family === right.family)
}

function configuredPortWarning(profile: Profile, profiles: Profile[]) {
  const requested = plannedPorts(profile)
  const conflicts = profiles.filter(other => other.id !== profile.id &&
    plannedPorts(other).some(owned => requested.some(port => plannedPortOverlap(owned, port))))
  if (conflicts.length === 0) return null
  const maximum = profile.kind === 'Valheim' || profile.kind === 'Fixture' ? 65534 : 65535
  let suggestion: number | null = null
  for (let candidate = Math.max(1024, profile.gamePort + 1); candidate <= maximum; candidate++) {
    const candidatePorts = plannedPorts(profile, candidate)
    if (profiles.every(other => other.id === profile.id ||
      !plannedPorts(other).some(owned => candidatePorts.some(port => plannedPortOverlap(owned, port))))) {
      suggestion = candidate
      break
    }
  }
  return { conflicts, suggestion }
}

function ConfiguredPortWarning({ profile, profiles }: { profile: Profile; profiles: Profile[] }) {
  const warning = configuredPortWarning(profile, profiles)
  if (!warning) return null
  const ports = plannedPorts(profile).map(port => `${port.protocol} ${port.port}`).join(', ')
  return <div className="configured-port-warning" role="status"><strong>Duplicate saved game port</strong>
    <p>{ports} overlaps {warning.conflicts.map(conflict => conflict.name).join(', ')}. TogetherServer will not run both configurations at once.</p>
    {warning.suggestion && <small>Try port {warning.suggestion}; it does not overlap another saved server. Windows availability is checked again only when Start is requested.</small>}
  </div>
}

export function isValidGamePassword(password: string): boolean {
  if (password.length < 5 || password.length > 64) return false
  for (const character of password) {
    const codePoint = character.codePointAt(0)
    if (codePoint !== undefined && (codePoint <= 0x1f || codePoint === 0x7f)) return false
  }
  return true
}

export function getSetupIssues(profile: Profile, hasPassword: boolean, enteredPassword: string,
  customScripts: CustomScriptBundle | undefined, customScriptsSaved: boolean): string[] {
  const issues: string[] = []
  if (profile.kind === 'Valheim') {
    if (!profile.serverName.trim()) issues.push('Name your world.')
    if (profile.worldSource === 'Existing' && (!profile.worldId || !profile.worldDirectory))
      issues.push('Choose an existing world to copy.')
    if (profile.worldSource === 'New') {
      if (!profile.worldId.trim()) issues.push('Name your new world.')
      if (!profile.worldDirectory.trim()) issues.push('Choose where the new world will be saved.')
    }
    if (!profile.executablePath.trim()) issues.push('Select your installed Valheim Dedicated Server.')
    if (!hasPassword && !enteredPassword) issues.push('Enter a game password.')
    if (enteredPassword && !isValidGamePassword(enteredPassword))
      issues.push('Use a game password of 5 to 64 characters without control characters.')
  } else if (profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') {
    issues.push(...minecraftSetupIssues(profile))
  } else if (profile.kind === 'Custom') {
    if (!profile.custom?.gameName.trim()) issues.push('Enter the game name.')
    if (!profile.name.trim()) issues.push('Name this server.')
    if (!profile.worldId.trim()) issues.push('Enter a save/world key.')
    if (!profile.worldDirectory.trim()) issues.push('Choose the game working and save directory.')
    if (!customScriptsSaved && (!customScripts?.start.trim() || !customScripts?.status.trim() || !customScripts?.stop.trim()))
      issues.push('Enter and save all three custom game scripts.')
  } else {
    if (!profile.name.trim()) issues.push('Enter a test profile name in step 1.')
    if (!profile.worldId.trim()) issues.push('Enter a world ID in step 1.')
    if (!profile.worldDirectory.trim()) issues.push('Choose a separate development directory in step 1.')
    if (!profile.executablePath.trim()) issues.push('Select the fixture executable in step 2.')
  }
  return issues
}

export function getStepIssues(step: SetupStep, profile: Profile, hasPassword: boolean, enteredPassword: string,
  customScripts: CustomScriptBundle | undefined, customScriptsSaved: boolean): string[] {
  if (step === 'game') return []
  if (step === 'review') return getSetupIssues(profile, hasPassword, enteredPassword, customScripts, customScriptsSaved)
  if (step === 'world') {
    if (profile.kind === 'Valheim') {
      if (profile.worldSource === 'Existing' && (!profile.worldId || !profile.worldDirectory)) return ['Choose a world to copy.']
      if (profile.worldSource === 'New' && !profile.worldId.trim()) return ['Name your new world.']
      if (!hasPassword && !enteredPassword) return ['Enter the password friends will use in Valheim.']
      if (enteredPassword && !isValidGamePassword(enteredPassword))
        return ['Use a game password of 5 to 64 characters without control characters.']
    }
    if ((profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') && !profile.name.trim())
      return ['Name this server.']
    if (profile.kind === 'Custom' && (!profile.name.trim() || !profile.worldId.trim() || !profile.worldDirectory.trim()))
      return ['Name the server, enter a save/world key, and choose its working directory.']
    return []
  }
  if (profile.kind === 'Valheim' && !profile.executablePath.trim()) return ['Choose the installed Valheim Dedicated Server.']
  if ((profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') && !profile.executablePath.trim())
    return ['Choose or install the game server.']
  if (profile.kind === 'Custom' && !customScriptsSaved &&
      (!customScripts?.start.trim() || !customScripts?.status.trim() || !customScripts?.stop.trim()))
    return ['Enter Start, Status/players, and Stop scripts.']
  if (profile.kind === 'Fixture' && !profile.executablePath.trim()) return ['Choose the fixture executable.']
  return []
}

type HostSetupDialogProps = {
  dialogRef: RefObject<HTMLDialogElement | null>
  snapshot: HostSnapshot
  draft: Settings
  savedProfiles: Profile[]
  editedProfile: Profile | undefined
  notice: { good: boolean; text: string } | null
  pending: string
  dirty: boolean
  setupStep: SetupStep
  setupIssues: string[]
  stepIssues: string[]
  discovery: Discovery | null
  minecraftDiscovery: MinecraftDiscovery | null
  sourceRoots: Record<string, string>
  passwords: Record<string, string>
  showPasswords: Record<string, boolean>
  minecraftSetupMode: Record<string, 'existing' | 'install'>
  minecraftTerms: Record<string, boolean>
  customScripts: Record<string, CustomScriptBundle>
  customScriptsSaved: Record<string, boolean>
  customScriptsLoading: Record<string, boolean>
  customScriptsChanged: boolean
  dataRecoveryBlocked: boolean
  freshWorldsOnly: boolean
  onCancel: () => void
  onFinishLater: () => void
  onAddProfile: () => void
  onStepChange: (step: SetupStep) => void
  onChangeGameKind: (profile: Profile, kind: Profile['kind']) => void
  onUpdateProfile: (profileId: string, patch: Partial<Profile>) => void
  onImportWorld: (profile: Profile, sourceSaveRoot: string, worldId: string, sourceFolder?: string) => void
  onBrowseWorld: (profile: Profile, folder?: boolean) => void
  onSourceRootChange: (profileId: string, value: string) => void
  onPasswordChange: (profileId: string, value: string) => void
  onShowPasswordChange: (profileId: string, value: boolean) => void
  onBrowseCustomDirectory: (profile: Profile) => void
  onMinecraftSetupModeChange: (profileId: string, mode: 'existing' | 'install') => void
  onBrowseMinecraft: (profile: Profile, target: 'folder' | 'executable' | 'jar') => void
  onApplyMinecraftInstallation: (profile: Profile, installation: MinecraftInstallation) => void
  onScanMinecraft: (folder?: string) => void
  onInstallMinecraft: (profile: Profile) => void
  onMinecraftTermsChange: (profileId: string, accepted: boolean) => void
  onScanValheim: () => void
  onBrowseServer: (profile: Profile) => void
  onEditCustomScripts: (profileId: string, patch: Partial<CustomScriptBundle>) => void
  onUpdateCustomPort: (profile: Profile, index: number, patch: Partial<CustomPort>) => void
  onAddCustomPort: (profile: Profile) => void
  onRemoveCustomPort: (profile: Profile, index: number) => void
  onRemoveProfile: (profile: Profile) => void
  onSave: (startAfterSave?: boolean) => void
}

export function HostSetupDialog({ dialogRef, snapshot, draft, savedProfiles, editedProfile, notice, pending, dirty,
  setupStep, setupIssues, stepIssues, discovery, minecraftDiscovery, sourceRoots, passwords, showPasswords,
  minecraftSetupMode, minecraftTerms, customScripts, customScriptsSaved, customScriptsLoading,
  customScriptsChanged, dataRecoveryBlocked, freshWorldsOnly, onCancel, onFinishLater, onAddProfile, onStepChange,
  onChangeGameKind, onUpdateProfile, onImportWorld, onBrowseWorld, onSourceRootChange, onPasswordChange,
  onShowPasswordChange, onBrowseCustomDirectory, onMinecraftSetupModeChange, onBrowseMinecraft,
  onApplyMinecraftInstallation, onScanMinecraft, onInstallMinecraft, onMinecraftTermsChange, onScanValheim,
  onBrowseServer, onEditCustomScripts, onUpdateCustomPort, onAddCustomPort, onRemoveCustomPort,
  onRemoveProfile, onSave }: HostSetupDialogProps) {
  const setupStepIndex = setupSteps.indexOf(setupStep)

  return <dialog ref={dialogRef} className="panel settings-panel modal-dialog" aria-labelledby="setup-title"
    onCancel={event => { event.preventDefault(); onCancel() }}>
    <div className="section-heading"><span className="section-icon"><Icon name="server" /></span><div><h2 id="setup-title">{savedProfiles.some(profile => profile.id === editedProfile?.id) ? 'Server settings' : 'Add new server'}</h2><p>Choose the game, world, and server files.</p></div></div>
    {freshWorldsOnly && <div className="staging-setup-notice"><strong>Development worlds persist in separate storage.</strong><span>Create and reuse them here. Existing production worlds cannot be selected, scanned, or copied, and every development save stays in the development data folder.</span></div>}
    {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
    <ol className="setup-progress" aria-label="Setup progress">{setupSteps.map((step, index) => <li aria-current={setupStep === step ? 'step' : undefined} className={setupStep === step ? 'current' : index < setupStepIndex ? 'complete' : ''} key={step}><span>{index + 1}</span>{step === 'game' ? 'Game' : step === 'world' ? 'World' : step === 'server' ? 'Server app' : 'Review'}</li>)}</ol>
    {!editedProfile && <div className="empty"><p>Start with one game server.</p><div className="actions"><Button onClick={onAddProfile}>Set up a server</Button></div></div>}
    {draft.profiles.filter(profile => profile.id === editedProfile?.id).map(profile => <div className="profile-form" key={profile.id}>
      {setupStep === 'game' && <div className="setup-stage"><h3>Choose a game</h3><p className="helper-text">{freshWorldsOnly ? 'Choose a reviewed built-in game for this separate development instance.' : 'Choose a reviewed built-in game or an advanced Host-only script profile. You can change technical defaults during Review.'}</p><div className="game-choice-grid">
        <Button aria-pressed={profile.kind === 'Valheim'} className={profile.kind === 'Valheim' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'Valheim')}><strong>Valheim</strong><small>Established local Host flow</small></Button>
        <Button aria-pressed={profile.kind === 'MinecraftJava'} className={profile.kind === 'MinecraftJava' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'MinecraftJava')}><strong>Minecraft Java</strong><small>Preview · real-server acceptance pending</small></Button>
        <Button aria-pressed={profile.kind === 'MinecraftBedrock'} className={profile.kind === 'MinecraftBedrock' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'MinecraftBedrock')}><strong>Minecraft Bedrock</strong><small>Preview · real-server acceptance pending</small></Button>
        {!freshWorldsOnly && <Button aria-pressed={profile.kind === 'Custom'} className={profile.kind === 'Custom' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'Custom')}><strong>Custom game</strong><small>Advanced · local PowerShell actions</small></Button>}
        {profile.kind === 'Fixture' && <Button aria-pressed="true" className="game-choice selected"><strong>Synthetic fixture</strong><small>Development checks only</small></Button>}
      </div></div>}
      {setupStep === 'world' && <div className="setup-step world-step"><h3><Icon name="game" /> {profile.kind === 'Valheim' ? 'Choose a world' : 'Name this server'}</h3>
        {profile.kind === 'Valheim' && <div className="choice-pills">
          <Button aria-pressed={profile.worldSource === 'New'} className={profile.worldSource === 'New' ? 'selected' : 'secondary'} onClick={() => onUpdateProfile(profile.id, { worldSource: 'New', worldId: '', name: '', serverName: '', worldDirectory: `${snapshot.managedWorldsRoot}\\${profile.id.replaceAll('-', '')}` })}>{freshWorldsOnly ? 'Create development world' : 'Create new'}</Button>
          {!freshWorldsOnly && <Button aria-pressed={profile.worldSource === 'Existing'} className={profile.worldSource === 'Existing' ? 'selected' : 'secondary'} onClick={() => onUpdateProfile(profile.id, { worldSource: 'Existing', worldId: '', name: '', serverName: '', worldDirectory: '' })}>Use existing</Button>}
        </div>}
        {!freshWorldsOnly && profile.kind === 'Valheim' && profile.worldSource === 'Existing' && <>
          {profile.worldId && profile.worldDirectory && <p className="selection-summary">Copy ready: <strong>{profile.worldId}</strong>. Your original save stays separate.</p>}
          {discovery && <div className="choices"><strong>Worlds found on this PC</strong>{discovery.worlds.length === 0 ? <p>None found. Browse to a world folder below.</p> : discovery.worlds.map(world => <div className="choice" key={world.saveRoot + world.sourceFolder + world.name}><span>{world.name} <small>{world.format === 'Steam cloud folder' ? 'Steam Cloud' : 'Local save'} · {world.saveRoot}</small></span><Button className="secondary" disabled={!!pending} onClick={() => onImportWorld(profile, world.saveRoot, world.name, world.sourceFolder)}>Copy world</Button></div>)}</div>}
          <div className="setup-tools"><Button className="secondary" disabled={!!pending} onClick={() => onBrowseWorld(profile, true)}>{pending === profile.id ? 'Browsing…' : 'Browse for a world folder'}</Button></div>
          <p className="helper-text">Close Valheim and let Steam finish syncing before copying a cloud world.</p>
          <details className="advanced-block"><summary>Older saves and custom paths</summary>
            <Button className="secondary" disabled={!!pending} onClick={() => onBrowseWorld(profile)}>Choose an older .db or .fwl file</Button>
            <div className="settings-grid"><label>Local save root<Input value={sourceRoots[profile.id] ?? ''} onChange={event => onSourceRootChange(profile.id, event.target.value)} placeholder="C:\\...\\IronGate\\Valheim" /></label><label>World ID<Input value={profile.worldId} onChange={event => onUpdateProfile(profile.id, { worldId: event.target.value })} placeholder="World folder name" /></label></div>
            <Button className="secondary" disabled={!!pending || !sourceRoots[profile.id] || !profile.worldId} onClick={() => onImportWorld(profile, sourceRoots[profile.id], profile.worldId)}>Copy named world</Button>
          </details>
        </>}
        {profile.kind === 'Valheim' && profile.worldSource === 'New' && <div className="quick-setup-fields"><label className="invite-input">World name<Input value={profile.worldId} onChange={event => onUpdateProfile(profile.id, { worldId: event.target.value, name: event.target.value, serverName: event.target.value })} placeholder="My Valheim world" /><small>Stored in TogetherServer's private data.</small></label>
          <label className="invite-input">Game password<Input type={showPasswords[profile.id] ? 'text' : 'password'} autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => onPasswordChange(profile.id, event.target.value)} placeholder={snapshot.passwordConfigured[profile.id] ? 'Saved already; leave blank to keep it' : '5 or more characters'} /><small>Friends use this inside Valheim.</small><span className="show-password"><Input type="checkbox" checked={!!showPasswords[profile.id]} onChange={event => onShowPasswordChange(profile.id, event.target.checked)} /> Show password</span></label></div>}
        {profile.kind === 'Fixture' && <div className="settings-grid"><label>Test profile name<Input value={profile.name} onChange={event => onUpdateProfile(profile.id, { name: event.target.value })} /></label><label>World ID<Input value={profile.worldId} onChange={event => onUpdateProfile(profile.id, { worldId: event.target.value })} /></label><label className="wide">Disposable directory<Input value={profile.worldDirectory} onChange={event => onUpdateProfile(profile.id, { worldDirectory: event.target.value })} /></label></div>}
        {profile.kind === 'Custom' && <div className="settings-grid custom-game-basics">
          <label>Game name<Input value={profile.custom?.gameName ?? ''} onChange={event => onUpdateProfile(profile.id, { custom: { gameName: event.target.value, primaryProtocol: profile.custom?.primaryProtocol ?? 'UDP', shareJoinAddress: profile.custom?.shareJoinAddress ?? true, additionalPorts: profile.custom?.additionalPorts ?? [] } })} placeholder="Palworld, Factorio, Terraria…" /></label>
          <label>Server name<Input value={profile.name} onChange={event => onUpdateProfile(profile.id, { name: event.target.value, serverName: event.target.value })} placeholder="Friends server" /></label>
          <label>Save / world key<Input value={profile.worldId} onChange={event => onUpdateProfile(profile.id, { worldId: event.target.value })} placeholder="main-world" /><small>Used to prevent two managed profiles from writing the same save.</small></label>
          <label className="wide">Working and save directory<div className="field-with-button"><Input value={profile.worldDirectory} onChange={event => onUpdateProfile(profile.id, { worldDirectory: event.target.value })} placeholder="C:\\GameServers\\MyServer" /><Button className="secondary" disabled={!!pending} onClick={() => onBrowseCustomDirectory(profile)}>Browse</Button></div><small>TogetherServer never deletes this folder.</small></label>
        </div>}
        {(profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') && <><div className="choice-pills">
          {!freshWorldsOnly && <Button aria-pressed={(minecraftSetupMode[profile.id] ?? 'existing') === 'existing'} className={(minecraftSetupMode[profile.id] ?? 'existing') === 'existing' ? 'selected' : 'secondary'} onClick={() => onMinecraftSetupModeChange(profile.id, 'existing')}>Use an existing server</Button>}
          <Button aria-pressed={(minecraftSetupMode[profile.id] ?? (freshWorldsOnly ? 'install' : 'existing')) === 'install'} className={(minecraftSetupMode[profile.id] ?? (freshWorldsOnly ? 'install' : 'existing')) === 'install' ? 'selected' : 'secondary'} onClick={() => onMinecraftSetupModeChange(profile.id, 'install')}>Install a new official server</Button>
        </div><MinecraftWorldSetup profile={profile} busy={!!pending} onChange={patch => onUpdateProfile(profile.id, patch)} /></>}
        {!freshWorldsOnly && profile.kind === 'Valheim' && profile.worldSource === 'Existing' && <label className="invite-input setup-password">Game password<Input type={showPasswords[profile.id] ? 'text' : 'password'} autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => onPasswordChange(profile.id, event.target.value)} placeholder={snapshot.passwordConfigured[profile.id] ? 'Saved already; leave blank to keep it' : '5 or more characters'} /><small>Friends use this inside Valheim.</small><span className="show-password"><Input type="checkbox" checked={!!showPasswords[profile.id]} onChange={event => onShowPasswordChange(profile.id, event.target.checked)} /> Show password</span></label>}
      </div>}
      {setupStep === 'server' && <div className="setup-step server-step"><h3><Icon name="search" /> Server app</h3>
        <div className="server-step-content">{profile.kind === 'Valheim' ? <>
          {profile.executablePath ? <p className="selection-summary"><Icon name="check" /> Valheim Dedicated Server found <small>{profile.executablePath}</small></p> : <>
            {discovery && <div className="choices">{discovery.installations.length === 0 ? <p>Valheim Dedicated Server was not found.</p> : discovery.installations.map(item => <div className="choice" key={item.executablePath}><span>{item.executablePath}</span><Button className="secondary" onClick={() => onUpdateProfile(profile.id, { executablePath: item.executablePath })}>Use this install</Button></div>)}</div>}
            <div className="setup-tools"><Button className="secondary" disabled={!!pending} onClick={onScanValheim}>{pending === 'scan' ? 'Searching…' : 'Search this PC'}</Button><Button className="secondary" disabled={!!pending} onClick={() => onBrowseServer(profile)}>Browse for server</Button>{discovery?.installations.length === 0 && <a href="steam://install/896660">Open install in Steam</a>}</div>
            <p className="helper-text">Steam handles installation and any terms when you open it.</p>
          </>}
        </> : profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock' ?
          <MinecraftServerSetup profile={profile} busy={!!pending} onChange={patch => onUpdateProfile(profile.id, patch)}
            onBrowse={target => onBrowseMinecraft(profile, target)} discovery={minecraftDiscovery}
            mode={minecraftSetupMode[profile.id] ?? (freshWorldsOnly ? 'install' : 'existing')}
            onSelect={item => onApplyMinecraftInstallation(profile, item)} onScan={() => onScanMinecraft(profile.worldDirectory)}
            onInstall={() => onInstallMinecraft(profile)} acceptedTerms={!!minecraftTerms[profile.id]}
            onTermsChange={accepted => onMinecraftTermsChange(profile.id, accepted)}
            installBusy={pending === 'install-minecraft'} />
        : profile.kind === 'Custom' ? <div className="custom-script-manager">
          <div className="script-warning"><strong>These scripts can do anything your Windows account can do.</strong><p>Use only scripts you wrote or reviewed. They stay on the Host in Windows protected storage; Friends can request only the saved profile’s fixed Start action and never receive or edit script text.</p></div>
          {customScriptsLoading[profile.id] && <p className="helper-text script-loading" role="status"><Icon name="loader" />Loading protected scripts…</p>}
          <label>Start script<TextArea disabled={customScriptsLoading[profile.id]} value={customScripts[profile.id]?.start ?? ''} onChange={event => onEditCustomScripts(profile.id, { start: event.target.value })} placeholder={'# Start the server, then keep this script running until that server exits.\n$server = Start-Process .\\Server.exe -PassThru\nWait-Process -Id $server.Id\nexit $server.ExitCode'} /><small>The PowerShell process must stay alive for the whole server run. Use $env:TOGETHERSERVER_WORKING_DIRECTORY and $env:TOGETHERSERVER_GAME_PORT as needed.</small></label>
          <label>Status and players script<TextArea disabled={customScriptsLoading[profile.id]} value={customScripts[profile.id]?.status ?? ''} onChange={event => onEditCustomScripts(profile.id, { status: event.target.value })} placeholder={'# Finish within 4 seconds and echo every contract value.\n@{ contractVersion = [int]$env:TOGETHERSERVER_CONTRACT_VERSION; probeId = $env:TOGETHERSERVER_PROBE_ID; operationId = $env:TOGETHERSERVER_OPERATION_ID; state = "Ready"; detail = "Server answered"; onlinePlayers = 0; maxPlayers = 8; players = @() } | ConvertTo-Json -Compress'} /><small>Allowed states: Ready, Starting, or Failed. Existing scripts still show status, but contract v2 echoes plus the guided live certification are required before player counts can authorize remote lifecycle actions.</small></label>
          <label>Stop script<TextArea disabled={customScriptsLoading[profile.id]} value={customScripts[profile.id]?.stop ?? ''} onChange={event => onEditCustomScripts(profile.id, { stop: event.target.value })} placeholder={'# Ask the real server to save and exit gracefully.\n# The Start script process must then exit within 90 seconds.'} /><small>Finish within 15 seconds after sending the game’s own save/stop command. TogetherServer never force-kills the game.</small></label>
          <details className="advanced-block"><summary>Script environment and output contract</summary><p className="helper-text">Every action receives TOGETHERSERVER_ACTION, TOGETHERSERVER_PROFILE_ID, TOGETHERSERVER_WORLD_ID, TOGETHERSERVER_WORKING_DIRECTORY, TOGETHERSERVER_GAME_PORT, and TOGETHERSERVER_MANAGED_PID. Status output must be a single JSON object with optional detail, onlinePlayers, maxPlayers, and players fields.</p></details>
        </div>
        : <label>Fixture executable path<Input value={profile.executablePath} onChange={event => onUpdateProfile(profile.id, { executablePath: event.target.value })} placeholder="C:\\...\\TogetherServer.Fixture.exe" /></label>}</div>
      </div>}
      {setupStep === 'review' && <div className="setup-stage review-stage"><h3>Review and start</h3><div className="review-summary"><div><span>Game</span><strong>{profileGameLabel(profile)}</strong></div><div><span>Server</span><strong>{profile.name || profile.serverName || 'Needs a name'}</strong></div><div><span>World / save key</span><strong>{profile.worldId || 'Not selected'}</strong></div><div><span>Server control</span><strong>{profile.kind === 'Custom' ? (customScriptsSaved[profile.id] || customScriptsChanged ? 'Scripts ready' : 'Scripts needed') : profile.executablePath ? 'Selected' : 'Not selected'}</strong></div></div>
      <ConfiguredPortWarning profile={profile} profiles={draft.profiles} />
      <details className="advanced-block"><summary>Advanced server settings</summary>
        <div className="settings-grid">{profile.kind === 'Valheim' && <><label>Game UDP start port<Input type="number" value={profile.gamePort} onChange={event => onUpdateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label><label>Server listing name<Input value={profile.serverName} onChange={event => onUpdateProfile(profile.id, { serverName: event.target.value, name: event.target.value })} /></label><label className="wide">Installed server path<Input value={profile.executablePath} onChange={event => onUpdateProfile(profile.id, { executablePath: event.target.value })} /></label><label className="wide">Save directory<Input value={profile.worldDirectory} onChange={event => onUpdateProfile(profile.id, { worldDirectory: event.target.value })} /></label></>}</div>
        {profile.kind === 'Valheim' && <div className="device-options"><label className="check-row"><Input type="checkbox" checked={profile.crossplay} onChange={event => onUpdateProfile(profile.id, { crossplay: event.target.checked })} /> Crossplay relay</label><label className="check-row"><Input type="checkbox" checked={profile.publicListing} onChange={event => onUpdateProfile(profile.id, { publicListing: event.target.checked })} /> Show in server list</label></div>}
        {profile.kind === 'Custom' && <div className="custom-ports"><div className="settings-grid"><label>Primary protocol<Select value={profile.custom?.primaryProtocol ?? 'UDP'} onChange={event => onUpdateProfile(profile.id, { custom: { gameName: profile.custom?.gameName ?? 'Custom game', primaryProtocol: event.target.value as 'TCP' | 'UDP', shareJoinAddress: profile.custom?.shareJoinAddress ?? true, additionalPorts: profile.custom?.additionalPorts ?? [] } })}><option value="UDP">UDP</option><option value="TCP">TCP</option></Select></label><label>Primary game port<Input type="number" min="1024" max="65535" value={profile.gamePort} onChange={event => onUpdateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label></div>
          <label className="check-row"><Input type="checkbox" checked={profile.custom?.shareJoinAddress ?? true} onChange={event => onUpdateProfile(profile.id, { custom: { gameName: profile.custom?.gameName ?? 'Custom game', primaryProtocol: profile.custom?.primaryProtocol ?? 'UDP', shareJoinAddress: event.target.checked, additionalPorts: profile.custom?.additionalPorts ?? [] } })} /> Share public IP and primary port with assigned Friends</label>
          {(profile.custom?.additionalPorts ?? []).map((port, index) => <div className="custom-port-row" key={customPortKey(profile.id, index)}><Select aria-label={`Additional port ${index + 1} protocol`} value={port.protocol} onChange={event => onUpdateCustomPort(profile, index, { protocol: event.target.value as 'TCP' | 'UDP' })}><option value="UDP">UDP</option><option value="TCP">TCP</option></Select><Input aria-label={`Additional port ${index + 1}`} type="number" min="1024" max="65535" value={port.port} onChange={event => onUpdateCustomPort(profile, index, { port: Number(event.target.value) })} /><Input aria-label={`Additional port ${index + 1} label`} value={port.label} onChange={event => onUpdateCustomPort(profile, index, { label: event.target.value })} placeholder="Query or RCON" /><Select aria-label={`Additional port ${index + 1} address family`} value={port.family} onChange={event => onUpdateCustomPort(profile, index, { family: event.target.value as 'Any' | 'IPv4' | 'IPv6' })}><option value="Any">Any IP</option><option value="IPv4">IPv4</option><option value="IPv6">IPv6</option></Select><Button className="text-button" onClick={() => onRemoveCustomPort(profile, index)}>Remove</Button></div>)}
          <Button className="secondary" disabled={(profile.custom?.additionalPorts.length ?? 0) >= 15} onClick={() => onAddCustomPort(profile)}>Add another port</Button><p className="helper-text">Declared ports participate in conflict and local-listener checks. TogetherServer does not create firewall or router rules.</p></div>}
        {['Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(profile.kind) && <div className="device-options world-protection-options">
          <label className="check-row"><Input type="checkbox" checked={profile.crashRecovery?.enabled ?? false} onChange={event => onUpdateProfile(profile.id, { crashRecovery: { enabled: event.target.checked } })} /> Restart after an unexpected server exit</label>
          <small>Off by default. Only a previously Ready server with a definitively exited exact process is eligible. Retries wait 1, 5, and 15 minutes, then suspend.</small>
          <label className="check-row"><Input type="checkbox" checked={profile.backups?.enabled ?? false} onChange={event => onUpdateProfile(profile.id, { backups: { enabled: event.target.checked, retentionCount: profile.backups?.retentionCount ?? 5, minimumFreeSpaceMb: profile.backups?.minimumFreeSpaceMb ?? 1024 } })} /> Back up after each confirmed graceful Stop</label>
          {(profile.backups?.enabled ?? false) && <div className="settings-grid"><label>Completed backups to keep<Input type="number" min="1" max="50" value={profile.backups?.retentionCount ?? 5} onChange={event => onUpdateProfile(profile.id, { backups: { enabled: true, retentionCount: Number(event.target.value), minimumFreeSpaceMb: profile.backups?.minimumFreeSpaceMb ?? 1024 } })} /></label><label>Free-space reserve (MB)<Input type="number" min="0" max="1048576" value={profile.backups?.minimumFreeSpaceMb ?? 1024} onChange={event => onUpdateProfile(profile.id, { backups: { enabled: true, retentionCount: profile.backups?.retentionCount ?? 5, minimumFreeSpaceMb: Number(event.target.value) } })} /></label></div>}
          <p className="helper-text">Backups use staged, verified copies. Restore stays on this Host, requires Offline, and takes a pre-restore snapshot. Complete real-game save/restart acceptance before relying on automation for a valued world.</p>
        </div>}
        {profile.worldDirectory && <p className="helper-text">Server save location: <code>{profile.worldDirectory}</code></p>}
      </details></div>}
    </div>)}
    {editedProfile && <>{setupStep === 'review' && savedProfiles.some(saved => saved.id === editedProfile.id) && <details className="advanced-block danger-zone"><summary>Danger zone</summary><p className="helper-text">Removing this server forgets its setup and Friend access. TogetherServer leaves its world files in place.</p><Button className="text-button danger" disabled={!!pending} onClick={() => onRemoveProfile(editedProfile)}>Remove from TogetherServer</Button></details>}
    {(setupStep === 'review' ? setupIssues : stepIssues).length > 0 && <p className="field-error" role="status">{setupStep === 'review' ? setupIssues[0] : stepIssues[0]}</p>}
    <div className="wizard-footer">{savedProfiles.length === 0
      ? <Button className="text-button" disabled={!!pending} onClick={onFinishLater}>Finish later</Button>
      : <Button className="text-button" disabled={!!pending} onClick={onCancel}>Cancel</Button>}<div className="actions">
      {setupStepIndex > 0 && <Button className="secondary" disabled={!!pending} onClick={() => onStepChange(setupSteps[setupStepIndex - 1])}>Back</Button>}
      {setupStep !== 'review' && <Button disabled={stepIssues.length > 0 || !!pending} onClick={() => onStepChange(setupSteps[setupStepIndex + 1])}>Continue</Button>}
      {setupStep === 'review' && <><Button className="secondary" disabled={setupIssues.length > 0 || (!dirty && !passwords[editedProfile.id] && !customScriptsChanged) || !!pending} onClick={() => onSave()}>Save for later</Button><Button disabled={setupIssues.length > 0 || (!dirty && !passwords[editedProfile.id] && !customScriptsChanged) || !!pending || dataRecoveryBlocked} title={dataRecoveryBlocked ? 'Resolve the local data recovery warning before starting.' : undefined} onClick={() => onSave(true)}><Icon name="play" />{pending === 'save' ? 'Starting…' : 'Save and start'}</Button></>}
    </div></div></>}
  </dialog>
}

import { useEffect, useRef, type RefObject } from 'react'
import { Button, Input, Select, TextArea } from '../../Controls'
import type { CustomScriptBundle, Discovery, HostSnapshot, Settings } from '../../contracts'
import type { CustomPort, Profile } from '../../GameProfile'
import { profileGameLabel } from '../../GameProfile'
import { Icon } from '../../Icon'
import { HostStartConnectionNotice } from '../../StartConnectionNotice'
import {
  MinecraftServerSetup,
  MinecraftWorldSetup,
  minecraftSetupIssues,
  type MinecraftDiscovery,
  type MinecraftInstallation
} from '../../MinecraftSetup'
import { customPortKey } from '../../setupDraft'
import { setupPlannedPorts as plannedPorts, setupPortSuggestion, setupProfileChanges, setupSupport } from '../../setupImprovements'
import { EditorDraftRecovery } from '../../editorProtectedDraft'
import type { SetupImportReview } from '../../setupImportPreview'

export type SetupStep = 'game' | 'world' | 'server' | 'review'

export const setupSteps: SetupStep[] = ['game', 'world', 'server', 'review']

function ConfiguredPortWarning({ profile, profiles, onApply, busy }: {
  profile: Profile; profiles: Profile[]; onApply: (patch: Partial<Profile>) => void; busy: boolean
}) {
  const warning = setupPortSuggestion(profile, profiles)
  if (!warning.conflicts.length) return null
  const ports = plannedPorts(profile).map(port => `${port.protocol} ${port.port}`).join(', ')
  return <div className="configured-port-warning" role="status"><strong>Duplicate saved game port</strong>
    <p>{ports} overlaps {warning.conflicts.map(conflict => conflict.name).join(', ')}. TogetherServer will not run both configurations at once.</p>
    {warning.patch ? <><small>Suggested: {plannedPorts({ ...profile, ...warning.patch }).map(port => `${port.protocol} ${port.port}`).join(', ')}. Windows and game-file port availability is checked again at Start.</small>
      <Button className="secondary" disabled={busy} onClick={() => onApply(warning.patch!)}>Apply suggested ports</Button></> :
      <small>No primary-port suggestion can resolve these saved overlaps. Review the declared ports below.</small>}
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
  if (!Number.isInteger(profile.gamePort) || profile.gamePort < 1024 || profile.gamePort > (profile.kind === 'Valheim' || profile.kind === 'Fixture' ? 65534 : 65535))
    issues.push(`Choose a whole game port from 1024 to ${profile.kind === 'Valheim' || profile.kind === 'Fixture' ? 65534 : 65535}.`)
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
  } else if (profile.kind === 'Factorio') {
    if (!profile.name.trim()) issues.push('Name this Factorio server.')
    if (!profile.worldId.trim()) issues.push('Enter the existing Factorio save name without .zip.')
    if (!profile.worldDirectory.trim()) issues.push('Choose the folder containing the save ZIP.')
    if (!profile.executablePath.trim()) issues.push('Choose owner-installed factorio.exe.')
    if (!Number.isInteger(profile.factorio?.rconPort) || (profile.factorio?.rconPort ?? 0) < 1024 || (profile.factorio?.rconPort ?? 0) > 65535 ||
        profile.factorio?.rconPort === profile.gamePort) issues.push('Choose a separate local RCON port from 1024 to 65535.')
  } else if (profile.kind === 'Terraria') {
    if (!profile.name.trim()) issues.push('Name this Terraria server.')
    if (!profile.worldId.trim() || !profile.worldDirectory.trim()) issues.push('Copy an existing Terraria world.')
    if (!profile.executablePath.trim()) issues.push('Choose owner-installed TerrariaServer.exe.')
  } else if (profile.kind === 'Custom') {
    if (!profile.custom?.gameName.trim()) issues.push('Enter the game name.')
    if (!profile.name.trim()) issues.push('Name this server.')
    if (!profile.worldId.trim()) issues.push('Enter a save/world key.')
    if (!profile.worldDirectory.trim()) issues.push('Choose the game working and save directory.')
    if (!customScriptsSaved && (!customScripts?.start.trim() || !customScripts?.status.trim() || !customScripts?.stop.trim()))
      issues.push('Enter and save all three custom game scripts.')
    if ((profile.custom?.additionalPorts ?? []).some(port => !Number.isInteger(port.port) || port.port < 1024 || port.port > 65535))
      issues.push('Use whole additional ports from 1024 to 65535.')
  } else {
    if (!profile.name.trim()) issues.push('Enter a test profile name in step 1.')
    if (!profile.worldId.trim()) issues.push('Enter a world ID in step 1.')
    if (!profile.worldDirectory.trim()) issues.push('Choose a separate development directory in step 1.')
    if (!profile.executablePath.trim()) issues.push('Select the fixture executable in step 2.')
  }
  return issues
}

export type SetupBlocker = { message: string; step: SetupStep; field: string }
export function setupIssueTarget(message: string, profile: Profile): SetupBlocker {
  if (message === 'Name your world.' && profile.worldSource === 'Existing') return { message, step: 'review', field: 'server-name' }
  if (/password/iu.test(message)) return { message, step: 'world', field: 'game-password' }
  if (/RCON|additional port|game port/iu.test(message)) return { message, step: profile.kind.startsWith('Minecraft') && /game port/iu.test(message) ? 'world' : 'review', field: /RCON/iu.test(message) ? 'rcon-port' : /additional/iu.test(message) ? 'additional-ports' : 'game-port' }
  if (/where the new world will be saved/iu.test(message)) return { message, step: 'review', field: 'world-directory' }
  if (/script/iu.test(message)) return { message, step: 'server', field: 'scripts' }
  if (/JAR|java\.exe|executable|Dedicated Server|bedrock_server|factorio\.exe|TerrariaServer|prepared Minecraft/iu.test(message)) return { message, step: 'server', field: 'server-selection' }
  if (/game name/iu.test(message)) return { message, step: 'world', field: 'game-name' }
  if (/directory|folder|existing world|copy|save name|Terraria world/iu.test(message)) return { message, step: 'world', field: 'world-source' }
  if (/world name|new world|world ID|world key|world.*key|save\/world/iu.test(message)) return { message, step: 'world', field: 'world-id' }
  return { message, step: 'world', field: profile.kind === 'Valheim' ? 'world-id' : 'server-name' }
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
    if (profile.kind === 'Factorio' && (!profile.name.trim() || !profile.worldId.trim() || !profile.worldDirectory.trim()))
      return ['Name the server, enter its existing save name, and choose the save folder.']
    if (profile.kind === 'Terraria' && (!profile.name.trim() || !profile.worldId.trim() || !profile.worldDirectory.trim()))
      return ['Name the server and copy an existing Terraria world.']
    if (profile.kind === 'Custom' && (!profile.name.trim() || !profile.worldId.trim() || !profile.worldDirectory.trim()))
      return ['Name the server, enter a save/world key, and choose its working directory.']
    return []
  }
  if (profile.kind === 'Valheim' && !profile.executablePath.trim()) return ['Choose the installed Valheim Dedicated Server.']
  if ((profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') && !profile.executablePath.trim())
    return ['Choose or install the game server.']
  if (profile.kind === 'Factorio' && !profile.executablePath.trim()) return ['Choose owner-installed factorio.exe.']
  if (profile.kind === 'Terraria' && !profile.executablePath.trim()) return ['Choose owner-installed TerrariaServer.exe.']
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
  onBrowseFactorio: (profile: Profile, target: 'executable' | 'save') => void
  onBrowseTerraria: (profile: Profile, target: 'executable' | 'world') => void
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
  onReuseProfile?: (profileId: string) => void
  setupDraftRecovery?: { recovered: string | null; message: string }
  onRecoverSetupDraft?: () => void
  onDiscardSetupDraft?: () => void
  importReview?: SetupImportReview | null
  onConfirmImport?: () => void
  onCancelImport?: () => void
}

export function HostSetupDialog({ dialogRef, snapshot, draft, savedProfiles, editedProfile, notice, pending, dirty,
  setupStep, setupIssues, stepIssues, discovery, minecraftDiscovery, sourceRoots, passwords, showPasswords,
  minecraftSetupMode, minecraftTerms, customScripts, customScriptsSaved, customScriptsLoading,
  customScriptsChanged, dataRecoveryBlocked, freshWorldsOnly, onCancel, onFinishLater, onAddProfile, onStepChange,
  onChangeGameKind, onUpdateProfile, onImportWorld, onBrowseWorld, onSourceRootChange, onPasswordChange,
  onShowPasswordChange, onBrowseCustomDirectory, onBrowseFactorio, onBrowseTerraria, onMinecraftSetupModeChange, onBrowseMinecraft,
  onApplyMinecraftInstallation, onScanMinecraft, onInstallMinecraft, onMinecraftTermsChange, onScanValheim,
  onBrowseServer, onEditCustomScripts, onUpdateCustomPort, onAddCustomPort, onRemoveCustomPort,
  onRemoveProfile, onSave, onReuseProfile, setupDraftRecovery, onRecoverSetupDraft, onDiscardSetupDraft,
  importReview, onConfirmImport, onCancelImport }: HostSetupDialogProps) {
  const setupStepIndex = setupSteps.indexOf(setupStep)
  const focusTarget = useRef<string | null>(null)
  useEffect(() => {
    const id = focusTarget.current
    if (!id) return
    const target = document.getElementById(id)
    if (!target) return
    focusTarget.current = null
    let ancestor = target.parentElement
    while (ancestor) { if (ancestor instanceof HTMLDetailsElement) ancestor.open = true; ancestor = ancestor.parentElement }
    const field = target.matches('input, select, textarea, button') ? target : target.querySelector<HTMLElement>('input, select, textarea, button') ?? target
    field.focus()
  }, [setupStep, editedProfile?.id])
  const openBlocker = (blocker: SetupBlocker) => {
    if (!editedProfile) return
    focusTarget.current = `setup-${editedProfile.id}-${blocker.field}`
    onStepChange(blocker.step)
    if (setupStep === blocker.step) {
      const target = document.getElementById(focusTarget.current)
      let ancestor = target?.parentElement
      while (ancestor) { if (ancestor instanceof HTMLDetailsElement) ancestor.open = true; ancestor = ancestor.parentElement }
      ;(target?.querySelector<HTMLElement>('input, select, textarea, button') ?? target)?.focus()
      focusTarget.current = null
    }
  }

  return <dialog ref={dialogRef} className="panel settings-panel modal-dialog" aria-labelledby="setup-title"
    onCancel={event => { event.preventDefault(); onCancel() }}>
    <div className="section-heading"><span className="section-icon"><Icon name="server" /></span><div><h2 id="setup-title">{savedProfiles.some(profile => profile.id === editedProfile?.id) ? 'Server settings' : 'Add new server'}</h2><p>Choose the game, world, and server files.</p></div></div>
    {freshWorldsOnly && <div className="staging-setup-notice"><strong>Development worlds persist in separate storage.</strong><span>Create and reuse them here. Existing production worlds cannot be selected, scanned, or copied, and every development save stays in the development data folder.</span></div>}
    {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
    {setupDraftRecovery && onRecoverSetupDraft && onDiscardSetupDraft && <EditorDraftRecovery {...setupDraftRecovery}
      disabled={!!pending} onRecover={onRecoverSetupDraft} onDiscard={onDiscardSetupDraft} />}
    {importReview && onConfirmImport && onCancelImport && <SetupImportReviewPanel review={importReview} busy={!!pending}
      onConfirm={onConfirmImport} onCancel={onCancelImport} />}
    <ol className="setup-progress" aria-label="Setup progress">{setupSteps.map((step, index) => <li aria-current={setupStep === step ? 'step' : undefined} className={setupStep === step ? 'current' : index < setupStepIndex ? 'complete' : ''} key={step}><span>{index + 1}</span>{step === 'game' ? 'Game' : step === 'world' ? 'World' : step === 'server' ? 'Server app' : 'Review'}</li>)}</ol>
    {!editedProfile && <div className="empty"><p>Start with one game server.</p><div className="actions"><Button onClick={onAddProfile}>Set up a server</Button></div></div>}
    {draft.profiles.filter(profile => profile.id === editedProfile?.id).map(profile => <div className="profile-form" key={profile.id}>
      <details className="advanced-block" open={setupStep === 'game'} aria-label="Game support and limits"><summary>{setupSupport(profile.kind).label} · support and action limits</summary>
        <p>{setupSupport(profile.kind).actions}</p><p>{setupSupport(profile.kind).limits}</p></details>
      {setupStep === 'game' && onReuseProfile && savedProfiles.length > 0 && <details className="advanced-block"><summary>Reuse a saved server's nonsecret setup</summary>
        <p>Creates a new server with separate world selection and ports. Worlds, passwords, Friend access, and custom scripts are never copied.</p>
        <div className="choices">{savedProfiles.filter(saved => !freshWorldsOnly || !['Custom', 'Factorio', 'Terraria'].includes(saved.kind)).map(saved =>
          <div className="choice" key={saved.id}><span><strong>{saved.name || profileGameLabel(saved)}</strong><small>{profileGameLabel(saved)}</small></span>
            <Button className="secondary" disabled={!!pending} onClick={() => onReuseProfile(saved.id)}>Use setup from {saved.name || profileGameLabel(saved)}</Button></div>)}</div>
      </details>}
      {setupStep === 'game' && <div className="setup-stage"><h3>Choose a game</h3><p className="helper-text">{freshWorldsOnly ? 'Choose a reviewed built-in game for this separate development instance.' : 'Choose a reviewed built-in game or an advanced Host-only script profile. You can change technical defaults during Review.'}</p><div className="game-choice-grid">
        <Button disabled={!!pending} aria-pressed={profile.kind === 'Valheim'} className={profile.kind === 'Valheim' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'Valheim')}><strong>Valheim</strong><small>Established local Host flow</small></Button>
        <Button disabled={!!pending} aria-pressed={profile.kind === 'MinecraftJava'} className={profile.kind === 'MinecraftJava' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'MinecraftJava')}><strong>Minecraft Java</strong><small>Preview · real-server acceptance pending</small></Button>
        <Button disabled={!!pending} aria-pressed={profile.kind === 'MinecraftBedrock'} className={profile.kind === 'MinecraftBedrock' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'MinecraftBedrock')}><strong>Minecraft Bedrock</strong><small>Preview · real-server acceptance pending</small></Button>
        {!freshWorldsOnly && <Button disabled={!!pending} aria-pressed={profile.kind === 'Factorio'} className={profile.kind === 'Factorio' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'Factorio')}><strong>Factorio</strong><small>Preview · fixture-tested lifecycle</small></Button>}
        {!freshWorldsOnly && <Button disabled={!!pending} aria-pressed={profile.kind === 'Terraria'} className={profile.kind === 'Terraria' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'Terraria')}><strong>Terraria</strong><small>Preview · owner-installed server</small></Button>}
        {!freshWorldsOnly && <Button disabled={!!pending} aria-pressed={profile.kind === 'Custom'} className={profile.kind === 'Custom' ? 'game-choice selected' : 'game-choice'} onClick={() => onChangeGameKind(profile, 'Custom')}><strong>Custom game</strong><small>Advanced · local PowerShell actions</small></Button>}
        {profile.kind === 'Fixture' && <Button disabled={!!pending} aria-pressed="true" className="game-choice selected"><strong>Synthetic fixture</strong><small>Development checks only</small></Button>}
      </div></div>}
      {setupStep === 'world' && <div className="setup-step world-step" id={`setup-${profile.id}-world-source`} tabIndex={-1}><h3><Icon name="game" /> {profile.kind === 'Valheim' ? 'Choose a world' : 'Name this server'}</h3>
        <SetupWorldLocations profile={profile} sourceRoot={sourceRoots[profile.id]} minecraftMode={minecraftSetupMode[profile.id]} />
        {profile.kind === 'Valheim' && <div className="choice-pills">
          <Button disabled={!!pending} aria-pressed={profile.worldSource === 'New'} className={profile.worldSource === 'New' ? 'selected' : 'secondary'} onClick={() => onUpdateProfile(profile.id, { worldSource: 'New', worldId: '', name: '', serverName: '', worldDirectory: `${snapshot.managedWorldsRoot}\\${profile.id.replaceAll('-', '')}` })}>{freshWorldsOnly ? 'Create development world' : 'Create new'}</Button>
          {!freshWorldsOnly && <Button disabled={!!pending} aria-pressed={profile.worldSource === 'Existing'} className={profile.worldSource === 'Existing' ? 'selected' : 'secondary'} onClick={() => onUpdateProfile(profile.id, { worldSource: 'Existing', worldId: '', name: '', serverName: '', worldDirectory: '' })}>Use existing</Button>}
        </div>}
        {!freshWorldsOnly && profile.kind === 'Valheim' && profile.worldSource === 'Existing' && <>
          {profile.worldId && profile.worldDirectory && <p className="selection-summary">Copy ready: <strong>{profile.worldId}</strong>. Your original save stays separate.</p>}
          {discovery && <div className="choices"><strong>Worlds found on this PC</strong>{discovery.worlds.length === 0 ? <p>None found. Browse to a world folder below.</p> : discovery.worlds.map(world => <div className="choice" key={world.saveRoot + world.sourceFolder + world.name}><span>{world.name} <small>{world.format === 'Steam cloud folder' ? 'Steam Cloud' : 'Local save'} · {world.saveRoot}</small></span><Button className="secondary" disabled={!!pending} onClick={() => onImportWorld(profile, world.saveRoot, world.name, world.sourceFolder)}>Copy world</Button></div>)}</div>}
          <div className="setup-tools"><Button className="secondary" disabled={!!pending} onClick={() => onBrowseWorld(profile, true)}>{pending === profile.id ? 'Browsing…' : 'Browse for a world folder'}</Button></div>
          <p className="helper-text">Close Valheim and let Steam finish syncing before copying a cloud world.</p>
          <details className="advanced-block"><summary>Older saves and custom paths</summary>
            <Button className="secondary" disabled={!!pending} onClick={() => onBrowseWorld(profile)}>Choose an older .db or .fwl file</Button>
            <div className="settings-grid"><label>Local save root<Input disabled={!!pending} value={sourceRoots[profile.id] ?? ''} onChange={event => onSourceRootChange(profile.id, event.target.value)} placeholder="C:\\...\\IronGate\\Valheim" /></label><label>World ID<Input disabled={!!pending} id={`setup-${profile.id}-world-id`} value={profile.worldId} onChange={event => onUpdateProfile(profile.id, { worldId: event.target.value })} placeholder="World folder name" /></label></div>
            <Button className="secondary" disabled={!!pending || !sourceRoots[profile.id] || !profile.worldId} onClick={() => onImportWorld(profile, sourceRoots[profile.id], profile.worldId)}>Copy named world</Button>
          </details>
        </>}
        {profile.kind === 'Valheim' && profile.worldSource === 'New' && <div className="quick-setup-fields"><label className="invite-input">World name<Input disabled={!!pending} id={`setup-${profile.id}-world-id`} value={profile.worldId} onChange={event => onUpdateProfile(profile.id, { worldId: event.target.value, name: event.target.value, serverName: event.target.value })} placeholder="My Valheim world" /><small>Stored in TogetherServer's private data.</small></label>
          <label className="invite-input">Game password<Input disabled={!!pending} id={`setup-${profile.id}-game-password`} type={showPasswords[profile.id] ? 'text' : 'password'} autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => onPasswordChange(profile.id, event.target.value)} placeholder={snapshot.passwordConfigured[profile.id] ? 'Saved already; leave blank to keep it' : '5 or more characters'} /><small>Friends use this inside Valheim.</small><span className="show-password"><Input disabled={!!pending} type="checkbox" checked={!!showPasswords[profile.id]} onChange={event => onShowPasswordChange(profile.id, event.target.checked)} /> Show password</span></label></div>}
        {profile.kind === 'Fixture' && <div className="settings-grid"><label>Test profile name<Input disabled={!!pending} id={`setup-${profile.id}-server-name`} value={profile.name} onChange={event => onUpdateProfile(profile.id, { name: event.target.value })} /></label><label>World ID<Input disabled={!!pending} id={`setup-${profile.id}-world-id`} value={profile.worldId} onChange={event => onUpdateProfile(profile.id, { worldId: event.target.value })} /></label><label className="wide">Disposable directory<Input disabled={!!pending} id={`setup-${profile.id}-world-directory`} value={profile.worldDirectory} onChange={event => onUpdateProfile(profile.id, { worldDirectory: event.target.value })} /></label></div>}
        {profile.kind === 'Custom' && <div className="settings-grid custom-game-basics">
          <label>Game name<Input disabled={!!pending} id={`setup-${profile.id}-game-name`} value={profile.custom?.gameName ?? ''} onChange={event => onUpdateProfile(profile.id, { custom: { gameName: event.target.value, primaryProtocol: profile.custom?.primaryProtocol ?? 'UDP', shareJoinAddress: profile.custom?.shareJoinAddress ?? true, additionalPorts: profile.custom?.additionalPorts ?? [] } })} placeholder="Palworld, Factorio, Terraria…" /></label>
          <label>Server name<Input disabled={!!pending} id={`setup-${profile.id}-server-name`} value={profile.name} onChange={event => onUpdateProfile(profile.id, { name: event.target.value, serverName: event.target.value })} placeholder="Friends server" /></label>
          <label>Save / world key<Input disabled={!!pending} id={`setup-${profile.id}-world-id`} value={profile.worldId} onChange={event => onUpdateProfile(profile.id, { worldId: event.target.value })} placeholder="main-world" /><small>Used to prevent two managed profiles from writing the same save.</small></label>
          <label className="wide">Working and save directory<div className="field-with-button"><Input disabled={!!pending} id={`setup-${profile.id}-world-directory`} value={profile.worldDirectory} onChange={event => onUpdateProfile(profile.id, { worldDirectory: event.target.value })} placeholder="C:\\GameServers\\MyServer" /><Button className="secondary" disabled={!!pending} onClick={() => onBrowseCustomDirectory(profile)}>Browse</Button></div><small>TogetherServer never deletes this folder.</small></label>
        </div>}
        {profile.kind === 'Factorio' && <div className="settings-grid factorio-basics">
          <label>Server name<Input disabled={!!pending} id={`setup-${profile.id}-server-name`} value={profile.name} onChange={event => onUpdateProfile(profile.id, { name: event.target.value, serverName: event.target.value })} placeholder="Factory night" /></label>
          <div className="wide"><strong>{profile.worldId ? `Copied save: ${profile.worldId}.zip` : 'Choose an existing Factorio save ZIP'}</strong><div className="setup-tools"><Button className="secondary" disabled={!!pending} onClick={() => onBrowseFactorio(profile, 'save')}>{pending === profile.id ? 'Copying…' : 'Browse save to review'}</Button></div><small>TogetherServer copies the selected ZIP once into this server's managed folder. The original is never moved, overwritten, or used for hosting.</small></div>
        </div>}
        {profile.kind === 'Terraria' && <div className="settings-grid">
          <label>Server name<Input disabled={!!pending} id={`setup-${profile.id}-server-name`} value={profile.name} onChange={event => onUpdateProfile(profile.id, { name: event.target.value, serverName: event.target.value })} placeholder="Terraria night" /></label>
          <div className="wide"><strong>{profile.worldId ? `Copied world: ${profile.worldId}.wld` : 'Choose an existing Terraria world'}</strong><div className="setup-tools"><Button className="secondary" disabled={!!pending} onClick={() => onBrowseTerraria(profile, 'world')}>{pending === profile.id ? 'Copying…' : 'Browse world to review'}</Button></div><small>The original world stays untouched. TogetherServer hosts the verified managed copy.</small></div>
        </div>}
        {(profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') && <><div className="choice-pills">
          {!freshWorldsOnly && <Button disabled={!!pending} aria-pressed={(minecraftSetupMode[profile.id] ?? 'existing') === 'existing'} className={(minecraftSetupMode[profile.id] ?? 'existing') === 'existing' ? 'selected' : 'secondary'} onClick={() => onMinecraftSetupModeChange(profile.id, 'existing')}>Use an existing server</Button>}
          <Button disabled={!!pending} aria-pressed={(minecraftSetupMode[profile.id] ?? (freshWorldsOnly ? 'install' : 'existing')) === 'install'} className={(minecraftSetupMode[profile.id] ?? (freshWorldsOnly ? 'install' : 'existing')) === 'install' ? 'selected' : 'secondary'} onClick={() => onMinecraftSetupModeChange(profile.id, 'install')}>Install a new official server</Button>
        </div><MinecraftWorldSetup profile={profile} busy={!!pending} onChange={patch => onUpdateProfile(profile.id, patch)} /></>}
        {!freshWorldsOnly && profile.kind === 'Valheim' && profile.worldSource === 'Existing' && <label className="invite-input setup-password">Game password<Input disabled={!!pending} id={`setup-${profile.id}-game-password`} type={showPasswords[profile.id] ? 'text' : 'password'} autoComplete="new-password" value={passwords[profile.id] ?? ''} onChange={event => onPasswordChange(profile.id, event.target.value)} placeholder={snapshot.passwordConfigured[profile.id] ? 'Saved already; leave blank to keep it' : '5 or more characters'} /><small>Friends use this inside Valheim.</small><span className="show-password"><Input disabled={!!pending} type="checkbox" checked={!!showPasswords[profile.id]} onChange={event => onShowPasswordChange(profile.id, event.target.checked)} /> Show password</span></label>}
      </div>}
      {setupStep === 'server' && <div className="setup-step server-step" id={`setup-${profile.id}-server-selection`} tabIndex={-1}><h3><Icon name="search" /> Server app</h3>
        <div className="server-step-content">{profile.kind === 'Valheim' ? <>
          {profile.executablePath ? <div className="selection-summary"><strong>Selected Valheim Dedicated Server</strong>
            <details><summary>Install location</summary><code>{profile.executablePath}</code></details>
            <Button className="secondary" disabled={!!pending} onClick={() => onBrowseServer(profile)}>Choose another install</Button></div> : <>
            {discovery && <div className="choices">{discovery.installations.length === 0 ? <p>Valheim Dedicated Server was not found.</p> : discovery.installations.map(item => <div className="choice" key={item.executablePath}><div><strong>Valheim Dedicated Server</strong><small>{item.source}</small><details><summary>Install location</summary><code>{item.executablePath}</code></details></div><Button className="secondary" disabled={!!pending} onClick={() => onUpdateProfile(profile.id, { executablePath: item.executablePath })}>Use this install</Button></div>)}</div>}
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
        : profile.kind === 'Factorio' ? <div className="factorio-server-setup">
          <div className="script-warning"><strong>Owner-installed preview</strong><p>TogetherServer does not download Factorio, accept terms, or expose RCON to Friends. The built-in driver has fixture-tested Start, authenticated local player count, /quit, and backup behavior; real-game join/save acceptance is still required.</p></div>
          <label>Factorio server executable<div className="field-with-button"><Input disabled={!!pending} value={profile.executablePath} onChange={event => onUpdateProfile(profile.id, { executablePath: event.target.value })} placeholder="C:\\Factorio\\bin\\x64\\factorio.exe" /><Button className="secondary" disabled={!!pending} onClick={() => onBrowseFactorio(profile, 'executable')}>Browse</Button></div></label>
        </div>
        : profile.kind === 'Terraria' ? <div className="factorio-server-setup">
          <div className="script-warning"><strong>Owner-installed preview</strong><p>TogetherServer does not download Terraria. The built-in driver can launch the copied world and request a graceful exit; a TCP listener cannot establish player count, join, or save integrity. Remote and automatic Stop stay blocked.</p></div>
          <label>Terraria server executable<div className="field-with-button"><Input disabled={!!pending} value={profile.executablePath} onChange={event => onUpdateProfile(profile.id, { executablePath: event.target.value })} placeholder="C:\\Terraria\\TerrariaServer.exe" /><Button className="secondary" disabled={!!pending} onClick={() => onBrowseTerraria(profile, 'executable')}>Browse</Button></div></label>
        </div>
        : profile.kind === 'Custom' ? <div className="custom-script-manager" id={`setup-${profile.id}-scripts`} tabIndex={-1}>
          <div className="script-warning"><strong>These scripts can do anything your Windows account can do.</strong><p>Use only scripts you wrote or reviewed. They stay on the Host in Windows protected storage; Friends can request only the saved profile’s fixed Start action and never receive or edit script text.</p></div>
          {customScriptsLoading[profile.id] && <p className="helper-text script-loading" role="status"><Icon name="loader" />Loading protected scripts…</p>}
          <label>Start script<TextArea disabled={!!pending || customScriptsLoading[profile.id]} value={customScripts[profile.id]?.start ?? ''} onChange={event => onEditCustomScripts(profile.id, { start: event.target.value })} placeholder={'# Start the server, then keep this script running until that server exits.\n$server = Start-Process .\\Server.exe -PassThru\nWait-Process -Id $server.Id\nexit $server.ExitCode'} /><small>The PowerShell process must stay alive for the whole server run. Use $env:TOGETHERSERVER_WORKING_DIRECTORY and $env:TOGETHERSERVER_GAME_PORT as needed.</small></label>
          <label>Status and players script<TextArea disabled={!!pending || customScriptsLoading[profile.id]} value={customScripts[profile.id]?.status ?? ''} onChange={event => onEditCustomScripts(profile.id, { status: event.target.value })} placeholder={'# Finish within 4 seconds and echo every contract value.\n@{ contractVersion = [int]$env:TOGETHERSERVER_CONTRACT_VERSION; probeId = $env:TOGETHERSERVER_PROBE_ID; operationId = $env:TOGETHERSERVER_OPERATION_ID; state = "Ready"; detail = "Server answered"; onlinePlayers = 0; maxPlayers = 8; players = @() } | ConvertTo-Json -Compress'} /><small>Allowed states: Ready, Starting, or Failed. Existing scripts still show status, but contract v2 checks and the guided live test are required before player counts can allow remote Stop, Restart, replacement, or automatic shutdown.</small></label>
          <label>Stop script<TextArea disabled={!!pending || customScriptsLoading[profile.id]} value={customScripts[profile.id]?.stop ?? ''} onChange={event => onEditCustomScripts(profile.id, { stop: event.target.value })} placeholder={'# Ask the real server to save and exit gracefully.\n# The Start script process must then exit within 90 seconds.'} /><small>Finish within 15 seconds after sending the game’s own save/stop command. TogetherServer never force-kills the game.</small></label>
          <details className="advanced-block"><summary>Script environment and output contract</summary><p className="helper-text">Every action receives TOGETHERSERVER_ACTION, TOGETHERSERVER_PROFILE_ID, TOGETHERSERVER_WORLD_ID, TOGETHERSERVER_WORKING_DIRECTORY, TOGETHERSERVER_GAME_PORT, and TOGETHERSERVER_MANAGED_PID. Status output must be a single JSON object with optional detail, onlinePlayers, maxPlayers, and players fields.</p></details>
        </div>
        : <label>Fixture executable path<Input disabled={!!pending} value={profile.executablePath} onChange={event => onUpdateProfile(profile.id, { executablePath: event.target.value })} placeholder="C:\\...\\TogetherServer.Fixture.exe" /></label>}</div>
      </div>}
      {setupStep === 'review' && <div className="setup-stage review-stage"><h3>Review and start</h3><div className="review-summary"><div><span>Game</span><strong>{profileGameLabel(profile)}</strong></div><div><span>Server</span><strong>{profile.name || profile.serverName || 'Needs a name'}</strong></div><div><span>World / save key</span><strong>{profile.worldId || 'Not selected'}</strong></div><div><span>Server control</span><strong>{profile.kind === 'Custom' ? (customScriptsSaved[profile.id] || customScriptsChanged ? 'Scripts ready' : 'Scripts needed') : profile.executablePath ? 'Selected' : 'Not selected'}</strong></div></div>
      <ConfiguredPortWarning profile={profile} profiles={draft.profiles} busy={!!pending} onApply={patch => onUpdateProfile(profile.id, patch)} />
      {savedProfiles.find(saved => saved.id === profile.id) && <SetupEditReview before={savedProfiles.find(saved => saved.id === profile.id)!} after={profile}
        passwordChanged={!!passwords[profile.id]} scriptsChanged={customScriptsChanged} />}
      <SetupWorldLocations profile={profile} sourceRoot={sourceRoots[profile.id]} minecraftMode={minecraftSetupMode[profile.id]} />
      <HostStartConnectionNotice profile={profile} ports={null} routeCheck={null} />
      <details className="advanced-block"><summary>Advanced server settings</summary>
        <div className="settings-grid">{profile.kind === 'Valheim' && <><label>Game UDP start port<Input disabled={!!pending} type="number" id={`setup-${profile.id}-game-port`} value={profile.gamePort} onChange={event => onUpdateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label><label>Server listing name<Input disabled={!!pending} id={`setup-${profile.id}-server-name`} value={profile.serverName} onChange={event => onUpdateProfile(profile.id, { serverName: event.target.value, name: event.target.value })} /></label><label className="wide">Installed server path<Input disabled={!!pending} value={profile.executablePath} onChange={event => onUpdateProfile(profile.id, { executablePath: event.target.value })} /></label><label className="wide">Save directory<Input disabled={!!pending} id={`setup-${profile.id}-world-directory`} value={profile.worldDirectory} onChange={event => onUpdateProfile(profile.id, { worldDirectory: event.target.value })} /></label></>}</div>
        {profile.kind === 'Factorio' && <div className="settings-grid"><label>Game UDP port<Input disabled={!!pending} type="number" min="1024" max="65535" id={`setup-${profile.id}-game-port`} value={profile.gamePort} onChange={event => onUpdateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label><label>RCON TCP port<Input disabled={!!pending} type="number" min="1024" max="65535" id={`setup-${profile.id}-rcon-port`} value={profile.factorio?.rconPort ?? 27015} onChange={event => onUpdateProfile(profile.id, { factorio: { rconPort: Number(event.target.value) } })} /></label><label className="wide">Installed factorio.exe<Input disabled={!!pending} value={profile.executablePath} onChange={event => onUpdateProfile(profile.id, { executablePath: event.target.value })} /></label><label className="wide">Managed save copy<Input disabled={!!pending} id={`setup-${profile.id}-world-directory`} value={profile.worldDirectory} readOnly /></label><small className="wide">TogetherServer connects to RCON only through loopback and never gives it to Friends. Do not forward this port; Factorio may listen on network interfaces.</small></div>}
        {profile.kind === 'Terraria' && <div className="settings-grid"><label>Game TCP port<Input disabled={!!pending} type="number" min="1024" max="65535" id={`setup-${profile.id}-game-port`} value={profile.gamePort} onChange={event => onUpdateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label><label className="wide">Installed TerrariaServer.exe<Input disabled={!!pending} value={profile.executablePath} onChange={event => onUpdateProfile(profile.id, { executablePath: event.target.value })} /></label><label className="wide">Managed world copy<Input disabled={!!pending} id={`setup-${profile.id}-world-directory`} value={profile.worldDirectory} readOnly /></label></div>}
        {profile.kind === 'Valheim' && <div className="device-options"><label className="check-row"><Input disabled={!!pending} type="checkbox" checked={profile.crossplay} onChange={event => onUpdateProfile(profile.id, { crossplay: event.target.checked })} /> Crossplay relay</label><label className="check-row"><Input disabled={!!pending} type="checkbox" checked={profile.publicListing} onChange={event => onUpdateProfile(profile.id, { publicListing: event.target.checked })} /> Show in server list</label></div>}
        {profile.kind === 'Custom' && <div className="custom-ports" id={`setup-${profile.id}-additional-ports`} tabIndex={-1}><div className="settings-grid"><label>Primary protocol<Select disabled={!!pending} value={profile.custom?.primaryProtocol ?? 'UDP'} onChange={event => onUpdateProfile(profile.id, { custom: { gameName: profile.custom?.gameName ?? 'Custom game', primaryProtocol: event.target.value as 'TCP' | 'UDP', shareJoinAddress: profile.custom?.shareJoinAddress ?? true, additionalPorts: profile.custom?.additionalPorts ?? [] } })}><option value="UDP">UDP</option><option value="TCP">TCP</option></Select></label><label>Primary game port<Input disabled={!!pending} type="number" min="1024" max="65535" id={`setup-${profile.id}-game-port`} value={profile.gamePort} onChange={event => onUpdateProfile(profile.id, { gamePort: Number(event.target.value) })} /></label></div>
          <label className="check-row"><Input disabled={!!pending} type="checkbox" checked={profile.custom?.shareJoinAddress ?? true} onChange={event => onUpdateProfile(profile.id, { custom: { gameName: profile.custom?.gameName ?? 'Custom game', primaryProtocol: profile.custom?.primaryProtocol ?? 'UDP', shareJoinAddress: event.target.checked, additionalPorts: profile.custom?.additionalPorts ?? [] } })} /> Share public IP and primary port with assigned Friends</label>
          {(profile.custom?.additionalPorts ?? []).map((port, index) => <div className="custom-port-row" key={customPortKey(profile.id, index)}><Select disabled={!!pending} aria-label={`Additional port ${index + 1} protocol`} value={port.protocol} onChange={event => onUpdateCustomPort(profile, index, { protocol: event.target.value as 'TCP' | 'UDP' })}><option value="UDP">UDP</option><option value="TCP">TCP</option></Select><Input disabled={!!pending} aria-label={`Additional port ${index + 1}`} type="number" min="1024" max="65535" value={port.port} onChange={event => onUpdateCustomPort(profile, index, { port: Number(event.target.value) })} /><Input disabled={!!pending} aria-label={`Additional port ${index + 1} label`} value={port.label} onChange={event => onUpdateCustomPort(profile, index, { label: event.target.value })} placeholder="Query or RCON" /><Select disabled={!!pending} aria-label={`Additional port ${index + 1} address family`} value={port.family} onChange={event => onUpdateCustomPort(profile, index, { family: event.target.value as 'Any' | 'IPv4' | 'IPv6' })}><option value="Any">Any IP</option><option value="IPv4">IPv4</option><option value="IPv6">IPv6</option></Select><Button className="text-button" onClick={() => onRemoveCustomPort(profile, index)}>Remove</Button></div>)}
          <Button className="secondary" disabled={(profile.custom?.additionalPorts.length ?? 0) >= 15} onClick={() => onAddCustomPort(profile)}>Add another port</Button><p className="helper-text">Declared ports participate in conflict and local-listener checks. TogetherServer does not create firewall or router rules.</p></div>}
        {['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(profile.kind) && <div className="device-options world-protection-options">
          {profile.kind !== 'Factorio' && profile.kind !== 'Terraria' ? <>
            <label className="check-row"><Input disabled={!!pending} type="checkbox" checked={profile.crashRecovery?.enabled ?? false} onChange={event => onUpdateProfile(profile.id, { crashRecovery: { enabled: event.target.checked } })} /> Restart after an unexpected server exit</label>
            <small>Off by default. Only a previously Ready server with a definitively exited exact process is eligible. Retries wait 1, 5, and 15 minutes, then suspend.</small>
          </> : <p className="helper-text">Automatic crash recovery stays unavailable for this preview until real save/restart acceptance is recorded.</p>}
          <label className="check-row"><Input disabled={!!pending} type="checkbox" checked={profile.backups?.enabled ?? false} onChange={event => onUpdateProfile(profile.id, { backups: { enabled: event.target.checked, retentionCount: profile.backups?.retentionCount ?? 5, minimumFreeSpaceMb: profile.backups?.minimumFreeSpaceMb ?? 1024 } })} /> Back up after each confirmed graceful Stop</label>
          {(profile.backups?.enabled ?? false) && <div className="settings-grid"><label>Completed backups to keep<Input disabled={!!pending} type="number" min="1" max="50" value={profile.backups?.retentionCount ?? 5} onChange={event => onUpdateProfile(profile.id, { backups: { enabled: true, retentionCount: Number(event.target.value), minimumFreeSpaceMb: profile.backups?.minimumFreeSpaceMb ?? 1024 } })} /></label><label>Free-space reserve (MB)<Input disabled={!!pending} type="number" min="0" max="1048576" value={profile.backups?.minimumFreeSpaceMb ?? 1024} onChange={event => onUpdateProfile(profile.id, { backups: { enabled: true, retentionCount: profile.backups?.retentionCount ?? 5, minimumFreeSpaceMb: Number(event.target.value) } })} /></label></div>}
          <p className="helper-text">Backups use staged, verified copies. Restore stays on this Host, requires Offline, and takes a pre-restore snapshot. Complete real-game save/restart acceptance before relying on automation for a valued world.</p>
        </div>}
        {profile.worldDirectory && <p className="helper-text">Server save location: <code>{profile.worldDirectory}</code></p>}
      </details></div>}
    </div>)}
    {editedProfile && <>{setupStep === 'review' && savedProfiles.some(saved => saved.id === editedProfile.id) && <details className="advanced-block danger-zone"><summary>Danger zone</summary><p className="helper-text">Removing this server forgets its setup and Friend access. TogetherServer leaves its world files in place.</p><Button className="text-button danger" disabled={!!pending} onClick={() => onRemoveProfile(editedProfile)}>Remove from TogetherServer</Button></details>}
    {setupIssues.length > 0 && <section aria-label="Setup blockers" className="field-error"><strong role="status">{setupIssues.length} setup {setupIssues.length === 1 ? 'item needs' : 'items need'} attention</strong>
      <ul>{setupIssues.map((message, index) => <li key={`${message}-${index}`}><Button className="text-button" disabled={!!pending} onClick={() => openBlocker(setupIssueTarget(message, editedProfile))}>{message}</Button></li>)}</ul>
    </section>}
    <div className="wizard-footer"><div className="actions"><Button className="text-button" disabled={!!pending} onClick={onFinishLater}>Finish later</Button>
      <Button className="text-button" disabled={!!pending} onClick={onCancel}>{savedProfiles.length > 0 ? 'Cancel' : 'Cancel setup'}</Button></div><div className="actions">
      {setupStepIndex > 0 && <Button className="secondary" disabled={!!pending} onClick={() => onStepChange(setupSteps[setupStepIndex - 1])}>Back</Button>}
      {setupStep !== 'review' && <Button disabled={stepIssues.length > 0 || !!pending} onClick={() => onStepChange(setupSteps[setupStepIndex + 1])}>Continue</Button>}
      {setupStep === 'review' && <><Button className="secondary" disabled={setupIssues.length > 0 || (!dirty && !passwords[editedProfile.id] && !customScriptsChanged) || !!pending} onClick={() => onSave()}>Save for later</Button><Button disabled={setupIssues.length > 0 || (!dirty && !passwords[editedProfile.id] && !customScriptsChanged) || !!pending || dataRecoveryBlocked} title={dataRecoveryBlocked ? 'Resolve the local data recovery warning before starting.' : undefined} onClick={() => onSave(true)}><Icon name="play" />{pending === 'save' ? 'Starting…' : 'Save and start'}</Button></>}
    </div></div></>}
  </dialog>
}

function SetupWorldLocations({ profile, sourceRoot, minecraftMode }: {
  profile: Profile; sourceRoot?: string; minecraftMode?: 'existing' | 'install'
}) {
  const minecraft = profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock'
  const usesExisting = minecraft && minecraftMode !== 'install'
  const newInstall = minecraft && minecraftMode === 'install'
  return <section aria-label="World locations"><dl className="review-summary">
    {(profile.worldSource === 'Existing' && !minecraft && profile.kind !== 'Custom') && <div><dt>Original on this PC</dt><dd>{sourceRoot ? <code>{sourceRoot}</code> : 'Chosen using discovery or Browse.'}<small>The original is retained.</small></dd></div>}
    <div><dt>{usesExisting ? 'World hosted in the selected server folder' : profile.kind === 'Custom' ? 'Owner-selected working and save folder' : 'World TogetherServer hosts'}</dt>
      <dd>{profile.worldDirectory ? <code>{profile.worldDirectory}</code> : 'A fresh managed destination is allocated when setup copies or installs the server.'}</dd></div>
  </dl><p className="helper-text">{usesExisting ? 'Existing Minecraft setup uses this folder directly. Discovery only reads its setup; it does not copy the world.' :
    profile.kind === 'Custom' ? 'Custom scripts use the reviewed folder you choose. No world is copied by this setup form.' :
      profile.worldSource === 'New' || newInstall ? 'A new world is created here. Other worlds on this PC stay separate.' : 'Original source → verified managed copy → hosted world. Copying never moves or replaces the original.'}</p></section>
}

function SetupEditReview({ before, after, passwordChanged, scriptsChanged }: {
  before: Profile; after: Profile; passwordChanged: boolean; scriptsChanged: boolean
}) {
  const changes = setupProfileChanges(before, after)
  return <section aria-label="Review server setup changes"><h4>Changes to the saved setup</h4>
    {changes.length === 0 ? <p>No nonsecret setup fields changed.</p> : <dl>{changes.map(change => <div key={change.label}><dt>{change.label}</dt>
      <dd><span>Before: </span><code>{change.before}</code><br /><span>After: </span><code>{change.after}</code></dd></div>)}</dl>}
    {passwordChanged && <p>The entered game password will replace the saved password. Its value is hidden from this review.</p>}
    {scriptsChanged && <p>Custom scripts changed. Review them on the Server app step; script text stays out of this summary and recoverable setup draft.</p>}
    <p>Changes to game, world, application, ports or route can make earlier game acceptance confirmations stale.</p>
  </section>
}

function SetupImportReviewPanel({ review, busy, onConfirm, onCancel }: {
  review: SetupImportReview; busy: boolean; onConfirm: () => void; onCancel: () => void
}) {
  const expired = review.expiresUtc !== undefined && Date.parse(review.expiresUtc) <= Date.now()
  const bytes = (value: number | null | undefined) => value === null || value === undefined ? 'Unavailable' : `${value.toLocaleString()} bytes`
  const modified = (value: string | null | undefined) => value ? new Date(value).toLocaleString() : 'Unavailable'
  return <section className="notice" aria-label="Review world import"><h3>Review the copy</h3>
    <dl><div><dt>World / save</dt><dd>{review.worldId}</dd></div>
      <div><dt>Source</dt><dd>{review.sourceSaveRoot ? <code>{review.sourceSaveRoot}\{review.sourceFolder}\{review.worldId}</code> : 'The file you selected in Windows'}</dd></div>
      <div><dt>Destination</dt><dd>A fresh app-managed copy belonging to this server. An existing destination is never replaced.</dd></div>
      <div><dt>Total source size</dt><dd>{bytes(review.totalBytes)}</dd></div>
      <div><dt>Source last changed</dt><dd>{modified(review.modifiedUtc)}</dd></div>
    </dl>
    {review.sourceFiles?.length ? <ul>{review.sourceFiles.map(file => <li key={file.name}><strong>{file.name}</strong> · {bytes(file.bytes)} · {modified(file.modifiedUtc)}</li>)}</ul> :
      <p>Detailed source-file facts are unavailable for this selection. The importer still checks a complete source and refuses an existing destination.</p>}
    {review.format && <p>Source format: {review.format}.</p>}
    {review.sourceFolder === 'worlds' && <p>Close Valheim and wait for Steam Cloud syncing to finish before confirming this cached-world copy.</p>}
    <p>Your original stays untouched. Size and dates describe the source; they do not prove a world loads or saves correctly in the game.</p>
    {review.expiresUtc && <p>{expired ? 'This source selection expired. Cancel and browse again.' : `This source selection expires at ${new Date(review.expiresUtc).toLocaleTimeString()}. Copy checks the selected files again.`}</p>}
    <div className="actions"><Button disabled={busy || expired} onClick={onConfirm}>Confirm copy</Button>
      <Button className="secondary" disabled={busy} onClick={onCancel}>Cancel copy</Button></div>
  </section>
}

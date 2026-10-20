import { useId, useState, type ReactNode } from 'react'
import { Button, Input } from './Controls'
import { Icon, type IconName } from './Icon'
import { SettingsDialog } from './SettingsPresentation'
import { DismissibleNotice } from './DismissibleNotice'
import type { Profile } from './GameProfile'
import type { Settings, WorldBackupStatus } from './contracts'
import { backupBytes } from './backupCatalogModel'
import './HostBackupsPage.css'

export type ProtectionPolicy = { backups: NonNullable<Profile['backups']>; crashRecovery: { enabled: boolean } }
export function protectionPolicy(profile: Profile): ProtectionPolicy {
  return { backups: { enabled: profile.backups?.enabled ?? false, retentionCount: profile.backups?.retentionCount ?? 5,
    minimumFreeSpaceMb: profile.backups?.minimumFreeSpaceMb ?? 1024 }, crashRecovery: { enabled: profile.crashRecovery?.enabled ?? false } }
}
export function applyProtectionPolicy(settings: Settings, profileId: string, expected: ProtectionPolicy, next: ProtectionPolicy): Settings | null {
  const profile = settings.profiles.find(item => item.id === profileId)
  if (!profile || profile.kind === 'Custom' || profile.worldLoadRehearsalId ||
      profile.separateCopySourceProfileId && next.crashRecovery.enabled ||
      JSON.stringify(protectionPolicy(profile)) !== JSON.stringify(expected) ||
      !Number.isInteger(next.backups.retentionCount) || next.backups.retentionCount < 1 || next.backups.retentionCount > 50 ||
      !Number.isInteger(next.backups.minimumFreeSpaceMb) || next.backups.minimumFreeSpaceMb < 0 || next.backups.minimumFreeSpaceMb > 1048576 ||
      !['Fixture', 'Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(profile.kind) && next.crashRecovery.enabled) return null
  return { ...settings, profiles: settings.profiles.map(item => item.id === profileId ?
    { ...item, backups: { ...next.backups }, crashRecovery: { ...next.crashRecovery } } : item) }
}

function ProtectionEditor({ profile, disabled, onClose, onSave }: {
  profile: Profile; disabled: boolean; onClose: () => void;
  onSave: (expected: ProtectionPolicy, next: ProtectionPolicy) => Promise<boolean>
}) {
  const [original] = useState(() => protectionPolicy(profile))
  const [value, setValue] = useState(original)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const canRecover = !profile.separateCopySourceProfileId && ['Fixture', 'Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(profile.kind)
  const valid = Number.isInteger(value.backups.retentionCount) && value.backups.retentionCount >= 1 && value.backups.retentionCount <= 50 &&
    Number.isInteger(value.backups.minimumFreeSpaceMb) && value.backups.minimumFreeSpaceMb >= 0 && value.backups.minimumFreeSpaceMb <= 1048576
  const changed = JSON.stringify(value) !== JSON.stringify(original)
  async function save() {
    if (saving || disabled || !valid || !changed) return
    setSaving(true); setError('')
    try {
      if (await onSave(original, value)) onClose()
      else setError('Settings were not saved. Check the server status and refresh any other changed settings, then try again. Your edits are kept here.')
    } catch { setError('Settings could not be saved. Your edits are kept here; try again.') }
    finally { setSaving(false) }
  }
  return <SettingsDialog title="Protection settings" description={`Automatic backups and recovery for ${profile.name}.`}
    busy={saving} onClose={onClose} footer={<>
      <Button className="secondary" disabled={saving} onClick={onClose}>Cancel</Button>
      <Button disabled={saving || disabled || !changed || !valid} onClick={() => void save()}>
        <Icon name={saving ? 'loader' : 'check'} />{saving ? 'Saving…' : 'Save protection settings'}</Button>
    </>}>
    <div className="protection-editor">
      <section className="protection-setting-card">
        <header><span className="protection-card-icon"><Icon name="archive" size={22} /></span><div><h3>Automatic backups</h3><p>Keep a copy each time this server stops gracefully.</p></div></header>
        <label className="protection-toggle"><span><strong>Back up after each graceful Stop</strong><small>Waits for the server to exit, then backs up before deleting older copies.</small></span>
          <Input type="checkbox" checked={value.backups.enabled} disabled={saving || disabled}
            onChange={event => setValue(current => ({ ...current, backups: { ...current.backups, enabled: event.target.checked } }))} /></label>
        <div className="protection-number-fields">
          <label>Completed backups to keep<Input type="number" min="1" max="50" step="1" disabled={saving || disabled}
            value={Number.isNaN(value.backups.retentionCount) ? '' : value.backups.retentionCount} onChange={event => setValue(current => ({ ...current, backups: { ...current.backups, retentionCount: event.target.value === '' ? NaN : Number(event.target.value) } }))} />
            <small>1–50 unpinned copies. Pinned copies stay protected.</small></label>
          <label>Free-space reserve (MB)<Input type="number" min="0" max="1048576" step="1" disabled={saving || disabled}
            value={Number.isNaN(value.backups.minimumFreeSpaceMb) ? '' : value.backups.minimumFreeSpaceMb}
            onChange={event => setValue(current => ({ ...current, backups: { ...current.backups, minimumFreeSpaceMb: event.target.value === '' ? NaN : Number(event.target.value) } }))} />
            <small>Pause backup creation before using this space.</small></label>
        </div>
      </section>
      <section className="protection-setting-card">
        <header><span className="protection-card-icon"><Icon name="refresh" size={22} /></span><div><h3>Crash recovery</h3><p>Try restarting if this server closes unexpectedly.</p></div></header>
        {canRecover ? <label className="protection-toggle"><span><strong>Restart after an unexpected server exit</strong><small>Only after a previously Ready server has definitely exited. Retries after 1, 5 and 15 minutes.</small></span>
          <Input type="checkbox" checked={value.crashRecovery.enabled} disabled={saving || disabled}
            onChange={event => setValue(current => ({ ...current, crashRecovery: { enabled: event.target.checked } }))} /></label> :
          <p className="helper-text">{profile.separateCopySourceProfileId ? 'Start a recovered world manually. Automatic crash recovery is unavailable for this hosting copy.' : 'Automatic crash recovery is unavailable for this game preview.'}</p>}
      </section>
      {!valid && <p role="alert" className="warning-text">Keep 1–50 completed backups and enter a whole-number reserve from 0 to 1,048,576 MB.</p>}
      {disabled && <p role="status">Finish other edits and confirm the server is Offline before saving protection settings.</p>}
      {error && <DismissibleNotice scope={profile.id} noticeKey={error} role="alert">{error}</DismissibleNotice>}
    </div>
  </SettingsDialog>
}

export type ProtectionSection = 'backups' | 'sharing' | 'settings'
const sections: { id: ProtectionSection; label: string; icon: IconName }[] = [
  { id: 'backups', label: 'Backup library', icon: 'archive' },
  { id: 'sharing', label: 'Sharing & hosting', icon: 'users' },
  { id: 'settings', label: 'Protection settings', icon: 'shield' }
]
export function HostBackupsPage({ profile, status, state, busy, editDisabled, backingUp, onBackup, onRefresh, onSafeRestart,
  onSaveProtection, children }: {
  profile: Profile; status?: WorldBackupStatus; state: string; busy: boolean; editDisabled: boolean; backingUp: boolean;
  onBackup: () => void; onRefresh: () => void; onSafeRestart?: () => void;
  onSaveProtection: (expected: ProtectionPolicy, next: ProtectionPolicy) => Promise<boolean>;
  children: (section: ProtectionSection, selectSection: (section: ProtectionSection) => void) => ReactNode
}) {
  const [section, setSection] = useState<ProtectionSection>('backups')
  const [editing, setEditing] = useState(false)
  const id = useId()
  const policy = protectionPolicy(profile)
  const supported = profile.kind !== 'Custom' && !profile.worldLoadRehearsalId
  const recoverySupported = !profile.separateCopySourceProfileId && ['Fixture', 'Valheim', 'MinecraftJava', 'MinecraftBedrock'].includes(profile.kind)
  const failure = status?.lastFailureUtc && (!status.lastSuccessfulUtc || Date.parse(status.lastFailureUtc) > Date.parse(status.lastSuccessfulUtc))
  return <section className="host-backups-page" aria-label="World protection">
    <header className="host-protection-heading">
      <div><span className="protection-eyebrow"><Icon name="shield" size={15} />WORLD PROTECTION</span><h2>Backups & sharing</h2>
        <p>Keep recovery copies and choose who can host this world.</p></div>
      <div className="actions"><Button className="secondary icon-button" aria-label="Refresh backups" title="Refresh backups" disabled={busy} onClick={onRefresh}><Icon name="refresh" /></Button>
        <Button disabled={busy || editDisabled || state !== 'Offline' || !supported} onClick={onBackup}>
          <Icon name={backingUp ? 'loader' : 'plus'} />{backingUp ? 'Backing up…' : 'Back up now'}</Button></div>
    </header>
    <div className="protection-overview" aria-label="Protection overview">
      <article><span className="protection-card-icon"><Icon name="archive" /></span><div><small>Automatic backups</small><strong>{policy.backups.enabled ? 'After graceful Stop' : 'Off'}</strong><span>{policy.backups.enabled ? `Keep ${policy.backups.retentionCount} unpinned copies` : 'Manual backups are available while Offline'}</span></div></article>
      <article><span className="protection-card-icon"><Icon name="clock" /></span><div><small>Last completed backup</small><strong>{status?.lastSuccessfulUtc ? <time dateTime={status.lastSuccessfulUtc}>{new Date(status.lastSuccessfulUtc).toLocaleString()}</time> : status ? 'No completed backup recorded' : 'Not checked yet'}</strong><span>{status ? `${status.completedCount} retained · ${backupBytes(status.retainedSizeBytes)}` : 'Backup details not checked yet'}</span></div></article>
      <article><span className="protection-card-icon"><Icon name="shield" /></span><div><small>Backup drive</small><strong>{status?.availableSpaceBytes != null ? `${backupBytes(status.availableSpaceBytes)} free` : 'Space not checked'}</strong><span>{backupBytes(policy.backups.minimumFreeSpaceMb * 1024 * 1024)} reserve configured</span></div></article>
    </div>
    {failure && <DismissibleNotice scope={profile.id} noticeKey={status.lastFailureUtc} role="alert" className="protection-failure"><Icon name="warning" /><span><strong>The latest backup attempt failed</strong><br />{status.lastFailure || 'Review the backup details before relying on a recovery copy.'}</span></DismissibleNotice>}
    {state !== 'Offline' && <p className="protection-status-note"><Icon name="shield" />Stop the server and confirm it is Offline before creating or restoring a backup. You can review copies and sharing while it runs.</p>}
    <nav className="protection-navigation" aria-label="World protection sections">{sections.map(item =>
      <Button key={item.id} className="secondary" aria-current={section === item.id ? 'page' : undefined}
        disabled={busy} onClick={() => setSection(item.id)}><Icon name={item.icon} />{item.label}</Button>)}</nav>
    <div className="protection-content">{children(section, setSection)}</div>
    <div id={`${id}-settings`} hidden={section !== 'settings'} className="protection-settings-panel">
      <div className="protection-section-heading"><div><h3>Protect this world automatically</h3><p>Choose how many backups to keep, reserve free space and set crash recovery.</p></div>
        <Button className="secondary" disabled={editDisabled || busy || state !== 'Offline' || !supported} onClick={() => setEditing(true)}><Icon name="settings" />Change protection settings</Button></div>
      {profile.worldLoadRehearsalId && <p className="protection-status-note">This is a disposable test world. Change protection settings on the original world.</p>}
      {profile.kind === 'Custom' && <p className="protection-status-note">Automatic backups and recovery are unavailable for Custom servers.</p>}
      <div className="protection-setting-grid">
        <article className="protection-setting-card"><header><span className="protection-card-icon"><Icon name="archive" size={22} /></span><div><h3>Automatic backups</h3><p>{policy.backups.enabled ? 'On · after every confirmed graceful Stop' : 'Off · enable to keep copies after Stop'}</p></div></header><dl><div><dt>Unpinned copies</dt><dd>{policy.backups.retentionCount}</dd></div><div><dt>Drive reserve</dt><dd>{backupBytes(policy.backups.minimumFreeSpaceMb * 1024 * 1024)}</dd></div></dl><p className="helper-text">Pinned backups are kept separately. Sharing completed saves requires automatic backups.</p></article>
        <article className="protection-setting-card"><header><span className="protection-card-icon"><Icon name="refresh" size={22} /></span><div><h3>Crash recovery</h3><p>{!recoverySupported ? profile.separateCopySourceProfileId ? 'Manual Start for this recovered world' : 'Unavailable for this preview' : policy.crashRecovery.enabled ? 'On · limited restart attempts' : 'Off · manual Start remains available'}</p></div></header><p className="helper-text">The app checks the recorded server process. If it cannot confirm the server has exited, it will not restart it.</p>{onSafeRestart && <Button className="secondary" disabled={busy || editDisabled} onClick={onSafeRestart}><Icon name="refresh" />Safe restart</Button>}</article>
      </div>
      <details className="protection-help"><summary>How backups and checks work</summary><p>Safe restart waits for a graceful Stop, completes a backup, then starts the server again. If the backup fails, the server stays stopped. File checks confirm the copied files match; you still need to load the world in the game and check it after restart.</p><p>Copy to vault copies a verified backup to a folder you choose and keeps the local backup. Choose another drive or PC for separate storage.</p></details>
    </div>
    {editing && <ProtectionEditor key={profile.id} profile={profile} disabled={editDisabled || busy || state !== 'Offline'} onClose={() => setEditing(false)} onSave={onSaveProtection} />}
  </section>
}

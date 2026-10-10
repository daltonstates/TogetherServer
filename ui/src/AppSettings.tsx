import { useState, type Dispatch, type SetStateAction } from 'react'
import { Button, Input } from './Controls'
import { DesktopPreferencesPanel, UpdateDetailsPanel, type DesktopNotificationServer } from './DesktopPreferencesPanel'
import type { AppInstanceView, DesktopPreferences } from './contracts'
import type { DesktopUpdateDetails } from './desktopPreferencesWire'
import { Icon } from './Icon'
import { ActionFeedback, AppearancePreferences } from './QolWidgets'
import type { UiPreferences } from './qolPreferences'
import { SettingsCard, SettingsDialog, SettingsSectionHeading } from './SettingsPresentation'

type AppSettingsPanel = 'appearance' | 'notifications' | 'windows' | 'updates' | 'shortcuts'
type AppSettingsProps = {
  preferences: UiPreferences
  onPreferencesChange: Dispatch<SetStateAction<UiPreferences>>
  servers: DesktopNotificationServer[]
  desktop: DesktopPreferences | null
  instance: AppInstanceView | null
  update: DesktopUpdateDetails | null
  busy: boolean
  desktopBusy: boolean
  updateBusy: boolean
  onDesktopChange: (patch: { launchAtLogin: boolean } | { closeToTray: boolean }) => void
  keepAwake?: { enabled: boolean; detail?: string; onChange: (enabled: boolean) => void }
  notice: { good: boolean; text: string } | null
  onClearNotice: () => void
  onCheckUpdate: () => Promise<boolean>
  onReviewUpdate: () => void
  onReloadUpdate: () => void
  onQuit: () => void
}

const titles: Record<AppSettingsPanel, string> = {
  appearance: 'Appearance', notifications: 'Notifications', windows: 'Windows behavior',
  updates: 'App updates', shortcuts: 'Keyboard shortcuts'
}

export function AppSettings({ preferences, onPreferencesChange, servers, desktop, instance, update,
  busy, desktopBusy, updateBusy, onDesktopChange, keepAwake, notice, onClearNotice, onCheckUpdate, onReviewUpdate, onReloadUpdate, onQuit }: AppSettingsProps) {
  const [panel, setPanel] = useState<AppSettingsPanel | null>(null)
  const [childBusy, setChildBusy] = useState(false)
  const modalBusy = busy || desktopBusy || updateBusy || childBusy
  const open = (next: AppSettingsPanel) => { onClearNotice(); setPanel(next) }
  const checkUpdate = async () => {
    if (await onCheckUpdate()) { setPanel(null); onReviewUpdate() }
  }
  return <section className="settings-section app-settings-page">
    <SettingsSectionHeading icon="settings" title="Application" description="Choose a setting to see its options." />
    <div className="settings-card-grid">
      <SettingsCard icon="palette" title="Appearance" description="Theme, spacing, text size, and contrast."
        summary={`${preferences.theme === 'system' ? 'Windows theme' : preferences.theme === 'dark' ? 'Dark theme' : 'Light theme'} · ${preferences.density === 'compact' ? 'Compact' : 'Comfortable'} · ${preferences.textScale}% text`}
        actionLabel="Edit appearance" onOpen={() => open('appearance')} />
      <SettingsCard icon="bell" title="Notifications" description="Windows alerts and choices for each server."
        summary={preferences.quietMode ? 'Quiet mode is on' : 'Quiet mode is off'}
        actionLabel="Edit notifications" onOpen={() => open('notifications')} />
      <SettingsCard icon="monitor" title="Windows behavior" description="Sign-in, tray, and hosting power preferences."
        summary={desktop?.available ? `${desktop.launchAtLogin ? 'Open at sign-in' : 'Manual launch'} · ${desktop.closeToTray ? 'Close to tray' : 'Close normally'}` : 'Windows preferences unavailable'}
        actionLabel="Edit Windows behavior" onOpen={() => open('windows')} />
      <SettingsCard icon="refresh" title="App updates" description="Version, release notes, and reminder choices."
        summary={instance?.updatesAvailable === false ? instance.isStaging ? 'Updates off in staging' : 'Updates unavailable in this build' : update ? `Version ${update.currentVersion}${update.state === 'Available' ? ' · Update available' : ''}` : 'Version unavailable'}
        actionLabel="Manage updates" onOpen={() => open('updates')} />
      <SettingsCard icon="keyboard" title="Keyboard shortcuts" description="Move around the app from your keyboard."
        summary="Ctrl K opens the command palette" actionLabel="View shortcuts" onOpen={() => open('shortcuts')} />
    </div>
    <div className="settings-danger-row"><span><strong>Quit TogetherServer</strong><small>Active or unresolved hosted servers still block Quit.</small></span>
      <Button className="secondary" disabled={!desktop?.available || busy} onClick={onQuit}><Icon name="power" />Quit {instance?.displayName ?? 'TogetherServer'}</Button></div>
    {panel && <SettingsDialog title={titles[panel]} onClose={() => setPanel(null)} busy={modalBusy}
      description="Preferences save when you change them."
      footer={<Button className="secondary" disabled={modalBusy} onClick={() => setPanel(null)}>Done</Button>}>
      <ActionFeedback notice={notice} />
      {panel === 'appearance' && <AppearancePreferences value={preferences} onChange={onPreferencesChange} />}
      {panel === 'notifications' && <DesktopPreferencesPanel servers={servers} quietMode={preferences.quietMode}
        onQuietModeChange={quietMode => onPreferencesChange(current => ({ ...current, quietMode }))} onBusyChange={setChildBusy} />}
      {panel === 'windows' && <div className="settings-dialog-stack">
        <label className="setting-toggle"><span><strong>Open at Windows sign-in</strong><small>{instance?.isStaging ? 'Disabled in staging so the stable app keeps its sign-in setting.' : 'Starts quietly in the tray.'}</small></span>
          <Input type="checkbox" checked={desktop?.launchAtLogin ?? false} disabled={!desktop?.available || !desktop.startupAvailable || desktopBusy} onChange={event => onDesktopChange({ launchAtLogin: event.target.checked })} /></label>
        <label className="setting-toggle"><span><strong>Close to tray</strong><small>Hosting and Friend checks keep running.</small></span>
          <Input type="checkbox" checked={desktop?.closeToTray ?? false} disabled={!desktop?.available || desktopBusy} onChange={event => onDesktopChange({ closeToTray: event.target.checked })} /></label>
        {keepAwake && <label className="setting-toggle"><span><strong>Keep Windows awake while hosting</strong><small>Prevents idle sleep while an exact managed server is running. Manual sleep can still interrupt hosting.</small>
          {keepAwake.detail && <small>{keepAwake.detail}</small>}</span><Input type="checkbox" checked={keepAwake.enabled} disabled={busy} onChange={event => keepAwake.onChange(event.target.checked)} /></label>}
      </div>}
      {panel === 'updates' && <div className="settings-dialog-stack">
        <div className="settings-version-row"><span><strong>Version {update?.currentVersion ?? 'unavailable'}</strong>
          <small>{instance?.updatesAvailable === false ? instance.isStaging ? 'Stable updates are disabled in staging.' : 'Updates are unavailable in this build.' : 'Updates install only when you choose, after a verified local-state recovery checkpoint.'}</small></span>
          <Button className="secondary" disabled={updateBusy || busy || childBusy || instance?.updatesAvailable === false} onClick={() => void checkUpdate()}>
            <Icon name={updateBusy ? 'loader' : 'refresh'} />{updateBusy ? 'Checking…' : 'Check for updates'}</Button></div>
        {update && <UpdateDetailsPanel update={update} busy={updateBusy} onSnoozed={onReloadUpdate} onBusyChange={setChildBusy} />}
      </div>}
      {panel === 'shortcuts' && <dl className="settings-shortcuts">
        <div><dt><kbd>Ctrl K</kbd></dt><dd>Command palette</dd></div>
        <div><dt><kbd>Alt 1</kbd></dt><dd>Host</dd></div><div><dt><kbd>Alt 2</kbd></dt><dd>Join</dd></div>
        <div><dt><kbd>Alt 3</kbd></dt><dd>Attention</dd></div><div><dt><kbd>Alt 4</kbd></dt><dd>Settings</dd></div>
        <div><dt><kbd>Esc</kbd></dt><dd>Close a dialog or go back</dd></div>
      </dl>}
    </SettingsDialog>}
  </section>
}

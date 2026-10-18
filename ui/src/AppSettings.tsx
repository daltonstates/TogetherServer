import { ProfileSettings, type ProfileEditorState } from './ProfileSettings'
import { DismissibleNotice } from './DismissibleNotice'
import { useEffect, useId, useRef, useState, type Dispatch, type SetStateAction } from 'react'
import { Button, Input } from './Controls'
import { DesktopPreferencesPanel, UpdateDetailsPanel } from './DesktopPreferencesPanel'
import type { AppInstanceView, DesktopPreferences } from './contracts'
import { saveUpdatePreferences, type DesktopUpdateDetails } from './desktopPreferencesWire'
import { Icon } from './Icon'
import { ActionFeedback, AppearancePreferences } from './QolWidgets'
import type { UiPreferences } from './qolPreferences'
import { SettingsSectionHeading } from './SettingsPresentation'
import './TaskFirstPages.css'

export type AppSettingsSection = 'profile' | 'appearance' | 'notifications' | 'windows' | 'updates' | 'shortcuts'

export type AppSettingsProps = {
  profile?: ProfileEditorState
  section?: AppSettingsSection
  onSectionChange?: (section: AppSettingsSection) => void
  onClose?: () => void
  onBusyChange?: (busy: boolean) => void
  preferences: UiPreferences
  onPreferencesChange: Dispatch<SetStateAction<UiPreferences>>
  desktop: DesktopPreferences | null
  instance: AppInstanceView | null
  update: DesktopUpdateDetails | null
  busy: boolean
  desktopBusy: boolean
  updateBusy: boolean
  onDesktopChange: (patch: { launchAtLogin: boolean } | { closeToTray: boolean }) => void
  notice: { good: boolean; text: string } | null
  onClearNotice: () => void
  onCheckUpdate: () => Promise<boolean>
  onReviewUpdate: () => void
  onReloadUpdate: () => void | Promise<void>
  onQuit: () => void
}

const sections: Record<AppSettingsSection, { title: string; description: string; icon: 'users' | 'palette' | 'bell' | 'monitor' | 'refresh' | 'keyboard' }> = {
  profile: { title: 'Profile', description: 'The name you share when hosting and joining.', icon: 'users' },
  appearance: { title: 'Appearance', description: 'How TogetherServer looks on this PC.', icon: 'palette' },
  notifications: { title: 'Notifications', description: 'Defaults for Windows alerts on this PC.', icon: 'bell' },
  windows: { title: 'Windows behavior', description: 'Opening, closing and the Windows tray.', icon: 'monitor' },
  updates: { title: 'App updates', description: 'Review an update before downloading or restarting.', icon: 'refresh' },
  shortcuts: { title: 'Keyboard shortcuts', description: 'Move between pages and actions from the keyboard.', icon: 'keyboard' }
}

export function AppSettings({ profile, section, onSectionChange, onClose, onBusyChange, preferences, onPreferencesChange, desktop, instance, update: receivedUpdate,
  busy, desktopBusy, updateBusy, onDesktopChange, notice, onClearNotice, onCheckUpdate, onReviewUpdate, onReloadUpdate, onQuit }: AppSettingsProps) {
  const [internalSection, setInternalSection] = useState<AppSettingsSection>('appearance')
  const [childBusy, setChildBusy] = useState(false)
  const [updatePreferenceBusy, setUpdatePreferenceBusy] = useState(false)
  const [savedUpdate, setSavedUpdate] = useState<{ source: DesktopUpdateDetails | null; value: DesktopUpdateDetails } | null>(null)
  const [updatePreferenceNotice, setUpdatePreferenceNotice] = useState<{ error: boolean; text: string } | null>(null)
  const updatePreferenceRequest = useRef<AbortController | null>(null)
  const updatePreferenceHelpId = useId()
  const nestedBusy = childBusy || updatePreferenceBusy
  useEffect(() => { onBusyChange?.(nestedBusy) }, [nestedBusy, onBusyChange])
  useEffect(() => () => { onBusyChange?.(false) }, [onBusyChange])
  useEffect(() => () => { updatePreferenceRequest.current?.abort() }, [])
  const update = savedUpdate && savedUpdate.source === receivedUpdate ? savedUpdate.value : receivedUpdate
  const page = section ?? internalSection
  const pageBusy = !!profile?.saving || busy || desktopBusy || updateBusy || nestedBusy
  const selectSection = (next: AppSettingsSection) => {
    if (pageBusy || next === page) return
    onClearNotice()
    if (section === undefined) setInternalSection(next)
    onSectionChange?.(next)
  }
  const checkUpdate = async () => { if (await onCheckUpdate()) onReviewUpdate() }
  const saveUpdatePreference = async (includePrereleases: boolean) => {
    if (pageBusy || !update || instance?.isStaging || instance?.updatesAvailable === false) return
    const controller = new AbortController()
    updatePreferenceRequest.current = controller
    setUpdatePreferenceBusy(true); setUpdatePreferenceNotice(null)
    try {
      const next = await saveUpdatePreferences(includePrereleases, controller.signal)
      if (controller.signal.aborted) return
      setSavedUpdate({ source: receivedUpdate, value: next })
      setUpdatePreferenceNotice({ error: false, text: 'Update preference saved on this PC.' })
      try { await onReloadUpdate() }
      catch {
        if (!controller.signal.aborted) setUpdatePreferenceNotice({ error: true,
          text: 'Update preference saved on this PC, but update status could not be refreshed. Check updates to try again.' })
      }
    } catch {
      if (!controller.signal.aborted) setUpdatePreferenceNotice({ error: true,
        text: 'Could not save the update preference. The last confirmed choice is shown. Try again.' })
    } finally {
      if (!controller.signal.aborted) { updatePreferenceRequest.current = null; setUpdatePreferenceBusy(false) }
    }
  }
  return <section className="settings-section app-settings-page task-first-page" aria-busy={pageBusy}>
    {section === undefined && <nav className="app-settings-sections" aria-label="App Settings sections">
      {(Object.keys(sections) as AppSettingsSection[]).map(key => <Button key={key} className="text-button"
        disabled={pageBusy} aria-current={page === key ? 'page' : undefined} onClick={() => selectSection(key)}>
        <Icon name={sections[key].icon} />{sections[key].title}</Button>)}
    </nav>}
    <SettingsSectionHeading icon={sections[page].icon} title={sections[page].title} description={sections[page].description} />
    <ActionFeedback notice={notice} />
    <div className="app-settings-content" key={page}>
      {page === 'profile' && profile && <ProfileSettings profile={profile} />}
      {page === 'appearance' && <><AppearancePreferences value={preferences} onChange={onPreferencesChange} />
        <p className="helper-text">Preferences save as you change them.</p></>}
      {page === 'notifications' && <><DesktopPreferencesPanel quietMode={preferences.quietMode}
        onQuietModeChange={quietMode => onPreferencesChange(current => ({ ...current, quietMode }))} onBusyChange={setChildBusy} />
        <p className="helper-text">Individual server choices are available in that server’s Host or Join workspace.</p></>}
      {page === 'windows' && <div className="settings-dialog-stack">
        <label className="setting-toggle"><span><strong>Open at Windows sign-in</strong><small>{instance?.isStaging ? 'Disabled in staging so the stable app keeps its sign-in setting.' : 'Starts quietly in the tray.'}</small></span>
          <Input type="checkbox" checked={desktop?.launchAtLogin ?? false} disabled={!desktop?.available || !desktop.startupAvailable || desktopBusy || busy} onChange={event => onDesktopChange({ launchAtLogin: event.target.checked })} /></label>
        <label className="setting-toggle"><span><strong>Close to tray</strong><small>Hosting and Friend checks keep running.</small></span>
          <Input type="checkbox" checked={desktop?.closeToTray ?? false} disabled={!desktop?.available || desktopBusy || busy} onChange={event => onDesktopChange({ closeToTray: event.target.checked })} /></label>
        {!desktop?.available && <DismissibleNotice as="p" className="warning-text" role="status">Windows preferences are unavailable in this build.</DismissibleNotice>}
        <div className="settings-danger-row"><span><strong>Quit TogetherServer</strong><small>Active or unresolved hosted servers still block Quit.</small></span>
          <Button className="secondary" aria-label={`Quit ${instance?.displayName ?? 'TogetherServer'}`} disabled={!desktop?.available || pageBusy} onClick={onQuit}><Icon name="power" />Quit</Button></div>
      </div>}
      {page === 'updates' && <div className="settings-dialog-stack">
        <div className="settings-version-row"><span><strong>Version {update?.currentVersion ?? 'unavailable'}</strong>
          <small>{instance?.updatesAvailable === false ? instance.isStaging ? 'Stable updates are disabled in staging.' : 'Updates are unavailable in this build.' : 'Install when you choose, after a recovery checkpoint.'}</small></span>
          <Button className="secondary" aria-label={updateBusy ? 'Checking for updates' : 'Check for updates'} disabled={pageBusy || instance?.updatesAvailable === false} onClick={() => void checkUpdate()}>
            <Icon name={updateBusy ? 'loader' : 'refresh'} />{updateBusy ? 'Checking…' : 'Check updates'}</Button></div>
        <label className="setting-toggle"><span><strong>Include prerelease versions</strong><small id={updatePreferenceHelpId}>{instance?.isStaging
          ? 'Disabled in staging so it cannot replace the stable app.'
          : 'Applies only to this PC. Prerelease versions may be unvalidated. Installing still requires Update and restart.'}</small></span>
          <Input type="checkbox" aria-label="Include prerelease versions" aria-describedby={updatePreferenceHelpId}
            checked={update?.includePrereleases ?? false} disabled={!update || pageBusy || instance?.isStaging || instance?.updatesAvailable === false}
            onChange={event => void saveUpdatePreference(event.target.checked)} /></label>
        {updatePreferenceBusy && <p role="status" aria-live="polite">Saving update preference…</p>}
        {updatePreferenceNotice && <DismissibleNotice as="p" className={updatePreferenceNotice.error ? 'error' : 'helper-text'}
          role={updatePreferenceNotice.error ? 'alert' : 'status'} aria-live="polite" aria-atomic="true">{updatePreferenceNotice.text}</DismissibleNotice>}
        {update && <><p className="helper-text">Update trust: {update.publisherTrust}. Stop all managed servers and resolve uncertain process records before updating.</p>
          <DismissibleNotice as="p" className={update.state === 'Unavailable' || update.publisherTrust === 'Signature rejected' ? 'warning-text' : 'helper-text'} role="status">{update.message}</DismissibleNotice>
          {update.state === 'Available' && <Button className="secondary" disabled={pageBusy || instance?.updatesAvailable === false} onClick={onReviewUpdate}><Icon name="refresh" />Review update</Button>}
          <UpdateDetailsPanel update={update} busy={updateBusy} onSnoozed={onReloadUpdate} onBusyChange={setChildBusy} /></>}
      </div>}
      {page === 'shortcuts' && <dl className="settings-shortcuts">
        <div><dt><kbd>Ctrl K</kbd></dt><dd>Command palette</dd></div>
        <div><dt><kbd>Alt 1</kbd></dt><dd>Host</dd></div><div><dt><kbd>Alt 2</kbd></dt><dd>Join</dd></div>
        <div><dt><kbd>Alt 3</kbd></dt><dd>Attention</dd></div><div><dt><kbd>Alt 4</kbd></dt><dd>App Settings</dd></div>
        <div><dt><kbd>Esc</kbd></dt><dd>Close the current dialog or menu</dd></div>
        <div><dt><kbd>Tab</kbd></dt><dd>Move to the next control</dd></div>
      </dl>}
    </div>
    {onClose && <div className="actions"><Button className="secondary" disabled={pageBusy} onClick={onClose}>Done</Button></div>}
  </section>
}

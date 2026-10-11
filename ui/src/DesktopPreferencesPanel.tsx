import { useEffect, useId, useRef, useState, type ComponentProps } from 'react'
import { changeJson, getLocalJson } from './api'
import { parseBasicResult } from './contracts'
import { Button, Input, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'
import { notificationEventKinds, parseNotificationPreferences, parseUpdatePreparation, saveNotificationPreferences,
  trustedReleaseNotesLink, updatePreparationReport, type DesktopUpdateDetails, type NotificationEventKind,
  type NotificationPreferenceChange, type NotificationPreferences, type UpdatePreparation, type UpdateReminderDuration } from './desktopPreferencesWire'

const eventLabels: Record<NotificationEventKind, string> = { Lifecycle: 'Server starts and stops', Backup: 'Backups and shared saves',
  Access: 'Friend access', Connections: 'Pairing and connections', Network: 'Connection help', Recovery: 'Recovery',
  Countdown: 'Shutdown timer', Players: 'Player count changes', Maintenance: 'Maintenance', Configuration: 'Server files',
  AddOns: 'Add-ons', Remote: 'Friend actions', Update: 'App updates' }
export type DesktopNotificationServer = { id: string; name: string; connectionId?: string | null }
export type DesktopNotificationLoader = (signal?: AbortSignal) => Promise<NotificationPreferences>
export type DesktopNotificationSaver = (change: NotificationPreferenceChange, signal?: AbortSignal) => Promise<NotificationPreferences>
const defaultLoader: DesktopNotificationLoader = signal => getLocalJson('/api/local/desktop/notifications', parseNotificationPreferences, signal)

export function DesktopPreferencesPanel({ server, quietMode, onQuietModeChange, loader = defaultLoader,
  saver = saveNotificationPreferences, onBusyChange }: { servers?: DesktopNotificationServer[]; server?: DesktopNotificationServer; quietMode?: boolean;
  onQuietModeChange?: (quiet: boolean) => void; loader?: DesktopNotificationLoader; saver?: DesktopNotificationSaver;
  onBusyChange?: (busy: boolean) => void }) {
  const titleId = useId()
  const [preferences, setPreferences] = useState<NotificationPreferences | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => { onBusyChange?.(busy) }, [busy, onBusyChange])
  useEffect(() => () => { onBusyChange?.(false) }, [onBusyChange])
  const [error, setError] = useState(false)
  const [message, setMessage] = useState('')
  const [reloadKey, setReloadKey] = useState(0)
  const pending = useRef<AbortController | null>(null)
  const quietModeChanged = useRef(onQuietModeChange)
  quietModeChanged.current = onQuietModeChange
  const isServerScope = !!server
  useEffect(() => {
    const controller = new AbortController()
    void loader(controller.signal).then(next => { if (!controller.signal.aborted) {
      setPreferences(next); setError(false)
      if (!isServerScope) quietModeChanged.current?.(next.quietMode)
    } })
      .catch(() => { if (!controller.signal.aborted) setError(true) })
    return () => { controller.abort() }
  }, [loader, quietMode, reloadKey, isServerScope])
  useEffect(() => () => pending.current?.abort(), [])

  const selectedServer = server
  const noticeScope = JSON.stringify([selectedServer?.connectionId, selectedServer?.id])
  const override = selectedServer && preferences?.servers.find(server => server.profileId === selectedServer.id &&
    server.connectionId === (selectedServer.connectionId ?? null))
  const allowed = override?.allowedEvents ?? preferences?.allowedEvents ?? []
  const save = async (change: NotificationPreferenceChange) => {
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError(false); setMessage('')
    try {
      const next = await saver(change, controller.signal)
      if (controller.signal.aborted) return
      setPreferences(next); setMessage('Notification preferences saved on this PC.')
      if ('quietMode' in change) onQuietModeChange?.(next.quietMode)
    } catch { if (!controller.signal.aborted) setError(true) }
    finally { if (!controller.signal.aborted) setBusy(false) }
  }
  const changeEvent = (event: NotificationEventKind, checked: boolean) => {
    const allowedEvents = notificationEventKinds.filter(kind => (!selectedServer || kind !== 'Update') &&
      (kind === event ? checked : allowed.includes(kind)))
    void save(selectedServer ? { profileId: selectedServer.id, connectionId: selectedServer.connectionId ?? null, allowedEvents } :
      { allowedEvents })
  }

  return <section className="desktop-preferences" aria-labelledby={titleId} aria-busy={busy}>
    <h3 id={titleId}>{selectedServer ? `Notifications for ${selectedServer.name}` : 'Notifications on this PC'}</h3>
    <p>{selectedServer ? 'Choices for this server on this PC. App updates use the PC default.' : 'Choose which important events can appear in Windows. Hosting and Friend checks keep running.'}</p>
    {!preferences && !error && <p role="status">Loading notification preferences…</p>}
    {error && <DismissibleNotice scope={noticeScope} noticeKey={reloadKey} className="error" role="alert"><p>Could not save or read notification preferences. Try again.</p>
      <Button className="secondary" disabled={busy} onClick={() => setReloadKey(value => value + 1)}>Try again</Button></DismissibleNotice>}
    {preferences && <>
      {!selectedServer && <label className="setting-toggle"><span><strong>Quiet mode</strong><small>Keep events in Attention and pause Windows popups.</small></span>
        <Input type="checkbox" checked={preferences.quietMode} disabled={busy} onChange={event => void save({ quietMode: event.target.checked })} /></label>}
      {!preferences.systemAllowsNotifications && <DismissibleNotice as="p" scope={noticeScope} className="helper-text">Windows is currently keeping notifications quiet. Events remain in Attention.</DismissibleNotice>}
      {selectedServer && <label>Server alerts<Select value={!override ? 'default' : override.allowedEvents.length ? 'custom' : 'off'} disabled={busy}
        onChange={event => void save({ profileId: selectedServer.id, connectionId: selectedServer.connectionId ?? null,
          allowedEvents: event.target.value === 'default' ? null : event.target.value === 'off' ? [] : preferences.allowedEvents.filter(kind => kind !== 'Update') })}>
        <option value="default">Use PC defaults</option><option value="custom">Custom choices</option><option value="off">Off for this server</option>
      </Select></label>}
      <details className="advanced-block"><summary>Choose notification events</summary>
        {!selectedServer && <p className="helper-text">Default choices for all servers on this PC.</p>}
        {selectedServer && <p className="helper-text">{override ? 'These choices apply only to this server.' : 'This server currently follows the default choices.'}</p>}
        <div className="notification-event-choices">{notificationEventKinds.filter(kind => !selectedServer || kind !== 'Update').map(kind => <label className="check-row" key={kind}>
          <Input type="checkbox" checked={allowed.includes(kind)} disabled={busy} onChange={event => changeEvent(kind, event.target.checked)} />{eventLabels[kind]}</label>)}</div>
        {selectedServer && override && <Button className="text-button" disabled={busy}
          onClick={() => void save({ profileId: selectedServer.id, connectionId: selectedServer.connectionId ?? null, allowedEvents: null })}>Use default choices</Button>}
      </details>
    </>}
    {message && <DismissibleNotice as="p" scope={noticeScope} role="status" aria-live="polite" aria-atomic="true">{message}</DismissibleNotice>}
  </section>
}

export type ServerNotificationPreferencesProps = Omit<ComponentProps<typeof DesktopPreferencesPanel>,
  'servers' | 'server' | 'quietMode' | 'onQuietModeChange'> & { server: DesktopNotificationServer }

// Remount on scope changes so an unfinished save cannot appear in another server's editor.
export function ServerNotificationPreferences({ server, ...props }: ServerNotificationPreferencesProps) {
  return <DesktopPreferencesPanel key={`${server.connectionId ?? 'host'}:${server.id}`} server={server} {...props} />
}

export type ServerNotificationSettingsProps = Omit<ServerNotificationPreferencesProps, 'server'> & {
  serverId: string
  serverName?: string
  connectionId?: string | null
}

export function ServerNotificationSettings({ serverId, serverName = 'this server', connectionId,
  ...props }: ServerNotificationSettingsProps) {
  return <ServerNotificationPreferences server={{ id: serverId, name: serverName, connectionId }} {...props} />
}

const stageLabels: Record<UpdatePreparation['stage'], string> = { Idle: 'Update preparation', Checking: 'Checking release',
  Downloading: 'Downloading update', Verifying: 'Verifying update', Ready: 'Download verified', Checkpoint: 'Protecting local settings',
  Restarting: 'Restarting TogetherServer', Blocked: 'Update needs attention', Failed: 'Update preparation failed' }

export function UpdateDetailsPanel({ update, busy = false, onSnoozed, preparationLoader,
  snoozeSaver, onBusyChange }: { update: DesktopUpdateDetails; busy?: boolean; onSnoozed?: () => void | Promise<void>;
    preparationLoader?: (signal?: AbortSignal) => Promise<UpdatePreparation>;
    snoozeSaver?: (version: string, duration: UpdateReminderDuration) => Promise<{ ok: boolean; message: string }>;
    onBusyChange?: (busy: boolean) => void }) {
  const [polledPreparation, setPolledPreparation] = useState<{ version: string | null; value: UpdatePreparation } | null>(null)
  const [snoozeBusy, setSnoozeBusy] = useState(false)
  useEffect(() => { onBusyChange?.(snoozeBusy) }, [snoozeBusy, onBusyChange])
  useEffect(() => () => { onBusyChange?.(false) }, [onBusyChange])
  const [message, setMessage] = useState('')
  useSingleFlightPolling(async signal => {
    const next = await (preparationLoader ? preparationLoader(signal) :
      getLocalJson('/api/local/update/preparation', parseUpdatePreparation, signal))
    if (!signal.aborted) setPolledPreparation({ version: update.latestVersion, value: next })
  }, 800, () => { setMessage('Could not refresh update progress. The current app keeps working.') }, busy, update.latestVersion)
  const polled = polledPreparation?.version === update.latestVersion ? polledPreparation.value : null
  const preparation = busy ? polled ?? update.preparation : update.preparation ?? polled
  const snooze = async (duration: UpdateReminderDuration) => {
    if (!update.latestVersion) return
    setSnoozeBusy(true); setMessage('')
    try {
      const result = snoozeSaver ? await snoozeSaver(update.latestVersion, duration) :
        await changeJson('/api/local/update/snooze', 'POST', parseBasicResult, { version: update.latestVersion, duration })
      setMessage(result.message)
      if (result.ok) await onSnoozed?.()
    } catch { setMessage('Could not save the update reminder. Try again.') }
    finally { setSnoozeBusy(false) }
  }
  const downloadReport = () => {
    const url = URL.createObjectURL(new Blob([updatePreparationReport(update, preparation)], { type: 'application/json' }))
    const link = document.createElement('a')
    link.href = url; link.download = 'TogetherServer-update-preparation.json'; link.click()
    URL.revokeObjectURL(url)
  }
  return <section className="update-details" aria-label="Update details">
    {update.state === 'Available' && <details className="advanced-block"><summary>What changed in version {update.latestVersion}</summary>
      {update.releaseNotes ? <pre className="update-release-notes">{update.releaseNotes}</pre> : <p>No release notes were supplied for this version.</p>}
      {trustedReleaseNotesLink(update.releaseNotesUrl, update.latestVersion) &&
        <a href={update.releaseNotesUrl!} target="_blank" rel="noopener noreferrer">Open this release on GitHub</a>}
    </details>}
    {update.state === 'Available' && <div className="update-reminder-controls">
      {update.versionSkipped ? <p>This version's reminder is skipped. A different release will be shown. You can update now.</p> :
        update.promptSnoozed && update.snoozedUntilUtc && <p>Reminder paused until {new Date(update.snoozedUntilUtc).toLocaleString()}. You can update now.</p>}
      <label>Remind me<Select defaultValue="" disabled={busy || snoozeBusy} onChange={event => { const value = event.target.value;
        if (value) void snooze(value as UpdateReminderDuration); event.target.value = '' }}>
        <option value="">Choose a reminder</option><option value="OneHour">In one hour</option><option value="Tomorrow">Tomorrow</option>
        <option value="ThreeDays">In three days</option><option value="SevenDays">In one week</option>
        <option value="SkipVersion">Skip this version's reminder</option>
        {update.promptSnoozed && <option value="Clear">Resume reminders</option>}
      </Select></label>
    </div>}
    {preparation && (busy || preparation.stage !== 'Idle') && <div className="update-preparation" aria-busy={busy}>
      {preparation.stage === 'Blocked' || preparation.stage === 'Failed' ?
        <DismissibleNotice as="p" scope={update.latestVersion ?? undefined} role="status" aria-live="polite" aria-atomic="true"><strong>{stageLabels[preparation.stage]}</strong> · {preparation.message}</DismissibleNotice> :
        <p role="status" aria-live="polite" aria-atomic="true"><strong>{stageLabels[preparation.stage]}</strong> · {preparation.message}</p>}
      {preparation.totalBytes !== null && preparation.stage === 'Downloading' && <>
        <progress aria-label="Update download" max={preparation.totalBytes} value={preparation.downloadedBytes} />
        <small>{Math.round(preparation.downloadedBytes / preparation.totalBytes * 100)}% downloaded</small>
      </>}
      {preparation.blocker && <DismissibleNotice as="p" scope={update.latestVersion ?? undefined} className="error" role="alert">{preparation.blocker}</DismissibleNotice>}
      <Button className="text-button" onClick={downloadReport}>Download preparation report</Button>
    </div>}
    {message && <DismissibleNotice as="p" scope={update.latestVersion ?? undefined} role="status" aria-live="polite" aria-atomic="true">{message}</DismissibleNotice>}
  </section>
}

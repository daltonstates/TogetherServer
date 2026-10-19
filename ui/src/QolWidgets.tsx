import { Button, Input, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import type { ActionNotice } from './actionFeedback'
import type { UiPreferences } from './qolPreferences'

export function AppearancePreferences({ value, onChange }: {
  value: UiPreferences; onChange: (next: UiPreferences) => void
}) {
  return <fieldset className="appearance-preferences"><legend>Appearance</legend>
    <label>Theme<Select aria-label="Theme" value={value.theme} onChange={event => onChange({ ...value, theme: event.target.value as UiPreferences['theme'] })}>
      <option value="system">Use Windows preference</option><option value="dark">Dark</option><option value="light">Light</option>
    </Select></label>
    <label>Layout<Select aria-label="Layout" value={value.density} onChange={event => onChange({ ...value, density: event.target.value as UiPreferences['density'] })}>
      <option value="comfortable">Comfortable</option><option value="compact">Compact</option>
    </Select></label>
    <label>Text size<Select aria-label="Text size" value={value.textScale} onChange={event => onChange({ ...value, textScale: Number(event.target.value) as UiPreferences['textScale'] })}>
      {[100, 115, 130, 150].map(scale => <option value={scale} key={scale}>{scale}%</option>)}
    </Select></label>
    <label className="check-row"><Input type="checkbox" checked={value.highContrast} onChange={event => onChange({ ...value, highContrast: event.target.checked })} />Stronger contrast and status outlines</label>
  </fieldset>
}

const explanations: Record<string, string> = {
  Offline: 'The server is stopped. Start it when you are ready to play.',
  Starting: 'Starting the server. Waiting for the game to report ready.',
  'Process running': 'The server process has started. Waiting for the game to report ready.',
  Listening: 'The server is listening on this PC. Test joining in the game.',
  Ready: 'The game reports ready on this PC. Test joining from another PC.',
  Stopping: 'Waiting for the game to save and stop.',
  Failed: 'The last action failed. Check the message and logs before trying again.',
  Unknown: 'TogetherServer cannot confirm the server state. Review server health and Recovery.'
}

export function ServerStateExplanation({ state }: { state: string }) {
  return <p className="server-state-explanation" role="status" aria-live="polite" aria-atomic="true">{explanations[state] ?? 'Refresh to check the server status.'}</p>
}

export function ActionFeedback({ notice, label = 'Result' }: {
  notice: ActionNotice | null; label?: string
}) {
  return notice ? <DismissibleNotice as="p" className={`action-feedback ${notice.good ? 'good' : 'bad'}`} role="status" aria-atomic="true"
    noticeKey={notice.receivedAtMs} dismissLabel="Dismiss result">
    <strong>{label}: </strong>{notice.text}
  </DismissibleNotice> : null
}

export function TimerPresets({ minutes, deadline, savedMinutes, busy, onChange, onAdd }: {
  minutes: string; deadline: string | null; savedMinutes: number; busy: boolean
  onChange: (minutes: string) => void; onAdd: (minutes: number) => void
}) {
  const amount = Number(minutes)
  const valid = Number.isSafeInteger(amount) && amount > 0 && amount <= 1440
  const target = deadline && valid ? new Date(Date.parse(deadline) + amount * 60_000) : null
  return <div className="timer-presets">
    <div className="actions">{[15, 30, 60].map(value => <Button key={value} className="secondary" disabled={busy} onClick={() => { onChange(String(value)); onAdd(value) }}>+{value} min</Button>)}</div>
    <small>{target && Number.isFinite(target.getTime()) ? `After adding ${amount} minutes: ${target.toLocaleString()}.` :
      valid ? `After adding: ${savedMinutes + amount} extra minutes when the server is next empty.` : 'Enter a positive whole number of minutes.'}</small>
  </div>
}

export function pendingDescription(key: string, profiles: Array<{ id: string; name: string }> = [], profileId?: string): string {
  if (!key) return ''
  const name = profiles.find(profile => profile.id === profileId || key.includes(profile.id))?.name
  const labels: Array<[string, string]> = [
    ['safe-restart', 'Stopping, backing up, then restarting'], ['manual-backup', 'Creating a world backup'],
    ['verify-backup', 'Checking the backup'], ['vault-backup', 'Copying a verified backup'],
    ['rehearse-backup', 'Testing a backup copy'], ['restore', 'Reviewing and restoring a backup'],
    ['probe-game', 'Checking the game connection'], ['friend-start', 'Requesting server start'],
    ['friend-stop', 'Requesting server stop'], ['friend-restart', 'Requesting server restart'],
    ['refresh', 'Refreshing status'], ['start', 'Starting server'], ['stop', 'Stopping server'],
    ['restart', 'Restarting server'], ['extend', 'Adding shutdown time'], ['backups', 'Loading backups'],
    ['invite', 'Preparing the server code'], ['save', 'Saving changes'], ['pair', 'Connecting to the Host'],
    ['mode', 'Opening page'], ['poll', 'Refreshing Host status']
  ]
  const text = labels.find(([prefix]) => key.includes(prefix))?.[1] ?? 'Finishing up'
  return name ? `${text} · ${name}` : text
}

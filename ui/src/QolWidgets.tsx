import { Button, Input, Select } from './Controls'
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
  Offline: 'The managed server is stopped. Start it when you are ready to play.',
  Starting: 'The start request is running. Waiting for the game’s local readiness signal.',
  'Process running': 'The recorded process is running; the game has not confirmed readiness.',
  Listening: 'A local game listener answered. A real game join still needs checking.',
  Ready: 'The game reported local readiness. Outside reachability and a real join are separate checks.',
  Stopping: 'A graceful stop is in progress. Waiting for the exact managed process to exit.',
  Failed: 'The last operation failed. Review its explanation and logs before retrying.',
  Unknown: 'The app cannot currently establish a safe server state. Review the existing health and recovery checks.'
}

export function ServerStateExplanation({ state }: { state: string }) {
  return <p className="server-state-explanation" role="status" aria-live="polite" aria-atomic="true">{explanations[state] ?? 'Refresh status to review the current game-server observation.'}</p>
}

export function ActionFeedback({ notice, label = 'Result' }: {
  notice: { good: boolean; text: string } | null; label?: string
}) {
  return notice ? <p className={`action-feedback ${notice.good ? 'good' : 'bad'}`} role="status" aria-atomic="true">
    <strong>{label}: </strong>{notice.text}
  </p> : null
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
      valid ? `After adding: ${savedMinutes + amount} saved minutes for the next exact-zero idle window.` : 'Enter a positive whole number of minutes.'}</small>
  </div>
}

export function pendingDescription(key: string, profiles: Array<{ id: string; name: string }> = [], profileId?: string): string {
  if (!key) return ''
  const name = profiles.find(profile => profile.id === profileId || key.includes(profile.id))?.name
  const labels: Array<[string, string]> = [
    ['safe-restart', 'Stopping, backing up, then restarting'], ['manual-backup', 'Creating a world backup'],
    ['verify-backup', 'Verifying backup integrity'], ['vault-backup', 'Copying a verified backup'],
    ['rehearse-backup', 'Testing a disposable restore'], ['restore', 'Reviewing and restoring a backup'],
    ['probe-game', 'Checking the selected game route'], ['friend-start', 'Requesting server start'],
    ['friend-stop', 'Requesting guarded stop'], ['friend-restart', 'Requesting guarded restart'],
    ['refresh', 'Refreshing status'], ['start', 'Starting server'], ['stop', 'Stopping server'],
    ['restart', 'Restarting server'], ['extend', 'Adding shutdown time'], ['backups', 'Loading backups'],
    ['invite', 'Preparing the server code'], ['save', 'Saving changes'], ['pair', 'Connecting to the Host'],
    ['mode', 'Opening workspace'], ['poll', 'Refreshing Host status']
  ]
  const text = labels.find(([prefix]) => key.includes(prefix))?.[1] ?? 'Completing the requested action'
  return name ? `${text} · ${name}` : text
}

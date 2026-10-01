import { Button, Input } from './Controls'

export function MaintenanceGuide({ enabled, message, state, onlinePlayers, countTrusted,
  lastBackupUtc, busy, onMessage, onToggle, onStop, onBackup, onStart, onOpenDoctor }: {
  enabled: boolean
  message: string
  state: string
  onlinePlayers: number | null
  countTrusted: boolean
  lastBackupUtc: string | null
  busy: boolean
  onMessage: (message: string) => void
  onToggle: (enabled: boolean) => void
  onStop: () => void
  onBackup: () => void
  onStart: () => void
  onOpenDoctor: () => void
}) {
  const empty = countTrusted && onlinePlayers === 0
  return <details className="advanced-block maintenance-guide" open={enabled || undefined}>
    <summary>{enabled ? 'Maintenance in progress' : 'Prepare maintenance'}</summary>
    <ol>
      <li><strong>Tell Friends and pause remote actions.</strong>
        <label>Message for connected PCs<Input maxLength={200} value={message} onChange={event => onMessage(event.target.value)} placeholder="Updating the game server" /></label>
        <Button className="secondary" disabled={busy} onClick={() => onToggle(true)}>{enabled ? 'Save message' : 'Begin maintenance'}</Button>
      </li>
      <li><strong>Wait until the game server is empty, then stop it gracefully.</strong>
        <small>{state === 'Offline' ? 'Server is offline.' : empty ? 'The server reports 0 players.' : countTrusted ? `${onlinePlayers} player${onlinePlayers === 1 ? '' : 's'} online.` : 'Player count unavailable. Check it again before stopping for maintenance.'}</small>
        {state === 'Ready' && <Button className="secondary" disabled={busy || !enabled || !empty} onClick={onStop}>Stop empty server</Button>}
      </li>
      <li><strong>Make an offline checkpoint.</strong>
        {lastBackupUtc && <small>Last completed backup: {new Date(lastBackupUtc).toLocaleString()}.</small>}
        <Button className="secondary" disabled={busy || !enabled || state !== 'Offline'} onClick={onBackup}>Back up now</Button>
        <small>Verify the new backup in World Safety Center before changing the game server files.</small>
      </li>
      <li><strong>Apply the change, then check the game.</strong>
        <small>Use the game provider's installer yourself. Start the server and test a real Friend join and saved change.</small>
        {state === 'Offline' && <Button className="secondary" disabled={busy || !enabled} onClick={onStart}>Start server</Button>}
        {state === 'Ready' && <Button className="secondary" disabled={busy || !enabled} onClick={onOpenDoctor}>Open Connection Doctor</Button>}
      </li>
    </ol>
    {enabled && <Button className="text-button" disabled={busy} onClick={() => onToggle(false)}>End maintenance</Button>}
  </details>
}

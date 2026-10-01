import { Button } from './Controls'
import type { Device } from './contracts'

export function TemporaryHelperAccess({ device, busy, nowMs, onGrant, onEnd }: {
  device: Device
  busy: boolean
  nowMs: number
  onGrant: (duration: 'OneHour' | 'EightHours') => void
  onEnd: () => void
}) {
  const until = device.temporaryHelperUntilUtc ? Date.parse(device.temporaryHelperUntilUtc) : null
  const active = until !== null && Number.isFinite(until) && until > nowMs && device.temporaryHelperActive
  const unavailable = busy || !device.paired || device.approvalPending || device.accessExpired
  return <section className="temporary-helper" aria-label="Temporary helper access">
    <div><strong>Temporary helper access</strong><small>{active
      ? `This PC can start, request guarded Stop, add time, and view logs until ${new Date(until).toLocaleString()}. Its usual permissions return automatically.`
      : 'Give this PC helper controls for a short time. Its usual permissions and server assignments stay saved.'}</small></div>
    <div className="actions">{active
      ? <Button className="secondary" disabled={busy} onClick={onEnd}>End now</Button>
      : <><Button className="secondary" disabled={unavailable} onClick={() => onGrant('OneHour')}>Grant 1 hour</Button>
        <Button className="secondary" disabled={unavailable} onClick={() => onGrant('EightHours')}>Grant 8 hours</Button></>}</div>
    {active && <small>End temporary access before editing usual permissions below.</small>}
  </section>
}

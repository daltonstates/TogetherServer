import { useEffect, useRef } from 'react'
import { Button } from './Controls'
import { Icon } from './Icon'

export type ConnectionField = {
  id: string
  label: string
  value: string
  revealed: boolean
  copying: boolean
  revealing: boolean
  onReveal: () => void
  onHide: () => void
  onCopy: () => void
}

function PrivateConnectionField({ field }: { field: ConnectionField }) {
  const hideRef = useRef(field.onHide)
  hideRef.current = field.onHide

  useEffect(() => {
    if (!field.revealed) return
    const hide = () => hideRef.current()
    const hideWhenBackgrounded = () => { if (document.hidden) hide() }
    const timer = window.setTimeout(hide, 30_000)
    window.addEventListener('blur', hide)
    document.addEventListener('visibilitychange', hideWhenBackgrounded)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('blur', hide)
      document.removeEventListener('visibilitychange', hideWhenBackgrounded)
    }
  }, [field.revealed])

  const busy = field.copying || field.revealing
  const visibilityLabel = `${field.revealed ? 'Hide' : 'Show'} ${field.label}`
  const labelId = `connection-field-${field.id}`
  return <div className="connection-field" aria-busy={busy} role="group" aria-labelledby={labelId}>
    <div className="connection-field-heading">
      <span className="connection-field-label" id={labelId}>{field.label}</span>
      <div className="connection-field-actions">
        <Button className="icon-button privacy-toggle" disabled={busy}
          onClick={field.revealed ? field.onHide : field.onReveal}
          aria-label={visibilityLabel} title={visibilityLabel} aria-pressed={field.revealed}>
          <Icon name={field.revealing ? 'loader' : field.revealed ? 'eyeOff' : 'eye'} />
        </Button>
        <Button className="secondary private-copy" disabled={busy} onClick={field.onCopy}
          aria-label={`Copy ${field.label}`} title={`Copy ${field.label}`}>
          <Icon name={field.copying ? 'loader' : 'copy'} />{field.copying ? 'Copying…' : 'Copy'}
        </Button>
      </div>
    </div>
    <div className="connection-field-value">{field.revealed
      ? <code>{field.value}</code>
      : <><span className="privacy-mask" aria-hidden="true">••••••••••••</span><span className="sr-only">Hidden</span></>}</div>
  </div>
}

export function ConnectionDetails({ fields, refreshing = false, note }: {
  fields: ConnectionField[]
  refreshing?: boolean
  note?: string
}) {
  const busy = fields.some(field => field.copying || field.revealing)
  return <section className="connection-details-card" aria-label="Connection details" aria-busy={busy || refreshing}>
    <div className="connection-details-heading">
      <strong><Icon name="link" />Connection details</strong>
      <small>Hidden for stream safety · each value has its own controls</small>
    </div>
    {refreshing && <div className="connection-refreshing" role="status"><Icon name="loader" />Refreshing connection details…</div>}
    <div className={refreshing ? 'connection-fields refreshing' : 'connection-fields'}>
      {fields.map(field => <PrivateConnectionField field={field} key={field.id} />)}
    </div>
    <p className="connection-privacy-note">Use an eye to show only that value. Shown values hide after 30 seconds or when the app loses focus. Copy keeps it hidden.{note && <> {note}</>}</p>
  </section>
}

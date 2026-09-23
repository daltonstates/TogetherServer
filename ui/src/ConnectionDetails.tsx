import { useEffect, useRef } from 'react'
import { Button } from './Controls'
import { Icon } from './Icon'

export type ConnectionField = {
  label: string
  value: string
}

export function ConnectionDetails({ fields, revealed, copying, revealing, refreshing = false, note,
  onReveal, onHide, onCopy }: {
  fields: ConnectionField[]
  revealed: boolean
  copying: boolean
  revealing: boolean
  refreshing?: boolean
  note?: string
  onReveal: () => void
  onHide: () => void
  onCopy: () => void
}) {
  const hideRef = useRef(onHide)
  hideRef.current = onHide

  useEffect(() => {
    if (!revealed) return
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
  }, [revealed])

  const busy = copying || revealing
  return <section className="connection-details-card" aria-label="Connection details" aria-busy={busy || refreshing}>
    <div className="connection-details-heading">
      <div><strong><Icon name="link" />Connection details</strong><small>Hidden for stream safety</small></div>
      <div className="connection-details-actions">
        <Button className="icon-button privacy-toggle" disabled={busy} onClick={revealed ? onHide : onReveal}
          aria-label={revealed ? 'Hide connection details' : 'Reveal connection details'}
          title={revealed ? 'Hide connection details' : 'Reveal connection details'}>
          <Icon name={revealing ? 'loader' : revealed ? 'eyeOff' : 'eye'} />
        </Button>
        <Button className="secondary private-copy" disabled={busy} onClick={onCopy}>
          <Icon name={copying ? 'loader' : 'copy'} />{copying ? 'Copying…' : 'Copy without revealing'}
        </Button>
      </div>
    </div>
    {refreshing && <div className="connection-refreshing" role="status"><Icon name="loader" />Refreshing connection details…</div>}
    <dl className={refreshing ? 'connection-fields refreshing' : 'connection-fields'}>
      {fields.map(field => <div className="connection-field" key={field.label}>
        <dt>{field.label}</dt>
        <dd>{revealed
          ? <code>{field.value}</code>
          : <><span className="privacy-mask" aria-hidden="true">••••••••••••</span><span className="sr-only">Hidden</span></>}</dd>
      </div>)}
    </dl>
    <p className="connection-privacy-note">{revealed
      ? 'Automatically hides after 30 seconds or when the app loses focus.'
      : 'Copying keeps these values hidden on screen.'}{note && <> {note}</>}</p>
  </section>
}

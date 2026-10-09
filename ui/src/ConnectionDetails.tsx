import { useEffect, useId, useRef, useState } from 'react'
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
  disabled?: boolean
}

export type JoinAddressParts = { address: string; port: string }

// A join address is data for the game's fields, never a URL or launch argument.
export function splitJoinAddress(value: string | null | undefined): JoinAddressParts | null {
  if (!value || value.length > 300 || /[\s\p{Cc}\p{Cf}/\\@?#]/u.test(value)) return null
  const match = /^(\[[^\]]+\]|[^:]+):([0-9]{1,5})$/.exec(value)
  if (!match || Number(match[2]) < 1 || Number(match[2]) > 65535) return null
  let address = match[1]
  if (address.startsWith('[')) {
    const ipv6 = address.slice(1, -1)
    if (!ipv6.includes(':') || !/^[0-9a-f:.]+$/i.test(ipv6)) return null
    try { if (!new URL(`http://${address}`).hostname.startsWith('[')) return null }
    catch { return null }
    address = ipv6
  } else if (/^[0-9.]+$/.test(address)) {
    const octets = address.split('.')
    if (octets.length !== 4 || octets.some(octet => !/^(0|[1-9][0-9]{0,2})$/.test(octet) || Number(octet) > 255)) return null
  } else {
    const dns = address.endsWith('.') ? address.slice(0, -1) : address
    if (dns.length > 253 || !dns.split('.').every(label => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i.test(label))) return null
  }
  return { address, port: String(Number(match[2])) }
}

function PrivateConnectionField({ field }: { field: ConnectionField }) {
  const labelId = useId()
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
  return <div className="connection-field" aria-busy={busy} role="group" aria-labelledby={labelId}>
    <div className="connection-field-heading">
      <span className="connection-field-label" id={labelId}>{field.label}</span>
      <div className="connection-field-actions">
        <Button className="icon-button privacy-toggle" disabled={busy || field.disabled}
          onClick={field.revealed ? field.onHide : field.onReveal}
          aria-label={visibilityLabel} title={visibilityLabel} aria-pressed={field.revealed}>
          <Icon name={field.revealing ? 'loader' : field.revealed ? 'eyeOff' : 'eye'} />
        </Button>
        <Button className="secondary private-copy" disabled={busy || field.disabled} onClick={field.onCopy}
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

function SplitConnectionEndpoint({ field, parts, refreshing, onCopyPart }: {
  field: ConnectionField
  parts: JoinAddressParts
  refreshing: boolean
  onCopyPart?: (part: keyof JoinAddressParts, value: string) => void | Promise<void>
}) {
  const [shown, setShown] = useState({ address: false, port: false })
  const [copying, setCopying] = useState<keyof JoinAddressParts | null>(null)
  const [message, setMessage] = useState('')
  const mounted = useRef(false), pending = useRef(false)
  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])
  async function copy(part: keyof JoinAddressParts) {
    if (pending.current || refreshing || field.copying || field.revealing || field.disabled) return
    pending.current = true; setCopying(part); setMessage('')
    try {
      if (onCopyPart) await onCopyPart(part, parts[part])
      else await navigator.clipboard.writeText(parts[part])
      if (mounted.current) setMessage(`${part === 'address' ? 'Server address' : 'Port'} copied. It stays hidden.`)
    } catch { if (mounted.current) setMessage('Could not copy. Clipboard access is unavailable.') }
    finally { pending.current = false; if (mounted.current) setCopying(null) }
  }
  return <>
    {(['address', 'port'] as const).map(part => <PrivateConnectionField key={part} field={{
      id: `${field.id}-${part}`, label: part === 'address' ? 'Server address' : 'Port', value: parts[part],
      revealed: shown[part], copying: copying === part, revealing: false,
      disabled: refreshing || !!copying || field.copying || field.revealing || field.disabled,
      onReveal: () => setShown(current => ({ ...current, [part]: true })),
      onHide: () => setShown(current => ({ ...current, [part]: false })), onCopy: () => void copy(part)
    }} />)}
    {message && <small role="status" style={{ gridColumn: '1 / -1' }}>{message}</small>}
  </>
}

export function ConnectionDetails({ fields, refreshing = false, note, kind, endpointFieldId, onCopyPart }: {
  fields: ConnectionField[]
  refreshing?: boolean
  note?: string
  kind?: string
  endpointFieldId?: string
  onCopyPart?: (part: keyof JoinAddressParts, value: string) => void | Promise<void>
}) {
  const busy = fields.some(field => field.copying || field.revealing)
  const splitField = ['MinecraftBedrock', 'Terraria'].includes(kind ?? '')
    ? fields.find(field => field.id === (endpointFieldId ?? fields[0]?.id)) : undefined
  const parts = splitField ? splitJoinAddress(splitField.value) : null
  return <section className="connection-details-card" aria-label="Connection details" aria-busy={busy || refreshing}>
    <div className="connection-details-heading">
      <strong><Icon name="link" />Connection details</strong>
      <small>Hidden for stream safety · each value has its own controls</small>
    </div>
    {refreshing && <div className="connection-refreshing" role="status"><Icon name="loader" />Refreshing connection details…</div>}
    <div className={refreshing ? 'connection-fields refreshing' : 'connection-fields'}>
      {fields.map(field => field === splitField && parts
        ? <SplitConnectionEndpoint key={`${field.id}:${field.value}`} field={field} parts={parts}
            refreshing={refreshing} onCopyPart={onCopyPart} />
        : <PrivateConnectionField field={field} key={field.id} />)}
    </div>
    {splitField && !parts && <p className="helper-text">Separate address and port are unavailable. Ask the Host to check the game address.</p>}
    <p className="connection-privacy-note">Use an eye to show only that value. Shown values hide after 30 seconds or when the app loses focus. Copy keeps it hidden.{note && <> {note}</>}</p>
  </section>
}

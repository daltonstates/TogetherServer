import { useEffect, useRef, useState } from 'react'
import { changeJson } from './api'
import { ContractError, type Decoder } from './contracts'
import { Button } from './Controls'
import { Icon } from './Icon'
import './SharedGamePassword.css'

export type SharedPasswordResult = { ok: boolean; code: string; message: string; profileId: string; password?: string | null }
export const parseSharedPassword: Decoder<SharedPasswordResult> = value => {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError('The password reply is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.ok !== 'boolean' || typeof item.code !== 'string' || item.code.length > 100 ||
    typeof item.message !== 'string' || item.message.length > 1000 || typeof item.profileId !== 'string' ||
    !/^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i.test(item.profileId) ||
    item.ok && (typeof item.password !== 'string' || item.password.length < 5 || item.password.length > 64 || /\p{Cc}/u.test(item.password)) ||
    !item.ok && item.password != null)
    throw new ContractError('The password reply is incomplete or invalid.')
  return { ok: item.ok, code: item.code, message: item.message, profileId: item.profileId, password: item.ok ? item.password as string : null }
}

/** Every Show/Copy asks the current Host; no background fetch or saved copy. */
export function SharedGamePassword({ profileId, connectionId, allowed, current, supported, changedUtc, identity }: {
  profileId: string; connectionId: string; allowed: boolean; current: boolean; supported: boolean
  changedUtc?: string | null; identity: string
}) {
  const [password, setPassword] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const sequence = useRef(0)
  const request = useRef<AbortController | null>(null)
  const scope = JSON.stringify([profileId, connectionId, allowed, current, supported, changedUtc, identity])
  const currentScope = useRef(scope)
  const revealedScope = useRef('')
  currentScope.current = scope
  useEffect(() => {
    setPassword(null); setNotice(''); setBusy(false); sequence.current += 1; request.current?.abort()
    const hide = () => { sequence.current += 1; request.current?.abort(); setPassword(null); setBusy(false) }
    const visibility = () => { if (document.visibilityState !== 'visible') hide() }
    window.addEventListener('blur', hide); document.addEventListener('visibilitychange', visibility)
    return () => {
      sequence.current += 1; request.current?.abort()
      window.removeEventListener('blur', hide); document.removeEventListener('visibilitychange', visibility)
    }
  }, [scope])
  useEffect(() => {
    if (!password) return
    const timer = window.setTimeout(() => setPassword(null), 30_000)
    return () => window.clearTimeout(timer)
  }, [password])
  const read = async (copy: boolean) => {
    if (!allowed || !current || !supported || busy) return
    const next = ++sequence.current
    const expectedScope = scope
    const abort = new AbortController()
    request.current = abort
    setBusy(true); setNotice(''); setPassword(null)
    const timer = window.setTimeout(() => {
      if (sequence.current !== next) return
      abort.abort(); sequence.current += 1; setBusy(false); setNotice('The password request timed out. Check your connection and try again.')
    }, 15_000)
    try {
      const result = await changeJson(`/api/local/friend/${encodeURIComponent(profileId)}/game-password/reveal`, 'POST', parseSharedPassword,
        { connectionId }, abort.signal)
      if (abort.signal.aborted || sequence.current !== next || currentScope.current !== expectedScope) return
      if (!result.ok || result.profileId !== profileId || !result.password) {
        setNotice('The Host did not share a current password. Ask them to check this PC’s password permission.'); return
      }
      if (copy) {
        await navigator.clipboard.writeText(result.password)
        if (!abort.signal.aborted && sequence.current === next) setNotice('Current game password copied.')
      } else { revealedScope.current = expectedScope; setPassword(result.password) }
    } catch {
      if (!abort.signal.aborted && sequence.current === next) setNotice(copy
        ? 'Could not copy the current password. Check your access and try Show instead.'
        : 'Could not get the current password. Check your connection and access, then try again.')
    } finally { window.clearTimeout(timer); if (sequence.current === next) setBusy(false) }
  }
  const shownPassword = allowed && current && supported && revealedScope.current === scope ? password : null
  const updated = changedUtc && Number.isFinite(Date.parse(changedUtc)) ? new Date(changedUtc).toLocaleString() : null
  return <section className="shared-game-password" aria-label="Shared game password" aria-busy={busy}>
    <div><strong><Icon name="shield" />Game password</strong><small>{!supported ? 'This Host needs an update to share passwords in the app.'
      : !allowed ? 'Ask the Host to allow game password access for this PC.'
        : !current ? 'Reconnect to the Host to get the current password.' : updated ? `Host updated ${updated}` : 'Get the current password from the Host.'}</small></div>
    <div className="shared-game-password-value">{shownPassword ? <code>{shownPassword}</code> : <span aria-label="Game password hidden">••••••••••</span>}</div>
    <div className="actions">{shownPassword ? <Button className="secondary" onClick={() => setPassword(null)}><Icon name="eyeOff" />Hide password</Button> :
      <Button className="secondary" disabled={busy || !allowed || !current || !supported} onClick={() => void read(false)}><Icon name={busy ? 'loader' : 'eye'} />Show password</Button>}
      <Button className="secondary" disabled={busy || !allowed || !current || !supported} onClick={() => void read(true)}><Icon name="copy" />Copy password</Button></div>
    {notice && <p role="status">{notice}</p>}
    {allowed && supported && <small>Show hides again after 30 seconds or when you leave the app. Copy fetches the latest password each time.</small>}
  </section>
}

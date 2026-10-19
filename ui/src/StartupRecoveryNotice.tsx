import { useId, useState } from 'react'
import { Button } from './Controls'
import type { StartupRecoveryView } from './contracts'
import { Icon } from './Icon'
import './StartupRecoveryNotice.css'

const dismissalKey = 'togetherserver.startup-recovery-dismissal.v1'

function readDismissal(): string | null {
  try { return sessionStorage.getItem(dismissalKey) }
  catch { return null }
}

export function StartupRecoveryNotice({ recovery, profiles, busy, pendingProfileId, onResume, review = false }: {
  recovery: StartupRecoveryView
  profiles: { id: string; name: string }[]
  busy: boolean
  pendingProfileId?: string
  onResume: (profileId: string) => void
  review?: boolean
}) {
  const titleId = useId()
  const [dismissed, setDismissed] = useState(readDismissal)
  // Only session identity and recovery states matter, not polling order or display text.
  const signature = JSON.stringify([recovery.previousStartedUtc, recovery.items
    .map(item => [item.profileId, item.state, item.canResume]).sort((a, b) => String(a[0]).localeCompare(String(b[0])))])
  if (!recovery.previousSessionInterrupted || (!review && dismissed === signature)) return null

  const dismiss = () => {
    setDismissed(signature)
    try { sessionStorage.setItem(dismissalKey, signature) }
    catch { /* Dismissal still works in this view when optional browser storage is unavailable. */ }
  }
  const content = <section className="panel startup-recovery" aria-labelledby={titleId}>
    <div className="section-heading">
      <div className="startup-recovery-heading"><h2 id={titleId}>App reopened after an interruption</h2><p>{recovery.message}</p></div>
      {!review && <Button className="secondary startup-recovery-dismiss" aria-label="Dismiss app recovery notice" onClick={dismiss}>
        <Icon name="close" />Dismiss</Button>}
    </div>
    {recovery.items.length > 0 && <div className="choices">{recovery.items.map(item => <div className="choice" key={item.profileId}>
      <span><strong>{profiles.find(profile => profile.id === item.profileId)?.name ?? 'Saved server'} · {item.state}</strong><small>{item.detail}</small></span>
      {item.canResume && <Button disabled={busy} onClick={() => onResume(item.profileId)}>
        {pendingProfileId === item.profileId ? 'Resuming…' : 'Resume hosting'}</Button>}
    </div>)}</div>}
    <details className="startup-recovery-details"><summary>About app recovery</summary>
      <p>{recovery.windowsRestartRegistered ? 'Windows can reopen this app after it crashes.' : 'This build cannot ask Windows to reopen the app after a crash.'} Reopening the app never starts a game server automatically.</p>
      {!review && <p>Review this notice again in Attention under Session recovery.</p>}
    </details>
  </section>

  return review ? <details className="startup-recovery-review"><summary><Icon name="refresh" />Session recovery</summary>{content}</details> : content
}

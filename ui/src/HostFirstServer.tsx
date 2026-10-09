import { useId, type ReactNode } from 'react'
import { Button } from './Controls'
import { Icon } from './Icon'

export function HostFirstServer({ busy, resume, recovered, recovery, onCreate, onResume, onJoin, onMove }: {
  busy: boolean
  resume: boolean
  recovered: boolean
  recovery: ReactNode
  onCreate: () => void
  onResume: () => void
  onJoin: () => void
  onMove: () => void
}) {
  const headingId = useId()
  return <section className="panel host-first-server" aria-labelledby={headingId}>
    <div className="section-heading"><div><h2 id={headingId}>Set up a server</h2>
      <p>Choose a game, a world and its server app. Review everything before starting.</p></div></div>
    {recovery}
    {resume && <div className="host-setup-continuation"><strong>Setup in progress</strong>
      <p>Your unfinished setup is still here. Enter the game password again before saving.</p>
      <Button disabled={busy} onClick={onResume}>Continue setup</Button></div>}
    <div className="actions host-first-server-actions">
      {!resume && <Button className={recovered ? 'secondary' : undefined} disabled={busy} onClick={onCreate}>
        <Icon name="server" />Host a server</Button>}
      <Button className="secondary" disabled={busy} onClick={onJoin}><Icon name="link" />Join a server</Button>
    </div>
    <div className="actions host-first-server-footer"><Button className="text-button" disabled={busy} onClick={onMove}>
      Move hosting from another PC</Button></div>
  </section>
}

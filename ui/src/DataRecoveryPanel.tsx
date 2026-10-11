import { useRef, useState } from 'react'
import { Button, Input } from './Controls'
import { useConfirmation } from './Confirmation'
import { Icon } from './Icon'
import type { DataRecoveryView, Run, Snapshot } from './contracts'
import { safeFileName } from './setupDraft'

type DataRecoveryPanelProps = {
  recovery: DataRecoveryView
  mode: Snapshot['mode'] | null
  runs: Run[]
  configuredProfileIds: string[]
  pending: string
  confirmed: boolean
  onConfirmedChange: (confirmed: boolean) => void
  onAcknowledge: () => void
  onSwitchToHost: () => void
  onStopRecordedRun: (profileId: string) => void
  onForgetRecordedRun: (profileId: string) => void
}

export function DataRecoveryPanel({ recovery, mode, runs, configuredProfileIds, pending, confirmed,
  onConfirmedChange, onAcknowledge, onSwitchToHost, onStopRecordedRun, onForgetRecordedRun }:
  DataRecoveryPanelProps) {
  const { confirm, confirmationDialog } = useConfirmation()
  const [dismissed, setDismissed] = useState<string | null>(null)
  const noticeIdentity = JSON.stringify([recovery.lifecycleBlocked, recovery.notices])
  const inFlight = useRef(false)
  const guard = JSON.stringify({ recovery, mode, pending, configuredProfileIds,
    runs: runs.map(run => ({ profileId: run.profileId, state: run.state,
      runOperationId: run.runOperationId, processId: run.processId })) })
  const current = useRef({ guard, revision: 0 })
  if (current.current.guard !== guard) current.current = { guard, revision: current.current.revision + 1 }

  const forget = async (profileId: string) => {
    if (inFlight.current || pending || mode !== 'Host' || configuredProfileIds.includes(profileId) ||
      !runs.some(run => run.profileId === profileId && run.state === 'Failed')) return
    const revision = current.current.revision
    inFlight.current = true
    try {
      if (await confirm({ title: 'Forget exited record',
        description: 'Forget this saved session only if TogetherServer confirms that its server process is no longer running?',
        confirmLabel: 'Forget exited record', destructive: true }) && current.current.revision === revision)
        onForgetRecordedRun(profileId)
    } finally { inFlight.current = false }
  }

  const configured = new Set(configuredProfileIds)
  const orphanedRuns = mode === 'Host' ? runs.filter(run => !configured.has(run.profileId)) : []
  const managedRunBlocksAcknowledgement = recovery.lifecycleBlocked && mode === 'Host' &&
    runs.some(run => run.state !== 'Offline')

  return <>{recovery.notices.length > 0 && (dismissed === noticeIdentity
    ? <Button className="secondary data-recovery-reopen" onClick={() => setDismissed(null)}><Icon name="warning" />Review local data</Button>
    : <section className={`panel data-recovery ${recovery.lifecycleBlocked ? 'blocking' : ''}`}
    role={recovery.lifecycleBlocked ? 'alert' : 'status'} aria-labelledby="data-recovery-title">
    <div className="section-heading"><div><h2 id="data-recovery-title">Local data needs review</h2>
      <p>{recovery.lifecycleBlocked
        ? 'TogetherServer moved unreadable server-control data aside and blocked Start and Restart so it does not control the wrong process.'
        : 'TogetherServer moved unreadable local data aside. Review the saved files before clearing this notice.'}</p></div>
      <span className="status error">{recovery.lifecycleBlocked ? 'Server controls blocked' : 'Review needed'}</span>
      <Button className="secondary icon-button" aria-label="Dismiss local data warning" onClick={() => setDismissed(noticeIdentity)}><Icon name="close" /></Button></div>
    <ul className="recovery-file-list">{recovery.notices.map((item, index) => <li key={`${item.detectedUtc}-${index}`}>
      <strong>{safeFileName(item.stateFile)}</strong><span>{item.reason}</span>
      <small>Saved as {safeFileName(item.quarantinedFile)} - {new Date(item.detectedUtc).toLocaleString()}</small>
    </li>)}</ul>
    {orphanedRuns.length > 0 && <div className="recovery-runs"><strong>Recorded servers without readable settings</strong>
      <p>Review these saved server processes on this PC. Start and Restart are unavailable until they are resolved.</p>
      {orphanedRuns.map(run => <div className="recovery-run" key={run.profileId}>
        <div><strong>{run.profileId}</strong><span>{run.state} - {run.detail}</span></div>
        <div className="actions">
          {['Process running', 'Starting', 'Listening', 'Ready'].includes(run.state) && <Button disabled={!!pending}
            onClick={() => onStopRecordedRun(run.profileId)}>
            {pending === `recovery-stop-${run.profileId}` ? 'Stopping...' : 'Stop recorded server'}
          </Button>}
          {run.state === 'Failed' && <Button className="secondary" disabled={!!pending}
            onClick={() => void forget(run.profileId)}>Forget exited record</Button>}
          {run.state === 'Unknown' && <small>Process identity is uncertain. TogetherServer will not Stop or forget this record until it can prove what happened.</small>}
        </div>
      </div>)}</div>}
    {mode === 'Host' ? <div className="recovery-actions">
      <label className="check-row"><Input type="checkbox" checked={confirmed}
        disabled={!!pending || managedRunBlocksAcknowledgement}
        onChange={event => onConfirmedChange(event.target.checked)} />{recovery.lifecycleBlocked
          ? 'I confirm no game server managed by TogetherServer is still running.'
          : 'I reviewed the saved files and understand that the affected Friend connection data was disabled.'}</label>
      {managedRunBlocksAcknowledgement && <small>Stop or resolve every recorded managed server before acknowledging recovery.</small>}
      <Button disabled={!!pending || !confirmed || managedRunBlocksAcknowledgement} onClick={onAcknowledge}>
        {pending === 'data-recovery' ? 'Checking...' : recovery.lifecycleBlocked
          ? 'Confirm and re-enable server controls' : 'Confirm review'}
      </Button>
    </div> : <div className="next-action"><span>Only My server can verify and acknowledge local recovery.</span>
      <Button disabled={!!pending} onClick={onSwitchToHost}>Switch to My server</Button></div>}
  </section>)}
    {confirmationDialog}
  </>
}

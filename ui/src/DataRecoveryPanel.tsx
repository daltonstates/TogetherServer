import { Button, Input } from './Controls'
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
  if (recovery.notices.length === 0) return null

  const configured = new Set(configuredProfileIds)
  const orphanedRuns = mode === 'Host' ? runs.filter(run => !configured.has(run.profileId)) : []
  const managedRunBlocksAcknowledgement = recovery.lifecycleBlocked && mode === 'Host' &&
    runs.some(run => run.state !== 'Offline')

  return <section className={`panel data-recovery ${recovery.lifecycleBlocked ? 'blocking' : ''}`}
    role={recovery.lifecycleBlocked ? 'alert' : 'status'} aria-labelledby="data-recovery-title">
    <div className="section-heading"><div><h2 id="data-recovery-title">Local data needs review</h2>
      <p>{recovery.lifecycleBlocked
        ? 'TogetherServer quarantined unreadable lifecycle state and blocked Start and Restart so it cannot guess which server processes are safe to control.'
        : 'TogetherServer quarantined unreadable local state. Review the retained files before clearing this notice.'}</p></div>
      <span className="status error">{recovery.lifecycleBlocked ? 'Lifecycle blocked' : 'Review needed'}</span></div>
    <ul className="recovery-file-list">{recovery.notices.map((item, index) => <li key={`${item.detectedUtc}-${index}`}>
      <strong>{safeFileName(item.stateFile)}</strong><span>{item.reason}</span>
      <small>Quarantined as {safeFileName(item.quarantinedFile)} - {new Date(item.detectedUtc).toLocaleString()}</small>
    </li>)}</ul>
    {orphanedRuns.length > 0 && <div className="recovery-runs"><strong>Recorded servers without readable settings</strong>
      <p>Resolve these exact recorded processes locally. Start and Restart are intentionally unavailable.</p>
      {orphanedRuns.map(run => <div className="recovery-run" key={run.profileId}>
        <div><strong>{run.profileId}</strong><span>{run.state} - {run.detail}</span></div>
        <div className="actions">
          {['Process running', 'Starting', 'Ready'].includes(run.state) && <Button disabled={!!pending}
            onClick={() => onStopRecordedRun(run.profileId)}>
            {pending === `recovery-stop-${run.profileId}` ? 'Stopping...' : 'Stop recorded server'}
          </Button>}
          {run.state === 'Failed' && <Button className="secondary" disabled={!!pending} onClick={() => {
            if (window.confirm('Forget this recorded run only if TogetherServer can prove its exact managed process is no longer running?'))
              onForgetRecordedRun(run.profileId)
          }}>Forget exited record</Button>}
          {run.state === 'Unknown' && <small>Process identity is uncertain. TogetherServer will not Stop or forget this record until it can prove what happened.</small>}
        </div>
      </div>)}</div>}
    {mode === 'Host' ? <div className="recovery-actions">
      <label className="check-row"><Input type="checkbox" checked={confirmed}
        disabled={!!pending || managedRunBlocksAcknowledgement}
        onChange={event => onConfirmedChange(event.target.checked)} />{recovery.lifecycleBlocked
          ? 'I confirm no game server managed by TogetherServer is still running.'
          : 'I reviewed the quarantined files and understand the affected saved Friend or pairing data was disabled.'}</label>
      {managedRunBlocksAcknowledgement && <small>Stop or resolve every recorded managed server before acknowledging recovery.</small>}
      <Button disabled={!!pending || !confirmed || managedRunBlocksAcknowledgement} onClick={onAcknowledge}>
        {pending === 'data-recovery' ? 'Checking...' : recovery.lifecycleBlocked
          ? 'Acknowledge and re-enable lifecycle actions' : 'Acknowledge warning'}
      </Button>
    </div> : <div className="next-action"><span>Only My server can verify and acknowledge local recovery.</span>
      <Button disabled={!!pending} onClick={onSwitchToHost}>Switch to My server</Button></div>}
  </section>
}

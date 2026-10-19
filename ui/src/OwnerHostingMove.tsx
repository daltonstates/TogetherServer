import { Button, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { Icon } from './Icon'
import { SettingsDialog } from './SettingsPresentation'
import type { PlannedHandoffStatus } from './plannedHandoff'

type Props = {
  worldName?: string; busy: boolean; fenced: boolean; review: boolean; available: boolean
  handoff: PlannedHandoffStatus | null; successors: { id: string; name: string }[]
  successorName: string; successorId: string; active: boolean; message: string; noticeScope: string
  onSelect: (id: string) => void; onClose: () => void; onRequest: () => void; onResume: () => void
  onCancel: () => void; onFinishLegacy: () => void; onRecheck: () => void
}

export function OwnerHostingMove({ worldName, busy, fenced, review, available, handoff, successors,
  successorName, successorId, active, message, noticeScope, onSelect, onClose, onRequest, onResume,
  onCancel, onFinishLegacy, onRecheck }: Props) {
  const requestOpen = handoff?.requestId && !['Declined', 'Canceled', 'Completed'].includes(handoff.recipientState ?? '')
  return <SettingsDialog title="Move hosting" description={worldName || 'Move this save to another approved PC.'}
    busy={busy} onClose={onClose}>
    <section className="host-sharing-handoff" aria-label="Planned handoff" aria-busy={busy}>
      {fenced || handoff?.recipientState === 'Completed' ? <>
        <h4>Hosting has moved</h4><p>The other PC keeps the final save and chooses when to Start. This PC keeps its copy.</p>
      </> : review ? <>
        <h4>Review the hosting decision</h4><p>Resolve the current hosting decision before moving this save.</p>
      </> : !handoff ? <>
        <h4>Checking the hosting request</h4><p role="status">Wait for current request details before moving this save.</p>
      </> : !available ? <p role="alert">Sharing permission is unavailable. Review sharing before moving this save.</p> :
        requestOpen ? <>
          <h4>{handoff.recipientState === 'Requested' ? 'Waiting for your friend' :
            handoff.recipientState === 'Prepared' ? 'Finishing the move' : 'Preparing the other PC'}</h4>
          <p><strong>{successorName}</strong></p><p role="status">{handoff.message}</p>
          {active ? <p className="helper-text">Keep this dialog open. The move continues after your friend prepares the save.</p> : <>
            <p className="helper-text">Resume to continue after your friend accepts and prepares their PC.</p>
            <Button disabled={busy || !successors.some(device => device.id === handoff.successorDeviceId)} onClick={onResume}>Resume move</Button>
          </>}
          {handoff.canCancel && <Button className="text-button" disabled={busy} onClick={onCancel}>Cancel move</Button>}
        </> : handoff.pending ? <>
          <h4>Finish the saved transfer</h4><p role="status">{handoff.message}</p>
          <div className="actions"><Button disabled={busy || !handoff.canComplete} onClick={onFinishLegacy}>Finish handoff</Button>
            <Button className="text-button" disabled={busy || !handoff.canCancel} onClick={onCancel}>Cancel handoff</Button></div>
        </> : <>
          <h4>Who should host next?</h4>
          <p>Your friend can accept or decline. After they prepare their PC, the app stops this server safely and moves the final verified save.</p>
          {handoff.recipientState === 'Declined' && <p role="status">Your friend declined. You can choose another PC.</p>}
          <label className="host-sharing-handoff-field">Next host PC<Select value={successorId} disabled={busy}
            onChange={event => onSelect(event.target.value)}><option value="">Choose a PC</option>
            {successors.map(device => <option key={device.id} value={device.id}>{device.name}</option>)}</Select></label>
          {successors.length === 0 && <p className="helper-text">Turn on Can host this save for an approved PC, then ask your friend to connect and finish sharing setup.</p>}
          <Button disabled={busy || !successors.some(device => device.id === successorId)} onClick={onRequest}>
            <Icon name="server" />Send hosting request</Button>
          <p className="helper-text">Your friend reviews the usual game setup checks and chooses Start separately.</p>
        </>}
      {busy && <p role="status">Moving and verifying the final save. Keep TogetherServer open.</p>}
      {message && <DismissibleNotice as="p" scope={noticeScope} role="status">{message}</DismissibleNotice>}
      {!busy && !active && !fenced && <Button className="text-button" onClick={onRecheck}><Icon name="refresh" />Recheck request</Button>}
    </section>
  </SettingsDialog>
}

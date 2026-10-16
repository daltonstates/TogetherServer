import { useState } from 'react'
import { changeJson, errorMessage } from './api'
import { parseBasicResult } from './contracts'
import { Button, Input } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { SharedWorldSeparateRoutePanel } from './SharedWorldSeparateRoutePanel'
import type { SharedGame } from './sharedWorldUx'

export function OwnerPeerRecoveryPanel({ groupKey, profileId, sourceBranchHash, onClose, onOpenHost,
  onBrowseServerFile }: { groupKey: string; profileId: string; sourceBranchHash: string;
  onClose: () => void; onOpenHost: (localProfileId: string) => void;
  onBrowseServerFile: (game: SharedGame) => Promise<string | null> }) {
  const [accepted, setAccepted] = useState(false)
  const [recorded, setRecorded] = useState(false)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const base = `/api/local/world-peers/${groupKey}/owner-recovery`
  return <section aria-label="Owner recovery from peer history">
    <h4>Recover a peer's history on the owner PC</h4>
    <p>This uses the latest complete verified copy of the selected history already stored here. The original world and other running Hosts keep their histories. This does not choose the group default.</p>
    {!recorded && <><label><Input type="checkbox" checked={accepted} onChange={event => setAccepted(event.target.checked)} />
      I understand another server may still be running and the saves will remain separate.</label>
      <Button disabled={!accepted || busy} onClick={async () => {
        setBusy(true)
        try { const result = await changeJson(base, 'POST', parseBasicResult, { sourceBranchHash, acceptSplitWarning: accepted });
          setMessage(result.message); if (result.ok) setRecorded(true) }
        catch (cause) { setMessage(errorMessage(cause)) }
        finally { setBusy(false) }
      }}>Prepare owner recovery copy</Button></>}
    {message && <DismissibleNotice scope={groupKey} role="status">{message}</DismissibleNotice>}
    {recorded && <SharedWorldSeparateRoutePanel profileId={profileId} separateCopies={1} expanded apiBase={base}
      onBrowseServerFile={onBrowseServerFile} onOpenSeparateHostControls={onOpenHost} />}
    <Button className="text-button" disabled={busy} onClick={onClose}>Close recovery steps</Button>
  </section>
}

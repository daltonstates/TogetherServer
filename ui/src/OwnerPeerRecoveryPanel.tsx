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
  return <section aria-label="Recover another PC's save history">
    <h4>Recover another PC's save history here</h4>
    <p>Use the newest complete verified copy of this history stored here. The original world and other Hosts keep their own progress. This does not change the group's default.</p>
    {!recorded && <><label><Input type="checkbox" checked={accepted} onChange={event => setAccepted(event.target.checked)} />
      I understand another server may still be running and the saves will remain separate.</label>
      <Button disabled={!accepted || busy} onClick={async () => {
        setBusy(true)
        try { const result = await changeJson(base, 'POST', parseBasicResult, { sourceBranchHash, acceptSplitWarning: accepted });
          setMessage(result.message); if (result.ok) setRecorded(true) }
        catch (cause) { setMessage(errorMessage(cause)) }
        finally { setBusy(false) }
      }}>Prepare recovery copy</Button></>}
    {message && <DismissibleNotice scope={groupKey} role="status">{message}</DismissibleNotice>}
    {recorded && <SharedWorldSeparateRoutePanel profileId={profileId} separateCopies={1} expanded apiBase={base}
      onBrowseServerFile={onBrowseServerFile} onOpenSeparateHostControls={onOpenHost} />}
    <Button className="text-button" disabled={busy} onClick={onClose}>Close recovery steps</Button>
  </section>
}

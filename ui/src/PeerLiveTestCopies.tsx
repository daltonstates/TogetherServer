import { useState } from 'react'
import { getLocalJson, errorMessage } from './api'
import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'

type Copy = { testId: string; versionHash: string; payloadDirectory: string; message: string }
function copies(value: unknown): Copy[] {
  if (!Array.isArray(value) || value.length > 32) throw new Error('Invalid private test copies.')
  return value.map(item => {
    if (!item || typeof item !== 'object' || typeof item.testId !== 'string' ||
      !/^[a-f0-9-]{36}$/i.test(item.testId) || typeof item.versionHash !== 'string' ||
      !/^[A-F0-9]{64}$/.test(item.versionHash) || typeof item.payloadDirectory !== 'string' ||
      item.payloadDirectory.length > 4096 || typeof item.message !== 'string' || item.message.length > 1500)
      throw new Error('Invalid private test copy.')
    return item as Copy
  })
}
export function PeerLiveTestCopies({ groupKey }: { groupKey: string }) {
  const [saved, setSaved] = useState<Copy[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  return <details><summary>Private live-save test copies</summary>
    <p>These private copies cannot be used for recovery. Test them in a separate game setup, then give the Host the test ID and your results.</p>
    <Button className="secondary" disabled={busy} onClick={async () => {
      setBusy(true)
      try { setSaved(await getLocalJson(`/api/local/world-peers/${groupKey}/live-tests`, copies)); setMessage('') }
      catch (cause) { setMessage(errorMessage(cause)) }
      finally { setBusy(false) }
    }}>Show private test copies on this PC</Button>
    {saved?.length === 0 && <p>No completed private test is stored here yet. You need Receive permission and must allow saves on this PC.</p>}
    {saved?.map(copy => <section key={copy.testId}>
      <p>Test {copy.testId} · save {copy.versionHash.slice(0, 12)}</p>
      <p>{copy.message}</p><p>Private test folder: <code>{copy.payloadDirectory}</code></p>
      <Button className="text-button" onClick={() => void navigator.clipboard.writeText(copy.payloadDirectory)
        .then(() => setMessage('Private test folder copied.')).catch(() => setMessage('Select the folder text to copy it manually.'))}>Copy test folder</Button>
    </section>)}
    {message && <DismissibleNotice scope={groupKey} role="status">{message}</DismissibleNotice>}
  </details>
}

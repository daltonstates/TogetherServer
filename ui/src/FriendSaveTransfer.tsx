import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { getLocalJson } from './api'
import { Icon } from './Icon'
import { formatSharedBytes, sharedTransferPhase, type TransferPhase } from './sharedWorldUx'

export type FriendSaveTransferStatus = {
  state: string; transferPhase: TransferPhase | null; receivedBytes: number; totalBytes: number
}

export function parseFriendSaveTransferStatus(value: unknown): FriendSaveTransferStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Save transfer status is unavailable.')
  const item = value as Record<string, unknown>
  if (typeof item.state !== 'string' || item.state.length === 0 || item.state.length > 300 ||
    [...item.state].some(character => character.charCodeAt(0) < 32) ||
    item.transferPhase !== null && !['Preparing', 'Receiving', 'Verifying', 'Receipt'].includes(String(item.transferPhase)) ||
    [item.receivedBytes, item.totalBytes].some(bytes => typeof bytes !== 'number' || !Number.isSafeInteger(bytes) || bytes < 0) ||
    Number(item.receivedBytes) > Number(item.totalBytes)) throw new Error('Save transfer status is unavailable.')
  return { state: item.state, transferPhase: sharedTransferPhase(item.state, item.transferPhase as TransferPhase | null),
    receivedBytes: item.receivedBytes as number, totalBytes: item.totalBytes as number }
}

type Observation = { scope: string; status: FriendSaveTransferStatus | null; checkedAt: number | null; unavailable: boolean }
const freshForMs = 5000

/** Cached receiver reads only: opening this surface never requests a save or starts a server. */
export function useFriendSaveTransfer({ endpoint, scope, enabled }: { endpoint: string; scope: string; enabled: boolean }) {
  const [observation, setObservation] = useState<Observation | null>(null)
  const [now, setNow] = useState(Date.now)
  const latest = useRef(observation)
  latest.current = observation
  useEffect(() => {
    if (!enabled) return
    let active = true
    let timer: number | undefined
    let timeout: number | undefined
    let controller: AbortController | null = null
    const update = (next: Observation) => { latest.current = next; setObservation(next); setNow(Date.now()) }
    const read = async () => {
      const abort = new AbortController()
      controller = abort
      let timedOut = false
      const requestTimeout = window.setTimeout(() => {
        timedOut = true
        if (active) update({ scope, status: latest.current?.scope === scope ? latest.current.status : null,
          checkedAt: null, unavailable: true })
        abort.abort()
        if (active) timer = window.setTimeout(() => void read(), 1000)
      }, 4000)
      timeout = requestTimeout
      try {
        const status = await getLocalJson(endpoint, parseFriendSaveTransferStatus, abort.signal)
        if (active && !abort.signal.aborted) update({ scope, status, checkedAt: Date.now(), unavailable: false })
      } catch {
        if (active && !abort.signal.aborted) update({ scope, status: latest.current?.scope === scope ? latest.current.status : null,
          checkedAt: null, unavailable: true })
      } finally {
        window.clearTimeout(requestTimeout)
        if (active && !timedOut) timer = window.setTimeout(() => void read(), 1000)
      }
    }
    void read()
    const clock = window.setInterval(() => setNow(Date.now()), 1000)
    return () => { active = false; controller?.abort(); window.clearTimeout(timer); window.clearTimeout(timeout); window.clearInterval(clock) }
  }, [endpoint, scope, enabled])
  const current = observation?.scope === scope ? observation : null
  const fresh = enabled && current?.checkedAt != null && !current.unavailable && now - current.checkedAt <= freshForMs
  const canHost = useCallback(() => {
    const current = latest.current
    return enabled && current?.scope === scope && current.checkedAt != null && !current.unavailable &&
      Date.now() - current.checkedAt <= freshForMs && current.status?.transferPhase === null
  }, [enabled, scope])
  return { status: current?.status ?? null, fresh, blocked: !fresh || current?.status?.transferPhase !== null,
    unavailable: current?.unavailable === true || current?.checkedAt != null && !fresh, canHost }
}

export function FriendSaveTransferProgress({ status, fresh, unavailable }: {
  status: FriendSaveTransferStatus | null; fresh: boolean; unavailable: boolean
}) {
  const titleId = useId()
  const detailId = useId()
  const phase = status?.transferPhase
  const title = !fresh ? unavailable ? 'Save transfer status unavailable' : 'Checking save transfer' :
    phase === 'Preparing' ? 'Preparing local save files' : phase === 'Receiving' ? 'Receiving the shared save' : phase === 'Verifying' ? 'Verifying the shared save' : 'Confirming save receipt'
  const message = !fresh ? 'Host locally is unavailable until this PC can confirm that no save transfer is active.' :
    phase === 'Preparing' ? 'Checking and staging reusable files on this PC. This local work happens before missing save bytes are downloaded.' :
      phase === 'Receiving' ? 'Keep TogetherServer open while the completed save arrives. Progress includes bytes retained from earlier passes.' :
      phase === 'Verifying' ? 'All bytes have arrived. Checking the files and hosting access before continuing.' :
        'The save checks have finished. Waiting for receipt confirmation before hosting.'
  const measured = fresh && (phase === 'Preparing' || phase === 'Receiving') && status !== null && status.totalBytes > 0
  return <section className="local-hosting-live-progress local-hosting-background-transfer" aria-label="Save transfer progress" aria-busy="true">
    <div className="local-hosting-live-progress-heading"><div role="status" aria-atomic="true">
      <strong id={titleId}><Icon name={unavailable ? 'warning' : 'loader'} />{title}</strong><p id={detailId}>{message}</p>
    </div></div>
    {measured ? <><progress aria-label={phase === 'Preparing' ? 'Local save bytes staged' : 'Shared save bytes received'} max={status.totalBytes} value={status.receivedBytes} />
      <p className="local-hosting-byte-progress"><strong>{Math.floor(status.receivedBytes * 100 / status.totalBytes)}% {phase === 'Preparing' ? 'staged locally' : 'received or retained'}</strong>
        <span>{formatSharedBytes(status.receivedBytes)} of {formatSharedBytes(status.totalBytes)}</span></p></> :
      <div className="local-hosting-indeterminate" role="progressbar" aria-labelledby={titleId} aria-describedby={detailId}
        aria-valuetext={title}><span /></div>}
    {fresh && phase && <ol className="local-hosting-transfer-steps" aria-label="Save transfer stages">
      <li aria-current={phase === 'Preparing' ? 'step' : undefined}>Prepare local files</li>
      <li aria-current={phase === 'Receiving' ? 'step' : undefined}>Receive save</li>
      <li aria-current={phase === 'Verifying' ? 'step' : undefined}>Verify files</li>
      <li aria-current={phase === 'Receipt' ? 'step' : undefined}>Confirm receipt</li>
    </ol>}
    <p className="helper-text">Host locally becomes available after local preparation, receiving, verification and receipt confirmation finish. Remote Start on another eligible Host remains separate.</p>
  </section>
}

import { useEffect, useId, useState } from 'react'
import { getLocalJson } from './api'
import { Icon } from './Icon'
import { formatSharedBytes } from './sharedWorldUx'

export type HostingProgress = {
  phase: string; state: 'Working' | 'Completed' | 'Failed'; message: string
  startedUtc: string; updatedUtc: string; receivedBytes: number; totalBytes: number; percent: number | null
}

export function parseHostingProgress(value: unknown): HostingProgress | null {
  if (value === null) return null
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Hosting progress is unavailable.')
  const item = value as Record<string, unknown>
  const boundedText = (value: unknown, limit: number) => typeof value === 'string' && value.length > 0 && value.length <= limit &&
    ![...value].some(character => character.charCodeAt(0) < 32 && !['\n', '\r', '\t'].includes(character))
  if (!boundedText(item.phase, 100) || !boundedText(item.message, 1000) || !['Working', 'Completed', 'Failed'].includes(String(item.state)) ||
    ![item.startedUtc, item.updatedUtc].every(time => typeof time === 'string' && Number.isFinite(Date.parse(time))) ||
    Date.parse(String(item.updatedUtc)) < Date.parse(String(item.startedUtc)) ||
    [item.receivedBytes, item.totalBytes].some(bytes => typeof bytes !== 'number' || !Number.isSafeInteger(bytes) || bytes < 0) ||
    Number(item.receivedBytes) > Number(item.totalBytes) ||
    item.percent !== null && (typeof item.percent !== 'number' || !Number.isFinite(item.percent) || item.percent < 0 || item.percent > 100))
    throw new Error('Hosting progress is unavailable.')
  return value as HostingProgress
}

const phaseLabels: Record<string, string> = {
  Permission: 'Checking hosting access', LatestSave: 'Finding the latest completed save', Receiving: 'Receiving the save',
  Verifying: 'Verifying the save files', Receipt: 'Confirming the received copy', PreparingCopy: 'Preparing this PC’s copy',
  ServerSetup: 'Checking the server app', Ports: 'Checking local game ports', Connection: 'Checking this PC’s connection',
  Starting: 'Starting the local server'
}

function elapsed(since: number, now: number): string {
  const seconds = Math.max(0, Math.floor((now - since) / 1000))
  return seconds < 60 ? `${seconds}s elapsed` : `${Math.floor(seconds / 60)}m ${seconds % 60}s elapsed`
}

/** A separate, read-only flight keeps progress moving while preparation holds its own gate. */
export function LocalHostingProgress({ endpoint, title, message, startedAt }: { endpoint?: string; title: string; message: string; startedAt?: number }) {
  const titleId = useId()
  const detailId = useId()
  const [began] = useState(() => startedAt ?? Date.now())
  const [now, setNow] = useState(Date.now)
  const [progress, setProgress] = useState<HostingProgress | null>(null)
  const [unavailable, setUnavailable] = useState(false)
  const [phases, setPhases] = useState<string[]>([])
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])
  useEffect(() => {
    if (!endpoint) return
    let active = true
    let timer: number | undefined
    let timeout: number | undefined
    let readController: AbortController | null = null
    let accepted: HostingProgress | null = null
    let finished = false
    const read = async () => {
      const abort = new AbortController()
      readController = abort
      timeout = window.setTimeout(() => { if (active) setUnavailable(true); abort.abort() }, 4000)
      try {
        const next = await getLocalJson(endpoint, parseHostingProgress, abort.signal)
        if (!active || abort.signal.aborted) return
        // Queued work may still observe a previous attempt updating. Only this
        // attempt's start belongs to the caller's already-captured dispatch.
        if (next && Date.parse(next.startedUtc) >= began && (!accepted ||
          next.startedUtc === accepted.startedUtc && Date.parse(next.updatedUtc) >= Date.parse(accepted.updatedUtc))) {
          accepted = next
          finished = next.state !== 'Working'
          setProgress(next)
          setPhases(prior => prior.at(-1) === next.phase ? prior : [...prior, next.phase].slice(-8))
        }
        setUnavailable(false)
      } catch { if (active && !abort.signal.aborted) setUnavailable(true) }
      finally {
        window.clearTimeout(timeout)
        if (active && !finished) timer = window.setTimeout(() => void read(), 1000)
      }
    }
    // Quick checks can finish before the first passive read is needed.
    timer = window.setTimeout(() => void read(), 250)
    return () => { active = false; readController?.abort(); window.clearTimeout(timer); window.clearTimeout(timeout) }
  }, [endpoint, began])
  const terminal = progress !== null && progress.state !== 'Working'
  const stale = !terminal && (unavailable || progress !== null && now - Date.parse(progress.updatedUtc) > 10_000)
  const receiving = !terminal && progress?.phase === 'Receiving' && !stale && progress.totalBytes > 0
  // This percentage measures received bytes only. Verification and preparation have no measured denominator.
  const percent = receiving ? Math.floor(progress.receivedBytes * 100 / progress.totalBytes) : null
  const currentTitle = progress?.state === 'Failed' ? 'Hosting step needs attention' : progress?.state === 'Completed' ? 'Hosting step completed' :
    progress ? phaseLabels[progress.phase] ?? progress.phase : title
  return <section className="local-hosting-live-progress" aria-label="Hosting progress" aria-busy={!terminal}>
    <div className="local-hosting-live-progress-heading">
      <div role="status" aria-atomic="true"><strong id={titleId}><Icon name={terminal ? progress.state === 'Failed' ? 'warning' : 'check' : 'loader'} />{currentTitle}</strong>
        <p id={detailId}>{progress?.message ?? message}</p></div>
      <span className="local-hosting-elapsed">{elapsed(began, terminal ? Date.parse(progress.updatedUtc) : now)}</span>
    </div>
    {percent !== null && progress ? <>
      <progress aria-label="Shared save bytes received" max={progress.totalBytes} value={progress.receivedBytes} />
      <p className="local-hosting-byte-progress"><strong>{percent}% received</strong>
        <span>{formatSharedBytes(progress.receivedBytes)} of {formatSharedBytes(progress.totalBytes)}</span></p>
      <p className="helper-text">Save transfer only. File verification and local preparation must also finish.</p>
    </> : !terminal && <div className="local-hosting-indeterminate" role="progressbar" aria-labelledby={titleId}
      aria-describedby={detailId} aria-valuetext={currentTitle}><span /></div>}
    {terminal && <p className="helper-text">Waiting for the operation result before continuing.</p>}
    {phases.length > 1 && <p className="local-hosting-phase-history">Earlier: {phases.slice(0, -1).map(phase => phaseLabels[phase] ?? phase).join(' · ')}</p>}
    {stale && <p className="helper-text">Waiting for a fresh progress update. The operation result is still pending.</p>}
  </section>
}

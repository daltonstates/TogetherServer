import { useEffect, useId, useRef, useState } from 'react'
import { getLocalJson } from './api'
import { Icon } from './Icon'
import { formatSharedBytes } from './sharedWorldUx'

export type HostingProgress = {
  phase: string; state: 'Working' | 'Completed' | 'Failed'; message: string
  startedUtc: string; updatedUtc: string; receivedBytes: number; totalBytes: number; percent: number | null
  completedFiles?: number; totalFiles?: number
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
    (item.completedFiles !== undefined || item.totalFiles !== undefined) &&
      ([item.completedFiles, item.totalFiles].some(files => typeof files !== 'number' || !Number.isSafeInteger(files) || files < 0) ||
        Number(item.completedFiles) > Number(item.totalFiles)) ||
    item.percent !== null && (typeof item.percent !== 'number' || !Number.isFinite(item.percent) || item.percent < 0 || item.percent > 100))
    throw new Error('Hosting progress is unavailable.')
  return value as HostingProgress
}

const phaseLabels: Record<string, string> = {
  Permission: 'Checking hosting access', PeerSync: 'Checking approved PCs for updates', LatestSave: 'Finding the latest completed save', Receiving: 'Receiving the save',
  Verifying: 'Verifying the save files', Receipt: 'Confirming the received copy', PreparingCopy: 'Preparing the hosting copy on this PC',
  ServerSetup: 'Checking the server app', Ports: 'Checking local game ports', Connection: 'Checking this PC’s connection',
  Starting: 'Starting the local server'
}

export function hostingProgressPhaseLabel(phase: string): string {
  return phaseLabels[phase] ?? 'Preparing this PC'
}

export function hostingProgressFilesLabel(phase: string): string {
  return phase === 'PreparingCopy' ? 'files prepared for verification' : phase === 'Verifying' ? 'files checked' : 'files reported'
}

function progressMessage(progress: HostingProgress): string {
  return phaseLabels[progress.phase] ? progress.message :
    'This preparation step is not recognized. Wait for the operation result before continuing.'
}

function elapsed(since: number, now: number): string {
  const seconds = Math.max(0, Math.floor((now - since) / 1000))
  return seconds < 60 ? `${seconds}s elapsed` : `${Math.floor(seconds / 60)}m ${seconds % 60}s elapsed`
}

function updateAge(updatedUtc: string, now: number): string {
  const seconds = Math.max(0, Math.floor((now - Date.parse(updatedUtc)) / 1000))
  return seconds < 60 ? `${seconds}s ago` : `${Math.floor(seconds / 60)}m ago`
}

/** A separate, read-only flight keeps progress moving while preparation holds its own gate. */
export function LocalHostingProgress({ endpoint, title, message, startedAt, expectedStartedUtc, onResult, onUpdate }: {
  endpoint?: string; title: string; message: string; startedAt?: number; expectedStartedUtc?: string
  onResult?: (result: HostingProgress) => void; onUpdate?: (progress: HostingProgress) => void
}) {
  const titleId = useId()
  const detailId = useId()
  const [began] = useState(() => startedAt ?? Date.now())
  const [now, setNow] = useState(Date.now)
  const [progress, setProgress] = useState<HostingProgress | null>(null)
  const [unavailable, setUnavailable] = useState(false)
  const [phases, setPhases] = useState<string[]>([])
  const updateCallback = useRef(onUpdate)
  updateCallback.current = onUpdate
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
        if (next && (!expectedStartedUtc || next.startedUtc === expectedStartedUtc) && Date.parse(next.startedUtc) >= began && (!accepted ||
          next.startedUtc === accepted.startedUtc && Date.parse(next.updatedUtc) >= Date.parse(accepted.updatedUtc))) {
          accepted = next
          finished = next.state !== 'Working'
          updateCallback.current?.({ ...next, message: progressMessage(next) })
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
  }, [endpoint, began, expectedStartedUtc])
  const terminal = progress !== null && progress.state !== 'Working'
  useEffect(() => {
    if (progress && progress.state !== 'Working') onResult?.({ ...progress, message: progressMessage(progress) })
  }, [progress, onResult])
  const stale = !terminal && (unavailable || progress !== null && now - Date.parse(progress.updatedUtc) > 10_000)
  const preparingCopy = progress?.phase === 'PreparingCopy'
  const measuredBytes = !terminal && (progress?.phase === 'Receiving' || preparingCopy) && !stale && progress.totalBytes > 0
  // The measured bytes describe this transfer/copy step, never verification or overall hosting completion.
  const percent = measuredBytes ? Math.floor(progress.receivedBytes * 100 / progress.totalBytes) : null
  const currentTitle = progress?.state === 'Failed' ? 'Hosting step needs attention' : progress?.state === 'Completed' ? 'Hosting step completed' :
    progress ? hostingProgressPhaseLabel(progress.phase) : title
  return <section className="local-hosting-live-progress" aria-label="Hosting progress" aria-busy={!terminal}>
    <div className="local-hosting-live-progress-heading">
      <div role="status" aria-atomic="true"><strong id={titleId}><Icon name={terminal ? progress.state === 'Failed' ? 'warning' : 'check' : 'loader'} />{currentTitle}</strong>
        <p id={detailId}>{progress ? progressMessage(progress) : message}</p></div>
      <span className="local-hosting-elapsed">{elapsed(began, terminal ? Date.parse(progress.updatedUtc) : now)}</span>
    </div>
    {progress && <p className="local-hosting-phase-history">{terminal ? 'Last step' : 'Current step'}: {hostingProgressPhaseLabel(progress.phase)}.
      {' '}Updated <time dateTime={progress.updatedUtc}>{updateAge(progress.updatedUtc, now)}</time>.</p>}
    {percent !== null && progress ? <>
      <progress aria-label={preparingCopy ? 'Hosting copy bytes prepared' : 'Shared save bytes received'} max={progress.totalBytes} value={progress.receivedBytes} />
      <p className="local-hosting-byte-progress"><strong>{percent}% {preparingCopy ? 'of copy bytes prepared' : 'received'}</strong>
        <span>{formatSharedBytes(progress.receivedBytes)} of {formatSharedBytes(progress.totalBytes)}</span></p>
      <p className="helper-text">{preparingCopy ? 'Copy preparation only. These bytes still need final file verification before the hosting copy can be used.' : 'Save transfer only. File verification and local preparation must also finish.'}</p>
    </> : !terminal && <div className="local-hosting-indeterminate" role="progressbar" aria-labelledby={titleId}
      aria-describedby={detailId} aria-valuetext={currentTitle}><span /></div>}
    {progress?.completedFiles !== undefined && progress.totalFiles !== undefined && progress.totalFiles > 0 && <p className="local-hosting-byte-progress">
      {progress.completedFiles} of {progress.totalFiles} {hostingProgressFilesLabel(progress.phase)}.</p>}
    {terminal && <p className="helper-text">Waiting for the operation result before continuing.</p>}
    {phases.length > 1 && <p className="local-hosting-phase-history">Earlier: {phases.slice(0, -1).map(phase => phaseLabels[phase] ?? 'Preparation step').join(' · ')}</p>}
    {stale && <p className="helper-text">Waiting for a fresh progress update. The operation result is still pending. Keep TogetherServer open and wait for that result before trying again.</p>}
  </section>
}

import { useEffect, useState } from 'react'
import { getLocalJson } from './api'
import { Icon } from './Icon'
import { formatSharedBytes } from './sharedWorldUx'
import './BackgroundJobs.css'

export type BackgroundJob = {
  id: string; profileId: string | null; serverName: string; worldName: string; operation: string; phase: string
  state: 'Running' | 'Waiting' | 'Succeeded' | 'Failed' | 'Interrupted' | 'Stalled'
  receivedBytes: number; totalBytes: number | null; startedUtc: string; updatedUtc: string
  completedUtc: string | null; resultCode: string | null; message: string | null; attempts: number
}
type BackgroundJobsResult = { jobs: BackgroundJob[]; activeCount: number }
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const states = ['Running', 'Waiting', 'Succeeded', 'Failed', 'Interrupted', 'Stalled']
export function parseBackgroundJobs(value: unknown): BackgroundJobsResult {
  const invalid = () => { throw new Error('Background jobs could not be read.') }
  const object = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown> : invalid()
  const text = (value: unknown, max = 1000): string => typeof value === 'string' && value.length <= max &&
    ![...value].some(character => character.charCodeAt(0) < 32) ? value : invalid()
  const bytes = (value: unknown): number => typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : invalid()
  const stamp = (value: unknown): string => {
    const result = text(value, 64)
    return /^\d{4}-\d\d-\d\dT.*(?:Z|\+00:00)$/.test(result) && Number.isFinite(Date.parse(result)) ? result : invalid()
  }
  const source = object(value)
  if (!Array.isArray(source.jobs) || source.jobs.length > 512) return invalid()
  const jobs = source.jobs.map(value => {
    const item = object(value), id = text(item.id, 36), profileId = item.profileId === null ? null : text(item.profileId, 36)
    const state = text(item.state, 32), receivedBytes = bytes(item.receivedBytes), totalBytes = item.totalBytes === null ? null : bytes(item.totalBytes)
    const attempts = bytes(item.attempts)
    if (!guid.test(id) || profileId !== null && !guid.test(profileId) || !states.includes(state) ||
      totalBytes !== null && receivedBytes > totalBytes || attempts < 1 || attempts > 1_000_000) return invalid()
    return { id, profileId, serverName: text(item.serverName, 128), worldName: text(item.worldName, 128),
      operation: text(item.operation, 100), phase: text(item.phase, 100), state: state as BackgroundJob['state'], receivedBytes, totalBytes,
      startedUtc: stamp(item.startedUtc), updatedUtc: stamp(item.updatedUtc), completedUtc: item.completedUtc === null ? null : stamp(item.completedUtc),
      resultCode: item.resultCode === null ? null : text(item.resultCode, 100), message: item.message === null ? null : text(item.message), attempts }
  })
  const activeCount = bytes(source.activeCount)
  if (new Set(jobs.map(job => job.id)).size !== jobs.length || activeCount !== jobs.filter(job => ['Running', 'Stalled'].includes(job.state)).length) return invalid()
  return { jobs, activeCount }
}

/** App-level observation only. Closing the menu or changing servers never cancels backend work. */
export function BackgroundJobs() {
  const [result, setResult] = useState<BackgroundJobsResult | null>(null)
  const [unavailable, setUnavailable] = useState(false)
  useEffect(() => {
    let active = true
    let timer: number | undefined
    let pending: AbortController | null = null
    const read = async () => {
      const controller = new AbortController()
      pending = controller
      const timeout = window.setTimeout(() => controller.abort(), 4000)
      try {
        const next = await getLocalJson('/api/local/background-jobs', parseBackgroundJobs, controller.signal)
        if (active && !controller.signal.aborted) { setResult(next); setUnavailable(false) }
      } catch { if (active) setUnavailable(true) }
      finally { window.clearTimeout(timeout); if (active) timer = window.setTimeout(() => void read(), 1000) }
    }
    void read()
    return () => { active = false; pending?.abort(); window.clearTimeout(timer) }
  }, [])
  const activeCount = result?.activeCount ?? 0
  const failed = result?.jobs.some(job => ['Failed', 'Interrupted', 'Stalled'].includes(job.state))
  return <details className="background-jobs-menu">
    <summary aria-label={`Background jobs${activeCount ? `, ${activeCount} active` : ''}`} title="Background jobs">
      <Icon name={activeCount ? 'loader' : failed ? 'warning' : 'archive'} size={18} /><span>Background jobs</span>
      {activeCount > 0 && <span className="background-jobs-count">{activeCount}</span>}
    </summary>
    <section className="background-jobs-panel" aria-label="Background jobs">
      <header><strong>Background jobs</strong><small>Save work continues while you use other servers.</small></header>
      {unavailable && <p role="status">Current progress is unavailable. Last observations are shown; work may still be running.</p>}
      {!result && !unavailable && <p role="status">Reading background jobs…</p>}
      {result?.jobs.length === 0 && <p>No active or recent save jobs.</p>}
      <ol>{result?.jobs.map(job => <li key={job.id} className={`background-job job-${job.state.toLowerCase()}`}>
        <div><strong>{job.serverName || job.worldName || 'Shared server'}</strong><span>{job.state}</span></div>
        {job.worldName && job.worldName !== job.serverName && <small>{job.worldName}</small>}
        <p>{job.operation} · {job.phase}</p>
        {['Running', 'Stalled'].includes(job.state) && (job.totalBytes !== null && job.totalBytes > 0
          ? <><progress aria-label={`${job.serverName || job.worldName} save bytes received`} max={job.totalBytes} value={job.receivedBytes} />
            <small>{Math.floor(job.receivedBytes * 100 / job.totalBytes)}% · {formatSharedBytes(job.receivedBytes)} of {formatSharedBytes(job.totalBytes)}</small></>
          : <><progress aria-label={`${job.serverName || job.worldName} ${job.phase}`} />{(job.receivedBytes > 0 || job.phase === 'Receiving') &&
            <small>{formatSharedBytes(job.receivedBytes)} received · total unknown</small>}</>)}
        {!['Running', 'Stalled'].includes(job.state) && job.totalBytes !== null && job.totalBytes > 0 &&
          <small>{formatSharedBytes(job.receivedBytes)} of {formatSharedBytes(job.totalBytes)} received</small>}
        <p>{job.message}</p>
        <small><time dateTime={job.updatedUtc}>{new Date(job.updatedUtc).toLocaleString()}</time>{job.attempts > 1 && ` · ${job.attempts} attempts`}</small>
        {job.resultCode && <small>Result: {job.resultCode}</small>}
      </li>)}</ol>
    </section>
  </details>
}

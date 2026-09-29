import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { getLocalJson } from './api'
import { Button, Input, Select } from './Controls'
import { Icon } from './Icon'
import {
  parseServerLogResult,
  type ServerLogRecord,
  type ServerLogResult,
  type ServerLogSourceState
} from './contracts'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'

export const serverLogsCapability = 'server-logs-v1'

export type ServerLogFilters = {
  severity: '' | 'Info' | 'Warning' | 'Error'
  category: '' | 'Server' | 'Lifecycle' | 'Player' | 'Connections'
  contains: string
}

export type ServerLogRequest = ServerLogFilters & { cursor: string | null; limit: number }
export type ServerLogLoader = (endpoint: string, request: ServerLogRequest,
  signal: AbortSignal) => Promise<ServerLogResult>

export type ServerLogUnsupported = { code: string; message: string }

export function friendLogAvailability(canViewLogs: boolean, capabilities?: readonly string[]) {
  if (!canViewLogs) return { visible: false, supported: false, unsupported: null }
  if (!capabilities?.includes(serverLogsCapability)) return {
    visible: true,
    supported: false,
    unsupported: {
      code: 'ServerLogsUpdateRequired',
      message: 'Update the Host app before viewing server logs from this PC.'
    }
  }
  return { visible: true, supported: true, unsupported: null }
}

export function serverLogRequestPath(endpoint: string, request: ServerLogRequest) {
  const query = new URLSearchParams({ limit: String(request.limit) })
  if (request.cursor) query.set('cursor', request.cursor)
  if (request.severity) query.set('severity', request.severity)
  if (request.category) query.set('category', request.category)
  if (request.contains) query.set('contains', request.contains)
  return `${endpoint}?${query.toString()}`
}

export const loadServerLogPage: ServerLogLoader = (endpoint, request, signal) =>
  getLocalJson(serverLogRequestPath(endpoint, request), parseServerLogResult, signal)

const emptyFilters: ServerLogFilters = { severity: '', category: '', contains: '' }

function stateLabel(state: ServerLogSourceState | 'Loading' | 'Error', paused: boolean) {
  if (paused && state === 'Active') return 'Paused'
  switch (state) {
    case 'Active': return 'Active run'
    case 'Ended': return 'Ended run'
    case 'Missing': return 'No run log'
    case 'Unsupported': return 'Unsupported'
    case 'Unavailable': case 'Error': return 'Log unavailable'
    default: return 'Loading recent records'
  }
}

function displayTime(value: string | null) {
  if (!value) return '--:--:--'
  const parsed = new Date(value)
  return Number.isFinite(parsed.valueOf()) ? parsed.toLocaleTimeString() : '--:--:--'
}

export function ServerLogViewer({ endpoint, visible, unsupported = null, loader = loadServerLogPage,
  pollIntervalMs = 2500, batchSize = 100, historyLimit = 500 }: {
  endpoint: string
  visible: boolean
  unsupported?: ServerLogUnsupported | null
  loader?: ServerLogLoader
  pollIntervalMs?: number
  batchSize?: number
  historyLimit?: number
}) {
  const safeBatchSize = Math.max(1, Math.min(200, batchSize))
  const safeHistoryLimit = Math.max(1, Math.min(1000, historyLimit))
  const [records, setRecords] = useState<ServerLogRecord[]>([])
  const [result, setResult] = useState<ServerLogResult | null>(null)
  const [requestError, setRequestError] = useState('')
  const [paused, setPaused] = useState(false)
  const [loading, setLoading] = useState(false)
  const [filters, setFilters] = useState<ServerLogFilters>(emptyFilters)
  const [filterDraft, setFilterDraft] = useState<ServerLogFilters>(emptyFilters)
  const [documentVisible, setDocumentVisible] = useState(() => !document.hidden)
  const [intersecting, setIntersecting] = useState(() => typeof IntersectionObserver === 'undefined')
  const rootRef = useRef<HTMLElement | null>(null)
  const recordsRef = useRef<ServerLogRecord[]>([])
  const cursorRef = useRef<string | null>(null)
  const runIdRef = useRef<string | null>(null)
  const generationRef = useRef(0)
  const inFlightRef = useRef<Promise<void> | null>(null)
  const requestControllerRef = useRef<AbortController | null>(null)
  const manualControllerRef = useRef<AbortController | null>(null)
  const filterKey = useMemo(() => JSON.stringify(filters), [filters])
  const restartKey = `${endpoint}|${filterKey}|${unsupported?.code ?? ''}`

  useEffect(() => {
    const onVisibility = () => setDocumentVisible(!document.hidden)
    document.addEventListener('visibilitychange', onVisibility)
    return () => document.removeEventListener('visibilitychange', onVisibility)
  }, [])

  useEffect(() => {
    const element = rootRef.current
    if (!element || typeof IntersectionObserver === 'undefined') {
      setIntersecting(true)
      return
    }
    const observer = new IntersectionObserver(entries => setIntersecting(entries.some(entry => entry.isIntersecting)))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    generationRef.current += 1
    requestControllerRef.current?.abort()
    manualControllerRef.current?.abort()
    inFlightRef.current = null
    cursorRef.current = null
    runIdRef.current = null
    recordsRef.current = []
    setRecords([])
    setResult(null)
    setRequestError('')
    setLoading(false)
  }, [endpoint, filterKey, unsupported?.code])

  useEffect(() => () => {
    requestControllerRef.current?.abort()
    manualControllerRef.current?.abort()
  }, [])

  const applyResult = useCallback((next: ServerLogResult) => {
    const sameRun = runIdRef.current !== null && runIdRef.current === next.runId
    const runChanged = runIdRef.current !== null && next.runId !== null && runIdRef.current !== next.runId
    if (runChanged || (next.runId === null && next.sourceState !== 'Active') || (!next.ok && !sameRun)) {
      recordsRef.current = []
      setRecords([])
    }
    if (next.ok) {
      const base = runChanged ? [] : recordsRef.current
      const merged = [...base, ...next.records].slice(-safeHistoryLimit)
      recordsRef.current = merged
      setRecords(merged)
      cursorRef.current = next.cursor
    } else if (!sameRun) {
      cursorRef.current = null
    }
    runIdRef.current = next.runId
    setResult(next)
    setRequestError('')
  }, [safeHistoryLimit])

  const refresh = useCallback((externalSignal: AbortSignal): Promise<void> => {
    if (inFlightRef.current) return inFlightRef.current
    const generation = generationRef.current
    const controller = new AbortController()
    requestControllerRef.current = controller
    const abort = () => controller.abort()
    if (externalSignal.aborted) controller.abort()
    else externalSignal.addEventListener('abort', abort, { once: true })

    const read = async (allowCursorReset: boolean): Promise<void> => {
      const next = await loader(endpoint, { ...filters, cursor: cursorRef.current, limit: safeBatchSize }, controller.signal)
      if (controller.signal.aborted || generation !== generationRef.current) return
      if (next.code === 'InvalidLogCursor' && allowCursorReset) {
        cursorRef.current = null
        runIdRef.current = null
        recordsRef.current = []
        setRecords([])
        setResult(null)
        await read(false)
        return
      }
      applyResult(next)
    }

    setLoading(true)
    const tracked = read(true).finally(() => {
      externalSignal.removeEventListener('abort', abort)
      if (requestControllerRef.current === controller) requestControllerRef.current = null
      if (inFlightRef.current === tracked) inFlightRef.current = null
      if (generation === generationRef.current) setLoading(false)
    })
    inFlightRef.current = tracked
    return tracked
  }, [applyResult, endpoint, filters, loader, safeBatchSize])

  const handleError = useCallback((error: unknown) => {
    if (error instanceof DOMException && error.name === 'AbortError') return
    setRequestError(error instanceof Error ? error.message : String(error))
  }, [])

  const polling = visible && documentVisible && intersecting && !paused && !unsupported
  useSingleFlightPolling(refresh, pollIntervalMs, handleError, polling, restartKey)

  const manualRefresh = () => {
    manualControllerRef.current?.abort()
    const controller = new AbortController()
    manualControllerRef.current = controller
    void refresh(controller.signal).catch(handleError).finally(() => {
      if (manualControllerRef.current === controller) manualControllerRef.current = null
    })
  }

  const sourceState: ServerLogSourceState | 'Loading' | 'Error' = unsupported ? 'Unsupported' :
    requestError ? 'Error' : result?.sourceState ?? 'Loading'
  const message = unsupported?.message ?? (requestError || result?.message ||
    'Loading recent lines from this server session.')

  return <section ref={rootRef} className="server-log-viewer" aria-label="Server logs">
    <div className="server-log-toolbar">
      <div className="server-log-heading"><span className={`server-log-state state-${sourceState.toLocaleLowerCase()}`}>{stateLabel(sourceState, paused)}</span>
        <small>{records.length} of {safeHistoryLimit} lines shown{result?.hasMore ? ' · more available' : ''}</small></div>
      <div className="server-log-actions"><Button className="secondary" disabled={!!unsupported} onClick={() => setPaused(value => !value)}>{paused ? <><Icon name="play" />Resume</> : <><Icon name="stop" />Pause</>}</Button>
        <Button className="secondary" disabled={loading || !!unsupported || !visible} onClick={manualRefresh}>{loading ? <><Icon name="loader" />Refreshing</> : <><Icon name="refresh" />Refresh</>}</Button></div>
    </div>
    <form className="server-log-filters" onSubmit={event => {
      event.preventDefault()
      setFilters({ ...filterDraft, contains: filterDraft.contains.trim() })
    }}>
      <label>Severity<Select aria-label="Log severity" value={filterDraft.severity} onChange={event => setFilterDraft(current => ({ ...current, severity: event.target.value as ServerLogFilters['severity'] }))}>
        <option value="">All</option><option value="Info">Info</option><option value="Warning">Warning</option><option value="Error">Error</option>
      </Select></label>
      <label>Category<Select aria-label="Log category" value={filterDraft.category} onChange={event => setFilterDraft(current => ({ ...current, category: event.target.value as ServerLogFilters['category'] }))}>
        <option value="">All</option><option value="Server">Server</option><option value="Lifecycle">Start and stop</option><option value="Player">Player</option><option value="Connections">Connections</option>
      </Select></label>
      <label className="server-log-contains">Contains<Input aria-label="Log contains" maxLength={80} value={filterDraft.contains} placeholder="Find text" onChange={event => setFilterDraft(current => ({ ...current, contains: event.target.value }))} /></label>
      <Button type="submit" className="secondary">Apply</Button>
      <Button className="text-button" disabled={!filters.severity && !filters.category && !filters.contains && !filterDraft.severity && !filterDraft.category && !filterDraft.contains} onClick={() => { setFilterDraft(emptyFilters); setFilters(emptyFilters) }}>Clear</Button>
    </form>
    <div className={`server-log-source state-${sourceState.toLocaleLowerCase()}`} role="status"><strong>{stateLabel(sourceState, paused)}</strong><span>{message}</span></div>
    {records.length > 0 ? <div className="server-log-console" role="log" aria-live="off" aria-label="Server log console">
      {records.map((record, index) => <div className={`server-log-line severity-${record.severity.toLocaleLowerCase()}`} key={`${index}-${record.timestampUtc ?? ''}-${record.message}`}>
        <time dateTime={record.timestampUtc ?? undefined}>{displayTime(record.timestampUtc)}</time><span className="server-log-severity">{record.severity}</span><span className="server-log-category">{record.category}</span><code>{record.message}</code>
      </div>)}
    </div> : <div className="server-log-empty">{sourceState === 'Active' ? 'No matching complete lines yet.' : sourceState === 'Loading' ? 'Loading recent records…' : 'No log records are available for this state.'}</div>}
    <p className="server-log-disclaimer">Diagnostic display only. These records never determine readiness, player count, timers, Start, Stop, or recovery.</p>
  </section>
}

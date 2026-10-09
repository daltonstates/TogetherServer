import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
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

const preferenceKey = 'togetherserver.log-filters.v1'
const severities = ['', 'Info', 'Warning', 'Error'] as const
const categories = ['', 'Server', 'Lifecycle', 'Player', 'Connections'] as const

// Only these two enums may enter browser storage. Search text, records, cursors,
// endpoints and profile/connection identities stay in memory.
export function readLogPreferences(): ServerLogFilters {
  try {
    const raw = localStorage.getItem(preferenceKey)
    if (!raw || raw.length > 100) return { ...emptyFilters }
    const value: unknown = JSON.parse(raw)
    if (!value || typeof value !== 'object' || Array.isArray(value)) return { ...emptyFilters }
    const saved = value as Record<string, unknown>
    if (Object.keys(saved).some(key => key !== 'severity' && key !== 'category') ||
        !severities.some(item => item === saved.severity) || !categories.some(item => item === saved.category))
      return { ...emptyFilters }
    return { severity: saved.severity as ServerLogFilters['severity'],
      category: saved.category as ServerLogFilters['category'], contains: '' }
  } catch { return { ...emptyFilters } }
}

export function saveLogPreferences(filters: ServerLogFilters) {
  if (!severities.some(item => item === filters.severity) || !categories.some(item => item === filters.category)) return
  try {
    localStorage.setItem(preferenceKey, JSON.stringify({ severity: filters.severity, category: filters.category }))
  } catch { /* A blocked browser store must not prevent reading logs. */ }
}

export type ServerLogIssueDestination = 'files' | 'addons' | 'health'

export function serverLogIssueDestination(record: ServerLogRecord): ServerLogIssueDestination | null {
  switch (serverLogProblem(record)) {
    case 'Settings issue': return 'files'
    case 'Add-on issue': return 'addons'
    case 'Server problem': return 'health'
    default: return null
  }
}

const issueActions: Record<ServerLogIssueDestination, string> = {
  files: 'Open Files', addons: 'Review add-ons', health: 'Check health'
}

type LogLine = ServerLogRecord & { localSequence: number }
type ReadingPosition = { sequence: string | null; offset: number; scrollTop: number }

export function serverLogProblem(record: ServerLogRecord): string | null {
  const message = record.message.toLocaleLowerCase()
  if (/\b(mod|mods|addon|pack|dependency|dependencies|incompatible)\b/.test(message) &&
      /\b(error|failed|missing|invalid|incompatible|mismatch|could not)\b/.test(message)) return 'Add-on issue'
  if (/\b(config|configuration|properties|permission|whitelist|allowlist|json|eula)\b/.test(message) &&
      /\b(error|failed|missing|invalid|denied|could not)\b/.test(message)) return 'Settings issue'
  if (record.severity === 'Error' ||
      /\b(startup|launch|bind|port|listen)\b/.test(message) &&
      /\b(error|failed|in use|could not)\b/.test(message)) return 'Server problem'
  return null
}

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
  pollIntervalMs = 2500, batchSize = 100, historyLimit = 500, onNavigateIssue }: {
  endpoint: string
  visible: boolean
  unsupported?: ServerLogUnsupported | null
  loader?: ServerLogLoader
  pollIntervalMs?: number
  batchSize?: number
  historyLimit?: number
  onNavigateIssue?: (destination: ServerLogIssueDestination) => void
}) {
  const safeBatchSize = Math.max(1, Math.min(200, batchSize))
  const safeHistoryLimit = Math.max(1, Math.min(1000, historyLimit))
  const [records, setRecords] = useState<LogLine[]>([])
  const [result, setResult] = useState<ServerLogResult | null>(null)
  const [requestError, setRequestError] = useState('')
  const [paused, setPaused] = useState(false)
  const [loading, setLoading] = useState(false)
  const [filters, setFilters] = useState<ServerLogFilters>(readLogPreferences)
  const [filterDraft, setFilterDraft] = useState<ServerLogFilters>(readLogPreferences)
  const [onlyProblems, setOnlyProblems] = useState(false)
  const [followLatest, setFollowLatest] = useState(true)
  const [newLineCount, setNewLineCount] = useState(0)
  const [readingLineExpired, setReadingLineExpired] = useState(false)
  const [documentVisible, setDocumentVisible] = useState(() => !document.hidden)
  const [intersecting, setIntersecting] = useState(() => typeof IntersectionObserver === 'undefined')
  const rootRef = useRef<HTMLElement | null>(null)
  const consoleRef = useRef<HTMLDivElement | null>(null)
  const recordsRef = useRef<LogLine[]>([])
  const lineSequenceRef = useRef(0)
  const readingPositionRef = useRef<ReadingPosition | null>(null)
  const followingRef = useRef(true)
  const restoredScrollTopRef = useRef<number | null>(null)
  const cursorRef = useRef<string | null>(null)
  const runIdRef = useRef<string | null>(null)
  const generationRef = useRef(0)
  const inFlightRef = useRef<Promise<void> | null>(null)
  const requestControllerRef = useRef<AbortController | null>(null)
  const manualControllerRef = useRef<AbortController | null>(null)
  const filterKey = useMemo(() => JSON.stringify(filters), [filters])
  const restartKey = `${endpoint}|${filterKey}|${unsupported?.code ?? ''}`

  const rememberReadingPosition = useCallback(() => {
    const console = consoleRef.current
    if (!console) return
    const top = console.getBoundingClientRect().top
    const anchor = Array.from(console.querySelectorAll<HTMLElement>('[data-log-sequence]'))
      .find(line => line.getBoundingClientRect().bottom > top)
    readingPositionRef.current = { sequence: anchor?.dataset.logSequence ?? null,
      offset: anchor ? anchor.getBoundingClientRect().top - top : 0, scrollTop: console.scrollTop }
  }, [])

  const jumpToLatest = () => {
    followingRef.current = true
    setFollowLatest(true)
    setNewLineCount(0)
    setReadingLineExpired(false)
    readingPositionRef.current = null
    if (consoleRef.current) consoleRef.current.scrollTop = consoleRef.current.scrollHeight
  }

  useLayoutEffect(() => {
    const console = consoleRef.current
    if (!console || !visible) return
    if (followingRef.current) {
      console.scrollTop = console.scrollHeight
      restoredScrollTopRef.current = console.scrollTop
      return
    }
    const position = readingPositionRef.current
    if (!position) return
    const anchor = position.sequence === null ? null :
      console.querySelector<HTMLElement>(`[data-log-sequence="${position.sequence}"]`)
    if (anchor) {
      console.scrollTop += anchor.getBoundingClientRect().top - console.getBoundingClientRect().top - position.offset
    } else {
      console.scrollTop = position.sequence === null ? position.scrollTop : 0
      if (position.sequence !== null) setReadingLineExpired(true)
    }
    restoredScrollTopRef.current = console.scrollTop
    rememberReadingPosition()
  }, [records, onlyProblems, followLatest, visible, rememberReadingPosition])

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
    followingRef.current = true
    readingPositionRef.current = null
    setFollowLatest(true)
    setNewLineCount(0)
    setReadingLineExpired(false)
  }, [endpoint, filterKey, unsupported?.code])

  useEffect(() => () => {
    requestControllerRef.current?.abort()
    manualControllerRef.current?.abort()
  }, [])

  const applyResult = useCallback((next: ServerLogResult) => {
    if (!followingRef.current) rememberReadingPosition()
    const sameRun = runIdRef.current !== null && runIdRef.current === next.runId
    const runChanged = runIdRef.current !== null && next.runId !== null && runIdRef.current !== next.runId
    if (runChanged || (next.runId === null && next.sourceState !== 'Active') || (!next.ok && !sameRun)) {
      recordsRef.current = []
      setRecords([])
      readingPositionRef.current = null
      setNewLineCount(0)
      setReadingLineExpired(false)
      followingRef.current = true
      setFollowLatest(true)
    }
    if (next.ok) {
      const base = runChanged ? [] : recordsRef.current
      const merged = [...base, ...next.records.map(record => ({ ...record, localSequence: ++lineSequenceRef.current }))].slice(-safeHistoryLimit)
      recordsRef.current = merged
      setRecords(merged)
      cursorRef.current = next.cursor
      if (!followingRef.current) setNewLineCount(count => Math.min(1000, count + next.records.length))
    } else if (!sameRun) {
      cursorRef.current = null
    }
    runIdRef.current = next.runId
    setResult(next)
    setRequestError('')
  }, [safeHistoryLimit, rememberReadingPosition])

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
        readingPositionRef.current = null
        setNewLineCount(0)
        setReadingLineExpired(false)
        followingRef.current = true
        setFollowLatest(true)
        await read(false)
        return
      }
      applyResult(next)
    }

    setLoading(true)
    const tracked = read(true).catch(error => {
      if (controller.signal.aborted || generation !== generationRef.current) return
      throw error
    }).finally(() => {
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
  const visibleRecords = onlyProblems ? records.filter(record => serverLogProblem(record) !== null) : records

  return <section ref={rootRef} className="server-log-viewer" aria-label="Server logs">
    <div className="server-log-toolbar">
      <div className="server-log-heading"><span className={`server-log-state state-${sourceState.toLocaleLowerCase()}`}>{stateLabel(sourceState, paused)}</span>
        <small>{visibleRecords.length} of {onlyProblems ? records.length : safeHistoryLimit} lines shown{result?.hasMore ? ' · more available' : ''}</small></div>
      <div className="server-log-actions"><Button className="secondary" disabled={!!unsupported} onClick={() => setPaused(value => !value)}>{paused ? <><Icon name="play" />Resume</> : <><Icon name="stop" />Pause</>}</Button>
        <Button className="secondary" aria-pressed={followLatest} onClick={() => {
          if (!followingRef.current) jumpToLatest()
          else { rememberReadingPosition(); followingRef.current = false; setFollowLatest(false) }
        }}>Follow latest</Button>
        <Button className="secondary" disabled={loading || !!unsupported || !visible} onClick={manualRefresh}>{loading ? <><Icon name="loader" />Refreshing</> : <><Icon name="refresh" />Refresh</>}</Button></div>
    </div>
    <form className="server-log-filters" onSubmit={event => {
      event.preventDefault()
      setFilters({ ...filterDraft, contains: filterDraft.contains.trim() })
      saveLogPreferences(filterDraft)
    }}>
      <label>Severity<Select aria-label="Log severity" value={filterDraft.severity} onChange={event => setFilterDraft(current => ({ ...current, severity: event.target.value as ServerLogFilters['severity'] }))}>
        <option value="">All</option><option value="Info">Info</option><option value="Warning">Warning</option><option value="Error">Error</option>
      </Select></label>
      <label>Category<Select aria-label="Log category" value={filterDraft.category} onChange={event => setFilterDraft(current => ({ ...current, category: event.target.value as ServerLogFilters['category'] }))}>
        <option value="">All</option><option value="Server">Server</option><option value="Lifecycle">Start and stop</option><option value="Player">Player</option><option value="Connections">Connections</option>
      </Select></label>
      <label className="server-log-contains">Contains<Input aria-label="Log contains" maxLength={80} value={filterDraft.contains} placeholder="Find text" onChange={event => setFilterDraft(current => ({ ...current, contains: event.target.value }))} /></label>
      <Button type="submit" className="secondary">Apply</Button>
      <Button type="button" className={onlyProblems ? '' : 'secondary'} aria-pressed={onlyProblems}
        onClick={() => setOnlyProblems(value => !value)}>Likely problems</Button>
      <Button className="text-button" disabled={!filters.severity && !filters.category && !filters.contains && !filterDraft.severity && !filterDraft.category && !filterDraft.contains} onClick={() => { setFilterDraft(emptyFilters); setFilters(emptyFilters); saveLogPreferences(emptyFilters) }}>Clear</Button>
    </form>
    <div className={`server-log-source state-${sourceState.toLocaleLowerCase()}`} role="status"><strong>{stateLabel(sourceState, paused)}</strong><span>{message}</span></div>
    {!followLatest && <div className="server-log-reading" role="status"><span>{newLineCount > 0 ? `${newLineCount}${newLineCount === 1000 ? '+' : ''} new line${newLineCount === 1 ? '' : 's'}` : 'Reading earlier lines'}{readingLineExpired ? ' · The line you were reading is no longer in the loaded view.' : ''}</span>
      <Button className="secondary" onClick={jumpToLatest}>Jump to latest</Button></div>}
    <div ref={consoleRef} className="server-log-console" role="log" aria-live="off" aria-label="Server log console"
      onScroll={event => {
        const console = event.currentTarget
        const restoredScrollTop = restoredScrollTopRef.current
        restoredScrollTopRef.current = null
        if (restoredScrollTop !== null && Math.abs(restoredScrollTop - console.scrollTop) < 1) return
        const nearLatest = console.scrollHeight - console.clientHeight - console.scrollTop <= 24
        if (!nearLatest) {
          followingRef.current = false
          setFollowLatest(false)
          rememberReadingPosition()
        } else if (!followingRef.current && console.scrollHeight > console.clientHeight) {
          followingRef.current = true
          setFollowLatest(true)
          setNewLineCount(0)
          setReadingLineExpired(false)
        }
      }}>
      {visibleRecords.map(record => <div className={`server-log-line severity-${record.severity.toLocaleLowerCase()}${serverLogProblem(record) ? ' likely-problem' : ''}`} key={record.localSequence} data-log-sequence={record.localSequence}>
        <time dateTime={record.timestampUtc ?? undefined}>{displayTime(record.timestampUtc)}</time><span className="server-log-severity">{record.severity}</span><span className="server-log-category">{record.category}</span><code>{record.message}</code>
        {serverLogIssueDestination(record) && (onNavigateIssue ? <Button className="text-button server-log-problem-label"
          onClick={() => onNavigateIssue(serverLogIssueDestination(record)!)}>{serverLogProblem(record)} · {issueActions[serverLogIssueDestination(record)!]}</Button> :
          <small className="server-log-problem-label">{serverLogProblem(record)} · {issueActions[serverLogIssueDestination(record)!]}</small>)}
      </div>)}
      {visibleRecords.length === 0 && <div className="server-log-empty">{onlyProblems && records.length > 0 ? 'No likely startup, settings, or add-on problems in the loaded lines.' : sourceState === 'Active' ? 'No matching complete lines yet.' : sourceState === 'Loading' ? 'Loading recent records…' : 'No log records are available for this state.'}</div>}
    </div>
    <p className="server-log-disclaimer">Diagnostic display only. These records never determine readiness, player count, timers, Start, Stop, or recovery.</p>
  </section>
}

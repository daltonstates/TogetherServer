import { useId, useState, type ReactNode } from 'react'
import { Button, Input, Select } from './Controls'
import { Icon } from './Icon'
import type { ActivityEvent } from './contracts'
import { activityDestination, type ActivityDestination } from './notificationState'

export type AttentionServer = { id: string; name: string }
// These rows come from the current canonical snapshot, never activity history.
export type AttentionProblemRow = {
  id: string
  name: string
  warning: string
  priority: number
  destination: ActivityDestination
  category?: string
  severity?: ActivityEvent['severity']
}
export type AttentionFilters = {
  serverId: string
  category: string
  severity: 'all' | ActivityEvent['severity']
  time: 'all' | 'hour' | 'day' | 'week' | 'month'
  search: string
  historyState: 'visible' | 'unread' | 'read' | 'dismissed' | 'all'
}
export const defaultAttentionFilters: AttentionFilters = {
  serverId: 'all', category: 'all', severity: 'all', time: 'all', search: '', historyState: 'visible'
}
export type AttentionHistoryIds = { readIds: string[]; dismissedIds: string[] }
export type AttentionHistoryState = {
  version: 1
  sources: Array<AttentionHistoryIds & { source: string }>
}
export type AttentionStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>
export const attentionHistoryStorageKey = 'togetherserver.attention-history.v1'
const maximumSources = 20
const maximumIds = 500
const maximumStoredCharacters = 1_000_000
const opaqueIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const emptyHistory = (): AttentionHistoryState => ({ version: 1, sources: [] })
const emptyIds = (): AttentionHistoryIds => ({ readIds: [], dismissedIds: [] })
function opaqueId(value: unknown): value is string {
  return typeof value === 'string' && opaqueIdPattern.test(value)
}
function opaqueSource(value: unknown): value is string {
  return typeof value === 'string' && (value === 'host' ||
    (value.startsWith('friend:') && opaqueId(value.slice('friend:'.length))))
}
function objectWithKeys(value: unknown, keys: string[]): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) &&
    Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key))
}
function validIds(value: unknown): value is string[] {
  return Array.isArray(value) && value.length <= maximumIds && value.every(opaqueId) && new Set(value).size === value.length
}
function decodeHistory(value: unknown): AttentionHistoryState | null {
  if (!objectWithKeys(value, ['version', 'sources']) || value.version !== 1 ||
      !Array.isArray(value.sources) || value.sources.length > maximumSources) return null
  const sources: AttentionHistoryState['sources'] = []
  for (const item of value.sources) {
    if (!objectWithKeys(item, ['source', 'readIds', 'dismissedIds']) || !opaqueSource(item.source) ||
        !validIds(item.readIds) || !validIds(item.dismissedIds) || sources.some(source => source.source === item.source)) return null
    sources.push({ source: item.source, readIds: [...item.readIds], dismissedIds: [...item.dismissedIds] })
  }
  return { version: 1, sources }
}

export function readAttentionHistory(storage: Pick<Storage, 'getItem' | 'removeItem'>): AttentionHistoryState {
  try {
    const saved = storage.getItem(attentionHistoryStorageKey)
    if (!saved) return emptyHistory()
    if (saved.length > maximumStoredCharacters) throw new Error('invalid state')
    const parsed = decodeHistory(JSON.parse(saved))
    if (!parsed) throw new Error('invalid state')
    return parsed
  } catch {
    try { storage.removeItem(attentionHistoryStorageKey) }
    catch { /* Optional browser storage must not break Attention. */ }
    return emptyHistory()
  }
}

export function writeAttentionHistory(storage: Pick<Storage, 'setItem'>, state: AttentionHistoryState): void {
  const safe = decodeHistory(state)
  if (!safe) return
  try { storage.setItem(attentionHistoryStorageKey, JSON.stringify(safe)) }
  catch { /* Read/dismiss still applies for this mounted workspace. */ }
}
function boundedIds(ids: readonly string[]): string[] {
  return [...new Set(ids.filter(opaqueId))].slice(-maximumIds)
}

export function withAttentionHistorySource(state: AttentionHistoryState, source: string,
  ids: AttentionHistoryIds): AttentionHistoryState {
  const safe = decodeHistory(state) ?? emptyHistory()
  if (!opaqueSource(source)) return safe
  return { version: 1, sources: [...safe.sources.filter(item => item.source !== source), {
    source, readIds: boundedIds(ids.readIds), dismissedIds: boundedIds(ids.dismissedIds)
  }].slice(-maximumSources) }
}

function problemCategory(problem: AttentionProblemRow): string {
  if (problem.category) return problem.category
  switch (problem.destination.section) {
    case 'players': case 'stop': return 'Players'
    case 'backups': return 'Backup'
    case 'network': return 'Network'
    case 'access': return 'Access'
    case 'diagnostics': return 'Recovery'
    default: return 'Lifecycle'
  }
}
function problemSeverity(problem: AttentionProblemRow): ActivityEvent['severity'] {
  return problem.severity ?? (problem.priority >= 90 ? 'Warning' : 'Important')
}
function matchesCommonFilters(profileId: string | null, category: string, severity: ActivityEvent['severity'],
  text: string, filters: AttentionFilters): boolean {
  return (filters.serverId === 'all' || (filters.serverId === 'app' ? profileId === null : profileId === filters.serverId)) &&
    (filters.category === 'all' || category === filters.category) &&
    (filters.severity === 'all' || severity === filters.severity) &&
    (!filters.search.trim() || text.toLocaleLowerCase().includes(filters.search.trim().toLocaleLowerCase()))
}

export function filterAttentionProblems(problems: readonly AttentionProblemRow[], filters: AttentionFilters): AttentionProblemRow[] {
  // History time/read/dismiss filters never hide a still-active problem.
  return problems.filter(problem => problem.priority > 0 && !!problem.warning &&
    matchesCommonFilters(problem.id, problemCategory(problem), problemSeverity(problem),
      `${problem.name} ${problem.warning} ${problemCategory(problem)}`, filters))
}

export function filterAttentionEvents(events: readonly ActivityEvent[], filters: AttentionFilters, nowMs: number,
  servers: readonly AttentionServer[] = [], ids: AttentionHistoryIds = emptyIds()): ActivityEvent[] {
  const windows = { hour: 3_600_000, day: 86_400_000, week: 7 * 86_400_000, month: 30 * 86_400_000 }
  const read = new Set(ids.readIds)
  const dismissed = new Set(ids.dismissedIds)
  return events.filter(event => {
    const serverName = servers.find(server => server.id === event.profileId)?.name ?? ''
    if (!matchesCommonFilters(event.profileId, event.category, event.severity,
      `${serverName} ${event.category} ${event.action} ${event.message}`, filters)) return false
    if (filters.time !== 'all') {
      const occurred = Date.parse(event.occurredUtc)
      if (!Number.isFinite(occurred) || !Number.isFinite(nowMs) || occurred > nowMs || nowMs - occurred > windows[filters.time]) return false
    }
    switch (filters.historyState) {
      case 'visible': return !dismissed.has(event.id)
      case 'unread': return !read.has(event.id) && !dismissed.has(event.id)
      case 'read': return read.has(event.id) && !dismissed.has(event.id)
      case 'dismissed': return dismissed.has(event.id)
      case 'all': return true
    }
  })
}

export function groupAttentionEvents(events: readonly ActivityEvent[]): Array<{ latest: ActivityEvent; events: ActivityEvent[]; repeatCount: number }> {
  const ordered = [...events].sort((a, b) => (Date.parse(b.occurredUtc) || 0) - (Date.parse(a.occurredUtc) || 0))
  const seen = new Set<string>()
  const groups = new Map<string, { latest: ActivityEvent; events: ActivityEvent[]; repeatCount: number }>()
  for (const event of ordered) {
    if (seen.has(event.id)) continue
    seen.add(event.id)
    const key = JSON.stringify([event.category, event.action, event.profileId, event.deviceId, event.severity])
    const existing = groups.get(key)
    if (existing) { existing.events.push(event); existing.repeatCount += 1 }
    else groups.set(key, { latest: event, events: [event], repeatCount: 1 })
  }
  return [...groups.values()]
}

export type AttentionWorkspaceProps = {
  mode: 'Host' | 'Friend'
  activitySource: string
  activity: readonly ActivityEvent[]
  currentProblems?: readonly AttentionProblemRow[]
  servers: readonly AttentionServer[]
  nowMs: number
  onOpen?: (destination: ActivityDestination) => void
  onOpenFriendServer?: (profileId: string) => void
  onHistoryChange?: (source: string, ids: AttentionHistoryIds) => void
  children?: ReactNode
  storage?: AttentionStorage | null
}

function storageFor(storage: AttentionWorkspaceProps['storage']): AttentionStorage | null {
  if (storage !== undefined) return storage
  try { return window.localStorage }
  catch { return null }
}
function eventTime(value: string): string {
  const timestamp = Date.parse(value)
  return Number.isFinite(timestamp) ? new Date(timestamp).toLocaleString() : 'Event time unavailable'
}

export function AttentionWorkspace(props: AttentionWorkspaceProps) {
  // Changing saved Host must replace local filters and ID state together.
  return <AttentionWorkspaceContent key={`${props.mode}:${props.activitySource}`} {...props} />
}

function AttentionWorkspaceContent({ mode, activitySource, activity, currentProblems = [], servers, nowMs,
  onOpen, onOpenFriendServer, onHistoryChange, children, storage }: AttentionWorkspaceProps) {
  const headingId = useId()
  const [filters, setFilters] = useState<AttentionFilters>({ ...defaultAttentionFilters })
  const [ids, setIds] = useState<AttentionHistoryIds>(() => {
    const available = storageFor(storage)
    const saved = available ? readAttentionHistory(available).sources.find(item => item.source === activitySource) : null
    return saved ? { readIds: saved.readIds, dismissedIds: saved.dismissedIds } : emptyIds()
  })
  const [feedback, setFeedback] = useState('')
  const serverIds = new Set(servers.map(server => server.id))
  const scopedEvents = groupAttentionEvents(activity.filter(event => mode === 'Host' ||
    ((event.visibility === 'AssignedFriends' || event.visibility === 'Device') &&
      (event.profileId === null || serverIds.has(event.profileId)))))
    .flatMap(group => group.events).sort((a, b) => (Date.parse(b.occurredUtc) || 0) - (Date.parse(a.occurredUtc) || 0)).slice(0, maximumIds)
  const scopedProblems = mode === 'Host' ? currentProblems.filter(problem => serverIds.has(problem.id) && problem.priority > 0 && !!problem.warning) : []
  const problems = filterAttentionProblems(scopedProblems, filters)
  const events = filterAttentionEvents(scopedEvents, filters, nowMs, servers, ids)
  const groups = groupAttentionEvents(events)
  const categories = [...new Set([...scopedProblems.map(problemCategory), ...scopedEvents.map(event => event.category)])].sort()
  const unreadCount = scopedEvents.filter(event => !ids.readIds.includes(event.id) && !ids.dismissedIds.includes(event.id)).length
  const changeIds = (next: AttentionHistoryIds, message: string) => {
    const bounded = { readIds: boundedIds(next.readIds), dismissedIds: boundedIds(next.dismissedIds) }
    setIds(bounded)
    const available = storageFor(storage)
    if (available) writeAttentionHistory(available, withAttentionHistorySource(readAttentionHistory(available), activitySource, bounded))
    if (opaqueSource(activitySource)) onHistoryChange?.(activitySource, { readIds: [...bounded.readIds], dismissedIds: [...bounded.dismissedIds] })
    setFeedback(message)
  }
  const eventContent = (event: ActivityEvent) => {
    const isRead = ids.readIds.includes(event.id)
    const isDismissed = ids.dismissedIds.includes(event.id)
    const destination = mode === 'Host' ? activityDestination(event) : null
    const canOpen = destination && onOpen && (!destination.profileId || serverIds.has(destination.profileId))
    return <div className="attention-history-event" key={event.id}>
      <p>{event.message}</p><small><time dateTime={event.occurredUtc}>{eventTime(event.occurredUtc)}</time> · {isDismissed ? 'Dismissed' : isRead ? 'Read' : 'Unread'}</small>
      <div className="actions">
        {canOpen && destination && <Button className="text-button" onClick={() => {
          if (mode === 'Host' && destination) onOpen?.(destination)
        }}>{destination.label}</Button>}
        {mode === 'Friend' && event.profileId !== null && serverIds.has(event.profileId) && onOpenFriendServer &&
          <Button className="text-button" onClick={() => onOpenFriendServer(event.profileId!)}>Open server</Button>}
        <Button className="text-button" disabled={!opaqueId(event.id)} onClick={() => changeIds({ ...ids,
          readIds: isRead ? ids.readIds.filter(id => id !== event.id) : [...ids.readIds, event.id]
        }, isRead ? 'History notice marked unread.' : 'History notice marked read.')}>{isRead ? 'Mark unread' : 'Mark read'}</Button>
        <Button className="text-button" disabled={!opaqueId(event.id)} onClick={() => changeIds({ ...ids,
          dismissedIds: isDismissed ? ids.dismissedIds.filter(id => id !== event.id) : [...ids.dismissedIds, event.id]
        }, isDismissed ? 'History notice restored.' : 'History notice dismissed. Current problems are unchanged.')}>{isDismissed ? 'Restore notice' : 'Dismiss notice'}</Button>
      </div>
    </div>
  }
  return <section className="attention-workspace" aria-labelledby={headingId}>
    <div className="attention-toolbar"><div><h2 id={headingId}>Attention Center</h2>
      <span>{mode === 'Host' ? `${scopedProblems.length} current problems` : 'Current saved Host activity'} · {unreadCount} unread history notices</span></div>
      <div className="attention-toolbar-actions"><Button className="secondary" disabled={!scopedEvents.some(event => opaqueId(event.id) && !ids.dismissedIds.includes(event.id))}
        onClick={() => changeIds({ ...ids, dismissedIds: [...ids.dismissedIds, ...scopedEvents.map(event => event.id)] },
          mode === 'Host' ? 'History cleared for this Host. Current problems remain visible.' : 'History cleared for this saved Host.')}>Clear history</Button></div>
    </div>
    <div className="settings-grid attention-filters" style={{ padding: '1rem' }}>
      <label>Search attention<Input type="search" value={filters.search} maxLength={200} placeholder="Server, category or notice" onChange={event => setFilters({ ...filters, search: event.target.value })} /></label>
      <label>Server<Select value={filters.serverId} onChange={event => setFilters({ ...filters, serverId: event.target.value })}>
        <option value="all">All servers and app</option><option value="app">App and connection</option>
        {servers.map(server => <option value={server.id} key={server.id}>{server.name}</option>)}
      </Select></label>
      <label>Category<Select value={filters.category} onChange={event => setFilters({ ...filters, category: event.target.value })}>
        <option value="all">All categories</option>{categories.map(category => <option key={category} value={category}>{category}</option>)}
      </Select></label>
      <label>Severity<Select value={filters.severity} onChange={event => setFilters({ ...filters, severity: event.target.value as AttentionFilters['severity'] })}>
        <option value="all">All severities</option><option value="Info">Information</option><option value="Important">Important</option><option value="Warning">Warning</option>
      </Select></label>
      <label>History time<Select value={filters.time} onChange={event => setFilters({ ...filters, time: event.target.value as AttentionFilters['time'] })}>
        <option value="all">All retained history</option><option value="hour">Last hour</option><option value="day">Last day</option><option value="week">Last 7 days</option><option value="month">Last 30 days</option>
      </Select></label>
      <label>History notices<Select value={filters.historyState} onChange={event => setFilters({ ...filters, historyState: event.target.value as AttentionFilters['historyState'] })}>
        <option value="visible">Visible notices</option><option value="unread">Unread</option><option value="read">Read</option><option value="dismissed">Dismissed</option><option value="all">All, including dismissed</option>
      </Select></label>
      <Button className="text-button" onClick={() => setFilters({ ...defaultAttentionFilters })}>Reset filters</Button>
    </div>
    {children}
    {mode === 'Host' && <section aria-label="Current problems" style={{ padding: '0 1rem 1rem' }}><h3>Current problems · {problems.length}</h3>
      <p className="helper-text">From the current Host state. Reading or dismissing history cannot resolve these problems. History time filters apply below.</p>
      {problems.length === 0 ? <p>{scopedProblems.length ? 'No current problems match these filters.' : 'No current server problems are reported.'}</p> :
        problems.map(problem => <article className="notification-item bad" key={problem.id}><span aria-hidden="true"><Icon name="warning" /></span><div><strong>{problem.name}</strong><p>{problem.warning}</p>
          <small>{problemCategory(problem)} · {problemSeverity(problem)}</small>
          {onOpen && <Button className="text-button" onClick={() => onOpen(problem.destination)}>{problem.destination.label}</Button>}
        </div></article>)}
    </section>}
    <section aria-label="Activity history" style={{ padding: '0 1rem 1rem' }}><h3>Activity history · {events.length}</h3>
      <p className="helper-text">Past events can remain after a problem ends. Clear history hides the retained notices on this PC; new events appear normally.</p>
      <div className="attention-list">
      {groups.length === 0 ? <p>{scopedEvents.length ? 'No history notices match these filters.' : 'No retained activity for this Host.'}</p> :
        groups.map(group => <article className={`notification-item ${group.latest.severity === 'Warning' ? 'bad' : ''}`} key={group.latest.id}><span aria-hidden="true"><Icon name={group.latest.severity === 'Warning' ? 'warning' : 'bell'} /></span><div>
          <strong>{group.latest.category} · {group.latest.severity}</strong>
          {group.latest.profileId && <small>{servers.find(server => server.id === group.latest.profileId)?.name ?? 'Former saved server'}</small>}
          {group.repeatCount > 1 && <small>Repeated {group.repeatCount} times in these results</small>}
          {eventContent(group.latest)}
          {group.events.length > 1 && <details><summary>Earlier matching events · {group.events.length - 1}</summary>{group.events.slice(1).map(eventContent)}</details>}
        </div></article>)}
      </div>
    </section>
    <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">{mode === 'Host' ? `${scopedProblems.length} current problems. ` : ''}{unreadCount} unread history notices.</span>
    <p className="helper-text" role="status" aria-live="polite" aria-atomic="true">{feedback}</p>
  </section>
}

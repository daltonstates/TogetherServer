type ActivityItem = { id: string }
type ActivityClearMarkers = Record<string, string>
type ReadableStorage = Pick<Storage, 'getItem' | 'removeItem'>
type WritableStorage = Pick<Storage, 'setItem'>
type StorageProvider<T> = () => T

export type ActivityDestination =
  | { workspace: 'host'; section: 'overview' | 'players' | 'backups' | 'sessions' | 'setup' | 'files' | 'logs'; profileId?: string | null; label: string }
  | { workspace: 'settings'; section: 'access' | 'network' | 'stop' | 'diagnostics'; profileId?: string | null; label: string }

type RoutedActivity = {
  id: string
  category: string
  action: string
  profileId?: string | null
  deviceId?: string | null
}

export const activityClearStorageKey = 'togetherserver.activity-clear-markers'

export function activityAfterMarker<T extends ActivityItem>(activity: T[], marker?: string): T[] {
  if (!marker) return activity
  const markerIndex = activity.findIndex(item => item.id === marker)
  return markerIndex < 0 ? activity : activity.slice(0, markerIndex)
}

export function withActivityClearMarker(markers: ActivityClearMarkers, source: string,
  activityId: string): ActivityClearMarkers {
  const previous = Object.entries(markers).filter(([key]) => key !== source)
  return Object.fromEntries([...previous.slice(-99), [source, activityId]]) as ActivityClearMarkers
}

export function readActivityClearMarkers(storage: ReadableStorage): ActivityClearMarkers {
  try {
    const saved = storage.getItem(activityClearStorageKey)
    if (!saved) return {}
    const parsed: unknown = JSON.parse(saved)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) throw new Error('invalid state')
    const source = parsed as Record<string, unknown>
    if (source.version !== 1 || typeof source.markers !== 'object' || source.markers === null ||
        Array.isArray(source.markers)) throw new Error('invalid state')
    const entries = Object.entries(source.markers)
    if (entries.length > 100 || entries.some(([key, value]) => key.length < 1 || key.length > 96 ||
        typeof value !== 'string' || value.length < 1 || value.length > 128)) throw new Error('invalid state')
    return Object.fromEntries(entries) as ActivityClearMarkers
  } catch {
    try { storage.removeItem(activityClearStorageKey) }
    catch { /* Browser storage is optional; clearing activity must not break the app. */ }
    return {}
  }
}

export function readActivityClearMarkersFrom(provider: StorageProvider<ReadableStorage>): ActivityClearMarkers {
  try { return readActivityClearMarkers(provider()) }
  catch { return {} }
}

export function writeActivityClearMarkersTo(provider: StorageProvider<WritableStorage>,
  markers: ActivityClearMarkers): void {
  try { provider().setItem(activityClearStorageKey, JSON.stringify({ version: 1, markers })) }
  catch { /* Browser storage is optional; the clear still applies for this app session. */ }
}

export function activityDestination(item: RoutedActivity): ActivityDestination | null {
  switch (item.category) {
    case 'Backup': return { workspace: 'host', section: 'backups', profileId: item.profileId, label: 'Open world protection' }
    case 'Countdown': case 'Players': return { workspace: 'host', section: 'players', profileId: item.profileId, label: 'Open players & timer' }
    case 'Lifecycle': return { workspace: 'host', section: 'overview', profileId: item.profileId, label: 'Open server' }
    case 'Maintenance': return { workspace: 'host', section: 'setup', profileId: item.profileId, label: 'Continue maintenance' }
    case 'Remote': return { workspace: 'host', section: 'sessions', profileId: item.profileId, label: 'Review server activity' }
    case 'Connections': case 'Access': return { workspace: 'settings', section: 'access', profileId: item.profileId, label: 'Review Friend access' }
    case 'Network': return { workspace: 'settings', section: 'network', profileId: item.profileId, label: 'Open Connection Doctor' }
    case 'Recovery': return { workspace: 'settings', section: 'diagnostics', profileId: item.profileId, label: 'Review diagnostics' }
    default: return null
  }
}

export function collapseRepeatedActivity<T extends RoutedActivity>(activity: T[]) {
  const groups = new Map<string, { item: T; repeatCount: number }>()
  for (const item of activity) {
    const key = [item.category, item.action, item.profileId ?? '', item.deviceId ?? ''].join('|')
    const existing = groups.get(key)
    if (existing) existing.repeatCount += 1
    else groups.set(key, { item, repeatCount: 1 })
  }
  return [...groups.values()]
}

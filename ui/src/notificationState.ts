type ActivityItem = { id: string }
type ActivityClearMarkers = Record<string, string>
type ReadableStorage = Pick<Storage, 'getItem' | 'removeItem'>
type WritableStorage = Pick<Storage, 'setItem'>
type StorageProvider<T> = () => T

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

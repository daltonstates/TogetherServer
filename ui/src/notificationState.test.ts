import { describe, expect, it, vi } from 'vitest'
import {
  activityAfterMarker,
  activityDestination,
  activityClearStorageKey,
  collapseRepeatedActivity,
  readActivityClearMarkers,
  readActivityClearMarkersFrom,
  withActivityClearMarker,
  writeActivityClearMarkersTo
} from './notificationState'

describe('notification activity state', () => {
  const activity = [{ id: 'new' }, { id: 'cleared-through' }, { id: 'old' }]

  it('shows only activity newer than the saved clear marker', () => {
    expect(activityAfterMarker(activity, 'cleared-through')).toEqual([{ id: 'new' }])
    expect(activityAfterMarker(activity, 'new')).toEqual([])
    expect(activityAfterMarker(activity, 'missing')).toEqual(activity)
  })

  it('round-trips versioned markers without storing activity content', () => {
    const setItem = vi.fn()
    writeActivityClearMarkersTo(() => ({ setItem }), { host: 'activity-id' })

    expect(setItem).toHaveBeenCalledWith(activityClearStorageKey,
      '{"version":1,"markers":{"host":"activity-id"}}')
    const stored = setItem.mock.calls[0][1] as string
    expect(readActivityClearMarkers({ getItem: () => stored, removeItem: vi.fn() }))
      .toEqual({ host: 'activity-id' })
  })

  it('keeps the most recent one hundred source markers', () => {
    const markers = Object.fromEntries(Array.from({ length: 100 }, (_, index) => [`friend:${index}`, `id:${index}`]))
    const next = withActivityClearMarker(markers, 'host', 'host-id')

    expect(Object.keys(next)).toHaveLength(100)
    expect(next['friend:0']).toBeUndefined()
    expect(next.host).toBe('host-id')
  })

  it('treats unavailable or malformed browser storage as optional', () => {
    const removeItem = vi.fn()
    expect(readActivityClearMarkers({ getItem: () => '{bad json', removeItem })).toEqual({})
    expect(removeItem).toHaveBeenCalledWith(activityClearStorageKey)

    const denied = () => { throw new DOMException('denied', 'SecurityError') }
    expect(readActivityClearMarkersFrom(denied)).toEqual({})
    expect(() => writeActivityClearMarkersTo(denied, { host: 'activity-id' })).not.toThrow()
  })

  it('routes actionable events without embedding commands in activity data', () => {
    expect(activityDestination({ id: '1', category: 'Backup', action: 'BackupFailed', profileId: 'server' }))
      .toEqual({ workspace: 'host', section: 'backups', profileId: 'server', label: 'Open world protection' })
    expect(activityDestination({ id: '2', category: 'Access', action: 'PermissionsChanged' }))
      .toEqual({ workspace: 'settings', section: 'access', profileId: undefined, label: 'Review Friend access' })
    expect(activityDestination({ id: 'players', category: 'Players', action: 'CountIncreased', profileId: 'server' }))
      .toEqual({ workspace: 'host', section: 'players', profileId: 'server', label: 'Open players & timer' })
    expect(activityDestination({ id: '3', category: 'Other', action: 'Observed' })).toBeNull()
  })

  it('collapses repeated event kinds while retaining the newest item', () => {
    const grouped = collapseRepeatedActivity([
      { id: 'new', category: 'Network', action: 'RouteChanged', profileId: null },
      { id: 'old', category: 'Network', action: 'RouteChanged', profileId: null },
      { id: 'backup', category: 'Backup', action: 'BackupFailed', profileId: 'server' }
    ])
    expect(grouped).toEqual([
      { item: { id: 'new', category: 'Network', action: 'RouteChanged', profileId: null }, repeatCount: 2 },
      { item: { id: 'backup', category: 'Backup', action: 'BackupFailed', profileId: 'server' }, repeatCount: 1 }
    ])
  })
})

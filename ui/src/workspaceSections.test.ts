import { describe, expect, it } from 'vitest'
import { parseDesktopNotification } from './desktopNotificationBridge'
import { hostPcSections, joinSections, workspaceDestination } from './workspaceSections'

const profileId = '11111111-1111-1111-1111-111111111111'
const connectionId = '22222222-2222-2222-2222-222222222222'

describe('workspace destinations', () => {
  it('offers one clear Network location for local hosting and one Settings location for a friend server', () => {
    expect(hostPcSections.map(section => section.label)).toEqual(['Access', 'Shutdown', 'Network', 'Advanced'])
    expect(joinSections.filter(section => section.id === 'network' || section.id === 'connection'))
      .toEqual([{ id: 'connection', label: 'Settings', icon: 'settings' }])
    expect(joinSections.some(section => section.id === 'pairing')).toBe(false)
  })
  it('routes the old app alias to Appearance regardless of a saved server hint', () => {
    expect(workspaceDestination({ workspace: 'settings', section: 'app', profileId, connectionId: null }))
      .toEqual({ workspace: 'settings', section: 'appearance' })
  })
  it.each(['access', 'stop', 'network', 'diagnostics', 'advanced'])('preserves the old %s alias inside This Host PC', section => {
    expect(workspaceDestination({ workspace: 'settings', section, profileId, connectionId: null }))
      .toEqual({ workspace: 'host', scope: 'pc', section, profileId })
  })
  it('keeps game settings scoped to the selected server', () => {
    expect(workspaceDestination({ workspace: 'host', section: 'files', profileId, connectionId: null }))
      .toEqual({ workspace: 'host', scope: 'server', section: 'files', profileId })
  })
  it('maps the old Join overview to Play without losing Host identity', () => {
    expect(workspaceDestination({ workspace: 'join', section: 'overview', profileId, connectionId }))
      .toEqual({ workspace: 'join', section: 'play', profileId, connectionId })
  })
  it('maps old Join network links into the consolidated Settings page without losing the selected server', () => {
    expect(workspaceDestination({ workspace: 'join', section: 'network', profileId, connectionId }))
      .toEqual({ workspace: 'join', section: 'connection', profileId, connectionId })
  })
  it('keeps detailed Host diagnostics reachable after removing its top-level settings item', () => {
    expect(workspaceDestination({ workspace: 'host', section: 'diagnostics', profileId, connectionId: null }))
      .toEqual({ workspace: 'host', scope: 'pc', section: 'diagnostics', profileId })
  })
  it.each(['appearance', 'notifications', 'game-ports', 'windows', 'updates', 'shortcuts'])('accepts the explicit App Settings %s destination', section => {
    const parsed = parseDesktopNotification({ type: 'together-notification', destination: {
      workspace: 'settings', section, profileId: null, connectionId: null
    } })
    expect(parsed).not.toBeNull()
    expect(workspaceDestination(parsed!)).toEqual({ workspace: 'settings', section })
  })
  it('still rejects unsupported destinations and cross-workspace connection IDs', () => {
    expect(parseDesktopNotification({ type: 'together-notification', destination: {
      workspace: 'host', section: 'appearance', profileId, connectionId: null
    } })).toBeNull()
    expect(parseDesktopNotification({ type: 'together-notification', destination: {
      workspace: 'settings', section: 'appearance', profileId: null, connectionId
    } })).toBeNull()
  })
})

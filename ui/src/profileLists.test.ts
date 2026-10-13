import { describe, expect, it } from 'vitest'
import type { FriendSnapshot } from './contracts'
import { isRemovedHost, orderHostConnections, orderProfiles } from './profileLists'

export function savedHost(id: string, state: string, connectionCode: string | null = null): FriendSnapshot {
  return { mode: 'Friend', state, connectionCode, detail: '', endpoint: 'https://192.0.2.1:5131',
    lastConnectedUtc: null, remoteControlsEnabled: false, canStart: false, canStop: false,
    profiles: [], connectionId: id, connectionName: id, connections: null, hostCapabilities: [] }
}

describe('profile list preferences', () => {
  it('puts active servers ahead of offline favorites and manual order without mutating settings', () => {
    const profiles = [{ id: 'offline-favorite' }, { id: 'offline' }, { id: 'ready' }, { id: 'starting' }, { id: 'unknown' }]
    const runs = [{ profileId: 'ready', state: 'Ready' }, { profileId: 'starting', state: 'Starting' },
      { profileId: 'unknown', state: 'Unknown' }]
    expect(orderProfiles(profiles, runs, ['offline-favorite'], ['offline', 'starting', 'ready']).map(profile => profile.id))
      .toEqual(['starting', 'ready', 'offline-favorite', 'offline', 'unknown'])
    expect(profiles.map(profile => profile.id)).toEqual(['offline-favorite', 'offline', 'ready', 'starting', 'unknown'])
    expect(orderProfiles([{ id: 'offline', state: 'Offline' }, { id: 'ready', state: 'Ready' }]).map(profile => profile.id))
      .toEqual(['ready', 'offline'])
  })

  it('hides revoked Hosts even when pinned and keeps connected Hosts first when removed Hosts are revealed', () => {
    const removed = savedHost('removed', 'Revoked')
    const offline = savedHost('offline', 'Disconnected/Unknown')
    const connected = savedHost('connected', 'Connected')
    const paused = savedHost('paused', 'Disabled')
    const source = [removed, offline, connected, paused]
    expect(orderHostConnections(source, false, ['removed', 'offline']).map(host => host.connectionId))
      .toEqual(['connected', 'paused', 'offline'])
    expect(orderHostConnections(source, true, ['removed', 'offline']).map(host => host.connectionId))
      .toEqual(['connected', 'paused', 'offline', 'removed'])
    expect(source.map(host => host.connectionId)).toEqual(['removed', 'offline', 'connected', 'paused'])
    expect(isRemovedHost(savedHost('typed', 'Disconnected/Unknown', 'Revoked'))).toBe(true)
    expect(isRemovedHost(savedHost('expired', 'Access expired', 'AccessExpired'))).toBe(false)
  })
})

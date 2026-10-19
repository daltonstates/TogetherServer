import { describe, expect, it } from 'vitest'
import type { PublicProfile, Run } from './contracts'
import { ContractError, parseSnapshot } from './contracts'
import hostSnapshotFixture from '../../contracts/host-snapshot.v1.json'
import { hasRepeatedPlayerCountFailure, isTransientPlayerObservation, playerCountPresentation, publicPlayerCount } from './playerCountPresentation'

const now = Date.parse('2026-10-19T12:00:05Z')
const run: Run = {
  profileId: 'server', runOperationId: '11111111-1111-4111-8111-111111111111', state: 'Ready', detail: 'Ready', processId: 123,
  onlinePlayers: 0, maxPlayers: 10, autoShutdownAtUtc: null, autoShutdownReason: null, hostAddedTime: false,
  playerNames: null, playerCountTrusted: true, friendAddedMinutes: 0, addedShutdownMinutes: 0,
  playerObservationSource: 'MinecraftLocalStatus', playerCountObservedUtc: '2026-10-19T12:00:00Z'
}
const profile: PublicProfile = {
  id: 'server', runOperationId: run.runOperationId, name: 'Synthetic', state: 'Ready', joinAddress: null, canStopNow: false,
  stopReason: null, kind: 'MinecraftJava', onlinePlayers: 0, maxPlayers: 10, autoShutdownAtUtc: null, autoShutdownReason: null,
  canStart: false, canStop: true, canRestartNow: false, restartReason: null, maintenanceEnabled: false, maintenanceMessage: null,
  canExtendTimer: false, timerExtensionMinutes: 0, timerExtensionRemainingMinutes: 0, canViewLogs: false
}

describe('completed player-count presentation evidence', () => {
  it('does not turn renders, refresh starts or one completed failure into repeated failure evidence', () => {
    for (let pass = 0; pass < 20; pass++) {
      expect(playerCountPresentation({ ...run, onlinePlayers: null, playerCountRefreshing: true }, now))
        .toMatchObject({ count: null, showWarning: false, checking: true, refreshing: true })
      expect(playerCountPresentation({ ...run, onlinePlayers: null, playerCountFailureCount: 1 }, now).showWarning).toBe(false)
    }
    expect(playerCountPresentation({ ...run, onlinePlayers: null, playerCountFailureCount: 2 }, now).showWarning).toBe(true)
    expect(playerCountPresentation({ ...run, onlinePlayers: null, playerCountFailureCount: 2, playerCountRefreshing: true }, now).showWarning).toBe(true)
    expect(playerCountPresentation({ ...run, playerCountFailureCount: 0 }, now)).toMatchObject({ count: 0, showWarning: false })
  })

  it('keeps a fresh count during refresh but never promotes an old or untrusted zero', () => {
    expect(playerCountPresentation({ ...run, playerCountRefreshing: true }, now).count).toBe(0)
    for (const change of [
      { playerCountObservedUtc: null }, { playerCountObservedUtc: '2026-10-19T12:01:00Z' },
      { playerCountTrusted: false }, { playerObservationSource: 'ObservationPending' }
    ]) expect(playerCountPresentation({ ...run, ...change }, now).count).toBeNull()
    expect(playerCountPresentation(run, now + 6000)).toMatchObject({ count: null, state: 'Stale', showWarning: false })
  })

  it('distinguishes a transient observation from unresolved process identity and repeated failure', () => {
    expect(isTransientPlayerObservation({ ...run, state: 'Unknown', playerObservationSource: 'ObservationStale' })).toBe(true)
    expect(isTransientPlayerObservation({ ...run, state: 'Unknown', playerObservationSource: null })).toBe(false)
    expect(isTransientPlayerObservation({ ...run, state: 'Unknown', playerObservationSource: 'ObservationStale', playerCountFailureCount: 2 })).toBe(false)
    expect(hasRepeatedPlayerCountFailure({})).toBe(false)
  })

  it('preserves legacy peer compatibility and checks explicit new peer evidence independently', () => {
    expect(publicPlayerCount(profile, now)).toBe(0)
    const current = { ...profile, playerCountTrusted: true, playerCountObservedUtc: run.playerCountObservedUtc }
    expect(publicPlayerCount(current, now)).toBe(0)
    expect(publicPlayerCount({ ...current, playerCountRefreshing: true }, now)).toBe(0)
    expect(publicPlayerCount({ ...current, playerCountTrusted: false }, now)).toBeNull()
    expect(publicPlayerCount(current, now + 6000)).toBeNull()
  })

  it('strictly decodes optional refresh metadata without inventing legacy failure history', () => {
    const decodeHost = (metadata: Record<string, unknown>) => {
      const snapshot = parseSnapshot({ ...hostSnapshotFixture, runs: [{ ...hostSnapshotFixture.runs[0], ...metadata }] })
      if (snapshot.mode !== 'Host') throw new Error('Expected Host fixture')
      return snapshot.runs[0]
    }
    const decodeFriend = (metadata: Record<string, unknown>) => {
      const snapshot = parseSnapshot({ mode: 'Friend', state: 'Connected', detail: '', endpoint: '', lastConnectedUtc: null,
        remoteControlsEnabled: false, canStart: false, canStop: false, profiles: [{ ...profile, ...metadata }],
        connectionId: '22222222-2222-4222-8222-222222222222', connections: [] })
      if (snapshot.mode !== 'Friend') throw new Error('Expected Friend fixture')
      return snapshot.profiles[0]
    }
    for (const decode of [decodeHost, decodeFriend]) {
      expect(decode({})).toMatchObject({ playerCountRefreshing: false, playerCountFailureCount: 0 })
      expect(decode({ playerCountRefreshing: true, playerCountFailureCount: 2 }))
        .toMatchObject({ playerCountRefreshing: true, playerCountFailureCount: 2 })
      for (const playerCountFailureCount of [null, -1, 1.5, 2_147_483_648, NaN, '2', true])
        expect(() => decode({ playerCountFailureCount })).toThrow(ContractError)
      expect(() => decode({ playerCountRefreshing: 'true' })).toThrow(ContractError)
    }
    expect(decodeFriend({})).toMatchObject({ playerCountTrusted: null, playerObservationSource: null, playerCountObservedUtc: null })
    expect(() => decodeFriend({ playerCountTrusted: 'true' })).toThrow(ContractError)
  })
})

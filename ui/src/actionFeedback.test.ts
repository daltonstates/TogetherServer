import { describe, expect, it } from 'vitest'
import hostWire from '../../contracts/host-snapshot.v1.json'
import { parseSnapshot, type HostSnapshot, type PublicProfile, type RemoteOperation } from './contracts'
import { currentActionNotice, currentRemoteOperation, serverActionNotice, successFeedbackMs } from './actionFeedback'

const nowMs = Date.parse('2026-10-09T23:00:00Z')
function host(): HostSnapshot {
  const snapshot = parseSnapshot(structuredClone(hostWire)) as HostSnapshot
  snapshot.runs[0].state = 'Starting'
  return snapshot
}
function feedback(snapshot: HostSnapshot, action = 'start', good = true) {
  return { ...serverActionNotice(good, 'Process launched. Waiting for readiness.', snapshot, snapshot.runs[0].profileId, action), receivedAtMs: nowMs }
}

describe('action receipts and current server state', () => {
  it.each(['start', 'restart', 'safe-restart', 'resume-hosting'])('retires %s progress as soon as readiness supersedes it, including a late receipt', action => {
    const snapshot = host()
    const notice = feedback(snapshot, action)
    expect(currentActionNotice(notice, snapshot, nowMs)).toBe(notice)
    snapshot.runs[0].state = 'Ready'
    expect(currentActionNotice(notice, snapshot, nowMs + 100)).toBeNull()
    expect(currentActionNotice(feedback(snapshot, action), snapshot, nowMs)).toBeNull()
  })

  it.each(['Offline', 'Unknown', 'Failed', 'Stopping'])('never keeps old startup success after the canonical phase becomes %s', state => {
    const snapshot = host()
    const notice = feedback(snapshot)
    snapshot.runs[0].state = state
    expect(currentActionNotice(notice, snapshot, nowMs + 100)).toBeNull()
  })

  it('retires count and timer receipts when their observation or run changes', () => {
    const snapshot = host()
    snapshot.runs[0].state = 'Ready'
    snapshot.runs[0].onlinePlayers = 0
    const refresh = feedback(snapshot, 'refresh')
    snapshot.runs[0].onlinePlayers = 1
    expect(currentActionNotice(refresh, snapshot, nowMs + 100)).toBeNull()
    const timer = feedback(snapshot, 'extend')
    snapshot.runs[0].autoShutdownAtUtc = '2026-10-09T23:15:00Z'
    expect(currentActionNotice(timer, snapshot, nowMs + 100)).toBeNull()
    const health = feedback(snapshot, 'health')
    snapshot.runs[0].runOperationId = 'new-run'
    expect(currentActionNotice(health, snapshot, nowMs + 100)).toBeNull()
    const observed = feedback(snapshot, 'refresh')
    snapshot.runs[0].playerCountTrusted = !snapshot.runs[0].playerCountTrusted
    expect(currentActionNotice(observed, snapshot, nowMs + 100)).toBeNull()
  })

  it('expires successful receipts without reviving them on another view, but preserves failures', () => {
    const snapshot = host()
    const notice = feedback(snapshot)
    expect(currentActionNotice(notice, snapshot, nowMs + successFeedbackMs - 1)).toBe(notice)
    expect(currentActionNotice(notice, snapshot, nowMs + successFeedbackMs)).toBeNull()
    expect(currentActionNotice(notice, snapshot, nowMs - 1)).toBeNull()
    const failure = feedback(snapshot, 'start', false)
    snapshot.runs[0].state = 'Ready'
    expect(currentActionNotice(failure, snapshot, nowMs + 60_000)).toBe(failure)
  })
})

describe('remote operation results', () => {
  const operation: RemoteOperation = { id: 'operation', action: 'start', state: 'Succeeded', ok: true,
    code: 'Started', message: 'Waiting for readiness.', requestedUtc: new Date(nowMs - 1000).toISOString(),
    completedUtc: new Date(nowMs).toISOString() }
  const profile = { state: 'Starting', operation } as PublicProfile

  it.each(['start', 'restart', 'replace'])('retires a successful remote %s when the server is Ready', action => {
    expect(currentRemoteOperation({ ...profile, operation: { ...operation, action } }, nowMs)).toBe(true)
    expect(currentRemoteOperation({ ...profile, state: 'Ready', operation: { ...operation, action } }, nowMs)).toBe(false)
  })

  it('retires an old Stop when the server starts again', () => {
    const stopped = { ...profile, state: 'Offline', operation: { ...operation, action: 'stop' } }
    expect(currentRemoteOperation(stopped, nowMs)).toBe(true)
    expect(currentRemoteOperation({ ...stopped, state: 'Ready' }, nowMs)).toBe(false)
  })

  it('expires dated successes, refuses unknown timing and retains pending work and failures', () => {
    expect(currentRemoteOperation(profile, nowMs + successFeedbackMs)).toBe(false)
    expect(currentRemoteOperation({ ...profile, operation: { ...operation, completedUtc: null } }, nowMs)).toBe(false)
    for (const state of ['Pending', 'Running', 'Failed', 'Interrupted'] as const)
      expect(currentRemoteOperation({ ...profile, state: 'Ready', operation: { ...operation, state } }, nowMs + 60_000)).toBe(true)
  })
})

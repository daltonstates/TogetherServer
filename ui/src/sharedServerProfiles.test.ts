import { describe, expect, it } from 'vitest'
import fixture from '../../contracts/host-snapshot.v1.json'
import { parseSnapshot, type HostSnapshot } from './contracts'
import { originalLocalServer, startedSharedServer } from './sharedServerProfiles'

const rootId = fixture.settings.profiles[0].id
const oldId = '22222222-2222-4222-8222-222222222222'
const newId = '33333333-3333-4333-8333-333333333333'
function host(): HostSnapshot {
  const snapshot = parseSnapshot(structuredClone(fixture))
  if (snapshot.mode !== 'Host') throw new Error('Expected the Host fixture.')
  const original = snapshot.settings.profiles[0]
  snapshot.settings.profiles = [original, { ...original, id: oldId, separateCopySourceProfileId: rootId },
    { ...original, id: newId, separateCopySourceProfileId: rootId }]
  snapshot.runs = snapshot.settings.profiles.map(profile => ({ ...snapshot.runs[0], profileId: profile.id,
    state: 'Offline', processId: null, runOperationId: null }))
  return snapshot
}

describe('exact local shared-server relationships', () => {
  it('recognizes the local original regardless of profile order', () => {
    const [root, old, newest] = host().settings.profiles
    expect(originalLocalServer(newest, [newest, root, old])).toBe(root)
    expect(originalLocalServer(root, [newest, root, old])).toBe(root)
  })

  it('does not infer ownership from a missing source, another retained copy, or a cycle', () => {
    const [root, old, newest] = host().settings.profiles
    expect(originalLocalServer(old, [old, newest])).toBeNull()
    expect(originalLocalServer({ ...newest, separateCopySourceProfileId: old.id }, [root, old, newest])).toBeNull()
    expect(originalLocalServer({ ...old, separateCopySourceProfileId: old.id }, [old, newest])).toBeNull()
  })
})

describe('selection after normal Start', () => {
  it('uses the original root when Start came from an older child, keeping a retained Unknown copy distinct', () => {
    const previous = host()
    previous.runs[1].state = 'Unknown'
    const next = structuredClone(previous)
    next.runs[2] = { ...next.runs[2], state: 'Starting', runOperationId: 'new-run' }
    expect(startedSharedServer(next, previous, oldId)?.id).toBe(newId)
  })

  it('selects the newly started run when another related run was already active', () => {
    const previous = host()
    previous.runs[1] = { ...previous.runs[1], state: 'Ready', processId: 101, runOperationId: 'existing-run' }
    const next = structuredClone(previous)
    next.runs[2] = { ...next.runs[2], state: 'Starting', processId: 202, runOperationId: 'new-run' }
    expect(startedSharedServer(next, previous, oldId)?.id).toBe(newId)
  })

  it('recognizes a new run waiting for its first readiness observation', () => {
    const previous = host()
    const next = structuredClone(previous)
    next.runs[2] = { ...next.runs[2], state: 'Unknown', playerObservationSource: 'ObservationPending', runOperationId: 'new-run' }
    expect(startedSharedServer(next, previous, rootId)?.id).toBe(newId)
  })

  it('does not guess between multiple newly active runs or cross into an unrelated server', () => {
    const previous = host()
    const next = structuredClone(previous)
    next.runs[1].state = next.runs[2].state = 'Starting'
    expect(startedSharedServer(next, previous, rootId)).toBeUndefined()
    next.settings.profiles[2].separateCopySourceProfileId = '44444444-4444-4444-8444-444444444444'
    next.runs[1].state = 'Offline'
    expect(startedSharedServer(next, previous, rootId)).toBeUndefined()
  })
})

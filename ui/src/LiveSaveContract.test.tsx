import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { LiveSaveControls, parseLiveSaveStatus } from './LiveSaveControls'

const ids = ['completion', 'snapshot', 'transfer', 'load', 'change', 'restart']
const sources = ['ObservedExactRun', 'VerifiedImmutableSnapshot', 'SignedSeparateDeviceReceipt', 'OwnerReported', 'OwnerReported', 'OwnerReported']
const ready = (game = 'Valheim') => ({ available: true, message: 'Accepted installation ready to capture.', game, code: 'LiveSaveReady',
  stages: ids.map((id, index) => ({ id, state: 'Passed', detail: `Evidence for ${id}.`, source: sources[index], observedUtc: '2026-10-17T10:00:00.1234567+00:00' })) })
describe('Current installed live-save contract', () => {
  it.each(['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'])('accepts %s only with all six exact observed/reported stages', game => {
    const result = parseLiveSaveStatus(ready(game))
    expect(result.available).toBe(true)
    expect(result.stages.every(stage => stage.state === 'Passed')).toBe(true)
    expect(() => parseLiveSaveStatus({ ...ready(game), code: 'StagingFixtureReady' })).toThrow()
    expect(() => parseLiveSaveStatus({ ...ready(game), stages: ready(game).stages.map((stage, index) => index === 5 ? { ...stage, state: 'Unverified' } : stage) })).toThrow()
  })
  it.each(['Fixture', 'Custom', 'Unknown'])('refuses %s with a claimed real-game acceptance', game => {
    expect(() => parseLiveSaveStatus(ready(game))).toThrow()
  })
  it.each([
    { source: 'OwnerReported', observedUtc: '2026-10-17T10:00:00Z' },
    { source: 'ObservedExactRun', observedUtc: undefined },
    { source: 'ObservedExactRun', observedUtc: 'not-a-date' },
    { source: undefined, observedUtc: '2026-10-17T10:00:00Z' }
  ])('rejects a first-stage pass with mismatched or missing evidence %j', changed => {
    const input = ready()
    expect(() => parseLiveSaveStatus({ ...input, stages: input.stages.map((stage, index) => index === 0 ? { ...stage, ...changed } : stage) })).toThrow()
  })
  it('shows a real ready installation and treats unchanged publication as terminal', () => {
    const lastAttempt = { requestId: '11111111-1111-4111-8111-111111111111', state: 'Unchanged', code: 'LiveSaveUnchanged',
      message: 'The current completed copy already contains these bytes.', versionHash: 'A'.repeat(64), versionNumber: 4 }
    const status = parseLiveSaveStatus({ ...ready(), lastAttempt })
    render(<LiveSaveControls profileId="world-a" status={status} onUpdated={vi.fn()} />)
    expect(screen.getByRole('button', { name: 'Save and share now' })).toBeEnabled()
    expect(screen.queryByText('Development test server')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancel this attempt' })).not.toBeInTheDocument()
    expect(() => parseLiveSaveStatus({ ...ready(), lastAttempt: { ...lastAttempt, versionHash: null } })).toThrow()
    expect(() => parseLiveSaveStatus({ ...ready(), resumePending: true })).toThrow()
  })
  it('keeps accepted evidence visible while the current run blocks capture', () => {
    const status = parseLiveSaveStatus({ ...ready(), available: false, code: 'LiveSaveBlocked' })
    expect(status.available).toBe(false)
    expect(status.stages).toHaveLength(6)
  })
})

import { describe, expect, it } from 'vitest'
import { createSharedRouteDetails, formatSharedBytes, parseSharedRouteDetails, plannedHandoffChecklist,
  sharedSaveAge, sharedTransferPhase } from './sharedWorldUx'

const profileId = '11111111-1111-4111-8111-111111111111'
const details = { recordHash: 'a'.repeat(64), tlsFingerprint: 'b'.repeat(64), endpoint: 'https://192.0.2.10:5131' }

describe('reviewed combined route details', () => {
  it('round-trips one bounded world identity and normalizes hashes without adding authority inputs', () => {
    expect(parseSharedRouteDetails(createSharedRouteDetails(profileId, details), profileId)).toEqual({
      ...details, recordHash: 'A'.repeat(64), tlsFingerprint: 'B'.repeat(64) })
  })
  it('accepts the previous two-line copy format without inventing an endpoint', () => {
    expect(parseSharedRouteDetails(`${details.recordHash}\r\n${details.tlsFingerprint}`, profileId)).toEqual({
      recordHash: 'A'.repeat(64), tlsFingerprint: 'B'.repeat(64), endpoint: null })
  })
  it.each([
    { profileId: '22222222-2222-4222-8222-222222222222' },
    { endpoint: 'http://192.0.2.10:5131' },
    { endpoint: 'https://user:password@192.0.2.10:5131' },
    { endpoint: 'https://192.0.2.10:5131/command' },
    { endpoint: 'https://example.com:5131' },
    { endpoint: 'https://127.0.0.1:5131' },
    { endpoint: 'https://192.0.2.10:5131?command=stop' },
    { recordHash: 'wrong' },
    { tlsFingerprint: ['b'.repeat(64)] },
    { schema: 2 },
    { executablePath: 'C:\\game.exe' },
  ])('rejects wrong identity, arbitrary routes and unexpected inputs: %j', changes => {
    const source = JSON.parse(createSharedRouteDetails(profileId, details)) as Record<string, unknown>
    expect(() => parseSharedRouteDetails(JSON.stringify({ ...source, ...changes }), profileId)).toThrow()
  })
  it('refuses extra legacy lines and oversized input before a proof can be reviewed', () => {
    expect(() => parseSharedRouteDetails(`${details.recordHash}\n${details.tlsFingerprint}\n${details.endpoint}\nrun`, profileId)).toThrow()
    expect(() => parseSharedRouteDetails('A'.repeat(4097), profileId)).toThrow()
  })
})

describe('canonical handoff and transfer evidence', () => {
  it('resumes at the successor receipt and then at owner completion, with no Start step completed', () => {
    const waiting = { pending: true, code: 'WaitingForSuccessorCopy', finalVersion: 4, receiptConfirmed: false, canComplete: false }
    const received = { ...waiting, code: 'ReadyToComplete', receiptConfirmed: true, canComplete: true }
    expect(plannedHandoffChecklist(waiting, 'Next PC').find(step => step.current)?.id).toBe('receive')
    const resumed = plannedHandoffChecklist(received, 'Next PC')
    expect(resumed.find(step => step.current)?.id).toBe('complete')
    expect(resumed.find(step => step.id === 'setup')?.done).toBe(false)
  })
  it('puts changed access or an unverifiable pending copy ahead of the ordinary checklist', () => {
    for (const code of ['HandoffReviewRequired', 'SuccessorAccessChanged']) {
      const steps = plannedHandoffChecklist({ pending: true, code, finalVersion: 4,
        receiptConfirmed: true, canComplete: false }, 'Next PC')
      expect(steps.filter(step => step.current).map(step => step.id)).toEqual(['review'])
      expect(steps.find(step => step.id === 'complete')?.done).toBe(false)
    }
  })
  it('does not infer verification or receipt from a received-byte total', () => {
    expect(sharedTransferPhase('Receiving')).toBe('Receiving')
    expect(sharedTransferPhase('Ready')).toBeNull()
    expect(sharedTransferPhase('Receiving', 'Verifying')).toBe('Verifying')
    expect(sharedTransferPhase('Receiving', 'Receipt')).toBe('Receipt')
    expect(formatSharedBytes(2 * 1024 * 1024)).toBe('2 MiB')
  })
  it('keeps unavailable and future timestamps distinct from a completed save age', () => {
    const now = Date.parse('2026-10-08T12:00:00Z')
    expect(sharedSaveAge(null, now)).toBe('Unavailable')
    expect(sharedSaveAge('bad', now)).toBe('Unavailable')
    expect(sharedSaveAge('2026-10-08T11:00:00Z', now)).toBe('1 hr ago')
    expect(sharedSaveAge('2026-10-09T12:00:00Z', now)).toBe('Time is ahead of this PC')
  })
})

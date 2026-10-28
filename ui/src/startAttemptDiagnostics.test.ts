import { expect, it } from 'vitest'
import { parseRecentServerSessions } from './contracts'

const profileId = '11111111-1111-4111-8111-111111111111'
const attempt = { id: '22222222-2222-4222-8222-222222222222', profileId,
  operationId: '33333333-3333-4333-8333-333333333333', targetDeviceId: null,
  operation: 'RemoteStart', phase: 'Freshness', code: 'NewerSharedSaveAvailable',
  message: 'A newer save is available; no transfer has started.', requestedUtc: '2026-10-28T12:00:00Z',
  completedUtc: '2026-10-28T12:00:01Z', relatedJobId: null, kind: 'BlockedStart' }
const result = { ok: true, code: 'RecentSessions', message: 'Attempt recorded.', profileId, sessions: [], attempts: [attempt] }

it('decodes prelaunch refusal evidence without adding game session fields and supports older responses', () => {
  expect(parseRecentServerSessions(result).attempts).toEqual([attempt])
  expect(parseRecentServerSessions({ ...result, attempts: undefined }).attempts).toEqual([])
  expect(parseRecentServerSessions({ ...result, attempts: [{ ...attempt, durationSeconds: 999, world: 'must not project' }] }).attempts?.[0])
    .not.toHaveProperty('durationSeconds')
})

it.each([{ id: 'invalid' }, { profileId: '44444444-4444-4444-8444-444444444444' }, { targetDeviceId: 'invalid' },
  { operationId: 'invalid' }, { relatedJobId: '../path' }, { kind: 'Ready' }, { phase: 'x'.repeat(65) },
  { message: 'x'.repeat(401) }, { requestedUtc: null }, { completedUtc: '2026-10-28T11:59:59Z' }])(
  'rejects inconsistent attempt diagnostics %j', patch => {
    expect(() => parseRecentServerSessions({ ...result, attempts: [{ ...attempt, ...patch }] })).toThrow()
  })

it('rejects duplicate and unbounded attempts', () => {
  expect(() => parseRecentServerSessions({ ...result, attempts: [attempt, attempt] })).toThrow()
  expect(() => parseRecentServerSessions({ ...result, attempts: Array.from({ length: 21 }, () => attempt) })).toThrow()
})

import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LiveSaveControls, parseLiveSaveStatus } from './LiveSaveControls'
const status = (available = false) => ({ available, message: 'Game-specific completion and load checks remain unverified.',
  game: available ? 'Fixture' : 'MinecraftJava', code: available ? 'StagingFixtureReady' : 'GameAcceptanceRequired',
  stages: ['completion', 'snapshot', 'transfer', 'load', 'change', 'restart'].map(id => ({ id, state: 'Unverified', detail: 'Owner-controlled game check required.' })) })
const publishedAttempt = { requestId: '01234567-89ab-cdef-0123-456789abcdef', state: 'Published', code: 'LiveSavePublished',
  message: 'Synthetic copy published.', versionHash: 'a'.repeat(64), versionNumber: 1 }
afterEach(() => vi.unstubAllGlobals())
describe('live-save attempt decoder', () => {
  it.each([
    { versionHash: 'a'.repeat(64), versionNumber: 1 },
    { versionHash: 'A'.repeat(64), versionNumber: Number.MAX_SAFE_INTEGER },
  ])('accepts valid Published metadata with version $versionNumber', fields => {
    const attempt = { ...publishedAttempt, ...fields }
    expect(parseLiveSaveStatus({ ...status(true), lastAttempt: attempt }).lastAttempt).toEqual(attempt)
  })
  it.each(['Requested', 'Capturing', 'Publishing', 'Failed', 'Canceled', 'Withdrawn'])('accepts %s without published metadata', state => {
    const attempt = { ...publishedAttempt, state, versionHash: null, versionNumber: null }
    expect(parseLiveSaveStatus({ ...status(), lastAttempt: attempt }).lastAttempt).toEqual(attempt)
  })
  it.each([
    { name: 'array', state: ['Published'] },
    { name: 'other enum array', state: ['Withdrawn'] },
    { name: 'object', state: { state: 'Published' } },
    { name: 'null', state: null },
    { name: 'missing', state: undefined },
    { name: 'number', state: 1 },
    { name: 'unknown string', state: 'Complete' },
    { name: 'wrong case', state: 'published' },
    { name: 'trailing text', state: 'Published ' },
  ])('rejects $name state', ({ state }) => {
    expect(() => parseLiveSaveStatus({ ...status(), lastAttempt: { ...publishedAttempt, state } })).toThrow()
  })
  it.each([
    { name: 'missing fields', fields: { versionHash: undefined, versionNumber: undefined } },
    { name: 'missing hash', fields: { versionHash: undefined } },
    { name: 'null hash', fields: { versionHash: null } },
    { name: 'missing number', fields: { versionNumber: undefined } },
    { name: 'null number', fields: { versionNumber: null } },
    { name: 'short hash', fields: { versionHash: 'a'.repeat(63) } },
    { name: 'long hash', fields: { versionHash: 'a'.repeat(65) } },
    { name: 'nonhex hash', fields: { versionHash: 'g'.repeat(64) } },
    { name: 'array hash', fields: { versionHash: [publishedAttempt.versionHash] } },
    { name: 'object hash', fields: { versionHash: { value: publishedAttempt.versionHash } } },
    { name: 'string number', fields: { versionNumber: '1' } },
    { name: 'zero number', fields: { versionNumber: 0 } },
    { name: 'negative number', fields: { versionNumber: -1 } },
    { name: 'fractional number', fields: { versionNumber: 1.5 } },
    { name: 'unsafe number', fields: { versionNumber: Number.MAX_SAFE_INTEGER + 1 } },
    { name: 'nonfinite number', fields: { versionNumber: Infinity } },
    { name: 'array number', fields: { versionNumber: [1] } },
    { name: 'object number', fields: { versionNumber: { value: 1 } } },
  ])('rejects Published with $name', ({ fields }) => {
    expect(() => parseLiveSaveStatus({ ...status(), lastAttempt: { ...publishedAttempt, ...fields } })).toThrow()
  })
  it.each(['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'])('keeps %s live capture disabled', game => {
    expect(parseLiveSaveStatus({ ...status(), game }).available).toBe(false)
    expect(() => parseLiveSaveStatus({ ...status(true), game })).toThrow()
  })
})
describe('live capture gates', () => {
  it('rejects a real game enable, a partial matrix and an invented acceptance pass', () => {
    expect(() => parseLiveSaveStatus({ ...status(true), game: 'MinecraftJava' })).toThrow()
    expect(() => parseLiveSaveStatus({ ...status(true), stages: [] })).toThrow()
    const input = status(); input.stages[2].state = 'Passed'
    expect(() => parseLiveSaveStatus(input)).toThrow()
  })
  it('shows a disabled real-game action with concrete independent checks', () => {
    render(<LiveSaveControls profileId="test" status={parseLiveSaveStatus(status())} onUpdated={vi.fn()} />)
    expect(screen.getByRole('button', { name: 'Save and share now' })).toBeDisabled()
    expect(screen.getByText(/Other-PC transfer and receipt:/)).toBeInTheDocument()
    expect(screen.getByText(/Owner load confirmations do not enable/)).toBeInTheDocument()
  })
  it('uses a fixed scoped request and retains its identity after a lost reply', async () => {
    const fetch = vi.fn().mockRejectedValueOnce(new Error('lost reply')).mockResolvedValueOnce(new Response(JSON.stringify({ ok: true, code: 'LiveSavePublished', message: 'Synthetic copy published.', attempt: null })))
    vi.stubGlobal('fetch', fetch)
    const updated = vi.fn().mockResolvedValue(undefined)
    render(<LiveSaveControls profileId="test" status={parseLiveSaveStatus(status(true))} onUpdated={updated} />)
    fireEvent.click(screen.getByRole('button', { name: 'Save and share now' }))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('LocalAppUnavailable'))
    fireEvent.click(screen.getByRole('button', { name: 'Save and share now' }))
    await waitFor(() => expect(updated).toHaveBeenCalledTimes(1))
    expect(fetch.mock.calls[0][0]).toBe('/api/local/profiles/test/shared-world/live/save')
    expect(fetch.mock.calls[0][1].body).toBe(fetch.mock.calls[1][1].body)
    expect(Object.keys(JSON.parse(fetch.mock.calls[0][1].body))).toEqual(['requestId'])
  })
})

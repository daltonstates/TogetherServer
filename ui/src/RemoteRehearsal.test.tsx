import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { HostRemoteRehearsal, parseRehearsalReport } from './RemoteRehearsal'

const report = () => ({ schema: 1, generatedUtc: '2026-10-04T12:00:00Z', networkContext: 'Loopback',
  stages: ['listener', 'outsideTcp', 'connection', 'chat', 'transfer', 'gameEndpoint', 'humanJoinLoad']
    .map(id => ({ id, state: ['listener', 'connection', 'chat', 'transfer'].includes(id) ? 'Passed' : 'Unverified', detail: 'Fixed redacted result.' })) })

afterEach(() => vi.unstubAllGlobals())
describe('remote rehearsal evidence', () => {
  it('keeps loopback, outside TCP and human evidence independent', () => {
    const parsed = parseRehearsalReport(report())
    expect(parsed.networkContext).toBe('Loopback')
    expect(parsed.stages[1].state).toBe('Unverified')
    expect(parsed.stages[6].state).toBe('Unverified')
  })
  it.each([1, 6])('rejects an invented external pass at stage %s', index => {
    const value = report(); value.stages[index].state = 'Passed'
    expect(() => parseRehearsalReport(value)).toThrow()
  })
  it.each([0, 1, 6])('rejects array-valued evidence states at stage %s', index => {
    const value = report()
    const stages = value.stages.map((stage, stageIndex) => stageIndex === index ? { ...stage, state: ['Passed'] } : stage)
    expect(() => parseRehearsalReport({ ...value, stages })).toThrow()
  })
  it('rejects an array-valued network context', () => {
    expect(() => parseRehearsalReport({ ...report(), networkContext: ['Loopback'] })).toThrow()
  })
  it('rejects partial, reordered and unbounded stage data', () => {
    const value = report(); value.stages.reverse()
    expect(() => parseRehearsalReport(value)).toThrow()
    expect(() => parseRehearsalReport({ ...report(), stages: [] })).toThrow()
    value.stages[0].detail = 'x'.repeat(501)
    expect(() => parseRehearsalReport(value)).toThrow()
  })
  it('prepares fixed staging data and leaves pairing and Receive visible', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: true, message: 'Pair the test PC and grant Receive.' })))
    vi.stubGlobal('fetch', fetch)
    render(<HostRemoteRehearsal />)
    fireEvent.click(screen.getByRole('button', { name: 'Prepare rehearsal' }))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('grant Receive'))
    expect(fetch).toHaveBeenCalledWith('/api/local/rehearsal/prepare', expect.objectContaining({ method: 'POST', body: undefined }))
  })
})

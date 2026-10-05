import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LiveSaveControls, parseLiveSaveStatus } from './LiveSaveControls'
const status = (available = false) => ({ available, message: 'Game-specific completion and load checks remain unverified.',
  game: available ? 'Fixture' : 'MinecraftJava', code: available ? 'StagingFixtureReady' : 'GameAcceptanceRequired',
  stages: ['completion', 'snapshot', 'transfer', 'load', 'change', 'restart'].map(id => ({ id, state: 'Unverified', detail: 'Owner-controlled game check required.' })) })
afterEach(() => vi.unstubAllGlobals())
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

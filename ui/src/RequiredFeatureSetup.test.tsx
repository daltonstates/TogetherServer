import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { RequiredFeatureSetup, parseRequiredSetup, parseRequiredSetupResult, useRequiredFeatureSetup, type RequiredFeatureRegistry } from './RequiredFeatureSetup'
import { ContractError } from './contracts'

const profile = { id: 'profile-name', revision: 1, title: 'Your name', description: 'Share a recognizable name.', completed: false }
const acknowledgement = { id: 'new-feature', revision: 2, title: 'New choice', description: 'Review this new feature.', completed: false }
const registry = { 'profile-name': { revision: 1 }, 'new-feature': { revision: 2 } }
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
function Harness({ versions = registry, unknown = false }: { versions?: RequiredFeatureRegistry; unknown?: boolean }) {
  const setup = useRequiredFeatureSetup(versions)
  return setup.ready ? <p>App workspace</p> : <RequiredFeatureSetup setup={setup}
    renderStep={(feature, complete, busy) => unknown ? null : <button disabled={busy} onClick={() => void complete()}>Complete {feature.title}</button>} />
}
afterEach(() => vi.unstubAllGlobals())

describe('required feature setup', () => {
  it('sequences independent revisions, skips completed steps and rechecks the backend before revealing the app', async () => {
    let finishCheck!: (response: Response) => void
    const fetch = vi.fn().mockResolvedValueOnce(response({ features: [profile, acknowledgement] }))
      .mockResolvedValueOnce(response({ ok: true, code: 'Completed', message: 'Done', features: [{ ...profile, completed: true }, acknowledgement] }))
      .mockResolvedValueOnce(response({ features: [{ ...profile, completed: true }, acknowledgement] }))
      .mockResolvedValueOnce(response({ ok: true, code: 'Completed', message: 'Done', features: [{ ...profile, completed: true }, { ...acknowledgement, completed: true }] }))
      .mockImplementationOnce(() => new Promise<Response>(resolve => { finishCheck = resolve }))
    vi.stubGlobal('fetch', fetch)
    render(<Harness />)
    expect(screen.getByText('Checking required setup…')).toBeInTheDocument()
    expect(screen.queryByText('App workspace')).not.toBeInTheDocument()
    fireEvent.click(await screen.findByText('Complete Your name'))
    await screen.findByText('Complete New choice')
    expect(screen.getByText('1 of 2 steps complete')).toBeInTheDocument()
    expect(screen.queryByText('Complete Your name')).not.toBeInTheDocument()
    expect(fetch.mock.calls[0][1].headers).toEqual({ 'X-TogetherServer-Local': '1' })
    expect(fetch.mock.calls[1][0]).toBe('/api/local/required-setup/profile-name/complete')
    expect(fetch.mock.calls[1][1]).toMatchObject({ method: 'POST', body: '{"revision":1}', headers: { 'X-TogetherServer-Local': '1' } })
    fireEvent.click(screen.getByText('Complete New choice'))
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(5))
    expect(fetch.mock.calls[3][1].body).toBe('{"revision":2}')
    expect(screen.queryByText('App workspace')).not.toBeInTheDocument()
    finishCheck(response({ features: [{ ...profile, completed: true }, { ...acknowledgement, completed: true }] }))
    await screen.findByText('App workspace')
  })

  it('keeps setup on load failure, rejects a denied completion and resumes from backend state on retry', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(response({ message: 'Unavailable' }, 503))
      .mockResolvedValueOnce(response({ features: [{ ...profile, completed: true }, acknowledgement] }))
      .mockResolvedValueOnce(response({ ok: false, code: 'NotReady', message: 'Review the choice first.', features: [{ ...profile, completed: true }, acknowledgement] }))
      .mockResolvedValueOnce(response({ features: [{ ...profile, completed: true }, { ...acknowledgement, completed: true }] }))
    vi.stubGlobal('fetch', fetch)
    render(<Harness />)
    fireEvent.click(await screen.findByText('Retry setup'))
    fireEvent.click(await screen.findByText('Complete New choice'))
    await screen.findByText('Review the choice first.')
    expect(screen.queryByText('App workspace')).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Retry setup'))
    await screen.findByText('App workspace')
  })

  it.each([
    { features: [] },
    { features: [{ ...profile, revision: 2 }, acknowledgement] },
    { features: [profile, acknowledgement, { ...profile, id: 'unknown-feature', completed: true }] },
    { features: [profile, { ...acknowledgement, completed: 'yes' }] }
  ])('fails closed for missing, unknown or invalid versioned requirements: %j', async value => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response(value)))
    render(<Harness />)
    await screen.findByRole('alert')
    expect(screen.getByText('Retry setup')).toBeInTheDocument()
    expect(screen.queryByText('App workspace')).not.toBeInTheDocument()
  })

  it('keeps an unknown renderer blocked even with a valid registered feature', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response({ features: [profile, acknowledgement] })))
    render(<Harness unknown />)
    await screen.findByRole('alert')
    expect(screen.queryByText('App workspace')).not.toBeInTheDocument()
  })

  it('does not trust a successful mutation when the authoritative recheck still says pending', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(response({ features: [profile] }))
      .mockResolvedValueOnce(response({ ok: true, code: 'Completed', message: 'Done', features: [{ ...profile, completed: true }] }))
      .mockResolvedValueOnce(response({ features: [profile] }))
    vi.stubGlobal('fetch', fetch)
    const versions = { 'profile-name': { revision: 1 } }
    render(<Harness versions={versions} />)
    fireEvent.click(await screen.findByText('Complete Your name'))
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(3))
    await waitFor(() => expect(screen.getByText('Complete Your name')).toBeEnabled())
    expect(screen.queryByText('App workspace')).not.toBeInTheDocument()
  })

  it('validates strict feature shape, duplicate IDs and completion results', () => {
    for (const features of [null, [profile, profile], [{ ...profile, revision: 0 }], [{ ...profile, revision: 1.5 }], [{ ...profile, revision: 10001 }],
      [{ ...profile, id: 'x'.repeat(65) }], [{ ...profile, title: '' }], [{ ...profile, title: 'x'.repeat(81) }],
      [{ ...profile, description: '' }], [{ ...profile, description: 'x'.repeat(321) }],
      Array.from({ length: 129 }, (_, index) => ({ ...profile, id: `feature-${index}` }))])
      expect(() => parseRequiredSetup({ features })).toThrow(ContractError)
    expect(parseRequiredSetup({ features: [{ ...profile, id: '1'.repeat(64), revision: 10000, title: 'x'.repeat(80), description: 'x'.repeat(320) }] }).features).toHaveLength(1)
    expect(parseRequiredSetup({ features: Array.from({ length: 128 }, (_, index) => ({ ...profile, id: `feature-${index}` })) }).features).toHaveLength(128)
    expect(() => parseRequiredSetupResult({ features: [profile] })).toThrow(ContractError)
  })
})

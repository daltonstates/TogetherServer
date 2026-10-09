import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { GameCompatibilityPanel } from './GameCompatibilityPanel'

const profileId = '11111111-1111-4111-8111-111111111111'
const requirements = { profileId, kind: 'Valheim', gameName: 'Valheim', requiredVersion: '0.219.16', versionSource: 'OwnerReported',
  addOnState: 'NotReviewed', addOns: [], checkedUtc: '2026-10-08T15:00:00Z', guidance: 'Use the required game version. Mod support is not reviewed.' }
const basic = { ok: true, code: 'RequirementsRead', message: 'Informational.', requirements }
const friend = { ...basic, clientVersion: '0.218.0', clientVersionSource: 'Manual', versionComparison: 'Mismatch', addOnComparison: 'Unknown' }
afterEach(() => vi.unstubAllGlobals())

describe('game requirements panel', () => {
  it('shows Host required game and a labelled owner fallback with a fixed protected request', async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(basic)))
      .mockResolvedValueOnce(new Response(JSON.stringify({ ...basic, requirements: { ...requirements, requiredVersion: '0.220.0' } })))
    vi.stubGlobal('fetch', fetcher)
    render(<GameCompatibilityPanel profileId={profileId} host />)
    expect(await screen.findByText(/Valheim · 0.219.16/)).toBeInTheDocument()
    expect(screen.getByText(/Required version:.*owner-reported/)).toBeInTheDocument()
    expect(fetcher.mock.calls[0][1]).toMatchObject({ headers: { 'X-TogetherServer-Local': '1' } })
    fireEvent.click(screen.getByText('State the required version'))
    fireEvent.change(screen.getByLabelText('Required game version (owner-reported)'), { target: { value: '0.220.0' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save version' }))
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2))
    expect(fetcher.mock.calls[1]).toEqual([`/api/local/profiles/${profileId}/requirements`, expect.objectContaining({ method: 'PUT', body: JSON.stringify({ version: '0.220.0' }) })])
    expect(await screen.findByText(/Valheim · 0.220.0/)).toBeInTheDocument()
  })
  it('shows mismatch and unknown add-ons without installing, starting or launching', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify(friend)))
    vi.stubGlobal('fetch', fetcher)
    const rendered = render(<GameCompatibilityPanel profileId={profileId} host={false} />)
    expect(await screen.findByText(/Version mismatch/)).toBeInTheDocument()
    expect(screen.getByText(/Installed add-on compatibility is Unknown/)).toBeInTheDocument()
    rendered.rerender(<GameCompatibilityPanel profileId={profileId} host={false} />)
    expect(fetcher).toHaveBeenCalledTimes(1)
    expect(fetcher.mock.calls.some(call => /open-game|\/start|install/.test(String(call[0])))).toBe(false)
  })
  it('refuses another profile and shows a retryable error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ ...basic, requirements: { ...requirements, profileId: '22222222-2222-4222-8222-222222222222' } }))))
    render(<GameCompatibilityPanel profileId={profileId} host />)
    expect(await screen.findByRole('alert')).toHaveTextContent('different saved server')
    expect(screen.getByRole('button', { name: 'Refresh requirements' })).toBeEnabled()
  })
})

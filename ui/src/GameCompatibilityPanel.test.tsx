import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { GameCompatibilityPanel } from './GameCompatibilityPanel'

const profileId = '11111111-1111-4111-8111-111111111111'
const nowMs = Date.parse('2026-10-08T15:00:10Z')
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
    render(<GameCompatibilityPanel profileId={profileId} host nowMs={nowMs} />)
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
    const rendered = render(<GameCompatibilityPanel profileId={profileId} host={false} nowMs={nowMs} />)
    expect(await screen.findByText(/Version mismatch/)).toBeInTheDocument()
    expect(screen.getByText(/Installed add-on compatibility is Unknown/)).toBeInTheDocument()
    rendered.rerender(<GameCompatibilityPanel profileId={profileId} host={false} nowMs={nowMs} />)
    expect(fetcher).toHaveBeenCalledTimes(1)
    expect(fetcher.mock.calls.some(call => /open-game|\/start|install/.test(String(call[0])))).toBe(false)
  })
  it('refuses another profile and shows a retryable error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ ...basic, requirements: { ...requirements, profileId: '22222222-2222-4222-8222-222222222222' } }))))
    render(<GameCompatibilityPanel profileId={profileId} host nowMs={nowMs} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('different saved server')
    expect(screen.getByRole('button', { name: 'Refresh requirements' })).toBeEnabled()
  })

  it('keeps version text Match separate from Unknown add-ons and hides the inventory initially', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ ...friend,
      clientVersion: requirements.requiredVersion, versionComparison: 'Match' }))))
    const view = render(<GameCompatibilityPanel profileId={profileId} host={false} nowMs={nowMs} />)
    expect(await screen.findByText(/^Version:/)).toHaveTextContent('Version: Match · Add-ons: Unknown')
    expect(screen.getByText(/^Current on this PC:/)).toHaveTextContent('(manual)')
    expect(screen.getByText(/^Required version:/)).toHaveTextContent('(owner-reported)')
    expect(view.container.querySelector('details')).not.toHaveAttribute('open')
  })

  it('downgrades old comparison results to Unknown while preserving the checked time', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ ...friend,
      clientVersion: requirements.requiredVersion, versionComparison: 'Match' })))
    vi.stubGlobal('fetch', fetcher)
    const view = render(<GameCompatibilityPanel profileId={profileId} host={false} nowMs={nowMs} />)
    expect(await screen.findByText(/^Version:/)).toHaveTextContent('Version: Match')
    view.rerender(<GameCompatibilityPanel profileId={profileId} host={false} nowMs={nowMs + 300_001} />)
    expect(screen.getByText(/^Version:/)).toHaveTextContent('Version: Unknown · Add-ons: Unknown')
    expect(screen.getByText(/Stale requirements/)).toBeInTheDocument()
    expect(view.container.querySelector('time')).toHaveAttribute('dateTime', requirements.checkedUtc)
    expect(fetcher).toHaveBeenCalledOnce()
  })

  it('aborts a pending Save and never applies it or its manual draft to another profile', async () => {
    const otherId = '22222222-2222-4222-8222-222222222222'
    let finishSave: ((response: Response) => void) | undefined
    const fetcher = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(basic)))
      .mockImplementationOnce(() => new Promise<Response>(resolve => { finishSave = resolve }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ ...basic,
        requirements: { ...requirements, profileId: otherId, gameName: 'Other game', requiredVersion: '1.2.3' } })))
    vi.stubGlobal('fetch', fetcher)
    const view = render(<GameCompatibilityPanel profileId={profileId} host nowMs={nowMs} />)
    await screen.findByText(/Valheim · 0.219.16/)
    fireEvent.click(screen.getByText('State the required version'))
    fireEvent.change(screen.getByLabelText('Required game version (owner-reported)'), { target: { value: '0.220.0' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save version' }))
    const saveSignal = (fetcher.mock.calls[1][1] as RequestInit).signal
    view.rerender(<GameCompatibilityPanel profileId={otherId} host nowMs={nowMs} />)
    expect(saveSignal?.aborted).toBe(true)
    expect(await screen.findByText(/Other game · 1.2.3/)).toBeInTheDocument()
    expect(screen.getByLabelText('Required game version (owner-reported)')).toHaveValue('')
    await act(async () => finishSave?.(new Response(JSON.stringify({ ...basic,
      requirements: { ...requirements, requiredVersion: '0.220.0' } }))))
    expect(screen.queryByText(/Valheim · 0.220.0/)).not.toBeInTheDocument()
    expect(fetcher.mock.calls.filter(call => (call[1] as RequestInit).method === 'PUT')).toHaveLength(1)
  })

  it('cancels a read when the same profile belongs to another saved Host and ignores its late response', async () => {
    let finishRead: ((response: Response) => void) | undefined
    const fetcher = vi.fn().mockImplementationOnce(() => new Promise<Response>(resolve => { finishRead = resolve }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ ...friend,
        requirements: { ...requirements, requiredVersion: '0.218.0' }, versionComparison: 'Match' })))
    vi.stubGlobal('fetch', fetcher)
    const view = render(<GameCompatibilityPanel profileId={profileId} host={false} connectionId="first" nowMs={nowMs} />)
    const readSignal = (fetcher.mock.calls[0][1] as RequestInit).signal
    view.rerender(<GameCompatibilityPanel profileId={profileId} host={false} connectionId="second" nowMs={nowMs} />)
    expect(readSignal?.aborted).toBe(true)
    expect(await screen.findByText(/Valheim · 0.218.0/)).toBeInTheDocument()
    await act(async () => finishRead?.(new Response(JSON.stringify(friend))))
    expect(screen.getByText(/^Version:/)).toHaveTextContent('Version: Match')
    expect(screen.queryByText(/Valheim · 0.219.16/)).not.toBeInTheDocument()
  })

  it('does not read or save from unavailable access', () => {
    const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher)
    render(<GameCompatibilityPanel profileId={profileId} host={false} available={false} nowMs={nowMs} />)
    expect(screen.getByRole('button', { name: 'Refresh requirements' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Save version' })).not.toBeInTheDocument()
    expect(fetcher).not.toHaveBeenCalled()
  })

  it('clears old comparison data after a typed access denial during Save', async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(friend)))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: 'AccessExpired', message: 'The Host ended access.' }), { status: 403 }))
    vi.stubGlobal('fetch', fetcher)
    render(<GameCompatibilityPanel profileId={profileId} host={false} nowMs={nowMs} />)
    await screen.findByText(/Valheim · 0.219.16/)
    fireEvent.click(screen.getByText('Enter the version shown in your game'))
    fireEvent.change(screen.getByLabelText('Installed game version (manual)'), { target: { value: '0.219.16' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save version' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('AccessExpired')
    expect(screen.queryByText(/^Version:/)).not.toBeInTheDocument()
  })
})

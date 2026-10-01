import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { ServerAddOnsPanel } from './ServerAddOnsPanel'

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

it('requires offline maintenance and the current token before changing a mod', async () => {
  const base = {
    ok: true, code: 'AddOnsReady', message: 'Ready', gameVersion: '2.0.72',
    requiredOnFriendPc: 'Match the enabled mod set.', stateToken: 'before',
    canImport: true, canUndo: false, warning: null, importType: 'FactorioMod',
    items: [{ key: 'fixture_1.0.0.zip', name: 'fixture', version: '1.0.0',
      requiredGameVersion: '2.0', enabled: true, compatibility: 'Version matches', type: 'Factorio mod' }]
  }
  const fetcher = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => new Response(JSON.stringify(
    init?.method === 'POST' ? { ok: true, code: 'ModStateChanged', message: 'State saved',
      view: { ...base, stateToken: 'after', items: [{ ...base.items[0], enabled: false }] } } : base),
  { status: 200, headers: { 'Content-Type': 'application/json' } }))
  vi.stubGlobal('fetch', fetcher)
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  const props = { profileId: '11111111-1111-4111-8111-111111111111',
    state: 'Ready', maintenance: false, busy: false, recoveryBlocked: false }
  const rendered = render(<ServerAddOnsPanel {...props} />)
  expect(await screen.findByText(/fixture 1.0.0/)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Disable' })).toBeDisabled()
  rendered.rerender(<ServerAddOnsPanel {...props} state="Offline" maintenance />)
  fireEvent.click(screen.getByRole('button', { name: 'Disable' }))
  await waitFor(() => expect(screen.getByText('State saved')).toBeInTheDocument())
  expect(fetcher).toHaveBeenCalledWith(expect.stringContaining('/addons/state'),
    expect.objectContaining({ method: 'POST', body: expect.stringContaining('"expectedStateToken":"before"') }))
  expect(screen.getByRole('button', { name: 'Enable' })).toBeEnabled()
})

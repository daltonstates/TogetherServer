import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { ServerAddOnsPanel } from './ServerAddOnsPanel'

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

const base = {
    ok: true, code: 'AddOnsReady', message: 'Ready', gameVersion: '2.0.72',
    requiredOnFriendPc: 'Match the enabled mod set.', stateToken: 'before',
    canImport: true, canUndo: false, warning: null, importType: 'FactorioMod',
    items: [{ key: 'fixture_1.0.0.zip', name: 'fixture', version: '1.0.0',
      requiredGameVersion: '2.0', enabled: true, compatibility: 'Version matches', type: 'Factorio mod' }]
}
const props = { profileId: '11111111-1111-4111-8111-111111111111',
  state: 'Offline', maintenance: true, busy: false, recoveryBlocked: false }
function stubAddOns(view = base) {
  const fetcher = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>(async () => new Response(JSON.stringify(view),
    { status: 200, headers: { 'Content-Type': 'application/json' } }))
  vi.stubGlobal('fetch', fetcher)
  return fetcher
}
async function openDisable() {
  await screen.findByText(/fixture 1.0.0/)
  fireEvent.click(screen.getByRole('button', { name: 'Disable' }))
  return screen.findByRole('dialog', { name: 'Confirm add-on change' })
}

it('requires offline maintenance and the current token before changing a mod', async () => {
  const fetcher = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => new Response(JSON.stringify(
    init?.method === 'POST' ? { ok: true, code: 'ModStateChanged', message: 'State saved',
      view: { ...base, stateToken: 'after', items: [{ ...base.items[0], enabled: false }] } } : base),
  { status: 200, headers: { 'Content-Type': 'application/json' } }))
  vi.stubGlobal('fetch', fetcher)
  const rendered = render(<ServerAddOnsPanel {...props} state="Ready" maintenance={false} />)
  expect(await screen.findByText(/fixture 1.0.0/)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Disable' })).toBeDisabled()
  rendered.rerender(<ServerAddOnsPanel {...props} state="Offline" maintenance />)
  fireEvent.click(screen.getByRole('button', { name: 'Disable' }))
  const dialog = await screen.findByRole('dialog', { name: 'Confirm add-on change' })
  expect(within(dialog).getByText('Disable fixture? An offline complete setup checkpoint will be made first.')).toBeInTheDocument()
  expect(fetcher.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(0)
  fireEvent.click(within(dialog).getByRole('button', { name: 'Continue' }))
  await waitFor(() => expect(screen.getByText('State saved')).toBeInTheDocument())
  expect(fetcher).toHaveBeenCalledWith(expect.stringContaining('/addons/state'),
    expect.objectContaining({ method: 'POST', body: expect.stringContaining('"expectedStateToken":"before"') }))
  expect(screen.getByRole('button', { name: 'Enable' })).toBeEnabled()
})

it('declines an add-on change without making a mutation request', async () => {
  const fetcher = stubAddOns()
  render(<ServerAddOnsPanel {...props} />)
  const dialog = await openDisable()
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(fetcher).toHaveBeenCalledTimes(1)
  expect(screen.getByRole('button', { name: 'Disable' })).toBeEnabled()
})

it.each([
  { state: 'Unknown' }, { maintenance: false }, { busy: true }, { recoveryBlocked: true },
  { profileId: '22222222-2222-4222-8222-222222222222' }
])('does not change add-ons when current guards change during confirmation: %j', async changed => {
  const fetcher = stubAddOns()
  const rendered = render(<ServerAddOnsPanel {...props} />)
  const dialog = await openDisable()
  rendered.rerender(<ServerAddOnsPanel {...props} {...changed} />)
  fireEvent.click(within(dialog).getByRole('button', { name: 'Continue' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(fetcher.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(0)
})

it('does not revive a confirmation after an Offline guard changes and returns', async () => {
  const fetcher = stubAddOns()
  const rendered = render(<ServerAddOnsPanel {...props} />)
  const dialog = await openDisable()
  rendered.rerender(<ServerAddOnsPanel {...props} state="Unknown" />)
  rendered.rerender(<ServerAddOnsPanel {...props} />)
  fireEvent.click(within(dialog).getByRole('button', { name: 'Continue' }))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Disable' })).toBeEnabled())
  expect(fetcher).toHaveBeenCalledTimes(1)
})

it('does not submit a token after an earlier refresh completes during confirmation', async () => {
  let finishRefresh!: (response: Response) => void
  const response = (view: typeof base) => new Response(JSON.stringify(view),
    { status: 200, headers: { 'Content-Type': 'application/json' } })
  const fetcher = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(response(base)))
  fetcher.mockImplementationOnce(() => Promise.resolve(response(base)))
    .mockImplementationOnce(() => new Promise<Response>(resolve => { finishRefresh = resolve }))
  vi.stubGlobal('fetch', fetcher)
  render(<ServerAddOnsPanel {...props} />)
  await screen.findByText(/fixture 1.0.0/)
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
  const dialog = await openDisable()
  finishRefresh(response({ ...base, gameVersion: '2.0.73', stateToken: 'refreshed' }))
  await screen.findByText('2.0.73')
  fireEvent.click(within(dialog).getByRole('button', { name: 'Continue' }))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Disable' })).toBeEnabled())
  expect(fetcher).toHaveBeenCalledTimes(2)
})

it.each([
  ['Import mod ZIP', 'import', 'Choose a local Factorio mod ZIP? TogetherServer will checkpoint the offline setup before copying it.'],
  ['Undo last add-on change', 'undo', 'Undo the last mod change? TogetherServer will checkpoint the offline world first.'],
  ['Mark version reviewed', 'review-version', 'Confirm you reviewed this game version and each installed add-on? This does not prove a real game join.']
])('confirms %s with its original warning and state token', async (label, route, description) => {
  const view = { ...base, canUndo: true, warning: 'Version needs review' }
  const fetcher = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => new Response(JSON.stringify(
    init?.method === 'POST' ? { ok: true, code: 'Saved', message: 'State saved', view } : view),
    { status: 200, headers: { 'Content-Type': 'application/json' } }))
  vi.stubGlobal('fetch', fetcher)
  render(<ServerAddOnsPanel {...props} />)
  await screen.findByText(/fixture 1.0.0/)
  fireEvent.click(screen.getByRole('button', { name: label }))
  const dialog = await screen.findByRole('dialog')
  expect(within(dialog).getByText(description)).toBeInTheDocument()
  fireEvent.click(within(dialog).getByRole('button', { name: 'Continue' }))
  await screen.findByText('State saved')
  expect(fetcher).toHaveBeenCalledWith(expect.stringContaining(`/addons/${route}`),
    expect.objectContaining({ method: 'POST', body: JSON.stringify({ expectedStateToken: 'before' }) }))
})

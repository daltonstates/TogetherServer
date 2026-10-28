import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { ApprovedGamePortsPanel, localHostingPortChoice, parseApprovedGamePorts, parseApprovedGamePortsResult, type ApprovedGamePort } from './ApprovedGamePorts'

const endpoint = '/api/local/approved-game-ports'
const plan: ApprovedGamePort = { id: '11111111-1111-4111-8111-111111111111', game: 'Valheim', gamePort: 2456, factorioRconPort: null }
const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
const result = (approvedPorts: ApprovedGamePort[], ok = true) => ({ ok, code: ok ? 'GamePortsApproved' : 'LocalPortsRequired',
  message: ok ? 'Game ports saved on this PC.' : 'Those ports could not be saved.', approvedPorts })
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

it.each([
  null, {}, [null], [{ ...plan, id: 'bad' }], [{ ...plan, id: '00000000-0000-0000-0000-000000000000' }],
  [{ ...plan, game: 'Unknown' }], [{ ...plan, gamePort: 1023 }], [{ ...plan, gamePort: 65535 }],
  [{ ...plan, gamePort: 2456.5 }], [{ ...plan, factorioRconPort: 2457 }],
  [{ ...plan, game: 'Factorio' }], [{ ...plan, game: 'Factorio', factorioRconPort: 2456 }], [plan, plan],
  [plan, { ...plan, id: '22222222-2222-4222-8222-222222222222' }]
])('rejects malformed or inconsistent approved plans %j', value => {
  expect(() => parseApprovedGamePorts(value)).toThrow()
})

it('validates returned result lists and requires a deliberate local choice before creating approval', () => {
  expect(parseApprovedGamePortsResult(result([plan])).approvedPorts).toEqual([plan])
  expect(() => parseApprovedGamePortsResult(result([{ ...plan, gamePort: 0 }]))).toThrow()
  const input = { game: 'Valheim', choice: 'new', plans: [plan], gamePort: '2460', rconPort: '', approved: false }
  expect(localHostingPortChoice(input)).toBeNull()
  expect(localHostingPortChoice({ ...input, approved: true })).toEqual({ gamePort: 2460, factorioRconPort: null, approvePorts: true })
  expect(localHostingPortChoice({ ...input, choice: plan.id })).toEqual({ approvedPortId: plan.id })
  expect(localHostingPortChoice({ ...input, choice: plan.id, game: 'MinecraftJava' })).toBeNull()
  expect(localHostingPortChoice({ ...input, choice: 'unknown' })).toBeNull()
})

it('reads only after Game ports is opened, with the local header and no mutation', async () => {
  const fetcher = vi.fn(async () => json({ approvedPorts: [plan] }))
  vi.stubGlobal('fetch', fetcher)
  const view = render(<ApprovedGamePortsPanel active={false} />)
  expect(fetcher).not.toHaveBeenCalled()
  view.rerender(<ApprovedGamePortsPanel active />)
  expect(await screen.findByRole('list', { name: 'Approved game ports' })).toHaveTextContent('Valheim · 2456–2457')
  expect(fetcher).toHaveBeenCalledExactlyOnceWith(endpoint, expect.objectContaining({ cache: 'no-store',
    headers: { 'X-TogetherServer-Local': '1' }, signal: expect.any(AbortSignal) }))
})

it('creates, edits and removes saved approval only through explicit clicks and canonical results', async () => {
  const calls: { url: string; method?: string; body?: unknown }[] = []
  const saved = { ...plan, gamePort: 2460 }
  const edited = { ...plan, gamePort: 2470 }
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method, body: init?.body === undefined ? undefined : JSON.parse(String(init.body)) })
    return json(!init?.method ? { approvedPorts: [] } : init.method === 'POST' ? result([saved]) :
      init.method === 'PUT' ? result([edited]) : result([]))
  }))
  render(<ApprovedGamePortsPanel />)
  await screen.findByText('No game ports have been approved on this PC.')
  const approve = screen.getByRole('button', { name: 'Approve ports' })
  expect(approve).toBeDisabled()
  fireEvent.change(screen.getByRole('spinbutton', { name: 'Approved game port' }), { target: { value: '2460' } })
  expect(calls).toHaveLength(1)
  fireEvent.click(approve)
  await screen.findByText('Valheim · 2460–2461')
  expect(calls[1]).toEqual({ url: endpoint, method: 'POST', body: { game: 'Valheim', gamePort: 2460, factorioRconPort: null } })
  fireEvent.click(screen.getByRole('button', { name: 'Edit Valheim · 2460–2461' }))
  fireEvent.change(screen.getByRole('spinbutton', { name: 'Approved game port' }), { target: { value: '2470' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save approved ports' }))
  await screen.findByText('Valheim · 2470–2471')
  expect(calls[2]).toEqual({ url: `${endpoint}/${plan.id}`, method: 'PUT', body: { game: 'Valheim', gamePort: 2470, factorioRconPort: null } })
  fireEvent.click(screen.getByRole('button', { name: 'Remove Valheim · 2470–2471' }))
  await screen.findByText('No game ports have been approved on this PC.')
  expect(calls[3]).toEqual({ url: `${endpoint}/${plan.id}`, method: 'DELETE', body: undefined })
})

it('requires distinct Factorio game and private RCON ports and keeps failed edits retryable', async () => {
  const calls: RequestInit[] = []
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
    calls.push(init ?? {})
    return json(init?.method ? result([], false) : { approvedPorts: [] })
  }))
  render(<ApprovedGamePortsPanel />)
  await screen.findByText('No game ports have been approved on this PC.')
  fireEvent.change(screen.getByRole('combobox', { name: 'Game for approved ports' }), { target: { value: 'Factorio' } })
  fireEvent.change(screen.getByRole('spinbutton', { name: 'Approved game port' }), { target: { value: '34197' } })
  fireEvent.change(screen.getByRole('spinbutton', { name: 'Approved private RCON port' }), { target: { value: '34197' } })
  expect(screen.getByRole('button', { name: 'Approve ports' })).toBeDisabled()
  fireEvent.change(screen.getByRole('spinbutton', { name: 'Approved private RCON port' }), { target: { value: '27015' } })
  fireEvent.click(screen.getByRole('button', { name: 'Approve ports' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Those ports could not be saved.')
  expect(screen.getByRole('spinbutton', { name: 'Approved game port' })).toHaveValue(34197)
  expect(screen.getByRole('button', { name: 'Approve ports' })).toBeEnabled()
  expect(JSON.parse(String(calls[1].body))).toEqual({ game: 'Factorio', gamePort: 34197, factorioRconPort: 27015 })
})

it('keeps saved rows during a failed refresh and aborts an unfinished mutation on unmount', async () => {
  let reads = 0
  let signal: AbortSignal | null | undefined
  let finish!: (value: Response) => void
  const busy = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
    if (init?.method) { signal = init.signal; return new Promise<Response>(resolve => { finish = resolve }) }
    if (++reads !== 2) return json({ approvedPorts: [plan] })
    throw new Error('Unavailable.')
  }))
  const view = render(<ApprovedGamePortsPanel onBusyChange={busy} />)
  await screen.findByRole('list', { name: 'Approved game ports' })
  fireEvent.click(screen.getByRole('button', { name: 'Refresh approved ports' }))
  await screen.findByRole('alert')
  expect(screen.getByRole('list', { name: 'Approved game ports' })).toHaveTextContent('Valheim · 2456–2457')
  expect(screen.getByRole('button', { name: 'Remove Valheim · 2456–2457' })).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Refresh approved ports' }))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Remove Valheim · 2456–2457' })).toBeEnabled())
  fireEvent.click(screen.getByRole('button', { name: 'Remove Valheim · 2456–2457' }))
  await waitFor(() => expect(busy).toHaveBeenLastCalledWith(true))
  expect(within(screen.getByRole('list', { name: 'Approved game ports' })).getByRole('button', { name: /Remove/ })).toBeDisabled()
  view.unmount()
  expect(signal?.aborted).toBe(true)
  expect(busy).toHaveBeenLastCalledWith(false)
  await act(async () => { finish(json(result([]))) })
})

it('bounds a stalled initial read, permits a fresh retry and ignores the timed-out reply', async () => {
  vi.useFakeTimers()
  let finish!: (value: Response) => void
  let oldSignal: AbortSignal | null | undefined
  let reads = 0
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
    if (++reads === 1) { oldSignal = init?.signal; return new Promise<Response>(resolve => { finish = resolve }) }
    return json({ approvedPorts: [plan] })
  }))
  render(<ApprovedGamePortsPanel />)
  expect(screen.getByText('Loading approved game ports…')).toBeInTheDocument()
  await act(async () => { await vi.advanceTimersByTimeAsync(20000) })
  expect(oldSignal?.aborted).toBe(true)
  expect(screen.getByRole('alert')).toHaveTextContent('timed out after 20 seconds')
  expect(screen.getByRole('button', { name: 'Refresh approved ports' })).toBeEnabled()
  fireEvent.click(screen.getByRole('button', { name: 'Refresh approved ports' }))
  await act(async () => {})
  expect(screen.getByRole('list', { name: 'Approved game ports' })).toHaveTextContent('Valheim · 2456–2457')
  await act(async () => { finish(json({ approvedPorts: [] })) })
  expect(screen.getByRole('list', { name: 'Approved game ports' })).toHaveTextContent('Valheim · 2456–2457')
})

it.each(['POST', 'PUT', 'DELETE'] as const)('bounds a stalled %s, releases navigation and requires a canonical refresh before another change', async method => {
  vi.useFakeTimers()
  let finish!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const changed = { ...plan, gamePort: 2460 }
  const busy = vi.fn()
  const writes: string[] = []
  let reads = 0
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
    if (!init?.method) return json({ approvedPorts: ++reads === 1 ? [plan] : [changed] })
    writes.push(init.method); signal = init.signal
    return new Promise<Response>(resolve => { finish = resolve })
  }))
  render(<ApprovedGamePortsPanel onBusyChange={busy} />)
  await act(async () => {})
  if (method === 'DELETE') fireEvent.click(screen.getByRole('button', { name: 'Remove Valheim · 2456–2457' }))
  else {
    if (method === 'PUT') fireEvent.click(screen.getByRole('button', { name: 'Edit Valheim · 2456–2457' }))
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Approved game port' }), { target: { value: '2460' } })
    fireEvent.click(screen.getByRole('button', { name: method === 'PUT' ? 'Save approved ports' : 'Approve ports' }))
  }
  await act(async () => {})
  expect(busy).toHaveBeenLastCalledWith(true)
  await act(async () => { await vi.advanceTimersByTimeAsync(19999) })
  expect(screen.getByRole('button', { name: 'Refresh approved ports' })).toBeDisabled()
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(signal?.aborted).toBe(true)
  expect(busy).toHaveBeenLastCalledWith(false)
  expect(screen.getByRole('alert')).toHaveTextContent('It may have been saved. Refresh approved ports before making another change.')
  expect(screen.getByRole('button', { name: 'Remove Valheim · 2456–2457' })).toBeDisabled()
  expect(screen.getByRole('button', { name: 'Refresh approved ports' })).toBeEnabled()
  fireEvent.click(screen.getByRole('button', { name: 'Refresh approved ports' }))
  await act(async () => {})
  expect(screen.getByRole('list', { name: 'Approved game ports' })).toHaveTextContent('Valheim · 2460–2461')
  expect(screen.getByRole('button', { name: 'Remove Valheim · 2460–2461' })).toBeEnabled()
  await act(async () => { finish(json(result([]))) })
  expect(screen.getByRole('list', { name: 'Approved game ports' })).toHaveTextContent('Valheim · 2460–2461')
  expect(writes).toEqual([method])
})

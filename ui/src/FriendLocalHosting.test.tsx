import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FriendLocalHosting, parseLocalHostingStatus, type LocalHostingStatus } from './FriendLocalHosting'

const profileId = '11111111-1111-4111-8111-111111111111'
const localProfileId = '22222222-2222-4222-8222-222222222222'
const branchHash = 'A'.repeat(64)
const base = `/api/local/friend/${profileId}/shared-world`
const initial: LocalHostingStatus = {
  ok: true, code: 'NeedsPreparation', message: 'Prepare the local copy.', allowed: true, consented: false,
  game: 'Valheim', name: 'Shared world', needsServerFile: false, needsPassword: false,
  serverFile: 'C:\\Games\\valheim_server.exe', gameVersion: '0.222.1', gamePort: 2456, controlPort: 5131,
  localProfileId: null, branchHash: null, readyForManualStart: false, running: false,
  steps: [
    { key: 'permission', state: 'Ready', message: 'The owner allows this PC to host.' },
    { key: 'server', state: 'Ready', message: 'Installed server found.' },
    { key: 'save', state: 'ActionRequired', message: 'Allow a verified local copy.' },
    { key: 'ports', state: 'Unknown', message: 'Checked during preparation.' },
    { key: 'connection', state: 'Unknown', message: 'Incoming Friend access is unverified. Local hosting is available.' }
  ]
}
const prepared: LocalHostingStatus = {
  ...initial, code: 'LocalHostingPrepared', message: 'Verified copy prepared without starting.', consented: true,
  localProfileId, branchHash, readyForManualStart: true,
  steps: initial.steps.map(step => step.key === 'connection' ? step : { ...step, state: 'Ready' })
}
const response = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
const props = { profileId, connectionId: 'first', worldName: 'Shared world', available: true, onOpenHost: vi.fn() }
afterEach(() => vi.useRealTimers())

describe('local hosting response trust', () => {
  it('accepts verified local preparation while incoming connectivity remains unverified', () => {
    expect(parseLocalHostingStatus(prepared)).toBe(prepared)
  })
  it.each([
    { allowed: false }, { consented: false }, { ok: false }, { running: true }, { needsServerFile: true },
    { localProfileId: null }, { branchHash: 'bad' }, { gamePort: 0 }, { controlPort: 65536 },
    { steps: prepared.steps.slice(1) },
    { steps: prepared.steps.map(step => step.key === 'save' ? { ...step, state: 'Unknown' } : step) },
    { steps: prepared.steps.map(step => step.key === 'server' ? { ...step, state: 'Passed' } : step) }
  ])('refuses inconsistent preparation %j', patch => {
    expect(() => parseLocalHostingStatus({ ...prepared, ...patch })).toThrow()
  })
})

it('checks read-only, prepares with local consent, and starts only on the separate explicit action', async () => {
  const requests: { url: string; init?: RequestInit }[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    requests.push({ url, init })
    if (url.endsWith('/hosting/prepare')) return response(prepared)
    if (url.endsWith('/start')) return response({ ok: true, code: 'Starting', message: 'Local Start requested.' })
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const dialog = screen.getByRole('dialog', { name: 'Host on this PC' })
  expect(requests).toHaveLength(1)
  expect(requests[0].init?.method).toBeUndefined()
  expect(requests[0].init?.headers).toMatchObject({ 'X-TogetherServer-Local': '1' })
  expect(within(dialog).getByRole('button', { name: 'Prepare this PC' })).toBeDisabled()
  fireEvent.click(within(dialog).getByRole('checkbox', { name: /Keep this save on my PC/ }))
  fireEvent.click(within(dialog).getByRole('button', { name: 'Prepare this PC' }))
  const start = await within(dialog).findByRole('button', { name: 'Start server on this PC' })
  expect(requests.map(item => item.url)).toEqual([`${base}/hosting`, `${base}/hosting/prepare`])
  expect(JSON.parse(requests[1].init!.body as string)).toEqual({
    allowReceiving: true, acceptSplitWarning: true, serverFile: null, gamePassword: null
  })
  expect(within(dialog).getByText(/Incoming Friend access is unverified/)).toBeInTheDocument()
  expect(open).not.toHaveBeenCalled()
  fireEvent.click(start)
  await waitFor(() => expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId))
  expect(requests[2].url).toBe(`${base}/recovery/separate/start`)
  expect(JSON.parse(requests[2].init!.body as string)).toEqual({ branchHash, localProfileId, acceptSplitWarning: true })
})

it('does not show the hosting action for a denied grant or explicit removed access', async () => {
  const fetcher = vi.fn(async () => response({ ...initial, allowed: false }))
  vi.stubGlobal('fetch', fetcher)
  const view = render(<FriendLocalHosting {...props} />)
  await waitFor(() => expect(fetcher).toHaveBeenCalledOnce())
  expect(screen.queryByRole('button', { name: 'Host on this PC' })).not.toBeInTheDocument()
  view.rerender(<FriendLocalHosting {...props} accessBlocked />)
  expect(fetcher).toHaveBeenCalledOnce()
  expect(screen.queryByRole('button', { name: 'Host on this PC' })).not.toBeInTheDocument()
})

it('uses cached signed permission while the remote Host is offline and does not require remote Start', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => response(prepared)))
  render(<FriendLocalHosting {...props} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  expect(screen.getByRole('button', { name: 'Start server on this PC' })).toBeDisabled()
  fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC/ }))
  expect(screen.getByRole('button', { name: 'Start server on this PC' })).toBeEnabled()
})

it('retains a missing-install fix in the same flow and never starts after a failed preparation', async () => {
  const missing = { ...initial, ok: false, needsServerFile: true, serverFile: null,
    steps: initial.steps.map(step => step.key === 'server' ? { ...step, state: 'ActionRequired', message: 'Install Valheim Dedicated Server.' } : step) }
  const requests: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string) => { requests.push(url); return response(missing) }))
  const browse = vi.fn(async () => 'C:\\Selected\\valheim_server.exe')
  render(<FriendLocalHosting {...props} onBrowseServerFile={browse} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  expect(screen.getByRole('link', { name: 'Install Valheim Dedicated Server in Steam' })).toHaveAttribute('href', 'steam://install/896660')
  fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC/ }))
  expect(screen.getByRole('button', { name: 'Prepare this PC' })).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Browse' }))
  await waitFor(() => expect(screen.getByRole('textbox', { name: 'Installed server file' })).toHaveValue('C:\\Selected\\valheim_server.exe'))
  fireEvent.click(screen.getByRole('button', { name: 'Prepare this PC' }))
  await waitFor(() => expect(requests).toContain(`${base}/hosting/prepare`))
  expect(screen.queryByRole('button', { name: 'Start server on this PC' })).not.toBeInTheDocument()
  expect(requests.some(url => url.endsWith('/start'))).toBe(false)
})

it('blocks an already open dialog when another operation disables actions', async () => {
  const fetcher = vi.fn(async () => response(prepared))
  vi.stubGlobal('fetch', fetcher)
  const view = render(<FriendLocalHosting {...props} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC/ }))
  view.rerender(<FriendLocalHosting {...props} disabled />)
  const start = screen.getByRole('button', { name: 'Start server on this PC' })
  expect(start).toBeDisabled()
  fireEvent.click(start)
  expect(fetcher).toHaveBeenCalledOnce()
})

it('opens the prepared local server’s connection fix without starting it', async () => {
  const fetcher = vi.fn(async () => response(prepared))
  vi.stubGlobal('fetch', fetcher)
  const connectionHelp = vi.fn()
  render(<FriendLocalHosting {...props} onOpenConnectionHelp={connectionHelp} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  fireEvent.click(screen.getByRole('button', { name: 'Network for this PC' }))
  expect(connectionHelp).toHaveBeenCalledExactlyOnceWith(localProfileId)
  expect(screen.queryByRole('dialog', { name: 'Host on this PC' })).not.toBeInTheDocument()
  expect(fetcher).toHaveBeenCalledOnce()
})

it('discards an old prepare completion after switching away and back to the same saved connection', async () => {
  let release!: (value: Response) => void
  let prepareSignal: AbortSignal | null | undefined
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/hosting/prepare')) {
      prepareSignal = init?.signal
      return new Promise<Response>(resolve => { release = resolve })
    }
    return response(initial)
  }))
  const view = render(<FriendLocalHosting {...props} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC/ }))
  fireEvent.click(screen.getByRole('button', { name: 'Prepare this PC' }))
  await waitFor(() => expect(prepareSignal).toBeDefined())
  view.rerender(<FriendLocalHosting {...props} connectionId="other" />)
  await screen.findByRole('button', { name: 'Host on this PC' })
  view.rerender(<FriendLocalHosting {...props} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  await act(async () => { release(response(prepared)) })
  expect(prepareSignal?.aborted).toBe(true)
  expect(screen.queryByRole('button', { name: 'Start server on this PC' })).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Prepare this PC' })).toBeDisabled()
})

it('withdraws stale ready controls if a read stalls, and ignores its late response', async () => {
  vi.useFakeTimers()
  let reads = 0
  let release!: (value: Response) => void
  vi.stubGlobal('fetch', vi.fn(async () => ++reads === 1 ? response(prepared)
    : new Promise<Response>(resolve => { release = resolve })))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host on this PC' }))
  fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC/ }))
  await act(async () => { await vi.advanceTimersByTimeAsync(25001) })
  expect(screen.queryByRole('button', { name: 'Start server on this PC' })).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Prepare this PC' })).toBeDisabled()
  await act(async () => { release(response(prepared)) })
  expect(screen.queryByRole('button', { name: 'Start server on this PC' })).not.toBeInTheDocument()
})

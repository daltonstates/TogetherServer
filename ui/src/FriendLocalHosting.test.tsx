import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FriendLocalHosting, localHostingAttention, parseLocalHostingStatus, type LocalHostingStatus } from './FriendLocalHosting'

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
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

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

async function openHosting() {
  fireEvent.click(await screen.findByRole('button', { name: 'Host locally' }))
  const dialog = screen.getByRole('dialog', { name: 'Host locally' })
  await waitFor(() => expect(dialog).not.toHaveAttribute('aria-busy', 'true'))
  return within(dialog)
}
async function confirmHosting() {
  const dialog = within(screen.getByRole('dialog', { name: 'Host locally' }))
  const button = await dialog.findByRole('button', { name: /^(Host locally|Retry and start)$/ })
  await waitFor(() => expect(button).toBeEnabled())
  fireEvent.click(button)
}

it('uses one network confirmation to prepare and Start the exact checked save', async () => {
  const calls: { url: string; body?: Record<string, unknown> }[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, ...(init?.method ? { body: JSON.parse(String(init.body)) } : {}) })
    return response(url.endsWith('/prepare') ? prepared : url.endsWith('/start') ?
      { ok: true, code: 'Starting', message: 'Start requested.' } : initial)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  const dialog = await openHosting()
  expect(dialog.getByText(/Hosting lets other players connect to this PC through your network/)).toBeInTheDocument()
  expect(dialog.queryByRole('checkbox')).not.toBeInTheDocument()
  expect(dialog.queryByText(/Receive|Reshare|voter|choose a history/i)).not.toBeInTheDocument()
  expect(calls).toEqual([{ url: `${base}/hosting` }, { url: `${base}/hosting` }])
  await confirmHosting()
  await waitFor(() => expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId))
  expect(calls.slice(2)).toEqual([{ url: `${base}/hosting/prepare`, body: {
    allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null
  } }, { url: `${base}/hosting/start`, body: { localProfileId, branchHash, requestId: null, acceptSplitWarning: true } }])
})

it('rechecks and prepares an existing copy after the network confirmation', async () => {
  const writes: string[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(url.endsWith('/start') ? { ok: true, code: 'Starting', message: 'Start requested.' } : prepared)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await openHosting()
  expect(writes).toEqual([])
  await confirmHosting()
  await waitFor(() => expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId))
  expect(writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
})

it.each(['action', 'card'] as const)('hides the %s affordance when permission is denied', async presentation => {
  const fetcher = vi.fn(async () => response({ ...initial, allowed: false }))
  vi.stubGlobal('fetch', fetcher)
  const view = render(<FriendLocalHosting {...props} presentation={presentation} />)
  await waitFor(() => expect(fetcher).toHaveBeenCalledOnce())
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  view.rerender(<FriendLocalHosting {...props} presentation={presentation} accessBlocked />)
  expect(fetcher).toHaveBeenCalledOnce()
})

it('shows the missing install and continues with the selected file without a separate save permission', async () => {
  const missing: LocalHostingStatus = { ...initial, ok: false, code: 'InstalledServerRequired', needsServerFile: true, serverFile: null }
  const writes: { url: string; body: Record<string, unknown> }[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push({ url, body: JSON.parse(String(init.body)) })
    return response(missing)
  }))
  const browse = vi.fn(async () => 'C:/Selected/valheim_server.exe')
  render(<FriendLocalHosting {...props} onBrowseServerFile={browse} />)
  const dialog = await openHosting()
  expect(dialog.getByRole('heading', { name: 'Install or find the game server' })).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Browse' }))
  await confirmHosting()
  await waitFor(() => expect(writes).toHaveLength(1))
  expect(writes[0].body.serverFile).toBe('C:/Selected/valheim_server.exe')
  expect(dialog.getByRole('button', { name: 'Retry and start' })).toBeEnabled()
})

it('requires Minecraft terms to be accepted locally before the network confirmation can proceed', async () => {
  const writes: { url: string; body: Record<string, unknown> }[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push({ url, body: JSON.parse(String(init.body)) })
    return response({ ...initial, game: 'MinecraftJava' })
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const terms = dialog.getByRole('checkbox', { name: /I have read and accept the terms/ })
  expect(terms).not.toBeChecked()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  fireEvent.click(terms)
  expect(writes).toEqual([])
  await confirmHosting()
  await waitFor(() => expect(writes).toEqual([{ url: `${base}/hosting/prepare`, body: {
    allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null, acceptedTerms: true
  } }]))
})

it('offers Connection help before a profile exists without changing the network from a status read', async () => {
  const help = vi.fn()
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response({ ...initial, remedy: { kind: 'Connection', title: 'Let friends connect to this PC', message: 'Review the incoming address.' } })
  }))
  render(<FriendLocalHosting {...props} onOpenConnectionHelp={help} />)
  const dialog = await openHosting()
  fireEvent.click(dialog.getByRole('button', { name: 'Connection help' }))
  expect(help).toHaveBeenCalledExactlyOnceWith(profileId)
  expect(writes).toEqual([])
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it.each([true, false])('offers port setup only for an existing local profile (%s)', async hasProfile => {
  const setup = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async () => response({ ...initial, consented: true, branchHash,
    localProfileId: hasProfile ? localProfileId : null, code: 'PortInUse' })))
  render(<FriendLocalHosting {...props} onOpenGameSetup={setup} />)
  const dialog = await openHosting()
  if (hasProfile) {
    fireEvent.click(dialog.getByRole('button', { name: 'Open game setup' }))
    expect(setup).toHaveBeenCalledExactlyOnceWith('Valheim', localProfileId)
  } else expect(dialog.queryByRole('button', { name: 'Open game setup' })).not.toBeInTheDocument()
})

it.each(['HostingHistoryChoiceRequired', 'WorldHistoryReviewRequired', 'SharedSaveHistoryNeedsReview'])('keeps %s protected behind one comprehensible blocker', async code => {
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response({ ...initial, code })
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  expect(dialog.getByRole('heading', { name: 'This server has two different save histories' })).toBeInTheDocument()
  expect(dialog.getByText(/All existing saves are kept/)).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  expect(dialog.queryByRole('combobox')).not.toBeInTheDocument()
  expect(writes).toEqual([])
})

it.each(['connection changed', 'actions disabled', 'access removed'] as const)('cancels the pending Start when %s', async reason => {
  let release!: (value: Response) => void
  const writes: string[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); return new Promise<Response>(resolve => { release = resolve }) }
    return response(initial)
  }))
  const view = render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await openHosting(); await confirmHosting()
  if (reason === 'connection changed') view.rerender(<FriendLocalHosting {...props} connectionId="second" onOpenHost={open} />)
  else if (reason === 'actions disabled') view.rerender(<FriendLocalHosting {...props} onOpenHost={open} disabled />)
  else view.rerender(<FriendLocalHosting {...props} onOpenHost={open} accessBlocked />)
  await act(async () => { release(response(prepared)) })
  expect(writes).toEqual([`${base}/hosting/prepare`])
  expect(open).not.toHaveBeenCalled()
})

it('never starts or accepts terms or installs anything from polling', async () => {
  vi.useFakeTimers()
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => { if (init?.method) writes.push(url); return response(prepared) }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(40000) })
  expect(writes).toEqual([])
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it('does not repeat Start after a failure and a later ready poll', async () => {
  vi.useFakeTimers()
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(url.endsWith('/start') ? { ok: false, code: 'PortInUse', message: 'A game port is busy.' } : prepared)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  expect(screen.getByRole('heading', { name: 'Free the game ports on this PC' })).toBeInTheDocument()
  await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
  expect(writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
})

it('bounds preparation and ignores a late result without retrying Start', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); signal = init.signal; return new Promise<Response>(resolve => { release = resolve }) }
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(120001) })
  expect(signal?.aborted).toBe(true)
  expect(screen.getByRole('button', { name: 'Retry automatic checks' })).toBeEnabled()
  await act(async () => { release(response(prepared)); await vi.advanceTimersByTimeAsync(10000) })
  expect(writes).toEqual([`${base}/hosting/prepare`])
})

it('withdraws stale permission when an automatic check stalls', async () => {
  vi.useFakeTimers()
  let reads = 0
  let release!: (value: Response) => void
  vi.stubGlobal('fetch', vi.fn(async () => ++reads <= 2 ? response(initial) : new Promise<Response>(resolve => { release = resolve })))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(25001) })
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' })).toBeDisabled()
  await act(async () => { release(response(prepared)) })
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' })).toBeDisabled()
})

it('keeps automatic check results secondary and rejects unreviewed action destinations', async () => {
  expect(() => parseLocalHostingStatus({ ...prepared, remedy: { kind: 'https://untrusted', title: 'Click', message: 'Click' } })).toThrow()
  expect(localHostingAttention(initial)).toBeNull()
  expect(localHostingAttention(prepared)).toBeNull()
  vi.stubGlobal('fetch', vi.fn(async () => response(initial)))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  expect(dialog.getByText('Automatically checked results', { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
})

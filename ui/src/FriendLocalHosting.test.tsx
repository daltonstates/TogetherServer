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

it('checks read-only and uses one consented click for preparation and Start', async () => {
  const calls: { url: string; body?: Record<string, unknown> }[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, ...(init?.method ? { body: JSON.parse(String(init.body)) } : {}) })
    return response(url.endsWith('/prepare') ? prepared : url.endsWith('/start') ?
      { ok: true, code: 'Starting', message: 'Start requested.' } : initial)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const dialog = screen.getByRole('dialog')
  expect(within(dialog).getByRole('heading', { name: 'Review before hosting your own copy' })).toBeInTheDocument()
  const proceed = within(dialog).getByRole('button', { name: 'Continue and start' })
  expect(proceed).toBeDisabled()
  fireEvent.click(within(dialog).getByRole('checkbox', { name: /Keep this save on my PC/ }))
  expect(calls).toEqual([{ url: `${base}/hosting` }])
  fireEvent.click(proceed)
  await waitFor(() => expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId))
  expect(calls).toEqual([{ url: `${base}/hosting` }, { url: `${base}/hosting/prepare`, body: {
    allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null
  } }, { url: `${base}/hosting/start`, body: { localProfileId, branchHash, requestId: null, acceptSplitWarning: true } }])
})

it('starts an already prepared copy directly from the overview without a wizard', async () => {
  const writes: string[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); return response({ ok: true, code: 'Starting', message: 'Start requested.' }) }
    return response(prepared)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  const hostButton = await screen.findByRole('button', { name: 'Host on this PC' })
  expect(hostButton).toHaveAccessibleDescription('Starts your own copy. The other Host can keep running; progress on the two servers stays separate.')
  fireEvent.click(hostButton)
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  await waitFor(() => expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId))
  expect(writes).toEqual([`${base}/hosting/start`])
})

it('hides the action when permission is denied and does not read explicitly blocked access', async () => {
  const fetcher = vi.fn(async () => response({ ...initial, allowed: false }))
  vi.stubGlobal('fetch', fetcher)
  const view = render(<FriendLocalHosting {...props} />)
  await waitFor(() => expect(fetcher).toHaveBeenCalledOnce())
  expect(screen.queryByRole('button', { name: 'Host on this PC' })).not.toBeInTheDocument()
  view.rerender(<FriendLocalHosting {...props} accessBlocked />)
  expect(fetcher).toHaveBeenCalledOnce()
})

it('opens at the missing game step and never starts after a failed preparation', async () => {
  const missing: LocalHostingStatus = { ...initial, ok: false, code: 'InstalledServerRequired', needsServerFile: true, serverFile: null }
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => { if (init?.method) writes.push(url); return response(missing) }))
  const browse = vi.fn(async () => 'C:/Selected/valheim_server.exe')
  render(<FriendLocalHosting {...props} onBrowseServerFile={browse} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  expect(screen.getByRole('heading', { name: 'Install or find the game server' })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Install Valheim Dedicated Server in Steam' })).toHaveAttribute('href', 'steam://install/896660')
  fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC/ }))
  expect(screen.getByRole('button', { name: 'Continue and start' })).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Browse' }))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Continue and start' })).toBeEnabled())
  fireEvent.click(screen.getByRole('button', { name: 'Continue and start' }))
  await waitFor(() => expect(writes).toEqual([`${base}/hosting/prepare`]))
})

it('requires local Minecraft terms consent and a new click before preparing', async () => {
  const writes: { url: string; body: Record<string, unknown> }[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push({ url, body: JSON.parse(String(init.body)) })
    return response({ ...initial, game: 'MinecraftJava' })
  }))
  render(<FriendLocalHosting {...props} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const terms = screen.getByRole('checkbox', { name: /I have read and accept the terms/ })
  expect(terms).not.toBeChecked()
  fireEvent.click(terms)
  fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC/ }))
  expect(writes).toEqual([])
  fireEvent.click(screen.getByRole('button', { name: 'Continue and start' }))
  await waitFor(() => expect(writes).toEqual([{ url: `${base}/hosting/prepare`, body: {
    allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null, acceptedTerms: true
  } }]))
})

it('opens connection help before a local profile exists without a preparation or network mutation', async () => {
  const help = vi.fn()
  const fetcher = vi.fn(async () => response({ ...initial, consented: true, branchHash,
    remedy: { kind: 'Connection', title: 'Let friends connect to this PC', message: 'Turn on Friend connections in Connection help.' } }))
  vi.stubGlobal('fetch', fetcher)
  render(<FriendLocalHosting {...props} onOpenConnectionHelp={help} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  fireEvent.click(screen.getByRole('button', { name: 'Connection help' }))
  expect(help).toHaveBeenCalledExactlyOnceWith(profileId)
  expect(fetcher).toHaveBeenCalledOnce()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it.each([true, false])('offers working setup for busy ports only when a local profile exists (%s)', async hasProfile => {
  const setup = vi.fn()
  const fetcher = vi.fn(async () => response({ ...initial, consented: true, branchHash, localProfileId: hasProfile ? localProfileId : null,
    steps: initial.steps.map(step => step.key === 'ports' ? { ...step, state: 'ActionRequired' } : step) }))
  vi.stubGlobal('fetch', fetcher)
  render(<FriendLocalHosting {...props} onOpenGameSetup={setup} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  if (hasProfile) {
    fireEvent.click(screen.getByRole('button', { name: 'Open game setup' }))
    expect(setup).toHaveBeenCalledExactlyOnceWith('Valheim', localProfileId)
  } else {
    expect(screen.queryByRole('button', { name: 'Open game setup' })).not.toBeInTheDocument()
    expect(setup).not.toHaveBeenCalled()
  }
  expect(fetcher).toHaveBeenCalledOnce()
})

it.each(['connection changed', 'actions disabled'] as const)('cancels a pending Start instruction when %s', async reason => {
  let release!: (value: Response) => void
  const writes: string[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); return new Promise<Response>(resolve => { release = resolve }) }
    return response(initial)
  }))
  const view = render(<FriendLocalHosting {...props} onOpenHost={open} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC/ }))
  fireEvent.click(screen.getByRole('button', { name: 'Continue and start' }))
  if (reason === 'connection changed') {
    view.rerender(<FriendLocalHosting {...props} connectionId="second" onOpenHost={open} />)
    await screen.findByRole('button', { name: 'Host on this PC' })
    view.rerender(<FriendLocalHosting {...props} onOpenHost={open} />)
  } else view.rerender(<FriendLocalHosting {...props} onOpenHost={open} disabled />)
  await act(async () => { release(response(prepared)) })
  expect(writes).toEqual([`${base}/hosting/prepare`])
  expect(open).not.toHaveBeenCalled()
})

it('never starts or accepts or installs anything from polling', async () => {
  vi.useFakeTimers()
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => { if (init?.method) writes.push(url); return response(prepared) }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(40000) })
  expect(writes).toEqual([])
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it('shows the Start failure remedy and never retries it from a later ready poll', async () => {
  vi.useFakeTimers()
  const writes: string[] = []
  const setup = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) { writes.push(url); return response({ ok: false, code: 'PortInUse', message: 'A game port is busy. Open Setup and choose another port.' }) }
    return response(prepared)
  }))
  render(<FriendLocalHosting {...props} onOpenGameSetup={setup} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host on this PC' }))
  await act(async () => {})
  expect(screen.getByRole('heading', { name: 'Free the game ports on this PC' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Open game setup' })).toBeEnabled()
  await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
  expect(writes).toEqual([`${base}/hosting/start`])
  expect(setup).not.toHaveBeenCalled()
})

it('ignores a timed out preparation result without retrying Start', async () => {
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
  fireEvent.click(screen.getByRole('button', { name: 'Host on this PC' }))
  fireEvent.click(screen.getByRole('checkbox', { name: /Keep this save on my PC/ }))
  fireEvent.click(screen.getByRole('button', { name: 'Continue and start' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(120001) })
  expect(signal?.aborted).toBe(true)
  expect(screen.getByRole('button', { name: 'Retry automatic checks' })).toBeEnabled()
  expect(screen.getByRole('button', { name: 'Close Host on this PC' })).toBeEnabled()
  await act(async () => { release(response(prepared)); await vi.advanceTimersByTimeAsync(10000) })
  expect(writes).toEqual([`${base}/hosting/prepare`])
})

it('withdraws stale controls if a status read stalls and ignores its late response', async () => {
  vi.useFakeTimers()
  let reads = 0
  let release!: (value: Response) => void
  vi.stubGlobal('fetch', vi.fn(async () => ++reads === 1 ? response(initial) : new Promise<Response>(resolve => { release = resolve })))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host on this PC' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(25001) })
  expect(screen.getByRole('button', { name: 'Continue and start' })).toBeDisabled()
  await act(async () => { release(response(prepared)) })
  expect(screen.getByRole('button', { name: 'Continue and start' })).toBeDisabled()
})

it('keeps automatic results secondary and refuses unknown action destinations', async () => {
  expect(() => parseLocalHostingStatus({ ...prepared, remedy: { kind: 'https://untrusted', title: 'Click', message: 'Click' } })).toThrow()
  expect(localHostingAttention(initial)?.kind).toBe('ConfirmCopy')
  expect(localHostingAttention(prepared)).toBeNull()
  vi.stubGlobal('fetch', vi.fn(async () => response(initial)))
  render(<FriendLocalHosting {...props} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const summary = screen.getByText('Automatically checked results', { selector: 'summary' })
  expect(summary.closest('details')).not.toHaveAttribute('open')
  fireEvent.click(summary)
  expect(screen.getByText(/Incoming Friend access is unverified/)).toBeVisible()
  expect(screen.getByText(/Loading and saving in the real game and a friend joining from another network require separate testing/)).toBeVisible()
})

it.each([false, true])('carries only the explicit bounded Start instruction through a move (closed: %s)', async close => {
  vi.useFakeTimers()
  const requestId = '33333333-3333-4333-8333-333333333333'
  const move = { pending: false, code: 'RecipientPrepared', message: 'Waiting for the current Host.',
    successorDeviceId: localProfileId, requestId, recipientState: 'Prepared' as const,
    preparedVersionHash: branchHash, finalVersion: null, receiptConfirmed: false, canComplete: false, canCancel: false }
  let current: LocalHostingStatus = { ...initial, consented: true, handoffRequest: move }
  const writes: string[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (!init?.method) return response(current)
    writes.push(url)
    if (url.endsWith('/prepare')) {
      current = { ...prepared, localProfileId: profileId, branchHash: null,
        handoffRequest: { ...move, recipientState: 'Completed', receiptConfirmed: true } }
      return response(current)
    }
    return response({ ok: true, code: 'Starting', message: 'Start requested.' })
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host on this PC' }))
  expect(writes).toEqual([])
  expect(screen.getByText(/Start is requested for this move only/)).toBeInTheDocument()
  if (close) fireEvent.click(screen.getByRole('button', { name: 'Close Host on this PC' }))
  current = { ...current, handoffRequest: { ...move, recipientState: 'Completed', receiptConfirmed: true } }
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  if (close) { expect(writes).toEqual([]); expect(open).not.toHaveBeenCalled() }
  else {
    expect(writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
    expect(open).toHaveBeenCalledExactlyOnceWith(profileId)
  }
})

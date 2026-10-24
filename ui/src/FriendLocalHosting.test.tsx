import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { useLayoutEffect } from 'react'
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

it.each(['action', 'card'] as const)('waits for confirmed permission before offering the %s entry', async presentation => {
  const calls: string[] = []
  const writes: string[] = []
  let finishPermission!: (value: Response) => void
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    if (url.endsWith('/access')) return new Promise<Response>(resolve => { finishPermission = resolve })
    return response({ ...initial, needsServerFile: true, serverFile: null })
  })
  vi.stubGlobal('fetch', fetcher)
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Checking hosting access...')
  expect(screen.queryByRole('button', { name: 'Check hosting access again' })).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`])
  await act(async () => { finishPermission(response({ allowed: true, code: 'HostingAllowed', message: 'The owner allows hosting.' })) })
  const entry = screen.getByRole('button', { name: 'Host locally' })
  expect(entry).toHaveTextContent('Host locally')
  expect(entry).toBeEnabled()
  expect(calls).toEqual([`${base}/hosting/access`])
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  fireEvent.click(entry)
  const dialog = await screen.findByRole('dialog', { name: 'Host locally' })
  expect(await within(dialog).findByRole('heading', { name: 'Install or find the game server' })).toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting`])
  expect(calls).toHaveLength(2)
  expect(writes).toEqual([])
})

it.each(['action', 'card'] as const)('hides the %s entry after a failed permission lookup', async presentation => {
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    calls.push(url)
    if (url.endsWith('/access')) throw new Error('Permission lookup unavailable.')
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Hosting access could not be checked.')
  expect(screen.getByRole('button', { name: 'Check hosting access again' })).toBeEnabled()
  expect(calls).toEqual([`${base}/hosting/access`])
})

it.each(['network', 'http', 'malformed'] as const)('retries %s permission failures with only a fresh access read', async failure => {
  let accessReads = 0
  const calls: { url: string; method?: string; headers?: HeadersInit }[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method, headers: init?.headers })
    if (++accessReads > 1) return response({ allowed: true, code: 'HostingAllowed', message: 'The owner allows hosting.' })
    if (failure === 'network') throw new Error('Unavailable.')
    return failure === 'http' ? new Response(JSON.stringify({ code: 'AppBusy', message: 'Wait for this operation.' }), { status: 409 })
      : response({ allowed: 'true', code: 'HostingAllowed', message: 'Invalid permission.' })
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Hosting access could not be checked.')
  fireEvent.click(screen.getByRole('button', { name: 'Check hosting access again' }))
  await act(async () => {})
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  expect(screen.queryByRole('status', { name: 'Hosting access' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual(Array.from({ length: 2 }, () => ({ url: `${base}/hosting/access`, method: undefined,
    headers: { 'X-TogetherServer-Local': '1' } })))
  expect(open).not.toHaveBeenCalled()
})

it.each(['action', 'card'] as const)('keeps a timed-out %s entry hidden when the old permission result arrives late', async presentation => {
  vi.useFakeTimers()
  let finishPermission!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url); signal = init?.signal
    return new Promise<Response>(resolve => { finishPermission = resolve })
  }))
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(15001) })
  expect(signal?.aborted).toBe(true)
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Hosting access check timed out.')
  expect(screen.getByRole('button', { name: 'Check hosting access again' })).toBeEnabled()
  await act(async () => { finishPermission(response({ allowed: true, code: 'HostingAllowed', message: 'Sharing was on.' })) })
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`])
})

it.each(['Unknown', 'RosterUnavailable', 'SharingReviewRequired'])('waits for %s hosting access to recover before offering the entry', async code => {
  vi.useFakeTimers()
  let restored = false
  const calls: string[] = []
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    return response(restored ? { allowed: true, code: 'HostingAllowed', message: 'The owner allows hosting.' }
      : { allowed: false, code, message: 'The saved hosting information needs checking.' })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('The saved hosting information needs checking.')
  expect(screen.getByRole('button', { name: 'Check hosting access again' })).toBeEnabled()
  restored = true
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting/access`])
  expect(writes).toEqual([])
})

it('keeps a retry result when a timed-out access read returns allowed later', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let accessReads = 0
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return ++accessReads === 1 ? new Promise<Response>(resolve => { release = resolve })
      : response({ allowed: false, code: 'HostingPermissionRequired', message: 'The owner has not allowed hosting.' })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(15000) })
  fireEvent.click(screen.getByRole('button', { name: 'Check hosting access again' }))
  await act(async () => {})
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('The owner has not allowed hosting.')
  await act(async () => { release(response({ allowed: true, code: 'HostingAllowed', message: 'Old grant.' })) })
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('The owner has not allowed hosting.')
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(accessReads).toBe(2)
  expect(writes).toEqual([])
})

it.each(['profile', 'connection'] as const)('cancels a manual access retry when the selected %s changes', async change => {
  let release!: (value: Response) => void
  let retrySignal: AbortSignal | null | undefined
  let accessReads = 0
  const calls: string[] = []
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    if (++accessReads === 2) {
      retrySignal = init?.signal
      return new Promise<Response>(resolve => { release = resolve })
    }
    return response({ allowed: false, code: accessReads === 1 ? 'RosterUnavailable' : 'HostingPermissionRequired',
      message: accessReads === 1 ? 'Keep the Host reachable.' : 'This server is not shared with this PC.' })
  }))
  const view = render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Check hosting access again' }))
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Checking hosting access...')
  expect(screen.queryByRole('button', { name: 'Check hosting access again' })).not.toBeInTheDocument()
  const nextProfile = change === 'profile' ? '33333333-3333-4333-8333-333333333333' : profileId
  view.rerender(<FriendLocalHosting {...props} profileId={nextProfile} connectionId={change === 'connection' ? 'second' : 'first'} />)
  await act(async () => {})
  expect(retrySignal?.aborted).toBe(true)
  await act(async () => { release(response({ allowed: true, code: 'HostingAllowed', message: 'Old server grant.' })) })
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('This server is not shared with this PC.')
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting/access`,
    `/api/local/friend/${nextProfile}/shared-world/hosting/access`])
  expect(writes).toEqual([])
})

it.each(['unavailable', 'blocked'] as const)('keeps hosting access hidden and cancels its lookup when %s', async guard => {
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const fetcher = vi.fn(async (_url: string, init?: RequestInit) => {
    signal = init?.signal
    return new Promise<Response>(resolve => { release = resolve })
  })
  vi.stubGlobal('fetch', fetcher)
  const view = render(<FriendLocalHosting {...props} available={false} />)
  expect(view.container).toBeEmptyDOMElement()
  expect(fetcher).not.toHaveBeenCalled()
  view.rerender(<FriendLocalHosting {...props} />)
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Checking hosting access...')
  view.rerender(<FriendLocalHosting {...props} available={guard !== 'unavailable'} accessBlocked={guard === 'blocked'} />)
  expect(signal?.aborted).toBe(true)
  await act(async () => { release(response({ allowed: true, code: 'HostingAllowed', message: 'Late grant.' })) })
  expect(view.container).toBeEmptyDOMElement()
  expect(fetcher).toHaveBeenCalledOnce()
})

it.each(['RosterRollback', 'HostingPermissionRequired', 'RosterUnavailable'])('marks checks blocked by %s as not run instead of failed', async code => {
  const writes: string[] = []
  const blocked: LocalHostingStatus = { ...initial, ok: false, allowed: false, code,
    message: 'Current hosting access needs review.',
    steps: initial.steps.map(step => ({ ...step, state: 'ActionRequired' })) }
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(url.endsWith('/access') ? { allowed: true, code: 'HostingAllowed', message: 'Sharing was allowed.' } : blocked)
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const rows = within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')
  expect(within(rows[0]).getByText('Needs attention')).toBeInTheDocument()
  for (const row of rows.slice(1)) {
    expect(within(row).getByText('Not checked')).toBeInTheDocument()
    expect(within(row).getByText(/This check has not run in this attempt/)).toBeInTheDocument()
    expect(within(row).queryByText('Needs attention')).not.toBeInTheDocument()
  }
  fireEvent.click(within(rows[3]).getByRole('button'))
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  expect(guide.getByText(/The ports check has not run in this attempt/)).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  expect(writes).toEqual([])
})

it('reports an unchanged history failure after a pending retry, then shows the actual failed port check after access recovers', async () => {
  let release!: (value: Response) => void
  let pending = false
  const writes: string[] = []
  const blocked: LocalHostingStatus = { ...initial, ok: false, allowed: false, code: 'RosterRollback',
    message: 'The saved sharing history changed unexpectedly.',
    steps: initial.steps.map(step => ({ ...step, state: 'ActionRequired' })) }
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    return pending ? new Promise<Response>(resolve => { release = resolve }) : response(blocked)
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  expect(guide.getByRole('status')).toHaveTextContent('Check finished: sharing history needs review.')
  pending = true
  fireEvent.click(guide.getByRole('button', { name: 'Check hosting access again' }))
  expect(guide.getByRole('status')).toHaveTextContent('Checking hosting access and this PC.')
  expect(guide.getByRole('status')).toHaveTextContent('This check does not start the server.')
  expect(dialog.getByRole('button', { name: 'Checking this PC...' })).toBeDisabled()
  await act(async () => { release(response(blocked)) })
  expect(guide.getByRole('status')).toHaveTextContent('Check finished: sharing history needs review.')
  expect(guide.getByRole('button', { name: 'Check hosting access again' })).toBeEnabled()
  fireEvent.click(guide.getByRole('button', { name: 'Check hosting access again' }))
  await act(async () => { release(response({ ...prepared, ok: false, code: 'PortInUse', readyForManualStart: false,
    message: 'The required game port is in use.',
    steps: prepared.steps.map(step => step.key === 'ports' ? { ...step, state: 'ActionRequired', message: 'The required game port is in use.' } : step) })) })
  expect(guide.getByRole('status')).toHaveTextContent('Check finished. Next: Local game ports.')
  const rows = within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')
  expect(within(rows[3]).getByText('Needs attention')).toBeInTheDocument()
  expect(within(rows[3]).getByText('The required game port is in use.')).toBeInTheDocument()
  expect(within(rows[4]).getByText('Unverified')).toBeInTheDocument()
  expect(dialog.queryByText('Not checked')).not.toBeInTheDocument()
  expect(writes).toEqual([])
})

it('cancels preparation when the pending permission lookup returns an explicit denial', async () => {
  vi.useFakeTimers()
  let accessReads = 0
  let finishPermission!: (value: Response) => void
  let finishPreparation!: (value: Response) => void
  let preparationSignal: AbortSignal | null | undefined
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/access')) return ++accessReads === 1 ? response(initial)
      : new Promise<Response>(resolve => { finishPermission = resolve })
    if (init?.method) {
      writes.push(url); preparationSignal = init.signal
      return new Promise<Response>(resolve => { finishPreparation = resolve })
    }
    return response(initial)
  }))
  const open = vi.fn()
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  await act(async () => { finishPermission(response({ allowed: false, code: 'HostingPermissionRequired', message: 'The owner removed hosting access.' })) })
  expect(preparationSignal?.aborted).toBe(true)
  expect(screen.getByRole('heading', { name: 'Ask the owner to allow hosting' })).toBeInTheDocument()
  const guide = within(screen.getByRole('region', { name: 'Local hosting setup guide' }))
  fireEvent.click(guide.getByRole('button', { name: '1. Permission' }))
  expect(guide.getByRole('heading', { name: 'Ask the owner to allow shared hosting' })).toBeInTheDocument()
  expect(guide.queryByText('This PC is allowed to host')).not.toBeInTheDocument()
  await act(async () => { finishPreparation(response(prepared)) })
  expect(writes).toEqual([`${base}/hosting/prepare`])
  expect(open).not.toHaveBeenCalled()
})

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
  expect(calls).toEqual([{ url: `${base}/hosting/access` }, { url: `${base}/hosting` }])
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

it('explains preparation before confirmation, then preserves an actual save failure without starting', async () => {
  const unprepared: LocalHostingStatus = { ...initial, ok: false, code: 'LocalHostingPreparationRequired', consented: true,
    message: 'Choose Host locally to get the latest completed save and prepare this PC.',
    steps: initial.steps.map(step => step.key === 'save' ? { ...step, state: 'Unknown' } : step) }
  const failed: LocalHostingStatus = { ...unprepared, code: 'SharedChunkUnavailable',
    message: 'The source no longer has a required part of this save. Ask it to share a completed backup again.',
    remedy: { kind: 'GetSave', title: 'Get a completed save', message: 'Generic save advice.' } }
  let latest = unprepared
  const calls: { url: string; method?: string }[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method })
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'The owner allows hosting.' })
    if (url.endsWith('/prepare')) latest = failed
    return response(latest)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} onReviewSharedSave={vi.fn()} />)
  const dialog = await openHosting()
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  expect(guide.getByRole('heading', { name: 'Prepare this PC\'s hosting copy' })).toBeInTheDocument()
  expect(guide.getByText(/receives it if needed and prepares a separate local copy/)).toBeInTheDocument()
  expect(dialog.queryByText(/interrupted transfer|stop the game server safely/i)).not.toBeInTheDocument()
  expect(calls).toEqual([{ url: `${base}/hosting/access`, method: undefined }, { url: `${base}/hosting`, method: undefined }])
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  expect(open).not.toHaveBeenCalled()
  await confirmHosting()
  await waitFor(() => expect(guide.getByRole('heading', { name: 'Get a completed verified save' })).toBeInTheDocument())
  expect(within(dialog.getByRole('region', { name: 'Next hosting step' })).getByText(failed.message)).toBeInTheDocument()
  expect(guide.getByText(failed.message)).toBeInTheDocument()
  expect(guide.getByRole('button', { name: 'Review the shared saves' })).toBeEnabled()
  expect(guide.getByRole('button', { name: 'Recheck the save' })).toBeEnabled()
  fireEvent.click(guide.getByRole('button', { name: 'Recheck the save' }))
  await act(async () => {})
  expect(calls.filter(call => call.method)).toEqual([{ url: `${base}/hosting/prepare`, method: 'POST' }])
  expect(calls.at(-1)).toEqual({ url: `${base}/hosting`, method: undefined })
  expect(open).not.toHaveBeenCalled()
})

it.each(['action', 'card'] as const)('hides the %s affordance when permission is denied', async presentation => {
  const fetcher = vi.fn(async () => response({ ...initial, allowed: false, code: 'HostingPermissionRequired' }))
  vi.stubGlobal('fetch', fetcher)
  const view = render(<FriendLocalHosting {...props} presentation={presentation} />)
  await waitFor(() => expect(fetcher).toHaveBeenCalledOnce())
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  await waitFor(() => expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent(initial.message))
  expect(screen.queryByRole('button', { name: 'Check hosting access again' })).not.toBeInTheDocument()
  view.rerender(<FriendLocalHosting {...props} presentation={presentation} accessBlocked />)
  expect(fetcher).toHaveBeenCalledOnce()
})

it.each(['action', 'card'] as const)('updates the %s entry when the owner enables then disables hosting without making writes', async presentation => {
  vi.useFakeTimers()
  let enabled = false
  const calls: string[] = []
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    return response({ allowed: enabled, code: enabled ? 'HostingAllowed' : 'HostingPermissionRequired',
      message: enabled ? 'The owner allows hosting.' : 'Shared hosting is off.' })
  }))
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  enabled = true
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  enabled = false
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(screen.getByRole('status', { name: 'Hosting access' })).toHaveTextContent('Shared hosting is off.')
  expect(screen.queryByRole('button', { name: 'Check hosting access again' })).not.toBeInTheDocument()
  expect(calls).toEqual(Array<string>(3).fill(`${base}/hosting/access`))
  expect(writes).toEqual([])
})

it.each(['profile', 'connection'] as const)('does not carry confirmed permission across a %s change before reset effects run', async change => {
  vi.useFakeTimers()
  let finishPermission!: (value: Response) => void
  let accessReads = 0
  const calls: string[] = []
  const writes: string[] = []
  const entryAtCommit: boolean[] = []
  const continueAtCommit: (boolean | null)[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push(url)
    if (init?.method) writes.push(url)
    if (!url.endsWith('/access')) return response(initial)
    return ++accessReads === 1 ? response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
      : new Promise<Response>(resolve => { finishPermission = resolve })
  }))
  function ScopeProbe({ nextProfile = profileId, nextConnection = 'first' }: { nextProfile?: string; nextConnection?: string }) {
    useLayoutEffect(() => {
      const buttons = screen.queryAllByRole('button', { name: 'Host locally' })
      entryAtCommit.push(buttons.some(button => !button.closest('dialog, [role="dialog"]')))
      const confirm = buttons.find(button => button.closest('dialog, [role="dialog"]'))
      continueAtCommit.push(confirm ? !(confirm as HTMLButtonElement).disabled : null)
    }, [nextProfile, nextConnection])
    return <FriendLocalHosting {...props} profileId={nextProfile} connectionId={nextConnection} />
  }
  const view = render(<ScopeProbe />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' })).toBeEnabled()
  const nextProfile = change === 'profile' ? '33333333-3333-4333-8333-333333333333' : profileId
  view.rerender(<ScopeProbe nextProfile={nextProfile} nextConnection={change === 'connection' ? 'second' : 'first'} />)
  expect(entryAtCommit).toEqual([false, false])
  expect(continueAtCommit).toEqual([null, false])
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  await act(async () => { finishPermission(response({ allowed: false, code: 'HostingPermissionRequired', message: 'Shared hosting is off.' })) })
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting`, `/api/local/friend/${nextProfile}/shared-world/hosting/access`])
  expect(writes).toEqual([])
})

it.each(['action', 'card'] as const)('keeps an open guide recoverable after the %s entry loses permission', async presentation => {
  vi.useFakeTimers()
  let enabled = true
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(url.endsWith('/access') ? { allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' }
      : enabled ? initial : { ...initial, ok: false, allowed: false, code: 'HostingPermissionRequired', message: 'The owner disabled shared hosting.' })
  }))
  render(<FriendLocalHosting {...props} presentation={presentation} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog', { name: 'Host locally' }))
  enabled = false
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  const confirm = dialog.getByRole('button', { name: 'Host locally' })
  expect(confirm).toBeDisabled()
  expect(screen.getAllByRole('button', { name: 'Host locally' })).toEqual([confirm])
  expect(screen.queryByRole('region', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(dialog.getByRole('heading', { name: 'Ask the owner to allow hosting' })).toBeInTheDocument()
  expect(dialog.getByRole('region', { name: 'Local hosting setup guide' })).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Retry automatic checks' })).toBeEnabled()
  enabled = true
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await act(async () => {})
  expect(confirm).toBeEnabled()
  expect(writes).toEqual([])
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

it.each([true, false])('opens the server picker from the guide and reports its result without starting (selected: %s)', async selected => {
  let finishBrowse!: (value: string | null) => void
  const writes: string[] = []
  const missing: LocalHostingStatus = { ...initial, needsServerFile: true, serverFile: null,
    steps: initial.steps.map(step => step.key === 'server' ? { ...step, state: 'ActionRequired' } : step) }
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    return response(missing)
  }))
  const browse = vi.fn(() => new Promise<string | null>(resolve => { finishBrowse = resolve }))
  render(<FriendLocalHosting {...props} onBrowseServerFile={browse} />)
  const dialog = await openHosting()
  fireEvent.change(dialog.getByRole('textbox', { name: 'Installed server file' }), { target: { value: 'C:/Previous/valheim_server.exe' } })
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  fireEvent.click(guide.getByRole('button', { name: 'Choose the installed server file' }))
  expect(browse).toHaveBeenCalledExactlyOnceWith('Valheim')
  expect(guide.getByRole('status')).toHaveTextContent('Choose the installed dedicated server file in the file picker.')
  expect(guide.getByRole('button', { name: 'Choose the installed server file' })).toBeDisabled()
  expect(dialog.getByRole('button', { name: 'Choosing server file...' })).toBeDisabled()
  await act(async () => { finishBrowse(selected ? 'C:/Selected/valheim_server.exe' : null) })
  expect(dialog.getByRole('textbox', { name: 'Installed server file' })).toHaveValue(selected ? 'C:/Selected/valheim_server.exe' : 'C:/Previous/valheim_server.exe')
  expect(guide.getByRole('status')).toHaveTextContent(selected ? 'Server file selected. Choose Host locally to verify it' : 'No server file was selected. Your previous choice is kept.')
  expect(guide.getByRole('button', { name: 'Choose the installed server file' })).toBeEnabled()
  expect(writes).toEqual([])
})

it('explains manual file entry when the server picker is unavailable', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => response({ ...initial, needsServerFile: true, serverFile: null,
    steps: initial.steps.map(step => step.key === 'server' ? { ...step, state: 'ActionRequired' } : step) })))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const guide = within(dialog.getByRole('region', { name: 'Local hosting setup guide' }))
  fireEvent.click(guide.getByRole('button', { name: 'Choose the installed server file' }))
  expect(dialog.getByRole('textbox', { name: 'Installed server file' })).toHaveFocus()
  expect(guide.getByRole('status')).toHaveTextContent('Enter the installed server file above, then choose Host locally.')
  expect(guide.getByRole('status')).toHaveTextContent('The file is checked before Start.')
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

it('shows acceptance progress, then a retryable timeout without accepting or starting again from a late reply', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const invitation: LocalHostingStatus = { ...initial, handoffRequest: {
    pending: true, code: 'WaitingForRecipient', message: 'Waiting for this PC.',
    successorDeviceId: '33333333-3333-4333-8333-333333333333', requestId: '44444444-4444-4444-8444-444444444444',
    recipientState: 'Requested', preparedVersionHash: null, finalVersion: null,
    receiptConfirmed: false, canComplete: false, canCancel: true
  } }
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) {
      writes.push(url); signal = init.signal
      return new Promise<Response>(resolve => { release = resolve })
    }
    return response(invitation)
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  fireEvent.click(dialog.getByRole('button', { name: 'Host locally' }))
  expect(dialog.getByRole('button', { name: 'Accepting move...' })).toBeDisabled()
  expect(within(dialog.getByRole('region', { name: 'Local hosting setup guide' })).getByRole('status')).toHaveTextContent('Accepting the hosting move')
  await act(async () => { await vi.advanceTimersByTimeAsync(15000) })
  expect(signal?.aborted).toBe(true)
  expect(dialog.getByRole('alert')).toHaveTextContent('Retry the checks to see whether it was accepted before responding again.')
  expect(dialog.getByRole('button', { name: 'Retry automatic checks' })).toBeEnabled()
  await act(async () => {
    release(response({ ok: true, code: 'HostingAccepted', message: 'Late acceptance.' }))
    await vi.advanceTimersByTimeAsync(10000)
  })
  expect(writes).toEqual([`${base}/handoff/respond`])
  expect(dialog.queryByText('Late acceptance.')).not.toBeInTheDocument()
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

it('keeps Start busy beyond 30 seconds and accepts a delayed reply exactly once', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/start')) {
      signal = init?.signal
      return new Promise<Response>(resolve => { release = resolve })
    }
    return response(prepared)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  expect(writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
  await act(async () => { await vi.advanceTimersByTimeAsync(31000) })
  expect(signal?.aborted).toBe(false)
  expect(screen.getByRole('dialog')).toHaveAttribute('aria-busy', 'true')
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Requesting Start...' })).toBeDisabled()
  expect(within(screen.getByRole('region', { name: 'Local hosting setup guide' })).getByRole('status')).toHaveTextContent('Requesting Start for the checked local save.')
  expect(open).not.toHaveBeenCalled()
  expect(writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
  await act(async () => { release(response({ ok: true, code: 'Starting', message: 'Start requested.' })) })
  expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId)
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  await act(async () => { await vi.advanceTimersByTimeAsync(120001) })
  expect(signal?.aborted).toBe(false)
  expect(open).toHaveBeenCalledExactlyOnceWith(localProfileId)
  expect(writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
})

it.each([true, false])('expires Start at exactly 120 seconds and ignores a late reply without retrying (success: %s)', async ok => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  let signal: AbortSignal | null | undefined
  const writes: string[] = []
  const open = vi.fn()
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/start')) {
      signal = init?.signal
      return new Promise<Response>(resolve => { release = resolve })
    }
    return response(prepared)
  }))
  render(<FriendLocalHosting {...props} onOpenHost={open} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  expect(writes).toEqual([`${base}/hosting/prepare`, `${base}/hosting/start`])
  await act(async () => { await vi.advanceTimersByTimeAsync(119999) })
  expect(signal?.aborted).toBe(false)
  expect(screen.getByRole('dialog')).toHaveAttribute('aria-busy', 'true')
  await act(async () => { await vi.advanceTimersByTimeAsync(1) })
  expect(signal?.aborted).toBe(true)
  expect(screen.getByRole('dialog')).not.toHaveAttribute('aria-busy', 'true')
  const timeoutMessage = /The Start result could not be checked in time. Open local servers to check this PC before trying again./
  expect(screen.getByText(timeoutMessage)).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Retry automatic checks' })).toBeEnabled()
  await act(async () => {
    release(response({ ok, code: ok ? 'Starting' : 'PortInUse', message: 'Late result.' }))
    await vi.advanceTimersByTimeAsync(30000)
  })
  expect(screen.getByText(timeoutMessage)).toBeInTheDocument()
  expect(open).not.toHaveBeenCalled()
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
  expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Preparing this PC...' })).toBeDisabled()
  expect(within(screen.getByRole('region', { name: 'Local hosting setup guide' })).getByRole('status')).toHaveTextContent('Getting the latest completed save')
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

it('always shows all automatic check results and rejects unreviewed action destinations', async () => {
  expect(() => parseLocalHostingStatus({ ...prepared, remedy: { kind: 'https://untrusted', title: 'Click', message: 'Click' } })).toThrow()
  expect(localHostingAttention(initial)).toBeNull()
  expect(localHostingAttention(prepared)).toBeNull()
  vi.stubGlobal('fetch', vi.fn(async () => response(initial)))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  expect(dialog.getByRole('heading', { name: 'Automatically checked results' }).closest('details')).toBeNull()
  expect(within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')).toHaveLength(5)
  expect(dialog.getByText('Last checked 0s ago')).toBeInTheDocument()
})

it('retains the same check rows and timestamp through a failed retry, then recovers on reopen without waiting for a permission poll', async () => {
  let result: 'ready' | 'waiting' | 'error' = 'ready'
  let release!: (value: Response) => void
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    if (result === 'error') return new Response(JSON.stringify({ code: 'HostUnavailable', message: 'The Host could not be reached.' }), { status: 503 })
    if (result === 'waiting') return new Promise<Response>(resolve => { release = resolve })
    return response(initial)
  }))
  render(<FriendLocalHosting {...props} />)
  const dialog = await openHosting()
  const list = dialog.getByRole('list', { name: 'Local hosting checks' })
  const rows = within(list).getAllByRole('listitem')
  const checked = dialog.getByText(/Last checked/).getAttribute('datetime')
  result = 'waiting'
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  expect(within(list).getAllByRole('listitem')).toEqual(rows)
  expect(dialog.getByText(/Checking again/)).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Checking this PC...' })).toBeDisabled()
  await act(async () => { release(response(initial)) })
  result = 'error'
  fireEvent.click(dialog.getByRole('button', { name: 'Retry automatic checks' }))
  await waitFor(() => expect(dialog.getByText('The Host could not be reached.')).toBeInTheDocument())
  expect(within(list).getAllByRole('listitem')).toEqual(rows)
  expect(dialog.getByText(/Showing the last results/)).toBeInTheDocument()
  expect(within(dialog.getByRole('region', { name: 'Local hosting setup guide' })).getByRole('status')).toHaveTextContent('The checks could not finish.')
  expect(dialog.getByText(/Last checked/)).toHaveAttribute('datetime')
  expect(checked).toBeTruthy()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Close Host locally' }))
  expect(screen.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  result = 'ready'
  const reopened = await openHosting()
  expect(reopened.getByRole('button', { name: 'Host locally' })).toBeEnabled()
  expect(writes).toEqual([])
})

it('keeps the list visible on the first pending read and updates a last-checked timer without hiding it', async () => {
  vi.useFakeTimers()
  let release!: (value: Response) => void
  vi.stubGlobal('fetch', vi.fn(async (url: string) => url.endsWith('/access')
    ? response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    : new Promise<Response>(resolve => { release = resolve })))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  const dialog = within(screen.getByRole('dialog'))
  const rows = within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')
  expect(rows).toHaveLength(5)
  expect(dialog.getByText(/Waiting for the first check/)).toBeInTheDocument()
  await act(async () => { release(response(initial)) })
  await act(async () => { await vi.advanceTimersByTimeAsync(3000) })
  expect(dialog.getByText('Last checked 3s ago')).toBeInTheDocument()
  expect(within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')).toEqual(rows)
})

it('preserves displayed checks after an automatic timeout and ignores a late result', async () => {
  vi.useFakeTimers()
  let reads = 0
  let release!: (value: Response) => void
  const writes: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) writes.push(url)
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    return ++reads === 1 ? response(initial) : new Promise<Response>(resolve => { release = resolve })
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  const rows = within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')
  await act(async () => { await vi.advanceTimersByTimeAsync(25001) })
  expect(within(dialog.getByRole('list', { name: 'Local hosting checks' })).getAllByRole('listitem')).toEqual(rows)
  expect(dialog.getByText(/Showing the last results/)).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  await act(async () => { release(response(prepared)) })
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeDisabled()
  expect(writes).toEqual([])
})

it.each(['failed', 'timed out'])('keeps the entry hidden when a later permission read %s after an earlier denial', async failure => {
  vi.useFakeTimers()
  let accessReads = 0
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    calls.push(url)
    if (!url.endsWith('/access')) return response(initial)
    if (++accessReads === 1) return response({ allowed: false, code: 'HostingPermissionRequired', message: 'Sharing was off.' })
    if (failure === 'timed out') return new Promise<Response>(() => {})
    throw new Error('The current permission is unavailable.')
  }))
  render(<FriendLocalHosting {...props} />)
  await act(async () => {})
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  await act(async () => { await vi.advanceTimersByTimeAsync(failure === 'timed out' ? 25001 : 10001) })
  expect(screen.queryByRole('button', { name: 'Host locally' })).not.toBeInTheDocument()
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  expect(calls).toEqual([`${base}/hosting/access`, `${base}/hosting/access`])
})

it('waits for an in-flight automatic check before letting Browse change the selected server', async () => {
  vi.useFakeTimers()
  let statusReads = 0
  let release!: (value: Response) => void
  const missing = { ...initial, needsServerFile: true, serverFile: null }
  const browse = vi.fn(async () => 'C:/Selected/valheim_server.exe')
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    if (url.endsWith('/access')) return response({ allowed: true, code: 'HostingAllowed', message: 'Sharing is on.' })
    if (++statusReads === 1) return response(missing)
    return new Promise<Response>(resolve => { release = resolve })
  }))
  render(<FriendLocalHosting {...props} onBrowseServerFile={browse} />)
  await act(async () => {})
  fireEvent.click(screen.getByRole('button', { name: 'Host locally' }))
  await act(async () => {})
  const dialog = within(screen.getByRole('dialog'))
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(dialog.getByRole('button', { name: 'Browse' })).toBeDisabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Browse' }))
  expect(browse).not.toHaveBeenCalled()
  await act(async () => { release(response(missing)) })
  expect(dialog.getByRole('button', { name: 'Browse' })).toBeEnabled()
  fireEvent.click(dialog.getByRole('button', { name: 'Browse' }))
  await act(async () => {})
  expect(browse).toHaveBeenCalledOnce()
  expect(dialog.getByRole('button', { name: 'Host locally' })).toBeEnabled()
})

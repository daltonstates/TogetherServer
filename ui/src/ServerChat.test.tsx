import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { parseChatRoomSummary, parseManagedChatRoom, PinnedNoticePreview, ServerChat,
  ServerChatCardSummary, unreadMessageCount, type ChatDraftState } from './ServerChat'
import { parseChatRoomView } from './contracts'
import { parseChatRoomWithNotice, type PinnedServerNotice } from './pinnedNoticeContracts'

const protectedDrafts = vi.hoisted(() => ({
  readProtectedDraft: vi.fn(), saveProtectedDraft: vi.fn(), clearProtectedDraft: vi.fn()
}))
vi.mock('./protectedUiDrafts', () => protectedDrafts)

const profileId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
const deviceId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
const hostId = 'cccccccc-cccc-cccc-cccc-cccccccccccc'
const entry = { id: 'dddddddd-dddd-dddd-dddd-dddddddddddd', hostId, profileId,
  authorId: deviceId, author: 'Friend', sentUtc: '2026-10-04T09:00:00Z',
  text: 'The server is restarting', signature: 'signed-copy' }
function room(allowed = true, entries = [entry]) {
  return { ok: true, code: 'ChatReady', message: 'Messages are copied.', hostId,
    profileId, entries, pending: [], members: [{ deviceId, name: 'Friend PC', allowed }] }
}
const notice: PinnedServerNotice = { hostId, profileId, revision: 1, updatedUtc: '2026-10-04T09:00:00+00:00',
  text: 'Leave spawn clear.\nMaintenance tonight.', signature: 'A'.repeat(86) + '==' }
function noticeRoom(current: typeof notice | null = notice, cached = false) {
  return { ...room(), notice: current, noticeSupported: true, noticeCached: cached }
}

beforeEach(() => {
  localStorage.clear()
  protectedDrafts.readProtectedDraft.mockReset().mockResolvedValue({ ok: true, text: null, revision: 0, message: '' })
  protectedDrafts.saveProtectedDraft.mockReset().mockImplementation(async (_identity, text, revision) =>
    ({ ok: true, text, revision: revision + 1, message: '' }))
  protectedDrafts.clearProtectedDraft.mockReset().mockImplementation(async (_identity, revision) =>
    ({ ok: true, text: null, revision: revision + 1, message: '' }))
})
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('ServerChat', () => {
  it('shows copied messages and lets the Host remove a room member and send an issue update', async () => {
    const requests: Array<{ path: string; method: string; body: string }> = []
    vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
      requests.push({ path, method: init?.method ?? 'GET', body: String(init?.body ?? '') })
      if (path.endsWith(`/members/${deviceId}`)) return Response.json(room(false))
      if (path.endsWith('/messages')) return Response.json(room(false,
        [entry, { ...entry, id: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee', author: 'Host', text: 'Restart is complete' }]))
      return Response.json(room())
    }))
    render(<ServerChat profileId={profileId} host visible />)
    expect(await screen.findByText('The server is restarting')).toBeInTheDocument()
    fireEvent.click(screen.getByText(/People in this room/))
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }))
    await waitFor(() => expect(requests.some(request => request.method === 'PUT' &&
      request.path.endsWith(`/members/${deviceId}`) && request.body.includes('"allowed":false'))).toBe(true))
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Restart is complete' } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    expect(await screen.findByText('Restart is complete')).toBeInTheDocument()
    expect(requests.some(request => request.path.endsWith('/messages') &&
      request.body.includes('Restart is complete'))).toBe(true)
  })

  it('rejects over limit room payloads and does not call an older Host', () => {
    expect(() => parseChatRoomView({ ...room(), entries: [{ ...entry, text: 'x'.repeat(501) }] }))
      .toThrow(/chat limits/)
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    render(<ServerChat profileId={profileId} host={false} visible supported={false} />)
    expect(screen.getByText(/Update the Host app/)).toBeInTheDocument()
    expect(fetch).not.toHaveBeenCalled()
  })

  it('lets only the Host edit and clear a pinned notice with the revision opened in the editor', async () => {
    const requests: Array<{ path: string; method: string; body: string }> = []
    let current = noticeRoom()
    vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
      requests.push({ path, method: init?.method ?? 'GET', body: String(init?.body ?? '') })
      if (path.endsWith('/notice')) {
        const change = JSON.parse(String(init.body)) as { text: string | null; expectedRevision: number }
        current = { ...current, notice: { ...notice, revision: change.expectedRevision + 1,
          text: change.text } }
      }
      return Response.json(current)
    }))
    render(<ServerChat profileId={profileId} host visible />)
    expect(await screen.findByText(/Leave spawn clear/)).toHaveTextContent('Maintenance tonight.')
    fireEvent.click(screen.getByRole('button', { name: 'Edit notice' }))
    fireEvent.change(screen.getByLabelText('Notice text'), { target: { value: 'Rules have changed.' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save notice' }))
    expect(await screen.findByText('Rules have changed.', { selector: 'p' })).toBeInTheDocument()
    expect(requests.some(request => request.path.endsWith('/notice') && request.method === 'PUT' &&
      JSON.parse(request.body).expectedRevision === 1)).toBe(true)
    fireEvent.click(screen.getByRole('button', { name: 'Edit notice' }))
    fireEvent.click(screen.getByRole('button', { name: 'Clear notice' }))
    expect(await screen.findByText('The Host cleared the notice.')).toBeInTheDocument()
    expect(requests.some(request => request.path.endsWith('/notice') &&
      JSON.parse(request.body).text === null && JSON.parse(request.body).expectedRevision === 2)).toBe(true)
    expect(screen.queryByText('Rules have changed.')).not.toBeInTheDocument()
  })

  it('shows a cached Friend notice as plain text and offers no notice edit or clear', async () => {
    const markupNotice = { ...notice, text: '<script>unsafe()</script>\nServer rules' }
    const fetch = vi.fn(async (path: string) => {
      expect(path).toContain('/sync')
      return Response.json(noticeRoom(markupNotice, true))
    })
    vi.stubGlobal('fetch', fetch)
    const { container } = render(<ServerChat profileId={profileId} host={false} visible />)
    expect(await screen.findByText(/<script>unsafe/)).toBeInTheDocument()
    expect(screen.getByText(/Cached on this PC/)).toBeInTheDocument()
    expect(container.querySelector('script')).toBeNull()
    expect(screen.queryByRole('button', { name: /Edit notice|Add notice|Clear notice/ })).not.toBeInTheDocument()
    expect(fetch.mock.calls.every(call => String(call[0]).endsWith('/sync'))).toBe(true)
  })

  it('marks the last verified Friend notice cached when a later sync fails', async () => {
    let failed = false
    vi.stubGlobal('fetch', vi.fn(async () => {
      if (failed) throw new Error('offline')
      return Response.json(noticeRoom())
    }))
    render(<ServerChat profileId={profileId} host={false} visible />)
    await screen.findByText(/Leave spawn clear/)
    expect(screen.queryByText(/Cached on this PC/)).not.toBeInTheDocument()
    failed = true
    fireEvent.click(screen.getByRole('button', { name: 'Sync now' }))
    expect(await screen.findByText(/Cached on this PC/)).toBeInTheDocument()
    expect(screen.getByText(/Leave spawn clear/)).toBeInTheDocument()
  })

  it('keeps the opened revision and typed edit during refresh and a conflict response', async () => {
    let current = noticeRoom()
    let submitted: { text: string; expectedRevision: number } | undefined
    vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
      if (path.endsWith('/notice')) {
        submitted = JSON.parse(String(init.body))
        return Response.json({ code: 'NoticeChanged', message: 'Reload the pinned notice before saving.' }, { status: 409 })
      }
      return Response.json(current)
    }))
    render(<ServerChat profileId={profileId} host visible />)
    await screen.findByText(/Leave spawn clear/)
    fireEvent.click(screen.getByRole('button', { name: 'Edit notice' }))
    fireEvent.change(screen.getByLabelText('Notice text'), { target: { value: 'Unsaved owner edit' } })
    current = noticeRoom({ ...notice, revision: 2, text: 'Another owner edit' })
    fireEvent.click(screen.getByRole('button', { name: 'Sync now' }))
    await screen.findByText('Another owner edit')
    expect(screen.getByLabelText('Notice text')).toHaveValue('Unsaved owner edit')
    fireEvent.click(screen.getByRole('button', { name: 'Save notice' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Reload the pinned notice')
    expect(submitted).toEqual({ text: 'Unsaved owner edit', expectedRevision: 1 })
    expect(screen.getByLabelText('Notice text')).toHaveValue('Unsaved owner edit')
  })

  it('can add a notice to an empty room and blocks empty, control, and oversized editor text', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(noticeRoom(null))))
    render(<ServerChat profileId={profileId} host visible />)
    fireEvent.click(await screen.findByRole('button', { name: 'Add notice' }))
    const input = screen.getByLabelText('Notice text')
    expect(screen.getByRole('button', { name: 'Save notice' })).toBeDisabled()
    expect(input).toHaveAttribute('maxlength', '2000')
    for (const value of [' \n ', 'x'.repeat(2001), 'Hidden\u202evalue']) {
      fireEvent.change(input, { target: { value } })
      expect(screen.getByRole('button', { name: 'Save notice' })).toBeDisabled()
    }
    fireEvent.change(input, { target: { value: 'New rules' } })
    expect(screen.getByRole('button', { name: 'Save notice' })).toBeEnabled()
  })

  it('keeps chat usable with older notice-unaware peers and labels the update boundary', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ ...room(), code: 'ChatSynced' })))
    render(<ServerChat profileId={profileId} host={false} visible />)
    expect(await screen.findByText(/The server is restarting/)).toBeInTheDocument()
    expect(screen.getByText(/Update the Host app to see current pinned notices/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add notice' })).not.toBeInTheDocument()
  })

  it('discards the previous room notice when the selected server changes and refuses a wrong-room reply', async () => {
    const otherProfile = 'ffffffff-ffff-ffff-ffff-ffffffffffff'
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(noticeRoom())))
    const { rerender } = render(<ServerChat profileId={profileId} host visible />)
    await screen.findByText(/Leave spawn clear/)
    rerender(<ServerChat profileId={otherProfile} host visible />)
    expect(screen.queryByText(/Leave spawn clear/)).not.toBeInTheDocument()
    expect(await screen.findByRole('alert')).toHaveTextContent('different server room')
    expect(screen.queryByRole('button', { name: 'Edit notice' })).not.toBeInTheDocument()
  })
})

describe('pinned notice decoding', () => {
  it('accepts old peer payloads, signed clears, and old current notices independently of chat retention', () => {
    expect(parseChatRoomWithNotice(room()).notice).toBeNull()
    expect(parseChatRoomWithNotice(room()).noticeSupported).toBe(false)
    expect(parseChatRoomWithNotice(noticeRoom({ ...notice, text: null })).notice?.text).toBeNull()
    expect(parseChatRoomWithNotice(noticeRoom({ ...notice, updatedUtc: '2020-01-01T00:00:00Z' })).notice?.text)
      .toBe(notice.text)
  })

  it.each([
    { text: 'x'.repeat(2001) }, { text: '' }, { text: '   ' }, { text: 'bad\0text' },
    { text: 'bad\u202eformat' }, { text: 3 }, { text: undefined }, { revision: 0 },
    { revision: 1.5 }, { revision: Number.MAX_SAFE_INTEGER + 1 }, { revision: '1' },
    { profileId: deviceId }, { hostId: deviceId }, { hostId: 'bad-host' },
    { updatedUtc: 'yesterday' }, { updatedUtc: '2020-01-01T00:00:00-05:00' },
    { signature: '' }, { signature: 'forged-copy' }, { signature: 'x'.repeat(201) }
  ])('rejects malformed or different-room notice fields %#', patch => {
    expect(() => parseChatRoomWithNotice({ ...noticeRoom(), notice: { ...notice, ...patch } })).toThrow()
  })

  it('rejects malformed cache/support flags and future dates', () => {
    expect(() => parseChatRoomWithNotice({ ...noticeRoom(), noticeCached: 'yes' })).toThrow(/boolean/)
    expect(() => parseChatRoomWithNotice({ ...noticeRoom(), noticeSupported: null })).toThrow(/boolean/)
    expect(() => parseChatRoomWithNotice({ ...noticeRoom(), noticeSupported: false })).toThrow(/cached copy/)
    expect(() => parseChatRoomWithNotice(noticeRoom({ ...notice,
      updatedUtc: new Date(Date.now() + 60 * 60_000).toISOString() }))).toThrow(/UTC time/)
  })
})

describe('protected unfinished messages', () => {
  it('keeps draft loading usable when React StrictMode replays effect setup', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(room())))
    render(<StrictMode><ServerChat profileId={profileId} host visible /></StrictMode>)
    await screen.findByLabelText('Message')
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    expect(protectedDrafts.readProtectedDraft).toHaveBeenCalledTimes(2)
    expect((protectedDrafts.readProtectedDraft.mock.calls[0][1] as AbortSignal).aborted).toBe(true)
    expect((protectedDrafts.readProtectedDraft.mock.calls[1][1] as AbortSignal).aborted).toBe(false)
  })

  it('requires an explicit review before recovering a message and never writes the initial empty composer', async () => {
    protectedDrafts.readProtectedDraft.mockResolvedValue({ ok: true, text: 'Private unfinished question', revision: 4, message: '' })
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(room())))
    render(<ServerChat profileId={profileId} host visible />)
    const resume = await screen.findByRole('button', { name: 'Use recovered message' })
    expect(screen.getByLabelText('Message')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()
    expect(protectedDrafts.saveProtectedDraft).not.toHaveBeenCalled()
    fireEvent.click(resume)
    expect(screen.getByLabelText('Message')).toHaveValue('Private unfinished question')
    vi.useFakeTimers()
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Reviewed private question' } })
    await act(async () => { await vi.advanceTimersByTimeAsync(700) })
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenCalledWith(
      { purpose: 'chat', profileId, connectionId: null, key: 'compose' }, 'Reviewed private question', 4, expect.any(AbortSignal))
    expect(localStorage.length).toBe(1)
    expect(localStorage.getItem(localStorage.key(0)!)).toBe(entry.id)
  })

  it('debounces saves and serializes a later edit behind the returned revision', async () => {
    let finish: (value: { ok: boolean; text: string; revision: number; message: string }) => void = () => undefined
    protectedDrafts.saveProtectedDraft.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(room())))
    render(<ServerChat profileId={profileId} host visible />)
    await screen.findByLabelText('Message')
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    vi.useFakeTimers()
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'First' } })
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'First edit' } })
    await act(async () => { await vi.advanceTimersByTimeAsync(700) })
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenCalledTimes(1)
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Later edit' } })
    await act(async () => { await vi.advanceTimersByTimeAsync(700) })
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenCalledTimes(1)
    await act(async () => { finish({ ok: true, text: 'First edit', revision: 1, message: '' }) })
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenNthCalledWith(2,
      expect.objectContaining({ purpose: 'chat' }), 'Later edit', 1, expect.any(AbortSignal))
  })

  it('keeps a rejected message and clears the protected composer only after a successful post', async () => {
    let accepts = false
    vi.stubGlobal('fetch', vi.fn(async (path: string) => Response.json(path.endsWith('/messages') && !accepts
      ? { ...room(), ok: false, code: 'ChatQueueFull', message: 'Send queued messages before adding more.' } : room())))
    render(<ServerChat profileId={profileId} host visible />)
    await screen.findByLabelText('Message')
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Keep this question' } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    await screen.findByText('Send queued messages before adding more.', { selector: '[role="alert"]' })
    expect(protectedDrafts.clearProtectedDraft).not.toHaveBeenCalled()
    accepts = true
    fireEvent.click(screen.getByRole('button', { name: 'Sync now' }))
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue('Keep this question'))
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(protectedDrafts.clearProtectedDraft).toHaveBeenCalledTimes(1))
    expect(screen.getByLabelText('Message')).toHaveValue('')
  })

  it('does not post after a committed autosave loses its reply, then sends once after explicit recovery', async () => {
    const message = 'Question whose protected save reply was lost'
    let stored: string | null = null
    let storedRevision = 0
    let loseSaveReply = true
    const posts: string[] = []
    protectedDrafts.readProtectedDraft.mockImplementation(async () =>
      ({ ok: true, text: stored, revision: storedRevision, message: '' }))
    protectedDrafts.saveProtectedDraft.mockImplementation(async (_identity, value, expectedRevision) => {
      if (expectedRevision !== storedRevision)
        return { ok: false, text: stored, revision: storedRevision, message: 'Review the current protected copy.' }
      stored = value; storedRevision++
      if (loseSaveReply) { loseSaveReply = false; throw new Error('Synthetic lost protected-save reply') }
      return { ok: true, text: stored, revision: storedRevision, message: '' }
    })
    protectedDrafts.clearProtectedDraft.mockImplementation(async (_identity, expectedRevision) => {
      if (expectedRevision !== storedRevision)
        return { ok: false, text: stored, revision: storedRevision, message: 'Review the current protected copy.' }
      stored = null; storedRevision++
      return { ok: true, text: null, revision: storedRevision, message: '' }
    })
    vi.stubGlobal('fetch', vi.fn(async (path: string) => {
      if (path.endsWith('/messages')) posts.push(path)
      return Response.json(room())
    }))
    render(<ServerChat profileId={profileId} host visible />)
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: message } })
    await screen.findByText('Synthetic lost protected-save reply', {}, { timeout: 2500 })
    expect(stored).toBe(message)
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    await screen.findByRole('button', { name: 'Use recovered message' })
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenCalledTimes(2)
    expect(storedRevision).toBe(1)
    expect(screen.getByText('The unfinished message could not be confirmed on this PC. Review its draft status before sending.')).toBeInTheDocument()
    expect(posts).toEqual([])
    expect(protectedDrafts.clearProtectedDraft).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Use recovered message' }))
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(posts).toHaveLength(1))
    await waitFor(() => expect(stored).toBeNull())
    expect(screen.queryByRole('button', { name: 'Use recovered message' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Message')).toHaveValue('')
  })

  it('clears an accepted snapshot at its newer canonical revision without offering it for recovery', async () => {
    const message = 'Already accepted issue report'
    const posts: string[] = []
    protectedDrafts.clearProtectedDraft.mockResolvedValueOnce({ ok: false, text: message, revision: 7,
      message: 'The same protected snapshot has a newer revision.' })
      .mockResolvedValueOnce({ ok: true, text: null, revision: 8, message: '' })
    vi.stubGlobal('fetch', vi.fn(async (path: string) => {
      if (path.endsWith('/messages')) posts.push(path)
      return Response.json(room())
    }))
    render(<ServerChat profileId={profileId} host visible />)
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: message } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(protectedDrafts.clearProtectedDraft).toHaveBeenCalledTimes(2))
    expect(protectedDrafts.clearProtectedDraft).toHaveBeenNthCalledWith(1, expect.anything(), 1, expect.any(AbortSignal))
    expect(protectedDrafts.clearProtectedDraft).toHaveBeenNthCalledWith(2, expect.anything(), 7, expect.any(AbortSignal))
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    expect(screen.getByLabelText('Message')).toHaveValue('')
    expect(screen.queryByRole('button', { name: 'Use recovered message' })).not.toBeInTheDocument()
    expect(posts).toHaveLength(1)
  })

  it('keeps a repeatedly conflicting accepted snapshot out of recovery while explicit Retry clears it', async () => {
    const message = 'Known accepted question awaiting composer cleanup'
    const states: ChatDraftState[] = []
    const posts: string[] = []
    protectedDrafts.clearProtectedDraft.mockResolvedValueOnce({ ok: false, text: message, revision: 2, message: 'Changed.' })
      .mockResolvedValueOnce({ ok: false, text: message, revision: 3, message: 'Changed again.' })
    protectedDrafts.readProtectedDraft.mockResolvedValueOnce({ ok: true, text: null, revision: 0, message: '' })
      .mockResolvedValueOnce({ ok: true, text: message, revision: 3, message: '' })
    vi.stubGlobal('fetch', vi.fn(async (path: string) => {
      if (path.endsWith('/messages')) posts.push(path)
      return Response.json(room())
    }))
    render(<ServerChat profileId={profileId} host visible onDraftStateChange={state => states.push(state)} />)
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: message } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    const retry = await screen.findByRole('button', { name: 'Retry unfinished message' })
    expect(screen.queryByRole('button', { name: 'Use recovered message' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Message')).toBeDisabled()
    expect(states.at(-1)?.dirty).toBe(true)
    fireEvent.click(retry)
    await waitFor(() => expect(protectedDrafts.clearProtectedDraft).toHaveBeenCalledTimes(3))
    expect(protectedDrafts.clearProtectedDraft).toHaveBeenLastCalledWith(expect.anything(), 3, expect.any(AbortSignal))
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    expect(screen.queryByRole('button', { name: 'Use recovered message' })).not.toBeInTheDocument()
    expect(states.at(-1)?.dirty).toBe(false)
    expect(posts).toHaveLength(1)
  })

  it.each([false, true])('preserves a different newer draft during accepted cleanup (retry=%s)', async retry => {
    const message = 'Accepted earlier question'
    const newer = 'A different unfinished question from a newer revision'
    const posts: string[] = []
    if (retry) protectedDrafts.clearProtectedDraft.mockResolvedValueOnce({ ok: false, text: message,
      revision: 4, message: 'The accepted snapshot has a newer revision.' })
    protectedDrafts.clearProtectedDraft.mockResolvedValueOnce({ ok: false, text: newer, revision: 5,
      message: 'Review this newer unfinished message.' })
    vi.stubGlobal('fetch', vi.fn(async (path: string) => {
      if (path.endsWith('/messages')) posts.push(path)
      return Response.json(room())
    }))
    render(<ServerChat profileId={profileId} host visible />)
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: message } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    await screen.findByRole('button', { name: 'Use recovered message' })
    expect(screen.getByText(newer, { selector: 'p' })).toBeInTheDocument()
    expect(protectedDrafts.clearProtectedDraft).toHaveBeenCalledTimes(retry ? 2 : 1)
    expect(posts).toHaveLength(1)
    // Repeating an earlier message can be intentional. The different recovered
    // value must replace the saved cache, so this later edit is persisted anew.
    fireEvent.click(screen.getByRole('button', { name: 'Use recovered message' }))
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: message } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(protectedDrafts.saveProtectedDraft).toHaveBeenCalledWith(
      expect.anything(), message, 5, expect.any(AbortSignal)))
    await waitFor(() => expect(posts).toHaveLength(2))
  })

  it('stops automatic writes on a CAS conflict and presents the other protected draft for review', async () => {
    protectedDrafts.saveProtectedDraft.mockResolvedValue({ ok: false, text: 'Other unfinished message', revision: 3, message: 'Review the newer draft.' })
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(room())))
    render(<ServerChat profileId={profileId} host visible />)
    await screen.findByLabelText('Message')
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    vi.useFakeTimers()
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'My unfinished message' } })
    await act(async () => { await vi.advanceTimersByTimeAsync(700) })
    expect(screen.getByText('Other unfinished message')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Review the newer draft')
    expect(screen.getByLabelText('Message')).toHaveValue('My unfinished message')
    await act(async () => { await vi.advanceTimersByTimeAsync(1400) })
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenCalledTimes(1)
  })

  it('keeps navigation open when a newer edit arrives while its protected save is pending', async () => {
    let finish: (value: { ok: boolean; text: string; revision: number; message: string }) => void = () => undefined
    protectedDrafts.saveProtectedDraft.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const states: ChatDraftState[] = []
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(room())))
    render(<ServerChat profileId={profileId} host visible onDraftStateChange={state => states.push(state)} />)
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    vi.useFakeTimers()
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Snapshot before navigation' } })
    const flushing = states.at(-1)!.flush()
    await act(async () => { await Promise.resolve() })
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenCalledWith(expect.anything(),
      'Snapshot before navigation', 0, expect.any(AbortSignal))
    // This is a navigation flush, so the owner can keep typing while it waits.
    expect(screen.getByLabelText('Message')).toBeEnabled()
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'A newer edit during the pending save' } })
    await act(async () => {
      finish({ ok: true, text: 'Snapshot before navigation', revision: 1, message: '' })
      expect(await flushing).toBe(false)
    })
    expect(screen.getByLabelText('Message')).toHaveValue('A newer edit during the pending save')
    expect(states.at(-1)?.dirty).toBe(true)
    await act(async () => { expect(await states.at(-1)!.flush()).toBe(true) })
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenNthCalledWith(2, expect.anything(),
      'A newer edit during the pending save', 1, expect.any(AbortSignal))
    expect(states.at(-1)?.saving).toBe(false)
    expect(states.at(-1)?.dirty).toBe(false)
  })

  it('removes the room guard when navigation unmounts before its final save effect renders', async () => {
    const guards = new Map<string, ChatDraftState>()
    const keepGuard = (guard: ChatDraftState) => {
      if (guard.dirty || guard.saving) guards.set('chat', guard)
      else guards.delete('chat')
    }
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(room())))
    const view = render(<ServerChat profileId={profileId} host visible onDraftStateChange={keepGuard} />)
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Keep this before changing pages' } })
    expect(guards.get('chat')?.dirty).toBe(true)
    await act(async () => {
      expect(await guards.get('chat')!.flush()).toBe(true)
      // Main changes scope immediately after flush. Do not wait for an effect
      // that could otherwise conceal the stale aborted-guard regression.
      view.unmount()
    })
    expect(guards.size).toBe(0)
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenCalledWith(expect.anything(),
      'Keep this before changing pages', 0, expect.any(AbortSignal))
    const next = render(<ServerChat profileId={profileId} host={false} visible
      connectionId="11111111-1111-1111-1111-111111111111" onDraftStateChange={keepGuard} />)
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'A later room remains navigable' } })
    expect(guards.get('chat')?.dirty).toBe(true)
    await act(async () => { expect(await guards.get('chat')!.flush()).toBe(true) })
    next.unmount()
    expect(guards.size).toBe(0)
  })

  it('flushes a recent edit for navigation and aborts requests when the connection scope changes', async () => {
    const connection = '11111111-1111-1111-1111-111111111111'
    const otherConnection = '22222222-2222-2222-2222-222222222222'
    const states: ChatDraftState[] = []
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(room())))
    const { rerender } = render(<ServerChat profileId={profileId} host={false} connectionId={connection} visible
      onDraftStateChange={state => states.push(state)} />)
    await screen.findByLabelText('Message')
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'Message for the first connection' } })
    expect(states.at(-1)?.dirty).toBe(true)
    await act(async () => { expect(await states.at(-1)!.flush()).toBe(true) })
    expect(protectedDrafts.saveProtectedDraft).toHaveBeenCalledWith(
      expect.objectContaining({ connectionId: connection }), 'Message for the first connection', 0, expect.any(AbortSignal))
    const previousSignal = protectedDrafts.readProtectedDraft.mock.calls[0][1] as AbortSignal
    rerender(<ServerChat profileId={profileId} host={false} connectionId={otherConnection} visible />)
    expect(previousSignal.aborted).toBe(true)
    await waitFor(() => expect(protectedDrafts.readProtectedDraft).toHaveBeenCalledWith(
      expect.objectContaining({ connectionId: otherConnection }), expect.any(AbortSignal)))
    expect(screen.getByLabelText('Message')).toHaveValue('')
  })

  it('ignores a late post from the room that was closed without clearing the new room draft', async () => {
    const otherProfile = 'ffffffff-ffff-ffff-ffff-ffffffffffff'
    let finish: (value: Response) => void = () => undefined
    let postSignal: AbortSignal | undefined
    vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
      if (path.endsWith('/messages')) {
        postSignal = init.signal as AbortSignal
        return new Promise<Response>(resolve => { finish = resolve })
      }
      return Response.json(path.includes(otherProfile) ? { ...room(), profileId: otherProfile, entries: [] } : room())
    }))
    const { rerender } = render(<ServerChat profileId={profileId} host visible />)
    await screen.findByLabelText('Message')
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Message'), { target: { value: 'First room question' } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(postSignal).toBeDefined())
    rerender(<ServerChat profileId={otherProfile} host visible />)
    await screen.findByLabelText('Message')
    expect(postSignal?.aborted).toBe(true)
    await act(async () => { finish(Response.json(room())) })
    expect(protectedDrafts.clearProtectedDraft).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Message')).toHaveValue('')
  })
})

describe('reading and queued messages', () => {
  it('preserves a scrolled reading position and counts new accepted messages until Jump to latest', async () => {
    const later = { ...entry, id: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee', text: 'Second update' }
    let current = room()
    const unread = vi.fn()
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(current)))
    render(<ServerChat profileId={profileId} host visible onUnreadChange={unread} />)
    await screen.findByText(entry.text)
    const log = screen.getByRole('log')
    Object.defineProperties(log, { scrollHeight: { configurable: true, value: 2000 }, clientHeight: { configurable: true, value: 400 } })
    log.scrollTop = 100
    fireEvent.scroll(log)
    expect(log).toHaveAttribute('aria-live', 'off')
    current = room(true, [entry, later])
    fireEvent.click(screen.getByRole('button', { name: 'Sync now' }))
    await screen.findByText(later.text)
    expect(log.scrollTop).toBe(100)
    expect(unread).toHaveBeenLastCalledWith(1)
    fireEvent.click(screen.getByRole('button', { name: 'New messages (1) · Jump to latest' }))
    expect(log.scrollTop).toBe(2000)
    expect(unread).toHaveBeenLastCalledWith(0)
    expect(localStorage.getItem(localStorage.key(0)!)).toBe(later.id)
  })

  it('edits and cancels only the local unsent queue with an expected-text guard', async () => {
    const draftId = 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee'
    let queued = [{ id: draftId, text: 'Unsent question', submitted: false }]
    const mutations: Array<{ path: string; method: string; body: unknown }> = []
    vi.stubGlobal('fetch', vi.fn(async (path: string, init: RequestInit) => {
      if (path.includes('/queue/')) {
        const body = JSON.parse(String(init.body))
        mutations.push({ path, method: String(init.method), body })
        queued = path.endsWith('/cancel') ? [] : [{ ...queued[0], text: body.text }]
      }
      return Response.json({ ...room(), pending: queued })
    }))
    render(<ServerChat profileId={profileId} host={false} visible />)
    fireEvent.click(await screen.findByRole('button', { name: 'Edit queued message 1' }))
    fireEvent.change(screen.getByLabelText('Queued message'), { target: { value: 'Reviewed question' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save queued message' }))
    await screen.findByText('Reviewed question', { selector: 'p' })
    expect(mutations[0]).toEqual({ path: `/api/local/friend/${profileId}/chat/queue/${draftId}`, method: 'PUT',
      body: { text: 'Reviewed question', expectedText: 'Unsent question' } })
    fireEvent.click(screen.getByRole('button', { name: 'Cancel queued message 1' }))
    await waitFor(() => expect(screen.queryByText('Reviewed question')).not.toBeInTheDocument())
    expect(mutations[1].body).toEqual({ expectedText: 'Reviewed question' })
    expect(screen.getByText(entry.text)).toBeInTheDocument()
  })

  it('offers no mutation for dispatched or legacy uncertain messages', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ ...room(), pending: [
      { id: deviceId, text: 'Reply may already be accepted', submitted: true },
      { id: hostId, text: 'Legacy queue without dispatch proof' }
    ] })))
    render(<ServerChat profileId={profileId} host={false} visible />)
    await screen.findByText('Reply may already be accepted')
    expect(screen.getAllByText('Delivery attempted; waiting for Host confirmation')).toHaveLength(2)
    expect(screen.queryByRole('button', { name: /Edit queued message|Cancel queued message/ })).not.toBeInTheDocument()
    expect(() => parseManagedChatRoom({ ...room(), pending: [{ id: deviceId, text: 'x', submitted: 'false' }] })).toThrow(/delivery state/)
  })

  it('counts retained IDs without treating pending drafts or message content as unread state', () => {
    expect(unreadMessageCount([deviceId, hostId, entry.id], deviceId)).toBe(2)
    expect(unreadMessageCount([hostId, entry.id], deviceId)).toBe(2)
    expect(unreadMessageCount([], deviceId)).toBe(0)
    expect(unreadMessageCount([deviceId, hostId], hostId.toUpperCase())).toBe(0)
  })
})

describe('authorized notice card previews', () => {
  it('renders a concise plain-text cached preview only with room access', () => {
    const current = parseManagedChatRoom(noticeRoom({ ...notice, text: '<script>unsafe()</script> ' + 'Rules '.repeat(80) }, true))
    const { container, rerender } = render(<PinnedNoticePreview room={current} authorized />)
    expect(screen.getByText(/<script>unsafe/).textContent!.length).toBeLessThanOrEqual(181)
    expect(container.querySelector('script')).toBeNull()
    expect(screen.getByText(/Cached on this PC/)).toBeInTheDocument()
    rerender(<PinnedNoticePreview room={current} authorized={false} />)
    expect(screen.queryByText('Pinned notice')).not.toBeInTheDocument()
    rerender(<PinnedNoticePreview room={{ ...current, ok: false }} authorized />)
    expect(screen.queryByText('Pinned notice')).not.toBeInTheDocument()
  })

  it('does not poll by default or for unauthorized, unscoped or open Friend rooms', () => {
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    const connectionId = '11111111-1111-1111-1111-111111111111'
    const { rerender } = render(<ServerChatCardSummary profileId={profileId} host={false} authorized connectionId={connectionId} />)
    rerender(<ServerChatCardSummary profileId={profileId} host={false} authorized={false} connectionId={connectionId} pollWhenClosed />)
    rerender(<ServerChatCardSummary profileId={profileId} host={false} authorized pollWhenClosed />)
    rerender(<ServerChatCardSummary profileId={profileId} host={false} authorized connectionId={connectionId} pollWhenClosed roomOpen />)
    expect(fetch).not.toHaveBeenCalled()
  })

  it('polls only the current scoped local summary and removes content when authorization is removed', async () => {
    const connectionId = '11111111-1111-1111-1111-111111111111'
    const unread = vi.fn()
    const fetch = vi.fn<typeof globalThis.fetch>(async () => Response.json({ ...noticeRoom(), messageIds: [entry.id] }))
    vi.stubGlobal('fetch', fetch)
    const { rerender } = render(<ServerChatCardSummary profileId={profileId} host={false} authorized connectionId={connectionId}
      pollWhenClosed onUnreadChange={unread} />)
    await screen.findByText(/Leave spawn clear/)
    expect(fetch).toHaveBeenCalledWith(`/api/local/friend/connections/${connectionId}/servers/${profileId}/chat/summary`,
      expect.objectContaining({ headers: { 'X-TogetherServer-Local': '1' } }))
    expect(unread).toHaveBeenLastCalledWith(1)
    rerender(<ServerChatCardSummary profileId={profileId} host={false} authorized={false} connectionId={connectionId} pollWhenClosed />)
    expect(screen.queryByText(/Leave spawn clear/)).not.toBeInTheDocument()
    expect((fetch.mock.calls[0][1] as RequestInit).signal?.aborted).toBe(true)
  })

  it('rejects unbounded, duplicate, malformed and unauthorized summary IDs/content', () => {
    const current = { ...noticeRoom(), messageIds: [entry.id] }
    expect(parseChatRoomSummary(current).messageIds).toEqual([entry.id])
    for (const patch of [
      { messageIds: Array(201).fill(entry.id) }, { messageIds: [entry.id, entry.id] },
      { messageIds: ['not-an-id'] }, { ok: false }, { message: 'x'.repeat(501) }
    ]) expect(() => parseChatRoomSummary({ ...current, ...patch })).toThrow()
  })
})

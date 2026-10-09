import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ServerChat } from './ServerChat'
import { parseChatRoomView } from './contracts'
import { parseChatRoomWithNotice, type PinnedServerNotice } from './pinnedNoticeContracts'

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

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

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

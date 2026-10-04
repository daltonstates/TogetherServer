import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ServerChat } from './ServerChat'
import { parseChatRoomView } from './contracts'

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
})

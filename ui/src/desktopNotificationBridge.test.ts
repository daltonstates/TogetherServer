import { describe, expect, it, vi } from 'vitest'
import { parseDesktopNotification, subscribeToDesktopNotifications, parseDesktopDraftFlushRequest,
  subscribeToDesktopDraftFlush } from './desktopNotificationBridge'

const profileId = '11111111-1111-4111-8111-111111111111'
const connectionId = '22222222-2222-4222-8222-222222222222'

describe('native notification routing', () => {
  it('accepts only fixed destinations and saved identifiers with no arbitrary target', () => {
    expect(parseDesktopNotification({ type: 'together-notification', destination: { workspace: 'join', section: 'shared-saves',
      profileId, connectionId } })).toEqual({ workspace: 'join', section: 'shared-saves', profileId, connectionId })
    for (const destination of [{ workspace: 'host', section: 'start', profileId },
      { workspace: 'host', section: 'overview', profileId: '../world' },
      { workspace: 'settings', section: 'https://example.com' },
      { workspace: 'settings', section: 'app', connectionId }])
      expect(parseDesktopNotification({ type: 'together-notification', destination })).toBeNull()
    expect(parseDesktopNotification(JSON.stringify({ type: 'together-notification', destination: { workspace: 'settings', section: 'app' } })))
      .toBeNull()
  })

  it('does not navigate on subscription or invalid events and detaches on cleanup', () => {
    let receive: ((event: { data: unknown }) => void) | undefined
    const native = { addEventListener: vi.fn((_type: 'message', callback: (event: { data: unknown }) => void) => { receive = callback }),
      removeEventListener: vi.fn() }
    const navigate = vi.fn()
    const cleanup = subscribeToDesktopNotifications(navigate, native)
    expect(navigate).not.toHaveBeenCalled()
    receive?.({ data: { type: 'activity', destination: { workspace: 'host', section: 'overview', profileId } } })
    expect(navigate).not.toHaveBeenCalled()
    receive?.({ data: { type: 'together-notification', destination: { workspace: 'host', section: 'backups', profileId } } })
    expect(navigate).toHaveBeenCalledOnce()
    expect(navigate).toHaveBeenCalledWith({ workspace: 'host', section: 'backups', profileId, connectionId: null })
    cleanup()
    expect(native.removeEventListener).toHaveBeenCalledWith('message', receive)
  })
})

describe('native Quit draft flush', () => {
  const requestId = '33333333-3333-4333-8333-333333333333'
  const otherId = '44444444-4444-4444-8444-444444444444'
  const request = { type: 'together-flush-drafts', requestId }
  const reply = (id: string, ok: boolean) => ({ type: 'together-drafts-flushed', requestId: id, ok })
  const bridge = () => {
    let receive: ((event: { data: unknown }) => void) | undefined
    const native = { addEventListener: vi.fn((_type: 'message', callback: (event: { data: unknown }) => void) => { receive = callback }),
      removeEventListener: vi.fn(), postMessage: vi.fn() }
    return { native, send: (data: unknown) => receive?.({ data }) }
  }

  it('accepts only the exact request type and nonempty GUID, without draft content or actions', () => {
    expect(parseDesktopDraftFlushRequest(request)).toEqual({ requestId })
    for (const value of [null, [], {}, JSON.stringify(request), { ...request, type: 'quit' },
      { ...request, requestId: '00000000-0000-0000-0000-000000000000' }, { ...request, requestId: '../private' },
      { ...request, requestId: requestId.replaceAll('-', '') }, { ...request, requestId: 1 },
      { ...request, text: 'private draft' }, { ...request, path: 'private.exe' }, { ...request, action: 'stop' }])
      expect(parseDesktopDraftFlushRequest(value)).toBeNull()
  })

  it('waits for the protected save before acknowledging and flushes duplicate requests once', async () => {
    const { native, send } = bridge()
    let finish!: (ok: boolean) => void
    const flush = vi.fn(() => new Promise<boolean>(resolve => { finish = resolve }))
    const cleanup = subscribeToDesktopDraftFlush(flush, native)
    expect(flush).not.toHaveBeenCalled()
    send(request)
    send(request)
    await Promise.resolve()
    expect(flush).toHaveBeenCalledOnce()
    expect(native.postMessage).not.toHaveBeenCalled()
    send({ type: 'together-flush-drafts', requestId: otherId })
    expect(flush).toHaveBeenCalledOnce()
    expect(native.postMessage).toHaveBeenCalledExactlyOnceWith(reply(otherId, false))
    finish(true)
    await vi.waitFor(() => expect(native.postMessage).toHaveBeenCalledWith(reply(requestId, true)))
    send(request)
    expect(flush).toHaveBeenCalledOnce()
    cleanup()
  })

  it.each(['false', 'reject', 'throw'])('fails closed when a protected flush reports %s', async outcome => {
    const { native, send } = bridge()
    const flush = vi.fn((): Promise<boolean> => {
      if (outcome === 'throw') throw new Error('draft unavailable')
      return outcome === 'reject' ? Promise.reject(new Error('draft unavailable')) : Promise.resolve(false)
    })
    const cleanup = subscribeToDesktopDraftFlush(flush, native)
    send({ type: 'together-notification', destination: { workspace: 'settings', section: 'app' } })
    send({ ...request, draft: 'private' })
    expect(flush).not.toHaveBeenCalled()
    send(request)
    await vi.waitFor(() => expect(native.postMessage).toHaveBeenCalledExactlyOnceWith(reply(requestId, false)))
    cleanup()
  })

  it('detaches and suppresses late acknowledgement after the subscriber unmounts', async () => {
    const { native, send } = bridge()
    let finish!: (ok: boolean) => void
    const flush = vi.fn(() => new Promise<boolean>(resolve => { finish = resolve }))
    const cleanup = subscribeToDesktopDraftFlush(flush, native)
    send(request)
    await Promise.resolve()
    cleanup()
    finish(true)
    await Promise.resolve()
    await Promise.resolve()
    await Promise.resolve()
    send({ type: 'together-flush-drafts', requestId: otherId })
    expect(native.removeEventListener).toHaveBeenCalledWith('message', expect.any(Function))
    expect(native.postMessage).not.toHaveBeenCalled()
    expect(flush).toHaveBeenCalledOnce()
  })

  it('does nothing when the native postMessage channel is unavailable', () => {
    const flush = vi.fn(async () => true)
    const native = { addEventListener: vi.fn(), removeEventListener: vi.fn(), postMessage: undefined }
    const cleanup = subscribeToDesktopDraftFlush(flush, native as never)
    expect(native.addEventListener).not.toHaveBeenCalled()
    cleanup()
    expect(flush).not.toHaveBeenCalled()
  })

  it('does not begin a queued flush after cleanup', async () => {
    const { native, send } = bridge()
    const flush = vi.fn(async () => true)
    const cleanup = subscribeToDesktopDraftFlush(flush, native)
    send(request)
    cleanup()
    await Promise.resolve()
    await Promise.resolve()
    expect(flush).not.toHaveBeenCalled()
    expect(native.postMessage).not.toHaveBeenCalled()
  })
})

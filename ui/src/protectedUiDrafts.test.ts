import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearProtectedDraft, parseProtectedDraftResult, readProtectedDraft, saveProtectedDraft,
  type DraftIdentity } from './protectedUiDrafts'

const identity: DraftIdentity = { purpose: 'chat', profileId: '8beb85a8-cf4f-4ff4-a599-d113bdc371da',
  connectionId: 'a286f7a5-d1ec-447c-a56b-2aa35a798fb4', key: 'composer' }

afterEach(() => { vi.unstubAllGlobals() })

describe('protected drafts', () => {
  it('uses only fixed local routes and the sensitive local header for all operations', async () => {
    const response = { ok: true, status: 200, text: async () => JSON.stringify({ ok: true, text: null,
      revision: 7, message: 'Checked.' }) } as Response
    const fetch = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>(async () => response)
    vi.stubGlobal('fetch', fetch)
    const controller = new AbortController()
    await readProtectedDraft(identity, controller.signal)
    await saveProtectedDraft(identity, 'private unfinished message', 7, controller.signal)
    await clearProtectedDraft(identity, 8, controller.signal)
    expect(fetch.mock.calls.map(call => call[0])).toEqual([
      '/api/local/ui-drafts/read', '/api/local/ui-drafts', '/api/local/ui-drafts/clear'
    ])
    expect(fetch).toHaveBeenNthCalledWith(2, '/api/local/ui-drafts', expect.objectContaining({
      method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' },
      signal: controller.signal,
      body: JSON.stringify({ ...identity, text: 'private unfinished message', expectedRevision: 7 })
    }))
  })

  it('refuses caller paths, wrong scope and oversized UTF-8 content before sending anything', () => {
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    expect(() => readProtectedDraft({ ...identity, key: '../passwords' })).toThrow()
    expect(() => readProtectedDraft({ ...identity, profileId: 'other-server' })).toThrow()
    expect(() => saveProtectedDraft(identity, '界'.repeat(22000), 1)).toThrow()
    expect(() => clearProtectedDraft(identity, Number.MAX_SAFE_INTEGER + 1)).toThrow()
    expect(fetch).not.toHaveBeenCalled()
  })

  it('allows only the reserved local non-secret setup draft with an empty profile ID', async () => {
    const fetch = vi.fn(async () => ({ ok: true, status: 200,
      text: async () => JSON.stringify({ ok: true, text: null, revision: 0, message: 'No draft.' }) } as Response))
    vi.stubGlobal('fetch', fetch)
    const profileId = '00000000-0000-0000-0000-000000000000'
    await readProtectedDraft({ purpose: 'settings', profileId, key: 'host-setup' })
    expect(() => readProtectedDraft({ ...identity, profileId })).toThrow()
    expect(() => readProtectedDraft({ purpose: 'settings', profileId, connectionId: identity.connectionId,
      key: 'host-setup' })).toThrow()
    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('preserves conflict text for explicit review and rejects corrupt revisions/response sizes', () => {
    expect(parseProtectedDraftResult({ ok: false, text: 'newer edit', revision: 5, message: 'Review the draft.' }))
      .toEqual({ ok: false, text: 'newer edit', revision: 5, message: 'Review the draft.' })
    expect(() => parseProtectedDraftResult({ ok: true, text: null, revision: -1, message: 'bad' })).toThrow()
    expect(() => parseProtectedDraftResult({ ok: true, text: '界'.repeat(700000), revision: 1, message: 'bad' })).toThrow()
  })

  it('preserves a legacy zero connection ID on every compose request separately from the Host null scope', async () => {
    const fetch = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>(async () => ({ ok: true, status: 200,
      text: async () => JSON.stringify({ ok: true, text: null, revision: 4, message: 'Checked.' }) } as Response))
    vi.stubGlobal('fetch', fetch)
    const connectionId = '00000000-0000-0000-0000-000000000000'
    const legacy: DraftIdentity = { ...identity, connectionId, key: 'compose' }
    await readProtectedDraft(legacy)
    await saveProtectedDraft(legacy, 'Reviewed legacy compose', 4)
    await clearProtectedDraft(legacy, 5)
    for (const [, request] of fetch.mock.calls) {
      expect(JSON.parse(request!.body as string)).toMatchObject({ purpose: 'chat', profileId: identity.profileId,
        connectionId, key: 'compose' })
    }
    await readProtectedDraft({ ...legacy, connectionId: null })
    expect(JSON.parse(fetch.mock.calls[3][1]!.body as string).connectionId).toBeNull()
  })

  it('keeps legacy zero connection IDs limited to the reviewed chat compose identity', () => {
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    const connectionId = '00000000-0000-0000-0000-000000000000'
    for (const purpose of ['file', 'settings', 'list'] as const)
      expect(() => readProtectedDraft({ ...identity, purpose, connectionId, key: 'compose' })).toThrow()
    expect(() => readProtectedDraft({ ...identity, connectionId })).toThrow()
    expect(() => readProtectedDraft({ ...identity, connectionId, profileId: connectionId, key: 'compose' })).toThrow()
    expect(() => readProtectedDraft({ ...identity, connectionId: '00000000', key: 'compose' })).toThrow()
    expect(fetch).not.toHaveBeenCalled()
  })

  it('protects a reviewed large file draft while keeping chat capped at 64 KiB', async () => {
    const large = 'a'.repeat(512 * 1024)
    const fetch = vi.fn(async () => ({ ok: true, status: 200,
      text: async () => JSON.stringify({ ok: true, text: large, revision: 1, message: 'Saved.' }) } as Response))
    vi.stubGlobal('fetch', fetch)
    expect((await saveProtectedDraft({ ...identity, purpose: 'file', connectionId: null, key: 'properties' }, large, 0)).text).toBe(large)
    expect(() => saveProtectedDraft(identity, large, 0)).toThrow()
    await expect(readProtectedDraft(identity)).rejects.toMatchObject({ code: 'InvalidResponse' })
  })
})

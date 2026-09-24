import { describe, expect, it, vi } from 'vitest'
import { errorMessage, requestJson } from './api'
import { parseBasicResult } from './contracts'

function jsonResponse(status: number, payload: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => JSON.stringify(payload)
  } as Response
}

describe('requestJson', () => {
  it('preserves structured server errors', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(409,
      { code: 'FriendMode', message: 'Switch to My server first.' })))

    await expect(requestJson('/api/test', parseBasicResult)).rejects.toMatchObject({
      status: 409,
      code: 'FriendMode',
      message: 'Switch to My server first.'
    })
  })

  it('rejects a successful response with the wrong runtime shape', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, { ok: 'yes', code: 'Bad', message: 'bad' })))

    await expect(requestJson('/api/test', parseBasicResult)).rejects.toMatchObject({
      status: 200,
      code: 'InvalidResponse'
    })
  })

  it('keeps the structured error code visible to the user', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(409,
      { code: 'FriendMode', message: 'Switch to My server first.' })))

    const error = await requestJson('/api/test', parseBasicResult).catch(reason => reason as unknown)

    expect(errorMessage(error)).toBe('FriendMode: Switch to My server first.')
  })
})

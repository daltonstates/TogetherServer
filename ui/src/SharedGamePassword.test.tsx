import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { parseSharedPassword, SharedGamePassword } from './SharedGamePassword'
import { changeJson } from './api'

vi.mock('./api', () => ({ changeJson: vi.fn() }))
const profileId = '11111111-1111-4111-8111-111111111111'
const input = { profileId, connectionId: '22222222-2222-4222-8222-222222222222', allowed: true, current: true, supported: true, identity: 'saved-host-a' }
const response = (password = 'synthetic-game-password') => ({ ok: true, code: 'GamePasswordReady', message: 'Current password.', profileId, password })
beforeEach(() => {
  vi.mocked(changeJson).mockReset().mockResolvedValue(response())
  localStorage.clear()
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: vi.fn(async () => {}) } })
})

describe('shared game passwords', () => {
  it('does not fetch on load and requires current explicit permission and Host capability', () => {
    const view = render(<SharedGamePassword {...input} allowed={false} />)
    expect(changeJson).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Show password' })).toBeDisabled()
    view.rerender(<SharedGamePassword {...input} current={false} />)
    expect(screen.getByRole('button', { name: 'Copy password' })).toBeDisabled()
    view.rerender(<SharedGamePassword {...input} supported={false} />)
    expect(screen.getByText(/Host needs an update/)).toBeInTheDocument()
    expect(changeJson).not.toHaveBeenCalled()
  })
  it('requests the current value explicitly, hides it on blur, and never saves it', async () => {
    render(<SharedGamePassword {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Show password' }))
    expect(await screen.findByText('synthetic-game-password')).toBeVisible()
    expect(changeJson).toHaveBeenCalledWith(`/api/local/friend/${profileId}/game-password/reveal`, 'POST', parseSharedPassword,
      { connectionId: input.connectionId }, expect.any(AbortSignal))
    expect(localStorage.length).toBe(0)
    fireEvent(window, new Event('blur'))
    expect(screen.queryByText('synthetic-game-password')).not.toBeInTheDocument()
  })
  it('masks a revealed value after 30 seconds', async () => {
    vi.useFakeTimers()
    render(<SharedGamePassword {...input} />)
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Show password' })) })
    expect(screen.getByText('synthetic-game-password')).toBeVisible()
    await act(async () => { await vi.advanceTimersByTimeAsync(30_001) })
    expect(screen.queryByText('synthetic-game-password')).not.toBeInTheDocument()
    vi.useRealTimers()
  })
  it('fetches again for each copy, keeping both versions out of the visible page', async () => {
    vi.mocked(changeJson).mockResolvedValueOnce(response('synthetic-before')).mockResolvedValueOnce(response('synthetic-after'))
    render(<SharedGamePassword {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Copy password' }))
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith('synthetic-before'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Copy password' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Copy password' }))
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith('synthetic-after'))
    expect(screen.queryByText('synthetic-before')).not.toBeInTheDocument()
    expect(screen.queryByText('synthetic-after')).not.toBeInTheDocument()
  })
  it('discards an in-flight reply after permission removal or switching Hosts', async () => {
    let release!: (value: unknown) => void
    vi.mocked(changeJson).mockImplementation(() => new Promise(resolve => { release = resolve }))
    const view = render(<SharedGamePassword {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Show password' }))
    view.rerender(<SharedGamePassword {...input} allowed={false} identity="saved-host-b" />)
    await act(async () => { release(response()) })
    expect(screen.queryByText('synthetic-game-password')).not.toBeInTheDocument()
    expect(navigator.clipboard.writeText).not.toHaveBeenCalled()
  })
  it('rejects malformed secrets and any password in a denial', () => {
    for (const value of [response('bad\npassword'), response(''), { ...response(), ok: false }, { ...response(), profileId: 'bad' }])
      expect(() => parseSharedPassword(value)).toThrow()
    expect(parseSharedPassword({ ok: false, code: 'Denied', message: 'Denied.', profileId }).password).toBeNull()
  })
})

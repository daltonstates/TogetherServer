import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { OpenGameButton } from './OpenGameButton'

afterEach(() => vi.unstubAllGlobals())
describe('explicit Open game action', () => {
  it('does nothing on mount or rerender and sends only the assigned profile action after a click', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: true, code: 'GameOpenRequested', message: 'Asked Steam to open the game. Join in the game.' })))
    vi.stubGlobal('fetch', fetcher)
    const view = render(<OpenGameButton profileId="saved-profile" kind="Valheim" available />)
    view.rerender(<OpenGameButton profileId="saved-profile" kind="Valheim" available />)
    expect(fetcher).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Open game' }))
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(1))
    expect(fetcher.mock.calls[0]).toEqual(['/api/local/friend/saved-profile/open-game', expect.objectContaining({ method: 'POST', body: undefined })])
    expect(await screen.findByRole('status')).toHaveTextContent('Join in the game')
  })
  it('keeps missing-handler and failed results visible', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: false, code: 'SteamHandlerUnavailable', message: 'Open Steam yourself, then copy the server address.' }))))
    render(<OpenGameButton profileId="saved" kind="Factorio" available />)
    fireEvent.click(screen.getByRole('button', { name: 'Open game' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Open Steam yourself')
  })
  it('shows a concrete Minecraft fallback without any endpoint call', () => {
    const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher)
    render(<OpenGameButton profileId="saved" kind="MinecraftJava" available />)
    expect(screen.getByText(/Windows Start or your own launcher/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Open game' })).not.toBeInTheDocument()
    expect(fetcher).not.toHaveBeenCalled()
  })
  it('does not launch from an unavailable saved link', () => {
    const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher)
    render(<OpenGameButton profileId="saved" kind="Terraria" available={false} />)
    expect(screen.getByRole('button', { name: 'Open game' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Open game' }))
    expect(fetcher).not.toHaveBeenCalled()
  })
  it('cancels the pending local request when its saved link is removed from view', () => {
    const fetcher = vi.fn().mockImplementation((_path: unknown, init: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init.signal?.addEventListener('abort', () => reject(new DOMException('Cancelled', 'AbortError')), { once: true })
    })); vi.stubGlobal('fetch', fetcher)
    const view = render(<OpenGameButton profileId="saved" kind="Valheim" available />)
    fireEvent.click(screen.getByRole('button', { name: 'Open game' }))
    const signal = (fetcher.mock.calls[0][1] as RequestInit).signal
    expect(signal?.aborted).toBe(false)
    view.unmount()
    expect(signal?.aborted).toBe(true)
  })
})

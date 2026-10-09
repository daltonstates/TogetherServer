import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { SharedWorldServerFile } from './SharedWorldServerFile'

it('uses Browse only after a click and keeps the chosen local path available for review', async () => {
  const chosen = vi.fn().mockResolvedValue('C:\\Synthetic\\valheim_server.exe')
  const change = vi.fn()
  render(<SharedWorldServerFile profileId="first" game="Valheim" value="" onChange={change} onBrowseServerFile={chosen} />)
  expect(chosen).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: 'Browse this PC' }))
  await waitFor(() => expect(change).toHaveBeenCalledWith('C:\\Synthetic\\valheim_server.exe'))
  expect(chosen).toHaveBeenCalledTimes(1)
  expect(chosen).toHaveBeenCalledWith('Valheim')
  expect(screen.getByText(/Review this local path/)).toBeInTheDocument()
})

it('ignores a late native-picker adapter result after changing the selected world', async () => {
  let resolve: ((value: string) => void) | undefined
  const chosen = vi.fn(() => new Promise<string>(complete => { resolve = complete }))
  const change = vi.fn()
  const view = render(<SharedWorldServerFile profileId="first" game="Valheim" value="" onChange={change} onBrowseServerFile={chosen} />)
  fireEvent.click(screen.getByRole('button', { name: 'Browse this PC' }))
  view.rerender(<SharedWorldServerFile profileId="second" game="Factorio" value={'C:\\Synthetic\\factorio.exe'}
    onChange={change} onBrowseServerFile={chosen} />)
  resolve?.('C:\\Synthetic\\valheim_server.exe')
  await waitFor(() => expect(screen.getByRole('button', { name: 'Browse this PC' })).toBeEnabled())
  expect(change).not.toHaveBeenCalled()
  expect(screen.getByLabelText('Factorio dedicated server')).toHaveValue('C:\\Synthetic\\factorio.exe')
})

it('does not clear a reviewed path when the picker is canceled', async () => {
  const chosen = vi.fn().mockResolvedValue(null)
  const change = vi.fn()
  render(<SharedWorldServerFile profileId="first" game="Terraria" value={'C:\\Synthetic\\TerrariaServer.exe'}
    onChange={change} onBrowseServerFile={chosen} />)
  fireEvent.click(screen.getByRole('button', { name: 'Browse this PC' }))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Browse this PC' })).toBeEnabled())
  expect(change).not.toHaveBeenCalled()
  expect(screen.getByLabelText('Terraria dedicated server')).toHaveValue('C:\\Synthetic\\TerrariaServer.exe')
})

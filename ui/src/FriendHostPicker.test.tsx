import { fireEvent, render, screen, within } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import type { FriendSnapshot } from './contracts'
import { FriendHostPicker } from './FriendHostPicker'

const connection = (id: string, state: string): FriendSnapshot => ({ mode: 'Friend', state, detail: '',
  endpoint: 'https://192.0.2.1:5131', lastConnectedUtc: null, remoteControlsEnabled: false, canStart: false, canStop: false,
  profiles: [], connectionId: id, connectionName: id, connections: null, hostCapabilities: [] })
const connections = [connection('Removed Host', 'Revoked'), connection('Offline favorite', 'Disconnected/Unknown'),
  connection('Active Host', 'Connected')]

it('uses the shared Host name while preserving the searchable local alias and falling back when cleared', () => {
  const props = { selectedId: 'one', showRemoved: false, favorites: [], busy: false, onShowRemoved: vi.fn(),
    onSelect: vi.fn(), onFavorite: vi.fn(), onForget: vi.fn() }
  const saved = { ...connection('one', 'Connected'), connectionName: 'Weekend PC', hostProfileName: 'Alex' }
  const { container, rerender } = render(<FriendHostPicker {...props} connections={[saved]} />)
  expect(container.querySelector('summary')).toHaveAccessibleName('Selected Host: Alex')
  container.querySelector('details')!.open = true
  expect(screen.getByText('Saved as: Weekend PC')).toBeInTheDocument()
  fireEvent.change(screen.getByRole('textbox'), { target: { value: 'Weekend PC' } })
  expect(screen.getByRole('button', { name: /Alex/ })).toBeInTheDocument()
  for (const hostProfileName of [undefined, null, '', '  ']) {
    rerender(<FriendHostPicker {...props} connections={[{ ...saved, hostProfileName }]} />)
    expect(container.querySelector('summary')).toHaveAccessibleName('Selected Host: Weekend PC')
  }
})

it('hides removed Hosts from names and search, orders active Hosts first, and never implies a hidden selection', () => {
  const onSelect = vi.fn()
  const { container } = render(<FriendHostPicker connections={connections} selectedId="Removed Host" showRemoved={false}
    favorites={['Offline favorite', 'Removed Host']} busy={false} onShowRemoved={vi.fn()} onSelect={onSelect}
    onFavorite={vi.fn()} onForget={vi.fn()} />)
  expect(screen.queryByText('Removed Host')).not.toBeInTheDocument()
  expect(container.querySelector('summary')).toHaveAccessibleName('Selected Host: None selected')
  container.querySelector('details')!.open = true
  const choices = screen.getByLabelText('Selected Host choices')
  expect(within(choices).getAllByRole('button').filter(button => button.textContent?.includes('Host') || button.textContent?.includes('favorite'))[0])
    .toHaveTextContent('Active Host')
  fireEvent.change(screen.getByRole('textbox'), { target: { value: 'Removed Host' } })
  expect(screen.queryByText('Removed Host')).not.toBeInTheDocument()
  expect(screen.getByText(/No matches/)).toBeInTheDocument()
  expect(onSelect).not.toHaveBeenCalled()
})

it('reveals removed Hosts only by choice and deletes the exact record without selecting it', () => {
  const onShowRemoved = vi.fn(), onForget = vi.fn(), onSelect = vi.fn()
  const props = { connections, selectedId: 'Active Host', favorites: [], busy: false,
    onShowRemoved, onForget, onSelect, onFavorite: vi.fn() }
  const { container, rerender } = render(<FriendHostPicker {...props} showRemoved={false} />)
  container.querySelector('details')!.open = true
  fireEvent.click(screen.getByRole('checkbox', { name: 'Show removed Hosts' }))
  expect(onShowRemoved).toHaveBeenCalledWith(true)
  rerender(<FriendHostPicker {...props} showRemoved />)
  const remove = screen.getByRole('button', { name: 'Delete saved Host Removed Host' })
  fireEvent.click(remove)
  expect(onForget).toHaveBeenCalledWith('Removed Host')
  expect(onSelect).not.toHaveBeenCalled()
  rerender(<FriendHostPicker {...props} showRemoved busy />)
  expect(remove).toBeDisabled()
})

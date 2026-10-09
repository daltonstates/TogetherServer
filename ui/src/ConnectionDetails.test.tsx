import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ConnectionDetails, splitJoinAddress, type ConnectionField } from './ConnectionDetails'

const field = (value = 'game.example:19132'): ConnectionField => ({
  id: 'join', label: 'Server IP', value, revealed: false, copying: false, revealing: false,
  onReveal: vi.fn(), onHide: vi.fn(), onCopy: vi.fn()
})
afterEach(() => vi.useRealTimers())

describe('separate game address and port', () => {
  it.each([
    ['192.0.2.10:19132', '192.0.2.10', '19132'],
    ['game.example:7777', 'game.example', '7777'],
    ['server:1', 'server', '1'],
    ['game.example.:65535', 'game.example.', '65535'],
    ['[2001:db8::1]:19132', '2001:db8::1', '19132'],
    ['[::ffff:192.0.2.10]:7777', '::ffff:192.0.2.10', '7777']
  ])('splits %s into game fields without URL or launcher behavior', (value, address, port) => {
    expect(splitJoinAddress(value)).toEqual({ address, port })
  })

  it.each(['', 'game.example', '2001:db8::1:19132', '[invalid]:19132', '[2001:::1]:19132',
    '[fe80::1%25ethernet]:19132', '256.0.2.10:7777', '192.0.2:7777', '192.0.2.010:7777',
    'game.example:0', 'game.example:65536', 'game.example:-1', 'game.example:80/path',
    'https://game.example:19132', 'name@game.example:19132', 'game example:7777', '-bad.example:7777',
    'game.example:7777?secret', 'game.example\u202e:7777'])('rejects malformed or ambiguous endpoint %s', value => {
    expect(splitJoinAddress(value)).toBeNull()
  })

  it('copies hidden address and port independently, without rendering either secret', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    const source = field('[2001:db8::123]:19132')
    const view = render(<ConnectionDetails kind="MinecraftBedrock" fields={[source]} />)
    expect(view.container.innerHTML).not.toContain('2001:db8::123')
    expect(view.container.innerHTML).not.toContain('19132')
    fireEvent.click(screen.getByRole('button', { name: 'Copy Server address' }))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Server address copied'))
    expect(writeText).toHaveBeenCalledWith('2001:db8::123')
    fireEvent.click(screen.getByRole('button', { name: 'Copy Port' }))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Port copied'))
    expect(writeText).toHaveBeenLastCalledWith('19132')
    expect(source.onCopy).not.toHaveBeenCalled()
    expect(view.container.innerHTML).not.toContain('2001:db8::123')
    expect(view.container.innerHTML).not.toContain('19132')
  })

  it('reveals only the chosen field, then hides it on a synthetic blur or timeout', () => {
    vi.useFakeTimers()
    const view = render(<ConnectionDetails kind="Terraria" fields={[field('192.0.2.10:7777')]} />)
    fireEvent.click(screen.getByRole('button', { name: 'Show Server address' }))
    expect(screen.getByText('192.0.2.10')).toBeInTheDocument()
    expect(view.container.innerHTML).not.toContain('7777')
    fireEvent.blur(window)
    expect(screen.queryByText('192.0.2.10')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Show Port' }))
    expect(screen.getByText('7777')).toBeInTheDocument()
    expect(view.container.innerHTML).not.toContain('192.0.2.10')
    act(() => vi.advanceTimersByTime(30_000))
    expect(screen.queryByText('7777')).not.toBeInTheDocument()
  })

  it('hides recovered fields when the source endpoint changes and ignores an old copy receipt', async () => {
    let finish: (() => void) | undefined
    const onCopyPart = vi.fn(() => new Promise<void>(resolve => { finish = resolve }))
    const view = render(<ConnectionDetails kind="Terraria" fields={[field('192.0.2.10:7777')]} onCopyPart={onCopyPart} />)
    fireEvent.click(screen.getByRole('button', { name: 'Show Server address' }))
    fireEvent.click(screen.getByRole('button', { name: 'Copy Port' }))
    view.rerender(<ConnectionDetails kind="Terraria" fields={[field('192.0.2.11:7778')]} onCopyPart={onCopyPart} />)
    await act(async () => finish?.())
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    expect(view.container.innerHTML).not.toContain('192.0.2.11')
    expect(view.container.innerHTML).not.toContain('7778')
    expect(screen.getByRole('button', { name: 'Copy Port' })).toBeEnabled()
    expect(onCopyPart).toHaveBeenCalledExactlyOnceWith('port', '7777')
  })

  it('does not copy while refreshing and gives a fixed clipboard failure without leaking values', async () => {
    const onCopyPart = vi.fn().mockRejectedValue(new Error('Private endpoint 192.0.2.10:7777'))
    const view = render(<ConnectionDetails kind="Terraria" fields={[field('192.0.2.10:7777')]} refreshing onCopyPart={onCopyPart} />)
    fireEvent.click(screen.getByRole('button', { name: 'Copy Port' }))
    expect(onCopyPart).not.toHaveBeenCalled()
    view.rerender(<ConnectionDetails kind="Terraria" fields={[field('192.0.2.10:7777')]} onCopyPart={onCopyPart} />)
    fireEvent.click(screen.getByRole('button', { name: 'Copy Port' }))
    expect(await screen.findByRole('status')).toHaveTextContent('Could not copy')
    expect(view.container.innerHTML).not.toContain('192.0.2.10')
  })

  it('keeps other private fields and the existing combined control independent', () => {
    const password = { ...field('test-secret'), id: 'password', label: 'Game password' }
    const view = render(<ConnectionDetails kind="MinecraftBedrock" endpointFieldId="join" fields={[field(), password]} />)
    expect(screen.getByRole('button', { name: 'Copy Game password' })).toBeInTheDocument()
    expect(view.container.innerHTML).not.toContain('test-secret')
    const combined = field('192.0.2.10:2456')
    view.rerender(<ConnectionDetails kind="Valheim" fields={[combined]} />)
    fireEvent.click(screen.getByRole('button', { name: 'Copy Server IP' }))
    expect(combined.onCopy).toHaveBeenCalledOnce()
    expect(screen.queryByRole('button', { name: 'Copy Port' })).not.toBeInTheDocument()
  })
})

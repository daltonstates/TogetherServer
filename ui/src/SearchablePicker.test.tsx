import { fireEvent, render, screen } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { SearchablePicker } from './SearchablePicker'

const items = [{ id: 'a', name: 'Alderheim', detail: 'Valheim', keywords: 'forest' }, { id: 'b', name: 'Cedar', detail: 'Minecraft' }]

it('finds a world by game or world name and returns focus after selecting', () => {
  const onSelect = vi.fn()
  const { container } = render(<SearchablePicker label="Selected server" items={items} selectedId="a" onSelect={onSelect} />)
  const details = container.querySelector('details')!
  const summary = container.querySelector('summary')!
  details.open = true
  fireEvent.change(screen.getByRole('textbox', { name: 'Find selected server' }), { target: { value: 'minecraft' } })
  expect(screen.queryByRole('button', { name: /Alderheim/ })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: /Cedar/ }))
  expect(onSelect).toHaveBeenCalledWith('b')
  expect(details.open).toBe(false)
  expect(summary).toHaveFocus()
})

it('dismisses with Escape and outside touch without selecting a different server', () => {
  const onSelect = vi.fn()
  const { container } = render(<SearchablePicker label="Selected Host" items={items} selectedId="a" onSelect={onSelect} />)
  const details = container.querySelector('details')!
  details.open = true
  fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Escape' })
  expect(details.open).toBe(false)
  expect(container.querySelector('summary')).toHaveFocus()
  details.open = true
  fireEvent.pointerDown(document.body, { pointerType: 'touch' })
  expect(details.open).toBe(false)
  expect(onSelect).not.toHaveBeenCalled()
})

it('keeps the picker open while scrolling choices and dismisses when its sidebar scrolls', () => {
  const onSelect = vi.fn()
  const { container } = render(<aside><SearchablePicker label="Selected server" items={items}
    selectedId="a" onSelect={onSelect} /></aside>)
  const details = container.querySelector('details')!
  details.open = true
  fireEvent.scroll(screen.getByLabelText('Selected server choices'))
  expect(details.open).toBe(true)
  fireEvent.scroll(container.querySelector('aside')!)
  expect(details.open).toBe(false)
  expect(onSelect).not.toHaveBeenCalled()
})

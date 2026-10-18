import { fireEvent, render, screen } from '@testing-library/react'
import { expect, it } from 'vitest'
import { ServerSettingsWorkspace } from './ServerSettingsWorkspace'

it('supports keyboard section selection and retains unfinished fields in both editors', () => {
  render(<ServerSettingsWorkspace settings={<label>Difficulty draft<input defaultValue="Hard" /></label>}
    files={<label>File draft<textarea defaultValue="Unfinished file" /></label>} />)
  const settings = screen.getByRole('tab', { name: 'Game settings' })
  const files = screen.getByRole('tab', { name: 'Server files' })
  fireEvent.change(screen.getByRole('textbox', { name: 'Difficulty draft' }), { target: { value: 'Easy' } })
  fireEvent.keyDown(settings, { key: 'ArrowRight' })
  expect(files).toHaveFocus()
  expect(files).toHaveAttribute('aria-selected', 'true')
  fireEvent.change(screen.getByRole('textbox', { name: 'File draft' }), { target: { value: 'Latest keystroke' } })
  fireEvent.keyDown(files, { key: 'Home' })
  expect(settings).toHaveFocus()
  expect(screen.getByRole('textbox', { name: 'Difficulty draft' })).toHaveValue('Easy')
  fireEvent.keyDown(settings, { key: 'End' })
  expect(screen.getByRole('textbox', { name: 'File draft' })).toHaveValue('Latest keystroke')
})

import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Button, Input } from './Controls'
import { SettingsCard, SettingsDialog, SettingsSectionHeading } from './SettingsPresentation'

describe('SettingsCard', () => {
  it('exposes a named action and readable summary before opening its editor', () => {
    const open = vi.fn()
    render(<SettingsCard icon="palette" title="Appearance" description="Choose how the app looks."
      summary={<span>Orange accent · 110% text</span>} actionLabel="Customize" onOpen={open} />)
    const card = screen.getByRole('button', { name: 'Appearance Customize' })
    expect(card).toHaveAccessibleDescription('Choose how the app looks. Orange accent · 110% text')
    expect(screen.getByText('Orange accent · 110% text')).toBeVisible()
    fireEvent.click(card)
    expect(open).toHaveBeenCalledOnce()
  })

  it('does not open an unavailable settings action', () => {
    const open = vi.fn()
    render(<SettingsCard icon="monitor" title="Windows" actionLabel="Manage" onOpen={open} disabled />)
    const card = screen.getByRole('button', { name: 'Windows Manage' })
    expect(card).toBeDisabled()
    fireEvent.click(card)
    expect(open).not.toHaveBeenCalled()
  })
})

describe('SettingsDialog', () => {
  const originalShowModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal')
  const originalClose = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close')
  const showModal = vi.fn(function (this: HTMLDialogElement) {
    this.setAttribute('open', '')
    this.querySelector<HTMLButtonElement>('.settings-dialog-close:not(:disabled)')?.focus()
  })
  const close = vi.fn(function (this: HTMLDialogElement) { this.removeAttribute('open') })

  beforeEach(() => {
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: showModal })
    Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: close })
  })

  afterEach(() => {
    if (originalShowModal) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', originalShowModal)
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal')
    if (originalClose) Object.defineProperty(HTMLDialogElement.prototype, 'close', originalClose)
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'close')
  })

  it('opens a named native modal with its description and a separate action footer', () => {
    const onClose = vi.fn()
    const onSave = vi.fn()
    const view = render(<SettingsDialog title="Windows preferences" description="Choose what happens at sign-in and when closing."
      onClose={onClose} footer={<div className="actions"><Button onClick={onSave}>Save preferences</Button></div>}>
      <label>Preferred page<Input defaultValue="Host" /></label>
    </SettingsDialog>)
    const dialog = screen.getByRole('dialog', { name: 'Windows preferences' })
    expect(dialog).toHaveAccessibleDescription('Choose what happens at sign-in and when closing.')
    expect(dialog).toHaveAttribute('aria-modal', 'true')
    expect(dialog).toHaveAttribute('open')
    expect(showModal).toHaveBeenCalledOnce()
    expect(screen.getByRole('textbox', { name: 'Preferred page' }).closest('.settings-dialog-body')).not.toBeNull()
    expect(screen.getByRole('button', { name: 'Save preferences' }).closest('.settings-dialog-footer')).not.toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Save preferences' }))
    expect(onSave).toHaveBeenCalledOnce()
    fireEvent.click(screen.getByRole('button', { name: 'Close Windows preferences' }))
    expect(onClose).toHaveBeenCalledOnce()
    view.unmount()
    expect(close).toHaveBeenCalledOnce()
  })

  it('blocks native cancel and Close while saving, then permits cancel with the latest callback', () => {
    const initialClose = vi.fn()
    const latestClose = vi.fn()
    const view = render(<SettingsDialog title="Hosting policy" onClose={initialClose} busy><p>Saving changes…</p></SettingsDialog>)
    const dialog = screen.getByRole('dialog', { name: 'Hosting policy' })
    const cancel = new Event('cancel', { cancelable: true })
    fireEvent(dialog, cancel)
    fireEvent.click(screen.getByRole('button', { name: 'Close Hosting policy' }))
    expect(cancel.defaultPrevented).toBe(true)
    expect(initialClose).not.toHaveBeenCalled()
    expect(dialog).toHaveAttribute('open')
    expect(dialog).toHaveAttribute('aria-busy', 'true')
    expect(screen.getByRole('button', { name: 'Close Hosting policy' })).toBeDisabled()

    view.rerender(<SettingsDialog title="Hosting policy" onClose={latestClose}><p>Saved.</p></SettingsDialog>)
    fireEvent(dialog, new Event('cancel', { cancelable: true }))
    expect(initialClose).not.toHaveBeenCalled()
    expect(latestClose).toHaveBeenCalledOnce()
    expect(showModal).toHaveBeenCalledOnce()
    expect(close).not.toHaveBeenCalled()
    view.unmount()
  })

  it('keeps Escape within the modal so a workspace shortcut cannot dismiss underlying UI', () => {
    const onClose = vi.fn()
    const shortcut = vi.fn()
    const view = render(<SettingsDialog title="Appearance" onClose={onClose} busy><Input aria-label="Accent name" /></SettingsDialog>)
    window.addEventListener('keydown', shortcut)
    try {
      const escape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
      fireEvent(screen.getByRole('textbox', { name: 'Accent name' }), escape)
      expect(escape.defaultPrevented).toBe(true)
      expect(shortcut).not.toHaveBeenCalled()
      expect(onClose).not.toHaveBeenCalled()
    } finally {
      window.removeEventListener('keydown', shortcut)
      view.unmount()
    }
  })

  it('preserves field focus on refresh and returns focus to the connected opener on unmount', () => {
    render(<Button>Open appearance</Button>)
    const opener = screen.getByRole('button', { name: 'Open appearance' })
    opener.focus()
    const view = render(<SettingsDialog title="Appearance" onClose={vi.fn()}><Input aria-label="Accent name" /></SettingsDialog>)
    const input = screen.getByRole('textbox', { name: 'Accent name' })
    input.focus()
    view.rerender(<SettingsDialog title="Appearance" description="Updated settings" onClose={vi.fn()}>
      <Input aria-label="Accent name" />
    </SettingsDialog>)
    expect(input).toHaveFocus()
    expect(showModal).toHaveBeenCalledOnce()
    expect(close).not.toHaveBeenCalled()
    view.unmount()
    expect(opener).toHaveFocus()
    expect(close).toHaveBeenCalledOnce()
  })

  it('does not refocus an opener that has been removed from the workspace', () => {
    const opener = document.createElement('button')
    document.body.append(opener)
    opener.focus()
    const focus = vi.spyOn(opener, 'focus')
    const view = render(<SettingsDialog title="Appearance" onClose={vi.fn()}><p>Appearance controls</p></SettingsDialog>)
    opener.remove()
    view.unmount()
    expect(focus).not.toHaveBeenCalled()
    expect(close).toHaveBeenCalledOnce()
  })
})

describe('SettingsSectionHeading', () => {
  it('keeps section names and purpose visible beside a decorative icon', () => {
    render(<SettingsSectionHeading icon="shield" title="Friend access" description="Choose what connected PCs may do." />)
    expect(screen.getByRole('heading', { name: 'Friend access', level: 2 })).toBeVisible()
    expect(screen.getByText('Choose what connected PCs may do.')).toBeVisible()
  })
})

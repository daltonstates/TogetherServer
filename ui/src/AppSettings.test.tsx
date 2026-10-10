import type { ComponentProps } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AppSettings } from './AppSettings'
import { defaultUiPreferences } from './qolPreferences'

vi.mock('./DesktopPreferencesPanel', () => ({
  DesktopPreferencesPanel: ({ onBusyChange }: { onBusyChange: (busy: boolean) => void }) => <section aria-label="Notification editor">
    <button onClick={() => onBusyChange(true)}>Save notification choice</button><button onClick={() => onBusyChange(false)}>Complete notification save</button></section>,
  UpdateDetailsPanel: () => <section aria-label="Update details">Release details</section>
}))

beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: function (this: HTMLDialogElement) { this.setAttribute('open', '') } })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: function (this: HTMLDialogElement) { this.removeAttribute('open') } })
})

function props(): ComponentProps<typeof AppSettings> {
  return { preferences: { ...defaultUiPreferences }, onPreferencesChange: vi.fn(), servers: [],
    desktop: { available: true, launchAtLogin: false, closeToTray: false, startupAvailable: true },
    instance: null, update: null, busy: false, desktopBusy: false, updateBusy: false,
    onDesktopChange: vi.fn(), notice: null, onClearNotice: vi.fn(), onCheckUpdate: vi.fn(async () => false),
    onReviewUpdate: vi.fn(), onReloadUpdate: vi.fn(), onQuit: vi.fn() }
}

describe('Compact app settings', () => {
  it('keeps the dialog open until its notification editor has finished saving', () => {
    render(<AppSettings {...props()} />)
    fireEvent.click(screen.getByRole('button', { name: 'Notifications Edit notifications' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save notification choice' }))
    const dialog = screen.getByRole('dialog', { name: 'Notifications' })
    expect(screen.getByRole('button', { name: 'Done' })).toBeDisabled()
    const cancel = new Event('cancel', { cancelable: true })
    fireEvent(dialog, cancel)
    expect(cancel.defaultPrevented).toBe(true)
    expect(dialog).toHaveAttribute('open')
    fireEvent.click(screen.getByRole('button', { name: 'Complete notification save' }))
    fireEvent.click(screen.getByRole('button', { name: 'Done' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('starts with summaries, opens only the chosen editor, and applies appearance changes directly', () => {
    const input = props()
    render(<AppSettings {...input} />)
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Notification editor' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Appearance Edit appearance' }))
    fireEvent.change(screen.getByRole('combobox', { name: 'Theme' }), { target: { value: 'light' } })
    expect(input.onPreferencesChange).toHaveBeenCalledWith({ ...input.preferences, theme: 'light' })
    expect(input.onClearNotice).toHaveBeenCalledOnce()
    fireEvent.click(screen.getByRole('button', { name: 'Done' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('keeps Windows failures visible inside the modal and respects unavailable controls', () => {
    const input = props()
    const view = render(<AppSettings {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Windows behavior Edit Windows behavior' }))
    fireEvent.click(screen.getByRole('checkbox', { name: /Close to tray/ }))
    expect(input.onDesktopChange).toHaveBeenCalledWith({ closeToTray: true })
    view.rerender(<AppSettings {...input} notice={{ good: false, text: 'Windows preference could not be saved.' }} />)
    expect(within(screen.getByRole('dialog', { name: 'Windows behavior' })).getByRole('status')).toHaveTextContent('Windows preference could not be saved.')
    view.rerender(<AppSettings {...input} desktop={{ ...input.desktop!, available: false }} />)
    expect(screen.getByRole('checkbox', { name: /Close to tray/ })).toBeDisabled()
  })

  it('closes its update editor before requesting the separate update review dialog', async () => {
    const input = props()
    vi.mocked(input.onCheckUpdate).mockResolvedValue(true)
    render(<AppSettings {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'App updates Manage updates' }))
    fireEvent.click(screen.getByRole('button', { name: 'Check for updates' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(input.onReviewUpdate).toHaveBeenCalledOnce()
  })

  it('retains the update editor for retry when the check fails', async () => {
    const input = props()
    const view = render(<AppSettings {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'App updates Manage updates' }))
    fireEvent.click(screen.getByRole('button', { name: 'Check for updates' }))
    await waitFor(() => expect(input.onCheckUpdate).toHaveBeenCalledOnce())
    view.rerender(<AppSettings {...input} notice={{ good: false, text: 'Update check unavailable.' }} />)
    expect(within(screen.getByRole('dialog', { name: 'App updates' })).getByRole('status')).toHaveTextContent('Update check unavailable.')
    expect(input.onReviewUpdate).not.toHaveBeenCalled()
  })
})

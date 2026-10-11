import type { ComponentProps } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { AppSettings } from './AppSettings'
import { defaultUiPreferences } from './qolPreferences'

vi.mock('./DesktopPreferencesPanel', () => ({
  DesktopPreferencesPanel: ({ onBusyChange, servers }: { onBusyChange: (busy: boolean) => void; servers?: unknown[] }) => <section aria-label="Notification editor">
    <span>{servers ? 'Server selector supplied' : 'PC defaults only'}</span>
    <button onClick={() => onBusyChange(true)}>Save notification choice</button><button onClick={() => onBusyChange(false)}>Complete notification save</button></section>,
  UpdateDetailsPanel: () => <section aria-label="Update details">Release details</section>
}))

function props(): ComponentProps<typeof AppSettings> {
  return { preferences: { ...defaultUiPreferences }, onPreferencesChange: vi.fn(), servers: [{ id: 'saved', name: 'Saved server' }],
    desktop: { available: true, launchAtLogin: false, closeToTray: false, startupAvailable: true },
    instance: null, update: null, busy: false, desktopBusy: false, updateBusy: false,
    onDesktopChange: vi.fn(), notice: null, onClearNotice: vi.fn(), onCheckUpdate: vi.fn(async () => false),
    onReviewUpdate: vi.fn(), onReloadUpdate: vi.fn(), onQuit: vi.fn() }
}

describe('App Settings pages', () => {
  it('opens Appearance directly and applies changes without opening a dialog', () => {
    const input = props()
    render(<AppSettings {...input} />)
    expect(screen.getByRole('heading', { name: 'Appearance' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    fireEvent.change(screen.getByRole('combobox', { name: 'Theme' }), { target: { value: 'light' } })
    expect(input.onPreferencesChange).toHaveBeenCalledWith({ ...input.preferences, theme: 'light' })
  })

  it('uses the controlled section without duplicating sidebar navigation or local tabs', () => {
    const input = props()
    const onSectionChange = vi.fn()
    const view = render(<AppSettings {...input} section="windows" onSectionChange={onSectionChange} />)
    expect(screen.getByRole('heading', { name: 'Windows behavior' })).toBeInTheDocument()
    expect(screen.queryByRole('navigation')).not.toBeInTheDocument()
    view.rerender(<AppSettings {...input} section="shortcuts" onSectionChange={onSectionChange} />)
    expect(screen.getByRole('heading', { name: 'Keyboard shortcuts' })).toBeInTheDocument()
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
    expect(onSectionChange).not.toHaveBeenCalled()
  })

  it('uses fallback navigation and reports section changes while global notifications receive only PC defaults', () => {
    const input = props()
    const onSectionChange = vi.fn()
    render(<AppSettings {...input} onSectionChange={onSectionChange} />)
    fireEvent.click(screen.getByRole('button', { name: 'Notifications' }))
    expect(onSectionChange).toHaveBeenCalledExactlyOnceWith('notifications')
    expect(input.onClearNotice).toHaveBeenCalledOnce()
    expect(screen.getByText('PC defaults only')).toBeInTheDocument()
    expect(screen.queryByText('Server selector supplied')).not.toBeInTheDocument()
  })

  it('preserves onClose and prevents fallback navigation or closing during a notification save', () => {
    const onClose = vi.fn()
    const onBusyChange = vi.fn()
    render(<AppSettings {...props()} onClose={onClose} onBusyChange={onBusyChange} />)
    fireEvent.click(screen.getByRole('button', { name: 'Notifications' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save notification choice' }))
    expect(onBusyChange).toHaveBeenLastCalledWith(true)
    expect(screen.getByRole('button', { name: 'Done' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Appearance' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Done' }))
    expect(onClose).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Complete notification save' }))
    expect(onBusyChange).toHaveBeenLastCalledWith(false)
    fireEvent.click(screen.getByRole('button', { name: 'Done' }))
    expect(onClose).toHaveBeenCalledOnce()
  })

  it('keeps Windows failures visible, preserves unavailable controls and excludes hosting power settings', () => {
    const input = props()
    const keepAwake = { enabled: true, onChange: vi.fn() }
    const view = render(<AppSettings {...input} section="windows" keepAwake={keepAwake} />)
    fireEvent.click(screen.getByRole('checkbox', { name: /Close to tray/ }))
    expect(input.onDesktopChange).toHaveBeenCalledWith({ closeToTray: true })
    expect(screen.queryByText('Keep Windows awake while hosting')).not.toBeInTheDocument()
    view.rerender(<AppSettings {...input} section="windows" desktop={{ ...input.desktop!, available: false }}
      notice={{ good: false, text: 'Windows preference could not be saved.' }} />)
    expect(screen.getByText('Windows preference could not be saved.')).toHaveAttribute('role', 'status')
    expect(screen.getByRole('checkbox', { name: /Close to tray/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quit TogetherServer' })).toBeDisabled()
    expect(keepAwake.onChange).not.toHaveBeenCalled()
  })

  it('hands an available update to the review dialog while retaining the settings page', async () => {
    const input = props()
    vi.mocked(input.onCheckUpdate).mockResolvedValue(true)
    render(<AppSettings {...input} section="updates" />)
    fireEvent.click(screen.getByRole('button', { name: 'Check for updates' }))
    await waitFor(() => expect(input.onReviewUpdate).toHaveBeenCalledOnce())
    expect(screen.getByRole('heading', { name: 'App updates' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('keeps failed update checks retryable on the same page', async () => {
    const input = props()
    const view = render(<AppSettings {...input} section="updates" />)
    fireEvent.click(screen.getByRole('button', { name: 'Check for updates' }))
    await waitFor(() => expect(input.onCheckUpdate).toHaveBeenCalledOnce())
    view.rerender(<AppSettings {...input} section="updates" notice={{ good: false, text: 'Update check unavailable.' }} />)
    expect(screen.getByRole('status')).toHaveTextContent('Update check unavailable.')
    expect(input.onReviewUpdate).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Check for updates' })).toBeEnabled()
    expect(within(screen.getByRole('heading', { name: 'App updates' }).parentElement!).queryByRole('dialog')).not.toBeInTheDocument()
  })
})

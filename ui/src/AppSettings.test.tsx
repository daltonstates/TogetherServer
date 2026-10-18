import type { ComponentProps } from 'react'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { AppSettings } from './AppSettings'
import { parseDesktopUpdateDetails, type DesktopUpdateDetails } from './desktopPreferencesWire'
import { defaultUiPreferences } from './qolPreferences'

vi.mock('./DesktopPreferencesPanel', () => ({
  DesktopPreferencesPanel: ({ onBusyChange, servers }: { onBusyChange: (busy: boolean) => void; servers?: unknown[] }) => <section aria-label="Notification editor">
    <span>{servers ? 'Server selector supplied' : 'PC defaults only'}</span>
    <button onClick={() => onBusyChange(true)}>Save notification choice</button><button onClick={() => onBusyChange(false)}>Complete notification save</button></section>,
  UpdateDetailsPanel: () => <section aria-label="Update details">Release details</section>
}))

function props(): ComponentProps<typeof AppSettings> {
  return { preferences: { ...defaultUiPreferences }, onPreferencesChange: vi.fn(),
    desktop: { available: true, launchAtLogin: false, closeToTray: false, startupAvailable: true },
    instance: null, update: null, busy: false, desktopBusy: false, updateBusy: false,
    onDesktopChange: vi.fn(), notice: null, onClearNotice: vi.fn(), onCheckUpdate: vi.fn(async () => false),
    onReviewUpdate: vi.fn(), onReloadUpdate: vi.fn(), onQuit: vi.fn() }
}

function updateView(includePrereleases = false): DesktopUpdateDetails {
  return { state: 'Current', currentVersion: '0.3.2', latestVersion: null,
    message: 'TogetherServer is up to date.', publisherTrust: 'GitHub digest only', includePrereleases, latestIsPrerelease: false }
}

function jsonResponse(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
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
    const view = render(<AppSettings {...input} section="windows" />)
    fireEvent.click(screen.getByRole('checkbox', { name: /Close to tray/ }))
    expect(input.onDesktopChange).toHaveBeenCalledWith({ closeToTray: true })
    expect(screen.queryByText('Keep Windows awake while hosting')).not.toBeInTheDocument()
    view.rerender(<AppSettings {...input} section="windows" desktop={{ ...input.desktop!, available: false }}
      notice={{ good: false, text: 'Windows preference could not be saved.' }} />)
    expect(screen.getByText('Windows preference could not be saved.')).toHaveAttribute('role', 'status')
    expect(screen.getByRole('checkbox', { name: /Close to tray/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quit TogetherServer' })).toBeDisabled()
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

describe('prerelease update preferences', () => {
  it('keeps an unknown saved choice disabled and defaults older update responses to off', () => {
    const input = props()
    const view = render(<AppSettings {...input} section="updates" />)
    const checkbox = screen.getByRole('checkbox', { name: 'Include prerelease versions' })
    expect(checkbox).not.toBeChecked()
    expect(checkbox).toBeDisabled()
    const legacy = parseDesktopUpdateDetails({ state: 'Current', currentVersion: '0.3.2', latestVersion: null,
      message: 'TogetherServer is up to date.', publisherTrust: 'GitHub digest only' })
    view.rerender(<AppSettings {...input} section="updates" update={legacy} />)
    expect(checkbox).not.toBeChecked()
    expect(checkbox).toBeEnabled()
    expect(checkbox).toHaveAccessibleDescription(/Applies only to this PC.*may be unvalidated.*requires Update and restart/)
    expect(input.onReviewUpdate).not.toHaveBeenCalled()
  })

  it('saves only the checkbox choice, waits for canonical refresh and never opens update review or installation', async () => {
    let finishSave!: (response: Response) => void
    let finishReload!: () => void
    const request = vi.fn(() => new Promise<Response>(resolve => { finishSave = resolve }))
    vi.stubGlobal('fetch', request)
    const input = props()
    const current = updateView()
    const onClose = vi.fn()
    const onBusyChange = vi.fn()
    const onReloadUpdate = vi.fn(() => new Promise<void>(resolve => { finishReload = resolve }))
    const view = render(<AppSettings {...input} update={current} onClose={onClose} onBusyChange={onBusyChange} onReloadUpdate={onReloadUpdate} />)
    fireEvent.click(screen.getByRole('button', { name: 'App updates' }))
    const checkbox = screen.getByRole('checkbox', { name: 'Include prerelease versions' })
    fireEvent.click(checkbox)
    expect(request).toHaveBeenCalledExactlyOnceWith('/api/local/update/preferences', {
      method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' },
      body: '{"includePrereleases":true}', signal: expect.any(AbortSignal)
    })
    expect(checkbox).not.toBeChecked()
    expect(checkbox).toBeDisabled()
    expect(screen.getByText('Saving update preference…')).toHaveAttribute('role', 'status')
    expect(screen.getByRole('button', { name: 'Check for updates' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Appearance' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Done' })).toBeDisabled()
    expect(onBusyChange).toHaveBeenLastCalledWith(true)
    fireEvent.click(screen.getByRole('button', { name: 'Done' }))
    expect(onClose).not.toHaveBeenCalled()
    await act(async () => finishSave(jsonResponse({ ...current, includePrereleases: true, state: 'Available',
      latestVersion: '0.4.0', latestIsPrerelease: true, message: 'TogetherServer 0.4.0 (prerelease) is available.' })))
    await waitFor(() => expect(onReloadUpdate).toHaveBeenCalledOnce())
    expect(checkbox).toBeChecked()
    expect(checkbox).toBeDisabled()
    expect(screen.getByText('Update preference saved on this PC.')).toHaveAttribute('aria-live', 'polite')
    expect(screen.getByText('TogetherServer 0.4.0 (prerelease) is available.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Review update' })).toBeDisabled()
    await act(async () => finishReload())
    expect(checkbox).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Done' })).toBeEnabled()
    expect(onBusyChange).toHaveBeenLastCalledWith(false)
    expect(input.onCheckUpdate).not.toHaveBeenCalled()
    expect(input.onReviewUpdate).not.toHaveBeenCalled()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(request).toHaveBeenCalledOnce()
    view.rerender(<AppSettings {...input} update={{ ...current, includePrereleases: false }} onClose={onClose}
      onBusyChange={onBusyChange} onReloadUpdate={onReloadUpdate} />)
    expect(checkbox).not.toBeChecked()
  })

  it('can turn a saved prerelease choice off using the returned canonical value', async () => {
    const input = props()
    const request = vi.fn(async () => jsonResponse(updateView(false)))
    vi.stubGlobal('fetch', request)
    render(<AppSettings {...input} section="updates" update={updateView(true)} />)
    const checkbox = screen.getByRole('checkbox', { name: 'Include prerelease versions' })
    expect(checkbox).toBeChecked()
    fireEvent.click(checkbox)
    await screen.findByText('Update preference saved on this PC.')
    expect(checkbox).not.toBeChecked()
    await waitFor(() => expect(checkbox).toBeEnabled())
    expect(request).toHaveBeenCalledWith('/api/local/update/preferences', expect.objectContaining({
      method: 'PUT', body: '{"includePrereleases":false}'
    }))
    expect(input.onReloadUpdate).toHaveBeenCalledOnce()
    expect(input.onReviewUpdate).not.toHaveBeenCalled()
  })

  it.each([false, true])('preserves the last confirmed %s choice after a failed save and permits retry', async includePrereleases => {
    const input = props()
    const request = vi.fn().mockResolvedValueOnce(jsonResponse({ code: 'UpdatePreferenceSaveFailed', message: 'Could not save.' }, 500))
      .mockResolvedValueOnce(jsonResponse(updateView(!includePrereleases)))
    vi.stubGlobal('fetch', request)
    render(<AppSettings {...input} section="updates" update={updateView(includePrereleases)} />)
    const checkbox = screen.getByRole('checkbox', { name: 'Include prerelease versions' })
    fireEvent.click(checkbox)
    expect(await screen.findByRole('alert')).toHaveTextContent('Could not save the update preference. The last confirmed choice is shown. Try again.')
    expect(checkbox).toHaveProperty('checked', includePrereleases)
    expect(checkbox).toBeEnabled()
    expect(input.onReloadUpdate).not.toHaveBeenCalled()
    fireEvent.click(checkbox)
    await screen.findByText('Update preference saved on this PC.')
    expect(checkbox).toHaveProperty('checked', !includePrereleases)
    expect(input.onReloadUpdate).toHaveBeenCalledOnce()
    expect(input.onReviewUpdate).not.toHaveBeenCalled()
    expect(request).toHaveBeenCalledTimes(2)
  })

  it('retains a successfully saved choice if refreshing global update status fails', async () => {
    const input = props()
    vi.mocked(input.onReloadUpdate).mockRejectedValue(new Error('Refresh unavailable.'))
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(updateView(true))))
    render(<AppSettings {...input} section="updates" update={updateView()} />)
    const checkbox = screen.getByRole('checkbox', { name: 'Include prerelease versions' })
    fireEvent.click(checkbox)
    expect(await screen.findByRole('alert')).toHaveTextContent('Update preference saved on this PC, but update status could not be refreshed.')
    expect(checkbox).toBeChecked()
    expect(checkbox).toBeEnabled()
    expect(input.onReviewUpdate).not.toHaveBeenCalled()
  })

  it('disables prerelease preferences in staging with a visible reason and no request', () => {
    const request = vi.fn()
    vi.stubGlobal('fetch', request)
    render(<AppSettings {...props()} section="updates" update={updateView()} instance={{ kind: 'Staging',
      displayName: 'TogetherServer DEVELOPMENT', isStaging: true, freshWorldsOnly: true, startupAvailable: false,
      updatesAvailable: false, localPort: 5128, companionPort: 5132, valheimPort: 2458, minecraftJavaPort: 25566,
      minecraftBedrockPort: 19134, dataRoot: 'Synthetic staging storage', dataIsolation: 'Separate staging storage' }} />)
    const checkbox = screen.getByRole('checkbox', { name: 'Include prerelease versions' })
    expect(checkbox).toBeDisabled()
    expect(checkbox).toHaveAccessibleDescription('Disabled in staging so it cannot replace the stable app.')
    fireEvent.click(checkbox)
    expect(request).not.toHaveBeenCalled()
  })

  it.each(['busy', 'desktopBusy', 'updateBusy'] as const)('disables preference changes while %s is active', busyProperty => {
    const request = vi.fn()
    vi.stubGlobal('fetch', request)
    render(<AppSettings {...props()} {...{ [busyProperty]: true }} section="updates" update={updateView()} />)
    const checkbox = screen.getByRole('checkbox', { name: 'Include prerelease versions' })
    expect(checkbox).toBeDisabled()
    fireEvent.click(checkbox)
    expect(request).not.toHaveBeenCalled()
  })

  it('aborts an unfinished preference save when Settings unmounts and ignores its late completion', async () => {
    let finishSave!: (response: Response) => void
    const request = vi.fn<(path: string, init: RequestInit) => Promise<Response>>(() =>
      new Promise<Response>(resolve => { finishSave = resolve }))
    vi.stubGlobal('fetch', request)
    const input = props()
    const view = render(<AppSettings {...input} section="updates" update={updateView()} />)
    fireEvent.click(screen.getByRole('checkbox', { name: 'Include prerelease versions' }))
    const signal = request.mock.calls[0][1].signal
    view.unmount()
    expect(signal?.aborted).toBe(true)
    await act(async () => finishSave(jsonResponse(updateView(true))))
    expect(input.onReloadUpdate).not.toHaveBeenCalled()
    expect(input.onReviewUpdate).not.toHaveBeenCalled()
  })
})

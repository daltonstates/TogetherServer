import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { DesktopPreferencesPanel, ServerNotificationPreferences, ServerNotificationSettings, UpdateDetailsPanel } from './DesktopPreferencesPanel'
import { notificationEventKinds, parseDesktopUpdateDetails, parseNotificationPreferences, parseUpdatePreparation,
  updatePreparationReport, type DesktopUpdateDetails, type NotificationPreferenceChange, type NotificationPreferences } from './desktopPreferencesWire'

const profileId = '11111111-1111-4111-8111-111111111111'
const connectionId = '22222222-2222-4222-8222-222222222222'
const preferences: NotificationPreferences = { quietMode: false, allowedEvents: [...notificationEventKinds], servers: [],
  systemState: 'AcceptsNotifications', systemAllowsNotifications: true }
const update: DesktopUpdateDetails = { state: 'Available', currentVersion: '0.3.0', latestVersion: '0.4.0',
  message: 'Available.', publisherTrust: 'GitHub digest only', includePrereleases: false, latestIsPrerelease: false,
  releaseNotes: 'Safer recovery\n<image src="https://example.com/private">',
  releaseNotesUrl: 'https://github.com/daltonstates/TogetherServer/releases/tag/v0.4.0',
  promptSnoozed: false, snoozedUntilUtc: null }

describe('desktop notification preferences', () => {
  it('saves Off and custom choices only for the selected Host server, without changing Quiet or update choices', async () => {
    const saver = vi.fn(async () => preferences)
    render(<ServerNotificationSettings serverId={profileId} serverName="Host server" loader={async () => preferences} saver={saver} />)
    const alerts = await screen.findByRole('combobox', { name: 'Server alerts' })
    fireEvent.change(alerts, { target: { value: 'off' } })
    await waitFor(() => expect(saver).toHaveBeenCalledWith({ profileId, connectionId: null, allowedEvents: [] }, expect.any(AbortSignal)))
    await waitFor(() => expect(alerts).not.toBeDisabled())
    fireEvent.change(alerts, { target: { value: 'custom' } })
    await waitFor(() => expect(saver).toHaveBeenLastCalledWith({ profileId, connectionId: null,
      allowedEvents: notificationEventKinds.filter(kind => kind !== 'Update') }, expect.any(AbortSignal)))
    expect(screen.queryByRole('checkbox', { name: /Quiet mode/ })).not.toBeInTheDocument()
  })

  it('aborts an old scoped save when another saved Host opens and ignores its late completion', async () => {
    let finish: ((next: NotificationPreferences) => void) | undefined
    let signal: AbortSignal | undefined
    const saver = vi.fn((_change: NotificationPreferenceChange, nextSignal?: AbortSignal) => {
      signal = nextSignal
      return new Promise<NotificationPreferences>(resolve => { finish = resolve })
    })
    const loader = vi.fn(async () => preferences)
    const view = render(<ServerNotificationSettings serverId={profileId} serverName="First" connectionId={connectionId} loader={loader} saver={saver} />)
    fireEvent.change(await screen.findByRole('combobox', { name: 'Server alerts' }), { target: { value: 'off' } })
    view.rerender(<ServerNotificationSettings serverId={profileId} serverName="Second" connectionId={profileId} loader={loader} saver={saver} />)
    expect(signal?.aborted).toBe(true)
    await act(async () => finish?.({ ...preferences, servers: [{ profileId, connectionId, allowedEvents: [] }] }))
    expect(await screen.findByRole('combobox', { name: 'Server alerts' })).toHaveValue('default')
    expect(screen.getByRole('heading', { name: 'Notifications for Second' })).toBeInTheDocument()
    expect(screen.queryByText('Notification preferences saved on this PC.')).not.toBeInTheDocument()
  })
  it('excludes app-update defaults when the first server override is created from inherited events', async () => {
    const saver = vi.fn(async () => preferences)
    render(<ServerNotificationPreferences server={{ id: profileId, name: 'Inherited', connectionId }} loader={async () => preferences} saver={saver} />)
    await screen.findByRole('combobox', { name: 'Server alerts' })
    fireEvent.click(screen.getByText('Choose notification events'))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Server starts and stops' }))
    await waitFor(() => expect(saver).toHaveBeenCalledWith({ profileId, connectionId,
      allowedEvents: notificationEventKinds.filter(kind => kind !== 'Lifecycle' && kind !== 'Update') }, expect.any(AbortSignal)))
  })
  it('persists quiet mode through the native service and announces the accepted result', async () => {
    const onQuietModeChange = vi.fn()
    const saver = vi.fn(async () => ({ ...preferences, quietMode: true }))
    render(<DesktopPreferencesPanel loader={async () => preferences} saver={saver} onQuietModeChange={onQuietModeChange} />)
    fireEvent.click(await screen.findByRole('checkbox', { name: /Quiet mode/ }))
    await waitFor(() => expect(saver).toHaveBeenCalledWith({ quietMode: true }, expect.any(AbortSignal)))
    expect(await screen.findByText('Notification preferences saved on this PC.')).toHaveAttribute('aria-live', 'polite')
    expect(onQuietModeChange).toHaveBeenCalledWith(true)
  })

  it('keeps server and connection scope on event changes and resets only the selected override', async () => {
    const scoped = { ...preferences, servers: [{ profileId, connectionId, allowedEvents: ['Backup' as const] }] }
    const saver = vi.fn(async () => scoped)
    render(<ServerNotificationPreferences server={{ id: profileId, name: 'Saved server', connectionId }}
      loader={async () => scoped} saver={saver} />)
    await screen.findByText('These choices apply only to this server.')
    fireEvent.click(screen.getByText('Choose notification events'))
    expect(screen.queryByRole('checkbox', { name: /Quiet mode/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('checkbox', { name: 'App updates' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Apply to')).not.toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: 'Backups and shared saves' })).toBeChecked()
    expect(screen.getByRole('checkbox', { name: 'Server starts and stops' })).not.toBeChecked()
    fireEvent.click(screen.getByRole('checkbox', { name: 'Server starts and stops' }))
    await waitFor(() => expect(saver).toHaveBeenCalledWith({ profileId, connectionId, allowedEvents: ['Lifecycle', 'Backup'] }, expect.any(AbortSignal)))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Use default choices' })).not.toBeDisabled())
    fireEvent.click(screen.getByRole('button', { name: 'Use default choices' }))
    await waitFor(() => expect(saver).toHaveBeenLastCalledWith({ profileId, connectionId, allowedEvents: null }, expect.any(AbortSignal)))
  })

  it('keeps PC defaults global even if legacy callers provide a server list', async () => {
    const saver = vi.fn(async () => preferences)
    render(<DesktopPreferencesPanel servers={[{ id: profileId, name: 'Saved server', connectionId }]}
      loader={async () => preferences} saver={saver} />)
    await screen.findByRole('checkbox', { name: /Quiet mode/ })
    fireEvent.click(screen.getByText('Choose notification events'))
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('checkbox', { name: 'App updates' }))
    await waitFor(() => expect(saver).toHaveBeenCalledWith({ allowedEvents: notificationEventKinds.filter(kind => kind !== 'Update') }, expect.any(AbortSignal)))
  })

  it('reports OS suppression separately and can recover a failed preference read', async () => {
    const loader = vi.fn().mockRejectedValueOnce(new Error('unavailable')).mockResolvedValue({ ...preferences,
      systemState: 'PresentationMode', systemAllowsNotifications: false })
    render(<DesktopPreferencesPanel loader={loader} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Try again' }))
    expect(await screen.findByText(/Windows is currently keeping notifications quiet/)).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: /Quiet mode/ })).not.toBeChecked()
  })

  it('rejects unknown events and malformed bounded Windows state', () => {
    expect(() => parseNotificationPreferences({ ...preferences, allowedEvents: ['RunCommand'] })).toThrow()
    expect(() => parseNotificationPreferences({ ...preferences, systemAllowsNotifications: false })).toThrow()
    expect(() => parseNotificationPreferences({ ...preferences, servers: [{ profileId: '../server', allowedEvents: ['Backup'] }] })).toThrow()
  })
})

describe('update details', () => {
  it('defaults missing prerelease flags to false and retains explicit saved and selected-release booleans', () => {
    const legacy = { state: 'Current', currentVersion: '0.3.0', latestVersion: null, message: 'Current.', publisherTrust: 'GitHub digest only' }
    expect(parseDesktopUpdateDetails(legacy)).toMatchObject({ includePrereleases: false, latestIsPrerelease: false })
    expect(parseDesktopUpdateDetails({ ...update, includePrereleases: true, latestIsPrerelease: true }))
      .toMatchObject({ includePrereleases: true, latestIsPrerelease: true })
    expect(parseDesktopUpdateDetails(update)).toMatchObject({ includePrereleases: false, latestIsPrerelease: false })
  })

  it('rejects present prerelease flags that are not booleans, including explicit null', () => {
    for (const field of ['includePrereleases', 'latestIsPrerelease']) {
      for (const value of [null, 'true', 'false', 0, 1, {}, []])
        expect(() => parseDesktopUpdateDetails({ ...update, [field]: value })).toThrow()
    }
  })

  it('omits idle details for a current app and retains preparation that still needs attention', () => {
    const current: DesktopUpdateDetails = { ...update, state: 'Current', preparation: {
      stage: 'Idle', message: 'No update in progress.', downloadedBytes: 0, totalBytes: null, blocker: null
    } }
    const { rerender } = render(<UpdateDetailsPanel update={current} />)
    expect(screen.queryByRole('region', { name: 'Update details' })).not.toBeInTheDocument()

    rerender(<UpdateDetailsPanel update={{ ...current, preparation: {
      stage: 'Blocked', message: 'Update needs attention.', downloadedBytes: 0, totalBytes: null,
      blocker: 'Resolve the hosted server before updating.'
    } }} />)
    expect(screen.getByRole('region', { name: 'Update details' })).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Resolve the hosted server before updating.')
    expect(screen.getByRole('button', { name: 'Download preparation report' })).toBeInTheDocument()

    rerender(<UpdateDetailsPanel update={current} />)
    expect(screen.queryByRole('region', { name: 'Update details' })).not.toBeInTheDocument()
  })

  it('renders release notes as plain text and snoozes exactly the displayed version', async () => {
    const saver = vi.fn(async () => ({ ok: true, message: 'Reminder saved.' }))
    const onSnoozed = vi.fn()
    const { container } = render(<UpdateDetailsPanel update={update} snoozeSaver={saver} onSnoozed={onSnoozed} />)
    fireEvent.click(screen.getByText('What changed in version 0.4.0'))
    expect(screen.getByText(/<image src=/)).toBeInTheDocument()
    expect(container.querySelector('img')).toBeNull()
    expect(screen.getByRole('link', { name: 'Open this release on GitHub' })).toHaveAttribute('href', update.releaseNotesUrl)
    fireEvent.change(screen.getByLabelText('Remind me'), { target: { value: 'Tomorrow' } })
    await waitFor(() => expect(saver).toHaveBeenCalledWith('0.4.0', 'Tomorrow'))
    expect(onSnoozed).toHaveBeenCalledOnce()
  })

  it('shows progress stages and blockers without claiming restart success and stops polling when idle', async () => {
    const loader = vi.fn(async () => ({ stage: 'Downloading' as const, message: 'Downloading the reviewed update.',
      downloadedBytes: 50, totalBytes: 100, blocker: null }))
    const { rerender } = render(<UpdateDetailsPanel update={update} busy preparationLoader={loader} />)
    expect(await screen.findByRole('progressbar', { name: 'Update download' })).toHaveAttribute('value', '50')
    expect(screen.getByText('50% downloaded')).toBeInTheDocument()
    rerender(<UpdateDetailsPanel update={{ ...update, preparation: { stage: 'Blocked', message: 'Update needs attention.',
      downloadedBytes: 50, totalBytes: 100, blocker: 'Stop or resolve every hosted server first.' } }} preparationLoader={loader} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Stop or resolve every hosted server first.')
    expect(loader).toHaveBeenCalledOnce()
  })

  it('describes a skipped version without displaying the storage sentinel as a calendar reminder', () => {
    render(<UpdateDetailsPanel update={{ ...update, promptSnoozed: true, versionSkipped: true,
      snoozedUntilUtc: '9999-12-31T23:59:59.9999999+00:00' }} />)
    expect(screen.getByText(/This version's reminder is skipped/)).toBeInTheDocument()
    expect(screen.queryByText(/9999/)).not.toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Resume reminders' })).toBeInTheDocument()
  })

  it('rejects foreign release links and progress outside the existing updater bounds and exports typed details only', () => {
    expect(() => parseDesktopUpdateDetails({ ...update, releaseNotesUrl: 'https://example.com/update.exe' })).toThrow()
    expect(() => parseDesktopUpdateDetails({ ...update, promptSnoozed: true, snoozedUntilUtc: null })).toThrow()
    expect(() => parseUpdatePreparation({ stage: 'Downloading', message: 'Downloading.', downloadedBytes: 201 * 1024 * 1024,
      totalBytes: null, blocker: null })).toThrow()
    const report = updatePreparationReport({ ...update, releaseNotes: 'private or lengthy notes', releaseNotesUrl: update.releaseNotesUrl },
      { stage: 'Checkpoint', message: 'Protecting local settings.', downloadedBytes: 100, totalBytes: 100, blocker: null })
    expect(report).toContain('Checkpoint')
    expect(report).not.toContain('private or lengthy notes')
    expect(report).not.toContain('github.com')
  })
})

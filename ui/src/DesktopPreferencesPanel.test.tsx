import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { DesktopPreferencesPanel, UpdateDetailsPanel } from './DesktopPreferencesPanel'
import { notificationEventKinds, parseDesktopUpdateDetails, parseNotificationPreferences, parseUpdatePreparation,
  updatePreparationReport, type DesktopUpdateDetails, type NotificationPreferences } from './desktopPreferencesWire'

const profileId = '11111111-1111-4111-8111-111111111111'
const connectionId = '22222222-2222-4222-8222-222222222222'
const preferences: NotificationPreferences = { quietMode: false, allowedEvents: [...notificationEventKinds], servers: [],
  systemState: 'AcceptsNotifications', systemAllowsNotifications: true }
const update: DesktopUpdateDetails = { state: 'Available', currentVersion: '0.3.0', latestVersion: '0.4.0',
  message: 'Available.', publisherTrust: 'GitHub digest only', releaseNotes: 'Safer recovery\n<image src="https://example.com/private">',
  releaseNotesUrl: 'https://github.com/daltonstates/TogetherServer/releases/tag/v0.4.0',
  promptSnoozed: false, snoozedUntilUtc: null }

describe('desktop notification preferences', () => {
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
    render(<DesktopPreferencesPanel servers={[{ id: profileId, name: 'Saved server', connectionId }]}
      loader={async () => scoped} saver={saver} />)
    await screen.findByRole('checkbox', { name: /Quiet mode/ })
    fireEvent.click(screen.getByText('Choose notification events'))
    fireEvent.change(screen.getByLabelText('Apply to'), { target: { value: `${connectionId}:${profileId}` } })
    expect(screen.getByRole('checkbox', { name: 'Backups and shared saves' })).toBeChecked()
    expect(screen.getByRole('checkbox', { name: 'Server starts and stops' })).not.toBeChecked()
    fireEvent.click(screen.getByRole('checkbox', { name: 'Server starts and stops' }))
    await waitFor(() => expect(saver).toHaveBeenCalledWith({ profileId, connectionId, allowedEvents: ['Lifecycle', 'Backup'] }, expect.any(AbortSignal)))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Use default choices' })).not.toBeDisabled())
    fireEvent.click(screen.getByRole('button', { name: 'Use default choices' }))
    await waitFor(() => expect(saver).toHaveBeenLastCalledWith({ profileId, connectionId, allowedEvents: null }, expect.any(AbortSignal)))
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

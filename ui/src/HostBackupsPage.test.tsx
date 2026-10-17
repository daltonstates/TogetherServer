import { act, fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { HostBackupsPage, applyProtectionPolicy, protectionPolicy } from './HostBackupsPage'
import type { Profile } from './GameProfile'
import type { Settings } from './contracts'

const profile: Profile = { id: 'world-a', kind: 'Valheim', name: 'Friends world', serverName: 'Friends world',
  crossplay: false, publicListing: false, worldId: 'WorldA', worldSource: 'New', worldDirectory: 'synthetic-world',
  gamePort: 2456, executablePath: 'synthetic-server', backups: { enabled: true, retentionCount: 5, minimumFreeSpaceMb: 1024 },
  crashRecovery: { enabled: false } }
function props() {
  return { profile, state: 'Offline', busy: false, editDisabled: false, backingUp: false,
    onBackup: vi.fn(), onRefresh: vi.fn(), onSaveProtection: vi.fn(async () => true) }
}
function content(section: string) {
  return <><div hidden={section !== 'backups'}><label>Backup draft<input defaultValue="Original" /></label></div>
    <div hidden={section !== 'sharing'}>Sharing permission controls</div></>
}
function openEditor() {
  fireEvent.click(screen.getByRole('button', { name: 'Protection settings' }))
  fireEvent.click(screen.getByRole('button', { name: 'Change protection settings' }))
  return within(screen.getByRole('dialog', { name: 'Protection settings' }))
}
describe('Host world protection workspace', () => {
  it('leads with factual summaries and makes sharing a direct navigation action without losing a backup draft', () => {
    render(<HostBackupsPage {...props()}>{content}</HostBackupsPage>)
    expect(screen.getByText('Not checked yet')).toBeVisible()
    expect(screen.getByText('Space not checked')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Backup library' })).toHaveAttribute('aria-current', 'page')
    fireEvent.change(screen.getByLabelText('Backup draft'), { target: { value: 'Keep my unsaved name' } })
    fireEvent.click(screen.getByRole('button', { name: 'Sharing & hosting' }))
    expect(screen.getByText('Sharing permission controls')).toBeVisible()
    expect(screen.getByLabelText('Backup draft')).not.toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Backup library' }))
    expect(screen.getByLabelText('Backup draft')).toHaveValue('Keep my unsaved name')
  })
  it('opens a focused editor and saves only after an explicit choice', async () => {
    const value = props()
    render(<HostBackupsPage {...value}>{content}</HostBackupsPage>)
    const editor = openEditor()
    expect(value.onSaveProtection).not.toHaveBeenCalled()
    expect(editor.getByRole('button', { name: 'Save protection settings' })).toBeDisabled()
    fireEvent.change(editor.getByRole('spinbutton', { name: /Completed backups to keep/ }), { target: { value: '8' } })
    fireEvent.click(editor.getByRole('checkbox', { name: /Restart after an unexpected server exit/ }))
    await act(async () => { fireEvent.click(editor.getByRole('button', { name: 'Save protection settings' })) })
    expect(value.onSaveProtection).toHaveBeenCalledWith(protectionPolicy(profile), {
      backups: { enabled: true, retentionCount: 8, minimumFreeSpaceMb: 1024 }, crashRecovery: { enabled: true } })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
  it('retains edits after rejection and refuses an empty or invalid numeric policy', async () => {
    const value = props(); value.onSaveProtection.mockResolvedValue(false)
    render(<HostBackupsPage {...value}>{content}</HostBackupsPage>)
    const editor = openEditor()
    fireEvent.change(editor.getByRole('spinbutton', { name: /Completed backups to keep/ }), { target: { value: '' } })
    expect(editor.getByRole('button', { name: 'Save protection settings' })).toBeDisabled()
    expect(editor.getByRole('alert')).toHaveTextContent('Keep 1–50')
    fireEvent.change(editor.getByRole('spinbutton', { name: /Completed backups to keep/ }), { target: { value: '7' } })
    await act(async () => { fireEvent.click(editor.getByRole('button', { name: 'Save protection settings' })) })
    expect(editor.getByRole('spinbutton', { name: /Completed backups to keep/ })).toHaveValue(7)
    expect(editor.getByRole('alert')).toHaveTextContent('Your edits are kept here')
  })
  it('blocks closing and repeated saves while the exact save is pending', async () => {
    let finish!: (ok: boolean) => void
    const value = { ...props(), onSaveProtection: vi.fn(() => new Promise<boolean>(resolve => { finish = resolve })) }
    render(<HostBackupsPage {...value}>{content}</HostBackupsPage>)
    const editor = openEditor()
    fireEvent.change(editor.getByRole('spinbutton', { name: /Completed backups to keep/ }), { target: { value: '9' } })
    fireEvent.click(editor.getByRole('button', { name: 'Save protection settings' }))
    expect(editor.getByRole('button', { name: 'Close Protection settings' })).toBeDisabled()
    expect(editor.getByRole('button', { name: 'Cancel' })).toBeDisabled()
    expect(editor.getByRole('button', { name: 'Saving…' })).toBeDisabled()
    expect(value.onSaveProtection).toHaveBeenCalledTimes(1)
    await act(async () => { finish(true) })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
  it('keeps unknown occupancy blocked and records a failed backup without suggesting success', () => {
    render(<HostBackupsPage {...props()} state="Unknown" status={{ profileId: profile.id, completedCount: 0,
      retainedSizeBytes: 0, availableSpaceBytes: null, lastSuccessfulUtc: null,
      lastFailureUtc: '2026-10-17T10:00:00Z', lastFailure: 'Not enough storage' }}>{content}</HostBackupsPage>)
    expect(screen.getByRole('button', { name: 'Back up now' })).toBeDisabled()
    expect(screen.getByRole('alert')).toHaveTextContent('Not enough storage')
    expect(screen.getByRole('button', { name: 'Sharing & hosting' })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: 'Protection settings' }))
    expect(screen.getByRole('button', { name: 'Change protection settings' })).toBeDisabled()
  })
  it('preserves unrelated settings and worlds, rejects stale policy and unsupported preview recovery', () => {
    const other = { ...profile, id: 'world-b', name: 'Other world' }
    const settings = { profiles: [profile, other], remoteControlsEnabled: true, idleMinutes: 50 } as Settings
    const expected = protectionPolicy(profile)
    const next = { ...expected, backups: { ...expected.backups, retentionCount: 12 } }
    const result = applyProtectionPolicy(settings, profile.id, expected, next)!
    expect(result.idleMinutes).toBe(50)
    expect(result.remoteControlsEnabled).toBe(true)
    expect(result.profiles[1]).toBe(other)
    expect(result.profiles[0].worldDirectory).toBe(profile.worldDirectory)
    expect(applyProtectionPolicy(settings, profile.id, next, expected)).toBeNull()
    expect(applyProtectionPolicy(settings, 'missing', expected, next)).toBeNull()
    expect(applyProtectionPolicy(settings, profile.id, expected, { ...next, backups: { ...next.backups, retentionCount: 0 } })).toBeNull()
    const preview = { ...profile, kind: 'Factorio' as const }
    expect(applyProtectionPolicy({ ...settings, profiles: [preview] }, preview.id, expected,
      { ...next, crashRecovery: { enabled: true } })).toBeNull()
  })
  it('keeps recovered-world automatic restart unavailable while allowing its backup policy to be edited', () => {
    const recovered = { ...profile, separateCopySourceProfileId: 'original-world', separateCopyBranchHash: 'A'.repeat(64) }
    render(<HostBackupsPage {...props()} profile={recovered}>{content}</HostBackupsPage>)
    const editor = openEditor()
    expect(editor.queryByRole('checkbox', { name: /Restart after an unexpected server exit/ })).not.toBeInTheDocument()
    expect(editor.getByText(/Start a recovered world manually/)).toBeVisible()
    expect(editor.getByRole('spinbutton', { name: /Completed backups to keep/ })).toBeEnabled()
  })
  it('keeps disposable trial protection read-only', () => {
    render(<HostBackupsPage {...props()} profile={{ ...profile, worldLoadRehearsalId: 'trial-id' }}>{content}</HostBackupsPage>)
    fireEvent.click(screen.getByRole('button', { name: 'Protection settings' }))
    expect(screen.getByRole('button', { name: 'Change protection settings' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Back up now' })).toBeDisabled()
  })
})

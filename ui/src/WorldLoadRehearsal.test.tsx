import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { parseWorldLoadView, WorldLoadRehearsalPanel } from './WorldLoadRehearsal'

const view = () => ({ id: 'b7dc82fb-256f-45f9-962a-8039f5e0b5ab', game: 'Terraria', sourceKind: 'Received',
  copyIdentity: 'A'.repeat(64), appVersion: '0.3.0', gameVersion: 'Unknown', state: 'Prepared', canLaunch: false,
  launchReason: 'Manual isolated setup required.', gamePort: 35000, worldDirectory: 'G:\\disposable', rehearsalProfileId: null,
  loadOutcome: 'Unobserved', changeOutcome: 'Unobserved', restartOutcome: 'Unobserved', managedStarts: 0,
  gracefulStops: 0, cleaned: false, preparedUtc: '2026-10-04T12:00:00Z' })
afterEach(() => vi.unstubAllGlobals())
describe('guided world load', () => {
  it('preserves unobserved and owner-reported outcomes without automatic proof', () => {
    expect(parseWorldLoadView(view()).loadOutcome).toBe('Unobserved')
    expect(parseWorldLoadView({ ...view(), loadOutcome: 'OwnerConfirmed' }).loadOutcome).toBe('OwnerConfirmed')
    expect(() => parseWorldLoadView({ ...view(), loadOutcome: 'Passed' })).toThrow()
    expect(() => parseWorldLoadView({ ...view(), copyIdentity: 'short' })).toThrow()
    expect(() => parseWorldLoadView({ ...view(), managedStarts: -1 })).toThrow()
  })
  it('offers a fixed received-copy preparation and safe manual steps', async () => {
    const fetch = vi.fn().mockImplementation((path: string) => Promise.resolve(new Response(JSON.stringify(
      path.endsWith('/prepare') ? { ok: true, code: 'WorldLoadPrepared', message: 'Disposable copy prepared.', rehearsal: view() } : { ok: true, message: '', rehearsals: [] }))))
    vi.stubGlobal('fetch', fetch)
    render(<WorldLoadRehearsalPanel profileId="test-profile" received />)
    fireEvent.click(screen.getByRole('button', { name: 'Prepare disposable copy' }))
    await waitFor(() => expect(screen.getByText(/Manual path:/)).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: 'Start disposable copy' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'The change survived restart' })).toBeDisabled()
    expect(fetch).toHaveBeenCalledWith('/api/local/friend/test-profile/world-load/prepare', expect.objectContaining({ method: 'POST', body: undefined }))
  })
  it('requires a completed backup and explicit stopped cleanup', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: true, message: '', rehearsals: [] }))))
    render(<WorldLoadRehearsalPanel profileId="test-profile" />)
    expect(screen.getByRole('button', { name: 'Prepare disposable copy' })).toBeDisabled()
    expect(screen.getByText('Show completed backups first')).toBeInTheDocument()
  })
})

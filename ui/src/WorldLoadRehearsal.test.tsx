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
    expect(screen.queryByRole('button', { name: 'Stop disposable copy' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'The change survived restart' })).toBeDisabled()
    expect(fetch).toHaveBeenCalledWith('/api/local/friend/test-profile/world-load/prepare', expect.objectContaining({ method: 'POST', body: undefined }))
  })
  it('keeps the fixed Stop available for a managed rehearsal when a changed binary blocks Start', async () => {
    const changed = { ...view(), game: 'Valheim', sourceKind: 'Backup', state: 'Game binary changed',
      rehearsalProfileId: '568fa42b-0874-46b5-8e0d-5116063499a8', managedStarts: 1,
      launchReason: 'Reviewed isolated driver trial.' }
    const fetch = vi.fn().mockImplementation((path: string) => Promise.resolve(new Response(JSON.stringify(
      path.endsWith('/stop') ? { ok: true, code: 'WorldLoadUpdated', message: 'Exact disposable process stopped.',
        rehearsal: { ...changed, gracefulStops: 1 } } : { ok: true, message: '', rehearsals: [changed] }))))
    vi.stubGlobal('fetch', fetch)
    render(<WorldLoadRehearsalPanel profileId="test-profile" />)
    fireEvent.click(screen.getByRole('button', { name: 'Refresh results' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Stop disposable copy' })).toBeEnabled())
    expect(screen.queryByRole('button', { name: 'Start disposable copy' })).not.toBeInTheDocument()
    expect(screen.queryByText(/Manual path:/)).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Stop disposable copy' }))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Exact disposable process stopped.'))
    expect(fetch).toHaveBeenCalledWith(`/api/local/world-load/${changed.id}/stop`, expect.objectContaining({
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' }, body: undefined
    }))
  })
  it('requires a completed backup and explicit stopped cleanup', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: true, message: '', rehearsals: [] }))))
    render(<WorldLoadRehearsalPanel profileId="test-profile" />)
    expect(screen.getByRole('button', { name: 'Prepare disposable copy' })).toBeDisabled()
    expect(screen.getByText('Show completed backups first')).toBeInTheDocument()
  })
})
